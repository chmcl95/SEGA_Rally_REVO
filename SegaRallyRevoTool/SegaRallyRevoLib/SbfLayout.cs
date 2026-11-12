using System;
using System.Collections.Generic;
using System.IO;

namespace SegaRallyRevoTool.SegaRallyRevoLib
{
    /// <summary>入力が SBF/VBF として解釈できないときに投げる。メッセージはそのまま利用者に表示する。</summary>
    public class SbfFormatException : Exception
    {
        public SbfFormatException(string message) : base(message) { }
    }

    /// <summary>
    /// Document/SBF_format.md (2026-11 再解析) のブロック構造・配置規則の実装。
    ///
    /// ブロック (§4):
    ///   +0x00 type, +0x04 nameHash, +0x08 size, +0x0C sub, +0x10 nReloc,
    ///   +0x14 reloc[nReloc], nImport, import[nImport], data[size], (type 5 のみ) トレーラ
    /// 配置 (§8):
    ///   データ先頭 (= ブロック先頭 + 0x18 + 4 × (nReloc + nImport)) を type ごとの境界に揃える。
    ///   type 4 はデータ先頭 + 0xAC (DDS ペイロード) を 0x1000 境界に揃える。
    ///   v4 の EOF = roundUp(最終ブロック終端, 4) + 0x800 (ゼロ埋め)、v3 は roundUp(終端, 0x800)。
    /// </summary>
    public static class SbfLayout
    {
        public const int GlobalHeaderSize = 0x18;
        public const int EntrySize = 8;
        public const int FirstBlockAlign = 0x1000;
        public const int TextureDescriptorSize = 0x2C;
        /// <summary>type 4 のデータ先頭から DDS ペイロードまで (記述子 0x2C + DDS ヘッダー 0x80)。</summary>
        public const int TexturePayloadBias = TextureDescriptorSize + Ps3DdsUtil.DdsHeaderSize;
        public const int TexturePayloadAlign = 0x1000;
        public const int V4EofPadding = 0x800;
        public const int V3EofAlign = 0x800;

        private static readonly HashSet<uint> KnownTypes = new HashSet<uint> { 1, 2, 3, 4, 5, 6, 7, 11, 12, 13 };

        public static long RoundUp(long value, long align)
        {
            return (value + align - 1) / align * align;
        }

        public static uint ReadU32(byte[] buffer, long offset, bool bigEndian)
        {
            if (offset < 0 || offset + 4 > buffer.Length)
            {
                throw new SbfFormatException($"read of u32 at 0x{offset:X} is outside the buffer (length 0x{buffer.Length:X})");
            }
            int o = (int)offset;
            return bigEndian
                ? (uint)(buffer[o] << 24 | buffer[o + 1] << 16 | buffer[o + 2] << 8 | buffer[o + 3])
                : (uint)(buffer[o] | buffer[o + 1] << 8 | buffer[o + 2] << 16 | buffer[o + 3] << 24);
        }

        public static void WriteU32(byte[] buffer, long offset, uint value, bool bigEndian)
        {
            byte[] b = BitConverter.GetBytes(value);
            if (bigEndian)
            {
                Array.Reverse(b);
            }
            Array.Copy(b, 0, buffer, offset, 4);
        }

        /// <summary>
        /// 展開済み SBF/VBF のエンディアンを判定する (tools/sbf_inspect.py の detect_endian と同じ規則)。
        /// +0x00 (バージョン) が 3 か 4 になるバイト順を候補とし、エントリーテーブルと
        /// 各ブロック先頭の type が妥当であることを確かめる。判定できなければ SbfFormatException。
        /// </summary>
        public static bool DetectBigEndian(byte[] data)
        {
            if (data.Length < GlobalHeaderSize)
            {
                throw new SbfFormatException(
                    $"file is only {data.Length} bytes; an SBF/VBF archive needs at least a 0x18-byte header");
            }

            List<string> problems = new List<string>();
            List<bool> plausible = new List<bool>();
            foreach (bool be in new[] { false, true })
            {
                string name = be ? "big-endian (PS3)" : "little-endian (PC)";
                uint version = ReadU32(data, 0x00, be);
                uint count = ReadU32(data, 0x14, be);
                if (version != 3 && version != 4)
                {
                    problems.Add($"{name}: version field is {version}, expected 3 or 4");
                    continue;
                }
                if (GlobalHeaderSize + (long)count * EntrySize > data.Length)
                {
                    problems.Add($"{name}: entry table for {count} blocks does not fit in the file");
                    continue;
                }

                string entryProblem = null;
                for (uint i = 0; i < count && entryProblem == null; i++)
                {
                    uint offset = ReadU32(data, GlobalHeaderSize + i * EntrySize + 4, be);
                    if ((long)offset + 0x18 > data.Length)
                    {
                        entryProblem = $"entry {i} points outside the file (0x{offset:X})";
                    }
                    else if (!KnownTypes.Contains(ReadU32(data, offset, be)))
                    {
                        entryProblem = $"entry {i} has unknown block type {ReadU32(data, offset, be)} at 0x{offset:X}";
                    }
                }
                if (entryProblem != null)
                {
                    problems.Add($"{name}: {entryProblem}");
                    continue;
                }
                plausible.Add(be);
            }

            if (plausible.Count == 1)
            {
                return plausible[0];
            }
            if (plausible.Count == 0)
            {
                throw new SbfFormatException("not a valid SBF/VBF archive (cannot determine byte order): " +
                                             string.Join("; ", problems));
            }
            throw new SbfFormatException("cannot determine byte order: the header is valid in both little- and big-endian");
        }

        /// <summary>ブロックの構造情報 (block 先頭からの相対値)。</summary>
        public class BlockInfo
        {
            public uint Type;
            public uint Size;
            public uint RelocCount;
            public uint ImportCount;
            /// <summary>データ本体の開始位置 (= 0x18 + 4 × (nReloc + nImport))。</summary>
            public int DataOffset;
            /// <summary>ブロックの実長 (データ本体 + type 5 のトレーラまで。後続パディングは含まない)。</summary>
            public int Length;
        }

        /// <summary>
        /// block (ブロック先頭から始まるバイト列。後ろにパディングや別のデータが続いてもよい) の
        /// 構造を解析し、ブロックの実長を求める。構造が block に収まらなければ SbfFormatException。
        /// </summary>
        public static BlockInfo ParseBlock(byte[] block, bool bigEndian)
        {
            BlockInfo info = new BlockInfo();
            if (block.Length < 0x18)
            {
                throw new SbfFormatException($"block is only {block.Length} bytes (minimum 0x18)");
            }
            info.Type = ReadU32(block, 0x00, bigEndian);
            info.Size = ReadU32(block, 0x08, bigEndian);
            info.RelocCount = ReadU32(block, 0x10, bigEndian);
            long importCountPos = 0x14 + 4L * info.RelocCount;
            if (importCountPos + 4 > block.Length)
            {
                throw new SbfFormatException($"reloc table ({info.RelocCount} entries) runs past the block data");
            }
            info.ImportCount = ReadU32(block, importCountPos, bigEndian);
            long dataOffset = importCountPos + 4 + 4L * info.ImportCount;
            long end = dataOffset + info.Size;
            if (end > block.Length)
            {
                throw new SbfFormatException(
                    $"declared size 0x{info.Size:X} (data at +0x{dataOffset:X}) runs past the available 0x{block.Length:X} bytes");
            }

            if (info.Type == 5)
            {
                // トレーラ: u32 0, u32 nSymbol, {u32 nameOffset, u32 valueOffset}[nSymbol], names (NUL 終端)
                long p = end;
                if (p + 8 > block.Length)
                {
                    throw new SbfFormatException("type 5 trailer runs past the block data");
                }
                uint symbolCount = ReadU32(block, p + 4, bigEndian);
                long tableEnd = p + 8 + 8L * symbolCount;
                if (tableEnd > block.Length)
                {
                    throw new SbfFormatException($"type 5 symbol table ({symbolCount} entries) runs past the block data");
                }
                end = tableEnd;
                for (uint k = 0; k < symbolCount; k++)
                {
                    uint nameOffset = ReadU32(block, p + 8 + 8L * k, bigEndian);
                    long q = dataOffset + nameOffset;
                    if (q >= block.Length)
                    {
                        throw new SbfFormatException($"type 5 symbol {k} name is outside the block data");
                    }
                    int z = Array.IndexOf(block, (byte)0, (int)q);
                    if (z < 0)
                    {
                        throw new SbfFormatException($"type 5 symbol {k} name is not NUL-terminated");
                    }
                    end = Math.Max(end, z + 1);
                }
            }

            info.DataOffset = (int)dataOffset;
            info.Length = (int)end;
            return info;
        }

        /// <summary>
        /// §8 の規則で、前ブロック終端 prevEnd 以降に置けるブロック先頭の最小位置を返す。
        /// </summary>
        public static long CanonicalBlockStart(long prevEnd, uint type, int dataOffset)
        {
            long bias;
            long align;
            if (type == 4)
            {
                bias = dataOffset + TexturePayloadBias;
                align = TexturePayloadAlign;
            }
            else
            {
                bias = dataOffset;
                align = type == 5 || type == 11 ? 16 : type == 7 ? 8 : 4;
            }
            return RoundUp(prevEnd + bias, align) - bias;
        }

        /// <summary>§8-3: 最終ブロック終端 lastEnd から EOF を求める。</summary>
        public static long CanonicalEof(long lastEnd, uint version)
        {
            return version >= 4 ? RoundUp(lastEnd, 4) + V4EofPadding : RoundUp(lastEnd, V3EofAlign);
        }
    }

    /// <summary>
    /// アンパック先ディレクトリ (_meta) に保存するエンディアン情報。
    /// .HEAD / .EXTRA / _header.bin は常に LE 化して保存されるため、元のエンディアンは
    /// このファイルが無いと分からない。
    /// </summary>
    public static class EndianMarker
    {
        public const string FileName = "_endian.txt";
        private const string BigToken = "BE";
        private const string LittleToken = "LE";

        public static void Write(string metaDir, bool bigEndian)
        {
            File.WriteAllText(Path.Combine(metaDir, FileName), (bigEndian ? BigToken : LittleToken) + Environment.NewLine);
        }

        /// <returns>true = BE, false = LE, null = マーカーなし (旧バージョンのツールでアンパックしたもの)</returns>
        public static bool? Read(string metaDir)
        {
            string path = Path.Combine(metaDir, FileName);
            if (!File.Exists(path))
            {
                return null;
            }
            string token = File.ReadAllText(path).Trim().ToUpperInvariant();
            if (token == BigToken)
            {
                return true;
            }
            if (token == LittleToken)
            {
                return false;
            }
            throw new SbfFormatException($"'{path}' must contain {BigToken} or {LittleToken}, found '{token}'");
        }
    }
}

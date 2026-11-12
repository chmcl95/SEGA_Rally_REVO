using SegaRallyRevoTool.SegaRallyRevoLib;
using System;
using System.Collections.Generic;
using System.IO;

namespace SegaRallyRevoTool
{
    class Packer
    {
        private string _inputPath;
        private string _destPath;
        private bool _disableCompress;
        private bool _isBigEndian;

        public Packer(string inputPath, string outputPath, bool disableCompress, bool isBigEndian)
        {
            _inputPath = inputPath;
            _destPath = outputPath;
            _disableCompress = disableCompress;
            _isBigEndian = isBigEndian;
        }

        /// <summary>
        /// アンパック済みディレクトリのエンディアンを決める。
        /// unpack が書いた _meta\_endian.txt を優先し、無ければ (旧バージョンのツールで
        /// アンパックしたディレクトリ) _predata.bin の有無 (= VBF なので BE) と --ps3 で決める。
        /// </summary>
        public static bool ResolveBigEndian(string inputPath, bool ps3Requested, out bool guessed)
        {
            string metaDir = Path.Combine(inputPath, "_meta");
            bool? marker = EndianMarker.Read(metaDir);
            guessed = !marker.HasValue;
            if (marker.HasValue)
            {
                if (ps3Requested && !marker.Value)
                {
                    Console.WriteLine("Warning: --ps3 was given but this directory was unpacked from a " +
                                      "little-endian (PC) archive. Ignoring --ps3.");
                }
                return marker.Value;
            }
            if (File.Exists(Path.Combine(metaDir, "_predata.bin")))
            {
                return true;
            }
            if (!ps3Requested)
            {
                Console.WriteLine($"Note: '{Path.Combine(metaDir, EndianMarker.FileName)}' not found " +
                                  "(unpacked by an older version). Assuming little-endian (PC); pass --ps3 for a PS3 archive.");
            }
            return ps3Requested;
        }

        /// <exception cref="SbfFormatException">アンパック済みデータが矛盾していてパックできない場合</exception>
        public void Pack()
        {
            Console.WriteLine("Starting to Pack...");

            List<string> fileRelativePaths = new List<string>();
            // Order of Container
            using (StreamReader orderTextReader = File.OpenText($@"{_inputPath}\_meta\_order.txt"))
            {
                while (orderTextReader.Peek() >= 0)
                {
                    string line = orderTextReader.ReadLine();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        fileRelativePaths.Add(line);
                    }
                }
            }

            // 全 Container 数は .HEAD ファイル数から求める
            // (_order.txt にはデータファイルを持つ Container しか載らないため)
            int containerCount = Directory.GetFiles($@"{_inputPath}\_meta", "*.HEAD").Length;
            for (int i = 0; i < containerCount; i++)
            {
                if (!File.Exists($@"{_inputPath}\_meta\{i:D8}.HEAD"))
                {
                    throw new SbfFormatException($"_meta has {containerCount} .HEAD file(s) but '{i:D8}.HEAD' is missing; " +
                                                 "block numbers must be contiguous from 00000000.");
                }
            }

            // Container 番号 → データファイル相対パス
            Dictionary<int, string> dataFileByIndex = new Dictionary<int, string>();
            foreach (string relativePath in fileRelativePaths)
            {
                dataFileByIndex[int.Parse(Path.GetFileNameWithoutExtension(relativePath))] = relativePath;
            }

            // _header.bin は常に LE 化して保存されている
            byte[] headerBin = File.ReadAllBytes($@"{_inputPath}\_meta\_header.bin");
            if (headerBin.Length < SbfLayout.GlobalHeaderSize)
            {
                throw new SbfFormatException($"_meta\\_header.bin is {headerBin.Length} bytes; it must hold at least the 0x18-byte global header.");
            }

            // VBF (PS3 のテクスチャ専用アーカイブ) は +0x08 (メインメモリ部の長さ) が 0 以外 (§3)
            bool isVbf = BitConverter.ToUInt32(headerBin, 0x08) != 0;

            // PS3 (VBF) の type4 分割抽出モードかどうか
            // (Unpacker が _meta\_predata.bin を出力しているかで判定する)
            string preDataPath = $@"{_inputPath}\_meta\_predata.bin";
            bool ps3SplitMode = _isBigEndian && File.Exists(preDataPath);
            if (isVbf && containerCount > 0 && !ps3SplitMode)
            {
                throw new SbfFormatException(_isBigEndian
                    ? "_header.bin describes a VBF archive but _meta\\_predata.bin is missing."
                    : "_header.bin describes a VBF (PS3) archive but the directory is marked little-endian.");
            }

            byte[] archive = ps3SplitMode
                ? BuildVbf(headerBin, containerCount, dataFileByIndex, preDataPath)
                : BuildSbf(headerBin, containerCount, dataFileByIndex);

            Directory.CreateDirectory(_destPath);
            string extension = isVbf ? "vbf" : "sbf";
            string outputFile = $@"{_destPath}\{Path.GetFileName(_inputPath)}.{extension}";
            if (_disableCompress)
            {
                File.WriteAllBytes(outputFile, archive);
                Console.WriteLine("Uncompress SBF Generate Done.");
                return;
            }

            using (MemoryStream sbfFileStream = new MemoryStream(archive, false))
            using (FileStream sbz1Stream = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
            {
                sbz1Stream.Seek(0x8, SeekOrigin.Begin);
                SBZ1 sbz1 = new SBZ1(_isBigEndian);
                sbz1.Compress(sbfFileStream, sbz1Stream);
                sbz1Stream.Seek(0x0, SeekOrigin.Begin);
                sbz1.Pack(sbz1Stream);
            }

            Console.WriteLine("Done.");
        }

        // =====================================================================
        // SBF (PC の全アーカイブ、PS3 の .sbf)
        // =====================================================================

        /// <summary>パック中のブロック 1 個ぶんの情報。</summary>
        private class PackBlock
        {
            public int Index;
            /// <summary>ブロック先頭からのバイト列 (アーカイブのエンディアン)。HEAD + EXTRA + データファイル。</summary>
            public byte[] Stored;
            /// <summary>Stored のうち .HEAD / .EXTRA から来た部分の長さ (以降がデータファイル)。</summary>
            public int MetaLength;
            public SbfLayout.BlockInfo Info;
            public string DataFile;
            public bool TextureUpdated;
        }

        /// <summary>
        /// SBF を組み立てる。配置は Document/SBF_format.md §8 に従う。
        ///
        /// - ブロック k の直前のブロックが元の位置にあり、かつ元の位置 (= _header.bin に残っている
        ///   元のエントリーオフセット) の手前に収まるなら、ブロック k も元の位置に置き、
        ///   その間は元のパディングバイトをそのまま使う。無改造ならこれで元ファイルとバイト一致する
        ///   (§8 の規則から外れた製品版の例外配置も保たれる)。
        /// - それ以外 (テクスチャのサイズ変更などで位置がずれた後) は §8 の規則で最小位置に置き、
        ///   パディングはゼロで埋める。EOF も v4 = roundUp(終端, 4) + 0x800 / v3 = roundUp(終端, 0x800)。
        /// - PC (LE) の type 4 は、4/*.DDS が差し替えられていれば size / 幅 / 高さを DDS から再計算する。
        /// </summary>
        private byte[] BuildSbf(byte[] headerBinLe, int count, Dictionary<int, string> dataFileByIndex)
        {
            bool be = _isBigEndian;
            string metaDir = Path.Combine(_inputPath, "_meta");
            uint version = BitConverter.ToUInt32(headerBinLe, 0x00);
            uint headerCount = BitConverter.ToUInt32(headerBinLe, 0x14);
            if (headerCount != count)
            {
                throw new SbfFormatException($"_meta has {count} .HEAD file(s) but _header.bin declares {headerCount} block(s). " +
                                             "Adding or removing blocks is not supported.");
            }
            long tableEnd = SbfLayout.GlobalHeaderSize + (long)SbfLayout.EntrySize * count;
            if (headerBinLe.Length < tableEnd)
            {
                throw new SbfFormatException($"_meta\\_header.bin ({headerBinLe.Length} bytes) is shorter than the entry table (0x{tableEnd:X} bytes).");
            }

            byte[] header = (byte[])headerBinLe.Clone();
            if (be)
            {
                Ps3DdsUtil.DwordReverseInPlace(header, 0, header.Length & ~3);
            }
            if (count == 0)
            {
                Console.WriteLine("Empty archive (0 blocks).");
                return header;
            }

            // 元のエントリーオフセット (無改造ブロックを元の位置に戻すのに使う)
            long[] originalOffsets = new long[count];
            for (int k = 0; k < count; k++)
            {
                originalOffsets[k] = BitConverter.ToUInt32(headerBinLe, SbfLayout.GlobalHeaderSize + SbfLayout.EntrySize * k + 4);
            }

            PackBlock[] blocks = new PackBlock[count];
            bool anyTextureUpdated = false;
            for (int k = 0; k < count; k++)
            {
                blocks[k] = LoadBlock(metaDir, k, dataFileByIndex, be);
                anyTextureUpdated |= blocks[k].TextureUpdated;
            }

            long[] newOffsets = new long[count];
            bool relaid = false;
            using (MemoryStream output = new MemoryStream())
            {
                output.Write(header, 0, header.Length);

                for (int k = 0; k < count; k++)
                {
                    PackBlock block = blocks[k];
                    long start;
                    if (k == 0)
                    {
                        // 最初のブロックは _header.bin の直後 (= 元の位置。§8-1 を満たしている)
                        start = header.Length;
                    }
                    else
                    {
                        PackBlock prev = blocks[k - 1];
                        long prevEnd = newOffsets[k - 1] + prev.Info.Length;
                        long originalGapEnd = originalOffsets[k] - originalOffsets[k - 1];
                        bool keepOriginal = newOffsets[k - 1] == originalOffsets[k - 1]
                                            && prevEnd <= originalOffsets[k]
                                            && prev.Stored.Length >= originalGapEnd;
                        if (keepOriginal)
                        {
                            start = originalOffsets[k];
                            output.Write(prev.Stored, prev.Info.Length, (int)(originalGapEnd - prev.Info.Length));
                            WarnIfDroppingData(prev, originalGapEnd, version);
                        }
                        else
                        {
                            start = SbfLayout.CanonicalBlockStart(prevEnd, block.Info.Type, block.Info.DataOffset);
                            output.Write(new byte[start - prevEnd], 0, (int)(start - prevEnd));
                            WarnIfDroppingData(prev, prev.Info.Length, version);
                        }
                    }

                    if (output.Position != start)
                    {
                        throw new InvalidOperationException($"internal layout error at block {k} (0x{output.Position:X} != 0x{start:X})");
                    }
                    if (start != originalOffsets[k])
                    {
                        relaid = true;
                    }
                    newOffsets[k] = start;
                    output.Write(block.Stored, 0, block.Info.Length);
                }

                // ファイル末尾 (§8-3)
                PackBlock last = blocks[count - 1];
                long lastEnd = newOffsets[count - 1] + last.Info.Length;
                long tailLength = last.Stored.Length - last.Info.Length;
                long eof = SbfLayout.CanonicalEof(lastEnd, version);
                bool lastInPlace = newOffsets[count - 1] == originalOffsets[count - 1];
                if (lastInPlace && ((!relaid && !anyTextureUpdated) || lastEnd + tailLength == eof))
                {
                    output.Write(last.Stored, last.Info.Length, (int)tailLength);
                }
                else
                {
                    WarnIfDroppingData(last, last.Info.Length, version);
                    output.Write(new byte[eof - lastEnd], 0, (int)(eof - lastEnd));
                }

                if (relaid || anyTextureUpdated)
                {
                    int moved = 0;
                    for (int k = 0; k < count; k++)
                    {
                        if (newOffsets[k] != originalOffsets[k])
                        {
                            moved++;
                        }
                    }
                    Console.WriteLine($"Layout recomputed: {moved} of {count} block(s) moved, EOF at 0x{output.Length:X}.");
                }

                byte[] result = output.ToArray();
                SbfLayout.WriteU32(result, 0x14, (uint)count, be);
                for (int k = 0; k < count; k++)
                {
                    uint hash = SbfLayout.ReadU32(blocks[k].Stored, 0x04, be);
                    SbfLayout.WriteU32(result, SbfLayout.GlobalHeaderSize + SbfLayout.EntrySize * k, hash, be);
                    SbfLayout.WriteU32(result, SbfLayout.GlobalHeaderSize + SbfLayout.EntrySize * k + 4, (uint)newOffsets[k], be);
                }
                return result;
            }
        }

        /// <summary>.HEAD (+ .EXTRA) + データファイルを連結してブロックのバイト列を復元し、構造を解析する。</summary>
        private PackBlock LoadBlock(string metaDir, int k, Dictionary<int, string> dataFileByIndex, bool be)
        {
            byte[] head = File.ReadAllBytes(Path.Combine(metaDir, $"{k:D8}.HEAD"));
            string extraPath = Path.Combine(metaDir, $"{k:D8}.EXTRA");
            byte[] extra = File.Exists(extraPath) ? File.ReadAllBytes(extraPath) : new byte[0];
            if (be)
            {
                // .HEAD / .EXTRA は LE 化されて保存されている
                Ps3DdsUtil.DwordReverseInPlace(head, 0, head.Length & ~3);
                Ps3DdsUtil.DwordReverseInPlace(extra, 0, extra.Length & ~3);
            }

            byte[] data = new byte[0];
            string dataFile = null;
            if (dataFileByIndex.TryGetValue(k, out string relativePath))
            {
                dataFile = relativePath;
                data = File.ReadAllBytes(Path.Combine(_inputPath, relativePath));
            }

            byte[] stored = new byte[head.Length + extra.Length + data.Length];
            Array.Copy(head, 0, stored, 0, head.Length);
            Array.Copy(extra, 0, stored, head.Length, extra.Length);
            Array.Copy(data, 0, stored, head.Length + extra.Length, data.Length);

            PackBlock block = new PackBlock
            {
                Index = k,
                Stored = stored,
                MetaLength = head.Length + extra.Length,
                DataFile = dataFile,
            };
            try
            {
                // PC のテクスチャは、.HEAD の size が差し替え前の値のままでも解析できるよう、
                // 先に DDS から size / 寸法を更新する
                if (!be && stored.Length >= 0x18 && SbfLayout.ReadU32(stored, 0x00, false) == 4)
                {
                    UpdateTextureFromDds(block);
                }
                block.Info = SbfLayout.ParseBlock(block.Stored, be);
            }
            catch (SbfFormatException ex)
            {
                throw new SbfFormatException($"block {k:D8} ({Describe(block)}): {ex.Message}");
            }
            return block;
        }

        /// <summary>
        /// PC (LE) の type 4: データファイル (4/{k}.DDS) の DDS ヘッダーから
        /// size (= ミップ全段 [× キューブマップ面数] の合計 + 0xAC) と記述子の幅 / 高さを求め、
        /// 元の値と違えば (= DDS が差し替えられていれば) 書き換える。
        /// 差し替え時はミップチェーンより後ろの余分なバイトを捨てる。
        /// </summary>
        private void UpdateTextureFromDds(PackBlock block)
        {
            byte[] s = block.Stored;
            if (SbfLayout.ReadU32(s, 0x10, false) != 0 || SbfLayout.ReadU32(s, 0x14, false) != 0)
            {
                return;   // 製品版の type 4 は常に nReloc = nImport = 0。想定外の形はそのまま扱う
            }
            const int desc = 0x18;
            int ddsPos = desc + SbfLayout.TextureDescriptorSize;

            DdsHeader dds = DdsHeader.Parse(s, ddsPos);
            if (dds == null)
            {
                throw new SbfFormatException("texture data does not start with a valid DDS header.");
            }
            if (dds.HasDx10Header)
            {
                throw new SbfFormatException("DDS files with a DX10 extended header are not supported; " +
                                             "save the texture as a legacy DDS (e.g. DXT1 / DXT5 / A8R8G8B8).");
            }

            uint oldSize = SbfLayout.ReadU32(s, 0x08, false);
            uint oldWidth = SbfLayout.ReadU32(s, desc + 0x08, false);
            uint oldHeight = SbfLayout.ReadU32(s, desc + 0x0C, false);
            bool sameDims = dds.Width == oldWidth && dds.Height == oldHeight;
            if (!dds.IsSizeComputable)
            {
                if (sameDims)
                {
                    return;   // 形式が不明でも寸法が同じなら元の size を信用する
                }
                throw new SbfFormatException($"cannot compute the pixel data size of DDS format {dds.FormatName}, " +
                                             "so the texture cannot be resized.");
            }

            long payload = dds.ComputePayloadSize();
            long available = s.Length - ddsPos - Ps3DdsUtil.DdsHeaderSize;
            if (payload > available)
            {
                throw new SbfFormatException($"DDS is truncated: its header declares 0x{payload:X} bytes of pixel data " +
                                             $"but only 0x{Math.Max(0, available):X} are present.");
            }
            long newSize = payload + Ps3DdsUtil.SizeFieldOverhead;
            if (newSize == oldSize && sameDims)
            {
                return;   // 差し替えなし (または同じ寸法・形式への差し替え)。後続のバイトは元のパディング
            }

            byte[] content = new byte[ddsPos + Ps3DdsUtil.DdsHeaderSize + payload];
            Array.Copy(s, content, content.Length);
            SbfLayout.WriteU32(content, 0x08, (uint)newSize, false);
            SbfLayout.WriteU32(content, desc + 0x08, (uint)dds.Width, false);
            SbfLayout.WriteU32(content, desc + 0x0C, (uint)dds.Height, false);
            // 記述子 +0x00: 1 = 2D, 2 = キューブマップ (§6.4)
            uint oldKind = SbfLayout.ReadU32(s, desc, false);
            uint newKind = dds.IsCubemap ? 2u : 1u;
            if ((oldKind == 1 || oldKind == 2) && oldKind != newKind)
            {
                SbfLayout.WriteU32(content, desc, newKind, false);
            }

            Console.WriteLine($"  [{block.Index:D8}] texture replaced: {oldWidth}x{oldHeight} -> {dds.Width}x{dds.Height} " +
                              $"{dds.FormatName}, size 0x{oldSize:X} -> 0x{newSize:X}");
            if (available > payload)
            {
                Console.WriteLine($"  [{block.Index:D8}] Note: ignoring 0x{available - payload:X} trailing byte(s) " +
                                  "beyond the mip chain declared by the DDS header.");
            }

            block.Stored = content;
            block.TextureUpdated = true;
        }

        /// <summary>
        /// ブロックのデータファイル末尾のうち keepUpTo 以降 (出力に書かれない部分) に
        /// 0 以外のバイトがあれば警告する。v4 のパディングは常にゼロなので、
        /// 0 以外が捨てられるのは「データを編集したのに .HEAD の size を更新していない」場合が多い。
        /// v3 のパディングにはゴミが入る (§8-4) ので警告しない。
        /// </summary>
        private static void WarnIfDroppingData(PackBlock block, long keepUpTo, uint version)
        {
            if (version < 4)
            {
                return;
            }
            long from = Math.Max(keepUpTo, block.MetaLength);
            for (long i = from; i < block.Stored.Length; i++)
            {
                if (block.Stored[i] != 0)
                {
                    Console.WriteLine($"  [{block.Index:D8}] Warning: dropped 0x{block.Stored.Length - from:X} byte(s) " +
                                      $"(not all zero) after the end of the block in '{block.DataFile}'. If you edited " +
                                      $"this block, update its size in _meta\\{block.Index:D8}.HEAD.");
                    return;
                }
            }
        }

        private static string Describe(PackBlock block)
        {
            return block.DataFile ?? $"_meta\\{block.Index:D8}.HEAD";
        }

        // =====================================================================
        // VBF (PS3 のテクスチャ専用アーカイブ)。ヘッダー群を先頭に連続させ、DDS を後ろに並べる。
        // =====================================================================

        private byte[] BuildVbf(byte[] headerBinLe, int containerCount, Dictionary<int, string> dataFileByIndex, string preDataPath)
        {
            byte[] bytes;

            // Container 番号 → .EXTRA の有無 (type==4 かどうか)
            List<bool> hasExtraByIndex = new List<bool>(containerCount);
            for (int i = 0; i < containerCount; i++)
            {
                hasExtraByIndex.Add(File.Exists($@"{_inputPath}\_meta\{i:D8}.EXTRA"));
            }

            // type4 かつデータファイルを持つ Container 番号 (昇順 = ファイル内でのデータ並び順)
            List<int> ps3Type4DataIndices = new List<int>();
            // Container 番号 → 再計算した ExtraHeader+0x00 (DDS絶対オフセット)
            Dictionary<int, uint> ps3ComputedDataOffset = new Dictionary<int, uint>();

            for (int i = 0; i < containerCount; i++)
            {
                if (hasExtraByIndex[i] && dataFileByIndex.ContainsKey(i))
                {
                    ps3Type4DataIndices.Add(i);
                }
            }

            long headerBinLength = headerBinLe.Length;
            long containerHeadersTotalSize = 0;
            for (int i = 0; i < containerCount; i++)
            {
                containerHeadersTotalSize += 0x1C + (hasExtraByIndex[i] ? 0x28 : 0);
            }
            long preDataSize = new FileInfo(preDataPath).Length;

            // ヘッダー連続部 + _predata (未知領域) の直後から type4 データが並ぶ
            long runningOffset = headerBinLength + containerHeadersTotalSize + preDataSize;
            foreach (int idx in ps3Type4DataIndices)
            {
                long blobLength = new FileInfo($@"{_inputPath}\{dataFileByIndex[idx]}").Length;
                ps3ComputedDataOffset[idx] = (uint)runningOffset;
                runningOffset += blobLength;
            }

            using (MemoryStream sbfFileStream = new MemoryStream())
            {
                // SBF Header (_header.bin はアンパック時に LE 化されている)
                bytes = (byte[])headerBinLe.Clone();
                if (_isBigEndian)
                {
                    Ps3DdsUtil.DwordReverseInPlace(bytes, 0, bytes.Length & ~3);
                }
                sbfFileStream.Write(bytes);

                List<Entry> entrys = new List<Entry>();
                for (int i = 0; i < containerCount; i++)
                {
                    string headPath  = $@"{_inputPath}\_meta\{i:D8}.HEAD";
                    string extraPath = $@"{_inputPath}\_meta\{i:D8}.EXTRA";

                    using (FileStream headFileStream = new FileStream(headPath, FileMode.Open, FileAccess.Read))
                    {
                        // Entry のオフセットと unk0x00(=unk0x04 in Container) を記録
                        Entry entry = new Entry(_isBigEndian);
                        entry.offset = (UInt32)sbfFileStream.Position;

                        // unk0x00 は HEAD の +0x04 に格納されている
                        headFileStream.Seek(0x04, SeekOrigin.Begin);
                        bytes = new byte[4];
                        headFileStream.Read(bytes, 0x00, bytes.Length);
                        entry.unk0x00 = BitConverter.ToUInt32(bytes);
                        entrys.Add(entry);

                        headFileStream.Seek(0x00, SeekOrigin.Begin);

                        // .EXTRA が存在すれば type==4 → ExtraHeader も一緒に書く
                        if (hasExtraByIndex[i])
                        {
                            byte[] extraBytes = File.ReadAllBytes(extraPath);

                            // ExtraHeader+0x00 (DDS絶対オフセット) を実際のレイアウトに合わせて
                            // 逆算した値で上書きする (.EXTRA は常に LE 保存)
                            if (ps3ComputedDataOffset.TryGetValue(i, out uint newDataOffset))
                            {
                                byte[] ptrBytes = BitConverter.GetBytes(newDataOffset);
                                Array.Copy(ptrBytes, 0, extraBytes, 0, 4);
                            }

                            using (MemoryStream extraMemStream = new MemoryStream(extraBytes))
                            {
                                Container container = new Container(_isBigEndian);
                                container.Pack(sbfFileStream, headFileStream, extraMemStream);
                            }
                        }
                        else
                        {
                            // type==4 以外: HEAD(0x1C) のみ書く
                            using (MemoryStream emptyExtra = new MemoryStream())
                            {
                                Container container = new Container(_isBigEndian);
                                container.Pack(sbfFileStream, headFileStream, emptyExtra);
                            }
                        }
                    }
                }

                // ヘッダー連続部の直後に _predata (未知領域) → type4 データを並び順どおりに書き出す
                bytes = File.ReadAllBytes(preDataPath);
                sbfFileStream.Write(bytes);

                foreach (int idx in ps3Type4DataIndices)
                {
                    bytes = File.ReadAllBytes($@"{_inputPath}\{dataFileByIndex[idx]}");
                    sbfFileStream.Write(bytes);
                }

                // Entry テーブルを書き込む
                sbfFileStream.Seek(0x14, SeekOrigin.Begin);
                bytes = BitConverter.GetBytes((UInt32)containerCount);
                if (_isBigEndian)
                {
                    Array.Reverse(bytes);
                }
                sbfFileStream.Write(bytes);
                foreach (Entry entry in entrys)
                {
                    entry.Pack(sbfFileStream);
                }

                return sbfFileStream.ToArray();
            }
        }
    }
}

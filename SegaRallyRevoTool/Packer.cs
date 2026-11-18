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

            bool buildVbf = isVbf && containerCount > 0;
            if (buildVbf && !_isBigEndian)
            {
                throw new SbfFormatException("_header.bin describes a VBF (PS3) archive but the directory is marked little-endian.");
            }

            byte[] archive = buildVbf
                ? BuildVbf(headerBin, containerCount, dataFileByIndex)
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
        // VBF (PS3 のテクスチャ専用アーカイブ)。配置は Document/SBF_format.md §6.4.2 に従う。
        // =====================================================================

        /// <summary>
        /// VBF を組み立てる。
        ///
        /// <code>
        /// 0x0000  グローバルヘッダー + エントリー (_header.bin。元のパディングを含む)
        /// B + 0x44k  テクスチャ k のブロックヘッダー + 記述子 (B = _header.bin の長さ = 0x1000)
        /// mainEnd = B + 0x44n (グローバル +0x08)
        /// H_0 = roundUp(mainEnd, 0x1000) + 0x1000 - 0x80
        /// H_k: DDS ヘッダー (dword 反転) 0x80 + ペイロード (H_k + 0x80 は 0x1000 境界) + ゼロ 0x44
        /// H_{k+1} = roundUp(H_k + 0x80 + payloadLen_k + 0x44 + 0x80, 0x1000) - 0x80
        /// EOF = 最終ペイロードの終端
        /// 記述子 +0x04 = H_k - 0x1018 - 0x44k
        /// </code>
        ///
        /// テクスチャは 4/{k}.DDS (標準 DDS) から読む。DDS が差し替えられていれば size / 幅 / 高さを
        /// DDS から再計算する (PC と同じ)。テクスチャ以外の部分はすべてゼロで埋める
        /// (製品版の VBF もすべてゼロなので、無改造なら元ファイルとバイト一致する)。
        /// 旧バージョンのアンパック結果 (_predata.bin + 4/*.BIN の生スライス、編集用の dds/) も読める。
        /// </summary>
        private byte[] BuildVbf(byte[] headerBinLe, int count, Dictionary<int, string> dataFileByIndex)
        {
            const bool be = true;
            string metaDir = Path.Combine(_inputPath, "_meta");
            uint headerCount = BitConverter.ToUInt32(headerBinLe, 0x14);
            if (headerCount != count)
            {
                throw new SbfFormatException($"_meta has {count} .HEAD file(s) but _header.bin declares {headerCount} block(s). " +
                                             "Adding or removing textures is not supported.");
            }
            long tableEnd = SbfLayout.GlobalHeaderSize + (long)SbfLayout.EntrySize * count;
            if (headerBinLe.Length < tableEnd)
            {
                throw new SbfFormatException($"_meta\\_header.bin ({headerBinLe.Length} bytes) is shorter than the entry table (0x{tableEnd:X} bytes).");
            }
            if (headerBinLe.Length % 4 != 0)
            {
                throw new SbfFormatException($"_meta\\_header.bin ({headerBinLe.Length} bytes) is not a multiple of 4 bytes.");
            }

            long blocksStart = headerBinLe.Length;
            if (blocksStart != SbfLayout.RoundUp(tableEnd, SbfLayout.FirstBlockAlign))
            {
                Console.WriteLine($"Note: the first texture header is at 0x{blocksStart:X} " +
                                  $"(retail VBFs use 0x{SbfLayout.RoundUp(tableEnd, SbfLayout.FirstBlockAlign):X}).");
            }
            long mainEnd = blocksStart + (long)SbfLayout.VbfBlockLength * count;

            // ---- ブロックヘッダー + 記述子 (0x44) を読む (.HEAD 0x1C + .EXTRA 0x28、LE 化して保存されている) ----
            byte[][] metas = new byte[count][];
            for (int k = 0; k < count; k++)
            {
                byte[] head = File.ReadAllBytes(Path.Combine(metaDir, $"{k:D8}.HEAD"));
                string extraPath = Path.Combine(metaDir, $"{k:D8}.EXTRA");
                byte[] extra = File.Exists(extraPath) ? File.ReadAllBytes(extraPath) : new byte[0];
                if (head.Length + extra.Length != SbfLayout.VbfBlockLength)
                {
                    throw new SbfFormatException($"block {k:D8}: .HEAD + .EXTRA is 0x{head.Length + extra.Length:X} bytes; " +
                                                 $"a VBF texture header must be 0x{SbfLayout.VbfBlockLength:X} bytes.");
                }
                byte[] m = new byte[SbfLayout.VbfBlockLength];
                Array.Copy(head, 0, m, 0, head.Length);
                Array.Copy(extra, 0, m, head.Length, extra.Length);

                uint type = BitConverter.ToUInt32(m, 0x00);
                uint size = BitConverter.ToUInt32(m, 0x08);
                uint nReloc = BitConverter.ToUInt32(m, 0x10);
                uint nImport = BitConverter.ToUInt32(m, 0x14);
                if (type != 4 || nReloc != 0 || nImport != 0)
                {
                    throw new SbfFormatException($"block {k:D8}: type {type}, nReloc {nReloc}, nImport {nImport}; " +
                                                 "a VBF may only contain textures (type 4 with no reloc / import).");
                }
                if (size < Ps3DdsUtil.SizeFieldOverhead)
                {
                    throw new SbfFormatException($"block {k:D8}: size 0x{size:X} is smaller than the descriptor + DDS header (0xAC).");
                }
                metas[k] = m;
            }

            // ---- 各テクスチャの標準 DDS を集め、差し替えられていれば size / 寸法を更新する ----
            byte[][] ddsFiles = LoadVbfDdsFiles(metas, dataFileByIndex, mainEnd, out string[] sources);
            byte[][] textures = new byte[count][];   // 標準 DDS ヘッダー 0x80 + ペイロード
            long[] payloadLens = new long[count];
            for (int k = 0; k < count; k++)
            {
                byte[] stored = new byte[SbfLayout.VbfBlockLength + ddsFiles[k].Length];
                Array.Copy(metas[k], stored, SbfLayout.VbfBlockLength);
                Array.Copy(ddsFiles[k], 0, stored, SbfLayout.VbfBlockLength, ddsFiles[k].Length);
                PackBlock block = new PackBlock { Index = k, Stored = stored, MetaLength = SbfLayout.VbfBlockLength, DataFile = sources[k] };
                try
                {
                    UpdateTextureFromDds(block);
                }
                catch (SbfFormatException ex)
                {
                    throw new SbfFormatException($"block {k:D8} ({sources[k]}): {ex.Message}");
                }

                Array.Copy(block.Stored, metas[k], SbfLayout.VbfBlockLength);
                payloadLens[k] = BitConverter.ToUInt32(metas[k], 0x08) - Ps3DdsUtil.SizeFieldOverhead;
                long textureLength = Ps3DdsUtil.DdsHeaderSize + payloadLens[k];
                long available = block.Stored.Length - SbfLayout.VbfBlockLength;
                if (available < textureLength)
                {
                    throw new SbfFormatException($"block {k:D8} ({sources[k]}): the DDS file is 0x{available:X} bytes but the texture " +
                                                 $"needs 0x{textureLength:X} (DDS header + size - 0xAC).");
                }
                if (available > textureLength)
                {
                    Console.WriteLine($"  [{k:D8}] Note: ignoring 0x{available - textureLength:X} trailing byte(s) after the texture data.");
                }
                textures[k] = new byte[textureLength];
                Array.Copy(block.Stored, SbfLayout.VbfBlockLength, textures[k], 0, textureLength);
            }

            // ---- 規則どおりに並べる ----
            long[] headers = new long[count];
            headers[0] = SbfLayout.VbfFirstHeaderPosition(mainEnd);
            for (int k = 1; k < count; k++)
            {
                headers[k] = SbfLayout.VbfNextHeaderPosition(headers[k - 1], payloadLens[k - 1]);
            }
            long eof = headers[count - 1] + Ps3DdsUtil.DdsHeaderSize + payloadLens[count - 1];
            if (eof > int.MaxValue)
            {
                throw new SbfFormatException($"the archive would be 0x{eof:X} bytes, which is too large.");
            }

            byte[] output = new byte[eof];   // テクスチャ以外はゼロ
            Array.Copy(headerBinLe, output, headerBinLe.Length);
            Ps3DdsUtil.DwordReverseInPlace(output, 0, headerBinLe.Length);
            SbfLayout.WriteU32(output, 0x08, (uint)mainEnd, be);
            SbfLayout.WriteU32(output, 0x14, (uint)count, be);

            int changedPointers = 0;
            for (int k = 0; k < count; k++)
            {
                byte[] m = metas[k];
                uint oldPointer = BitConverter.ToUInt32(m, 0x1C);
                uint newPointer = SbfLayout.VbfDataPointer(headers[k], k);
                if (oldPointer != newPointer)
                {
                    changedPointers++;
                    Console.WriteLine($"  [{k:D8}] data pointer 0x{oldPointer:X} -> 0x{newPointer:X}");
                }
                byte[] beMeta = (byte[])m.Clone();
                Array.Copy(BitConverter.GetBytes(newPointer), 0, beMeta, 0x1C, 4);
                Ps3DdsUtil.DwordReverseInPlace(beMeta, 0, beMeta.Length);

                long blockPos = blocksStart + (long)SbfLayout.VbfBlockLength * k;
                Array.Copy(beMeta, 0, output, blockPos, beMeta.Length);
                SbfLayout.WriteU32(output, SbfLayout.GlobalHeaderSize + SbfLayout.EntrySize * k, BitConverter.ToUInt32(m, 0x04), be);
                SbfLayout.WriteU32(output, SbfLayout.GlobalHeaderSize + SbfLayout.EntrySize * k + 4, (uint)blockPos, be);

                // DDS ヘッダーは dword 反転して書く。ペイロードはそのまま
                Array.Copy(textures[k], 0, output, headers[k], textures[k].Length);
                Ps3DdsUtil.DwordReverseInPlace(output, (int)headers[k], Ps3DdsUtil.DdsHeaderSize);
            }

            if (changedPointers > 0)
            {
                Console.WriteLine($"Layout recomputed: {changedPointers} data pointer(s) changed, EOF at 0x{eof:X}.");
            }
            return output;
        }

        /// <summary>
        /// 各テクスチャの標準 DDS ファイルの中身を返す (sources は表示用のファイル名)。
        /// 通常は 4/{k}.DDS。旧バージョンのアンパック結果 (_meta\_predata.bin がある) では、
        /// dds/{k}.DDS があればそれを、無ければ _predata.bin + 4/*.BIN の生スライスから取り出したものを使う。
        /// </summary>
        private byte[][] LoadVbfDdsFiles(byte[][] metas, Dictionary<int, string> dataFileByIndex, long mainEnd, out string[] sources)
        {
            int count = metas.Length;
            byte[][] result = new byte[count][];
            sources = new string[count];
            string preDataPath = Path.Combine(_inputPath, "_meta", "_predata.bin");
            if (!File.Exists(preDataPath))
            {
                for (int k = 0; k < count; k++)
                {
                    if (!dataFileByIndex.TryGetValue(k, out string relativePath))
                    {
                        throw new SbfFormatException($"block {k:D8}: no data file is listed in _meta\\_order.txt.");
                    }
                    sources[k] = relativePath;
                    result[k] = File.ReadAllBytes(Path.Combine(_inputPath, relativePath));
                }
                return result;
            }

            // ---- 旧形式: _predata.bin + 4/*.BIN を連結すると、元ファイルの mainEnd 以降の物理バイト列になる ----
            Console.WriteLine("Note: this directory was unpacked by an older version (_meta\\_predata.bin, raw 4/*.BIN slices).");
            byte[] region;
            using (MemoryStream concat = new MemoryStream())
            {
                byte[] pre = File.ReadAllBytes(preDataPath);
                concat.Write(pre, 0, pre.Length);
                for (int k = 0; k < count; k++)
                {
                    if (!dataFileByIndex.TryGetValue(k, out string relativePath))
                    {
                        throw new SbfFormatException($"block {k:D8}: no data file is listed in _meta\\_order.txt.");
                    }
                    byte[] slice = File.ReadAllBytes(Path.Combine(_inputPath, relativePath));
                    concat.Write(slice, 0, slice.Length);
                }
                region = concat.ToArray();
            }
            long[] payloadLens = new long[count];
            for (int k = 0; k < count; k++)
            {
                payloadLens[k] = BitConverter.ToUInt32(metas[k], 0x08) - Ps3DdsUtil.SizeFieldOverhead;
            }
            long[] positions = Ps3DdsUtil.LocateVbfTextures(region, mainEnd, payloadLens, out _);

            for (int k = 0; k < count; k++)
            {
                string ddsPath = Path.Combine(_inputPath, "dds", $"{k:D8}.DDS");
                if (File.Exists(ddsPath))
                {
                    sources[k] = $@"dds\{k:D8}.DDS";
                    result[k] = File.ReadAllBytes(ddsPath);
                    continue;
                }
                sources[k] = $@"{dataFileByIndex[k]} (raw slices)";
                result[k] = new byte[Ps3DdsUtil.DdsHeaderSize + payloadLens[k]];
                Array.Copy(region, positions[k] - mainEnd, result[k], 0, result[k].Length);
                Ps3DdsUtil.DwordReverseInPlace(result[k], 0, Ps3DdsUtil.DdsHeaderSize);
            }
            return result;
        }
    }
}

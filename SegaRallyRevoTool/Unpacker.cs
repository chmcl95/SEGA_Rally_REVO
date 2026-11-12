using SegaRallyRevoTool.SegaRallyRevoLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SegaRallyRevoTool
{
    class Unpacker
    {
        private string _inputPath;
        private string _destPath;
        private bool _onlyDecompress;
        private bool _ps3Requested;
        private bool _isBigEndian;

        /// <summary>アンパックしたアーカイブのエンディアン (Unpack() 後に有効。自動判定の結果)。</summary>
        public bool IsBigEndian => _isBigEndian;

        /// <param name="ps3Requested">
        /// --ps3 が指定されたか。エンディアンはファイル内容から自動判定するので参考情報としてのみ使い、
        /// 判定結果と食い違えば警告を出す。
        /// </param>
        public Unpacker(string inputPath, string outputPath, bool onlyDecompress, bool ps3Requested)
        {
            _inputPath = inputPath;
            _destPath = outputPath;
            _onlyDecompress = onlyDecompress;
            _ps3Requested = ps3Requested;
        }

        /// <exception cref="SbfFormatException">入力が SBF/VBF/SBZ1 として解釈できない場合</exception>
        public void Unpack()
        {
            Console.WriteLine("Starting to unpack...");

            // SBZ1 (zlib 外殻) なら展開する。+0x04 のサイズはプラットフォームのエンディアンなので、
            // エンディアン判定後に照合する。
            byte[] fileBytes = File.ReadAllBytes(_inputPath);
            byte[] sbfBytes = fileBytes;
            bool compressed = fileBytes.Length >= 4 && BitConverter.ToUInt32(fileBytes, 0) == SBZ1.magic;
            uint sizeFieldLe = 0, sizeFieldBe = 0;
            if (compressed)
            {
                sbfBytes = SBZ1.DecompressAuto(fileBytes, out sizeFieldLe, out sizeFieldBe);
            }

            if (_onlyDecompress)
            {
                Directory.CreateDirectory(_destPath);
                string decompedSbfPath = $@"{_destPath}\{Path.GetFileNameWithoutExtension(_inputPath)}_decomp.sbf";
                File.WriteAllBytes(decompedSbfPath, sbfBytes);
                Console.WriteLine("Decompress Done.");
                return;
            }

            _isBigEndian = SbfLayout.DetectBigEndian(sbfBytes);
            Console.WriteLine($"Detected {(_isBigEndian ? "big-endian (PS3)" : "little-endian (PC)")} archive.");
            if (_ps3Requested && !_isBigEndian)
            {
                Console.WriteLine("Warning: --ps3 was given but the file is little-endian (PC). Ignoring --ps3.");
            }
            if (compressed)
            {
                uint expected = _isBigEndian ? sizeFieldBe : sizeFieldLe;
                if (expected != sbfBytes.Length)
                {
                    Console.WriteLine($"Warning: SBZ1 size field says 0x{expected:X} bytes but the zlib stream " +
                                      $"decompressed to 0x{sbfBytes.Length:X} bytes.");
                }
            }

            Directory.CreateDirectory(_destPath);
            using (MemoryStream sbfFileStream = new MemoryStream(sbfBytes, false))
            {
                byte[] bytes;
                string destDirectoryPath = $@"{_destPath}\{Path.GetFileNameWithoutExtension(_inputPath)}";
                Directory.CreateDirectory(destDirectoryPath);
                string metaDataPath = $@"{destDirectoryPath}\_meta";
                Directory.CreateDirectory(metaDataPath);
                EndianMarker.Write(metaDataPath, _isBigEndian);
                List<string> destRelativePaths = new List<string>();

                // Decompressed / RAW SBF file
                int length = (int)SbfLayout.ReadU32(sbfBytes, 0x14, _isBigEndian);
                sbfFileStream.Seek(0x18, SeekOrigin.Begin);

                List<Entry> entrys = new List<Entry>();
                for (int i = 0; i < length; i++)
                {
                    Entry entry = new Entry(_isBigEndian);
                    entry.Unpack(sbfFileStream);
                    entrys.Add(entry);
                }

                // PS3 (VBF) は ContainerHeader/ExtraHeader が全コンテナ分連続しているため、
                // type==4 コンテナについては ExtraHeader+0x00 (DDS絶対オフセット) が
                // 実データの位置を示す。ヘッダーループでは type4 コンテナの
                // (containerIndex, DDS絶対オフセット) を記録するだけにしておき、
                // ヘッダー読み終わり後にまとめてデータ分割を行う。
                List<(int index, uint dataOffset)> ps3Type4Pointers = new List<(int, uint)>();

                for (int i = 0; i < length; i++)
                {
                    long endAddress = sbfFileStream.Length;
                    if (i < length - 1)
                    {
                        endAddress = entrys[i + 1].offset;
                    }

                    Container container = new Container(_isBigEndian);
                    sbfFileStream.Seek(entrys[i].offset, SeekOrigin.Begin);

                    string headPath  = $@"{metaDataPath}\{i:D8}.HEAD";
                    string extraPath = $@"{metaDataPath}\{i:D8}.EXTRA";

                    using (FileStream headFileStream = new FileStream(headPath, FileMode.Create, FileAccess.Write))
                    using (MemoryStream extraMemStream = new MemoryStream())
                    {
                        container.Unpack(sbfFileStream, headFileStream, extraMemStream);

                        // type==4 のときだけ .EXTRA を出力
                        if (container.type == 4)
                        {
                            extraMemStream.Seek(0, SeekOrigin.Begin);
                            byte[] extraBytes = extraMemStream.ToArray();
                            using (FileStream extraFileStream = new FileStream(extraPath, FileMode.Create, FileAccess.Write))
                            {
                                extraFileStream.Write(extraBytes, 0, extraBytes.Length);
                            }

                            if (_isBigEndian)
                            {
                                // ExtraHeader+0x00 (常に .EXTRA 内では LE 保存) = DDS 絶対オフセット
                                uint dataOffset = BitConverter.ToUInt32(extraBytes, 0x00);
                                ps3Type4Pointers.Add((i, dataOffset));
                                // PS3 の type4 データはこのループでは抽出せず、後段でまとめて処理する
                                continue;
                            }
                        }
                    }

                    long size = endAddress - sbfFileStream.Position;
                    if(size < 1)
                    {
                        continue;
                    }

                    bytes = new byte[size];
                    sbfFileStream.Read(bytes, 0x00, bytes.Length);
                    string extension = "BIN";
                    // detection DDS
                    if (bytes[0] == 0x44 && bytes[1] == 0x44 && bytes[2] == 0x53 && bytes[3] == 0x20)
                    {
                        extension = "DDS";
                    }
                    if (!Directory.Exists($@"{destDirectoryPath}\{container.type}"))
                    {
                        Directory.CreateDirectory($@"{destDirectoryPath}\{container.type}");
                    }
                    string destRelativePath = $@"{container.type}\{i:D8}.{extension}";
                    using (FileStream extractedFileStream = new FileStream($@"{destDirectoryPath}\{destRelativePath}", FileMode.Create, FileAccess.Write))
                    {
                        extractedFileStream.Write(bytes);
                    }
                    destRelativePaths.Add(destRelativePath);
                }

                // PS3 (VBF) で type4 コンテナが存在する場合、ExtraHeader+0x00 の絶対オフセットを
                // 境界にしてデータ領域を type4 コンテナごとに分割抽出する。
                //
                // 調査結果: ヘッダー連続部の終端 (headersEnd) から最初の DDS オフセットまでの間、
                // および各 DDS データの後 (次の DDS オフセットまで、あるいは最後のコンテナなら
                // EOF まで) には非ゼロの未知領域が存在する (loading_alpine1_data.vbf では
                // headersEnd=0x1110, 最初の DDS オフセット=0x1F68 で 0xE58 バイトの隙間があり、
                // 中身は全ゼロだった。一方、各 DDS データ間の隙間は非ゼロで、内容はミップマップの
                // 残骸か GPU 側のアラインメント用データと推測されるが用途は未確定)。
                // バイト一致を保証するため、これらの隙間は「意味を解釈せず」各コンテナのデータに
                // 素直に含める形で保存する。
                //   - ヘッダー直後〜最初の DDS オフセットまで: _meta\_predata.bin として保存
                //   - 各コンテナのデータ: [このコンテナの DDS オフセット, 次コンテナの DDS オフセット)
                //     (最後のコンテナは EOF まで) をまるごと 4\{i:D8}.{ext} として保存
                // Pack 時はこれらのファイルサイズから逆算して ExtraHeader+0x00 を再計算する。
                if (_isBigEndian && ps3Type4Pointers.Count > 0)
                {
                    long headersEnd = sbfFileStream.Position;
                    uint firstDataOffset = ps3Type4Pointers[0].dataOffset;
                    long preDataSize = firstDataOffset - headersEnd;

                    if (preDataSize >= 0)
                    {
                        byte[] preDataBytes = new byte[preDataSize];
                        if (preDataSize > 0)
                        {
                            sbfFileStream.Seek(headersEnd, SeekOrigin.Begin);
                            sbfFileStream.Read(preDataBytes, 0x00, preDataBytes.Length);
                        }
                        using (FileStream preDataStream = new FileStream($@"{metaDataPath}\_predata.bin", FileMode.Create, FileAccess.Write))
                        {
                            preDataStream.Write(preDataBytes, 0, preDataBytes.Length);
                        }

                        if (!Directory.Exists($@"{destDirectoryPath}\4"))
                        {
                            Directory.CreateDirectory($@"{destDirectoryPath}\4");
                        }

                        for (int k = 0; k < ps3Type4Pointers.Count; k++)
                        {
                            int idx = ps3Type4Pointers[k].index;
                            uint blockStart = ps3Type4Pointers[k].dataOffset;
                            long blockEnd = (k + 1 < ps3Type4Pointers.Count)
                                ? ps3Type4Pointers[k + 1].dataOffset
                                : sbfFileStream.Length;
                            long blockSize = blockEnd - blockStart;
                            if (blockSize < 0)
                            {
                                blockSize = 0;
                            }

                            byte[] blockBytes = new byte[blockSize];
                            if (blockSize > 0)
                            {
                                sbfFileStream.Seek(blockStart, SeekOrigin.Begin);
                                sbfFileStream.Read(blockBytes, 0x00, blockBytes.Length);
                            }

                            string extension = "BIN";
                            if (blockBytes.Length >= 4 && blockBytes[0] == 0x44 && blockBytes[1] == 0x44 && blockBytes[2] == 0x53 && blockBytes[3] == 0x20)
                            {
                                extension = "DDS";
                            }

                            string destRelativePath = $@"4\{idx:D8}.{extension}";
                            using (FileStream extractedFileStream = new FileStream($@"{destDirectoryPath}\{destRelativePath}", FileMode.Create, FileAccess.Write))
                            {
                                extractedFileStream.Write(blockBytes, 0, blockBytes.Length);
                            }
                            destRelativePaths.Add(destRelativePath);
                        }
                    }
                    // preDataSize が負 (想定外のレイアウト) の場合は分割を諦め、
                    // 何も抽出しない (ヘッダーのみとなり pack 時に再現できない)。
                    // 既知のサンプルでは発生しない防御的分岐。
                }

                // SBF Header (グローバルヘッダー + エントリーテーブル + 最初のブロックまでのパディング)。
                // 空アーカイブ (count = 0) はファイル全体をヘッダーとして保存する。
                bytes = new byte[length > 0 ? entrys[0].offset : sbfBytes.Length];
                sbfFileStream.Seek(0x00, SeekOrigin.Begin);
                sbfFileStream.Read(bytes, 0x00, bytes.Length);
                // Reversing for PS3 ver
                if (_isBigEndian)
                {
                    Ps3DdsUtil.DwordReverseInPlace(bytes, 0, bytes.Length & ~3);
                }
                using (FileStream sbfHeader = new FileStream($@"{metaDataPath}\_header.bin", FileMode.Create, FileAccess.Write))
                {
                    sbfHeader.Write(bytes);
                }
                // Order of Container
                using (StreamWriter orderTextWriter = File.CreateText($@"{metaDataPath}\_order.txt"))
                {
                    foreach (string _path in destRelativePaths)
                    {
                        orderTextWriter.WriteLine(_path);
                    }
                }
            }

            Console.WriteLine("Done.");
            return;
        }

    }
}

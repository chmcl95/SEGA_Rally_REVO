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
        private bool _isBigEndian;

        public Unpacker(string inputPath, string outputPath, bool onlyDecompress, bool isBigEndian)
        {
            _inputPath = inputPath;
            _destPath = outputPath;
            _onlyDecompress = onlyDecompress;
            _isBigEndian = isBigEndian;
        }

        public void Unpack()
        {
            Console.WriteLine("Starting to unpack...");
            Directory.CreateDirectory(_destPath);

            // Check file is SBF or SBZ1
            using (MemoryStream sbfFileStream = new MemoryStream())
            {
                byte[] bytes = new byte[4];
                using (FileStream fileStream = new FileStream(_inputPath, FileMode.Open, FileAccess.Read))
                {
                    bytes = new byte[4];
                    fileStream.Read(bytes, 0x00, bytes.Length);
                    UInt32 _magic = BitConverter.ToUInt32(bytes);
                    if (SBZ1.magic.Equals(_magic))
                    {
                        fileStream.Seek(0x00, SeekOrigin.Begin);
                        SBZ1 sbz1 = new SBZ1(_isBigEndian);
                        sbz1.Unpack(fileStream);
                        // overwrite sbfFileStream as decompressed SBZ1
                        sbz1.Decompress(fileStream, sbfFileStream);
                    } else
                    {
                        fileStream.Seek(0x00, SeekOrigin.Begin);
                        fileStream.CopyTo(sbfFileStream);
                        fileStream.Seek(0x00, SeekOrigin.Begin);
                    }
                }

                if (_onlyDecompress)
                {
                    string decompedSbfPath = $@"{_destPath}\{Path.GetFileNameWithoutExtension(_inputPath)}_decomp.sbf";
                    using (FileStream decompedStream = new FileStream(decompedSbfPath, FileMode.Create, FileAccess.Write))
                    {
                        decompedStream.Write(sbfFileStream.ToArray());
                    }
                    Console.WriteLine("Decompress Done.");
                    return;
                }

                string destDirectoryPath = $@"{_destPath}\{Path.GetFileNameWithoutExtension(_inputPath)}";
                Directory.CreateDirectory(destDirectoryPath);
                string metaDataPath = $@"{destDirectoryPath}\_meta";
                Directory.CreateDirectory(metaDataPath);
                List<string> destRelativePaths = new List<string>();

                // Decompressed / RAW SBF file
                sbfFileStream.Seek(0x14, SeekOrigin.Begin);
                bytes = new byte[4];
                sbfFileStream.Read(bytes, 0x00, bytes.Length);
                if (_isBigEndian)
                {
                    Array.Reverse(bytes);
                }
                int length = BitConverter.ToInt32(bytes);
                if(length < 1)
                {
                    Console.WriteLine("Invalid SBF file.");
                    return;
                }

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

                // SBF Header
                bytes = new byte[entrys[0].offset];
                sbfFileStream.Seek(0x00, SeekOrigin.Begin);
                sbfFileStream.Read(bytes, 0x00, bytes.Length);
                // Reversing for PS3 ver
                if (_isBigEndian)
                {
                    for (int i = 0; i < bytes.Length; i += 4)
                    {
                        Array.Reverse(bytes, i, 4);
                    }
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

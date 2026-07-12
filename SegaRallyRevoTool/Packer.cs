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

        public void Pack()
        {
            Console.WriteLine("Starting to Pack...");
            Directory.CreateDirectory(_destPath);

            byte[] bytes = new byte[4];
            List<string> fileRelativePaths = new List<string>();
            // Order of Container
            using (StreamReader orderTextReader = File.OpenText($@"{_inputPath}\_meta\_order.txt"))
            {
                while (orderTextReader.Peek() >= 0)
                {
                    fileRelativePaths.Add(orderTextReader.ReadLine());
                }
            }

            // 全 Container 数は .HEAD ファイル数から求める
            // (_order.txt にはデータファイルを持つ Container しか載らないため)
            int containerCount = Directory.GetFiles($@"{_inputPath}\_meta", "*.HEAD").Length;

            // Container 番号 → データファイル相対パス
            Dictionary<int, string> dataFileByIndex = new Dictionary<int, string>();
            foreach (string relativePath in fileRelativePaths)
            {
                dataFileByIndex[int.Parse(Path.GetFileNameWithoutExtension(relativePath))] = relativePath;
            }

            // PS3 (VBF) の type4 分割抽出モードかどうか
            // (Unpacker が _meta\_predata.bin を出力しているかで判定する)
            string preDataPath = $@"{_inputPath}\_meta\_predata.bin";
            bool ps3SplitMode = _isBigEndian && File.Exists(preDataPath);

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

            if (ps3SplitMode)
            {
                for (int i = 0; i < containerCount; i++)
                {
                    if (hasExtraByIndex[i] && dataFileByIndex.ContainsKey(i))
                    {
                        ps3Type4DataIndices.Add(i);
                    }
                }

                long headerBinLength = new FileInfo($@"{_inputPath}\_meta\_header.bin").Length;
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
            }

            using (MemoryStream sbfFileStream = new MemoryStream())
            {
                // SBF Header
                using (FileStream headerFileStream = new FileStream($@"{_inputPath}\_meta\_header.bin", FileMode.Open, FileAccess.Read))
                {
                    bytes = new byte[headerFileStream.Length];
                    headerFileStream.Read(bytes, 0x00, bytes.Length);
                    // Reversing for PS3 ver (_header.bin はアンパック時に LE 化されている)
                    if (_isBigEndian)
                    {
                        for (int i = 0; i < bytes.Length; i += 4)
                        {
                            Array.Reverse(bytes, i, 4);
                        }
                    }
                    sbfFileStream.Write(bytes);
                }

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
                        bool hasExtra = hasExtraByIndex[i];
                        if (hasExtra)
                        {
                            byte[] extraBytes;
                            using (FileStream extraFileStream = new FileStream(extraPath, FileMode.Open, FileAccess.Read))
                            {
                                extraBytes = new byte[extraFileStream.Length];
                                extraFileStream.Read(extraBytes, 0x00, extraBytes.Length);
                            }

                            // PS3 分割モードでは ExtraHeader+0x00 (DDS絶対オフセット) を
                            // 実際のレイアウトに合わせて逆算した値で上書きする
                            // (.EXTRA は常に LE 保存)
                            if (ps3SplitMode && ps3ComputedDataOffset.TryGetValue(i, out uint newDataOffset))
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

                    // ContainerFile (.DDS / .BIN)
                    // PS3 分割モードではヘッダーが全コンテナ分連続する必要があるため、
                    // データ本体はここでは書かず、ヘッダーループの後にまとめて書く。
                    if (!ps3SplitMode)
                    {
                        // データファイルを持たない Container (PS3 のヘッダー連続部など) はスキップ
                        if (dataFileByIndex.TryGetValue(i, out string dataRelativePath))
                        {
                            using (FileStream containerFileStream = new FileStream($@"{_inputPath}\{dataRelativePath}", FileMode.Open, FileAccess.Read))
                            {
                                bytes = new byte[containerFileStream.Length];
                                containerFileStream.Read(bytes, 0x00, bytes.Length);
                                sbfFileStream.Write(bytes);
                            }
                        }
                    }
                }

                // PS3 分割モード: ヘッダー連続部の直後に _predata (未知領域) →
                // type4 データを並び順どおりに書き出す
                if (ps3SplitMode)
                {
                    using (FileStream preDataStream = new FileStream(preDataPath, FileMode.Open, FileAccess.Read))
                    {
                        bytes = new byte[preDataStream.Length];
                        preDataStream.Read(bytes, 0x00, bytes.Length);
                        sbfFileStream.Write(bytes);
                    }

                    foreach (int idx in ps3Type4DataIndices)
                    {
                        string dataRelativePath = dataFileByIndex[idx];
                        using (FileStream containerFileStream = new FileStream($@"{_inputPath}\{dataRelativePath}", FileMode.Open, FileAccess.Read))
                        {
                            bytes = new byte[containerFileStream.Length];
                            containerFileStream.Read(bytes, 0x00, bytes.Length);
                            sbfFileStream.Write(bytes);
                        }
                    }
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

                string extension = _isBigEndian ? "vbf" : "sbf";
                if (_disableCompress)
                {
                    using (FileStream uncompressedStream = new FileStream($@"{_destPath}\{Path.GetFileName(_inputPath)}.{extension}", FileMode.Create, FileAccess.Write))
                    {
                        sbfFileStream.Seek(0x0, SeekOrigin.Begin);
                        uncompressedStream.Write(sbfFileStream.ToArray());
                    }
                    Console.WriteLine("Uncompress SBF Generate Done.");
                    return;
                }

                using (FileStream sbz1Stream = new FileStream($@"{_destPath}\{Path.GetFileName(_inputPath)}.{extension}", FileMode.Create, FileAccess.Write))
                {
                    sbfFileStream.Seek(0x0, SeekOrigin.Begin);
                    sbz1Stream.Seek(0x8, SeekOrigin.Begin);
                    SBZ1 sbz1 = new SBZ1(_isBigEndian);
                    sbz1.Compress(sbfFileStream, sbz1Stream);
                    sbz1Stream.Seek(0x0, SeekOrigin.Begin);
                    sbz1.Pack(sbz1Stream);
                }
            }

            Console.WriteLine("Done.");
            return;
        }

    }
}

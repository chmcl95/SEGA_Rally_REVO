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

            using (MemoryStream sbfFileStream = new MemoryStream())
            {
                // SBF Header
                using (FileStream headerFileStream = new FileStream($@"{_inputPath}\_meta\_header.bin", FileMode.Open, FileAccess.Read))
                {
                    bytes = new byte[headerFileStream.Length];
                    headerFileStream.Read(bytes, 0x00, bytes.Length);
                    sbfFileStream.Write(bytes);
                }

                List<Entry> entrys = new List<Entry>();
                for (int i = 0; i < fileRelativePaths.Count; i++)
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
                        bool hasExtra = File.Exists(extraPath);
                        if (hasExtra)
                        {
                            using (FileStream extraFileStream = new FileStream(extraPath, FileMode.Open, FileAccess.Read))
                            {
                                Container container = new Container(_isBigEndian);
                                container.Pack(sbfFileStream, headFileStream, extraFileStream);
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
                    // type==4 以外 (type==5 など) はデータファイルなし → _order.txt に含まれないので
                    // ここでは _order.txt に列挙されたファイルのみ書く
                    using (FileStream containerFileStream = new FileStream($@"{_inputPath}\{fileRelativePaths[i]}", FileMode.Open, FileAccess.Read))
                    {
                        bytes = new byte[containerFileStream.Length];
                        containerFileStream.Read(bytes, 0x00, bytes.Length);
                        sbfFileStream.Write(bytes);
                    }
                }

                // Entry テーブルを書き込む
                sbfFileStream.Seek(0x14, SeekOrigin.Begin);
                bytes = BitConverter.GetBytes((UInt32)fileRelativePaths.Count);
                sbfFileStream.Write(bytes);
                foreach (Entry entry in entrys)
                {
                    entry.Pack(sbfFileStream);
                }

                if (_disableCompress)
                {
                    using (FileStream uncompressedStream = new FileStream($@"{_destPath}\{Path.GetFileName(_inputPath)}.sbf", FileMode.Create, FileAccess.Write))
                    {
                        sbfFileStream.Seek(0x0, SeekOrigin.Begin);
                        uncompressedStream.Write(sbfFileStream.ToArray());
                    }
                    Console.WriteLine("Uncompress SBF Generate Done.");
                    return;
                }

                using (FileStream sbz1Stream = new FileStream($@"{_destPath}\{Path.GetFileName(_inputPath)}.sbf", FileMode.Create, FileAccess.Write))
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

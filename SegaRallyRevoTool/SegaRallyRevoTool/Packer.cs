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

        public Packer(string inputPath, string outputPath)
        {
            _inputPath = inputPath;
            _destPath = outputPath;
        }

        public void Pack()
        {
            Console.WriteLine("Starting to Pack...");
            Directory.CreateDirectory(_destPath);

            byte[] bytes = new byte[4];
            List<string> fileRelativePaths = new List<string>();
            // Order of Conatiner
            using (StreamReader orderTextReader = File.OpenText($@"{_inputPath}\_meta\_order.txt"))
            {
                while(orderTextReader.Peek() >= 0)
                {
                    fileRelativePaths.Add(orderTextReader.ReadLine());
                }
            }

            using (FileStream sbfFileStream = new FileStream($@"{_destPath}\{Path.GetFileName(_inputPath)}.sbf", FileMode.Create, FileAccess.Write))
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
                    // ContainerHeader
                    using (FileStream containerHeaderFileStream = new FileStream($@"{_inputPath}\_meta\{i:D8}.HEAD", FileMode.Open, FileAccess.Read))
                    {
                        Entry entry = new Entry();
                        entry.offset = (UInt32)sbfFileStream.Position;
                        containerHeaderFileStream.Seek(0x04, SeekOrigin.Begin);
                        bytes = new byte[4];
                        containerHeaderFileStream.Read(bytes, 0x00, bytes.Length);
                        entry.unk0x00 = BitConverter.ToUInt32(bytes);
                        entrys.Add(entry);

                        containerHeaderFileStream.Seek(0x00, SeekOrigin.Begin);
                        bytes = new byte[containerHeaderFileStream.Length];
                        containerHeaderFileStream.Read(bytes, 0x00, bytes.Length);
                        sbfFileStream.Write(bytes);
                    }

                    // ContainerFile
                    using (FileStream containerFileStream = new FileStream($@"{_inputPath}\{fileRelativePaths[i]}", FileMode.Open, FileAccess.Read))
                    {
                        bytes = new byte[containerFileStream.Length];
                        containerFileStream.Read(bytes, 0x00, bytes.Length);
                        sbfFileStream.Write(bytes);
                    }

                    //TODO: Padding
                }

                // Entry
                sbfFileStream.Seek(0x14, SeekOrigin.Begin);
                bytes = BitConverter.GetBytes((UInt32)fileRelativePaths.Count);
                sbfFileStream.Write(bytes);
                foreach(Entry entry in entrys)
                {
                    entry.Pack(sbfFileStream);
                }
            }

            Console.WriteLine("Done.");
            return;
        }

    }
}

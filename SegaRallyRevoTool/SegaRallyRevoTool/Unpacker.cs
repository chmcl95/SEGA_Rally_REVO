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

        public Unpacker(string inputPath, string outputPath)
        {
            _inputPath = inputPath;
            _destPath = outputPath;
        }

        public void Unpack()
        {
            Console.WriteLine("Starting to unpack...");

            string destDirectoryPath = $@"{ _destPath}\{Path.GetFileNameWithoutExtension(_inputPath)}";
            Directory.CreateDirectory(destDirectoryPath);
            string metaDataPath = $@"{destDirectoryPath}\_meta";
            Directory.CreateDirectory(metaDataPath);

            List<string> destRelativePaths = new List<string>();

            // SBF file
            using (FileStream sbfFileStream = new FileStream(_inputPath, FileMode.Open, FileAccess.Read))
            {
                sbfFileStream.Seek(0x14, SeekOrigin.Begin);
                byte[] bytes = new byte[4];
                sbfFileStream.Read(bytes, 0x00, 4);
                int length = BitConverter.ToInt32(bytes);
                if(length < 1)
                {
                    Console.WriteLine("Invalid SBF file.");
                    return;
                }

                List<Entry> entrys = new List<Entry>();
                for (int i = 0; i < length; i++)
                {
                    Entry entry = new Entry();
                    entry.Unpack(sbfFileStream);
                    entrys.Add(entry);
                }

                for (int i = 0; i < length; i++)
                {
                    long endAddress = sbfFileStream.Length;
                    if (i < length-1)
                    {
                        endAddress = entrys[i + 1].offset;
                    }

                    Container container = new Container();
                    sbfFileStream.Seek(entrys[i].offset, SeekOrigin.Begin);
                    using (FileStream metaDataFileStream = new FileStream($@"{metaDataPath}\{i:D8}.HEAD", FileMode.Create, FileAccess.Write))
                    {

                        container.Unpack(sbfFileStream, metaDataFileStream);
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
                    if (bytes[0]==0x44 && bytes[1] == 0x44 && bytes[2] == 0x53 && bytes[3] == 0x20)
                    {
                        extension = "DDS";
                    }
                    if(!Directory.Exists($@"{destDirectoryPath}\{container.type}"))
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

                // SBF Header
                bytes = new byte[0x8 * length + 0x14 + 4];
                sbfFileStream.Seek(0x00, SeekOrigin.Begin);
                sbfFileStream.Read(bytes, 0x00, bytes.Length);
                using (FileStream sbfHeader = new FileStream($@"{metaDataPath}\_header.bin", FileMode.Create, FileAccess.Write))
                {
                    sbfHeader.Write(bytes);
                }
                // Order of Conatiner
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

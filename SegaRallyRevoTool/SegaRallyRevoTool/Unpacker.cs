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

        public Unpacker(string inputPath, string outputPath, bool onlyDecompress)
        {
            _inputPath = inputPath;
            _destPath = outputPath;
            _onlyDecompress = onlyDecompress;
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
                        SBZ1 sbz1 = new SBZ1();
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
                bytes = new byte[entrys[0].offset];
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

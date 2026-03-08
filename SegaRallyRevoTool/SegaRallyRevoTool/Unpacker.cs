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


        public Unpacker(string revision, string exePath, string inputPath, string outputPath)
        {
            _inputPath = inputPath;
            _destPath = outputPath;
        }

        public void Unpack()
        {
            Console.WriteLine("Starting to unpack...");

            string destPath = $@"{ _destPath}\{Path.GetFileNameWithoutExtension(_inputPath)}";
            Directory.CreateDirectory(destPath);
            string headerPath = $@"{destPath}\header";
            Directory.CreateDirectory(headerPath);

            // SBF file
            using (FileStream sbfFileStream = new FileStream(destPath, FileMode.Open, FileAccess.Read))
            using (FileStream headerFileStream = new FileStream(headerPath, FileMode.Create, FileAccess.Write))
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
                    Container container = new Container();
                    sbfFileStream.Seek(entrys[i].offset, SeekOrigin.Begin);
                    container.Unpack(sbfFileStream, headerFileStream);
                }


                /*
                // SBF Header
                int headerSize = 0x8 * length + 0x14 + 4;
                bytes = new byte[headerSize];
                sbfFileStream.Seek(0x00, SeekOrigin.Begin);
                sbfFileStream.Read(bytes, 0x00, headerSize);
                using (FileStream sbfHeader = new FileStream($@"{headerPath}\header.bin", FileMode.Create, FileAccess.Write))
                {
                    sbfHeader.Write(bytes);
                }
                */

            }

            Console.WriteLine("Done.");
            return;
        }

    }
}

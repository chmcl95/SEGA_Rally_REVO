using AuroraLib.Compression.Formats.Common;
using System;
using System.IO;

namespace SegaRallyRevoTool.SegaRallyRevoLib
{
    class SBZ1
    {
        public const UInt32 magic = 0x315A4253; // SBZ1
        public Int32 decompressedSize = 0x00;

        public bool Unpack(FileStream sbz1FileStream) {

            byte[] bytes = new byte[0x8];
            sbz1FileStream.Read(bytes, 0x00, 0x8);

            UInt32 _magic = BitConverter.ToUInt32(bytes, 0x00);
            if (!magic.Equals(_magic)){
                return true;
            }

            decompressedSize = BitConverter.ToInt32(bytes, 0x04); // magic + decompress size = 8

            return false;

        }

        public bool Pack(FileStream sbz1FileStream)
        {
            byte[] bytes = BitConverter.GetBytes(magic);
            sbz1FileStream.Write(bytes);
            bytes = BitConverter.GetBytes(decompressedSize);
            sbz1FileStream.Write(bytes);

            return false;

        }

        public void Decompress(Stream sbz1FileStream, Stream sbfFileStream)
        {
            byte[] compressed = new byte[decompressedSize];
            sbz1FileStream.Read(compressed, 0x00, decompressedSize);

            using (MemoryStream compressedStream = new MemoryStream(compressed))
            {
                new ZLib().Decompress(compressedStream, sbfFileStream);
            }
        }

        public void Compress(Stream sbfFileStream, Stream sbz1FileStream)
        {
            byte[] uncompressed = new byte[sbfFileStream.Length];
            decompressedSize = (Int32)uncompressed.Length;
            sbfFileStream.Read(uncompressed, 0x00, (int)sbfFileStream.Length);
            new ZLib().Compress(uncompressed, sbz1FileStream, AuroraLib.Compression.CompressionSettings.Maximum);
        }
    }
}

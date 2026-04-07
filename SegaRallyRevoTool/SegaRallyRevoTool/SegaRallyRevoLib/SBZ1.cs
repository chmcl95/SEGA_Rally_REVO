using AuroraLib.Compression.Formats.Common;
using System;
using System.IO;

namespace SegaRallyRevoTool.SegaRallyRevoLib
{
    class SBZ1
    {
        public const UInt32 magic = 0x315A4253; // SBZ1
        public Int32 size = 0x00;

        public bool Unpack(FileStream sbz1FileStream) {

            byte[] bytes = new byte[0x8];
            sbz1FileStream.Read(bytes, 0x00, 0x8);

            UInt32 _magic = BitConverter.ToUInt32(bytes, 0x00);
            if (!magic.Equals(_magic)){
                return true;
            }

            size = BitConverter.ToInt32(bytes, 0x04)-8; // magic + decompress size = 8

            return false;

        }

        public bool Pack(FileStream sbz1FileStream)
        {
            byte[] bytes = BitConverter.GetBytes(magic);
            sbz1FileStream.Write(bytes);
            bytes = BitConverter.GetBytes(size);
            sbz1FileStream.Write(bytes);

            return false;

        }

        public void Decompress(FileStream sbz1FileStream, FileStream sbfFileStream)
        {
            byte[] compressed = new byte[sbz1FileStream.Length-8];
            sbz1FileStream.Read(compressed, 0x00, compressed.Length);

            using (MemoryStream compressedStream = new MemoryStream(compressed))
            {
                new ZLib().Decompress(compressedStream, sbfFileStream);
            }
        }

        public void Compress(FileStream sbfFileStream, FileStream sbz1FileStream)
        {

            using (MemoryStream compressedStream = new MemoryStream())
            {
                new ZLib().Compress( , compressedStream);
            }
            //ZLib zLib = new ZLib();
            //byte[] compressed = new byte[zLib.CompressBound((uint)sbfFileStream.Length)];
            //byte[] decompressed = new byte[size];
            //sbfFileStream.Read(decompressed, 0, size);

            //ZStream compressStream = new ZStream
            //{
            //    Input = decompressed,
            //    Output = compressed
            //};
            //zLib.DeflateInit(ref compressStream, Z_DEFAULT_COMPRESSION);
            //zLib.Deflate(ref compressStream, Z_DEFAULT_COMPRESSION);
            //zLib.DeflateEnd(ref compressStream);

            //sbz1FileStream.Write(decompressed);
        }
    }
}

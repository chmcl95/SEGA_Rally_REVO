using System;
using System.Collections.Generic;
using System.IO;

namespace SegaRallyRevoTool.SegaRallyRevoLib
{
    public class SBF
    {
        public bool Unpack(string srcPath, out List<Entry> entries)
        {
            entries = new List<Entry>();
            try {
                using (FileStream fileStream = new FileStream(srcPath, FileMode.Open, FileAccess.Read)) 
                {
                    byte[] bytes = new byte[4];
                    fileStream.Read(bytes, 0x00, 4);
                    uint entryLength = BitConverter.ToUInt32(bytes, 0x00);
                    for (int i = 0; i < entryLength; i++) 
                    {
                        Entry entry = new Entry();
                        if (entry.Unpack(fileStream)) 
                        {
                            return true;
                        }
                        entries.Add(entry);
                    }
                }
                return false;
            } catch {
                return true;
            }
        }

    }


    public class Entry
    {
        public uint unk0x00;
        public uint offset;

        public bool Unpack(FileStream fileStream)
        {
            byte[] bytes = new byte[4];
            try
            {
                fileStream.Read(bytes, 0x00, 4);
                unk0x00 = BitConverter.ToUInt32(bytes, 0x00);
                fileStream.Read(bytes, 0x00, 4);
                offset = BitConverter.ToUInt32(bytes, 0x00);
                return false;
            }
            catch
            {
                return true;
            }
        }

        public bool Pack(FileStream fileStream)
        {
            try
            {
                byte[] bytes = BitConverter.GetBytes(unk0x00);
                fileStream.Write(bytes);
                bytes = BitConverter.GetBytes(offset);
                fileStream.Write(bytes);
                return false;
            }
            catch
            {
                return true;
            }
        }
    }

    public class Container
    {
        public int type;
        public uint unk0x04;
        public int unk0x08;
        public uint unk0x0C;
        public uint unk0x10;
        public uint unk0x14;
        public uint unk0x18;

        public bool Unpack(FileStream sbfFileStream, FileStream metaDataFileStream)
        {
            byte[] bytes = new byte[0x1C];
            try
            {
                sbfFileStream.Read(bytes, 0x00, 0x1C);
                metaDataFileStream.Write(bytes);

                type = BitConverter.ToInt32(bytes, 0x00);
                unk0x04 = BitConverter.ToUInt32(bytes, 0x04);
                unk0x08 = BitConverter.ToInt32(bytes, 0x08);
                unk0x0C = BitConverter.ToUInt32(bytes, 0x0C);
                unk0x10 = BitConverter.ToUInt32(bytes, 0x10);
                unk0x14 = BitConverter.ToUInt32(bytes, 0x14);
                unk0x18 = BitConverter.ToUInt32(bytes, 0x18);

                if(type == 4)
                {
                    bytes = new byte[0x28];
                    sbfFileStream.Read(bytes, 0x00, bytes.Length);
                    metaDataFileStream.Write(bytes);
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        public bool Pack(FileStream fileStream)
        {
            try
            {
                //byte[] bytes = BitConverter.GetBytes(type);
                //fileStream.Write(bytes);
                //bytes = BitConverter.GetBytes(unk0x04);
                //fileStream.Write(bytes);
                //bytes = BitConverter.GetBytes(size);
                //fileStream.Write(bytes);
                return false;
            }
            catch
            {
                return true;
            }
        }
    }

}

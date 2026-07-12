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
                        Entry entry = new Entry(false);
                        if (!entry.Unpack(fileStream)) 
                        {
                            return false;
                        }
                        entries.Add(entry);
                    }
                }
                return true;
            } catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine($"SBF Unpack error: {ex.Message}");
                return false;
            }
        }
    }


    public class Entry
    {
        public uint unk0x00;
        public uint offset;
        bool _isBigEndian;

        public Entry(bool isBigEndian)
        {
            _isBigEndian = isBigEndian;
        }

        public bool Unpack(Stream fileStream)
        {
            byte[] bytes = new byte[4];
            try
            {
                // Reversing for PS3 ver
                if (_isBigEndian)
                {
                    fileStream.Read(bytes, 0x00, 4);
                    Array.Reverse(bytes);
                    unk0x00 = BitConverter.ToUInt32(bytes, 0x00);
                    fileStream.Read(bytes, 0x00, 4);
                    Array.Reverse(bytes);
                    offset = BitConverter.ToUInt32(bytes, 0x00);
                }
                // PC ver
                else
                {
                    fileStream.Read(bytes, 0x00, 4);
                    unk0x00 = BitConverter.ToUInt32(bytes, 0x00);
                    fileStream.Read(bytes, 0x00, 4);
                    offset = BitConverter.ToUInt32(bytes, 0x00);
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Entry Unpack error: {ex.Message}");
                return false;
            }
        }

        public bool Pack(Stream fileStream)
        {
            try
            {
                byte[] bytes = new Byte[4];
                // Reversing for PS3 ver
                if (_isBigEndian)
                {
                    bytes = BitConverter.GetBytes(unk0x00);
                    Array.Reverse(bytes);
                    fileStream.Write(bytes);
                    bytes = BitConverter.GetBytes(offset);
                    Array.Reverse(bytes);
                    fileStream.Write(bytes);
                }
                // PC ver
                else
                {
                    bytes = BitConverter.GetBytes(unk0x00);
                    fileStream.Write(bytes);
                    bytes = BitConverter.GetBytes(offset);
                    fileStream.Write(bytes);
                }
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

        bool _isBigEndian;

        public Container(bool isBigEndian)
        {
            _isBigEndian = isBigEndian;
        }

        /// <summary>
        /// ContainerHeader(0x1C) を sbfFileStream から読み headFileStream へ書く。
        /// type==4 の場合は ExtraHeader(0x28) も sbfFileStream から読み extraFileStream へ書く。
        /// </summary>
        public bool Unpack(Stream sbfFileStream, Stream headFileStream, Stream extraFileStream)
        {
            byte[] bytes = new byte[0x1C];
            try
            {
                sbfFileStream.Read(bytes, 0x00, 0x1C);

                // Reversing for PS3 ver
                if (_isBigEndian)
                {
                    for (int i = 0; i < bytes.Length; i += 4)
                    {
                        Array.Reverse(bytes, i, 4);
                    }
                }

                headFileStream.Write(bytes, 0, bytes.Length);

                type    = BitConverter.ToInt32(bytes,  0x00);
                unk0x04 = BitConverter.ToUInt32(bytes, 0x04);
                unk0x08 = BitConverter.ToInt32(bytes,  0x08);
                unk0x0C = BitConverter.ToUInt32(bytes, 0x0C);
                unk0x10 = BitConverter.ToUInt32(bytes, 0x10);
                unk0x14 = BitConverter.ToUInt32(bytes, 0x14);
                unk0x18 = BitConverter.ToUInt32(bytes, 0x18);

                if (type == 4)
                {
                    bytes = new byte[0x28];
                    sbfFileStream.Read(bytes, 0x00, bytes.Length);

                    if (_isBigEndian)
                    {
                        for (int i = 0; i < bytes.Length; i += 4)
                        {
                            Array.Reverse(bytes, i, 4);
                        }
                    }

                    // ExtraHeader は別ストリームへ
                    extraFileStream.Write(bytes, 0, bytes.Length);
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Container Unpack error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ContainerHeader(0x1C) を sbfFileStream へ書く。
        /// type==4 の場合は続けて ExtraHeader(0x28) も書く。
        /// </summary>
        public bool Pack(Stream sbfFileStream, Stream headFileStream, Stream extraFileStream)
        {
            try
            {
                byte[] bytes = new byte[0x1C];
                headFileStream.Read(bytes, 0x00, bytes.Length);

                // .HEAD は常に LE 保存なので、BE 変換前に type を読む
                type = BitConverter.ToInt32(bytes, 0x00);

                if (_isBigEndian)
                {
                    for (int i = 0; i < bytes.Length; i += 4)
                    {
                        Array.Reverse(bytes, i, 4);
                    }
                }

                sbfFileStream.Write(bytes, 0, bytes.Length);

                if (type == 4)
                {
                    bytes = new byte[0x28];
                    extraFileStream.Read(bytes, 0x00, bytes.Length);

                    if (_isBigEndian)
                    {
                        for (int i = 0; i < bytes.Length; i += 4)
                        {
                            Array.Reverse(bytes, i, 4);
                        }
                    }

                    sbfFileStream.Write(bytes, 0, bytes.Length);
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Container Pack error: {ex.Message}");
                return false;
            }
        }
    }

}

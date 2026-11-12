using System;

namespace SegaRallyRevoTool.SegaRallyRevoLib
{
    /// <summary>
    /// 標準 (リトルエンディアン) DDS ヘッダー 0x80 バイトの読み取りヘルパー。
    ///
    /// ContainerHeader +0x08 (size) は
    ///     size = ヘッダーが宣言するミップ全段の合計バイト数 + 0xAC
    /// という関係にあり (サンプル全 358 テクスチャで成立を確認)、テクスチャを
    /// 差し替えたときはこの値を再計算する必要がある。その計算をここで行う。
    /// </summary>
    public class DdsHeader
    {
        /// <summary>DDS_PIXELFORMAT.dwFourCC のオフセット。</summary>
        private const int FourCcOffset = 84;
        /// <summary>DDS_PIXELFORMAT.dwRGBBitCount のオフセット。</summary>
        private const int RgbBitCountOffset = 88;

        private static readonly uint FourCcDxt1 = ToFourCc("DXT1");
        private static readonly uint FourCcDxt2 = ToFourCc("DXT2");
        private static readonly uint FourCcDxt3 = ToFourCc("DXT3");
        private static readonly uint FourCcDxt4 = ToFourCc("DXT4");
        private static readonly uint FourCcDxt5 = ToFourCc("DXT5");

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int MipMapCount { get; private set; }
        public uint FourCc { get; private set; }
        public int RgbBitCount { get; private set; }

        /// <summary>
        /// leHeader (0x80 バイトの LE DDS ヘッダー) を解釈する。
        /// 妥当な DDS ヘッダーでなければ null を返す。
        /// </summary>
        public static DdsHeader Parse(byte[] leHeader)
        {
            if (leHeader == null || leHeader.Length < Ps3DdsUtil.DdsHeaderSize)
            {
                return null;
            }

            bool magicOk = leHeader[0] == (byte)'D' && leHeader[1] == (byte)'D' &&
                           leHeader[2] == (byte)'S' && leHeader[3] == (byte)' ';
            if (!magicOk || BitConverter.ToUInt32(leHeader, 4) != 124)
            {
                return null;
            }

            DdsHeader header = new DdsHeader
            {
                Height      = (int)BitConverter.ToUInt32(leHeader, 12),
                Width       = (int)BitConverter.ToUInt32(leHeader, 16),
                MipMapCount = (int)BitConverter.ToUInt32(leHeader, 28),
                FourCc      = BitConverter.ToUInt32(leHeader, FourCcOffset),
                RgbBitCount = (int)BitConverter.ToUInt32(leHeader, RgbBitCountOffset),
            };

            if (header.Width < 1 || header.Height < 1)
            {
                return null;
            }

            return header;
        }

        /// <summary>
        /// ヘッダーが宣言するミップ段数ぶんのピクセルデータ合計バイト数。
        /// アーカイブ内のペイロード長 (= ContainerHeader +0x08 の size - 0xAC) と一致する。
        /// </summary>
        public long ComputePayloadSize()
        {
            int levels = MipMapCount > 0 ? MipMapCount : 1;
            long total = 0;
            int w = Width;
            int h = Height;

            for (int i = 0; i < levels; i++)
            {
                total += ComputeLevelSize(w, h);
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
            }

            return total;
        }

        private long ComputeLevelSize(int w, int h)
        {
            int blockBytes = BlockBytes();
            if (blockBytes > 0)
            {
                long blocks = (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4);
                return blocks * blockBytes;
            }

            // 非圧縮フォーマット (サンプルでは dwRGBBitCount=32 の 1 種類のみ)
            int bits = RgbBitCount > 0 ? RgbBitCount : 32;
            return (long)w * h * bits / 8;
        }

        /// <summary>ブロック圧縮なら 1 ブロックあたりのバイト数、非圧縮なら 0。</summary>
        private int BlockBytes()
        {
            if (FourCc == FourCcDxt1)
            {
                return 8;
            }
            if (FourCc == FourCcDxt2 || FourCc == FourCcDxt3 ||
                FourCc == FourCcDxt4 || FourCc == FourCcDxt5)
            {
                return 16;
            }
            return 0;
        }

        private static uint ToFourCc(string s)
        {
            return (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));
        }
    }
}

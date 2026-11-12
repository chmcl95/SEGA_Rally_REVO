using System;

namespace SegaRallyRevoTool.SegaRallyRevoLib
{
    /// <summary>
    /// 標準 (リトルエンディアン) DDS ヘッダー 0x80 バイトの読み取りヘルパー。
    ///
    /// ブロックヘッダー +0x08 (size) は
    ///     size = ヘッダーが宣言するミップ全段の合計バイト数 (キューブマップは全面) + 0xAC
    /// という関係にある (製品版 PC 16,786 / PS3 4,050 テクスチャ全件で成立。
    /// Document/SBF_format.md §6.4)。テクスチャを差し替えたときはこの値を再計算する必要がある。
    /// その計算をここで行う。
    /// </summary>
    public class DdsHeader
    {
        private const int DepthOffset = 24;
        private const int PixelFormatFlagsOffset = 80;
        /// <summary>DDS_PIXELFORMAT.dwFourCC のオフセット。</summary>
        private const int FourCcOffset = 84;
        /// <summary>DDS_PIXELFORMAT.dwRGBBitCount のオフセット。</summary>
        private const int RgbBitCountOffset = 88;
        private const int Caps2Offset = 112;

        private const uint DdpfAlpha = 0x2;
        private const uint DdpfFourCc = 0x4;
        private const uint DdpfRgb = 0x40;
        private const uint DdpfYuv = 0x200;
        private const uint DdpfLuminance = 0x20000;

        private const uint Caps2Cubemap = 0x200;
        private const uint Caps2CubemapAllFaces = 0xFC00;
        private const uint Caps2Volume = 0x200000;

        private static readonly uint FourCcDxt1 = ToFourCc("DXT1");
        private static readonly uint FourCcDxt2 = ToFourCc("DXT2");
        private static readonly uint FourCcDxt3 = ToFourCc("DXT3");
        private static readonly uint FourCcDxt4 = ToFourCc("DXT4");
        private static readonly uint FourCcDxt5 = ToFourCc("DXT5");
        private static readonly uint FourCcAti1 = ToFourCc("ATI1");
        private static readonly uint FourCcAti2 = ToFourCc("ATI2");
        private static readonly uint FourCcBc4U = ToFourCc("BC4U");
        private static readonly uint FourCcBc4S = ToFourCc("BC4S");
        private static readonly uint FourCcBc5U = ToFourCc("BC5U");
        private static readonly uint FourCcBc5S = ToFourCc("BC5S");

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Depth { get; private set; }
        public int MipMapCount { get; private set; }
        public uint PixelFormatFlags { get; private set; }
        public uint FourCc { get; private set; }
        public int RgbBitCount { get; private set; }
        public uint Caps2 { get; private set; }

        /// <summary>DX10 拡張ヘッダー (0x14 バイト) 付き。アーカイブは 0x80 バイトの旧形式ヘッダーしか想定しない。</summary>
        public bool HasDx10Header => (PixelFormatFlags & DdpfFourCc) != 0 && FourCc == ToFourCc("DX10");

        public bool IsCubemap => (Caps2 & Caps2Cubemap) != 0;
        public bool IsVolume => (Caps2 & Caps2Volume) != 0;

        /// <summary>キューブマップの面数 (通常テクスチャは 1)。</summary>
        public int FaceCount
        {
            get
            {
                if (!IsCubemap)
                {
                    return 1;
                }
                int faces = CountBits(Caps2 & Caps2CubemapAllFaces);
                return faces > 0 ? faces : 6;
            }
        }

        /// <summary>
        /// ComputePayloadSize() が信頼できる値を返すピクセル形式かどうか。
        /// false の形式 (DX10 拡張ヘッダー付き・不明な FourCC など) は、サイズ変更時の
        /// size 再計算ができない。
        /// </summary>
        public bool IsSizeComputable
        {
            get
            {
                if ((PixelFormatFlags & DdpfFourCc) != 0)
                {
                    return BlockBytes() > 0 || D3dFormatBytesPerPixel() > 0;
                }
                return (PixelFormatFlags & (DdpfRgb | DdpfLuminance | DdpfAlpha | DdpfYuv)) != 0 && RgbBitCount > 0;
            }
        }

        /// <summary>ログ表示用の形式名 (例: "DXT1", "RGB32")。</summary>
        public string FormatName
        {
            get
            {
                if ((PixelFormatFlags & DdpfFourCc) != 0)
                {
                    char[] c = { (char)(FourCc & 0xFF), (char)((FourCc >> 8) & 0xFF),
                                 (char)((FourCc >> 16) & 0xFF), (char)((FourCc >> 24) & 0xFF) };
                    foreach (char ch in c)
                    {
                        if (ch < 0x20 || ch > 0x7E)
                        {
                            return $"FourCC 0x{FourCc:X8}";
                        }
                    }
                    return new string(c);
                }
                return $"RGB{RgbBitCount}";
            }
        }

        /// <summary>
        /// leHeader (0x80 バイトの LE DDS ヘッダー) を解釈する。
        /// 妥当な DDS ヘッダーでなければ null を返す。
        /// </summary>
        public static DdsHeader Parse(byte[] leHeader)
        {
            return Parse(leHeader, 0);
        }

        /// <summary>buffer[offset..offset+0x80] を LE DDS ヘッダーとして解釈する。</summary>
        public static DdsHeader Parse(byte[] buffer, int offset)
        {
            if (buffer == null || offset < 0 || buffer.Length - offset < Ps3DdsUtil.DdsHeaderSize)
            {
                return null;
            }

            bool magicOk = buffer[offset] == (byte)'D' && buffer[offset + 1] == (byte)'D' &&
                           buffer[offset + 2] == (byte)'S' && buffer[offset + 3] == (byte)' ';
            if (!magicOk || BitConverter.ToUInt32(buffer, offset + 4) != 124)
            {
                return null;
            }

            DdsHeader header = new DdsHeader
            {
                Height           = (int)BitConverter.ToUInt32(buffer, offset + 12),
                Width            = (int)BitConverter.ToUInt32(buffer, offset + 16),
                Depth            = (int)BitConverter.ToUInt32(buffer, offset + DepthOffset),
                MipMapCount      = (int)BitConverter.ToUInt32(buffer, offset + 28),
                PixelFormatFlags = BitConverter.ToUInt32(buffer, offset + PixelFormatFlagsOffset),
                FourCc           = BitConverter.ToUInt32(buffer, offset + FourCcOffset),
                RgbBitCount      = (int)BitConverter.ToUInt32(buffer, offset + RgbBitCountOffset),
                Caps2            = BitConverter.ToUInt32(buffer, offset + Caps2Offset),
            };

            if (header.Width < 1 || header.Height < 1)
            {
                return null;
            }

            return header;
        }

        /// <summary>
        /// ヘッダーが宣言するピクセルデータ (ミップ全段 × キューブマップの全面) の合計バイト数。
        /// アーカイブ内のペイロード長 (= ブロックヘッダー +0x08 の size - 0xAC) と一致する。
        /// </summary>
        public long ComputePayloadSize()
        {
            int levels = MipMapCount > 0 ? MipMapCount : 1;
            long total = 0;
            int w = Width;
            int h = Height;
            int d = IsVolume && Depth > 0 ? Depth : 1;

            for (int i = 0; i < levels; i++)
            {
                total += ComputeLevelSize(w, h) * d;
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
                d = Math.Max(1, d / 2);
            }

            return total * FaceCount;
        }

        private long ComputeLevelSize(int w, int h)
        {
            int blockBytes = BlockBytes();
            if (blockBytes > 0)
            {
                long blocks = (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4);
                return blocks * blockBytes;
            }

            int d3dBytes = (PixelFormatFlags & DdpfFourCc) != 0 ? D3dFormatBytesPerPixel() : 0;
            if (d3dBytes > 0)
            {
                return (long)w * h * d3dBytes;
            }

            // 非圧縮フォーマット (製品版では dwRGBBitCount=32 の 1 種類のみ)
            int bits = RgbBitCount > 0 ? RgbBitCount : 32;
            return (long)w * h * bits / 8;
        }

        /// <summary>ブロック圧縮なら 1 ブロックあたりのバイト数、非圧縮なら 0。</summary>
        private int BlockBytes()
        {
            if (FourCc == FourCcDxt1 || FourCc == FourCcAti1 || FourCc == FourCcBc4U || FourCc == FourCcBc4S)
            {
                return 8;
            }
            if (FourCc == FourCcDxt2 || FourCc == FourCcDxt3 ||
                FourCc == FourCcDxt4 || FourCc == FourCcDxt5 ||
                FourCc == FourCcAti2 || FourCc == FourCcBc5U || FourCc == FourCcBc5S)
            {
                return 16;
            }
            return 0;
        }

        /// <summary>FourCC 欄に D3DFORMAT の数値が入る形式の 1 画素あたりバイト数 (不明なら 0)。</summary>
        private int D3dFormatBytesPerPixel()
        {
            switch (FourCc)
            {
                case 36:  return 8;   // D3DFMT_A16B16G16R16
                case 110: return 8;   // D3DFMT_Q16W16V16U16
                case 111: return 2;   // D3DFMT_R16F
                case 112: return 4;   // D3DFMT_G16R16F
                case 113: return 8;   // D3DFMT_A16B16G16R16F
                case 114: return 4;   // D3DFMT_R32F
                case 115: return 8;   // D3DFMT_G32R32F
                case 116: return 16;  // D3DFMT_A32B32G32R32F
                default:  return 0;
            }
        }

        private static int CountBits(uint v)
        {
            int n = 0;
            while (v != 0)
            {
                n += (int)(v & 1);
                v >>= 1;
            }
            return n;
        }

        private static uint ToFourCc(string s)
        {
            return (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));
        }
    }
}

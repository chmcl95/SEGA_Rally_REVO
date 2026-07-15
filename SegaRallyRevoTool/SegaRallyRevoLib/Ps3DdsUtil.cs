using System;

namespace SegaRallyRevoTool.SegaRallyRevoLib
{
    /// <summary>
    /// PS3 (VBF) の type4 コンテナ生スライスに埋め込まれた「dword 単位でバイト反転された
    /// BE DDS ヘッダー」を検出・変換するための共通ヘルパー。
    /// 詳細は CLAUDE.md の「PS3 (VBF) DDS 抽出・書き戻し仕様」を参照。
    /// </summary>
    public static class Ps3DdsUtil
    {
        /// <summary>DDS ヘッダーサイズ (128 bytes)。</summary>
        public const int DdsHeaderSize = 0x80;

        /// <summary>
        /// テクスチャ i のペイロード終端と次テクスチャの BE ヘッダーの間に必ず存在する
        /// 未解明の固定長領域。実測ではゼロ埋めされている (詳細不明・意味未解明)。
        /// </summary>
        public const int GapSize = 0x44;

        /// <summary>ペイロード開始位置のアラインメント単位 (4096 bytes)。</summary>
        public const int AlignSize = 0x1000;

        // dword 反転済みの DDS マジック (" SDD" というバイト列 = 20 53 44 44)
        private static readonly byte[] ReversedMagic = { 0x20, 0x53, 0x44, 0x44 };

        /// <summary>
        /// buffer[offset, offset+length) を 4 バイト単位でバイト反転する (in-place)。
        /// PC(LE) <-> PS3(BE) の変換、および DDS ヘッダーの BE<->LE 変換の両方に使う。
        /// </summary>
        public static void DwordReverseInPlace(byte[] buffer, int offset, int length)
        {
            for (int i = offset; i < offset + length; i += 4)
            {
                Array.Reverse(buffer, i, 4);
            }
        }

        /// <summary>source[offset, offset+length) を 4 バイト単位でバイト反転した新しい配列を返す。</summary>
        public static byte[] DwordReverse(byte[] source, int offset, int length)
        {
            byte[] result = new byte[length];
            Array.Copy(source, offset, result, 0, length);
            DwordReverseInPlace(result, 0, length);
            return result;
        }

        /// <summary>
        /// 生スライス内から dword 反転された BE DDS ヘッダーの開始オフセットを探す。
        /// 反転マジック(20 53 44 44) を検索し、反転後に "DDS " + dwSize==124 になることで
        /// 誤検出を防ぐ。見つからない場合は -1 を返す。
        /// </summary>
        public static int FindHeaderOffset(byte[] slice)
        {
            int searchFrom = 0;
            while (true)
            {
                int idx = IndexOf(slice, ReversedMagic, searchFrom);
                if (idx < 0)
                {
                    return -1;
                }

                if (idx + DdsHeaderSize <= slice.Length)
                {
                    byte[] candidate = DwordReverse(slice, idx, DdsHeaderSize);
                    uint dwSize = BitConverter.ToUInt32(candidate, 4);
                    bool magicOk = candidate[0] == (byte)'D' && candidate[1] == (byte)'D' &&
                                   candidate[2] == (byte)'S' && candidate[3] == (byte)' ';
                    if (magicOk && dwSize == 124)
                    {
                        return idx;
                    }
                }

                searchFrom = idx + 1;
            }
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start)
        {
            int last = haystack.Length - needle.Length;
            for (int i = start; i <= last; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return i;
                }
            }
            return -1;
        }
    }
}

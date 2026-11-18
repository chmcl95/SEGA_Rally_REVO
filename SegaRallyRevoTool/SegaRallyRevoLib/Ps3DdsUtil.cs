using System;
using System.Collections.Generic;

namespace SegaRallyRevoTool.SegaRallyRevoLib
{
    /// <summary>
    /// PS3 (VBF) のテクスチャデータ (dword 単位でバイト反転された BE DDS ヘッダー + ペイロード) を
    /// 探す・変換するための共通ヘルパー。配置規則は Document/SBF_format.md §6.4.2。
    /// </summary>
    public static class Ps3DdsUtil
    {
        /// <summary>DDS ヘッダーサイズ (128 bytes)。</summary>
        public const int DdsHeaderSize = 0x80;

        /// <summary>
        /// ブロックヘッダー +0x08 (size) とペイロード長の差分。
        ///     size = ミップ全段の合計バイト数 + 0xAC
        /// </summary>
        public const int SizeFieldOverhead = 0xAC;

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
        /// VBF の mainEnd 以降の物理バイト列 region から、各テクスチャの DDS ヘッダー位置
        /// (ファイル先頭基準) を求める。まず §6.4.2 の規則どおりの位置にあるかを調べ
        /// (製品版はすべてこれに当たる)、違えば dword 反転 DDS マジックを出現順に探す
        /// (旧ツールで再パックした壊れたファイルなど。searched = true)。
        /// </summary>
        /// <exception cref="SbfFormatException">テクスチャを見つけられない、または size と合わない場合</exception>
        public static long[] LocateVbfTextures(byte[] region, long mainEnd, long[] payloadLens, out bool searched)
        {
            int n = payloadLens.Length;
            long[] positions = new long[n];
            searched = false;
            bool ruleOk = true;
            long h = SbfLayout.VbfFirstHeaderPosition(mainEnd);
            for (int k = 0; k < n && ruleOk; k++)
            {
                positions[k] = h;
                ruleOk = IsVbfTextureAt(region, h - mainEnd, payloadLens[k]);
                h = SbfLayout.VbfNextHeaderPosition(h, payloadLens[k]);
            }
            if (ruleOk)
            {
                return positions;
            }

            searched = true;
            List<int> found = FindAllHeaderOffsets(region);
            if (found.Count != n)
            {
                throw new SbfFormatException($"found {found.Count} DDS header(s) in the texture data but the archive has {n} texture(s).");
            }
            for (int k = 0; k < n; k++)
            {
                positions[k] = mainEnd + found[k];
                if (!IsVbfTextureAt(region, found[k], payloadLens[k]) ||
                    (k + 1 < n && found[k] + DdsHeaderSize + payloadLens[k] > found[k + 1]))
                {
                    throw new SbfFormatException($"block {k:D8}: the texture data at 0x{positions[k]:X} does not fit its size " +
                                                 $"(payload 0x{payloadLens[k]:X} bytes).");
                }
            }
            return positions;
        }

        private static bool IsVbfTextureAt(byte[] region, long pos, long payloadLength)
        {
            if (pos < 0 || pos + DdsHeaderSize + payloadLength > region.Length)
            {
                return false;
            }
            return region[pos] == 0x20 && region[pos + 1] == 0x53 && region[pos + 2] == 0x44 && region[pos + 3] == 0x44
                   && SbfLayout.ReadU32(region, pos + 4, true) == 124;
        }

        /// <summary>
        /// バッファ内にある dword 反転された BE DDS ヘッダーの開始オフセットを
        /// すべて出現順に返す。反転マジック(20 53 44 44) を検索し、反転後に
        /// "DDS " + dwSize==124 になることで誤検出を防ぐ。
        /// </summary>
        public static List<int> FindAllHeaderOffsets(byte[] buffer)
        {
            List<int> result = new List<int>();
            int searchFrom = 0;

            while (true)
            {
                int idx = IndexOf(buffer, ReversedMagic, searchFrom);
                if (idx < 0)
                {
                    return result;
                }

                if (idx + DdsHeaderSize <= buffer.Length)
                {
                    byte[] candidate = DwordReverse(buffer, idx, DdsHeaderSize);
                    uint dwSize = BitConverter.ToUInt32(candidate, 4);
                    bool magicOk = candidate[0] == (byte)'D' && candidate[1] == (byte)'D' &&
                                   candidate[2] == (byte)'S' && candidate[3] == (byte)' ';
                    if (magicOk && dwSize == 124)
                    {
                        result.Add(idx);
                        // ヘッダー内部を再検索しても意味が無いので次のヘッダー以降へ進む
                        searchFrom = idx + DdsHeaderSize;
                        continue;
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

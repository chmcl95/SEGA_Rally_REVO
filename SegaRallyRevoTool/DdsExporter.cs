using SegaRallyRevoTool.SegaRallyRevoLib;
using System;
using System.Collections.Generic;
using System.IO;

namespace SegaRallyRevoTool
{
    /// <summary>
    /// unpack --ps3 で得られた "4/{i}.BIN" 生スライスから、通常のツールで開ける
    /// 標準 DDS ファイル (dds/{i}.DDS) を抽出する。
    ///
    /// PS3 (VBF) の物理レイアウトでは、テクスチャ i の「本当のペイロード全長」
    /// (PC 版 DDS のペイロードと同一のもの) は、コンテナ i 自身の生スライスの
    /// 末尾までで収まりきらず、コンテナ i+1 の生スライスの先頭側 (次ヘッダーの
    /// 手前にある「未知領域」) にまで物理的にはみ出して格納されている。
    /// このはみ出し量は ExtraHeader+0x00 の論理オフセット差分から
    /// (payloadFullLen = extra[i+1] - extra[i] - 0x80) として厳密に計算できるため、
    /// 本クラスはコンテナ i と i+1 の両方の生スライスを跨いでペイロードを
    /// 再構成する。最終テクスチャだけは「次」が無いため、スライス内に
    /// 物理的に存在する分だけを取り出す (PC 版よりわずかに短く切り詰められている
    /// ことがある)。
    /// </summary>
    class DdsExporter
    {
        private string _inputPath;

        public DdsExporter(string inputPath)
        {
            _inputPath = inputPath;
        }

        public void Export()
        {
            Console.WriteLine("Starting to export DDS...");

            List<int> indices = GetType4Indices(_inputPath);
            if (indices.Count == 0)
            {
                Console.WriteLine("No type4 containers found. Nothing to export.");
                return;
            }

            string type4Dir = Path.Combine(_inputPath, "4");
            string metaDir = Path.Combine(_inputPath, "_meta");
            string ddsDir = Path.Combine(_inputPath, "dds");
            Directory.CreateDirectory(ddsDir);

            // 各コンテナの ExtraHeader+0x00 (論理オフセット) と生スライスを読み込む
            Dictionary<int, uint> extras = new Dictionary<int, uint>();
            Dictionary<int, byte[]> slices = new Dictionary<int, byte[]>();
            foreach (int idx in indices)
            {
                string extraPath = Path.Combine(metaDir, $"{idx:D8}.EXTRA");
                byte[] extraBytes = File.ReadAllBytes(extraPath);
                extras[idx] = BitConverter.ToUInt32(extraBytes, 0x00);
                slices[idx] = File.ReadAllBytes(FindDataFile(type4Dir, idx));
            }

            int exportedCount = 0;
            for (int k = 0; k < indices.Count; k++)
            {
                int idx = indices[k];
                byte[] slice = slices[idx];

                int headerOffset = Ps3DdsUtil.FindHeaderOffset(slice);
                if (headerOffset < 0)
                {
                    Console.WriteLine($"  [{idx:D8}] BE DDS header not found in raw slice. Skipped.");
                    continue;
                }

                byte[] leHeader = Ps3DdsUtil.DwordReverse(slice, headerOffset, Ps3DdsUtil.DdsHeaderSize);
                int ownPayloadStart = headerOffset + Ps3DdsUtil.DdsHeaderSize;
                int ownPayloadLen = slice.Length - ownPayloadStart;
                if (ownPayloadLen < 0)
                {
                    ownPayloadLen = 0;
                }

                byte[] payload;
                if (k < indices.Count - 1)
                {
                    int nextIdx = indices[k + 1];
                    long payloadFullLen = (long)extras[nextIdx] - extras[idx] - Ps3DdsUtil.DdsHeaderSize;
                    long needed = payloadFullLen - ownPayloadLen;

                    if (needed > 0)
                    {
                        byte[] nextSlice = slices[nextIdx];
                        // 安全側ガード: 次スライスより多くを要求されたら取れる分だけにする
                        needed = Math.Min(needed, nextSlice.Length);
                        payload = new byte[ownPayloadLen + needed];
                        Array.Copy(slice, ownPayloadStart, payload, 0, ownPayloadLen);
                        Array.Copy(nextSlice, 0, payload, ownPayloadLen, needed);
                    }
                    else
                    {
                        long truncatedLen = Math.Max(0, payloadFullLen);
                        truncatedLen = Math.Min(truncatedLen, ownPayloadLen);
                        payload = new byte[truncatedLen];
                        Array.Copy(slice, ownPayloadStart, payload, 0, (int)truncatedLen);
                    }
                }
                else
                {
                    // 最終テクスチャ: 次コンテナが無いのでスライス内にある分だけを取り出す。
                    // PC 版ペイロードよりわずかに短く切り詰められている場合がある。
                    payload = new byte[ownPayloadLen];
                    Array.Copy(slice, ownPayloadStart, payload, 0, ownPayloadLen);
                }

                string ddsPath = Path.Combine(ddsDir, $"{idx:D8}.DDS");
                using (FileStream fs = new FileStream(ddsPath, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(leHeader, 0, leHeader.Length);
                    fs.Write(payload, 0, payload.Length);
                }
                exportedCount++;
                Console.WriteLine($"  [{idx:D8}] -> dds/{idx:D8}.DDS ({leHeader.Length + payload.Length} bytes)");
            }

            Console.WriteLine($"Done. Exported {exportedCount}/{indices.Count} DDS file(s).");
        }

        /// <summary>
        /// unpacked_dir 内で "4/" にデータファイルを持つ (= type4 の) コンテナ番号一覧を
        /// 昇順で返す (アンパック時のファイル内出現順と一致する)。
        /// </summary>
        internal static List<int> GetType4Indices(string unpackedDir)
        {
            List<int> result = new List<int>();

            string type4Dir = Path.Combine(unpackedDir, "4");
            string metaDir = Path.Combine(unpackedDir, "_meta");
            if (!Directory.Exists(type4Dir) || !Directory.Exists(metaDir))
            {
                return result;
            }

            foreach (string extraFile in Directory.GetFiles(metaDir, "*.EXTRA"))
            {
                int idx = int.Parse(Path.GetFileNameWithoutExtension(extraFile));
                if (FindDataFile(type4Dir, idx, throwIfMissing: false) != null)
                {
                    result.Add(idx);
                }
            }
            result.Sort();
            return result;
        }

        internal static string FindDataFile(string type4Dir, int idx, bool throwIfMissing = true)
        {
            string[] matches = Directory.GetFiles(type4Dir, $"{idx:D8}.*");
            if (matches.Length == 0)
            {
                if (throwIfMissing)
                {
                    throw new FileNotFoundException($"Data file for container {idx:D8} not found in {type4Dir}");
                }
                return null;
            }
            return matches[0];
        }
    }
}

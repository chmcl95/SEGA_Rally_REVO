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
    /// PS3 (VBF) のデータ領域の物理レイアウトは次のとおり (サンプル全 37 アーカイブ /
    /// 183 テクスチャで検証済み):
    ///
    ///   [先頭パディング 0x1018]
    ///   [BE DDS ヘッダー 0x80]   ← 直後のペイロード開始位置が 0x1000 アラインになる位置
    ///   [ペイロード]              ← 長さ = ContainerHeader +0x08 (size) - 0xAC
    ///   [0x44 バイトの gap]       ← 意味未解明 (実測では常にゼロ)
    ///   [アラインメント埋め]      ← 実測では常にゼロ
    ///   [BE DDS ヘッダー 0x80] ...
    ///   最終テクスチャはペイロード直後が EOF (gap もパディングも無い)
    ///
    /// 一方 ExtraHeader +0x00 が示す「論理オフセット」は、この物理レイアウトから
    /// テクスチャあたり 0x44 バイトずつ差し引いた別の区切り方であり、Unpacker は
    /// その論理境界で生スライスを分割している。したがって
    /// **「論理スライス k がテクスチャ k のヘッダーを含む」とは限らない**
    /// (小さいテクスチャが含まれると 1 つずつずれる)。
    ///
    /// そのため本クラスは生スライスを index 順に連結してデータ領域の物理バイト列を
    /// 復元し、その中の BE DDS ヘッダーを出現順に type4 コンテナへ対応付ける。
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

            // 生スライスを index 順に連結すると、データ領域 (絶対オフセット
            // ExtraHeader[0]+0x00 以降) の物理バイト列そのものになる。
            byte[] data = ConcatSlices(type4Dir, indices);

            List<int> headerOffsets = Ps3DdsUtil.FindAllHeaderOffsets(data);
            if (headerOffsets.Count != indices.Count)
            {
                Console.WriteLine($"  BE DDS header count ({headerOffsets.Count}) does not match " +
                                  $"type4 container count ({indices.Count}). Aborting export " +
                                  "(exporting with a wrong texture mapping would corrupt the archive).");
                return;
            }

            Directory.CreateDirectory(ddsDir);

            for (int k = 0; k < indices.Count; k++)
            {
                int idx = indices[k];
                long payloadLen = ReadContainerSize(metaDir, idx) - Ps3DdsUtil.SizeFieldOverhead;
                int payloadStart = headerOffsets[k] + Ps3DdsUtil.DdsHeaderSize;

                if (payloadLen < 0 || payloadStart + payloadLen > data.Length)
                {
                    Console.WriteLine($"  [{idx:D8}] Declared payload size (0x{payloadLen:X}) does not fit " +
                                      "in the archive data. Aborting export.");
                    return;
                }

                byte[] leHeader = Ps3DdsUtil.DwordReverse(data, headerOffsets[k], Ps3DdsUtil.DdsHeaderSize);

                // ヘッダーから算出した長さと size フィールドの食い違いは想定外なので警告する
                DdsHeader info = DdsHeader.Parse(leHeader);
                if (info != null && info.ComputePayloadSize() != payloadLen)
                {
                    Console.WriteLine($"  [{idx:D8}] Warning: size field implies 0x{payloadLen:X} bytes " +
                                      $"but the DDS header implies 0x{info.ComputePayloadSize():X}. " +
                                      "Using the size field.");
                }

                string ddsPath = Path.Combine(ddsDir, $"{idx:D8}.DDS");
                using (FileStream fs = new FileStream(ddsPath, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(leHeader, 0, leHeader.Length);
                    fs.Write(data, payloadStart, (int)payloadLen);
                }

                Console.WriteLine($"  [{idx:D8}] -> dds/{idx:D8}.DDS " +
                                  $"({Ps3DdsUtil.DdsHeaderSize + payloadLen} bytes)");
            }

            Console.WriteLine($"Done. Exported {indices.Count}/{indices.Count} DDS file(s).");
        }

        /// <summary>
        /// type4 コンテナの生スライスを index 昇順 (= ファイル内のデータ並び順) に連結する。
        /// </summary>
        internal static byte[] ConcatSlices(string type4Dir, List<int> indices)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                foreach (int idx in indices)
                {
                    byte[] slice = File.ReadAllBytes(FindDataFile(type4Dir, idx));
                    ms.Write(slice, 0, slice.Length);
                }
                return ms.ToArray();
            }
        }

        /// <summary>_meta\{idx}.HEAD の +0x08 (ContainerHeader の size。常に LE 保存)。</summary>
        internal static long ReadContainerSize(string metaDir, int idx)
        {
            byte[] head = File.ReadAllBytes(Path.Combine(metaDir, $"{idx:D8}.HEAD"));
            return BitConverter.ToUInt32(head, 0x08);
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

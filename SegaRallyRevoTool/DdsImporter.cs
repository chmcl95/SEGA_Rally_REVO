using SegaRallyRevoTool.SegaRallyRevoLib;
using System;
using System.Collections.Generic;
using System.IO;

namespace SegaRallyRevoTool
{
    /// <summary>
    /// dds/{i}.DDS (exportdds で出力したもの、またはユーザーが差し替えた別サイズの
    /// 有効な DDS) を "4/{i}.BIN" 生スライスへ書き戻す。
    ///
    /// 物理レイアウトの詳細は DdsExporter のコメントを参照。手順は次のとおり:
    ///
    /// 1. 書き換え前の生スライスを index 順に連結してデータ領域の物理バイト列を復元し、
    ///    BE DDS ヘッダーを出現順に type4 コンテナへ対応付ける。そこから
    ///      - 先頭パディング (先頭ヘッダーの手前。他のテクスチャに依存しない固定領域)
    ///      - 各テクスチャ末尾の 0x44 gap (意味未解明のため不透明バイトとして再利用)
    ///    を退避する。
    /// 2. dds/*.DDS を読み込み、DDS ヘッダーからペイロード長を算出する
    ///    (= ミップ全段の合計バイト数)。
    /// 3. 物理バイト列を
    ///      先頭パディング + Σ(BEヘッダー + ペイロード + gap + アラインメント埋め)
    ///    として再構築する。ペイロード開始位置が絶対オフセットで 0x1000 アラインに
    ///    なるようにアラインメント埋めを計算する (最終テクスチャはペイロード直後が EOF)。
    /// 4. 論理オフセットを
    ///      newExtra[0]   = oldExtra[0] (据え置き)
    ///      newExtra[k+1] = newExtra[k] + 0x80 + ペイロード長 + アラインメント埋め長
    ///    で再計算し (物理間隔から gap の 0x44 を差し引いた値)、その境界で
    ///    物理バイト列を再スライスして各コンテナの生スライスとする。
    /// 5. あわせて、テクスチャを差し替えたときに追随が必要な次の値を更新する:
    ///      - ContainerHeader +0x08 (size)      = ペイロード長 + 0xAC
    ///      - ExtraHeader     +0x04 / +0x08     = 幅 / 高さ
    ///      - ExtraHeader     +0x00             = 上記の論理オフセット
    ///
    /// dds/*.DDS が何も変更されていなければ、この手順は元の生スライス・
    /// ContainerHeader・ExtraHeader をバイト単位で再現する。
    ///
    /// 書き出しは全コンテナぶんの内容をメモリ上に組み立ててから一括で行うため、
    /// 途中でエラーになっても作業ディレクトリが中途半端な状態にはならない。
    /// </summary>
    class DdsImporter
    {
        private string _inputPath;

        public DdsImporter(string inputPath)
        {
            _inputPath = inputPath;
        }

        /// <returns>成功 (またはインポート対象なし) なら true。検証エラー時は false。</returns>
        public bool Import()
        {
            Console.WriteLine("Starting to import DDS...");

            List<int> indices = DdsExporter.GetType4Indices(_inputPath);
            if (indices.Count == 0)
            {
                Console.WriteLine("No type4 containers found. Nothing to import.");
                return true;
            }

            string type4Dir = Path.Combine(_inputPath, "4");
            string metaDir = Path.Combine(_inputPath, "_meta");
            string ddsDir = Path.Combine(_inputPath, "dds");

            if (!Directory.Exists(ddsDir))
            {
                Console.WriteLine($"'{ddsDir}' does not exist. Run 'exportdds' first.");
                return false;
            }

            int n = indices.Count;

            // ---- 1. 書き換え前の物理レイアウトを解析する ----
            byte[] oldData = DdsExporter.ConcatSlices(type4Dir, indices);
            List<int> oldHeaderOffsets = Ps3DdsUtil.FindAllHeaderOffsets(oldData);
            if (oldHeaderOffsets.Count != n)
            {
                Console.WriteLine($"  BE DDS header count ({oldHeaderOffsets.Count}) does not match " +
                                  $"type4 container count ({n}). Cannot import.");
                return false;
            }

            byte[][] oldExtraFiles = new byte[n][];   // .EXTRA 全体 (0x28 bytes)
            byte[][] oldHeadFiles = new byte[n][];    // .HEAD  全体 (0x1C bytes)
            long[] oldPayloadLens = new long[n];
            for (int k = 0; k < n; k++)
            {
                int idx = indices[k];
                oldExtraFiles[k] = File.ReadAllBytes(Path.Combine(metaDir, $"{idx:D8}.EXTRA"));
                oldHeadFiles[k] = File.ReadAllBytes(Path.Combine(metaDir, $"{idx:D8}.HEAD"));
                oldPayloadLens[k] = BitConverter.ToUInt32(oldHeadFiles[k], 0x08) - Ps3DdsUtil.SizeFieldOverhead;
            }

            long baseOffset = BitConverter.ToUInt32(oldExtraFiles[0], 0x00);
            int leadingPadLen = oldHeaderOffsets[0];

            // 境界ごとの 0x44 gap を退避する (不透明領域としてそのまま再利用する)
            byte[][] gapBytes = new byte[Math.Max(0, n - 1)][];
            for (int k = 0; k < n - 1; k++)
            {
                long gapStart = oldHeaderOffsets[k] + Ps3DdsUtil.DdsHeaderSize + oldPayloadLens[k];
                byte[] gap = new byte[Ps3DdsUtil.GapSize];
                if (gapStart < 0 || gapStart + Ps3DdsUtil.GapSize > oldData.Length)
                {
                    Console.WriteLine($"  [{indices[k]:D8}] Original layout is inconsistent " +
                                      $"(gap at 0x{gapStart:X} is out of range). Cannot import.");
                    return false;
                }
                Array.Copy(oldData, gapStart, gap, 0, Ps3DdsUtil.GapSize);
                gapBytes[k] = gap;
            }

            // ---- 2. dds/*.DDS (現在の内容) を読み込む ----
            byte[][] newHeadersLe = new byte[n][];
            byte[][] newPayloads = new byte[n][];
            DdsHeader[] newInfos = new DdsHeader[n];
            for (int k = 0; k < n; k++)
            {
                int idx = indices[k];
                string ddsPath = Path.Combine(ddsDir, $"{idx:D8}.DDS");
                if (!File.Exists(ddsPath))
                {
                    Console.WriteLine($"  [{idx:D8}] '{ddsPath}' not found.");
                    return false;
                }

                byte[] ddsBytes = File.ReadAllBytes(ddsPath);
                byte[] leHeader = new byte[Ps3DdsUtil.DdsHeaderSize];
                if (ddsBytes.Length >= Ps3DdsUtil.DdsHeaderSize)
                {
                    Array.Copy(ddsBytes, leHeader, Ps3DdsUtil.DdsHeaderSize);
                }

                DdsHeader info = ddsBytes.Length >= Ps3DdsUtil.DdsHeaderSize ? DdsHeader.Parse(leHeader) : null;
                if (info == null)
                {
                    Console.WriteLine($"  [{idx:D8}] '{ddsPath}' is not a valid DDS file.");
                    return false;
                }

                long payloadLen = info.ComputePayloadSize();
                long available = ddsBytes.Length - Ps3DdsUtil.DdsHeaderSize;
                if (available < payloadLen)
                {
                    Console.WriteLine($"  [{idx:D8}] '{ddsPath}' is truncated: the header declares " +
                                      $"0x{payloadLen:X} bytes of pixel data but only 0x{available:X} are present.");
                    return false;
                }
                if (available > payloadLen)
                {
                    Console.WriteLine($"  [{idx:D8}] Note: ignoring 0x{available - payloadLen:X} trailing " +
                                      "byte(s) beyond the mip chain declared by the DDS header.");
                }

                newHeadersLe[k] = leHeader;
                newPayloads[k] = new byte[payloadLen];
                Array.Copy(ddsBytes, Ps3DdsUtil.DdsHeaderSize, newPayloads[k], 0, (int)payloadLen);
                newInfos[k] = info;
            }

            // ---- 3. 物理バイト列を再構築し、あわせて論理オフセットを求める ----
            long[] newExtras = new long[n];
            newExtras[0] = baseOffset;
            byte[] newData;

            using (MemoryStream mega = new MemoryStream())
            {
                mega.Write(oldData, 0, leadingPadLen);

                for (int k = 0; k < n; k++)
                {
                    long headerAbs = baseOffset + mega.Position;
                    if ((headerAbs + Ps3DdsUtil.DdsHeaderSize) % Ps3DdsUtil.AlignSize != 0)
                    {
                        Console.WriteLine($"  [{indices[k]:D8}] Alignment invariant violated " +
                                          $"(header would land at 0x{headerAbs:X}). Aborting to avoid " +
                                          "producing a corrupt archive.");
                        return false;
                    }

                    byte[] beHeader = Ps3DdsUtil.DwordReverse(newHeadersLe[k], 0, Ps3DdsUtil.DdsHeaderSize);
                    mega.Write(beHeader, 0, beHeader.Length);
                    mega.Write(newPayloads[k], 0, newPayloads[k].Length);

                    if (k < n - 1)
                    {
                        mega.Write(gapBytes[k], 0, gapBytes[k].Length);

                        // 次のペイロード開始位置が 0x1000 アラインになるまでゼロで埋める
                        long afterGap = baseOffset + mega.Position + Ps3DdsUtil.DdsHeaderSize;
                        long alignPad = (Ps3DdsUtil.AlignSize - (afterGap % Ps3DdsUtil.AlignSize))
                                        % Ps3DdsUtil.AlignSize;
                        if (alignPad > 0)
                        {
                            mega.Write(new byte[alignPad], 0, (int)alignPad);
                        }

                        // 論理オフセットの差分は物理間隔から gap (0x44) を除いたもの
                        newExtras[k + 1] = newExtras[k] + Ps3DdsUtil.DdsHeaderSize
                                           + newPayloads[k].Length + alignPad;
                    }
                    // 最終テクスチャはペイロード直後が EOF (gap もアラインメント埋めも無い)
                }

                newData = mega.ToArray();
            }

            // ---- 4. 論理境界で再スライスし、更新後のメタデータを組み立てる ----
            byte[][] newSlices = new byte[n][];
            for (int k = 0; k < n; k++)
            {
                long relStart = newExtras[k] - baseOffset;
                long relEnd = (k < n - 1) ? (newExtras[k + 1] - baseOffset) : newData.Length;

                if (relStart < 0 || relEnd < relStart || relEnd > newData.Length)
                {
                    Console.WriteLine($"  [{indices[k]:D8}] Computed slice bounds are invalid " +
                                      $"(start=0x{relStart:X}, end=0x{relEnd:X}, total=0x{newData.Length:X}). " +
                                      "This can happen if a replacement DDS is too small relative to " +
                                      "the archive's padding overhead.");
                    return false;
                }

                newSlices[k] = new byte[relEnd - relStart];
                Array.Copy(newData, relStart, newSlices[k], 0, newSlices[k].Length);

                // ExtraHeader: +0x00 論理オフセット / +0x04 幅 / +0x08 高さ (すべて LE 保存)
                byte[] extraOut = (byte[])oldExtraFiles[k].Clone();
                Array.Copy(BitConverter.GetBytes((uint)newExtras[k]), 0, extraOut, 0x00, 4);
                Array.Copy(BitConverter.GetBytes((uint)newInfos[k].Width), 0, extraOut, 0x04, 4);
                Array.Copy(BitConverter.GetBytes((uint)newInfos[k].Height), 0, extraOut, 0x08, 4);
                oldExtraFiles[k] = extraOut;

                // ContainerHeader: +0x08 size = ペイロード長 + 0xAC
                byte[] headOut = (byte[])oldHeadFiles[k].Clone();
                uint newSize = (uint)(newPayloads[k].Length + Ps3DdsUtil.SizeFieldOverhead);
                Array.Copy(BitConverter.GetBytes(newSize), 0, headOut, 0x08, 4);
                oldHeadFiles[k] = headOut;
            }

            // ---- 5. ここまでで検証が済んでいるので、まとめて書き出す ----
            for (int k = 0; k < n; k++)
            {
                int idx = indices[k];
                File.WriteAllBytes(DdsExporter.FindDataFile(type4Dir, idx), newSlices[k]);
                File.WriteAllBytes(Path.Combine(metaDir, $"{idx:D8}.EXTRA"), oldExtraFiles[k]);
                File.WriteAllBytes(Path.Combine(metaDir, $"{idx:D8}.HEAD"), oldHeadFiles[k]);

                Console.WriteLine($"  [{idx:D8}] <- dds/{idx:D8}.DDS " +
                                  $"(slice {newSlices[k].Length} bytes, extra=0x{newExtras[k]:X}, " +
                                  $"size=0x{newPayloads[k].Length + Ps3DdsUtil.SizeFieldOverhead:X}, " +
                                  $"{newInfos[k].Width}x{newInfos[k].Height})");
            }

            Console.WriteLine($"Done. Imported {n} DDS file(s).");
            return true;
        }
    }
}

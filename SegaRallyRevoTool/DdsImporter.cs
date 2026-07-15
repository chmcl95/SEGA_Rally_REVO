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
    /// 設計方針:
    /// 1. 書き換え前の "4/*.BIN" から次を読み取る (常にこの実行時点のファイルを
    ///    「元レイアウト」とみなす。基点は固定値 0x1F68 決め打ちにはせず、
    ///    アンパック時に保存された ExtraHeader+0x00 の値をそのまま使う=安全側)
    ///      - leading_pad_0: 先頭コンテナの BE ヘッダー手前にあるパディング。
    ///        これは他のどのテクスチャサイズにも依存しない固定領域なので、
    ///        常にそのまま再利用する。
    ///      - 各コンテナ境界の 0x44 バイト (gap)。意味は未解明だが不透明領域として
    ///        そのまま再利用する。
    /// 2. dds/*.DDS の現在の内容 (ヘッダー+ペイロード) を「新しいテクスチャデータ」
    ///    として読み込む。
    /// 3. 新しい ExtraHeader+0x00 (論理オフセット) を
    ///        newExtra[0] = oldExtra[0] (据え置き)
    ///        newExtra[k+1] = newExtra[k] + 0x80 + payloadFullLen[k]
    ///    の累積式で再計算する。
    /// 4. 物理バイト列を
    ///        leading_pad_0 + Σ(header_k(BE) + payload_k + gap_k(0x44,再利用) + align_pad_k(新規計算))
    ///    として構築し (最終コンテナだけは payload の直後がそのままファイル終端になる。
    ///    実測では EOF は必ずしも 0x1000 アラインではない ── loading_alpine1 はたまたま
    ///    揃っていただけで livery19/36 では揃っていないため、EOF 用の追加パディングは
    ///    行わない)、newExtra による論理境界
    ///    [newExtra[k]-newExtra[0], newExtra[k+1]-newExtra[0]) でスライスして
    ///    各コンテナの新しい "4/{i}.BIN" とする。
    ///
    /// dds/*.DDS が何も変更されていなければ、この手順は元の生スライスと
    /// バイト一致する結果を再生成する (ExtraHeader+0x00 の据え置き基点、
    /// leading_pad_0 の再利用、gap の再利用、および align_pad が実測でも常に 0
    /// だったことによる)。
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

            // ---- 1. 書き換え前の生スライスを解析する ----
            byte[][] oldSlices = new byte[n][];
            byte[][] oldExtraFiles = new byte[n][]; // .EXTRA ファイル全体 (0x28 bytes) を保持
            uint[] oldExtras = new uint[n];
            int[] oldHeaderOffsets = new int[n];

            for (int k = 0; k < n; k++)
            {
                int idx = indices[k];
                string dataFile = DdsExporter.FindDataFile(type4Dir, idx);
                oldSlices[k] = File.ReadAllBytes(dataFile);

                string extraPath = Path.Combine(metaDir, $"{idx:D8}.EXTRA");
                oldExtraFiles[k] = File.ReadAllBytes(extraPath);
                oldExtras[k] = BitConverter.ToUInt32(oldExtraFiles[k], 0x00);

                int headerOffset = Ps3DdsUtil.FindHeaderOffset(oldSlices[k]);
                if (headerOffset < 0)
                {
                    Console.WriteLine($"  [{idx:D8}] Original BE DDS header not found. Cannot import.");
                    return false;
                }
                oldHeaderOffsets[k] = headerOffset;
            }

            // 境界ごとの 0x44 gap の中身を退避する (不透明領域として再利用する)
            byte[][] gapBytes = new byte[Math.Max(0, n - 1)][];
            for (int k = 0; k < n - 1; k++)
            {
                long payloadFullLenOld = (long)oldExtras[k + 1] - oldExtras[k] - Ps3DdsUtil.DdsHeaderSize;
                long ownPartLenOld = oldSlices[k].Length - oldHeaderOffsets[k] - Ps3DdsUtil.DdsHeaderSize;
                long tailNeededOld = payloadFullLenOld - ownPartLenOld;
                if (tailNeededOld < 0)
                {
                    tailNeededOld = 0;
                }

                byte[] nextSlice = oldSlices[k + 1];
                byte[] gap = new byte[Ps3DdsUtil.GapSize];
                long available = nextSlice.Length - tailNeededOld;
                long copyLen = Math.Min(Ps3DdsUtil.GapSize, Math.Max(0, available));
                if (copyLen > 0)
                {
                    Array.Copy(nextSlice, tailNeededOld, gap, 0, copyLen);
                }
                gapBytes[k] = gap;
            }

            int leadingPad0 = oldHeaderOffsets[0];

            // ---- 2. dds/*.DDS (現在の内容) を読み込む ----
            byte[][] newHeadersLe = new byte[n][];
            byte[][] newPayloads = new byte[n][];
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
                if (ddsBytes.Length < Ps3DdsUtil.DdsHeaderSize ||
                    !(ddsBytes[0] == (byte)'D' && ddsBytes[1] == (byte)'D' &&
                      ddsBytes[2] == (byte)'S' && ddsBytes[3] == (byte)' '))
                {
                    Console.WriteLine($"  [{idx:D8}] '{ddsPath}' is not a valid DDS file (bad magic).");
                    return false;
                }

                newHeadersLe[k] = new byte[Ps3DdsUtil.DdsHeaderSize];
                Array.Copy(ddsBytes, newHeadersLe[k], Ps3DdsUtil.DdsHeaderSize);

                int payloadLen = ddsBytes.Length - Ps3DdsUtil.DdsHeaderSize;
                newPayloads[k] = new byte[payloadLen];
                Array.Copy(ddsBytes, Ps3DdsUtil.DdsHeaderSize, newPayloads[k], 0, payloadLen);
            }

            // ---- 3. 新しい ExtraHeader+0x00 (論理オフセット) を再計算する ----
            long[] newExtras = new long[n];
            newExtras[0] = oldExtras[0];
            for (int k = 0; k < n - 1; k++)
            {
                newExtras[k + 1] = newExtras[k] + Ps3DdsUtil.DdsHeaderSize + newPayloads[k].Length;
            }

            // ---- 4. 物理バイト列 (leading_pad_0 起点の相対ストリーム) を構築する ----
            // アラインメントは元ファイル中の「絶対」ファイルオフセットが 0x1000 境界に
            // 揃うことが条件であり、oldExtras[0] (= newExtras[0]) はその絶対オフセットと
            // 一致することが分かっている (Unpacker がこの値をそのままシーク位置として
            // 使っている)。よって相対ストリーム内の位置を絶対位置に変換するための
            // 位相 (phase = oldExtras[0] % 0x1000) を全ての剰余演算に加える。
            long phase = oldExtras[0] % Ps3DdsUtil.AlignSize;
            using (MemoryStream mega = new MemoryStream())
            {
                mega.Write(oldSlices[0], 0, leadingPad0);

                for (int k = 0; k < n; k++)
                {
                    long headerPos = mega.Position;
                    if ((phase + headerPos + Ps3DdsUtil.DdsHeaderSize) % Ps3DdsUtil.AlignSize != 0)
                    {
                        throw new InvalidOperationException(
                            $"Alignment invariant violated at container {indices[k]:D8} " +
                            $"(header rel. pos=0x{headerPos:X}). Aborting to avoid producing a corrupt archive.");
                    }

                    byte[] beHeader = Ps3DdsUtil.DwordReverse(newHeadersLe[k], 0, Ps3DdsUtil.DdsHeaderSize);
                    mega.Write(beHeader, 0, beHeader.Length);
                    mega.Write(newPayloads[k], 0, newPayloads[k].Length);

                    if (k < n - 1)
                    {
                        mega.Write(gapBytes[k], 0, gapBytes[k].Length);

                        long posAfterGap = mega.Position;
                        long remainder = (phase + posAfterGap + Ps3DdsUtil.DdsHeaderSize) % Ps3DdsUtil.AlignSize;
                        long alignPad = (Ps3DdsUtil.AlignSize - remainder) % Ps3DdsUtil.AlignSize;
                        if (alignPad > 0)
                        {
                            mega.Write(new byte[alignPad], 0, (int)alignPad);
                        }
                    }
                    else
                    {
                        // 最終コンテナ: 実測では EOF が必ずしも 0x1000 アラインとは限らない
                        // (loading_alpine1 はたまたま揃っていただけで、livery19/36 では
                        // 揃っていない)。よって末尾に追加のパディングは行わず、
                        // ペイロードの直後をそのままファイル終端とする。
                    }
                }

                byte[] megaBytes = mega.ToArray();

                // ---- 5. 論理境界でスライスして各コンテナの新しい生スライスを書き出す ----
                for (int k = 0; k < n; k++)
                {
                    long relStart = newExtras[k] - newExtras[0];
                    long relEnd = (k < n - 1) ? (newExtras[k + 1] - newExtras[0]) : megaBytes.Length;

                    if (relStart < 0 || relEnd < relStart || relEnd > megaBytes.Length)
                    {
                        throw new InvalidOperationException(
                            $"Computed slice bounds for container {indices[k]:D8} are invalid " +
                            $"(start=0x{relStart:X}, end=0x{relEnd:X}, total=0x{megaBytes.Length:X}). " +
                            "This can happen if a replacement DDS is too small relative to the archive's padding overhead.");
                    }

                    int idx = indices[k];
                    int len = (int)(relEnd - relStart);
                    byte[] newSlice = new byte[len];
                    Array.Copy(megaBytes, relStart, newSlice, 0, len);

                    string dataFile = DdsExporter.FindDataFile(type4Dir, idx);
                    File.WriteAllBytes(dataFile, newSlice);

                    byte[] extraOut = (byte[])oldExtraFiles[k].Clone();
                    byte[] newExtraLe = BitConverter.GetBytes((uint)newExtras[k]);
                    Array.Copy(newExtraLe, 0, extraOut, 0, 4);
                    string extraPath = Path.Combine(metaDir, $"{idx:D8}.EXTRA");
                    File.WriteAllBytes(extraPath, extraOut);

                    Console.WriteLine($"  [{idx:D8}] <- dds/{idx:D8}.DDS (slice {len} bytes, extra=0x{newExtras[k]:X})");
                }
            }

            Console.WriteLine($"Done. Imported {n} DDS file(s).");
            return true;
        }
    }
}

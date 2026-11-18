using SegaRallyRevoTool.SegaRallyRevoLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SegaRallyRevoTool
{
    class Unpacker
    {
        private string _inputPath;
        private string _destPath;
        private bool _onlyDecompress;
        private bool _ps3Requested;
        private bool _isBigEndian;

        /// <summary>アンパックしたアーカイブのエンディアン (Unpack() 後に有効。自動判定の結果)。</summary>
        public bool IsBigEndian => _isBigEndian;

        /// <param name="ps3Requested">
        /// --ps3 が指定されたか。エンディアンはファイル内容から自動判定するので参考情報としてのみ使い、
        /// 判定結果と食い違えば警告を出す。
        /// </param>
        public Unpacker(string inputPath, string outputPath, bool onlyDecompress, bool ps3Requested)
        {
            _inputPath = inputPath;
            _destPath = outputPath;
            _onlyDecompress = onlyDecompress;
            _ps3Requested = ps3Requested;
        }

        /// <exception cref="SbfFormatException">入力が SBF/VBF/SBZ1 として解釈できない場合</exception>
        public void Unpack()
        {
            Console.WriteLine("Starting to unpack...");

            // SBZ1 (zlib 外殻) なら展開する。+0x04 のサイズはプラットフォームのエンディアンなので、
            // エンディアン判定後に照合する。
            byte[] fileBytes = File.ReadAllBytes(_inputPath);
            byte[] sbfBytes = fileBytes;
            bool compressed = fileBytes.Length >= 4 && BitConverter.ToUInt32(fileBytes, 0) == SBZ1.magic;
            uint sizeFieldLe = 0, sizeFieldBe = 0;
            if (compressed)
            {
                sbfBytes = SBZ1.DecompressAuto(fileBytes, out sizeFieldLe, out sizeFieldBe);
            }

            if (_onlyDecompress)
            {
                Directory.CreateDirectory(_destPath);
                string decompedSbfPath = $@"{_destPath}\{Path.GetFileNameWithoutExtension(_inputPath)}_decomp.sbf";
                File.WriteAllBytes(decompedSbfPath, sbfBytes);
                Console.WriteLine("Decompress Done.");
                return;
            }

            _isBigEndian = SbfLayout.DetectBigEndian(sbfBytes);
            Console.WriteLine($"Detected {(_isBigEndian ? "big-endian (PS3)" : "little-endian (PC)")} archive.");
            if (_ps3Requested && !_isBigEndian)
            {
                Console.WriteLine("Warning: --ps3 was given but the file is little-endian (PC). Ignoring --ps3.");
            }
            if (compressed)
            {
                uint expected = _isBigEndian ? sizeFieldBe : sizeFieldLe;
                if (expected != sbfBytes.Length)
                {
                    Console.WriteLine($"Warning: SBZ1 size field says 0x{expected:X} bytes but the zlib stream " +
                                      $"decompressed to 0x{sbfBytes.Length:X} bytes.");
                }
            }

            Directory.CreateDirectory(_destPath);
            using (MemoryStream sbfFileStream = new MemoryStream(sbfBytes, false))
            {
                byte[] bytes;
                string destDirectoryPath = $@"{_destPath}\{Path.GetFileNameWithoutExtension(_inputPath)}";
                Directory.CreateDirectory(destDirectoryPath);
                string metaDataPath = $@"{destDirectoryPath}\_meta";
                Directory.CreateDirectory(metaDataPath);
                EndianMarker.Write(metaDataPath, _isBigEndian);
                List<string> destRelativePaths = new List<string>();

                // Decompressed / RAW SBF file
                int length = (int)SbfLayout.ReadU32(sbfBytes, 0x14, _isBigEndian);
                sbfFileStream.Seek(0x18, SeekOrigin.Begin);

                List<Entry> entrys = new List<Entry>();
                for (int i = 0; i < length; i++)
                {
                    Entry entry = new Entry(_isBigEndian);
                    entry.Unpack(sbfFileStream);
                    entrys.Add(entry);
                }

                // PS3 (VBF) はブロックヘッダー + 記述子が全テクスチャ分連続していて、テクスチャデータは
                // その後ろにまとめて置かれる (Document/SBF_format.md §6.4.2)。ヘッダーループでは
                // type4 ブロックの (番号, size) を記録するだけにして、データは後でまとめて取り出す。
                List<(int index, long size)> ps3Textures = new List<(int, long)>();

                for (int i = 0; i < length; i++)
                {
                    long endAddress = sbfFileStream.Length;
                    if (i < length - 1)
                    {
                        endAddress = entrys[i + 1].offset;
                    }

                    Container container = new Container(_isBigEndian);
                    sbfFileStream.Seek(entrys[i].offset, SeekOrigin.Begin);

                    string headPath  = $@"{metaDataPath}\{i:D8}.HEAD";
                    string extraPath = $@"{metaDataPath}\{i:D8}.EXTRA";

                    using (FileStream headFileStream = new FileStream(headPath, FileMode.Create, FileAccess.Write))
                    using (MemoryStream extraMemStream = new MemoryStream())
                    {
                        container.Unpack(sbfFileStream, headFileStream, extraMemStream);

                        // type==4 のときだけ .EXTRA を出力
                        if (container.type == 4)
                        {
                            extraMemStream.Seek(0, SeekOrigin.Begin);
                            byte[] extraBytes = extraMemStream.ToArray();
                            using (FileStream extraFileStream = new FileStream(extraPath, FileMode.Create, FileAccess.Write))
                            {
                                extraFileStream.Write(extraBytes, 0, extraBytes.Length);
                            }

                            if (_isBigEndian)
                            {
                                // PS3 の type4 データはこのループでは抽出せず、後段でまとめて処理する
                                ps3Textures.Add((i, (uint)container.unk0x08));
                                continue;
                            }
                        }
                    }

                    long size = endAddress - sbfFileStream.Position;
                    if(size < 1)
                    {
                        continue;
                    }

                    bytes = new byte[size];
                    sbfFileStream.Read(bytes, 0x00, bytes.Length);
                    string extension = "BIN";
                    // detection DDS
                    if (bytes[0] == 0x44 && bytes[1] == 0x44 && bytes[2] == 0x53 && bytes[3] == 0x20)
                    {
                        extension = "DDS";
                    }
                    if (!Directory.Exists($@"{destDirectoryPath}\{container.type}"))
                    {
                        Directory.CreateDirectory($@"{destDirectoryPath}\{container.type}");
                    }
                    string destRelativePath = $@"{container.type}\{i:D8}.{extension}";
                    using (FileStream extractedFileStream = new FileStream($@"{destDirectoryPath}\{destRelativePath}", FileMode.Create, FileAccess.Write))
                    {
                        extractedFileStream.Write(bytes);
                    }
                    destRelativePaths.Add(destRelativePath);
                }

                // PS3 (VBF): 各テクスチャの DDS ヘッダー (dword 反転) + ペイロードを探し、
                // 標準 (LE) DDS として 4\{i}.DDS に書き出す。テクスチャ以外の部分 (0x1000 境界までの埋めと
                // ペイロード後の 0x44 バイト) は製品版ではすべてゼロで、pack が規則どおりに作り直すので保存しない。
                if (_isBigEndian && ps3Textures.Count > 0)
                {
                    ExtractVbfTextures(sbfBytes, sbfFileStream.Position, ps3Textures, destDirectoryPath, destRelativePaths);
                }

                // SBF Header (グローバルヘッダー + エントリーテーブル + 最初のブロックまでのパディング)。
                // 空アーカイブ (count = 0) はファイル全体をヘッダーとして保存する。
                bytes = new byte[length > 0 ? entrys[0].offset : sbfBytes.Length];
                sbfFileStream.Seek(0x00, SeekOrigin.Begin);
                sbfFileStream.Read(bytes, 0x00, bytes.Length);
                // Reversing for PS3 ver
                if (_isBigEndian)
                {
                    Ps3DdsUtil.DwordReverseInPlace(bytes, 0, bytes.Length & ~3);
                }
                using (FileStream sbfHeader = new FileStream($@"{metaDataPath}\_header.bin", FileMode.Create, FileAccess.Write))
                {
                    sbfHeader.Write(bytes);
                }
                // Order of Container
                using (StreamWriter orderTextWriter = File.CreateText($@"{metaDataPath}\_order.txt"))
                {
                    foreach (string _path in destRelativePaths)
                    {
                        orderTextWriter.WriteLine(_path);
                    }
                }
            }

            Console.WriteLine("Done.");
            return;
        }

        private static void ExtractVbfTextures(byte[] sbfBytes, long mainEnd, List<(int index, long size)> textures,
                                               string destDirectoryPath, List<string> destRelativePaths)
        {
            if (mainEnd > sbfBytes.Length)
            {
                throw new SbfFormatException($"the texture headers end at 0x{mainEnd:X}, past the end of the file (0x{sbfBytes.Length:X}).");
            }
            byte[] region = new byte[sbfBytes.Length - mainEnd];
            Array.Copy(sbfBytes, mainEnd, region, 0, region.Length);

            long[] payloadLens = new long[textures.Count];
            for (int k = 0; k < textures.Count; k++)
            {
                if (textures[k].size < Ps3DdsUtil.SizeFieldOverhead)
                {
                    throw new SbfFormatException($"block {textures[k].index:D8}: size 0x{textures[k].size:X} is smaller than " +
                                                 "the descriptor + DDS header (0xAC).");
                }
                payloadLens[k] = textures[k].size - Ps3DdsUtil.SizeFieldOverhead;
            }
            long[] positions = Ps3DdsUtil.LocateVbfTextures(region, mainEnd, payloadLens, out bool searched);
            if (searched)
            {
                Console.WriteLine("Note: the texture data is not laid out by the standard VBF rules " +
                                  "(e.g. a file repacked by an old tool); it was located by searching for DDS headers.");
            }

            Directory.CreateDirectory(Path.Combine(destDirectoryPath, "4"));
            byte[] covered = new byte[region.Length];
            for (int k = 0; k < textures.Count; k++)
            {
                int pos = (int)(positions[k] - mainEnd);
                int length = (int)(Ps3DdsUtil.DdsHeaderSize + payloadLens[k]);
                byte[] dds = new byte[length];
                Array.Copy(region, pos, dds, 0, length);
                Ps3DdsUtil.DwordReverseInPlace(dds, 0, Ps3DdsUtil.DdsHeaderSize);   // BE ヘッダー -> 標準 DDS ヘッダー
                for (int j = pos; j < pos + length; j++)
                {
                    covered[j] = 1;
                }

                string destRelativePath = $@"4\{textures[k].index:D8}.DDS";
                File.WriteAllBytes($@"{destDirectoryPath}\{destRelativePath}", dds);
                destRelativePaths.Add(destRelativePath);
            }

            // テクスチャ以外に 0 以外のバイトがあれば、pack で作り直したときに失われるので知らせる
            long dropped = 0;
            for (int j = 0; j < region.Length; j++)
            {
                if (covered[j] == 0 && region[j] != 0)
                {
                    dropped++;
                }
            }
            if (dropped > 0)
            {
                Console.WriteLine($"Warning: 0x{dropped:X} non-zero byte(s) lie outside the textures; " +
                                  "they are not kept, and pack writes zeros there.");
            }
        }

    }
}

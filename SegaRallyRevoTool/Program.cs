using CommandLine;
using System;
using System.IO;

namespace SegaRallyRevoTool
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Sega Rally Revo Tool - by chmcl95");
            Console.WriteLine();

            Parser.Default.ParseArguments<UnpackVerbs, PackVerbs, ExportDdsVerbs, ImportDdsVerbs>(args)
                .WithParsed<UnpackVerbs>(Unpack)
                .WithParsed<PackVerbs>(Pack)
                .WithParsed<ExportDdsVerbs>(ExportDds)
                .WithParsed<ImportDdsVerbs>(ImportDds);
        }

        public static void Unpack(UnpackVerbs options)
        {
            if (!File.Exists(options.InputPath))
            {
                Console.WriteLine($"'{options.InputPath}' does not exist.");
                return;
            }
            string outputPath = options.OutputPath;
            if (string.IsNullOrEmpty(options.OutputPath))
            {
                outputPath = $"{Path.GetDirectoryName(options.InputPath)}\\extracted";
            }

            Unpacker unpacker = new Unpacker(options.InputPath, outputPath, options.OnlyDecompress, options.IsBigEndian);
            unpacker.Unpack();

            // PS3 (--ps3) の場合はアンパック後に標準 DDS を dds/ へ自動抽出する
            if (options.IsBigEndian && !options.OnlyDecompress)
            {
                string destDirectoryPath = $@"{outputPath}\{Path.GetFileNameWithoutExtension(options.InputPath)}";
                if (Directory.Exists(destDirectoryPath))
                {
                    DdsExporter exporter = new DdsExporter(destDirectoryPath);
                    exporter.Export();
                }
            }

            return;
        }

        public static void Pack(PackVerbs options)
        {
            if (!Directory.Exists(options.InputPath))
            {
                Console.WriteLine($"'{options.InputPath}' does not exist.");
                return;
            }
            if (!File.Exists($@"{options.InputPath}\\_meta\\_order.txt"))
            {
                Console.WriteLine($"'{options.InputPath}\\_meta\\_order.txt' does not exist.");
                return;
            }
            if (!File.Exists($@"{options.InputPath}\\_meta\\_header.bin"))
            {
                Console.WriteLine($"'{options.InputPath}\\_meta\\_header.bin' does not exist.");
                return;
            }

            string outputPath = options.OutputPath;
            if (string.IsNullOrEmpty(options.OutputPath))
            {
                outputPath = $"{Path.GetDirectoryName(options.InputPath)}\\packed";
            }

            // PS3 (--ps3) の場合は dds/ の内容を 4/ の生スライスへ自動で書き戻してからパックする
            if (options.IsBigEndian && Directory.Exists($@"{options.InputPath}\dds"))
            {
                DdsImporter importer = new DdsImporter(options.InputPath);
                if (!importer.Import())
                {
                    Console.WriteLine("DDS import failed. Aborting pack.");
                    return;
                }
            }

            Packer packer = new Packer(options.InputPath, outputPath, options.DisableCompress, options.IsBigEndian);
            packer.Pack();

            return;
        }

        public static void ExportDds(ExportDdsVerbs options)
        {
            if (!Directory.Exists(options.InputPath))
            {
                Console.WriteLine($"'{options.InputPath}' does not exist.");
                return;
            }

            DdsExporter exporter = new DdsExporter(options.InputPath);
            exporter.Export();

            return;
        }

        public static void ImportDds(ImportDdsVerbs options)
        {
            if (!Directory.Exists(options.InputPath))
            {
                Console.WriteLine($"'{options.InputPath}' does not exist.");
                return;
            }

            DdsImporter importer = new DdsImporter(options.InputPath);
            importer.Import();

            return;
        }
    }


    [Verb("unpack", HelpText = "Unpacks SBF file.  Files are extract in \"extracted\" folder.(Deafult)")]
    public class UnpackVerbs
    {
        [Option('i', "input", Required = true, HelpText = "Input .BIN file like STR1.BIN")]
        public string InputPath { get; set; }

        [Option('o', "output", Required = false, HelpText = "Output directory for the extracted files.")]
        public string OutputPath { get; set; }

        [Option("decomp", Required = false, HelpText = "Only decompressing SBF files.")]
        public bool OnlyDecompress { get; set; }

        [Option("ps3", Required = false, HelpText = "Targets file as vbf (PS3)")]
        public bool IsBigEndian { get; set; }

    }

    [Verb("pack", HelpText = "Pack SBF file .Files are generat in \"packed\" folder.(Deafult)")]
    public class PackVerbs
    {
        [Option('i', "input", Required = true, HelpText = "Input Directry. Need unocked SBF files.")]
        public string InputPath { get; set; }

        [Option('o', "output", Required = false, HelpText = "Output directory for the recreate SBF files.")]
        public string OutputPath { get; set; }

        [Option("nocomp", Required = false, HelpText = "Disableo compressing SBF files.")]
        public bool DisableCompress { get; set; }

        [Option("ps3", Required = false, HelpText = "Targets file as vbf (PS3)")]
        public bool IsBigEndian { get; set; }

    }

    [Verb("exportdds", HelpText = "Exports standard DDS files (dds/{i}.DDS) from a PS3 (--ps3) unpacked directory's raw 4/{i}.BIN slices.")]
    public class ExportDdsVerbs
    {
        [Option('i', "input", Required = true, HelpText = "Unpacked directory (output of 'unpack --ps3').")]
        public string InputPath { get; set; }
    }

    [Verb("importdds", HelpText = "Imports dds/{i}.DDS back into a PS3 (--ps3) unpacked directory's raw 4/{i}.BIN slices, so that 'pack --ps3' can regenerate the archive.")]
    public class ImportDdsVerbs
    {
        [Option('i', "input", Required = true, HelpText = "Unpacked directory (output of 'unpack --ps3', containing a 'dds' subfolder from 'exportdds').")]
        public string InputPath { get; set; }
    }
}

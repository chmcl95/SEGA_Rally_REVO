using CommandLine;
using SegaRallyRevoTool.SegaRallyRevoLib;
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

            Parser.Default.ParseArguments<UnpackVerbs, PackVerbs>(args)
                .WithParsed<UnpackVerbs>(Unpack)
                .WithParsed<PackVerbs>(Pack);
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
            try
            {
                unpacker.Unpack();
            }
            catch (SbfFormatException ex)
            {
                Fail($"cannot unpack '{options.InputPath}': {ex.Message}");
                return;
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

            bool guessed = false;
            bool isBigEndian = false;
            try
            {
                // エンディアンは unpack が書いた _meta\_endian.txt から決める (無ければ --ps3)
                isBigEndian = Packer.ResolveBigEndian(options.InputPath, options.IsBigEndian, out guessed);

                Packer packer = new Packer(options.InputPath, outputPath, options.DisableCompress, isBigEndian);
                packer.Pack();
            }
            catch (SbfFormatException ex)
            {
                string hint = guessed
                    ? $" (_meta\\{EndianMarker.FileName} is missing, so {(isBigEndian ? "big" : "little")}-endian was assumed; " +
                      (isBigEndian ? "this may be a PC archive." : "if this is a PS3 archive, pass --ps3.") + ")"
                    : "";
                Fail($"cannot pack '{options.InputPath}': {ex.Message}{hint}");
            }

            return;
        }

        private static void Fail(string message)
        {
            Console.WriteLine($"Error: {message}");
            Environment.ExitCode = 1;
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

        [Option("ps3", Required = false, HelpText = "Optional. The byte order (PC little-endian / PS3 big-endian) is detected automatically; this flag is only checked against the detection.")]
        public bool IsBigEndian { get; set; }

    }

    [Verb("pack",HelpText = "Pack SBF file .Files are generat in \"packed\" folder.(Deafult)")]
    public class PackVerbs
    {
        [Option('i', "input", Required = true, HelpText = "Input Directry. Need unocked SBF files.")]
        public string InputPath { get; set; }

        [Option('o', "output", Required = false, HelpText = "Output directory for the recreate SBF files.")]
        public string OutputPath { get; set; }

        [Option("nocomp", Required = false, HelpText = "Disableo compressing SBF files.")]
        public bool DisableCompress { get; set; }

        [Option("ps3", Required = false, HelpText = "Only needed for directories unpacked by an older version (without _meta\\_endian.txt): treat the archive as PS3 (big-endian).")]
        public bool IsBigEndian { get; set; }

    }
}

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

            Unpacker unpacker = new Unpacker(options.InputPath, outputPath);
            unpacker.Unpack();

            return;
        }

        public static void Pack(PackVerbs options)
        {
            if (!Directory.Exists(options.InputPath))
            {
                Console.WriteLine($"'{options.InputPath}' does not exist.");
                return;
            }

            string outputPath = options.OutputPath;
            if (string.IsNullOrEmpty(options.OutputPath))
            {
                outputPath = $"{Path.GetDirectoryName(options.InputPath)}\\packed";
            }

            Packer packer = new Packer(options.InputPath, outputPath);
            packer.Pack();

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

        //[Option("nc", Required = false, HelpText = "Only decompressing SBF files.")]
        //public string OnlyDecompress { get; set; }

    }

    [Verb("pack", HelpText = "Pack SBF file .Files are generat in \"packed\" folder.(Deafult)")]
    public class PackVerbs
    {
        [Option('i', "input", Required = true, HelpText = "Input Directry. Need unocked SBF files.")]
        public string InputPath { get; set; }

        [Option('o', "output", Required = false, HelpText = "Output directory for the recreate SBF files.")]
        public string OutputPath { get; set; }

        //[Option("nc", Required = false, HelpText = "Disableo compressing SBF files.")]
        //public string DisableCompress { get; set; }


    }
}

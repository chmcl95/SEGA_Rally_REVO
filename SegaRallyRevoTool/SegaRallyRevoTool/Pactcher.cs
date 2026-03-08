using SegaRallyRevoTool.SEGAGTLib;
using System;
using System.IO;

namespace SegaRallyRevoTool
{
    class Pactcher
    {
        private string _elfPath;
        private string _inputPath;
        private string _destPath;

        private TOCInformation _toc;

        public Pactcher(string elfPath, string inputPath, string outputPath, string revison)
        {
            _elfPath = elfPath;
            _inputPath = inputPath;
            _destPath = outputPath;

            string elfName = Path.GetFileName(_elfPath);

            if (!TableOfContents.TOCInfos.TryGetValue(elfName, out TOCInformation toc))
            {
                throw new ArgumentException("Invalid or non-supported elf of Ridge Racer V provided.");
            }

            _toc = toc;
        }
    }
}

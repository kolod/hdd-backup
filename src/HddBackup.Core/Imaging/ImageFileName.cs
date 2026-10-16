using System;

namespace HddBackup.Core.Imaging
{
    /// <summary>Image file name extensions (.img / .img.gz).</summary>
    public static class ImageFileName
    {
        public const string Gzip = ".img.gz";
        public const string Raw = ".img";

        /// <summary>
        /// The path without any trailing <c>.img</c> / <c>.gz</c> extensions. Strips all of them, because
        /// the file dialog replaces only the last one when the filter changes (<c>x.img.gz</c> → <c>x.img.img</c>).
        /// </summary>
        public static string Stem(string path)
        {
            string stem = path;
            while (true)
            {
                if (stem.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && stem.Length > 3)
                {
                    stem = stem.Substring(0, stem.Length - 3);
                }
                else if (stem.EndsWith(".img", StringComparison.OrdinalIgnoreCase) && stem.Length > 4)
                {
                    stem = stem.Substring(0, stem.Length - 4);
                }
                else
                {
                    return stem;
                }
            }
        }

        /// <summary>The path with exactly one <c>.img.gz</c> or <c>.img</c> extension.</summary>
        public static string WithExtension(string path, bool gzip)
        {
            return path.Length == 0 ? path : Stem(path) + (gzip ? Gzip : Raw);
        }
    }
}

using System;
using System.Globalization;
using System.IO;

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

        /// <summary>
        /// <paramref name="path"/> when neither it nor its checksum file exists, otherwise the first
        /// free <c>name (2).img[.gz]</c>, <c>name (3).img[.gz]</c>, … so an earlier result is never replaced.
        /// </summary>
        public static string FirstFree(string path)
        {
            if (!Taken(path))
            {
                return path;
            }

            bool gzip = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
            string stem = Stem(path);
            for (int n = 2; ; n++)
            {
                string candidate = stem + " (" + n.ToString(CultureInfo.InvariantCulture) + ")" + (gzip ? Gzip : Raw);
                if (!Taken(candidate))
                {
                    return candidate;
                }
            }
        }

        private static bool Taken(string path) =>
            File.Exists(path) || File.Exists(path + ".sha256") || File.Exists(path + ".partial");

        /// <summary>The path with exactly one <c>.img.gz</c> or <c>.img</c> extension.</summary>
        public static string WithExtension(string path, bool gzip)
        {
            return path.Length == 0 ? path : Stem(path) + (gzip ? Gzip : Raw);
        }
    }
}

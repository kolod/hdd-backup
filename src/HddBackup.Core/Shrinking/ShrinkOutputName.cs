using System.Globalization;
using System.IO;
using HddBackup.Core.Imaging;

namespace HddBackup.Core.Shrinking
{
    /// <summary>Default file name for a shrunk copy of an image.</summary>
    public static class ShrinkOutputName
    {
        /// <summary>
        /// <c>&lt;dir&gt;\&lt;name&gt;_shrunk[_&lt;N.#&gt;G].img[.gz]</c> next to <paramref name="source"/>;
        /// the size suffix is the target in GiB when a target is given.
        /// </summary>
        public static string Default(string source, long? targetBytes, bool gzip)
        {
            string name = ImageFileName.Stem(Path.GetFileName(source));
            string suffix = targetBytes.HasValue
                ? "_shrunk_" + (targetBytes.Value / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + "G"
                : "_shrunk";
            return Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, name + suffix + (gzip ? ImageFileName.Gzip : ImageFileName.Raw));
        }
    }
}

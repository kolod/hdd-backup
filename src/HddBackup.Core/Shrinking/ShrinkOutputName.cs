using System.Globalization;
using System.IO;
using HddBackup.Core.Imaging;

namespace HddBackup.Core.Shrinking
{
    /// <summary>Default file names for shrunk and repaired copies of an image.</summary>
    public static class ShrinkOutputName
    {
        /// <summary>
        /// <c>&lt;dir&gt;\&lt;name&gt;_shrunk[_&lt;N.#&gt;G].img[.gz]</c> next to <paramref name="source"/>;
        /// the size suffix is the target in GiB when a target is given.
        /// </summary>
        public static string Default(string source, long? targetBytes, bool gzip) => Name(source, string.Empty, targetBytes, gzip);

        /// <summary><c>&lt;name&gt;_repaired.img[.gz]</c>, or <c>&lt;name&gt;_repaired_shrunk[_&lt;N.#&gt;G]…</c> when also shrunk.</summary>
        public static string Repaired(string source, long? targetBytes, bool gzip, bool shrunk) =>
            shrunk ? Name(source, "_repaired", targetBytes, gzip) : Name(source, "_repaired", null, gzip, suffixOnly: true);

        private static string Name(string source, string prefix, long? targetBytes, bool gzip, bool suffixOnly = false)
        {
            string name = ImageFileName.Stem(Path.GetFileName(source));
            string suffix = suffixOnly ? string.Empty
                : targetBytes.HasValue
                    ? "_shrunk_" + (targetBytes.Value / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + "G"
                    : "_shrunk";
            return Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, name + prefix + suffix + (gzip ? ImageFileName.Gzip : ImageFileName.Raw));
        }
    }
}

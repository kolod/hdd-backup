using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HddBackup.Core.Formats
{
    public enum ChecksumKind
    {
        /// <summary>Checksum of the image file as stored (compressed for .img.gz).</summary>
        File,

        /// <summary>Checksum of the uncompressed content (equals the device or partition).</summary>
        Content
    }

    /// <summary>
    /// One-entry checksum sidecar in sha256sum format: "&lt;hex&gt; *&lt;name&gt;\n".
    /// </summary>
    public sealed class ChecksumFile
    {
        public const string Extension = ".sha256";

        private static readonly Regex EntryPattern = new Regex(
            @"^(?<hash>[0-9a-fA-F]{64}) [ *](?<name>.+)$", RegexOptions.CultureInvariant);

        public ChecksumFile(string path, ChecksumKind kind, string sha256, string label)
        {
            Path = path;
            Kind = kind;
            Sha256 = sha256;
            Label = label;
        }

        public string Path { get; }

        public ChecksumKind Kind { get; }

        /// <summary>64 lowercase hex characters.</summary>
        public string Sha256 { get; }

        /// <summary>File name written inside the entry (informational).</summary>
        public string Label { get; }

        public static string FormatEntry(string sha256, string label)
        {
            return sha256.ToLowerInvariant() + " *" + label + "\n";
        }

        public static ChecksumFile Parse(string text, string path, ChecksumKind kind)
        {
            if (text == null)
            {
                throw new InvalidDataException("Checksum file is empty.");
            }

            string found = null;
            foreach (var raw in text.TrimStart('﻿').Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                if (found != null)
                {
                    throw new InvalidDataException("Checksum file must contain exactly one entry.");
                }

                found = line;
            }

            if (found == null)
            {
                throw new InvalidDataException("Checksum file is empty.");
            }

            var match = EntryPattern.Match(found);
            if (!match.Success)
            {
                throw new InvalidDataException("Checksum entry is not in sha256sum format.");
            }

            return new ChecksumFile(path, kind, match.Groups["hash"].Value.ToLowerInvariant(), match.Groups["name"].Value);
        }

        public static ChecksumFile Read(string path, ChecksumKind kind)
        {
            return Parse(File.ReadAllText(path, Encoding.UTF8), path, kind);
        }

        /// <summary>Writes the sidecar and flushes it to storage.</summary>
        public static void Write(string path, string sha256, string label)
        {
            var bytes = Encoding.ASCII.GetBytes(FormatEntry(sha256, label));
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }
    }
}

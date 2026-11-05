using System;
using System.Text;
using System.Threading;

namespace HddBackup.Core.Browsing
{
    public enum PreviewMode
    {
        Auto,
        Text,
        Hex
    }

    /// <summary>
    /// The beginning of a file as text or as a hex dump (research R-10). Reads at most
    /// <see cref="TextLimit"/> or <see cref="HexLimit"/> bytes; larger files report how much is shown.
    /// </summary>
    public sealed class FilePreview
    {
        public const int TextLimit = 1 << 20;
        public const int HexLimit = 256 << 10;
        public const int AutoProbeBytes = 8192;
        private const int ReadChunk = 64 << 10;

        private FilePreview()
        {
        }

        /// <summary>How the content is shown: <see cref="PreviewMode.Text"/> or <see cref="PreviewMode.Hex"/>.</summary>
        public PreviewMode Mode { get; private set; } = PreviewMode.Text;

        /// <summary>The rendered content (CRLF line breaks, ready for a text box); empty when there is none.</summary>
        public string Text { get; private set; } = string.Empty;

        /// <summary>Bytes of the file that are shown.</summary>
        public long ShownBytes { get; private set; }

        public long TotalBytes { get; private set; }

        public bool Truncated => ShownBytes < TotalBytes;

        /// <summary>
        /// Builds the preview of a regular file. Anything else (directory, link, special file, an
        /// unreadable or not viewable file) gives an empty preview: only the header is shown.
        /// </summary>
        public static FilePreview Create(IFileTree tree, FsEntry file, PreviewMode requested, CancellationToken cancellationToken)
        {
            if (tree == null)
            {
                throw new ArgumentNullException(nameof(tree));
            }

            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            var preview = new FilePreview { TotalBytes = file.Size };
            if (!file.HasContent || file.Size == 0)
            {
                preview.TotalBytes = file.HasContent ? file.Size : 0;
                return preview;
            }

            var mode = requested;
            if (mode == PreviewMode.Auto)
            {
                int probeLength = (int)Math.Min(file.Size, AutoProbeBytes);
                var probe = ReadBytes(tree, file, probeLength, cancellationToken);
                mode = LooksLikeText(probe, probe.Length, file.Size > probe.Length) ? PreviewMode.Text : PreviewMode.Hex;
            }

            int limit = mode == PreviewMode.Text ? TextLimit : HexLimit;
            var data = ReadBytes(tree, file, (int)Math.Min(file.Size, limit), cancellationToken);
            preview.Mode = mode;
            if (mode == PreviewMode.Text)
            {
                int count = data.Length;
                int start = HasBom(data) ? 3 : 0;
                if (data.Length < file.Size)
                {
                    count = TrimIncompleteTail(data, data.Length);
                }

                preview.ShownBytes = count;
                preview.Text = RenderText(data, start, count - start);
            }
            else
            {
                preview.ShownBytes = data.Length;
                preview.Text = RenderHex(data);
            }

            return preview;
        }

        /// <summary>One line per 16 bytes: offset, the bytes in two groups of eight, and the printable characters.</summary>
        public static string RenderHex(byte[] data)
        {
            var sb = new StringBuilder((data.Length / 16 + 1) * 77);
            for (int offset = 0; offset < data.Length; offset += 16)
            {
                if (offset > 0)
                {
                    sb.Append("\r\n");
                }

                int count = Math.Min(16, data.Length - offset);
                sb.Append(offset.ToString("X8")).Append("  ");
                for (int i = 0; i < 16; i++)
                {
                    if (i < count)
                    {
                        sb.Append(data[offset + i].ToString("X2")).Append(' ');
                    }
                    else
                    {
                        sb.Append("   ");
                    }

                    if (i == 7)
                    {
                        sb.Append(' ');
                    }
                }

                sb.Append(" |");
                for (int i = 0; i < count; i++)
                {
                    byte b = data[offset + i];
                    sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                }

                sb.Append('|');
            }

            return sb.ToString();
        }

        private static byte[] ReadBytes(IFileTree tree, FsEntry file, int length, CancellationToken cancellationToken)
        {
            var data = new byte[length];
            int done = 0;
            while (done < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = tree.ReadRange(file, done, data, done, Math.Min(ReadChunk, length - done));
                if (n <= 0)
                {
                    Array.Resize(ref data, done);
                    break;
                }

                done += n;
            }

            return data;
        }

        private static bool HasBom(byte[] data) => data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;

        /// <summary>No NUL and no unusual control characters, and valid UTF-8 (a cut-off last character is fine when the file goes on).</summary>
        internal static bool LooksLikeText(byte[] data, int count, bool truncated)
        {
            int i = 0;
            while (i < count)
            {
                byte b = data[i];
                if (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D && b != 0x0C && b != 0x1B)
                {
                    return false;
                }

                if (b < 0x80)
                {
                    i++;
                    continue;
                }

                int length = b >= 0xC2 && b <= 0xDF ? 2 : b >= 0xE0 && b <= 0xEF ? 3 : b >= 0xF0 && b <= 0xF4 ? 4 : 0;
                if (length == 0)
                {
                    return false;
                }

                if (i + length > count)
                {
                    return truncated; // the sequence is cut off by the end of what was read
                }

                for (int k = 1; k < length; k++)
                {
                    if ((data[i + k] & 0xC0) != 0x80)
                    {
                        return false;
                    }
                }

                i += length;
            }

            return true;
        }

        /// <summary>Length without a UTF-8 character that the end of the data cuts in two.</summary>
        private static int TrimIncompleteTail(byte[] data, int count)
        {
            int back = Math.Min(3, count);
            for (int k = 1; k <= back; k++)
            {
                byte b = data[count - k];
                if ((b & 0xC0) == 0x80)
                {
                    continue; // a continuation byte: look further back for its lead byte
                }

                int length = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
                return length > k ? count - k : count;
            }

            return count;
        }

        private static string RenderText(byte[] data, int start, int length)
        {
            string text = new UTF8Encoding(false, false).GetString(data, start, length);
            var sb = new StringBuilder(text.Length + text.Length / 40);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    sb.Append("\r\n");
                }
                else if (c == '\n')
                {
                    sb.Append("\r\n");
                }
                else if (c == '\t' || (c >= ' ' && c != 0x7F))
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('�'); // NUL and other control characters would break a text box
                }
            }

            return sb.ToString();
        }
    }
}

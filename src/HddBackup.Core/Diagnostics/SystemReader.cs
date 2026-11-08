using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Errors;

namespace HddBackup.Core.Diagnostics
{
    /// <summary>
    /// Reads files and directories of an opened file system by absolute path, following symbolic
    /// links inside the image (never on the host). Lists and small files are cached for the run.
    /// There is no write path. Unreadable entries and over-long files are reported as return
    /// values and through <see cref="Notes"/>; only errors of the source itself (device removed,
    /// read error) are thrown.
    /// </summary>
    public sealed class SystemReader
    {
        public const int DefaultMaxBytes = 1 << 20;
        public const int DefaultMaxEntries = 5000;
        public const int MaxLinks = 40;

        private readonly IFileTree _tree;
        private readonly CancellationToken _ct;
        private readonly Dictionary<uint, DirectoryCache> _dirs = new Dictionary<uint, DirectoryCache>();
        private readonly HashSet<FsEntry> _described = new HashSet<FsEntry>();
        private readonly Dictionary<string, TextResult> _texts = new Dictionary<string, TextResult>(StringComparer.Ordinal);

        public SystemReader(IFileTree tree, CancellationToken cancellationToken = default(CancellationToken))
        {
            _tree = tree ?? throw new ArgumentNullException(nameof(tree));
            _ct = cancellationToken;
        }

        public ReadNotes Notes { get; } = new ReadNotes();

        /// <summary>Number of file reads that went to the tree (cache misses), for tests and the performance check.</summary>
        public int FileReads { get; private set; }

        /// <summary>Number of directory listings that went to the tree.</summary>
        public int Listings { get; private set; }

        /// <summary>The entry at <paramref name="path"/> (described: mode, size), or null. A final link is followed unless <paramref name="followLast"/> is false.</summary>
        public FsEntry Lookup(string path, bool followLast = true)
        {
            var entry = Walk(path, followLast, out _);
            if (entry != null)
            {
                Describe(entry);
            }

            return entry;
        }

        public bool Exists(string path) => Lookup(path) != null;

        public bool IsFile(string path)
        {
            var e = Lookup(path);
            return e != null && e.Kind == FsEntryKind.File;
        }

        public bool IsDirectory(string path)
        {
            var e = Lookup(path);
            return e != null && e.IsDirectory;
        }

        /// <summary>True for a regular file that has an execute bit for anyone.</summary>
        public bool IsExecutable(string path)
        {
            var e = Lookup(path);
            return e != null && e.Kind == FsEntryKind.File && (e.Mode & 0x49) != 0;
        }

        /// <summary>The target text of a link at <paramref name="path"/> (the link itself, not followed), or null when it is not a link.</summary>
        public string ReadLink(string path)
        {
            var e = Walk(path, false, out _);
            if (e == null || e.Kind != FsEntryKind.Symlink)
            {
                return null;
            }

            Describe(e);
            return e.LinkTarget;
        }

        public bool IsLink(string path) => ReadLink(path) != null;

        /// <summary>
        /// The canonical path of <paramref name="path"/> with every link replaced by its target,
        /// or null when the path does not exist or has too many links.
        /// </summary>
        public string Resolve(string path, bool followLast = true)
        {
            Walk(path, followLast, out string resolved);
            return resolved;
        }

        /// <summary>
        /// The entries of a directory (links in the path followed), ordered by name, cut at
        /// <paramref name="maxEntries"/>; null when the path is not a directory. Entries have a
        /// kind and link target; use <see cref="Lookup"/> for mode and size.
        /// </summary>
        public IReadOnlyList<FsEntry> List(string path, int maxEntries = DefaultMaxEntries)
        {
            var dir = Walk(path, true, out string resolved);
            if (dir == null || !dir.IsDirectory)
            {
                return null;
            }

            var cache = Children(dir);
            if (cache == null)
            {
                Notes.Unreadable(resolved ?? path);
                return null;
            }

            if (cache.Ordered.Count > maxEntries)
            {
                Notes.Cut(resolved ?? path);
                return cache.Ordered.Take(maxEntries).ToList();
            }

            return cache.Ordered;
        }

        /// <summary>Names of the entries of a directory (empty when it is missing).</summary>
        public IEnumerable<string> ListNames(string path)
        {
            var list = List(path);
            return list == null ? Enumerable.Empty<string>() : list.Select(e => e.Name);
        }

        /// <summary>
        /// The text of a regular file (links followed), decoded as UTF-8 and read up to
        /// <paramref name="maxBytes"/>; null when it is missing, not a regular file or unreadable.
        /// </summary>
        public string ReadText(string path, int maxBytes = DefaultMaxBytes)
        {
            return ReadText(path, out _, maxBytes);
        }

        public string ReadText(string path, out bool truncated, int maxBytes = DefaultMaxBytes)
        {
            _ct.ThrowIfCancellationRequested();
            truncated = false;
            var entry = Walk(path, true, out string resolved);
            if (entry == null || entry.Kind != FsEntryKind.File)
            {
                return null;
            }

            string key = resolved + "|" + maxBytes;
            if (_texts.TryGetValue(key, out var cached))
            {
                truncated = cached.Truncated;
                return cached.Text;
            }

            Describe(entry);
            TextResult result;
            if (entry.Unreadable != null || entry.NotViewable != null)
            {
                Notes.Unreadable(resolved);
                result = new TextResult(null, false);
            }
            else
            {
                long size = entry.Size;
                int toRead = (int)Math.Min(size, maxBytes);
                var buffer = new byte[toRead];
                int got = 0;
                try
                {
                    while (got < toRead)
                    {
                        _ct.ThrowIfCancellationRequested();
                        int n = _tree.ReadRange(entry, got, buffer, got, Math.Min(toRead - got, 64 * 1024));
                        if (n <= 0)
                        {
                            break;
                        }

                        got += n;
                    }

                    FileReads++;
                    bool cut = size > maxBytes;
                    if (cut)
                    {
                        Notes.Truncated(resolved);
                    }

                    result = new TextResult(Decode(buffer, got), cut);
                }
                catch (ImagingException ex) when (ex.Code == ErrorCode.CorruptImage)
                {
                    Notes.Unreadable(resolved);
                    result = new TextResult(null, false);
                }
            }

            _texts[key] = result;
            truncated = result.Truncated;
            return result.Text;
        }

        /// <summary>
        /// The bytes of a regular file (links followed), exactly as stored, up to <paramref name="maxBytes"/>; null when
        /// it is missing, not a regular file, unreadable or longer than the limit (so a repair never works on a cut file).
        /// </summary>
        public byte[] ReadBytes(string path, int maxBytes = DefaultMaxBytes)
        {
            _ct.ThrowIfCancellationRequested();
            var entry = Walk(path, true, out _);
            if (entry == null || entry.Kind != FsEntryKind.File)
            {
                return null;
            }

            Describe(entry);
            if (entry.Unreadable != null || entry.NotViewable != null || entry.Size > maxBytes)
            {
                return null;
            }

            var buffer = new byte[entry.Size];
            int got = 0;
            try
            {
                while (got < buffer.Length)
                {
                    _ct.ThrowIfCancellationRequested();
                    int n = _tree.ReadRange(entry, got, buffer, got, Math.Min(buffer.Length - got, 64 * 1024));
                    if (n <= 0)
                    {
                        return null;
                    }

                    got += n;
                }
            }
            catch (ImagingException ex) when (ex.Code == ErrorCode.CorruptImage)
            {
                return null;
            }

            return buffer;
        }

        /// <summary>The lines of a text file (empty when it cannot be read). A trailing line break does not add an empty line.</summary>
        public IReadOnlyList<string> ReadLines(string path, int maxBytes = DefaultMaxBytes)
        {
            string text = ReadText(path, maxBytes);
            return text == null ? (IReadOnlyList<string>)Array.Empty<string>() : SplitLines(text);
        }

        public static IReadOnlyList<string> SplitLines(string text)
        {
            var lines = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    int end = i;
                    if (end > start && text[end - 1] == '\r')
                    {
                        end--;
                    }

                    lines.Add(text.Substring(start, end - start));
                    start = i + 1;
                }
            }

            if (start < text.Length)
            {
                lines.Add(text.Substring(start));
            }

            return lines;
        }

        /// <summary>Joins an absolute directory path and a name.</summary>
        public static string Combine(string dir, string name)
        {
            return dir.EndsWith("/", StringComparison.Ordinal) ? dir + name : dir + "/" + name;
        }

        private static string Decode(byte[] buffer, int count)
        {
            int skip = count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
            return new UTF8Encoding(false).GetString(buffer, skip, count - skip);
        }

        private void Describe(FsEntry entry)
        {
            if (_described.Add(entry))
            {
                _tree.Describe(entry);
            }
        }

        private DirectoryCache Children(FsEntry dir)
        {
            if (_dirs.TryGetValue(dir.Inode, out var cache))
            {
                return cache;
            }

            _ct.ThrowIfCancellationRequested();
            IReadOnlyList<FsEntry> raw;
            try
            {
                Listings++;
                raw = _tree.GetChildren(dir, _ct);
            }
            catch (ImagingException ex) when (ex.Code == ErrorCode.CorruptImage)
            {
                _dirs[dir.Inode] = null;
                return null;
            }

            cache = new DirectoryCache();
            foreach (var e in raw.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                cache.Ordered.Add(e);
                if (!cache.ByName.ContainsKey(e.Name))
                {
                    cache.ByName[e.Name] = e;
                }
            }

            _dirs[dir.Inode] = cache;
            return cache;
        }

        /// <summary>Walks the components; <paramref name="canonical"/> is the path without links, null when not found.</summary>
        private FsEntry Walk(string path, bool followLast, out string canonical)
        {
            canonical = null;
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var pending = new LinkedList<string>(path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries));
            var stack = new List<FsEntry> { _tree.Root };
            int links = 0;
            while (pending.Count > 0)
            {
                string seg = pending.First.Value;
                pending.RemoveFirst();
                if (seg == ".")
                {
                    continue;
                }

                if (seg == "..")
                {
                    if (stack.Count > 1)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }

                    continue;
                }

                var cur = stack[stack.Count - 1];
                if (!cur.IsDirectory)
                {
                    return null;
                }

                var cache = Children(cur);
                if (cache == null || !cache.ByName.TryGetValue(seg, out var child))
                {
                    return null;
                }

                if (child.Kind == FsEntryKind.Symlink && (pending.Count > 0 || followLast))
                {
                    if (++links > MaxLinks)
                    {
                        return null;
                    }

                    Describe(child);
                    string target = child.LinkTarget;
                    if (string.IsNullOrEmpty(target))
                    {
                        return null;
                    }

                    if (target[0] == '/')
                    {
                        stack.RemoveRange(1, stack.Count - 1);
                    }

                    var parts = target.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = parts.Length - 1; i >= 0; i--)
                    {
                        pending.AddFirst(parts[i]);
                    }

                    continue;
                }

                stack.Add(child);
            }

            var last = stack[stack.Count - 1];
            canonical = "/" + string.Join("/", stack.Skip(1).Select(e => e.Name));
            return last;
        }

        private sealed class DirectoryCache
        {
            public readonly List<FsEntry> Ordered = new List<FsEntry>();
            public readonly Dictionary<string, FsEntry> ByName = new Dictionary<string, FsEntry>(StringComparer.Ordinal);
        }

        private sealed class TextResult
        {
            public TextResult(string text, bool truncated)
            {
                Text = text;
                Truncated = truncated;
            }

            public string Text { get; }

            public bool Truncated { get; }
        }
    }
}

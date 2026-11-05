using System.Collections.Generic;
using System.Threading;

namespace HddBackup.Core.Browsing
{
    /// <summary>
    /// Read access to a file system tree: what the preview, the extraction planner and the
    /// extractor need, so that they can be tested against in-memory trees.
    /// </summary>
    public interface IFileTree
    {
        FsEntry Root { get; }

        /// <summary>
        /// Entries of a directory, directories first, then by name. Entries are "light": name,
        /// kind and (for symbolic links) the target; call <see cref="Describe"/> for the rest.
        /// </summary>
        IReadOnlyList<FsEntry> GetChildren(FsEntry directory, CancellationToken cancellationToken);

        /// <summary>
        /// Fills size, mode, owner, time and the "not viewable" reason. A failure to read the
        /// metadata is recorded in <see cref="FsEntry.Unreadable"/>, not thrown.
        /// </summary>
        void Describe(FsEntry entry);

        /// <summary>
        /// Reads up to <paramref name="count"/> bytes of a regular file starting at
        /// <paramref name="offset"/> (holes read as zeros); returns the number of bytes read,
        /// less than requested only at the end of the file.
        /// </summary>
        int ReadRange(FsEntry file, long offset, byte[] buffer, int index, int count);
    }
}

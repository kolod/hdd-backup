using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HddBackup.Core.Browsing;

namespace HddBackup.Core.Tests.Diagnostics
{
    /// <summary>A real ext4 tree from an image that records the paths of the files whose content was read.</summary>
    internal sealed class RecordingTree : IFileTree, IDisposable
    {
        private readonly BrowseSource _source;
        private readonly Ext4FileSystemView _view;
        private readonly List<string> _reads = new List<string>();

        public RecordingTree(string image)
        {
            _source = BrowseSource.OpenImage(image);
            _view = _source.OpenFileSystem(_source.Browsable.First());
        }

        public IFileTree View => this;

        public IReadOnlyCollection<string> Reads => _reads.Distinct().ToList();

        public FsEntry Root => _view.Root;

        public IReadOnlyList<FsEntry> GetChildren(FsEntry directory, CancellationToken cancellationToken) => _view.GetChildren(directory, cancellationToken);

        public void Describe(FsEntry entry) => _view.Describe(entry);

        public int ReadRange(FsEntry file, long offset, byte[] buffer, int index, int count)
        {
            _reads.Add(file.Path);
            return _view.ReadRange(file, offset, buffer, index, count);
        }

        public void Dispose()
        {
            _view.Dispose();
            _source.Dispose();
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Ext4;
using HddBackup.Core.Imaging;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Browsing
{
    /// <summary>
    /// An image file or a device opened read-only for browsing: its partitions, and for each ext4
    /// partition whether it can be browsed. Nothing in this class writes to the source.
    /// </summary>
    public sealed class BrowseSource : IDisposable
    {
        private IBlockDevice _device;

        private BrowseSource(IBlockDevice device, IReadOnlyList<BrowsePartition> partitions, BrowseProblem problem, string detail)
        {
            _device = device;
            Partitions = partitions;
            Problem = problem;
            ProblemDetail = detail ?? string.Empty;
        }

        public IReadOnlyList<BrowsePartition> Partitions { get; }

        public BrowseProblem Problem { get; }

        /// <summary>Extra text for the problem, e.g. the partitions present.</summary>
        public string ProblemDetail { get; }

        /// <summary>The partitions that can be browsed.</summary>
        public IEnumerable<BrowsePartition> Browsable => Partitions.Where(p => p.IsBrowsable);

        /// <summary>
        /// Opens an uncompressed image. A compressed image is not opened: the result has
        /// <see cref="BrowseProblem.NeedsConversion"/> and no handles. Read errors are thrown as
        /// <see cref="ImagingException"/>.
        /// </summary>
        public static BrowseSource OpenImage(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ImagingException(ErrorCode.InvalidRequest, "No image was chosen.");
            }

            if (ImageDescriptor.IsGzipPath(path))
            {
                return new BrowseSource(null, Array.Empty<BrowsePartition>(), BrowseProblem.NeedsConversion, string.Empty);
            }

            FileBlockDevice device;
            try
            {
                device = FileBlockDevice.OpenRead(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException)
            {
                throw new ImagingException(ErrorCode.ReadError, ex.Message, null, ex);
            }

            return Open(device);
        }

        /// <summary>Opens a device through <see cref="IDeviceAccess.OpenForRead"/>; never locks, dismounts or writes.</summary>
        public static BrowseSource OpenDevice(IDeviceAccess access, DeviceInfo device)
        {
            if (access == null)
            {
                throw new ArgumentNullException(nameof(access));
            }

            return Open(access.OpenForRead(device));
        }

        /// <summary>Takes ownership of an already opened (read-only) device.</summary>
        internal static BrowseSource Open(IBlockDevice device)
        {
            try
            {
                var partitions = ReadPartitions(device);
                if (!partitions.Any(p => p.IsBrowsable))
                {
                    string present = partitions.Count == 0
                        ? string.Empty
                        : string.Join(", ", partitions.Select(Describe));
                    return new BrowseSource(device, partitions, BrowseProblem.NoExt4, present);
                }

                return new BrowseSource(device, partitions, BrowseProblem.None, null);
            }
            catch
            {
                device.Dispose();
                throw;
            }
        }

        /// <summary>Opens the file system of a browsable partition. The view must not outlive this source.</summary>
        public Ext4FileSystemView OpenFileSystem(BrowsePartition partition)
        {
            if (partition == null)
            {
                throw new ArgumentNullException(nameof(partition));
            }

            if (!partition.IsBrowsable)
            {
                throw new ImagingException(ErrorCode.InvalidRequest, "The partition cannot be browsed: " + partition.Unsupported);
            }

            if (_device == null)
            {
                throw new ObjectDisposedException(nameof(BrowseSource));
            }

            PartitionView window = null;
            try
            {
                window = new PartitionView(_device, partition.Offset, partition.Length);
                var reader = new Ext4Reader(window);
                return new Ext4FileSystemView(reader, window, partition.State);
            }
            catch (Ext4FormatException ex)
            {
                window?.Dispose();
                throw new ImagingException(ErrorCode.CorruptImage, ex.Message, partition.Offset, ex);
            }
        }

        public void Dispose()
        {
            _device?.Dispose();
            _device = null;
        }

        private static string Describe(BrowsePartition p)
        {
            string name = string.IsNullOrEmpty(p.Name) ? string.Empty : " " + p.Name;
            return "#" + p.Number + name + " (" + p.FileSystem + ")";
        }

        private static List<BrowsePartition> ReadPartitions(IBlockDevice device)
        {
            var table = PartitionTableReader.Read(device);
            var result = new List<BrowsePartition>();
            if (table.Partitions.Count == 0)
            {
                // No partition table: a single-partition image or a file system on the whole device.
                string fs = FileSystemProbe.Detect(device, 0, device.Size);
                if (fs != null)
                {
                    result.Add(Classify(device, 1, string.Empty, 0, device.Size, fs));
                }

                return result;
            }

            foreach (var p in table.Partitions.OrderBy(x => x.Offset))
            {
                result.Add(Classify(device, p.Number, p.Name ?? string.Empty, p.Offset, p.Length, p.FileSystem));
            }

            return result;
        }

        private static BrowsePartition Classify(IBlockDevice device, int number, string name, long offset, long length, string fileSystem)
        {
            var result = new BrowsePartition
            {
                Number = number,
                Name = name,
                Offset = offset,
                Length = length,
                FileSystem = fileSystem ?? FileSystemProbe.UnknownName
            };
            if (result.FileSystem != "ext4")
            {
                result.Unsupported = "not an ext4 file system (" + result.FileSystem + ")";
                return result;
            }

            try
            {
                using (var window = new PartitionView(device, offset, length))
                using (var reader = new Ext4Reader(window))
                {
                    var sb = reader.Superblock;
                    result.State = Ext4StateProbe.Probe(sb, number, name, false);
                    var unsupported = Ext4Features.Unsupported(sb);
                    if (unsupported.Count > 0)
                    {
                        result.Unsupported = "unsupported features: " + string.Join(", ", unsupported);
                        return result;
                    }

                    // A file system whose root directory cannot be read has no tree to show.
                    var root = reader.ReadInode(2);
                    if (!root.IsDirectory)
                    {
                        result.Unsupported = "the root directory is damaged";
                        return result;
                    }

                    reader.ReadDirectory(root);
                    result.IsBrowsable = true;
                }
            }
            catch (Ext4FormatException ex)
            {
                result.Unsupported = "damaged file system: " + ex.Message;
            }
            catch (ArgumentException ex)
            {
                result.Unsupported = "the partition lies outside the source (" + ex.Message + ")";
            }

            return result;
        }
    }
}

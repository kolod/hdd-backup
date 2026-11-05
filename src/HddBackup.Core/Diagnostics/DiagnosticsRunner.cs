using System;
using System.Linq;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;

namespace HddBackup.Core.Diagnostics
{
    /// <summary>
    /// Runs a diagnostic tool on its own read-only handle of the source (research R-2): opens the
    /// image or device, the partition's file system and a <see cref="SystemReader"/>, runs the tool
    /// and releases everything, also on failure or cancellation. It never opens a device for
    /// writing, never locks and never dismounts.
    /// </summary>
    public static class DiagnosticsRunner
    {
        public static T Run<T>(
            IDeviceAccess access,
            DiagnosticsSource source,
            Func<SystemReader, DiagnosticsSource, T> tool,
            CancellationToken cancellationToken)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }

            BrowseSource browse = null;
            Ext4FileSystemView view = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (source.Device != null)
                {
                    browse = BrowseSource.OpenDevice(access, source.Device);
                }
                else
                {
                    browse = BrowseSource.OpenImage(source.ImagePath);
                }

                var partition = browse.Browsable.FirstOrDefault(p => p.Number == source.PartitionNumber);
                if (partition == null)
                {
                    throw new ImagingException(ErrorCode.InvalidRequest, "The partition cannot be browsed.");
                }

                view = browse.OpenFileSystem(partition);

                // What the tools need to know about the rest of the source, taken from the source itself.
                var effective = new DiagnosticsSource
                {
                    ImagePath = source.ImagePath,
                    Device = source.Device,
                    PartitionNumber = source.PartitionNumber,
                    DisplayName = source.DisplayName,
                    State = source.State ?? partition.State,
                    HasUnreadBootPartition = source.HasUnreadBootPartition
                        || browse.Partitions.Any(p => p.Number != partition.Number && p.FileSystem != null && p.FileSystem.StartsWith("FAT", StringComparison.OrdinalIgnoreCase))
                };
                return tool(new SystemReader(view, cancellationToken), effective);
            }
            finally
            {
                view?.Dispose();
                browse?.Dispose();
            }
        }
    }
}

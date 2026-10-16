using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;

namespace HddBackup.Core.Imaging
{
    /// <summary>Checks the backup destination before any data is read.</summary>
    public static class DestinationChecks
    {
        public const long Fat32MaxFileSize = 4294967295L;
        private const long SidecarReserve = 64 * 1024;

        public static IReadOnlyList<ValidationWarning> Check(OperationRequest request, PathInfo destination)
        {
            var warnings = new List<ValidationWarning>();
            var device = request.Device;
            if (device == null)
            {
                throw new ImagingException(ErrorCode.InvalidRequest, "No source device selected.");
            }

            if (device.IsCapacityMisreported)
            {
                if (!request.AllowIncompleteBackup)
                {
                    throw new ImagingException(ErrorCode.UnsuitableReader, "The reader reports less capacity than the card's partitions use.");
                }

                warnings.Add(ValidationWarning.IncompleteBackup);
            }

            if (string.IsNullOrWhiteSpace(request.ImagePath))
            {
                throw new ImagingException(ErrorCode.InvalidRequest, "No image file selected.");
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(request.ImagePath));
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                throw new ImagingException(ErrorCode.InvalidRequest, "Destination folder does not exist.");
            }

            long size = device.Size;
            if (request.Kind == OperationKind.BackupPartition)
            {
                var partition = request.Partition;
                if (partition == null || partition.Offset < 0)
                {
                    throw new ImagingException(ErrorCode.InvalidRequest, "No valid source partition selected.");
                }

                if (partition.End > device.Size)
                {
                    throw new ImagingException(ErrorCode.PartitionBeyondDevice, "The partition extends beyond the capacity the device reports.");
                }

                size = partition.Length;
            }

            if (destination.DiskNumbers.Contains(device.DiskNumber))
            {
                throw new ImagingException(ErrorCode.ImageOnSource, "The image cannot be stored on the device being backed up.");
            }

            bool gzip = ImageDescriptor.IsGzipPath(request.ImagePath);
            warnings.AddRange(CheckOutputFile(request.ImagePath, size, destination));

            if (gzip && File.Exists(Path.Combine(directory, ImageDescriptor.ContentFileName(request.ImagePath))))
            {
                warnings.Add(ValidationWarning.ContentChecksumCollides);
            }

            return warnings;
        }

        /// <summary>
        /// Checks that an image file of <paramref name="size"/> uncompressed bytes (null when unknown)
        /// can be written to <paramref name="path"/>: the folder exists, a FAT file system can hold it
        /// and, for an uncompressed image of known size, there is enough free space.
        /// </summary>
        public static IReadOnlyList<ValidationWarning> CheckOutputFile(string path, long? size, PathInfo destination)
        {
            var warnings = new List<ValidationWarning>();
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                throw new ImagingException(ErrorCode.InvalidRequest, "Destination folder does not exist.");
            }

            bool gzip = ImageDescriptor.IsGzipPath(path);
            bool fat = destination.FileSystem.StartsWith("FAT", StringComparison.OrdinalIgnoreCase);
            if (fat && (size == null || size.Value > Fat32MaxFileSize))
            {
                if (!gzip && size != null)
                {
                    throw new ImagingException(ErrorCode.FileTooLargeForFs, "The destination file system cannot store files over 4 GB.");
                }

                warnings.Add(ValidationWarning.MayExceedFat32Limit);
            }

            if (!gzip && size.HasValue && destination.FreeBytes.HasValue && destination.FreeBytes.Value < size.Value + SidecarReserve)
            {
                throw new ImagingException(
                    ErrorCode.DestinationFull,
                    string.Format(CultureInfo.InvariantCulture, "{0} bytes are needed, {1} are free.", size.Value + SidecarReserve, destination.FreeBytes.Value));
            }

            return warnings;
        }
    }
}

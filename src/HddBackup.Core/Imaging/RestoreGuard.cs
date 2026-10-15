using System.Collections.Generic;
using System.Linq;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;

namespace HddBackup.Core.Imaging
{
    /// <summary>
    /// Safety rules checked before a single byte is written to a device (constitution Principle I).
    /// Violations throw; findings the user only needs to acknowledge are returned as warnings.
    /// </summary>
    public static class RestoreGuard
    {
        public static IReadOnlyList<ValidationWarning> Check(OperationRequest request, ImageDescriptor image, PathInfo imageLocation)
        {
            var warnings = new List<ValidationWarning>(image.Warnings);
            var device = request.Device;
            if (device == null)
            {
                throw new ImagingException(ErrorCode.InvalidRequest, "No target device selected.");
            }

            if (device.IsCapacityMisreported)
            {
                throw new ImagingException(ErrorCode.UnsuitableReader, "The reader reports less capacity than the card's partitions use.");
            }

            if (device.IsWriteProtected)
            {
                throw new ImagingException(ErrorCode.WriteProtected, "Target medium is write-protected.");
            }

            if (device.HostsExecutable)
            {
                throw new ImagingException(ErrorCode.TargetHostsExecutable, "The program runs from the target device.");
            }

            if (!device.IsRemovableMedia)
            {
                if (!request.ExternalDiskConfirmed)
                {
                    throw new ImagingException(ErrorCode.ExternalDiskNotConfirmed, "Writing to an external disk was not confirmed.");
                }

                warnings.Add(ValidationWarning.ExternalDisk);
            }

            if (imageLocation != null && imageLocation.DiskNumbers.Contains(device.DiskNumber))
            {
                throw new ImagingException(ErrorCode.ImageOnTarget, "The image file is stored on the target device.");
            }

            long targetSize;
            if (request.Kind == OperationKind.RestorePartition)
            {
                if (image.Kind != ImageKind.Partition)
                {
                    throw new ImagingException(ErrorCode.MissingPartitionMetadata, "The image has no partition metadata file.");
                }

                var partition = request.Partition;
                if (partition == null || partition.Offset < 0 || partition.End > device.Size)
                {
                    throw new ImagingException(ErrorCode.InvalidRequest, "No valid target partition selected.");
                }

                targetSize = partition.Length;
            }
            else if (request.Kind == OperationKind.RestoreDevice)
            {
                if (image.Kind == ImageKind.Partition)
                {
                    throw new ImagingException(ErrorCode.InvalidRequest, "A partition image can only be restored into a partition.");
                }

                targetSize = device.Size;
            }
            else
            {
                throw new ImagingException(ErrorCode.InvalidRequest, "Not a restore request.");
            }

            if (image.DataSize.HasValue)
            {
                if (image.DataSize.Value > targetSize)
                {
                    throw new ImagingException(ErrorCode.SizeMismatch, "The image is larger than the target.");
                }

                if (image.DataSize.Value < targetSize)
                {
                    warnings.Add(ValidationWarning.TargetLarger);
                }
            }
            else
            {
                warnings.Add(ValidationWarning.ImageSizeUnknown);
            }

            return warnings.Distinct().ToList();
        }
    }
}

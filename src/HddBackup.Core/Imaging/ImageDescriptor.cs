using System;
using System.Collections.Generic;
using System.IO;
using HddBackup.Core.Errors;
using HddBackup.Core.Formats;

namespace HddBackup.Core.Imaging
{
    public enum ImageFormat
    {
        Raw,
        Gzip
    }

    public enum ImageKind
    {
        FullDevice,
        Partition
    }

    /// <summary>An image file together with its sidecars (checksums, partition metadata).</summary>
    public sealed class ImageDescriptor
    {
        private const string GzipExtension = ".gz";

        public string Path { get; private set; }

        public ImageFormat Format { get; private set; }

        public ImageKind Kind { get; private set; }

        /// <summary>Uncompressed size when known (raw file length, or partition metadata length).</summary>
        public long? DataSize { get; private set; }

        public long FileSize { get; private set; }

        /// <summary>
        /// Uncompressed size for progress: <see cref="DataSize"/> when known, otherwise for a gzip image of
        /// a whole device an estimate from its partition table and gzip trailer; null when unknown.
        /// </summary>
        public long? ProgressSize { get; private set; }

        /// <summary><c>disk.img.gz.sha256</c> for gzip images, <c>disk.img.sha256</c> for raw images.</summary>
        public ChecksumFile FileChecksum { get; private set; }

        /// <summary><c>disk.img.sha256</c> next to a gzip image; same as <see cref="FileChecksum"/> for raw.</summary>
        public ChecksumFile ContentChecksum { get; private set; }

        public PartitionMetadata PartitionMetadata { get; private set; }

        public IReadOnlyList<ValidationWarning> Warnings { get; private set; } = Array.Empty<ValidationWarning>();

        public bool HasAnyChecksum => FileChecksum != null || ContentChecksum != null;

        public static bool IsGzipPath(string path)
        {
            return path.EndsWith(GzipExtension, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Sidecar holding the checksum of the image file itself.</summary>
        public static string FileSidecarPath(string imagePath)
        {
            return imagePath + ChecksumFile.Extension;
        }

        /// <summary>Sidecar holding the checksum of the uncompressed content of a gzip image.</summary>
        public static string ContentSidecarPath(string imagePath)
        {
            return ContentName(imagePath, System.IO.Path.GetDirectoryName(imagePath) ?? string.Empty) + ChecksumFile.Extension;
        }

        /// <summary>The uncompressed image name a gzip image stands for (disk.img.gz → disk.img).</summary>
        public static string ContentFileName(string imagePath)
        {
            return System.IO.Path.GetFileName(ContentName(imagePath, string.Empty));
        }

        public static ImageDescriptor Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                throw new ImagingException(ErrorCode.InvalidRequest, "Image file not found: " + path);
            }

            var warnings = new List<ValidationWarning>();
            var descriptor = new ImageDescriptor
            {
                Path = path,
                FileSize = new FileInfo(path).Length,
                Format = HasGzipMagic(path) ? ImageFormat.Gzip : ImageFormat.Raw
            };

            var metadataPath = PartitionMetadataFile.PathFor(path);
            if (File.Exists(metadataPath))
            {
                descriptor.Kind = ImageKind.Partition;
                descriptor.PartitionMetadata = PartitionMetadataFile.Read(metadataPath);
            }

            descriptor.DataSize = descriptor.Format == ImageFormat.Raw
                ? descriptor.FileSize
                : descriptor.PartitionMetadata?.Length;
            descriptor.ProgressSize = descriptor.DataSize ?? EstimateGzipSize(path);

            string fileName = System.IO.Path.GetFileName(path);
            descriptor.FileChecksum = TryReadChecksum(FileSidecarPath(path), ChecksumKind.File, fileName, warnings);
            if (descriptor.Format == ImageFormat.Gzip)
            {
                descriptor.ContentChecksum = TryReadChecksum(
                    ContentSidecarPath(path), ChecksumKind.Content, ContentFileName(path), warnings);
            }
            else
            {
                descriptor.ContentChecksum = descriptor.FileChecksum;
            }

            if (!descriptor.HasAnyChecksum)
            {
                warnings.Add(ValidationWarning.NoStoredChecksum);
            }

            descriptor.Warnings = warnings;
            return descriptor;
        }

        private static string ContentName(string imagePath, string directory)
        {
            string name = System.IO.Path.GetFileName(imagePath);
            string content = IsGzipPath(name) ? name.Substring(0, name.Length - GzipExtension.Length) : name + ".content";
            return directory.Length == 0 ? content : System.IO.Path.Combine(directory, content);
        }

        private static long? EstimateGzipSize(string path)
        {
            try
            {
                return ImageHead.EstimateGzipSize(path, ImageHead.RequiredDeviceBytes(path));
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ImagingException)
            {
                return null; // only used for progress
            }
        }

        private static bool HasGzipMagic(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return stream.Length >= 18 && stream.ReadByte() == 0x1F && stream.ReadByte() == 0x8B;
            }
        }

        private static ChecksumFile TryReadChecksum(string path, ChecksumKind kind, string expectedLabel, List<ValidationWarning> warnings)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var file = ChecksumFile.Read(path, kind);
                if (!string.Equals(file.Label, expectedLabel, StringComparison.OrdinalIgnoreCase)
                    && !warnings.Contains(ValidationWarning.ChecksumLabelMismatch))
                {
                    warnings.Add(ValidationWarning.ChecksumLabelMismatch);
                }

                return file;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
            {
                if (!warnings.Contains(ValidationWarning.ChecksumUnreadable))
                {
                    warnings.Add(ValidationWarning.ChecksumUnreadable);
                }

                return null;
            }
        }
    }
}

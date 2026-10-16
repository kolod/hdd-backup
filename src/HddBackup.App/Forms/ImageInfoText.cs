using System;
using System.Collections.Generic;
using System.IO;
using HddBackup.App.Localization;
using HddBackup.Core.Imaging;

namespace HddBackup.App.Forms
{
    /// <summary>Description of the selected image (format, size, kind, checksum files) shown on the Restore and Image tabs.</summary>
    internal static class ImageInfoText
    {
        public static string For(SelectedImage selected)
        {
            if (selected.LoadError != null)
            {
                return Strings.Format("Restore_ImageError", selected.LoadError);
            }

            var image = selected.Descriptor;
            if (image == null)
            {
                return Strings.Get("Restore_NoImage");
            }

            string format = Strings.Get(image.Format == ImageFormat.Gzip ? "ImageFormat_Gzip" : "ImageFormat_Raw");
            string size = image.DataSize.HasValue ? Formatters.Bytes(image.DataSize.Value) : Strings.Get("Restore_SizeUnknown");
            string kind = image.Kind == ImageKind.Partition
                ? Strings.Format("ImageKind_Partition", image.PartitionMetadata.PartitionNumber, image.PartitionMetadata.FileSystem)
                : Strings.Get("ImageKind_Full");

            var sidecars = new List<string>();
            if (image.FileChecksum != null)
            {
                sidecars.Add(Path.GetFileName(image.FileChecksum.Path));
            }

            if (image.ContentChecksum != null && !ReferenceEquals(image.ContentChecksum, image.FileChecksum))
            {
                sidecars.Add(Path.GetFileName(image.ContentChecksum.Path));
            }

            string checksums = sidecars.Count > 0
                ? Strings.Format("Restore_ChecksumsFound", string.Join(", ", sidecars))
                : Strings.Get("Restore_ChecksumsNone");
            return Strings.Format("Restore_ImageInfo", format, size, kind) + Environment.NewLine + checksums;
        }
    }
}

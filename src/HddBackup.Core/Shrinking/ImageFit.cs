namespace HddBackup.Core.Shrinking
{
    public enum ImageFitResult
    {
        /// <summary>The image can be written as it is.</summary>
        Fits,

        /// <summary>Larger than the device; the shrink analysis has not finished yet.</summary>
        Pending,

        /// <summary>Larger than the device, but its last ext4 partition can be shrunk to fit.</summary>
        ShrinkToFit,

        /// <summary>Larger than the device and cannot be shrunk to fit.</summary>
        CannotFit
    }

    /// <summary>
    /// Whether a full-device image fits a device, and if not, whether shrinking helps. One rule for
    /// the Restore tab warning and the "shrink and write" offer.
    /// </summary>
    public static class ImageFit
    {
        /// <param name="isRaw">Uncompressed image (its file size is known).</param>
        /// <param name="fileSize">Size of the image file in bytes (used for raw images).</param>
        /// <param name="requiredBytes">End of the last partition (+ backup GPT); 0 when unknown.</param>
        /// <param name="plan">Finished shrink analysis, or null when not available yet.</param>
        /// <param name="analysisFailed">The analysis ended with an error.</param>
        /// <param name="deviceBytes">Size of the target device.</param>
        public static ImageFitResult Evaluate(bool isRaw, long fileSize, long requiredBytes, ShrinkPlan plan, bool analysisFailed, long deviceBytes)
        {
            if (isRaw)
            {
                // Only unused space after the last partition past the device end is acceptable.
                if (fileSize <= deviceBytes || (requiredBytes > 0 && requiredBytes <= deviceBytes))
                {
                    return ImageFitResult.Fits;
                }
            }
            else if (requiredBytes <= deviceBytes)
            {
                return ImageFitResult.Fits;
            }

            if (analysisFailed)
            {
                return ImageFitResult.CannotFit;
            }

            if (plan == null)
            {
                return ImageFitResult.Pending;
            }

            return plan.Fits(deviceBytes) ? ImageFitResult.ShrinkToFit : ImageFitResult.CannotFit;
        }
    }
}

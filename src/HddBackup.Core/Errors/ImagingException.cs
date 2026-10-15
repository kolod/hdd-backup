using System;
using System.Globalization;

namespace HddBackup.Core.Errors
{
    /// <summary>
    /// Machine-readable failure causes. The UI maps each code to a localized message.
    /// </summary>
    public enum ErrorCode
    {
        None = 0,
        DeviceRemoved,
        ReadError,
        WriteError,
        DestinationFull,
        FileTooLargeForFs,
        CorruptImage,
        WriteProtected,
        LockFailed,
        SizeMismatch,
        VerifyMismatch,
        ChecksumMismatch,
        EjectVetoed,
        ImageOnTarget,
        ImageOnSource,
        TargetHostsExecutable,
        ExternalDiskNotConfirmed,
        MissingPartitionMetadata,
        UnsupportedMetadata,
        DeviceChanged,
        PartitionBeyondDevice,
        UnsuitableReader,
        InvalidRequest,
        Unexpected
    }

    /// <summary>
    /// Expected imaging failure. <see cref="Offset"/> is the absolute byte offset at which the
    /// failing I/O started, when known.
    /// </summary>
    [Serializable]
    public sealed class ImagingException : Exception
    {
        public ImagingException(ErrorCode code, string detail, long? offset = null, Exception innerException = null)
            : base(BuildMessage(code, detail, offset), innerException)
        {
            Code = code;
            Detail = detail ?? string.Empty;
            Offset = offset;
        }

        public ErrorCode Code { get; }

        public long? Offset { get; }

        /// <summary>English technical detail for logs; never shown untranslated as the main message.</summary>
        public string Detail { get; }

        private static string BuildMessage(ErrorCode code, string detail, long? offset)
        {
            return offset.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "{0} at offset {1}: {2}", code, offset.Value, detail)
                : string.Format(CultureInfo.InvariantCulture, "{0}: {1}", code, detail);
        }
    }
}

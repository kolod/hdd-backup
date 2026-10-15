using System;

namespace HddBackup.Core.Formats
{
    public enum VerificationStatus
    {
        Match,
        Mismatch,
        NotAvailable
    }

    /// <summary>Outcome of comparing one computed checksum with its sidecar.</summary>
    public sealed class VerificationResult
    {
        public VerificationResult(ChecksumKind kind, string expected, string actual, VerificationStatus status, string sidecarPath)
        {
            Kind = kind;
            Expected = expected;
            Actual = actual;
            Status = status;
            SidecarPath = sidecarPath;
        }

        public ChecksumKind Kind { get; }

        /// <summary>Stored value; null when no sidecar was available.</summary>
        public string Expected { get; }

        public string Actual { get; }

        public VerificationStatus Status { get; }

        public string SidecarPath { get; }

        public static VerificationResult Compare(ChecksumKind kind, ChecksumFile expected, string actual)
        {
            if (expected == null)
            {
                return new VerificationResult(kind, null, actual, VerificationStatus.NotAvailable, null);
            }

            var status = string.Equals(expected.Sha256, actual, StringComparison.OrdinalIgnoreCase)
                ? VerificationStatus.Match
                : VerificationStatus.Mismatch;
            return new VerificationResult(kind, expected.Sha256, actual, status, expected.Path);
        }
    }
}

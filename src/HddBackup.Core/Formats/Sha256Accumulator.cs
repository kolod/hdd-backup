using System;
using System.Security.Cryptography;
using System.Text;

namespace HddBackup.Core.Formats
{
    /// <summary>Incremental SHA-256 over data fed in chunks, finished as lowercase hex.</summary>
    public sealed class Sha256Accumulator : IDisposable
    {
        private readonly SHA256 _algorithm = CreateAlgorithm();
        private string _hex;

        public long Length { get; private set; }

        public void Append(byte[] buffer, int offset, int count)
        {
            if (_hex != null)
            {
                throw new InvalidOperationException("Hash already finished.");
            }

            if (count > 0)
            {
                _algorithm.TransformBlock(buffer, offset, count, null, 0);
                Length += count;
            }
        }

        public string Finish()
        {
            if (_hex == null)
            {
                _algorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                _hex = ToHex(_algorithm.Hash);
            }

            return _hex;
        }

        public void Dispose() => _algorithm.Dispose();

        public static string ToHex(byte[] hash)
        {
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }

        private static SHA256 CreateAlgorithm()
        {
            // The CNG implementation is several times faster than SHA256Managed and exists on Windows 7.
            try
            {
                return new SHA256Cng();
            }
            catch (PlatformNotSupportedException)
            {
                return SHA256.Create();
            }
            catch (CryptographicException)
            {
                return SHA256.Create();
            }
        }
    }
}

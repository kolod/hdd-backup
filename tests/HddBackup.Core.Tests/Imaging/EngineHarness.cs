using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using HddBackup.Core.Devices;
using HddBackup.Core.Formats;
using HddBackup.Core.Imaging;
using HddBackup.Core.Tests.Fakes;

namespace HddBackup.Core.Tests.Imaging
{
    /// <summary>Shared helpers: small buffers so multi-chunk paths run on small fake devices.</summary>
    internal static class EngineHarness
    {
        public const int BufferSize = 256 * 1024;

        public static ImagingEngine NewEngine(IDeviceAccess access) => new ImagingEngine(access, BufferSize, 4);

        public static OperationResult Run(IDeviceAccess access, OperationRequest request, CancellationToken token = default(CancellationToken))
        {
            return NewEngine(access).RunAsync(request, token).GetAwaiter().GetResult();
        }

        public static OperationResult Backup(FakeDeviceAccess access, DeviceInfo device, string imagePath, bool verify = false)
        {
            return Run(access, new OperationRequest
            {
                Kind = OperationKind.BackupDevice,
                Device = device,
                ImagePath = imagePath,
                VerifyAfter = verify,
                Compression = CompressionChoice.Fastest
            });
        }

        public static OperationResult Restore(FakeDeviceAccess access, DeviceInfo target, string imagePath, bool verify = true)
        {
            return Run(access, new OperationRequest
            {
                Kind = OperationKind.RestoreDevice,
                Device = target,
                ImagePath = imagePath,
                VerifyAfter = verify
            });
        }

        public static byte[] ReadImageContent(string path)
        {
            using (var file = File.OpenRead(path))
            {
                Stream stream = file;
                if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                {
                    stream = new GZipStream(file, CompressionMode.Decompress);
                }

                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        public static string Sha(byte[] data, int offset = 0, int? count = null)
        {
            using (var sha = SHA256.Create())
            {
                return Sha256Accumulator.ToHex(sha.ComputeHash(data, offset, count ?? (data.Length - offset)));
            }
        }

        public static string ShaOfFile(string path) => Sha(File.ReadAllBytes(path));

        /// <summary>Writes raw content as a gzip file made of several members (like pigz output).</summary>
        public static void WriteMultiMemberGzip(string path, byte[] content, int members)
        {
            using (var file = File.Create(path))
            {
                int per = content.Length / members;
                for (int i = 0; i < members; i++)
                {
                    int start = i * per;
                    int count = i == members - 1 ? content.Length - start : per;
                    using (var gz = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
                    {
                        gz.Write(content, start, count);
                    }
                }
            }
        }
    }
}

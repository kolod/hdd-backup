using System;
using System.Linq;
using HddBackup.Core.Devices;
using HddBackup.Core.Native;
using Xunit;

namespace HddBackup.Core.Tests.Hardware
{
    /// <summary>
    /// Read-only check of the SCSI pass-through code against a real disk. Runs only when the
    /// environment variable HDDBACKUP_PT_DISK holds a disk number (requires administrator rights).
    /// </summary>
    public class PassThroughSmokeTests
    {
        [Fact]
        [Trait("Category", "Hardware")]
        public void PassThroughRead_MatchesNormalRead()
        {
            var value = Environment.GetEnvironmentVariable("HDDBACKUP_PT_DISK");
            if (string.IsNullOrEmpty(value))
            {
                return; // not configured on this machine
            }

            int disk = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            using (var handle = DiskIoctl.Open(PhysicalDiskDevice.PathFor(disk), NativeMethods.GenericRead | NativeMethods.GenericWrite))
            {
                Assert.False(handle.IsInvalid, "cannot open disk");
                Assert.True(ScsiPassThrough.ReadCapacity10(handle, out long lastLba, out int block, out string error), error);
                Assert.True(block == 512 || block == 4096);

                using (var device = PhysicalDiskDevice.Open(disk, writable: false))
                {
                    long expectedSectors = device.Size / block;
                    Assert.True(lastLba + 1 == expectedSectors || lastLba == 0xFFFFFFFF, $"capacity {lastLba + 1} vs {expectedSectors}");

                    foreach (long lba in new long[] { 0, 2048, Math.Min(lastLba, expectedSectors - 1) - 7 })
                    {
                        var viaPt = new byte[8 * block];
                        var viaDisk = new byte[8 * block];
                        Assert.True(ScsiPassThrough.Read10(handle, lba, 8, block, viaPt, out error), error);
                        device.Read(lba * block, viaDisk, 0, viaDisk.Length);
                        Assert.True(viaPt.SequenceEqual(viaDisk), "data differs at LBA " + lba);
                    }
                }
            }
        }
    }
}

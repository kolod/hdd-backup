using System;
using HddBackup.Core.Devices;

namespace HddBackup.Core.Partitioning
{
    /// <summary>Best-effort file system detection from boot sector / superblock signatures.</summary>
    public static class FileSystemProbe
    {
        public const string UnknownName = "unknown";
        private const int ProbeBytes = 4096;

        /// <summary>Returns the file system name, or null if no known signature is found.</summary>
        public static string Detect(IBlockDevice device, long offset, long length)
        {
            var data = ReadStart(device, offset, length);
            return data == null ? null : Detect(data);
        }

        /// <summary>Reads the first bytes of a partition (boot sector and ext superblock), or null if unreadable.</summary>
        public static byte[] ReadStart(IBlockDevice device, long offset, long length)
        {
            int sectorSize = device.SectorSize;
            if (offset < 0 || offset >= device.Size)
            {
                return null;
            }

            long available = Math.Min(length, device.Size - offset);
            int count = (int)Math.Min(Math.Max(ProbeBytes, sectorSize), available);
            count -= count % sectorSize;
            if (count <= 0)
            {
                return null;
            }

            var data = new byte[count];
            device.Read(offset, data, 0, count);
            return data;
        }

        /// <summary>
        /// Size in bytes the file system itself occupies, from its boot sector or superblock;
        /// null when unknown. Used to tell a sloppy partition entry (file system fits on the
        /// device) from a reader that hides part of the card (file system does not fit).
        /// </summary>
        public static long? DetectSize(byte[] data)
        {
            string fs = Detect(data);
            if (fs == null)
            {
                return null;
            }

            if (fs.StartsWith("ext", StringComparison.Ordinal))
            {
                const int super = 1024;
                if (data.Length < super + 344)
                {
                    return null;
                }

                long blocks = BinaryHelpers.U32(data, super + 4);
                uint logBlock = BinaryHelpers.U32(data, super + 24);
                uint incompat = BinaryHelpers.U32(data, super + 96);
                if ((incompat & 0x80) != 0)
                {
                    blocks |= (long)BinaryHelpers.U32(data, super + 0x150) << 32;
                }

                return logBlock <= 6 ? blocks * (1024L << (int)logBlock) : (long?)null;
            }

            if (fs == "exFAT")
            {
                int shift = data[0x6C];
                long sectors = BinaryHelpers.U64(data, 0x48);
                return shift >= 9 && shift <= 12 && sectors > 0 ? sectors << shift : (long?)null;
            }

            int bytesPerSector = data[0x0B] | (data[0x0C] << 8);
            if (bytesPerSector != 512 && bytesPerSector != 1024 && bytesPerSector != 2048 && bytesPerSector != 4096)
            {
                return null;
            }

            if (fs == "NTFS")
            {
                long sectors = BinaryHelpers.U64(data, 0x28);
                return sectors > 0 ? (sectors + 1) * bytesPerSector : (long?)null; // + backup boot sector
            }

            // FAT12/16/32: 16-bit total, or 32-bit total when the 16-bit field is zero.
            long total = data[0x13] | (data[0x14] << 8);
            if (total == 0)
            {
                total = BinaryHelpers.U32(data, 0x20);
            }

            return total > 0 ? total * bytesPerSector : (long?)null;
        }

        public static string Detect(byte[] data)
        {
            if (BinaryHelpers.AsciiAt(data, 3, "NTFS    "))
            {
                return "NTFS";
            }

            if (BinaryHelpers.AsciiAt(data, 3, "EXFAT   "))
            {
                return "exFAT";
            }

            if (BinaryHelpers.AsciiAt(data, 82, "FAT32   "))
            {
                return "FAT32";
            }

            if (BinaryHelpers.AsciiAt(data, 54, "FAT16   "))
            {
                return "FAT16";
            }

            if (BinaryHelpers.AsciiAt(data, 54, "FAT12   "))
            {
                return "FAT12";
            }

            if (BinaryHelpers.AsciiAt(data, 54, "FAT     "))
            {
                return "FAT";
            }

            const int super = 1024;
            if (data.Length >= super + 100 && data[super + 56] == 0x53 && data[super + 57] == 0xEF)
            {
                uint compat = BinaryHelpers.U32(data, super + 92);
                uint incompat = BinaryHelpers.U32(data, super + 96);
                if ((incompat & 0x40) != 0)
                {
                    return "ext4";
                }

                return (compat & 0x4) != 0 ? "ext3" : "ext2";
            }

            return null;
        }
    }
}

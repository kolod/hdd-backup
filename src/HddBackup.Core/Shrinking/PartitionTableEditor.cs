using System;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Shrinking
{
    /// <summary>
    /// Changes the length of the last partition in an image's MBR or GPT (with protective MBR,
    /// primary and backup headers, entry arrays and CRCs). Nothing else is modified.
    /// </summary>
    internal static class PartitionTableEditor
    {
        private const int Sector = 512;

        /// <summary>Byte ranges at the image start that may differ from the source after editing.</summary>
        public static bool IsTableArea(long offset, TableStyle style)
        {
            return style == TableStyle.Gpt ? offset < 34L * Sector : offset < Sector;
        }

        public static void ResizeLastPartition(IBlockDevice image, TableStyle style, long partitionOffset, long newLength)
        {
            if (style == TableStyle.Mbr)
            {
                ResizeMbr(image, partitionOffset, newLength);
            }
            else if (style == TableStyle.Gpt)
            {
                ResizeGpt(image, partitionOffset, newLength);
            }
            else
            {
                throw new ImagingException(ErrorCode.ShrinkNotPossible, "Unsupported partition table.");
            }
        }

        private static void ResizeMbr(IBlockDevice image, long partitionOffset, long newLength)
        {
            var mbr = new byte[Sector];
            image.Read(0, mbr, 0, Sector);
            for (int slot = 0; slot < 4; slot++)
            {
                int o = 446 + (slot * 16);
                if (mbr[o + 4] != 0 && BinaryHelpers.U32(mbr, o + 8) == partitionOffset / Sector)
                {
                    BinaryHelpers.PutU32(mbr, o + 12, (uint)(newLength / Sector));
                    image.Write(0, mbr, 0, Sector);
                    return;
                }
            }

            throw new ImagingException(ErrorCode.ShrinkNotPossible, "Root partition entry not found in the MBR.");
        }

        private static void ResizeGpt(IBlockDevice image, long partitionOffset, long newLength)
        {
            long diskSectors = image.Size / Sector;
            long lastLba = diskSectors - 1;

            // Protective MBR covers the whole (new) disk.
            var mbr = new byte[Sector];
            image.Read(0, mbr, 0, Sector);
            BinaryHelpers.PutU32(mbr, 446 + 12, (uint)Math.Min(diskSectors - 1, uint.MaxValue));
            image.Write(0, mbr, 0, Sector);

            var header = new byte[Sector];
            image.Read(Sector, header, 0, Sector);
            if (!BinaryHelpers.AsciiAt(header, 0, "EFI PART"))
            {
                throw new ImagingException(ErrorCode.ShrinkNotPossible, "Primary GPT header not found.");
            }

            int headerSize = (int)BinaryHelpers.U32(header, 12);
            long entriesLba = BinaryHelpers.U64(header, 0x48);
            int count = (int)BinaryHelpers.U32(header, 0x50);
            int entrySize = (int)BinaryHelpers.U32(header, 0x54);
            int arrayBytes = count * entrySize;
            int arraySectors = (arrayBytes + Sector - 1) / Sector;
            var array = new byte[arraySectors * Sector];
            image.Read(entriesLba * Sector, array, 0, array.Length);

            bool found = false;
            for (int i = 0; i < count; i++)
            {
                int o = i * entrySize;
                if (BinaryHelpers.U64(array, o + 32) == partitionOffset / Sector && !IsZero(array, o, 16))
                {
                    BinaryHelpers.PutU64(array, o + 40, ((partitionOffset + newLength) / Sector) - 1);
                    found = true;
                }
            }

            if (!found)
            {
                throw new ImagingException(ErrorCode.ShrinkNotPossible, "Root partition entry not found in the GPT.");
            }

            uint arrayCrc = Crc32.Compute(array, 0, arrayBytes);
            long backupArrayLba = lastLba - arraySectors;
            long lastUsable = backupArrayLba - 1;
            if ((partitionOffset + newLength) / Sector - 1 > lastUsable)
            {
                throw new ImagingException(ErrorCode.ShrinkNotPossible, "No room for the backup GPT.");
            }

            BinaryHelpers.PutU64(header, 0x20, lastLba);
            BinaryHelpers.PutU64(header, 0x30, lastUsable);
            BinaryHelpers.PutU32(header, 0x58, arrayCrc);
            WriteHeader(image, header, headerSize, 1);
            image.Write(entriesLba * Sector, array, 0, array.Length);

            var backup = (byte[])header.Clone();
            BinaryHelpers.PutU64(backup, 0x18, lastLba);
            BinaryHelpers.PutU64(backup, 0x20, 1);
            BinaryHelpers.PutU64(backup, 0x48, backupArrayLba);
            image.Write(backupArrayLba * Sector, array, 0, array.Length);
            WriteHeader(image, backup, headerSize, lastLba);
        }

        private static void WriteHeader(IBlockDevice image, byte[] header, int headerSize, long lba)
        {
            BinaryHelpers.PutU32(header, 0x10, 0);
            BinaryHelpers.PutU32(header, 0x10, Crc32.Compute(header, 0, headerSize));
            image.Write(lba * Sector, header, 0, Sector);
        }

        private static bool IsZero(byte[] data, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++)
            {
                if (data[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }
}

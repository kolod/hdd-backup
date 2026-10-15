namespace HddBackup.Core.Ext4
{
    /// <summary>
    /// CRC-32C (Castagnoli) as used by ext4 metadata_csum and jbd2 v3. <see cref="Append"/> is the
    /// raw update without pre/post inversion, exactly like the kernel's crc32c(seed, data, len).
    /// </summary>
    public static class Crc32c
    {
        private static readonly uint[] Table = BuildTable(0x82F63B78u);

        public static uint Append(uint crc, byte[] data, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++)
            {
                crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }

        public static uint AppendU32(uint crc, uint value)
        {
            crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            crc = Table[(crc ^ (value >> 8)) & 0xFF] ^ (crc >> 8);
            crc = Table[(crc ^ (value >> 16)) & 0xFF] ^ (crc >> 8);
            return Table[(crc ^ (value >> 24)) & 0xFF] ^ (crc >> 8);
        }

        public static uint AppendU64(uint crc, ulong value)
        {
            return AppendU32(AppendU32(crc, (uint)value), (uint)(value >> 32));
        }

        /// <summary>Standard CRC-32C (initial ~0, final inversion), for test vectors.</summary>
        public static uint Compute(byte[] data) => ~Append(~0u, data, 0, data.Length);

        internal static uint[] BuildTable(uint polynomial)
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? polynomial ^ (c >> 1) : c >> 1;
                }

                table[i] = c;
            }

            return table;
        }
    }

    /// <summary>CRC-16 (polynomial 0x8005, reflected) used by the ext4 uninit_bg group checksums.</summary>
    public static class Crc16
    {
        private static readonly ushort[] Table = BuildTable();

        public static ushort Append(ushort crc, byte[] data, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++)
            {
                crc = (ushort)((crc >> 8) ^ Table[(crc ^ data[i]) & 0xFF]);
            }

            return crc;
        }

        private static ushort[] BuildTable()
        {
            var table = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                int c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xA001 ^ (c >> 1) : c >> 1;
                }

                table[i] = (ushort)c;
            }

            return table;
        }
    }
}

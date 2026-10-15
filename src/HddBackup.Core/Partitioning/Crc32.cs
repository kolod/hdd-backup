namespace HddBackup.Core.Partitioning
{
    /// <summary>CRC-32 (IEEE 802.3, reflected), as used by GPT headers and gzip trailers.</summary>
    public static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(byte[] data, int offset, int count)
        {
            return Append(0, data, offset, count);
        }

        /// <summary>Continues a CRC computed over preceding data (pass 0 to start).</summary>
        public static uint Append(uint crc, byte[] data, int offset, int count)
        {
            uint c = ~crc;
            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                c = Table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            }

            return ~c;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[i] = c;
            }

            return table;
        }
    }
}

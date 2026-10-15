namespace HddBackup.Core.Partitioning
{
    /// <summary>Little-endian field access for on-disk structures.</summary>
    internal static class BinaryHelpers
    {
        public static ushort U16(byte[] b, int o)
        {
            return (ushort)(b[o] | (b[o + 1] << 8));
        }

        public static void PutU16(byte[] b, int o, int v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
        }

        public static uint U32BE(byte[] b, int o)
        {
            return (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
        }

        public static void PutU32BE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }

        public static uint U32(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        public static long U64(byte[] b, int o)
        {
            return (long)(U32(b, o) | ((ulong)U32(b, o + 4) << 32));
        }

        public static void PutU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        public static void PutU64(byte[] b, int o, long v)
        {
            PutU32(b, o, (uint)v);
            PutU32(b, o + 4, (uint)((ulong)v >> 32));
        }

        public static bool AsciiAt(byte[] b, int o, string text)
        {
            if (o + text.Length > b.Length)
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (b[o + i] != text[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}

using System.Text;
using HddBackup.Core.Ext4;
using Xunit;

namespace HddBackup.Core.Tests.Ext4
{
    public class Crc32cTests
    {
        [Fact]
        public void StandardVectors()
        {
            Assert.Equal(0xE3069283u, Crc32c.Compute(Encoding.ASCII.GetBytes("123456789")));
            Assert.Equal(0x8A9136AAu, Crc32c.Compute(new byte[32]));
            var ones = new byte[32];
            for (int i = 0; i < 32; i++)
            {
                ones[i] = 0xFF;
            }

            Assert.Equal(0x62A8AB43u, Crc32c.Compute(ones));
        }

        [Fact]
        public void AppendIsIncremental_AndU32MatchesBytes()
        {
            var data = Encoding.ASCII.GetBytes("hello ext4 metadata");
            uint whole = Crc32c.Append(~0u, data, 0, data.Length);
            uint parts = Crc32c.Append(Crc32c.Append(~0u, data, 0, 5), data, 5, data.Length - 5);
            Assert.Equal(whole, parts);

            Assert.Equal(Crc32c.Append(7u, new byte[] { 0x78, 0x56, 0x34, 0x12 }, 0, 4), Crc32c.AppendU32(7u, 0x12345678));
        }

        [Fact]
        public void Crc16Arc_Vector()
        {
            Assert.Equal(0xBB3D, Crc16.Append(0, Encoding.ASCII.GetBytes("123456789"), 0, 9));
        }
    }
}

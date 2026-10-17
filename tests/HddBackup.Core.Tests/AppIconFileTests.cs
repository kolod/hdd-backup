using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HddBackup.Core.Partitioning;
using Xunit;

namespace HddBackup.Core.Tests
{
    /// <summary>Feature 007: the committed application icon has every size Windows uses.</summary>
    public class AppIconFileTests
    {
        private static string IconPath()
        {
            for (var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "HddBackup.sln")))
                {
                    return Path.Combine(dir.FullName, "src", "HddBackup.App", "app.ico");
                }
            }

            throw new FileNotFoundException("HddBackup.sln not found above the test directory.");
        }

        [Fact]
        public void Icon_HasAllSizes_BmpUpTo64_Png256()
        {
            var data = File.ReadAllBytes(IconPath());
            Assert.True(data.Length < 100 * 1024, "app.ico is " + data.Length + " bytes");
            Assert.Equal(0, BinaryHelpers.U16(data, 0)); // reserved
            Assert.Equal(1, BinaryHelpers.U16(data, 2)); // icon
            int count = BinaryHelpers.U16(data, 4);

            var sizes = new List<int>();
            for (int i = 0; i < count; i++)
            {
                int entry = 6 + (i * 16);
                int size = data[entry] == 0 ? 256 : data[entry];
                int offset = (int)BinaryHelpers.U32(data, entry + 12);
                sizes.Add(size);
                bool png = data[offset] == 0x89 && data[offset + 1] == 0x50 && data[offset + 2] == 0x4E && data[offset + 3] == 0x47;
                if (size == 256)
                {
                    Assert.True(png, "256 px entry must be PNG");
                }
                else
                {
                    Assert.False(png, size + " px entry must be BMP");
                    Assert.Equal(40u, BinaryHelpers.U32(data, offset));      // BITMAPINFOHEADER
                    Assert.Equal(32, BinaryHelpers.U16(data, offset + 14)); // bit count
                }
            }

            Assert.Equal(new[] { 16, 20, 24, 32, 40, 48, 64, 256 }, sizes.OrderBy(s => s).ToArray());
        }
    }
}

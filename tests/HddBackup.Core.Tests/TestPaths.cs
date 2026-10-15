using System;
using System.IO;

namespace HddBackup.Core.Tests
{
    internal static class TestPaths
    {
        public static string NewTempDirectory()
        {
            var dir = Path.Combine(Path.GetTempPath(), "HddBackupTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}

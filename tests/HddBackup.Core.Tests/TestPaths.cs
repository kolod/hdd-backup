using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace HddBackup.Core.Tests
{
    /// <summary>
    /// Temporary directories for tests. Each test run gets its own "run-&lt;pid&gt;" folder; folders of
    /// runs that are no longer alive are deleted at startup, so images from earlier runs do not
    /// pile up (shrink tests create hundreds of MB per run).
    /// </summary>
    internal static class TestPaths
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "HddBackupTests");
        private static readonly string RunRoot = CreateRunRoot();

        public static string NewTempDirectory()
        {
            var dir = Path.Combine(RunRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string CreateRunRoot()
        {
            Directory.CreateDirectory(Root);
            foreach (var dir in Directory.GetDirectories(Root))
            {
                string name = Path.GetFileName(dir);
                bool stale = !name.StartsWith("run-", StringComparison.Ordinal)
                    || !int.TryParse(name.Substring(4), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                    || !IsAlive(pid);
                if (stale)
                {
                    try
                    {
                        Directory.Delete(dir, true);
                    }
                    catch (IOException)
                    {
                        // Still in use; it is removed by a later run.
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }

            var run = Path.Combine(Root, "run-" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(run);
            return run;
        }

        private static bool IsAlive(int pid)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    return !process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return true; // another user's process: leave its folder alone
            }
        }
    }
}

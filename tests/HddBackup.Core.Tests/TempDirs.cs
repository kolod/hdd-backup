using System;
using System.Collections.Generic;

namespace HddBackup.Core.Tests
{
    /// <summary>
    /// Temporary directories owned by one test. Test classes hold an instance and dispose it;
    /// xUnit creates a class instance per test and disposes it afterwards, so the directories
    /// are deleted as soon as the test ends. Used where tests write hundreds of MB, to keep the
    /// peak disk usage of a run low (CI runners have limited space).
    /// </summary>
    internal sealed class TempDirs : IDisposable
    {
        private readonly List<string> _dirs = new List<string>();

        public string New()
        {
            var dir = TestPaths.NewTempDirectory();
            lock (_dirs)
            {
                _dirs.Add(dir);
            }

            return dir;
        }

        public void Dispose()
        {
            lock (_dirs)
            {
                foreach (var dir in _dirs)
                {
                    TestPaths.DeleteQuietly(dir);
                }

                _dirs.Clear();
            }
        }
    }
}

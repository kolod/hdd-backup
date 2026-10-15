using System;
using Xunit;

namespace HddBackup.Core.Tests
{
    /// <summary>
    /// A fact that runs only when an environment variable is set (real hardware, very large
    /// images). Otherwise xUnit reports it as skipped, so it is never counted as passed.
    /// </summary>
    public sealed class OptInFactAttribute : FactAttribute
    {
        public OptInFactAttribute(string environmentVariable)
        {
            EnvironmentVariable = environmentVariable;
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(environmentVariable)))
            {
                Skip = "Set " + environmentVariable + " to run this test";
            }
        }

        public string EnvironmentVariable { get; }
    }
}

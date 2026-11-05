using System;
using System.Collections.Generic;
using HddBackup.Core.Browsing;
using Xunit;

namespace HddBackup.Core.Tests.Browsing
{
    public class WindowsNamesTests
    {
        private static string Map(string name, out bool renamed, params string[] alreadyUsed)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var u in alreadyUsed)
            {
                used.Add(u.ToLowerInvariant());
            }

            return WindowsNames.Map(name, used, out renamed);
        }

        [Theory]
        [InlineData("hostname", "hostname")]
        [InlineData("with space.txt", "with space.txt")]
        [InlineData("Ünï cödé.txt", "Ünï cödé.txt")]
        [InlineData("日本語.txt", "日本語.txt")]
        [InlineData(".bashrc", ".bashrc")]
        [InlineData("archive.tar.gz", "archive.tar.gz")]
        public void NamesWindowsCanStore_AreUnchanged(string name, string expected)
        {
            Assert.Equal(expected, Map(name, out bool renamed));
            Assert.False(renamed);
        }

        [Theory]
        [InlineData("a:b", "a_b")]
        [InlineData("what?.txt", "what_.txt")]
        [InlineData("a<b>c", "a_b_c")]
        [InlineData("say \"hi\"", "say _hi_")]
        [InlineData("a|b", "a_b")]
        [InlineData("star*", "star_")]
        [InlineData("back\\slash", "back_slash")]
        [InlineData("tab\there", "tab_here")]
        [InlineData("bad-\uFFFD\uFFFD-name", "bad-__-name")]
        public void InvalidCharacters_BecomeUnderscores(string name, string expected)
        {
            Assert.Equal(expected, Map(name, out bool renamed));
            Assert.True(renamed);
        }

        [Theory]
        [InlineData("trailing.", "trailing_")]
        [InlineData("trailing ", "trailing_")]
        [InlineData("dots...", "dots___")]
        public void TrailingDotsAndSpaces_BecomeUnderscores(string name, string expected)
        {
            Assert.Equal(expected, Map(name, out bool renamed));
            Assert.True(renamed);
        }

        [Theory]
        [InlineData("CON", "_CON")]
        [InlineData("con", "_con")]
        [InlineData("nul.txt", "_nul.txt")]
        [InlineData("COM1", "_COM1")]
        [InlineData("LPT9.log", "_LPT9.log")]
        [InlineData("AUX.tar.gz", "_AUX.tar.gz")]
        public void ReservedDeviceNames_GetALeadingUnderscore(string name, string expected)
        {
            Assert.Equal(expected, Map(name, out bool renamed));
            Assert.True(renamed);
        }

        [Theory]
        [InlineData("CONSOLE")]
        [InlineData("COM10")]
        [InlineData("console.txt")]
        [InlineData("LPT")]
        public void NamesThatOnlyStartLikeReservedOnes_AreUnchanged(string name)
        {
            Assert.Equal(name, Map(name, out bool renamed));
            Assert.False(renamed);
        }

        [Fact]
        public void CaseOnlyCollisions_GetANumberBeforeTheExtension()
        {
            // The colliding name keeps its own letter case.
            Assert.Equal("Readme (2)", Map("Readme", out bool renamed, "README"));
            Assert.True(renamed);
            Assert.Equal("NOTES (2).TXT", Map("NOTES.TXT", out renamed, "notes.txt"));
            Assert.True(renamed);
            Assert.Equal("A (3).TXT", Map("A.TXT", out renamed, "a.txt", "a (2).txt"));
        }

        [Fact]
        public void MappedNamesColliding_AreSeparated_InOrder()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var names = new[] { "trailing ", "trailing.", "trailing_" };
            var result = new List<string>();
            foreach (var n in names)
            {
                result.Add(WindowsNames.Map(n, used, out _));
            }

            Assert.Equal(new[] { "trailing_", "trailing_ (2)", "trailing_ (3)" }, result);
        }

        [Fact]
        public void TheMappingIsDeterministic()
        {
            var names = new[] { "b?", "B_", "b:", "README", "Readme", "readme" };
            Func<List<string>> run = () =>
            {
                var used = new HashSet<string>(StringComparer.Ordinal);
                var r = new List<string>();
                foreach (var n in names)
                {
                    r.Add(WindowsNames.Map(n, used, out _));
                }

                return r;
            };
            Assert.Equal(run(), run());
            Assert.Equal(names.Length, new HashSet<string>(run(), StringComparer.OrdinalIgnoreCase).Count);
        }

        [Theory]
        [InlineData(@"C:\Temp\out", @"\\?\C:\Temp\out")]
        [InlineData(@"\\?\C:\Temp\out", @"\\?\C:\Temp\out")]
        [InlineData(@"\\server\share\dir", @"\\?\UNC\server\share\dir")]
        public void ExtendedPaths(string path, string expected)
        {
            Assert.Equal(expected, WindowsNames.ToExtendedPath(path));
        }
    }
}

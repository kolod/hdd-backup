using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HddBackup.Core.Errors;
using HddBackup.Core.Formats;
using HddBackup.Core.Imaging;
using HddBackup.Core.Partitioning;
using HddBackup.Core.Progress;
using Xunit;

namespace HddBackup.Core.Tests.Localization
{
    /// <summary>
    /// Constitution Principle V: every UI string exists in all six languages with the same
    /// placeholders, and every code the UI can display has a string.
    /// </summary>
    public class ResourceParityTests
    {
        private static readonly string[] Files =
        {
            "Strings.resx", "Strings.uk.resx", "Strings.de.resx", "Strings.fr.resx", "Strings.es.resx", "Strings.ja.resx"
        };

        private static readonly Regex Placeholder = new Regex(@"\{\d+\}");

        private static string ResourceDirectory()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "HddBackup.sln")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "src", "HddBackup.App", "Resources");
        }

        private static Dictionary<string, string> Load(string file)
        {
            var doc = XDocument.Load(Path.Combine(ResourceDirectory(), file));
            return doc.Root.Elements("data").ToDictionary(e => (string)e.Attribute("name"), e => (string)e.Element("value"));
        }

        [Fact]
        public void AllLanguages_HaveIdenticalKeys_AndNoEmptyValues()
        {
            var english = Load(Files[0]);
            Assert.NotEmpty(english);
            foreach (var file in Files)
            {
                var table = Load(file);
                Assert.Equal(english.Keys.OrderBy(k => k, StringComparer.Ordinal), table.Keys.OrderBy(k => k, StringComparer.Ordinal));
                Assert.All(table, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), file + ": empty " + pair.Key));
            }
        }

        [Fact]
        public void Placeholders_MatchEnglish()
        {
            var english = Load(Files[0]);
            foreach (var file in Files.Skip(1))
            {
                var table = Load(file);
                foreach (var pair in english)
                {
                    var expected = Placeholder.Matches(pair.Value).Cast<Match>().Select(m => m.Value).OrderBy(v => v);
                    var actual = Placeholder.Matches(table[pair.Key]).Cast<Match>().Select(m => m.Value).OrderBy(v => v);
                    Assert.True(expected.SequenceEqual(actual), file + ": placeholders differ in " + pair.Key);
                }
            }
        }

        [Fact]
        public void FileDialogFilters_KeepTheirPatterns()
        {
            foreach (var file in Files)
            {
                var table = Load(file);
                foreach (var key in new[] { "Dialog_ImageFilter_Save", "Dialog_ImageFilter_Open" })
                {
                    var parts = table[key].Split('|');
                    Assert.True(parts.Length % 2 == 0, file + ": " + key);
                    var patterns = parts.Where((p, i) => i % 2 == 1).ToArray();
                    var englishPatterns = Load(Files[0])[key].Split('|').Where((p, i) => i % 2 == 1).ToArray();
                    Assert.Equal(englishPatterns, patterns);
                }
            }
        }

        [Fact]
        public void EveryDisplayedEnumValue_HasAString()
        {
            var english = Load(Files[0]);
            var required = new List<string>();
            required.AddRange(Enum.GetNames(typeof(ErrorCode)).Select(n => "Error_" + n));
            required.AddRange(Enum.GetNames(typeof(ValidationWarning)).Select(n => "Warning_" + n));
            required.AddRange(Enum.GetNames(typeof(OperationPhase)).Select(n => "Phase_" + n));
            required.AddRange(Enum.GetNames(typeof(VerificationStatus)).Select(n => "Check_" + n));
            required.AddRange(Enum.GetNames(typeof(TableStyle)).Select(n => "Table_" + n));
            foreach (var key in required)
            {
                Assert.True(english.ContainsKey(key), "Missing string " + key);
            }
        }
    }
}

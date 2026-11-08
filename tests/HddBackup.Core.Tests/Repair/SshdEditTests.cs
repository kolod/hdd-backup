using System;
using System.Linq;
using HddBackup.Core.Repair;
using Xunit;

namespace HddBackup.Core.Tests.Repair
{
    /// <summary>Feature 011 (research R-7): text edits keep every other line byte for byte.</summary>
    public class SshdEditTests
    {
        private const string Note = "hdd-backup 2026-11-08";

        private static string Lf(params string[] lines) => string.Join("\n", lines) + "\n";

        [Fact]
        public void AReplacedLineStaysAsACommentAndTheNewLineFollowsIt()
        {
            string text = Lf("# header", "PasswordAuthentication no", "UsePAM yes");
            string result = SshdEdit.ReplaceLine(text, 2, "PasswordAuthentication yes", Note);
            Assert.Equal(Lf("# header", "# " + Note + ": PasswordAuthentication no", "PasswordAuthentication yes", "UsePAM yes"), result);
        }

        [Fact]
        public void TheIndentationOfTheOldLineIsKept()
        {
            string result = SshdEdit.ReplaceLine("  \tPubkeyAuthentication no\n", 1, "PubkeyAuthentication yes", Note);
            Assert.Equal("# " + Note + ": PubkeyAuthentication no\n  \tPubkeyAuthentication yes\n", result);
        }

        [Fact]
        public void ALineCanBeCommentedOutWithoutAReplacement()
        {
            string result = SshdEdit.ReplaceLine(Lf("a", "DenyUsers pi", "b"), 2, null, Note);
            Assert.Equal(Lf("a", "# " + Note + ": DenyUsers pi", "b"), result);
        }

        [Fact]
        public void LineEndingsOfOtherLinesAreKeptEvenWhenTheyAreMixed()
        {
            string text = "one\r\nPasswordAuthentication no\r\ntwo\nthree\r\n";
            string result = SshdEdit.ReplaceLine(text, 2, "PasswordAuthentication yes", Note);
            Assert.Equal("one\r\n# " + Note + ": PasswordAuthentication no\r\nPasswordAuthentication yes\r\ntwo\nthree\r\n", result);
        }

        [Fact]
        public void AFileWithoutAFinalLineBreakGetsOneOnlyWhereALineIsAdded()
        {
            // replacing the last line: the old line is commented, the new one is added after it
            Assert.Equal(
                "a\n# " + Note + ": PubkeyAuthentication no\nPubkeyAuthentication yes\n",
                SshdEdit.ReplaceLine("a\nPubkeyAuthentication no", 2, "PubkeyAuthentication yes", Note));

            // adding at the end: a break is needed after the old last line
            Assert.Equal(
                "a\nb\n# " + Note + "\nPermitRootLogin prohibit-password\n",
                SshdEdit.AddSetting("a\nb", "PermitRootLogin prohibit-password", Note));

            // a file where nothing is added keeps its missing final break
            Assert.Equal("# " + Note + ": a\nb", SshdEdit.ReplaceLine("a\nb", 1, null, Note));
        }

        [Fact]
        public void ALineThatIsNotThereGivesNull()
        {
            Assert.Null(SshdEdit.ReplaceLine("a\n", 5, "x", Note));
            Assert.Null(SshdEdit.InsertAfter("a\n", 0, "x"));
        }

        [Fact]
        public void ADefaultSettingIsAddedBeforeTheFirstMatchBlockNeverInsideIt()
        {
            string text = Lf("Include /etc/ssh/sshd_config.d/*.conf", "UsePAM yes", "Match User bob", "    X11Forwarding no");
            string result = SshdEdit.AddSetting(text, "PermitRootLogin prohibit-password", Note);
            Assert.Equal(Lf("Include /etc/ssh/sshd_config.d/*.conf", "UsePAM yes", "# " + Note, "PermitRootLogin prohibit-password", "Match User bob", "    X11Forwarding no"), result);
        }

        [Fact]
        public void MatchIsRecognisedOnlyAsAKeywordOfItsOwn()
        {
            Assert.True(SshdEdit.IsMatchLine("Match All"));
            Assert.True(SshdEdit.IsMatchLine("  match user x"));
            Assert.True(SshdEdit.IsMatchLine("Match"));
            Assert.False(SshdEdit.IsMatchLine("MatchingRules yes"));
            Assert.False(SshdEdit.IsMatchLine("# Match User x"));
        }

        [Fact]
        public void AWordIsInsertedAfterAnExistingLineWithoutChangingIt()
        {
            string text = Lf("AllowUsers alice", "UsePAM yes");
            Assert.Equal(Lf("AllowUsers alice", "AllowUsers pi", "UsePAM yes"), SshdEdit.InsertAfter(text, 1, "AllowUsers pi"));
        }

        [Fact]
        public void ATokenIsRemovedFromAListLine()
        {
            string text = Lf("DenyUsers mallory pi eve", "UsePAM yes");
            Assert.Equal(Lf("# " + Note + ": DenyUsers mallory pi eve", "DenyUsers mallory eve", "UsePAM yes"), SshdEdit.RemoveToken(text, 1, "pi", Note));
        }

        [Fact]
        public void TheLastTokenOfAListLineLeavesJustTheComment()
        {
            Assert.Equal(Lf("# " + Note + ": DenyUsers pi", "UsePAM yes"), SshdEdit.RemoveToken(Lf("DenyUsers pi", "UsePAM yes"), 1, "pi", Note));
        }

        [Fact]
        public void RemovingATokenThatIsNotThereOrFromAKeyEqualsLineGivesNull()
        {
            Assert.Null(SshdEdit.RemoveToken(Lf("DenyUsers mallory"), 1, "pi", Note));
            Assert.Null(SshdEdit.RemoveToken(Lf("DenyUsers=pi"), 1, "pi", Note));
        }

        [Fact]
        public void WordsSplitsAKeywordLineAndRefusesCommentsAndBlankLines()
        {
            Assert.Equal(new[] { "AllowUsers", "a", "b" }, SshdEdit.Words("  AllowUsers \t a   b "));
            Assert.Null(SshdEdit.Words("# AllowUsers a"));
            Assert.Null(SshdEdit.Words("   "));
        }

        [Fact]
        public void EveryUntouchedLineSurvivesByteForByte()
        {
            var rnd = new Random(5);
            for (int round = 0; round < 60; round++)
            {
                var lines = Enumerable.Range(0, rnd.Next(1, 12)).Select(i => rnd.Next(4) == 0 ? "# comment " + i : "Keyword" + i + " value " + rnd.Next(100)).ToList();
                string eol = rnd.Next(2) == 0 ? "\n" : "\r\n";
                string text = string.Join(eol, lines) + (rnd.Next(2) == 0 ? eol : string.Empty);
                int target = rnd.Next(1, lines.Count + 1);
                string result = SshdEdit.ReplaceLine(text, target, "X y", Note);

                var after = result.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                for (int i = 0; i < lines.Count; i++)
                {
                    if (i + 1 == target)
                    {
                        continue;
                    }

                    // lines after the replaced one moved down by the added line
                    int where = i + 1 > target ? i + 1 : i;
                    Assert.Equal(lines[i], after[where]);
                }

                // the lines before the edited one are untouched down to their endings
                int cut = 0;
                for (int i = 0; i < target - 1; i++)
                {
                    cut += lines[i].Length + eol.Length;
                }

                Assert.Equal(text.Substring(0, cut), result.Substring(0, cut));
            }
        }

        [Fact]
        public void TheShellOfOneAccountChangesAndNothingElse()
        {
            string passwd = "root:x:0:0:root:/root:/bin/bash\npi:x:1000:1000:Pi User,,,:/home/pi:/usr/sbin/nologin\nbob:x:1001:1001::/home/bob:/usr/sbin/nologin\n";
            string result = PasswdEdit.ChangeShell(passwd, "pi", "/bin/bash");
            Assert.Equal("root:x:0:0:root:/root:/bin/bash\npi:x:1000:1000:Pi User,,,:/home/pi:/bin/bash\nbob:x:1001:1001::/home/bob:/usr/sbin/nologin\n", result);
        }

        [Fact]
        public void APasswdWithCrlfAndNoFinalBreakKeepsItsEndings()
        {
            string passwd = "pi:x:1000:1000::/home/pi:/bin/false\r\nbob:x:1001:1001::/home/bob:/bin/false";
            Assert.Equal("pi:x:1000:1000::/home/pi:/bin/sh\r\nbob:x:1001:1001::/home/bob:/bin/false", PasswdEdit.ChangeShell(passwd, "pi", "/bin/sh"));
        }

        [Fact]
        public void AnAccountThatIsNotThereOrAShortLineGivesNull()
        {
            Assert.Null(PasswdEdit.ChangeShell("root:x:0:0:root:/root:/bin/bash\n", "pi", "/bin/sh"));
            Assert.Null(PasswdEdit.ChangeShell("pi:x:1000\n", "pi", "/bin/sh"));
        }
    }
}

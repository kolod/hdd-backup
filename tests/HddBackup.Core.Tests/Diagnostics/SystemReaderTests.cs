using System;
using System.Linq;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Errors;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    public class SystemReaderTests
    {
        [Fact]
        public void ReadsTextAndTruncatesAtTheLimit()
        {
            var b = new LinuxRootBuilder().File("/etc/a", new string('x', 100));
            var r = b.Reader();

            Assert.Equal(100, r.ReadText("/etc/a").Length);
            string cut = r.ReadText("/etc/a", out bool truncated, 40);

            Assert.Equal(40, cut.Length);
            Assert.True(truncated);
            Assert.Contains("/etc/a", r.Notes.TruncatedFiles);
        }

        [Fact]
        public void ReadsLinesWithoutAnEmptyLastLineAndWithCrLf()
        {
            var r = new LinuxRootBuilder().File("/f", "a\r\nb\n\nc\n").Reader();

            Assert.Equal(new[] { "a", "b", string.Empty, "c" }, r.ReadLines("/f"));
        }

        [Fact]
        public void StripsAByteOrderMark()
        {
            var r = new LinuxRootBuilder().File("/f", "﻿hello").Reader();

            // The builder encodes the BOM char as text; the reader drops a leading UTF-8 BOM.
            Assert.Equal("hello", r.ReadText("/f"));
        }

        [Fact]
        public void ListsSortedAndCapsAtTheLimit()
        {
            var b = new LinuxRootBuilder();
            foreach (var n in new[] { "b", "a", "c", "B" })
            {
                b.File("/d/" + n, "x");
            }

            var r = b.Reader();

            Assert.Equal(new[] { "B", "a", "b", "c" }, r.List("/d").Select(e => e.Name));
            var cut = r.List("/d", 2);
            Assert.Equal(2, cut.Count);
            Assert.Contains("/d", r.Notes.CutDirectories);
        }

        [Fact]
        public void ListOfAFileOrMissingPathIsNull()
        {
            var r = new LinuxRootBuilder().File("/f", "x").Reader();

            Assert.Null(r.List("/f"));
            Assert.Null(r.List("/nope"));
        }

        [Fact]
        public void FollowsAnAbsoluteLink()
        {
            var r = new LinuxRootBuilder()
                .File("/lib/systemd/system/ssh.service", "unit")
                .Link("/etc/systemd/system/multi-user.target.wants/ssh.service", "/lib/systemd/system/ssh.service")
                .Reader();

            Assert.Equal("unit", r.ReadText("/etc/systemd/system/multi-user.target.wants/ssh.service"));
            Assert.Equal("/lib/systemd/system/ssh.service", r.Resolve("/etc/systemd/system/multi-user.target.wants/ssh.service"));
        }

        [Fact]
        public void FollowsRelativeLinksWithDotDot()
        {
            var r = new LinuxRootBuilder()
                .File("/usr/lib/systemd/system/ssh.service", "unit")
                .Link("/etc/systemd/system/multi-user.target.wants/ssh.service", "../../../../usr/lib/systemd/system/ssh.service")
                .Reader();

            Assert.Equal("unit", r.ReadText("/etc/systemd/system/multi-user.target.wants/ssh.service"));
        }

        [Fact]
        public void FollowsAChainOfLinks()
        {
            var r = new LinuxRootBuilder()
                .File("/real", "data")
                .Link("/l3", "/real")
                .Link("/l2", "l3")
                .Link("/l1", "l2")
                .Reader();

            Assert.Equal("data", r.ReadText("/l1"));
        }

        [Fact]
        public void FollowsMergedUsrDirectoryLinks()
        {
            var r = new LinuxRootBuilder()
                .File("/usr/lib/systemd/system/cron.service", "cron")
                .Link("/lib", "usr/lib")
                .Reader();

            Assert.Equal("cron", r.ReadText("/lib/systemd/system/cron.service"));
            Assert.Equal("/usr/lib/systemd/system/cron.service", r.Resolve("/lib/systemd/system/cron.service"));
            Assert.Contains("cron.service", r.ListNames("/lib/systemd/system"));
        }

        [Fact]
        public void ALinkToAMissingTargetDoesNotExistButTheLinkIsReadable()
        {
            var r = new LinuxRootBuilder().Link("/etc/x", "/nowhere").Reader();

            Assert.False(r.Exists("/etc/x"));
            Assert.Equal("/nowhere", r.ReadLink("/etc/x"));
            Assert.NotNull(r.Lookup("/etc/x", false));
        }

        [Fact]
        public void ALoopGivesNullAndDoesNotHang()
        {
            var r = new LinuxRootBuilder().Link("/a", "/b").Link("/b", "/a").Link("/self", "self").Reader();

            Assert.Null(r.Lookup("/a"));
            Assert.Null(r.Resolve("/self"));
            Assert.Null(r.ReadText("/a"));
        }

        [Fact]
        public void MoreThanFortyLinksFail()
        {
            var b = new LinuxRootBuilder().File("/end", "x");
            string target = "/end";
            for (int i = 0; i < 41; i++)
            {
                string name = "/l" + i;
                b.Link(name, target);
                target = name;
            }

            Assert.Null(b.Reader().ReadText(target));
        }

        [Fact]
        public void ALinkToDevNullIsReadableAsALinkAndDoesNotResolveInTheImage()
        {
            var r = new LinuxRootBuilder().Link("/etc/systemd/system/ssh.service", "/dev/null").Reader();

            Assert.Equal("/dev/null", r.ReadLink("/etc/systemd/system/ssh.service"));
            Assert.Null(r.ReadText("/etc/systemd/system/ssh.service"));
        }

        [Fact]
        public void DotDotAtTheRootStaysAtTheRoot()
        {
            var r = new LinuxRootBuilder().File("/etc/a", "x").Reader();

            Assert.Equal("x", r.ReadText("/../../etc/a"));
            Assert.Equal("x", r.ReadText("/etc/./a"));
            Assert.Equal("x", r.ReadText("etc/a"));
        }

        [Fact]
        public void SecondReadsAndListingsComeFromTheCache()
        {
            var b = new LinuxRootBuilder().File("/etc/a", "x").File("/etc/b", "y");
            var r = b.Reader();

            r.ReadText("/etc/a");
            r.List("/etc");
            int reads = b.ReadPaths.Count;
            int listings = r.Listings;
            r.ReadText("/etc/a");
            r.List("/etc");
            r.Exists("/etc/b");

            Assert.Equal(reads, b.ReadPaths.Count);
            Assert.Equal(listings, r.Listings);
            Assert.Equal(1, r.FileReads);
        }

        [Fact]
        public void CancellationStopsReading()
        {
            var cts = new CancellationTokenSource();
            var r = new LinuxRootBuilder().File("/a", "x").Reader(cts.Token);
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() => r.ReadText("/a"));
        }

        [Fact]
        public void AnUnreadableEntryIsAValueNotAnException()
        {
            var b = new LinuxRootBuilder().File("/etc/a", "x");
            b.Tree.OnRead = f => throw new ImagingException(ErrorCode.CorruptImage, "bad extent");
            var r = b.Reader();

            Assert.Null(r.ReadText("/etc/a"));
            Assert.Contains("/etc/a", r.Notes.UnreadableFiles);
        }

        [Fact]
        public void SourceErrorsPropagate()
        {
            var b = new LinuxRootBuilder().File("/etc/a", "x");
            b.Tree.OnRead = f => throw new ImagingException(ErrorCode.DeviceRemoved, "gone");
            var r = b.Reader();

            Assert.Equal(ErrorCode.DeviceRemoved, Assert.Throws<ImagingException>(() => r.ReadText("/etc/a")).Code);
        }

        [Fact]
        public void IsExecutableChecksTheModeBits()
        {
            var r = new LinuxRootBuilder().Executable("/bin/x").File("/bin/y", "x").Reader();

            Assert.True(r.IsExecutable("/bin/x"));
            Assert.False(r.IsExecutable("/bin/y"));
            Assert.False(r.IsExecutable("/bin/none"));
        }

        [Fact]
        public void InvalidUtf8BecomesReplacementCharacters()
        {
            var b = new LinuxRootBuilder();
            b.Tree.AddFile(b.Tree.Root, "bin", new byte[] { 0x61, 0xFF, 0x62 });

            Assert.Equal("a�b", b.Reader().ReadText("/bin"));
        }

        [Fact]
        public void CombineJoinsPaths()
        {
            Assert.Equal("/a/b", SystemReader.Combine("/a", "b"));
            Assert.Equal("/b", SystemReader.Combine("/", "b"));
            Assert.Equal("/", SystemReader.Combine(string.Empty, string.Empty));
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HddBackup.Core.Devices;
using HddBackup.Core.Editing;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Editing
{
    public class ImageEditSessionTests
    {
        private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

        private static string ImageSha(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var f = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string FirstFile(EditImage image)
        {
            return EditAssert.Snapshot(image.Device).Values.First(e => e.Type == 'F').Path;
        }

        [Fact]
        public void ChangesStayInTheDiffFileUntilSave()
        {
            using (var image = EditImage.Copy("edit-basic"))
            {
                string path = FirstFile(image);
                image.Device.Dispose();
                string before = ImageSha(image.Path);
                using (var session = ImageEditSession.Open(image.Path))
                {
                    session.Add(EditChange.Edit(path, Text("new text\n"), null));
                    Assert.True(File.Exists(session.DiffPath));
                    Assert.Equal(before, ImageSha(image.Path));
                }

                // reopening finds the same waiting changes
                using (var again = ImageEditSession.Open(image.Path))
                {
                    Assert.Equal(DiffStatus.Matches, again.DiffStatus);
                    Assert.Single(again.Changes);
                    again.Discard();
                    Assert.False(File.Exists(again.DiffPath));
                }

                Assert.Equal(before, ImageSha(image.Path));
            }
        }

        [Fact]
        public void SaveWritesTheImageAndCreatesTheChecksum()
        {
            using (var image = EditImage.Copy("edit-basic"))
            {
                string path = FirstFile(image);
                image.Device.Dispose();
                using (var session = ImageEditSession.Open(image.Path))
                {
                    session.Add(EditChange.Edit(path, Text("saved\n"), null));
                    session.Add(EditChange.NewDirectory("/made"));
                    session.Add(EditChange.NewFile("/made/a.txt", Text("a")));
                    var result = session.Save(null, CancellationToken.None);
                    Assert.True(result.Status == SaveStatus.Saved, result.Status + ": " + result.Detail);
                    Assert.False(File.Exists(session.DiffPath));
                    Assert.False(File.Exists(RedoLog.PathFor(image.Path)));
                    Assert.Single(result.ChecksumFiles);
                    Assert.Contains(ImageSha(image.Path), File.ReadAllText(result.ChecksumFiles[0]));
                }

                using (var device = FileBlockDevice.OpenRead(image.Path))
                {
                    EditAssert.CheckerClean(device, "after save");
                    Assert.Equal(Text("saved\n"), EditAssert.Content(device, path));
                    Assert.Equal(Text("a"), EditAssert.Content(device, "/made/a.txt"));
                }
            }
        }

        [Fact]
        public void ADiffOfAnotherVersionOfTheImageIsNotUsable()
        {
            using (var image = EditImage.Copy("edit-basic"))
            {
                string path = FirstFile(image);
                image.Device.Dispose();
                using (var session = ImageEditSession.Open(image.Path))
                {
                    session.Add(EditChange.Edit(path, Text("x"), null));
                }

                using (var f = new FileStream(image.Path, FileMode.Open, FileAccess.Write))
                {
                    f.Seek(0, SeekOrigin.End); // another version of the image: it is longer now
                    f.Write(new byte[512], 0, 512);
                }

                using (var session = ImageEditSession.Open(image.Path))
                {
                    Assert.True(session.IsStale);
                    Assert.Equal(DiffStatus.ImageChanged, session.DiffStatus);
                    Assert.Equal(SaveStatus.Refused, session.Save(null, CancellationToken.None).Status);
                }
            }
        }

        [Fact]
        public void ASecondInstanceCannotEditTheSameImage()
        {
            using (var image = EditImage.Copy("edit-basic"))
            {
                image.Device.Dispose();
                using (ImageEditSession.Open(image.Path))
                {
                    var ex = Assert.Throws<EditRefusedException>(() => ImageEditSession.Open(image.Path));
                    Assert.Equal(EditRefused.InUseByAnotherInstance, ex.Reason);
                }
            }
        }

        [Fact]
        public void AnInterruptedSaveIsFinishedFromTheRedoLog()
        {
            using (var image = EditImage.Copy("edit-basic"))
            {
                string path = FirstFile(image);
                image.Device.Dispose();
                using (var session = ImageEditSession.Open(image.Path))
                {
                    session.Add(EditChange.Edit(path, Text("after the crash\n"), null));
                    var cts = new CancellationTokenSource();
                    var result = session.Save((phase, done, total) =>
                    {
                        if (phase == SavePhase.Write && done > 0)
                        {
                            cts.Cancel();
                        }
                    }, cts.Token);
                    Assert.Equal(SaveStatus.Interrupted, result.Status);
                    Assert.True(File.Exists(RedoLog.PathFor(image.Path)));
                }

                using (var session = ImageEditSession.Open(image.Path))
                {
                    Assert.Equal(SessionState.Interrupted, session.State);
                    var result = session.FinishSave(null, CancellationToken.None);
                    Assert.True(result.Status == SaveStatus.Saved, result.Status + ": " + result.Detail);
                    Assert.False(File.Exists(RedoLog.PathFor(image.Path)));
                }

                using (var device = FileBlockDevice.OpenRead(image.Path))
                {
                    EditAssert.CheckerClean(device, "after finish");
                    Assert.Equal(Text("after the crash\n"), EditAssert.Content(device, path));
                }
            }
        }
    }
}

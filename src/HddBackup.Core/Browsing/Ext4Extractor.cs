using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HddBackup.Core.Errors;
using HddBackup.Core.Imaging;

namespace HddBackup.Core.Browsing
{
    /// <summary>
    /// Copies the items of an <see cref="ExtractionPlan"/> to a Windows folder (research R-5).
    /// A file is written as ".hddbackup-part-XXXXXXXXXXXX" in its directory and renamed when
    /// complete, so an interrupted extraction never leaves a file that looks finished. The part
    /// name has a fixed length because a suffix on the real name would push 255-character names
    /// over the limit of Windows. One failing item is recorded and the rest continues; only a
    /// full destination and source device errors stop the run.
    /// </summary>
    internal static class Ext4Extractor
    {
        public const string PartPrefix = ".hddbackup-part-";
        private const int BufferSize = 4 << 20;

        /// <param name="bytesDone">Cumulative bytes copied so far; called after every chunk.</param>
        public static ExtractionSummary Run(
            IFileTree tree,
            ExtractionPlan plan,
            ConflictPolicy policy,
            string protectedImagePath,
            Action<long> bytesDone,
            CancellationToken cancellationToken)
        {
            var summary = new ExtractionSummary { DestinationFolder = plan.DestinationFolder };
            summary.Issues.AddRange(plan.Issues);
            var directories = new List<ExtractionItem>();
            var failedDirectories = new List<string>();
            var buffer = new byte[BufferSize];
            long done = 0;

            void Failed(ExtractionItem item, ExtractionIssueReason reason, string detail)
            {
                summary.Issues.Add(new ExtractionIssue
                {
                    Kind = ExtractionIssueKind.Failed,
                    Reason = reason,
                    Path = item.Source.Path,
                    Detail = detail
                });
            }

            foreach (var item in plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Action != ExtractionAction.CreateDirectory && item.Action != ExtractionAction.Copy)
                {
                    continue;
                }

                if (failedDirectories.Exists(d => item.RelativePath.StartsWith(d + "\\", StringComparison.Ordinal)))
                {
                    continue; // its directory could not be created; that failure is already listed
                }

                string full = Path.Combine(plan.DestinationFolder, item.RelativePath);
                string path = WindowsNames.ToExtendedPath(full);
                if (item.Action == ExtractionAction.CreateDirectory)
                {
                    try
                    {
                        Directory.CreateDirectory(path);
                        directories.Add(item);
                        summary.DirectoriesCreated++;
                    }
                    catch (Exception ex) when (IsFileSystemError(ex))
                    {
                        failedDirectories.Add(item.RelativePath);
                        Failed(item, ExtractionIssueReason.WriteFailed, ex.Message);
                    }

                    continue;
                }

                if (protectedImagePath != null && FileIdentity.IsSameFile(full, protectedImagePath))
                {
                    throw new ImagingException(ErrorCode.DestinationIsSource, "The extraction would overwrite the image: " + full);
                }

                if (File.Exists(path) && policy != ConflictPolicy.Replace)
                {
                    summary.Issues.Add(new ExtractionIssue
                    {
                        Kind = ExtractionIssueKind.Skipped,
                        Reason = ExtractionIssueReason.AlreadyExists,
                        Path = item.Source.Path
                    });
                    done += item.Bytes;
                    bytesDone?.Invoke(done);
                    continue;
                }

                long before = done;
                if (CopyFile(tree, item, path, buffer, ref done, bytesDone, summary, Failed, cancellationToken))
                {
                    summary.FilesExtracted++;
                    summary.BytesExtracted += item.Bytes;
                }
                else
                {
                    done = before + item.Bytes;
                    bytesDone?.Invoke(done);
                }
            }

            // Directory times last, children first, because writing files changes them.
            for (int i = directories.Count - 1; i >= 0; i--)
            {
                try
                {
                    string path = WindowsNames.ToExtendedPath(Path.Combine(plan.DestinationFolder, directories[i].RelativePath));
                    Directory.SetLastWriteTimeUtc(path, directories[i].Source.Modified);
                }
                catch (Exception ex) when (IsFileSystemError(ex))
                {
                    // A time that cannot be set is not worth a failed extraction.
                }
            }

            return summary;
        }

        /// <summary>Returns false when the file failed (listed through <paramref name="failed"/>); throws for fatal errors.</summary>
        private static bool CopyFile(
            IFileTree tree,
            ExtractionItem item,
            string path,
            byte[] buffer,
            ref long done,
            Action<long> bytesDone,
            ExtractionSummary summary,
            Action<ExtractionItem, ExtractionIssueReason, string> failed,
            CancellationToken cancellationToken)
        {
            string part = Path.Combine(Path.GetDirectoryName(path), PartPrefix + Guid.NewGuid().ToString("N").Substring(0, 12));
            try
            {
                using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan))
                {
                    long offset = 0;
                    long size = item.Bytes;
                    while (offset < size)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int want = (int)Math.Min(buffer.Length, size - offset);
                        int n = tree.ReadRange(item.Source, offset, buffer, 0, want);
                        if (n <= 0)
                        {
                            throw new ImagingException(ErrorCode.CorruptImage, item.Source.Path + ": the file is shorter than its size says");
                        }

                        stream.Write(buffer, 0, n);
                        offset += n;
                        done += n;
                        bytesDone?.Invoke(done);
                    }
                }

                File.SetLastWriteTimeUtc(part, item.Source.Modified);
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }

                File.Move(part, path);
                return true;
            }
            catch (OperationCanceledException)
            {
                DeletePart(part);
                throw;
            }
            catch (ImagingException ex) when (ex.Code == ErrorCode.CorruptImage)
            {
                DeletePart(part);
                failed(item, ExtractionIssueReason.Unreadable, ex.Detail);
                return false;
            }
            catch (ImagingException)
            {
                DeletePart(part);
                throw; // device removed, read error: the whole extraction stops with the cause
            }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                DeletePart(part);
                throw new ImagingException(ErrorCode.DestinationFull, "Destination: " + ex.Message, null, ex);
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                DeletePart(part);
                failed(item, ExtractionIssueReason.WriteFailed, ex.Message);
                return false;
            }
        }

        internal static bool IsDiskFull(IOException ex)
        {
            int code = ex.HResult & 0xFFFF;
            return code == 112 || code == 39; // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
        }

        private static bool IsFileSystemError(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException
                || ex is ArgumentException || ex is System.Security.SecurityException;
        }

        private static void DeletePart(string part)
        {
            try
            {
                File.Delete(part);
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                // The part file is only a leftover; nothing looks complete either way.
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HddBackup.Core.Devices;
using HddBackup.Core.Imaging;

namespace HddBackup.Core.Browsing
{
    /// <summary>
    /// Works out what extracting an entry means before anything is written (research R-5/R-6):
    /// the Windows names, total size, conflicts with existing files, items that cannot be
    /// extracted and why. Reads directories and inode metadata only, never file contents.
    /// </summary>
    public static class ExtractionPlanner
    {
        /// <summary>Longest path (in characters, without the extended prefix) Windows can address.</summary>
        internal const int MaxWindowsPath = 32760;

        public static ExtractionPlan Plan(
            IFileTree tree,
            FsEntry selection,
            string destinationFolder,
            PathInfo destinationInfo,
            ConflictPolicy policy,
            string protectedImagePath,
            CancellationToken cancellationToken)
        {
            if (tree == null)
            {
                throw new ArgumentNullException(nameof(tree));
            }

            if (selection == null)
            {
                throw new ArgumentNullException(nameof(selection));
            }

            var plan = new ExtractionPlan
            {
                DestinationFolder = destinationFolder,
                FreeBytes = destinationInfo?.FreeBytes
            };
            bool fat = destinationInfo != null && destinationInfo.FileSystem.StartsWith("FAT", StringComparison.OrdinalIgnoreCase);
            var stack = new Stack<KeyValuePair<FsEntry, string>>();

            void AddItem(FsEntry entry, string relative, ExtractionAction action, long bytes = 0)
            {
                plan.Items.Add(new ExtractionItem { Source = entry, RelativePath = relative, Action = action, Bytes = bytes });
            }

            void Issue(ExtractionIssueKind kind, ExtractionIssueReason reason, FsEntry entry, string detail = "", string resultName = "")
            {
                plan.Issues.Add(new ExtractionIssue { Kind = kind, Reason = reason, Path = entry.Path, Detail = detail, ResultName = resultName });
            }

            // Handles one entry whose Windows name is already decided; pushes directories to visit.
            void Visit(FsEntry entry, string relative)
            {
                string full = Path.Combine(destinationFolder, relative);
                switch (entry.Kind)
                {
                    case FsEntryKind.Directory:
                        if (relative.Length + destinationFolder.Length + 1 > MaxWindowsPath)
                        {
                            AddItem(entry, relative, ExtractionAction.Fail);
                            Issue(ExtractionIssueKind.Failed, ExtractionIssueReason.PathTooLong, entry);
                        }
                        else if (File.Exists(WindowsNames.ToExtendedPath(full)))
                        {
                            AddItem(entry, relative, ExtractionAction.Fail);
                            Issue(ExtractionIssueKind.Failed, ExtractionIssueReason.WriteFailed, entry, "a file with this name exists in the destination");
                        }
                        else
                        {
                            AddItem(entry, relative, ExtractionAction.CreateDirectory);
                            plan.Directories++;
                            stack.Push(new KeyValuePair<FsEntry, string>(entry, relative));
                        }

                        break;

                    case FsEntryKind.File:
                        if (entry.NotViewable != null)
                        {
                            AddItem(entry, relative, ExtractionAction.Fail);
                            Issue(ExtractionIssueKind.Failed, ExtractionIssueReason.NotExtractable, entry, entry.NotViewable);
                        }
                        else if (relative.Length + destinationFolder.Length + 1 > MaxWindowsPath)
                        {
                            AddItem(entry, relative, ExtractionAction.Fail);
                            Issue(ExtractionIssueKind.Failed, ExtractionIssueReason.PathTooLong, entry);
                        }
                        else if (fat && entry.Size > DestinationChecks.Fat32MaxFileSize)
                        {
                            AddItem(entry, relative, ExtractionAction.Fail);
                            Issue(ExtractionIssueKind.Failed, ExtractionIssueReason.FatLimit, entry);
                        }
                        else
                        {
                            string extended = WindowsNames.ToExtendedPath(full);
                            if (Directory.Exists(extended))
                            {
                                AddItem(entry, relative, ExtractionAction.Fail);
                                Issue(ExtractionIssueKind.Failed, ExtractionIssueReason.WriteFailed, entry, "a directory with this name exists in the destination");
                                break;
                            }

                            if (protectedImagePath != null && plan.ProtectedPathHit == null && FileIdentity.IsSameFile(full, protectedImagePath))
                            {
                                plan.ProtectedPathHit = full;
                            }

                            if (File.Exists(extended))
                            {
                                plan.Conflicts++;
                                if (policy == ConflictPolicy.Skip)
                                {
                                    AddItem(entry, relative, ExtractionAction.SkipExisting);
                                    Issue(ExtractionIssueKind.Skipped, ExtractionIssueReason.AlreadyExists, entry);
                                    break;
                                }
                            }

                            AddItem(entry, relative, ExtractionAction.Copy, entry.Size);
                            plan.Files++;
                            plan.TotalBytes += entry.Size;
                        }

                        break;
                }
            }

            // Names are mapped only for items that are created; skipped ones need no Windows name.
            string MapName(FsEntry entry, ISet<string> used)
            {
                string mapped = WindowsNames.Map(entry.Name, used, out bool renamed);
                if (renamed)
                {
                    Issue(ExtractionIssueKind.Renamed, ExtractionIssueReason.WindowsName, entry, string.Empty, mapped);
                }

                return mapped;
            }

            tree.Describe(selection);
            if (selection.Unreadable != null)
            {
                AddItem(selection, selection.Name, ExtractionAction.Fail);
                Issue(ExtractionIssueKind.Failed, ExtractionIssueReason.Unreadable, selection, selection.Unreadable);
                return plan;
            }

            if (selection.Kind == FsEntryKind.Symlink || selection.Kind == FsEntryKind.Special)
            {
                bool link = selection.Kind == FsEntryKind.Symlink;
                AddItem(selection, selection.Name, link ? ExtractionAction.SkipSymlink : ExtractionAction.SkipSpecial);
                Issue(ExtractionIssueKind.Skipped, link ? ExtractionIssueReason.SymbolicLink : ExtractionIssueReason.SpecialFile, selection);
                return plan;
            }

            // The root directory has no name of its own.
            string top = selection.Name == "/" ? "root" : MapName(selection, new HashSet<string>(StringComparer.Ordinal));
            Visit(selection, top);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                var children = tree.GetChildren(current.Key, cancellationToken);
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var child in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    tree.Describe(child);
                    if (child.Unreadable != null)
                    {
                        AddItem(child, current.Value + "\\" + MapName(child, used), ExtractionAction.Fail);
                        Issue(ExtractionIssueKind.Failed, ExtractionIssueReason.Unreadable, child, child.Unreadable);
                        continue;
                    }

                    if (child.Kind == FsEntryKind.Symlink)
                    {
                        AddItem(child, current.Value + "\\" + child.Name, ExtractionAction.SkipSymlink);
                        Issue(ExtractionIssueKind.Skipped, ExtractionIssueReason.SymbolicLink, child);
                        continue;
                    }

                    if (child.Kind == FsEntryKind.Special)
                    {
                        AddItem(child, current.Value + "\\" + child.Name, ExtractionAction.SkipSpecial);
                        Issue(ExtractionIssueKind.Skipped, ExtractionIssueReason.SpecialFile, child);
                        continue;
                    }

                    Visit(child, current.Value + "\\" + MapName(child, used));
                }
            }

            return plan;
        }
    }
}

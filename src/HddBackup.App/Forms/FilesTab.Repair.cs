using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HddBackup.App.Localization;
using HddBackup.Core.Browsing;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Editing;
using HddBackup.Core.Errors;
using HddBackup.Core.Imaging;
using HddBackup.Core.Repair;

namespace HddBackup.App.Forms
{
    /// <summary>The SSH wizard's way to repair through the edit mode of the Files tab (feature 011).</summary>
    internal sealed partial class FilesTab
    {
        private SshRepairHost _repairHost;
        private bool _repairBusy;
        private string _repairStamp = string.Empty;

        private SshRepairHost RepairHost => _repairHost ?? (_repairHost = new SshRepairHost(this));

        /// <summary>Tells an open wizard that edit mode, the waiting changes or the open file system changed.</summary>
        private void NotifyRepairChanged()
        {
            string stamp = (Editing ? "1" : "0") + "|" + (_session != null ? _session.Changes.Count : -1) + "|" + (_card != null ? "c" : "-") + "|" + (_view != null ? "v" : "-");
            if (stamp == _repairStamp)
            {
                return;
            }

            _repairStamp = stamp;
            _repairHost?.RaiseChanged();
        }

        /// <summary>The waiting changes laid over the file system, so a tool sees it as it will be after Save; null when none wait.</summary>
        private Func<IFileTree, IFileTree> RepairOverlay()
        {
            var session = _session;
            if (session == null || session.Changes.Count == 0)
            {
                return null;
            }

            var changes = session.Changes.ToList();
            return view => new PendingTree(view, changes);
        }

        private sealed class SshRepairHost : ISshRepairHost
        {
            private readonly FilesTab _tab;
            private readonly RepairLog _log = new RepairLog();

            public SshRepairHost(FilesTab tab)
            {
                _tab = tab;
            }

            public event EventHandler Changed;

            public RepairLog LogOfSession => _log;

            public bool CanRepair => _tab._view != null && _tab.Editing && (_tab._session == null || !_tab._session.IsStale);

            public bool IsCard => _tab._card != null;

            public IReadOnlyList<RepairGroup> Waiting => _tab._session != null ? _tab._session.Groups : Array.Empty<RepairGroup>();

            public IReadOnlyList<RepairLogEntry> Log => _log.Entries;

            public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

            public Task<RepairView> ReadAsync()
            {
                bool can = CanRepair;
                return Run(
                    (reader, source) =>
                    {
                        var report = SshCheck.Run(reader, source, CancellationToken.None);
                        return new RepairView(report, can ? SshRepairPlanner.Offers(reader, report, DateTime.Today) : null);
                    });
            }

            public Task<RepairPlan> PlanAddKeyAsync(string account, PublicKey key)
            {
                return Run((reader, source) =>
                {
                    var report = SshCheck.Run(reader, source, CancellationToken.None);
                    return SshRepairPlanner.PlanAddKey(reader, report, account, key);
                });
            }

            private async Task<T> Run<T>(Func<SystemReader, DiagnosticsSource, T> tool) where T : class
            {
                var source = _tab.BuildToolSource();
                if (source == null)
                {
                    return null;
                }

                var overlay = _tab.RepairOverlay();
                try
                {
                    return await Task.Run(() => DiagnosticsRunner.Run(_tab._access, source, tool, CancellationToken.None, overlay));
                }
                catch (Exception ex) when (ex is ImagingException || ex is IOException || ex is ObjectDisposedException || ex is UnauthorizedAccessException)
                {
                    return null;
                }
            }

            public async Task<RepairApplyResult> ApplyAsync(RepairPlan plan)
            {
                if (!CanRepair)
                {
                    return new RepairApplyResult(false, Strings.Get("Repair_Hint_EditOff"));
                }

                var tab = _tab;
                string title = RepairTexts.Title(plan.Title);
                string select = tab._tree.SelectedNode?.Tag is FsEntry current ? current.Path : "/";
                tab._repairBusy = true;
                tab.UpdateEditUi();
                try
                {
                    if (tab._session != null)
                    {
                        int group;
                        try
                        {
                            group = tab._session.AddRepair(plan.Changes, plan.Title);
                        }
                        catch (EditException ex)
                        {
                            return Failed(EditTexts.Error(ex.Code));
                        }
                        catch (IOException ex)
                        {
                            return Failed(ex.Message);
                        }

                        _log.Add(plan, RepairState.Waiting, DateTime.Now, group);
                        tab.RebuildTree(select);
                        return new RepairApplyResult(true, Strings.Format("Repair_Applied_Image", title));
                    }

                    var card = tab._card;
                    SaveResult result = await Task.Run(() => card.Apply(plan.Changes, null, CancellationToken.None));
                    if (result.Status != SaveStatus.Saved)
                    {
                        if (result.Status != SaveStatus.Refused)
                        {
                            tab.RebuildTree(select); // the card may have changed in steps that leave files whole
                        }

                        return Failed(result.Error != null ? EditTexts.Error(result.Error.Value) : result.Detail ?? string.Empty);
                    }

                    _log.Add(plan, RepairState.Written, DateTime.Now);
                    tab.RebuildTree(select);
                    return new RepairApplyResult(true, Strings.Format("Repair_Applied_Card", title));
                }
                finally
                {
                    tab._repairBusy = false;
                    tab.UpdateEditUi();
                }
            }

            private static RepairApplyResult Failed(string why) => new RepairApplyResult(false, Strings.Format("Repair_Failed", why));

            public UndoResult Undo(int group)
            {
                var tab = _tab;
                if (tab._session == null)
                {
                    return new UndoResult(UndoStatus.NotFound, null);
                }

                var result = tab._session.Undo(group);
                if (result.Status == UndoStatus.Done)
                {
                    _log.Remove(group);
                    tab.RebuildTree(tab._tree.SelectedNode?.Tag is FsEntry current ? current.Path : "/");
                }

                return result;
            }
        }
    }
}

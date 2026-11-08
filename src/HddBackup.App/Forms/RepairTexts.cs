using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HddBackup.App.Localization;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Editing;
using HddBackup.Core.Repair;

namespace HddBackup.App.Forms
{
    /// <summary>Turns the language-neutral parts of a repair (kind, change lines, refusals) into text; Core never builds a sentence.</summary>
    internal static class RepairTexts
    {
        public static string Title(RepairTitle title) => Strings.Format("Repair_Title_" + title.Kind, title.Arg1, title.Arg2);

        public static string Why(RepairKind kind) => Strings.Get("Repair_Why_" + kind);

        /// <summary>The risk paragraph of a plan that lowers security; empty for the others.</summary>
        public static string Risk(RepairPlan plan) => plan.Risk == RepairRisk.None ? string.Empty : Strings.Get("Repair_Risk_" + plan.Kind);

        public static string Refusal(RepairRefusal why) => Strings.Get("Repair_Refused_" + why);

        public static string Key(KeyRefusal why) => Strings.Get("Repair_KeyRefused_" + why);

        public static string Change(ChangeLine line)
        {
            string path = line.Path;
            switch (line.What)
            {
                case ChangeWhat.CreateLink:
                    return Strings.Format("Repair_Change_CreateLink", path, line.Before, line.After);
                case ChangeWhat.RemoveLink:
                    return Strings.Format("Repair_Change_RemoveLink", path, line.Before, line.After);
                case ChangeWhat.EditText:
                    return Strings.Format(line.Before.Length == 0 ? "Repair_Change_EditAdd" : "Repair_Change_Edit", path, line.Before, line.After);
                case ChangeWhat.CreateFolder:
                    return Strings.Format(line.After.Length == 0 ? "Repair_Change_CreateFolder" : "Repair_Change_CreateFolderAs", path, line.Before, line.After);
                case ChangeWhat.CreateFile:
                    return Strings.Format(line.After.Length == 0 ? "Repair_Change_CreateFile" : "Repair_Change_CreateFileAs", path, line.Before, line.After);
                case ChangeWhat.SetModeOwner:
                    return Strings.Format("Repair_Change_SetModeOwner", path, line.Before, line.After);
                case ChangeWhat.ChangeShell:
                    return Strings.Format("Repair_Change_ChangeShell", path, line.Before, line.After);
                default:
                    return Strings.Format("Repair_Change_AppendKey", path, line.Before, line.After);
            }
        }
    }

    /// <summary>The "Repairs in this session" section of the copied or saved SSH report.</summary>
    internal sealed class RepairReportTexts : IRepairReportTexts
    {
        public string RepairsCaption => Strings.Get("Repair_ReportCaption");

        public string Repair(RepairLogEntry entry) => Strings.Format("Repair_Report_" + entry.State, RepairTexts.Title(entry.Title));
    }

    /// <summary>What the wizard shows: the SSH check of the file system as it is (or will be after Save) and the repairs offered for it.</summary>
    internal sealed class RepairView
    {
        public RepairView(SshReport report, IReadOnlyList<RepairOffer> offers)
        {
            Report = report;
            Offers = offers ?? Array.Empty<RepairOffer>();
        }

        public SshReport Report { get; }

        public IReadOnlyList<RepairOffer> Offers { get; }
    }

    internal sealed class RepairApplyResult
    {
        public RepairApplyResult(bool ok, string message)
        {
            Ok = ok;
            Message = message;
        }

        public bool Ok { get; }

        /// <summary>For the status line of the wizard: what was done, or why not.</summary>
        public string Message { get; }
    }

    /// <summary>
    /// What the SSH wizard needs from the Files tab to repair (feature 011): whether editing is allowed, a fresh check of the
    /// file system as it will be, and the means to queue or write a repair. The wizard never touches the edit sessions itself.
    /// </summary>
    internal interface ISshRepairHost
    {
        /// <summary>Edit mode is on and the session can take changes.</summary>
        bool CanRepair { get; }

        /// <summary>The file system is a physical card: repairs are written at once and cannot be undone.</summary>
        bool IsCard { get; }

        /// <summary>Raised (on the UI thread) when edit mode, the waiting changes or the open file system changed.</summary>
        event EventHandler Changed;

        /// <summary>The repairs waiting in the diff file of an image (empty on a card).</summary>
        IReadOnlyList<RepairGroup> Waiting { get; }

        /// <summary>The repairs of this session for the report.</summary>
        IReadOnlyList<RepairLogEntry> Log { get; }

        /// <summary>Runs the SSH check again on the file system as it will be and plans the repairs; null when nothing is open.</summary>
        Task<RepairView> ReadAsync();

        /// <summary>Plans adding <paramref name="key"/> for the account on the file system as it will be; null when nothing is open.</summary>
        Task<RepairPlan> PlanAddKeyAsync(string account, PublicKey key);

        /// <summary>Queues the repair (image) or writes it to the card (device).</summary>
        Task<RepairApplyResult> ApplyAsync(RepairPlan plan);

        /// <summary>Removes one waiting repair of an image.</summary>
        UndoResult Undo(int group);
    }
}

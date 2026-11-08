using System;
using System.Collections.Generic;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Editing;

namespace HddBackup.Core.Repair
{
    public enum RepairRisk
    {
        None,

        /// <summary>The repair makes the system easier to get into; the user must confirm it knowingly.</summary>
        LowersSecurity
    }

    /// <summary>Why a reason of the SSH check has no repair here.</summary>
    public enum RepairRefusal
    {
        None,
        NoInstallSection,
        ListedInMatch,
        KeysFromCommand,
        NoHome,
        NoSuitableShell,
        OutsideFileSystem,
        Duplicate,
        NoGroupName,
        NotRepairable,
        Unreadable,
        LinkTooLong,
        AlreadyThere
    }

    /// <summary>What one line of the confirmation window says will happen.</summary>
    public enum ChangeWhat
    {
        CreateLink,
        RemoveLink,
        EditText,
        CreateFolder,
        CreateFile,
        SetModeOwner,
        ChangeShell,
        AppendKey
    }

    /// <summary>One thing a repair changes, in words for the confirmation window. Never holds key text.</summary>
    public sealed class ChangeLine
    {
        public ChangeLine(ChangeWhat what, string path, string before = null, string after = null)
        {
            What = what;
            Path = path;
            Before = before ?? string.Empty;
            After = after ?? string.Empty;
        }

        public ChangeWhat What { get; }

        public string Path { get; }

        /// <summary>The old value (a mode, an owner, a shell, a setting line); empty when there is none.</summary>
        public string Before { get; }

        /// <summary>The new value; for a key, its fingerprint.</summary>
        public string After { get; }
    }

    /// <summary>
    /// One repair the wizard can offer: what it is called, what it changes, whether it lowers security, which other
    /// repairs could solve the same reason, or why there is none. Built from the file system as it is (or will be);
    /// nothing is written until the changes are handed to an edit session.
    /// </summary>
    public sealed class RepairPlan
    {
        private static readonly IReadOnlyList<EditChange> NoChanges = new EditChange[0];
        private static readonly IReadOnlyList<ChangeLine> NoLines = new ChangeLine[0];
        private static readonly IReadOnlyList<RepairPlan> NoPlans = new RepairPlan[0];

        public RepairPlan(RepairTitle title, IReadOnlyList<ChangeLine> summary, IReadOnlyList<EditChange> changes, RepairRisk risk = RepairRisk.None, IReadOnlyList<RepairPlan> alternatives = null)
        {
            Title = title;
            Summary = summary ?? NoLines;
            Changes = changes ?? NoChanges;
            Risk = risk;
            Alternatives = alternatives ?? NoPlans;
        }

        private RepairPlan(RepairTitle title, RepairRefusal why)
        {
            Title = title;
            Unavailable = why;
            Summary = NoLines;
            Changes = NoChanges;
            Alternatives = NoPlans;
        }

        public static RepairPlan Refused(RepairKind kind, RepairRefusal why, string account = null) =>
            new RepairPlan(new RepairTitle(kind, account), why);

        public RepairTitle Title { get; }

        public RepairKind Kind => Title.Kind;

        public IReadOnlyList<ChangeLine> Summary { get; }

        /// <summary>The changes in the order they are made; every prefix of them leaves the system as it was or harmlessly different.</summary>
        public IReadOnlyList<EditChange> Changes { get; }

        public RepairRisk Risk { get; }

        /// <summary>Other plans for the same reason, in the order they should be shown (the safer one first).</summary>
        public IReadOnlyList<RepairPlan> Alternatives { get; }

        /// <summary>Why there is no repair; <see cref="RepairRefusal.None"/> when this plan can be applied.</summary>
        public RepairRefusal Unavailable { get; }

        public bool CanApply => Unavailable == RepairRefusal.None && Changes.Count > 0;
    }

    /// <summary>A reason of the SSH check together with the repair for it (which may be unavailable).</summary>
    public sealed class RepairOffer
    {
        public RepairOffer(LoginReason reason, FindingCode finding, string account, RepairPlan plan, bool offersKey)
        {
            Reason = reason;
            Finding = finding;
            Account = account;
            Plan = plan;
            OffersKey = offersKey;
        }

        public LoginReason Reason { get; }

        /// <summary>The server-level finding the offer answers, or the account finding for an account reason.</summary>
        public FindingCode Finding { get; }

        /// <summary>The account the offer is about; empty for a server-level offer.</summary>
        public string Account { get; }

        /// <summary>Null when the reason has no repair of its own (only a key can help).</summary>
        public RepairPlan Plan { get; }

        /// <summary>The reason is solved by adding a key for the account.</summary>
        public bool OffersKey { get; }
    }
}

using System;
using System.Collections.Generic;

namespace HddBackup.Core.Diagnostics
{
    public enum LoginMethod
    {
        None,
        Password,
        Key,
        Both
    }

    public enum ServerEnabledState
    {
        No,
        Yes,
        Masked,
        OnDemand,
        Legacy
    }

    /// <summary>
    /// A place in the image a statement rests on: a file (and line), or <see cref="Path"/> null for a
    /// built-in default.
    /// </summary>
    public sealed class EvidenceRef
    {
        public EvidenceRef(string path, int line = 0)
        {
            Path = path;
            Line = line;
        }

        public string Path { get; }

        /// <summary>1-based line; 0 when the whole file is meant.</summary>
        public int Line { get; }

        public bool IsDefault => Path == null;

        public static EvidenceRef Default => new EvidenceRef(null);

        public static EvidenceRef From<T>(SettingValue<T> setting)
        {
            return setting.IsDefault ? Default : new EvidenceRef(setting.Source, setting.Line);
        }
    }

    /// <summary>One statement of the SSH check. Language-neutral: the text is a resource string for <see cref="Code"/>.</summary>
    public sealed class Finding
    {
        public Finding(FindingCode code, FindingStatus status, int page, IReadOnlyList<string> args = null, IReadOnlyList<EvidenceRef> evidence = null, LoginReason reason = LoginReason.None)
        {
            Code = code;
            Status = status;
            Page = page;
            Args = args ?? Array.Empty<string>();
            Evidence = evidence ?? Array.Empty<EvidenceRef>();
            Reason = reason;
        }

        public FindingCode Code { get; }

        public FindingStatus Status { get; }

        /// <summary>1 Server, 2 Settings, 3 Accounts, 4 Result.</summary>
        public int Page { get; }

        /// <summary>Data only: user names, paths, values.</summary>
        public IReadOnlyList<string> Args { get; }

        public IReadOnlyList<EvidenceRef> Evidence { get; }

        /// <summary>For <see cref="FindingCode.AccountCannot"/>: why.</summary>
        public LoginReason Reason { get; }
    }

    public sealed class UserLogin
    {
        public UserLogin(string account, LoginMethod method)
        {
            Account = account;
            Method = method;
        }

        public string Account { get; }

        public LoginMethod Method { get; }
    }

    public sealed class Verdict
    {
        public Verdict(VerdictCode code, IReadOnlyList<UserLogin> users, IReadOnlyList<Finding> reasons, IReadOnlyList<Finding> notes)
        {
            Code = code;
            Users = users ?? Array.Empty<UserLogin>();
            Reasons = reasons ?? Array.Empty<Finding>();
            Notes = notes ?? Array.Empty<Finding>();
        }

        public VerdictCode Code { get; }

        /// <summary>For <see cref="VerdictCode.Works"/>: who can log in, and how.</summary>
        public IReadOnlyList<UserLogin> Users { get; }

        /// <summary>Why not, in the order a person would fix them.</summary>
        public IReadOnlyList<Finding> Reasons { get; }

        /// <summary>What could not be checked or may differ.</summary>
        public IReadOnlyList<Finding> Notes { get; }
    }

    public sealed class ServerState
    {
        public bool Installed { get; internal set; }

        /// <summary>The unit that starts the server, e.g. <c>ssh.service</c> or <c>ssh.socket</c>; empty when none.</summary>
        public string Unit { get; internal set; } = string.Empty;

        public ServerEnabledState Enabled { get; internal set; }

        /// <summary>What shows that the server is switched on or off: the start link, the mask, the init script.</summary>
        public IReadOnlyList<string> Evidence { get; internal set; } = Array.Empty<string>();

        /// <summary>What shows that the server is installed: the program and the unit file.</summary>
        public IReadOnlyList<string> InstalledEvidence { get; internal set; } = Array.Empty<string>();

        /// <summary>A unit such as <c>sshswitch.service</c> that can switch SSH on at the first boot from a file on the boot partition.</summary>
        public bool FirstBootSwitchPresent { get; internal set; }
    }

    /// <summary>The result of the SSH check: findings by page, the accounts and the verdict. Holds no secret.</summary>
    public sealed class SshReport
    {
        public SshReport(DiagnosticsSource source)
        {
            Source = source;
        }

        public DiagnosticsSource Source { get; }

        public LinuxRoot System { get; internal set; }

        public ServerState Server { get; internal set; } = new ServerState();

        public SshSettings Settings { get; internal set; }

        public IReadOnlyList<Finding> Findings { get; internal set; } = Array.Empty<Finding>();

        /// <summary>Root, regular accounts, and system accounts that have a usable shell (see <see cref="Account.IsSystemAccount"/>).</summary>
        public IReadOnlyList<Account> Accounts { get; internal set; } = Array.Empty<Account>();

        public Verdict Verdict { get; internal set; }

        /// <summary>Files cut at the size limit or unreadable while reading.</summary>
        public ReadNotes ReadNotes { get; internal set; }
    }
}

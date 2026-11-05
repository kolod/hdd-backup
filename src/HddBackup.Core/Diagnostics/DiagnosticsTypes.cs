using System.Collections.Generic;
using HddBackup.Core.Devices;
using HddBackup.Core.Shrinking;

namespace HddBackup.Core.Diagnostics
{
    /// <summary>
    /// What a diagnostic tool analyses: the file system of one partition of an image or device,
    /// as the Files tab has it open. Exactly one of <see cref="ImagePath"/> and
    /// <see cref="Device"/> is set.
    /// </summary>
    public sealed class DiagnosticsSource
    {
        public string ImagePath { get; set; }

        public DeviceInfo Device { get; set; }

        public int PartitionNumber { get; set; }

        /// <summary>For window headers and reports, e.g. "raspberry-pi.img".</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Clean, needs repair or damaged (null when unknown); drives the "not cleanly unmounted" warning.</summary>
        public FileSystemState State { get; set; }

        /// <summary>The source also has a FAT partition that the tools do not read (the boot partition).</summary>
        public bool HasUnreadBootPartition { get; set; }
    }

    public enum StartupKind
    {
        Service,
        Timer,
        Socket,
        BootScript,
        Desktop,
        UserService,
        Cron
    }

    /// <summary>Why a startup entry or a scheduled task will not run (<see cref="None"/> for active ones).</summary>
    public enum InactiveReason
    {
        None,
        Disabled,
        Masked,
        UnitMissing,
        TargetNotReached,
        HiddenEntry,
        NotExecutable,
        IgnoredName,
        NothingRunsIt,
        NoSuchUser,
        NoCron,
        Malformed,
        Unreadable
    }

    public enum FindingStatus
    {
        Ok,
        Problem,
        Unknown,
        Info
    }

    /// <summary>Every statement of the SSH check; the text of each is a resource string <c>Ssh_F_&lt;Code&gt;</c>.</summary>
    public enum FindingCode
    {
        ServerInstalled,
        ServerMissing,
        ServerEnabled,
        ServerOnDemand,
        ServerNotEnabled,
        ServerMasked,
        ServerLegacy,
        BootFlagNotRead,
        Port,
        RootLogin,
        PasswordAuthOn,
        PasswordAuthOff,
        KeyAuthOn,
        KeyAuthOff,
        EmptyPasswords,
        UserRestriction,
        SettingDefault,
        ConditionalSettings,
        KeysFromCommand,
        AccountCanLogIn,
        AccountCannot,
        NoRegularAccount,
        ShadowUnreadable,
        ConfigMissing
    }

    public enum VerdictCode
    {
        Works,
        NotEnabled,
        NotEnabledBootFlagPossible,
        NoServer,
        NobodyCanLogIn,
        Undetermined,
        NotALinuxRoot
    }

    public enum LoginAnswer
    {
        Yes,
        No,
        Unknown
    }

    /// <summary>The state of an account's password; the password itself is never kept.</summary>
    public enum PasswordState
    {
        Set,
        Locked,
        NoPassword,
        Empty,
        Unknown
    }

    /// <summary>Why an account cannot log in; the text is a resource string <c>Ssh_R_&lt;Reason&gt;</c>.</summary>
    public enum LoginReason
    {
        None,
        NoShell,
        PasswordLocked,
        NoPasswordNoKey,
        RootNotAllowed,
        NotInAllowUsers,
        Denied,
        PasswordAuthOffNoKey,
        KeyAuthOffNoPassword,
        EmptyPasswordRefused,
        LockedRefused,
        MethodsNeedBoth
    }

    public enum ScheduleClass
    {
        AtBoot,
        EveryMinute,
        EveryNMinutes,
        HourlyAt,
        DailyAt,
        WeeklyOn,
        MonthlyOn,
        YearlyOn,

        /// <summary>anacron: once every N days (<see cref="CronSchedule.Number"/>).</summary>
        EveryNDays,
        Custom
    }

    /// <summary>A setting with the place it comes from. <see cref="Source"/> is null for a built-in default.</summary>
    public sealed class SettingValue<T>
    {
        public SettingValue(T value, string source, int line, bool conditional)
        {
            Value = value;
            Source = source;
            Line = line;
            Conditional = conditional;
        }

        public T Value { get; }

        /// <summary>Image path of the file the value comes from; null for the default.</summary>
        public string Source { get; }

        public int Line { get; }

        /// <summary>The keyword also appears inside a <c>Match</c> block, so the value may differ for some users.</summary>
        public bool Conditional { get; }

        public bool IsDefault => Source == null;
    }

    /// <summary>Notes a tool collects while reading: files cut at the size limit, lists cut, unreadable files.</summary>
    public sealed class ReadNotes
    {
        private readonly List<string> _truncated = new List<string>();
        private readonly List<string> _cut = new List<string>();
        private readonly List<string> _unreadable = new List<string>();

        public IReadOnlyList<string> TruncatedFiles => _truncated;

        public IReadOnlyList<string> CutDirectories => _cut;

        public IReadOnlyList<string> UnreadableFiles => _unreadable;

        internal void Truncated(string path)
        {
            if (!_truncated.Contains(path))
            {
                _truncated.Add(path);
            }
        }

        internal void Cut(string path)
        {
            if (!_cut.Contains(path))
            {
                _cut.Add(path);
            }
        }

        internal void Unreadable(string path)
        {
            if (!_unreadable.Contains(path))
            {
                _unreadable.Add(path);
            }
        }
    }
}

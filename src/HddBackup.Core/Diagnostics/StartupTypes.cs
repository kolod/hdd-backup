using System;
using System.Collections.Generic;
using System.Linq;

namespace HddBackup.Core.Diagnostics
{
    /// <summary>One thing that starts by itself, or one scheduled task. Language-neutral; holds data from the image only.</summary>
    public sealed class StartupEntry
    {
        public StartupKind Kind { get; internal set; }

        /// <summary>The unit, script, desktop entry or task name.</summary>
        public string Name { get; internal set; } = string.Empty;

        /// <summary>What it runs, verbatim from the image (it is the person's own configuration).</summary>
        public string Command { get; internal set; } = string.Empty;

        /// <summary>When it runs; null for entries that have no schedule (services, desktop entries).</summary>
        public CronSchedule Schedule { get; internal set; }

        /// <summary>The original schedule text (the five fields, <c>OnCalendar=</c>), for tooltips.</summary>
        public string ScheduleText { get; internal set; } = string.Empty;

        /// <summary>Who it runs as; empty when unknown.</summary>
        public string User { get; internal set; } = string.Empty;

        /// <summary>The file or target the entry comes from, e.g. <c>multi-user.target</c> or <c>/etc/crontab</c>.</summary>
        public string Source { get; internal set; } = string.Empty;

        public InactiveReason InactiveReason { get; internal set; }

        public bool Active => InactiveReason == InactiveReason.None;

        /// <summary>Image paths the entry rests on.</summary>
        public IReadOnlyList<string> Evidence { get; internal set; } = Array.Empty<string>();
    }

    /// <summary>Things the user should know about the list as a whole.</summary>
    public enum StartupNoteCode
    {
        /// <summary>Cron is not installed on this system.</summary>
        NoCron,

        /// <summary>Cron is installed but its service is not enabled.</summary>
        CronNotEnabled,

        /// <summary>The list was cut at the entry limit.</summary>
        ListCut
    }

    public sealed class StartupNote
    {
        public StartupNote(StartupNoteCode code, params string[] args)
        {
            Code = code;
            Args = args;
        }

        public StartupNoteCode Code { get; }

        public IReadOnlyList<string> Args { get; }
    }

    /// <summary>The startup list of an opened system.</summary>
    public sealed class StartupResult
    {
        public StartupResult(DiagnosticsSource source)
        {
            Source = source;
        }

        public DiagnosticsSource Source { get; }

        public LinuxRoot System { get; internal set; }

        /// <summary>Active entries first, then by kind and name; inactive entries follow.</summary>
        public IReadOnlyList<StartupEntry> Entries { get; internal set; } = Array.Empty<StartupEntry>();

        public IReadOnlyList<StartupNote> Notes { get; internal set; } = Array.Empty<StartupNote>();

        /// <summary>Files cut at the size limit or unreadable while reading.</summary>
        public ReadNotes ReadNotes { get; internal set; }

        public int ActiveCount => Entries.Count(e => e.Active);

        public int InactiveCount => Entries.Count - ActiveCount;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HddBackup.Core.Shrinking;

namespace HddBackup.Core.Diagnostics
{
    /// <summary>The words of a report, supplied by the UI in the interface language (research R-10).</summary>
    public interface IReportTexts
    {
        string SshTitle(string source);

        string StartupTitle(string source);

        string DateLine(DateTime now);

        /// <summary>The warning for a file system that was not cleanly unmounted or is damaged; null when it is clean.</summary>
        string StateWarning(FileSystemState state);

        /// <summary>"1 Server", "2 Settings", "3 Accounts" for the pages of the SSH check.</summary>
        string StepTitle(int page);

        string Glyph(FindingStatus status);

        string Finding(Finding finding);

        string Where(EvidenceRef evidence);

        string Verdict(Verdict verdict);

        string WhyCaption { get; }

        string NotesCaption { get; }

        string AccountLine(Account account);

        string NotLinux { get; }

        string Empty { get; }

        IReadOnlyList<string> StartupColumns { get; }

        IReadOnlyList<string> StartupRow(StartupEntry entry);

        string StartupNote(StartupNote note);

        string Truncated(string path);

        string Unreadable(string path);
    }

    /// <summary>
    /// Builds the text a person can copy or save: what the window shows, with the source, the date and the notes
    /// (research R-14). It reads only the structured results, which hold no password hash, key or other secret by
    /// construction, so a report cannot contain one either.
    /// </summary>
    public static class ReportBuilder
    {
        private const string Eol = "\r\n";

        public static string Ssh(SshReport report, IReportTexts t, DateTime now)
        {
            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            var sb = new StringBuilder();
            Header(sb, t.SshTitle(report.Source?.DisplayName ?? string.Empty), report.Source, t, now);
            if (report.Verdict.Code == VerdictCode.NotALinuxRoot)
            {
                sb.Append(t.NotLinux).Append(Eol);
                return sb.ToString();
            }

            sb.Append(t.Glyph(VerdictStatus(report.Verdict.Code))).Append(' ').Append(t.Verdict(report.Verdict)).Append(Eol).Append(Eol);
            for (int page = 1; page <= 2; page++)
            {
                sb.Append(t.StepTitle(page)).Append(Eol);
                foreach (var f in report.Findings.Where(x => x.Page == page))
                {
                    AppendFinding(sb, f, t, "  ");
                }

                sb.Append(Eol);
            }

            sb.Append(t.StepTitle(3)).Append(Eol);
            foreach (var a in report.Accounts.Where(x => !x.IsSystemAccount))
            {
                sb.Append("  ").Append(t.AccountLine(a)).Append(Eol);
            }

            sb.Append(Eol);
            if (report.Verdict.Reasons.Count > 0)
            {
                sb.Append(t.WhyCaption).Append(Eol);
                foreach (var f in report.Verdict.Reasons)
                {
                    AppendFinding(sb, f, t, "  ");
                }

                sb.Append(Eol);
            }

            AppendNotes(sb, report.Verdict.Notes, report.ReadNotes, t);
            return sb.ToString();
        }

        /// <param name="visible">The rows the window shows (after its filters).</param>
        /// <param name="countsLine">The line the window shows under the list ("42 entries (8 inactive)").</param>
        public static string Startup(StartupResult result, IEnumerable<StartupEntry> visible, string countsLine, IReportTexts t, DateTime now)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            var sb = new StringBuilder();
            Header(sb, t.StartupTitle(result.Source?.DisplayName ?? string.Empty), result.Source, t, now);
            if (result.System != null && !result.System.IsLinux)
            {
                sb.Append(t.NotLinux).Append(Eol);
                return sb.ToString();
            }

            var rows = visible.ToList();
            if (rows.Count == 0)
            {
                sb.Append(t.Empty).Append(Eol);
            }
            else
            {
                sb.Append(string.Join("\t", t.StartupColumns)).Append(Eol);
                foreach (var e in rows)
                {
                    sb.Append(string.Join("\t", t.StartupRow(e).Select(Flatten))).Append(Eol);
                }
            }

            sb.Append(Eol).Append(countsLine).Append(Eol);
            foreach (var n in result.Notes)
            {
                sb.Append(t.StartupNote(n)).Append(Eol);
            }

            if (result.ReadNotes != null)
            {
                foreach (string p in result.ReadNotes.TruncatedFiles)
                {
                    sb.Append(t.Truncated(p)).Append(Eol);
                }

                foreach (string p in result.ReadNotes.UnreadableFiles)
                {
                    sb.Append(t.Unreadable(p)).Append(Eol);
                }
            }

            return sb.ToString();
        }

        private static void Header(StringBuilder sb, string title, DiagnosticsSource source, IReportTexts t, DateTime now)
        {
            sb.Append(title).Append(Eol);
            sb.Append(t.DateLine(now)).Append(Eol);
            string warning = source?.State == null ? null : t.StateWarning(source.State);
            if (!string.IsNullOrEmpty(warning))
            {
                sb.Append(warning).Append(Eol);
            }

            sb.Append(Eol);
        }

        private static void AppendFinding(StringBuilder sb, Finding f, IReportTexts t, string indent)
        {
            sb.Append(indent).Append(t.Glyph(f.Status)).Append(' ').Append(t.Finding(f)).Append(Eol);
            foreach (var e in f.Evidence)
            {
                sb.Append(indent).Append("    ").Append(t.Where(e)).Append(Eol);
            }
        }

        private static void AppendNotes(StringBuilder sb, IReadOnlyList<Finding> notes, ReadNotes read, IReportTexts t)
        {
            bool any = notes.Count > 0 || (read != null && (read.TruncatedFiles.Count > 0 || read.UnreadableFiles.Count > 0));
            if (!any)
            {
                return;
            }

            sb.Append(t.NotesCaption).Append(Eol);
            foreach (var f in notes)
            {
                AppendFinding(sb, f, t, "  ");
            }

            if (read != null)
            {
                foreach (string p in read.TruncatedFiles)
                {
                    sb.Append("  ").Append(t.Truncated(p)).Append(Eol);
                }

                foreach (string p in read.UnreadableFiles)
                {
                    sb.Append("  ").Append(t.Unreadable(p)).Append(Eol);
                }
            }
        }

        private static FindingStatus VerdictStatus(VerdictCode code)
        {
            switch (code)
            {
                case VerdictCode.Works:
                    return FindingStatus.Ok;
                case VerdictCode.NotEnabledBootFlagPossible:
                case VerdictCode.Undetermined:
                    return FindingStatus.Unknown;
                case VerdictCode.NotALinuxRoot:
                    return FindingStatus.Info;
                default:
                    return FindingStatus.Problem;
            }
        }

        /// <summary>A tab or line break inside a value would break the table.</summary>
        private static string Flatten(string value)
        {
            return (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}

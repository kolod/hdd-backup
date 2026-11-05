using System;
using System.Collections.Generic;
using System.Linq;
using HddBackup.App.Localization;
using HddBackup.Core.Diagnostics;

namespace HddBackup.App.Forms
{
    /// <summary>
    /// Turns the language-neutral results of the diagnostic tools (codes, names, paths) into
    /// localized text. Core never builds a sentence; every code has a resource string, so a
    /// language switch can re-render an open window (research R-10).
    /// </summary>
    internal static class DiagnosticTexts
    {
        // --- SSH check --------------------------------------------------------------------------

        /// <summary>The sentence for one finding.</summary>
        public static string Finding(Finding f)
        {
            string key = "Ssh_F_" + f.Code;
            var a = f.Args;
            switch (f.Code)
            {
                case FindingCode.RootLogin:
                    return Strings.Format(key, Strings.Get("Ssh_Root_" + a[0]));
                case FindingCode.EmptyPasswords:
                    return Strings.Format(key, Strings.Get("Ssh_Empty_" + a[0]));
                case FindingCode.AccountCanLogIn:
                    return Strings.Format(key, a[0], Strings.Get("Ssh_With_" + a[1]));
                case FindingCode.AccountCannot:
                    return Strings.Format(key, a[0], Reason(f.Reason));
                default:
                    return a.Count == 0 ? Strings.Get(key) : Strings.Format(key, a.Cast<object>().ToArray());
            }
        }

        /// <summary>Why an account cannot log in, as a clause ("its password is locked").</summary>
        public static string Reason(LoginReason reason)
        {
            return reason == LoginReason.None ? string.Empty : Strings.Get("Ssh_R_" + reason);
        }

        /// <summary>The verdict as one sentence; for "works" it names who can log in and how.</summary>
        public static string Verdict(Verdict v)
        {
            switch (v.Code)
            {
                case VerdictCode.NotALinuxRoot:
                    return Strings.Get("Ssh_NotLinux");
                case VerdictCode.Works:
                    string who = string.Join("; ", v.Users.Select(u => u.Account + " — " + Strings.Get("Ssh_With_" + u.Method)));
                    return Strings.Format("Ssh_V_Works", who);
                default:
                    return Strings.Get("Ssh_V_" + v.Code);
            }
        }

        /// <summary>Where a value comes from: "/etc/ssh/sshd_config, line 3", the file alone, or "default".</summary>
        public static string Where(EvidenceRef e)
        {
            if (e.IsDefault)
            {
                return Strings.Get("Ssh_FromDefault");
            }

            return e.Line > 0 ? Strings.Format("Ssh_SettingFrom", e.Path, e.Line) : e.Path;
        }

        public static string Password(PasswordState state) => Strings.Get("Ssh_Pw_" + state);

        /// <summary>The "can log in" cell of the accounts list.</summary>
        public static string CanLogIn(Account a)
        {
            switch (a.CanLogIn)
            {
                case LoginAnswer.Yes:
                    return Strings.Format("Ssh_CanYes", Strings.Get("Ssh_With_" + a.Method));
                case LoginAnswer.No:
                    return Strings.Format("Ssh_CanNo", Reason(a.Reason));
                default:
                    return Strings.Get("Ssh_CanUnknown");
            }
        }

        /// <summary>The "keys" cell: the number found, or "unknown" when keys may exist that were not counted.</summary>
        public static string Keys(Account a)
        {
            if (a.KeysUnknown && a.KeyCount == 0)
            {
                return Strings.Get("Ssh_KeysUnknown");
            }

            return a.KeyCount.ToString(LanguageService.Culture) + (a.KeysUnknown ? "+" : string.Empty);
        }

        /// <summary>The glyph in front of a finding; colour is never the only signal.</summary>
        public static string Glyph(FindingStatus status)
        {
            switch (status)
            {
                case FindingStatus.Ok:
                    return "✔";
                case FindingStatus.Problem:
                    return "✖";
                case FindingStatus.Unknown:
                    return "⚠";
                default:
                    return "ℹ";
            }
        }

        // --- Startup and scheduled tasks ----------------------------------------------------------

        public static string KindName(StartupKind kind) => Strings.Get("Startup_K_" + kind);

        /// <summary>The "status" cell: "active", or why the entry will not run.</summary>
        public static string Status(StartupEntry e)
        {
            return e.Active ? Strings.Get("Startup_Active") : Strings.Get("Startup_R_" + e.InactiveReason);
        }

        /// <summary>When the entry runs, in words; empty for entries without a schedule.</summary>
        public static string When(StartupEntry e)
        {
            return e.Schedule == null ? string.Empty : ScheduleWords.Format(e.Schedule);
        }

        /// <summary>The "what it runs / when" cell: the schedule in words, then the command as written in the image.</summary>
        public static string Runs(StartupEntry e)
        {
            string when = When(e);
            if (when.Length == 0)
            {
                return e.Command;
            }

            return e.Command.Length == 0 ? when : when + " — " + e.Command;
        }

        /// <summary>Tooltip of a row: the original schedule text and the files the entry rests on.</summary>
        public static string Tooltip(StartupEntry e)
        {
            var lines = new List<string>();
            if (e.ScheduleText.Length > 0)
            {
                lines.Add(e.ScheduleText);
            }

            lines.AddRange(e.Evidence);
            return string.Join("\n", lines);
        }

        public static string Note(StartupNote n)
        {
            switch (n.Code)
            {
                case StartupNoteCode.NoCron:
                    return Strings.Get("Startup_N_NoCron");
                case StartupNoteCode.CronNotEnabled:
                    return Strings.Get("Startup_N_CronNotEnabled");
                default:
                    return Strings.Format("Diag_N_ListCut", n.Args.Cast<object>().ToArray());
            }
        }

        /// <summary>The evidence paths of a finding, one per line, for the grey text under it.</summary>
        public static string EvidenceText(IEnumerable<EvidenceRef> evidence)
        {
            return string.Join("\n", evidence.Select(Where));
        }
    }
}

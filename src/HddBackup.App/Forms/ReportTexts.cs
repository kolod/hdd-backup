using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using HddBackup.App.Localization;
using HddBackup.App.Settings;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Shrinking;

namespace HddBackup.App.Forms
{
    /// <summary>The words of a copied or saved report: the same sentences as the windows, in the interface language.</summary>
    internal sealed class ReportTexts : IReportTexts
    {
        public string SshTitle(string source) => Strings.Format("Ssh_Title", source);

        public string StartupTitle(string source) => Strings.Format("Startup_Title", source);

        public string DateLine(DateTime now) => Strings.Format("Report_Date", now.ToString("g", LanguageService.Culture));

        public string StateWarning(FileSystemState state)
        {
            if (state == null || state.Condition == FileSystemCondition.Clean)
            {
                return null;
            }

            return Strings.Get(state.Condition == FileSystemCondition.Damaged ? "Files_StateDamaged" : "Files_StateNeedsRepair");
        }

        public string StepTitle(int page) => page + " " + Strings.Get("Ssh_Step" + page);

        public string Glyph(FindingStatus status) => DiagnosticTexts.Glyph(status);

        public string Finding(Finding finding) => DiagnosticTexts.Finding(finding);

        public string Where(EvidenceRef evidence) => DiagnosticTexts.Where(evidence);

        public string Verdict(Verdict verdict) => DiagnosticTexts.Verdict(verdict);

        public string WhyCaption => Strings.Get("Ssh_Why");

        public string NotesCaption => Strings.Get("Ssh_CouldNotCheck");

        public string AccountLine(Account a)
        {
            return string.Join(
                "\t",
                a.Name,
                a.Shell,
                Strings.Get("Ssh_ColPassword") + ": " + DiagnosticTexts.Password(a.Password),
                Strings.Get("Ssh_ColKeys") + ": " + DiagnosticTexts.Keys(a),
                Strings.Get("Ssh_ColCanLogIn") + ": " + DiagnosticTexts.CanLogIn(a));
        }

        public string NotLinux => Strings.Get("Ssh_NotLinux");

        public string Empty => Strings.Get("Startup_Empty");

        public IReadOnlyList<string> StartupColumns => new[]
        {
            Strings.Get("Startup_ColKind"), Strings.Get("Startup_ColName"), Strings.Get("Startup_ColRuns"),
            Strings.Get("Startup_ColUser"), Strings.Get("Startup_ColSource"), Strings.Get("Startup_ColStatus")
        };

        public IReadOnlyList<string> StartupRow(StartupEntry e)
        {
            return new[] { DiagnosticTexts.KindName(e.Kind), e.Name, DiagnosticTexts.Runs(e), e.User, e.Source, DiagnosticTexts.Status(e) };
        }

        public string StartupNote(StartupNote note) => DiagnosticTexts.Note(note);

        public string Truncated(string path) => Strings.Format("Diag_N_Truncated", path);

        public string Unreadable(string path) => Strings.Format("Diag_N_Unreadable", path);
    }

    /// <summary>Copy and Save as… of a report; neither replaces a file without asking.</summary>
    internal static class ReportActions
    {
        /// <summary>Copies the text; returns the message to show (success or "clipboard busy").</summary>
        public static string Copy(string text, out bool ok)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    ok = true;
                    return Strings.Get("Report_Copied");
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    Thread.Sleep(60);
                }
            }

            ok = false;
            return Strings.Get("Report_CopyFailed");
        }

        /// <summary>
        /// Asks for a file name (the dialog asks before replacing) and writes the text as UTF-8 with a byte order
        /// mark, so that Windows 7 Notepad shows non-Latin text. Returns null when the person cancelled.
        /// </summary>
        public static string Save(string text, string defaultName, PortableSettings settings, IWin32Window owner, out bool ok)
        {
            ok = true;
            using (var dialog = new SaveFileDialog
            {
                Title = Strings.Get("Report_SaveTitle"),
                Filter = Strings.Get("Dialog_ReportFilter"),
                FileName = SafeName(defaultName) + ".txt",
                DefaultExt = "txt",
                AddExtension = true,
                OverwritePrompt = true
            })
            {
                string folder = settings?.LastReportFolder;
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    dialog.InitialDirectory = folder;
                }

                if (dialog.ShowDialog(owner) != DialogResult.OK)
                {
                    return null;
                }

                try
                {
                    File.WriteAllText(dialog.FileName, text, new UTF8Encoding(true));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    ok = false;
                    return Strings.Format("Report_Failed", ex.Message);
                }

                if (settings != null)
                {
                    settings.LastReportFolder = Path.GetDirectoryName(dialog.FileName);
                }

                return Strings.Format("Report_Saved", dialog.FileName);
            }
        }

        private static string SafeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }
}

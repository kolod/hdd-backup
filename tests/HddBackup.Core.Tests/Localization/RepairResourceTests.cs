using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using HddBackup.Core.Editing;
using HddBackup.Core.Repair;
using Xunit;

namespace HddBackup.Core.Tests.Localization
{
    /// <summary>Feature 011 (contracts/ui.md): every code a repair can show has a string in every language.</summary>
    public class RepairResourceTests
    {
        private static readonly string[] Files =
        {
            "Strings.resx", "Strings.uk.resx", "Strings.de.resx", "Strings.fr.resx", "Strings.es.resx", "Strings.ja.resx"
        };

        private static HashSet<string> Keys(string file)
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "HddBackup.sln")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            var doc = XDocument.Load(Path.Combine(dir.FullName, "src", "HddBackup.App", "Resources", file));
            return new HashSet<string>(doc.Root.Elements("data").Select(e => (string)e.Attribute("name")), StringComparer.Ordinal);
        }

        private static IEnumerable<string> Required()
        {
            foreach (var kind in Enum.GetValues(typeof(RepairKind)).Cast<RepairKind>().Where(k => k != RepairKind.None))
            {
                yield return "Repair_Title_" + kind;
                yield return "Repair_Why_" + kind;
            }

            yield return "Repair_Risk_" + RepairKind.RootPassword;
            yield return "Repair_Risk_" + RepairKind.EnablePasswordAuth;
            foreach (var why in Enum.GetValues(typeof(RepairRefusal)).Cast<RepairRefusal>().Where(r => r != RepairRefusal.None))
            {
                yield return "Repair_Refused_" + why;
            }

            foreach (var why in Enum.GetValues(typeof(KeyRefusal)).Cast<KeyRefusal>().Where(r => r != KeyRefusal.None))
            {
                yield return "Repair_KeyRefused_" + why;
            }

            foreach (var state in Enum.GetValues(typeof(RepairState)).Cast<RepairState>())
            {
                yield return "Repair_Report_" + state;
            }

            // the lines of the confirmation window: one string per kind of change (some have a second form)
            foreach (string name in new[]
            {
                "CreateLink", "RemoveLink", "Edit", "EditAdd", "CreateFolder", "CreateFolderAs", "CreateFile", "CreateFileAs",
                "SetModeOwner", "ChangeShell", "AppendKey"
            })
            {
                yield return "Repair_Change_" + name;
            }

            foreach (string name in new[]
            {
                "Hint_EditOff", "Fix", "AddKey", "Waiting", "Undo", "VerdictAfter", "Busy", "Applied_Image", "Applied_Card", "Failed",
                "UndoBlocked", "ReportCaption", "Confirm_Title", "Confirm_Intro", "Confirm_Card", "Confirm_Image", "Confirm_Apply",
                "Confirm_Others", "Risk_Confirm", "Key_Title", "Key_Account", "Key_Box", "Key_Load", "Key_Facts", "Key_WarnLegacy",
                "Key_WarnOptions", "Key_Target", "Key_Add", "Key_LoadFilter", "Key_LoadFailed", "Key_NoAccounts"
            })
            {
                yield return "Repair_" + name;
            }
        }

        [Theory]
        [InlineData("Strings.resx")]
        [InlineData("Strings.uk.resx")]
        [InlineData("Strings.de.resx")]
        [InlineData("Strings.fr.resx")]
        [InlineData("Strings.es.resx")]
        [InlineData("Strings.ja.resx")]
        public void EveryRepairCodeHasAStringInEveryLanguage(string file)
        {
            var keys = Keys(file);
            foreach (string key in Required())
            {
                Assert.True(keys.Contains(key), file + ": missing " + key);
            }
        }

        [Fact]
        public void TheChangeKindsThatTheTextsHandleAreAllOfThem()
        {
            // RepairTexts.Change has a branch for every ChangeWhat; a new one needs a string and a branch
            Assert.Equal(8, Enum.GetValues(typeof(ChangeWhat)).Length);
        }
    }
}

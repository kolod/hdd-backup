using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using HddBackup.App.Localization;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Repair;

namespace HddBackup.App.Forms
{
    /// <summary>
    /// Asks for a public key and the account it is for, shows its type, fingerprint and target file, and plans the addition
    /// (feature 011, FR-009 to FR-014). A private key is refused and the box is cleared; nothing typed here is kept after the dialog closes.
    /// </summary>
    internal sealed class AddKeyDialog : Form
    {
        private const int MaxFileBytes = PublicKey.MaxLength;

        private readonly Func<string, PublicKey, Task<RepairPlan>> _planFor;
        private readonly ComboBox _account = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
        private readonly TextBox _key = new TextBox { Multiline = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, Height = 76, Dock = DockStyle.Top, AcceptsReturn = false };
        private readonly Button _load = Ui.Button();
        private readonly Label _facts = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 6, 0, 0) };
        private readonly Label _warning = new Label { AutoSize = true, UseMnemonic = false, ForeColor = Color.FromArgb(0x9A, 0x6A, 0x00), Margin = new Padding(0, 4, 0, 0) };
        private readonly Label _message = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 6, 0, 0) };
        private readonly Button _add = Ui.Button();
        private readonly Button _cancel = Ui.Button();
        private int _generation;
        private bool _clearing;

        public AddKeyDialog(IReadOnlyList<Account> accounts, string preselect, Func<string, PublicKey, Task<RepairPlan>> planFor, bool isCard)
        {
            _planFor = planFor;
            Text = Strings.Get("Repair_Key_Title");
            Icon = AppIcon.Icon;
            Font = UiFont.For(LanguageService.Current);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(600, 420);

            foreach (var a in accounts)
            {
                _account.Items.Add(a.Name);
            }

            int at = preselect == null ? -1 : _account.Items.IndexOf(preselect);
            _account.SelectedIndex = at >= 0 ? at : (_account.Items.Count > 0 ? 0 : -1);
            _account.SelectedIndexChanged += (s, e) => Evaluate();

            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14, 12, 14, 0) };
            var accountCaption = new Label { Text = Strings.Get("Repair_Key_Account"), AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 2) };
            var boxCaption = new Label { Text = Strings.Get("Repair_Key_Box"), AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 10, 0, 2) };
            _account.Width = 300;
            _key.Width = 570;
            _facts.MaximumSize = new Size(570, 0);
            _warning.MaximumSize = new Size(570, 0);
            _message.MaximumSize = new Size(570, 0);
            _load.Text = Strings.Get("Repair_Key_Load");
            _load.Click += (s, e) => LoadFile();
            _key.TextChanged += (s, e) =>
            {
                if (!_clearing)
                {
                    Evaluate();
                }
            };
            body.Controls.AddRange(new Control[] { accountCaption, _account, boxCaption, _key, _load, _facts, _warning, _message });

            string noteKey = isCard ? "Repair_Confirm_Card" : "Repair_Confirm_Image";
            var note = new Label { Text = Strings.Get(noteKey), AutoSize = true, UseMnemonic = false, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 10, 0, 0), MaximumSize = new Size(570, 0) };
            body.Controls.Add(note);

            _add.Text = Strings.Get("Repair_Key_Add");
            _cancel.Text = Strings.Get("Edit_Cancel");
            _add.DialogResult = DialogResult.OK;
            _cancel.DialogResult = DialogResult.Cancel;
            _add.Enabled = false;
            var buttons = Ui.Flow(rightToLeft: true);
            buttons.Dock = DockStyle.Bottom;
            buttons.Padding = new Padding(14, 8, 14, 12);
            buttons.Controls.Add(_cancel);
            buttons.Controls.Add(_add);
            Controls.Add(body);
            Controls.Add(buttons);
            AcceptButton = _add;
            CancelButton = _cancel;
            FormClosed += (s, e) => _key.Text = string.Empty; // nothing typed here outlives the dialog
            Evaluate();
        }

        /// <summary>The plan to add the key; set when the dialog closes with OK.</summary>
        public RepairPlan Plan { get; private set; }

        private void LoadFile()
        {
            using (var dialog = new OpenFileDialog { Filter = Strings.Get("Repair_Key_LoadFilter"), CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    var info = new FileInfo(dialog.FileName);
                    if (info.Length > MaxFileBytes)
                    {
                        Say(Strings.Get("Repair_KeyRefused_TooLong"), true);
                        return;
                    }

                    _key.Text = File.ReadAllText(dialog.FileName).Trim();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Say(Strings.Format("Repair_Key_LoadFailed", ex.Message), true);
                }
            }
        }

        private void Say(string text, bool error)
        {
            _message.Text = text;
            _message.ForeColor = error ? Color.DarkRed : SystemColors.GrayText;
        }

        private async void Evaluate()
        {
            int generation = ++_generation;
            Plan = null;
            _add.Enabled = false;
            _facts.Text = string.Empty;
            _warning.Text = string.Empty;
            if (_account.Items.Count == 0)
            {
                Say(Strings.Get("Repair_Key_NoAccounts"), true);
                return;
            }

            if (!PublicKey.TryParse(_key.Text, out var key, out var refusal))
            {
                if (refusal == KeyRefusal.PrivateKey)
                {
                    _clearing = true;
                    _key.Text = string.Empty; // a private key is never kept, not even on the screen
                    _clearing = false;
                }

                Say(RepairTexts.Key(refusal), refusal != KeyRefusal.Empty);
                return;
            }

            _facts.Text = Strings.Format("Repair_Key_Facts", key.Type, key.Fingerprint, key.Comment.Length == 0 ? "-" : key.Comment);
            var warnings = new List<string>();
            if (key.Warnings.HasFlag(KeyWarnings.LegacyType))
            {
                warnings.Add(Strings.Get("Repair_Key_WarnLegacy"));
            }

            if (key.Warnings.HasFlag(KeyWarnings.HasOptions))
            {
                warnings.Add(Strings.Get("Repair_Key_WarnOptions"));
            }

            _warning.Text = string.Join(" ", warnings);
            Say(Strings.Get("Repair_Busy"), false);
            string account = (string)_account.SelectedItem;
            RepairPlan plan = null;
            try
            {
                plan = await _planFor(account, key);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException)
            {
                Say(Strings.Format("Repair_Failed", ex.Message), true);
                return;
            }

            if (generation != _generation || IsDisposed)
            {
                return; // the key or the account changed meanwhile
            }

            if (plan == null)
            {
                Say(Strings.Format("Repair_Failed", string.Empty), true);
                return;
            }

            if (!plan.CanApply)
            {
                Say(RepairTexts.Refusal(plan.Unavailable), true);
                return;
            }

            var target = plan.Summary.FirstOrDefault(l => l.What == ChangeWhat.AppendKey);
            Say(Strings.Format("Repair_Key_Target", target == null ? string.Empty : target.Path), false);
            Plan = plan;
            _add.Enabled = true;
        }
    }
}

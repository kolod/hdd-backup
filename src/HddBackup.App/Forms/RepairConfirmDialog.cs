using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using HddBackup.App.Localization;
using HddBackup.Core.Repair;

namespace HddBackup.App.Forms
{
    /// <summary>
    /// Shows exactly what a repair will change before anything is queued or written (feature 011, FR-004). A repair that lowers
    /// security shows its risk and needs a tick before Apply; other ways to solve the same reason are offered as a list.
    /// </summary>
    internal sealed class RepairConfirmDialog : Form
    {
        private readonly bool _isCard;
        private readonly List<RepairPlan> _plans = new List<RepairPlan>();
        private readonly Label _why = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 10) };
        private readonly Label _intro = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 4) };
        private readonly TextBox _changes = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 120, Dock = DockStyle.Top, BackColor = SystemColors.Window };
        private readonly Label _risk = new Label { AutoSize = true, UseMnemonic = false, ForeColor = Color.DarkRed, Margin = new Padding(0, 10, 0, 4) };
        private readonly CheckBox _understand = new CheckBox { AutoSize = true, UseMnemonic = false };
        private readonly Label _note = new Label { AutoSize = true, UseMnemonic = false, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 10, 0, 0) };
        private readonly Label _others = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 10, 0, 2) };
        private readonly FlowLayoutPanel _choices = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        private readonly Button _apply = Ui.Button();
        private readonly Button _cancel = Ui.Button();
        private int _index;

        public RepairConfirmDialog(RepairPlan plan, bool isCard)
        {
            _isCard = isCard;
            _plans.Add(plan);
            _plans.AddRange(plan.Alternatives.Where(a => a.CanApply));
            Icon = AppIcon.Icon;
            Font = UiFont.For(LanguageService.Current);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(600, 440);

            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14, 12, 14, 0), AutoScroll = true };
            _why.MaximumSize = new Size(560, 0);
            _risk.MaximumSize = new Size(560, 0);
            _note.MaximumSize = new Size(560, 0);
            _changes.Width = 560;
            _intro.Text = Strings.Get("Repair_Confirm_Intro");
            _risk.Font = new Font(Font, FontStyle.Bold);
            _understand.Text = Strings.Get("Repair_Risk_Confirm");
            _understand.CheckedChanged += (s, e) => UpdateApply();
            _note.Text = Strings.Get(isCard ? "Repair_Confirm_Card" : "Repair_Confirm_Image");
            _others.Text = Strings.Get("Repair_Confirm_Others");
            _others.Font = new Font(Font, FontStyle.Bold);

            body.Controls.Add(_why);
            body.Controls.Add(_intro);
            body.Controls.Add(_changes);
            body.Controls.Add(_risk);
            body.Controls.Add(_understand);
            body.Controls.Add(_note);
            if (_plans.Count > 1)
            {
                body.Controls.Add(_others);
                for (int i = 0; i < _plans.Count; i++)
                {
                    int index = i;
                    var radio = new RadioButton { AutoSize = true, UseMnemonic = false, Text = RepairTexts.Title(_plans[i].Title), Checked = i == 0, Margin = new Padding(0, 0, 0, 2) };
                    radio.CheckedChanged += (s, e) =>
                    {
                        if (radio.Checked)
                        {
                            Choose(index);
                        }
                    };
                    _choices.Controls.Add(radio);
                }

                body.Controls.Add(_choices);
            }

            _apply.Text = Strings.Get("Repair_Confirm_Apply");
            _cancel.Text = Strings.Get("Edit_Cancel");
            _apply.DialogResult = DialogResult.OK;
            _cancel.DialogResult = DialogResult.Cancel;
            var buttons = Ui.Flow(rightToLeft: true);
            buttons.Dock = DockStyle.Bottom;
            buttons.Padding = new Padding(14, 8, 14, 12);
            buttons.Controls.Add(_cancel);
            buttons.Controls.Add(_apply);
            Controls.Add(body);
            Controls.Add(buttons);
            AcceptButton = _apply;
            CancelButton = _cancel;
            Choose(0);
        }

        /// <summary>The plan the user confirmed (the main one or one of its alternatives).</summary>
        public RepairPlan ChosenPlan => _plans[_index];

        private void Choose(int index)
        {
            _index = index;
            var plan = _plans[index];
            Text = Strings.Format("Repair_Confirm_Title", RepairTexts.Title(plan.Title));
            _why.Text = RepairTexts.Why(plan.Kind);
            _changes.Text = string.Join(Environment.NewLine, plan.Summary.Select(RepairTexts.Change));
            _changes.Select(0, 0);
            bool risky = plan.Risk != RepairRisk.None;
            _risk.Visible = _understand.Visible = risky;
            _risk.Text = RepairTexts.Risk(plan);
            _understand.Checked = false;
            UpdateApply();
        }

        private void UpdateApply()
        {
            var plan = _plans[_index];
            _apply.Enabled = plan.CanApply && (plan.Risk == RepairRisk.None || _understand.Checked);
            // a plan that lowers security is never the default button
            AcceptButton = plan.Risk == RepairRisk.None ? _apply : null;
        }
    }
}

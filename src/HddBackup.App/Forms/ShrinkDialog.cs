using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HddBackup.App.Localization;
using HddBackup.App.Settings;
using HddBackup.Core.Imaging;
using HddBackup.Core.Shrinking;

namespace HddBackup.App.Forms
{
    /// <summary>Standalone "Shrink image" (US2): smallest possible or fit a card size; raw or gzip output.</summary>
    internal sealed class ShrinkDialog : Form
    {
        private readonly string _source;
        private readonly Label _summary = new Label { AutoSize = true, MaximumSize = new Size(560, 0), UseMnemonic = false };
        private readonly Label _blockers = new Label { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Color.DarkRed, Visible = false, UseMnemonic = false };
        private readonly RadioButton _minimum = new RadioButton { AutoSize = true, Checked = true };
        private readonly RadioButton _card = new RadioButton { AutoSize = true };
        private readonly NumericUpDown _size = new NumericUpDown { Minimum = 1, Maximum = 2048 * 1024, Value = 8, Width = 90, DecimalPlaces = 0 };
        private readonly ComboBox _unit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
        private readonly TextBox _output = new TextBox();
        private readonly Button _browse = Ui.Button();
        private readonly Button _start = Ui.Button();
        private ShrinkPlan _plan;

        public ShrinkDialog(string sourcePath, PortableSettings settings)
        {
            _source = sourcePath;
            Text = Strings.Get("Shrink_Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            _summary.Text = Strings.Get("Shrink_Analyzing");
            _minimum.Text = Strings.Get("Shrink_TargetMinimum");
            _card.Text = Strings.Get("Shrink_TargetCard");
            _unit.Items.AddRange(new object[] { Strings.Get("Unit_GiB"), Strings.Get("Unit_MiB") });
            _unit.SelectedIndex = 0;
            _browse.Text = Strings.Get("Common_Browse");
            _start.Text = Strings.Get("Shrink_Start");
            _start.Enabled = false;
            _output.Text = DefaultOutputPath(sourcePath, null, false);
            _output.Width = 420;

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(12), Dock = DockStyle.Fill };
            root.Controls.Add(_summary);
            root.Controls.Add(_blockers);
            var target = Ui.Flow();
            target.Margin = new Padding(0, 10, 0, 0);
            target.Controls.AddRange(new Control[] { _minimum, _card, _size, _unit });
            root.Controls.Add(target);
            root.Controls.Add(new Label { AutoSize = true, Text = Strings.Get("Shrink_Output"), Margin = new Padding(3, 10, 3, 2) });
            root.Controls.Add(Ui.PathRow(_output, _browse));

            var cancel = Ui.Button();
            cancel.Text = Strings.Get("Common_Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            var buttons = Ui.Flow(rightToLeft: true);
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 12, 0, 0);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_start);
            root.Controls.Add(buttons);
            Controls.Add(root);
            CancelButton = cancel;

            _browse.Click += (s, e) => Browse(settings);
            _start.Click += (s, e) => Accept();
            _card.CheckedChanged += (s, e) => _size.Enabled = _unit.Enabled = _card.Checked;
            _size.Enabled = _unit.Enabled = false;
        }

        public OperationRequest Request { get; private set; }

        public static string DefaultOutputPath(string source, long? targetBytes, bool gzip)
        {
            string name = Path.GetFileName(source);
            foreach (var ext in new[] { ".img.gz", ".img", ".gz" })
            {
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - ext.Length);
                    break;
                }
            }

            string suffix = targetBytes.HasValue
                ? "_shrunk_" + (targetBytes.Value / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + "G"
                : "_shrunk";
            return Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, name + suffix + (gzip ? ".img.gz" : ".img"));
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                _plan = await Task.Run(() => ShrinkPlanner.Analyze(_source, CancellationToken.None));
            }
            catch (Exception ex)
            {
                _summary.Text = ex.Message;
                return;
            }

            if (IsDisposed)
            {
                return;
            }

            string parts = string.Join(", ", _plan.Partitions.Select(p => Strings.Format("Partition_Summary", p.Number, p.FileSystem, Formatters.Bytes(p.Length))));
            bool measured = _plan.MinimumImageBytes > 0; // not computed when analysis stopped at a blocker
            _summary.Text = Strings.Format(
                "Shrink_Summary",
                Path.GetFileName(_source),
                Formatters.Bytes(_plan.SourceImageBytes > 0 ? _plan.SourceImageBytes : new FileInfo(_source).Length),
                parts,
                measured ? Formatters.Bytes(_plan.UsedDataBytes) : "—",
                measured ? Formatters.Bytes(_plan.MinimumImageBytes) : "—");
            if (!_plan.IsShrinkable)
            {
                _blockers.Text = Formatters.Blockers(_plan.Blockers);
                _blockers.Visible = true;
                return;
            }

            decimal gib = Math.Ceiling((decimal)_plan.MinimumImageBytes / (1024 * 1024 * 1024));
            _size.Value = Math.Max(_size.Minimum, Math.Min(_size.Maximum, gib));
            _start.Enabled = true;
        }

        private void Browse(PortableSettings settings)
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = Strings.Get("Dialog_ShrinkFilter");
                dialog.FileName = Path.GetFileName(_output.Text);
                dialog.InitialDirectory = Path.GetDirectoryName(_output.Text);
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _output.Text = dialog.FileName;
                    settings.LastImageFolder = Path.GetDirectoryName(dialog.FileName);
                }
            }
        }

        private void Accept()
        {
            long? target = null;
            if (_card.Checked)
            {
                long unit = _unit.SelectedIndex == 0 ? 1024L * 1024 * 1024 : 1024L * 1024;
                target = (long)_size.Value * unit;
                if (target.Value < _plan.MinimumImageBytes)
                {
                    MessageBox.Show(this, Strings.Get("Blocker_NotEnoughSpaceEvenAtMinimum"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            string output = _output.Text.Trim();
            if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(_source), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, Strings.Get("Error_InvalidRequest"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (File.Exists(output) && MessageBox.Show(this, Strings.Format("Backup_ConfirmOverwrite", Path.GetFileName(output)), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }

            Request = new OperationRequest
            {
                Kind = OperationKind.ShrinkImage,
                ImagePath = _source,
                ShrinkOutputPath = output,
                ShrinkTargetBytes = target,
                EjectAfter = false
            };
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}

using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using HddBackup.App.Localization;
using HddBackup.Core.Devices;

namespace HddBackup.App.Forms
{
    /// <summary>
    /// Shows why each disk is or is not listed, so problems with unusual card readers can be
    /// reported. The report body is technical English, like other error details.
    /// </summary>
    internal sealed class DiagnosticsDialog : Form
    {
        private readonly TextBox _report = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9f)
        };

        private readonly Button _copy = Ui.Button();
        private readonly Button _save = Ui.Button();
        private readonly Button _close = Ui.Button();

        public DiagnosticsDialog()
        {
            Text = Strings.Get("Diagnostics_Title");
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(820, 560);

            _copy.Text = Strings.Get("Diagnostics_Copy");
            _save.Text = Strings.Get("Diagnostics_Save");
            _close.Text = Strings.Get("Common_Close");
            _close.DialogResult = DialogResult.Cancel;
            _copy.Enabled = false;
            _save.Enabled = false;
            _report.Text = Strings.Get("Diagnostics_Collecting");

            var buttons = Ui.Flow(rightToLeft: true);
            buttons.Dock = DockStyle.Bottom;
            buttons.Padding = new Padding(6);
            buttons.Controls.AddRange(new Control[] { _close, _save, _copy });
            Controls.Add(_report);
            Controls.Add(buttons);
            CancelButton = _close;

            _copy.Click += (s, e) => Copy();
            _save.Click += (s, e) => Save();
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            string report;
            try
            {
                report = await Task.Run(() => new DeviceEnumerator().BuildDiagnosticReport());
            }
            catch (Exception ex)
            {
                report = ex.ToString();
            }

            if (IsDisposed)
            {
                return;
            }

            _report.Text = report.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            _report.SelectionStart = 0;
            _copy.Enabled = true;
            _save.Enabled = true;
        }

        private void Copy()
        {
            try
            {
                Clipboard.SetText(_report.Text);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Clipboard busy; the user can still select the text manually.
            }
        }

        private void Save()
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = Strings.Get("Dialog_TextFilter");
                dialog.FileName = "HddBackup-diagnostics.txt";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        File.WriteAllText(dialog.FileName, _report.Text);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        MessageBox.Show(this, ex.Message, Strings.Get("App_Name"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }
    }
}

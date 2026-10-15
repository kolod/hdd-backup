using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using HddBackup.App.Localization;

namespace HddBackup.App.Forms
{
    internal sealed class AboutDialog : Form
    {
        public AboutDialog()
        {
            Text = Strings.Get("About_Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(16), Dock = DockStyle.Fill };
            root.Controls.Add(new Label
            {
                AutoSize = true,
                Text = Strings.Get("App_Name") + " " + version,
                Font = new Font(Font.FontFamily, Font.Size * 1.4f, FontStyle.Bold)
            });
            root.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(420, 0),
                Margin = new Padding(3, 8, 3, 12),
                Text = Strings.Get("About_Text")
            });

            var ok = Ui.Button();
            ok.Text = Strings.Get("Common_Ok");
            ok.DialogResult = DialogResult.OK;
            var buttons = Ui.Flow(rightToLeft: true);
            buttons.Dock = DockStyle.Fill;
            buttons.Controls.Add(ok);
            root.Controls.Add(buttons);
            Controls.Add(root);
            AcceptButton = ok;
            CancelButton = ok;
        }
    }
}

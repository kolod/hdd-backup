using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using HddBackup.App.Localization;

namespace HddBackup.App.Forms
{
    internal sealed class AboutDialog : Form
    {
        private const string SourceUrl = "https://github.com/kolod/hdd-backup";

        public AboutDialog()
        {
            Text = Strings.Get("About_Title");
            Icon = AppIcon.Icon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            var outer = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(16), Dock = DockStyle.Fill };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            // The program icon at 64 px scaled for the display (feature 007).
            int iconSize = 64 * DeviceDpi / 96;
            var picture = new PictureBox
            {
                Image = AppIcon.Image(iconSize <= 64 ? 64 : iconSize <= 128 ? 128 : 256),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(iconSize, iconSize),
                Margin = new Padding(0, 4, 16, 0)
            };
            outer.Controls.Add(picture, 0, 0);
            Disposed += (s, e) => picture.Image.Dispose();

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Margin = new Padding(0), Dock = DockStyle.Fill };
            outer.Controls.Add(root, 1, 0);
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

            // GPL-3.0 section 5(d): an interactive program shows the copyright, the absence of
            // warranty and where the license can be read.
            var copyright = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
            root.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(420, 0),
                Margin = new Padding(3, 0, 3, 12),
                Text = copyright + "\n" + Strings.Format("About_License", SourceUrl)
            });

            root.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(420, 0),
                Margin = new Padding(3, 0, 3, 12),
                ForeColor = SystemColors.GrayText,
                Text = Strings.Get("About_IconCredit")
            });

            var ok = Ui.Button();
            ok.Text = Strings.Get("Common_Ok");
            ok.DialogResult = DialogResult.OK;
            var buttons = Ui.Flow(rightToLeft: true);
            buttons.Dock = DockStyle.Fill;
            buttons.Controls.Add(ok);
            outer.Controls.Add(buttons, 0, 1);
            outer.SetColumnSpan(buttons, 2);
            Controls.Add(outer);
            AcceptButton = ok;
            CancelButton = ok;
        }
    }
}

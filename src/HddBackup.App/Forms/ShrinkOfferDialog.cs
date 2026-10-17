using System.Drawing;
using System.Windows.Forms;
using HddBackup.App.Localization;
using HddBackup.Core.Shrinking;

namespace HddBackup.App.Forms
{
    /// <summary>"The image is larger than the card" → offer to shrink a copy and write it (FR-002, FR-003a).</summary>
    internal sealed class ShrinkOfferDialog : Form
    {
        public ShrinkOfferDialog(ShrinkPlan plan, long imageBytes, long deviceBytes, string outputPath)
        {
            Text = Strings.Get("Shrink_OfferTitle");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(12), Dock = DockStyle.Fill };
            root.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(560, 0),
                UseMnemonic = false,
                Text = Strings.Format(
                    "Shrink_OfferText",
                    Formatters.Bytes(imageBytes),
                    Formatters.Bytes(deviceBytes),
                    plan.UsedDataBytes > 0 ? Formatters.Bytes(plan.UsedDataBytes) : "—", // measured during the shrink for compressed images
                    Formatters.Bytes(deviceBytes),
                    outputPath)
            });

            if (plan.IsRepairable)
            {
                root.Controls.Add(new Label
                {
                    AutoSize = true,
                    MaximumSize = new Size(560, 0),
                    Margin = new Padding(3, 8, 3, 3),
                    Text = Strings.Get("Shrink_OfferRepair")
                });
            }

            var go = Ui.Button();
            go.Text = Strings.Get("Shrink_AndWrite");
            go.DialogResult = DialogResult.OK;
            var cancel = Ui.Button();
            cancel.Text = Strings.Get("Common_Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            var buttons = Ui.Flow(rightToLeft: true);
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 12, 0, 0);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(go);
            root.Controls.Add(buttons);
            Controls.Add(root);
            AcceptButton = go;
            CancelButton = cancel;
        }
    }
}

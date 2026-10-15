using System.Drawing;
using System.Windows.Forms;

namespace HddBackup.App.Forms
{
    /// <summary>Small helpers for auto-sizing layouts that tolerate long translations.</summary>
    internal static class Ui
    {
        public static TableLayoutPanel Column()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                AutoSize = false,
                Padding = new Padding(8)
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return panel;
        }

        /// <summary>Adds a row; <paramref name="fill"/> rows share the remaining height.</summary>
        public static void AddRow(TableLayoutPanel panel, Control control, bool fill = false)
        {
            panel.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            control.Dock = fill ? DockStyle.Fill : (control.Dock == DockStyle.None ? DockStyle.Top : control.Dock);
            panel.Controls.Add(control, 0, panel.RowCount++);
        }

        public static Label Caption()
        {
            return new Label { AutoSize = true, Margin = new Padding(3, 8, 3, 2) };
        }

        public static FlowLayoutPanel Flow(bool rightToLeft = false)
        {
            return new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                FlowDirection = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                Dock = DockStyle.Top,
                Margin = new Padding(0)
            };
        }

        public static Button Button()
        {
            return new Button
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(100, 28),
                Padding = new Padding(8, 0, 8, 0)
            };
        }

        /// <summary>A text box with a button on its right, e.g. a path plus "Browse…".</summary>
        public static TableLayoutPanel PathRow(TextBox box, Button button)
        {
            var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(3, 5, 3, 3);
            row.Controls.Add(box, 0, 0);
            row.Controls.Add(button, 1, 0);
            return row;
        }
    }
}

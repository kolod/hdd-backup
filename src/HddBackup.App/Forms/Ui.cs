using System;
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

        /// <summary>
        /// Tab layout: a left area that takes the free width, a right options column sized by
        /// <see cref="FitRightColumn"/>, and a full-width bottom area (path and action buttons).
        /// </summary>
        public static TableLayoutPanel TwoColumn(out TableLayoutPanel left, out TableLayoutPanel right, out TableLayoutPanel bottom)
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(8) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            left = Stack(new Padding(0, 0, 8, 0));
            right = Stack(new Padding(8, 0, 0, 0));
            bottom = Stack(new Padding(0, 4, 0, 0));
            bottom.AutoSize = true;
            root.Controls.Add(left, 0, 0);
            root.Controls.Add(right, 1, 0);
            root.Controls.Add(bottom, 0, 1);
            root.SetColumnSpan(bottom, 2);
            return root;
        }

        /// <summary>Bold heading above a group of options in the right column.</summary>
        public static Label GroupCaption()
        {
            return new Label { AutoSize = true, Margin = new Padding(3, 10, 3, 2) };
        }

        /// <summary>Makes group captions bold in the current UI font; call after every font change.</summary>
        public static void Embolden(Font baseFont, params Label[] labels)
        {
            foreach (var label in labels)
            {
                label.Font = new Font(baseFont, FontStyle.Bold);
            }
        }

        /// <summary>
        /// Sizes the right column of a <see cref="TwoColumn"/> layout to its widest option in the
        /// current language, clamped to 220–300 px at 96 DPI, and lets longer texts wrap within it.
        /// Call after every text or font change.
        /// </summary>
        public static void FitRightColumn(TableLayoutPanel root, TableLayoutPanel right)
        {
            float scale = root.DeviceDpi / 96f;
            int min = (int)(220 * scale);
            int max = (int)(300 * scale);
            var wrapped = new System.Collections.Generic.List<Control>();
            Collect(right, wrapped);

            int widest = 0;
            foreach (var control in wrapped)
            {
                control.MaximumSize = Size.Empty;
                widest = Math.Max(widest, control.GetPreferredSize(Size.Empty).Width + control.Margin.Horizontal + OffsetIn(right, control));
            }

            int width = Math.Max(min, Math.Min(max, widest)) + right.Padding.Horizontal;
            root.ColumnStyles[1] = new ColumnStyle(SizeType.Absolute, width);
            foreach (var control in wrapped)
            {
                control.MaximumSize = new Size(Math.Max(1, width - right.Padding.Horizontal - control.Margin.Horizontal - OffsetIn(right, control)), 0);
            }
        }

        private static TableLayoutPanel Stack(Padding padding)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = padding, Margin = new Padding(0) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return panel;
        }

        // Texts that may wrap: labels and check/radio buttons, also inside nested flow panels.
        private static void Collect(Control parent, System.Collections.Generic.List<Control> result)
        {
            foreach (Control child in parent.Controls)
            {
                if ((child is Label || child is CheckBox || child is RadioButton) && child.AutoSize)
                {
                    result.Add(child);
                }
                else if (child is Panel)
                {
                    Collect(child, result);
                }
            }
        }

        // Horizontal space taken by nested panels between the column and the control.
        private static int OffsetIn(Control column, Control control)
        {
            int offset = 0;
            for (var p = control.Parent; p != null && p != column; p = p.Parent)
            {
                offset += p.Padding.Horizontal + p.Margin.Horizontal;
            }

            return offset;
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

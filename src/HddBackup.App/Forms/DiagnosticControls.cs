using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace HddBackup.App.Forms
{
    /// <summary>One line of a <see cref="NoteListPanel"/>: a glyph, a sentence that wraps, and grey detail lines under it.</summary>
    internal sealed class NoteItem
    {
        public NoteItem(string glyph, Color glyphColor, string text, string detail = null)
        {
            Glyph = glyph;
            GlyphColor = glyphColor;
            Text = text;
            Detail = detail ?? string.Empty;
        }

        public string Glyph { get; }

        public Color GlyphColor { get; }

        public string Text { get; }

        public string Detail { get; }
    }

    /// <summary>
    /// A scrolling list of wrapping sentences with a status glyph (✔ ✖ ⚠ ℹ) and optional grey evidence
    /// text, used by the pages of the SSH check. Colour is never the only signal: the glyph carries it too.
    /// </summary>
    internal sealed class NoteListPanel : Panel
    {
        private readonly List<Label> _texts = new List<Label>();
        private readonly List<Label> _details = new List<Label>();
        private readonly FlowLayoutPanel _flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = new Padding(0)
        };

        public NoteListPanel()
        {
            AutoScroll = true;
            Dock = DockStyle.Fill;
            Controls.Add(_flow);
        }

        public void SetItems(IEnumerable<NoteItem> items)
        {
            SuspendLayout();
            _flow.Controls.Clear();
            _texts.Clear();
            _details.Clear();
            foreach (var item in items)
            {
                var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                var glyph = new Label
                {
                    Text = item.Glyph,
                    ForeColor = item.GlyphColor,
                    AutoSize = true,
                    UseMnemonic = false,
                    Margin = new Padding(0, 0, 8, 0),
                    MinimumSize = new Size((int)(Font.Size * 2), 0)
                };
                var text = new Label { Text = item.Text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0) };
                row.Controls.Add(glyph, 0, 0);
                row.Controls.Add(text, 1, 0);
                _texts.Add(text);
                if (item.Detail.Length > 0)
                {
                    var detail = new Label
                    {
                        Text = item.Detail,
                        AutoSize = true,
                        UseMnemonic = false,
                        ForeColor = SystemColors.GrayText,
                        Margin = new Padding(0, 1, 0, 0)
                    };
                    row.Controls.Add(detail, 1, 1);
                    _details.Add(detail);
                }

                _flow.Controls.Add(row);
            }

            ApplyWidths();
            ResumeLayout(true);
        }

        protected override void OnResize(System.EventArgs eventargs)
        {
            base.OnResize(eventargs);
            ApplyWidths();
        }

        /// <summary>Limits the wrapping labels to the visible width so long sentences wrap instead of scrolling sideways.</summary>
        private void ApplyWidths()
        {
            int width = ClientSize.Width - SystemInformation.VerticalScrollBarWidth - (int)(Font.Size * 3) - 16;
            if (width < 120)
            {
                width = 120;
            }

            foreach (var l in _texts)
            {
                l.MaximumSize = new Size(width, 0);
            }

            foreach (var l in _details)
            {
                l.MaximumSize = new Size(width, 0);
            }
        }
    }
}

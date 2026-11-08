using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace HddBackup.App.Forms
{
    /// <summary>A button at the right end of a <see cref="NoteItem"/> (feature 011: "Fix…", "Add a key…", "Undo").</summary>
    internal sealed class NoteAction
    {
        public NoteAction(string text, Action click, bool enabled = true, string tip = null)
        {
            Text = text;
            Click = click;
            Enabled = enabled;
            Tip = tip;
        }

        public string Text { get; }

        public Action Click { get; }

        public bool Enabled { get; }

        public string Tip { get; }
    }

    /// <summary>One line of a <see cref="NoteListPanel"/>: a glyph, a sentence that wraps, buttons, and grey detail lines under it.</summary>
    internal sealed class NoteItem
    {
        public NoteItem(string glyph, Color glyphColor, string text, string detail = null, IReadOnlyList<NoteAction> actions = null)
        {
            Glyph = glyph;
            GlyphColor = glyphColor;
            Text = text;
            Detail = detail ?? string.Empty;
            Actions = actions ?? Array.Empty<NoteAction>();
        }

        public IReadOnlyList<NoteAction> Actions { get; }

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
        private bool _hasActions;
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
            _hasActions = false;
            foreach (var item in items)
            {
                _hasActions |= item.Actions.Count > 0;
                var row = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
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
                if (item.Actions.Count > 0)
                {
                    var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(12, 0, 0, 0) };
                    var tips = new ToolTip();
                    foreach (var action in item.Actions)
                    {
                        var button = new Button
                        {
                            Text = action.Text,
                            AutoSize = true,
                            AutoSizeMode = AutoSizeMode.GrowAndShrink,
                            MinimumSize = new Size(90, 26),
                            Enabled = action.Enabled,
                            Margin = new Padding(0, 0, 6, 0)
                        };
                        var click = action.Click;
                        button.Click += (s, e) => click();
                        if (!string.IsNullOrEmpty(action.Tip))
                        {
                            tips.SetToolTip(button, action.Tip);
                        }

                        buttons.Controls.Add(button);
                    }

                    buttons.Disposed += (s, e) => tips.Dispose();
                    row.Controls.Add(buttons, 2, 0);
                }

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
            int width = ClientSize.Width - SystemInformation.VerticalScrollBarWidth - (int)(Font.Size * 3) - 16 - (_hasActions ? 230 : 0);
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

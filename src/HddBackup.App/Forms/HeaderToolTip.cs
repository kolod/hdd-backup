using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HddBackup.App.Forms
{
    /// <summary>
    /// Shows a tooltip over one column header of a details-view <see cref="ListView"/>; WinForms has
    /// no header tooltips. Listens to the header window (LVM_GETHEADER), which exists on Windows 7.
    /// </summary>
    internal sealed class HeaderToolTip : NativeWindow, IDisposable
    {
        private const int LVM_GETHEADER = 0x101F;
        private const int HDM_GETITEMRECT = 0x1207;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_MOUSELEAVE = 0x02A3;
        private const int TME_LEAVE = 0x2;

        private readonly ListView _list;
        private readonly int _column;
        private readonly ToolTip _tip = new ToolTip();
        private bool _shown;
        private bool _tracking;

        public HeaderToolTip(ListView list, int column)
        {
            _list = list;
            _column = column;
            _list.HandleCreated += (s, e) => Attach();
            _list.HandleDestroyed += (s, e) => ReleaseHandle();
            if (_list.IsHandleCreated)
            {
                Attach();
            }
        }

        public string Text { get; set; } = string.Empty;

        public void Dispose()
        {
            ReleaseHandle();
            _tip.Dispose();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEMOVE)
            {
                if (!_tracking)
                {
                    var track = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf(typeof(TRACKMOUSEEVENT)), dwFlags = TME_LEAVE, hwndTrack = Handle };
                    _tracking = TrackMouseEvent(ref track);
                }

                int lParam = unchecked((int)(long)m.LParam);
                var point = new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                var rect = new RECT();
                bool inside = SendMessage(Handle, HDM_GETITEMRECT, (IntPtr)_column, ref rect) != IntPtr.Zero
                    && point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
                if (inside && !_shown && Text.Length > 0)
                {
                    _tip.Show(Text, _list, rect.Left, rect.Bottom + 2, 5000);
                    _shown = true;
                }
                else if (!inside && _shown)
                {
                    Hide();
                }
            }
            else if (m.Msg == WM_MOUSELEAVE)
            {
                _tracking = false;
                Hide();
            }

            base.WndProc(ref m);
        }

        private void Attach()
        {
            IntPtr header = SendMessage(_list.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            if (header != IntPtr.Zero && header != Handle)
            {
                ReleaseHandle();
                AssignHandle(header);
            }
        }

        private void Hide()
        {
            if (_shown)
            {
                _tip.Hide(_list);
                _shown = false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TRACKMOUSEEVENT
        {
            public int cbSize;
            public int dwFlags;
            public IntPtr hwndTrack;
            public int dwHoverTime;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);

        [DllImport("user32.dll")]
        private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);
    }
}

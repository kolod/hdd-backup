using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HddBackup.App.Platform
{
    /// <summary>
    /// Raises <see cref="DevicesChanged"/> (debounced by 500 ms) when disks or volumes appear or
    /// disappear. Uses a hidden top-level window, which receives volume broadcasts, and registers
    /// for disk interface notifications so media without volumes are noticed too.
    /// </summary>
    internal sealed class DeviceChangeWatcher : NativeWindow, IDisposable
    {
        private const int WmDeviceChange = 0x0219;
        private const int DbtDeviceArrival = 0x8000;
        private const int DbtDeviceRemoveComplete = 0x8004;
        private const int DbtDevNodesChanged = 0x0007;
        private const int DbtDevTypDeviceInterface = 5;
        private const int DeviceNotifyWindowHandle = 0;

        private static readonly Guid DiskInterface = new Guid("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");

        private readonly Timer _debounce = new Timer { Interval = 500 };
        private IntPtr _notification;

        public DeviceChangeWatcher()
        {
            CreateHandle(new CreateParams());
            _debounce.Tick += (s, e) =>
            {
                _debounce.Stop();
                DevicesChanged?.Invoke(this, EventArgs.Empty);
            };
            Register();
        }

        public event EventHandler DevicesChanged;

        public void Dispose()
        {
            if (_notification != IntPtr.Zero)
            {
                UnregisterDeviceNotification(_notification);
                _notification = IntPtr.Zero;
            }

            _debounce.Dispose();
            DestroyHandle();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmDeviceChange)
            {
                int evt = m.WParam.ToInt32();
                if (evt == DbtDeviceArrival || evt == DbtDeviceRemoveComplete || evt == DbtDevNodesChanged)
                {
                    _debounce.Stop();
                    _debounce.Start();
                }
            }

            base.WndProc(ref m);
        }

        private void Register()
        {
            var filter = new DevBroadcastDeviceInterface
            {
                Size = Marshal.SizeOf(typeof(DevBroadcastDeviceInterface)),
                DeviceType = DbtDevTypDeviceInterface,
                ClassGuid = DiskInterface
            };
            IntPtr buffer = Marshal.AllocHGlobal(filter.Size);
            try
            {
                Marshal.StructureToPtr(filter, buffer, false);
                _notification = RegisterDeviceNotification(Handle, buffer, DeviceNotifyWindowHandle);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, IntPtr filter, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterDeviceNotification(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DevBroadcastDeviceInterface
        {
            public int Size;
            public int DeviceType;
            public int Reserved;
            public Guid ClassGuid;
            public short Name;
        }
    }
}

using System;
using System.Runtime.InteropServices;

namespace HddBackup.App.Platform
{
    internal enum TaskbarState
    {
        None = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8
    }

    /// <summary>Mirrors operation progress on the taskbar button (ITaskbarList3, Windows 7+).</summary>
    internal sealed class TaskbarProgress
    {
        private readonly IntPtr _window;
        private readonly ITaskbarList3 _taskbar;

        public TaskbarProgress(IntPtr window)
        {
            _window = window;
            try
            {
                _taskbar = (ITaskbarList3)new TaskbarInstance();
                _taskbar.HrInit();
            }
            catch (Exception ex) when (ex is COMException || ex is InvalidCastException)
            {
                _taskbar = null; // No taskbar progress (e.g. Explorer not running).
            }
        }

        public void SetState(TaskbarState state)
        {
            Try(() => _taskbar.SetProgressState(_window, state));
        }

        public void SetValue(double percent)
        {
            Try(() => _taskbar.SetProgressValue(_window, (ulong)Math.Max(0, Math.Min(1000, percent * 10)), 1000));
        }

        private void Try(Action action)
        {
            if (_taskbar == null)
            {
                return;
            }

            try
            {
                action();
            }
            catch (COMException)
            {
                // Cosmetic only.
            }
        }

        [ComImport]
        [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            void HrInit();

            void AddTab(IntPtr hwnd);

            void DeleteTab(IntPtr hwnd);

            void ActivateTab(IntPtr hwnd);

            void SetActiveAlt(IntPtr hwnd);

            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

            void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);

            void SetProgressState(IntPtr hwnd, TaskbarState state);
        }

        [ComImport]
        [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        [ClassInterface(ClassInterfaceType.None)]
        private class TaskbarInstance
        {
        }
    }
}

using System;
using System.Drawing;

namespace HddBackup.App.Forms
{
    /// <summary>
    /// The program icon (feature 007) with all its sizes, so title bars and the taskbar get a sharp
    /// image at any DPI (the icon extracted from the executable has only 32 px).
    /// </summary>
    internal static class AppIcon
    {
        private static readonly Lazy<Icon> Loaded = new Lazy<Icon>(() =>
        {
            using (var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("HddBackup.App.app.ico"))
            {
                return new Icon(stream);
            }
        });

        public static Icon Icon => Loaded.Value;

        /// <summary>The icon as a bitmap of the given size, from the nearest stored image.</summary>
        public static Bitmap Image(int size)
        {
            using (var sized = new Icon(Icon, size, size))
            {
                return sized.ToBitmap();
            }
        }
    }
}

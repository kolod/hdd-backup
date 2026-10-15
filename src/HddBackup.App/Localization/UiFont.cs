using System;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;

namespace HddBackup.App.Localization
{
    /// <summary>Chooses a UI font that renders the current language well on Windows 7–11.</summary>
    internal static class UiFont
    {
        private const float Size = 9f;
        private static readonly string[] JapaneseFonts = { "Meiryo UI", "MS UI Gothic", "Yu Gothic UI" };
        private static readonly string[] DefaultFonts = { "Segoe UI", "Tahoma" };

        public static Font For(string languageCode)
        {
            var preferred = string.Equals(languageCode, "ja", StringComparison.OrdinalIgnoreCase) ? JapaneseFonts : DefaultFonts;
            using (var installed = new InstalledFontCollection())
            {
                var names = installed.Families.Select(f => f.Name).ToList();
                foreach (var name in preferred)
                {
                    if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        return new Font(name, Size);
                    }
                }
            }

            return SystemFonts.MessageBoxFont;
        }
    }
}

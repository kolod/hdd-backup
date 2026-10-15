using System;
using System.Globalization;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Partitioning;

namespace HddBackup.App.Localization
{
    /// <summary>Culture-aware formatting of sizes, speeds, durations and enum values.</summary>
    internal static class Formatters
    {
        private static readonly string[] UnitKeys = { "Unit_B", "Unit_KiB", "Unit_MiB", "Unit_GiB", "Unit_TiB" };

        public static string Bytes(long bytes)
        {
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < UnitKeys.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            string number = unit == 0
                ? bytes.ToString("N0", LanguageService.Culture)
                : value.ToString(value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00", LanguageService.Culture);
            return number + " " + Strings.Get(UnitKeys[unit]);
        }

        public static string Speed(double bytesPerSecond)
        {
            return Strings.Format("Speed_PerSecond", Bytes((long)Math.Max(0, bytesPerSecond)));
        }

        public static string Duration(TimeSpan span)
        {
            if (span < TimeSpan.Zero)
            {
                span = TimeSpan.Zero;
            }

            return span.TotalHours >= 24
                ? string.Format(CultureInfo.InvariantCulture, "{0}.{1:hh\\:mm\\:ss}", (int)span.TotalDays, span)
                : span.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
        }

        public static string Offset(long offset)
        {
            return offset.ToString("N0", LanguageService.Culture);
        }

        public static string Bus(BusType bus)
        {
            switch (bus)
            {
                case BusType.Sd:
                    return Strings.Get("Bus_Sd");
                case BusType.Mmc:
                    return Strings.Get("Bus_Mmc");
                case BusType.Other:
                    return Strings.Get("Bus_Other");
                default:
                    return Strings.Get("Bus_Usb");
            }
        }

        public static string Table(TableStyle style)
        {
            return Strings.Get("Table_" + style);
        }

        public static string Error(ErrorCode code)
        {
            return Strings.Get("Error_" + code);
        }

        /// <summary>Localized bullet list of shrink blockers (technical detail appended verbatim).</summary>
        public static string Blockers(System.Collections.Generic.IEnumerable<HddBackup.Core.Shrinking.ShrinkBlockerInfo> blockers)
        {
            var lines = new System.Collections.Generic.List<string>();
            foreach (var b in blockers)
            {
                lines.Add(BlockerLine(b.Blocker.ToString(), b.Detail));
            }

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>Same as <see cref="Blockers(System.Collections.Generic.IEnumerable{HddBackup.Core.Shrinking.ShrinkBlockerInfo})"/> for an engine error detail ("Name: detail; Name").</summary>
        public static string BlockersFromDetail(string detail)
        {
            var lines = new System.Collections.Generic.List<string>();
            foreach (var part in (detail ?? string.Empty).Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = part.IndexOf(": ", StringComparison.Ordinal);
                string name = colon > 0 ? part.Substring(0, colon) : part;
                string extra = colon > 0 ? part.Substring(colon + 2) : string.Empty;
                lines.Add(Enum.IsDefined(typeof(HddBackup.Core.Shrinking.ShrinkBlocker), name) ? BlockerLine(name, extra) : "• " + part);
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string BlockerLine(string name, string detail)
        {
            string text = Strings.Get("Blocker_" + name);
            return "• " + (string.IsNullOrEmpty(detail) ? text : text + " (" + detail + ")");
        }
    }
}

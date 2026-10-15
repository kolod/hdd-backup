using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Threading;

namespace HddBackup.App.Localization
{
    internal sealed class LanguageInfo
    {
        public LanguageInfo(string code, string nativeName)
        {
            Code = code;
            NativeName = nativeName;
        }

        public string Code { get; }

        public string NativeName { get; }
    }

    /// <summary>
    /// Loads string tables embedded in the executable (HddBackup.Strings.&lt;code&gt;.resources)
    /// and tracks the current UI language. English is the fallback for missing entries.
    /// </summary>
    internal static class LanguageService
    {
        public const string DefaultCode = "en";
        private const string ResourcePrefix = "HddBackup.Strings.";

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Dictionary<string, string>> Tables =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<LanguageInfo> Supported { get; } = new[]
        {
            new LanguageInfo("en", "English"),
            new LanguageInfo("uk", "Українська"),
            new LanguageInfo("de", "Deutsch"),
            new LanguageInfo("fr", "Français"),
            new LanguageInfo("es", "Español"),
            new LanguageInfo("ja", "日本語")
        };

        public static event EventHandler LanguageChanged;

        public static string Current { get; private set; } = DefaultCode;

        public static CultureInfo Culture => CultureInfo.GetCultureInfo(Current);

        public static bool IsSupported(string code)
        {
            return Supported.Any(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The OS UI language if supported, otherwise English.</summary>
        public static string DetectOsLanguage()
        {
            foreach (var culture in new[] { CultureInfo.CurrentUICulture, CultureInfo.InstalledUICulture })
            {
                var code = culture.TwoLetterISOLanguageName;
                if (IsSupported(code))
                {
                    return code.ToLowerInvariant();
                }
            }

            return DefaultCode;
        }

        public static void SetLanguage(string code)
        {
            code = IsSupported(code) ? code.ToLowerInvariant() : DefaultCode;
            bool changed = !string.Equals(code, Current, StringComparison.Ordinal);
            Current = code;
            Thread.CurrentThread.CurrentUICulture = Culture;
            if (changed)
            {
                LanguageChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        public static string Lookup(string key)
        {
            if (TryLookup(Current, key, out var value) || TryLookup(DefaultCode, key, out value))
            {
                return value;
            }

            // Visible marker instead of a crash: the resource parity test keeps this from shipping.
            return "[" + key + "]";
        }

        private static bool TryLookup(string code, string key, out string value)
        {
            return GetTable(code).TryGetValue(key, out value) && !string.IsNullOrEmpty(value);
        }

        private static Dictionary<string, string> GetTable(string code)
        {
            lock (Sync)
            {
                if (Tables.TryGetValue(code, out var table))
                {
                    return table;
                }

                table = new Dictionary<string, string>(StringComparer.Ordinal);
                var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + code + ".resources");
                if (stream != null)
                {
                    using (stream)
                    using (var reader = new ResourceReader(stream))
                    {
                        foreach (DictionaryEntry entry in reader)
                        {
                            table[(string)entry.Key] = entry.Value as string;
                        }
                    }
                }

                Tables[code] = table;
                return table;
            }
        }
    }

    /// <summary>Short accessors for localized strings.</summary>
    internal static class Strings
    {
        public static string Get(string key) => LanguageService.Lookup(key);

        public static string Format(string key, params object[] args)
        {
            return string.Format(LanguageService.Culture, LanguageService.Lookup(key), args);
        }
    }

    /// <summary>Implemented by forms and controls that render localized text.</summary>
    internal interface ILocalizable
    {
        /// <summary>Re-applies all texts; called at start and whenever the language changes.</summary>
        void ApplyStrings();
    }
}

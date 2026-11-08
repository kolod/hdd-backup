using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HddBackup.Core.Repair
{
    /// <summary>
    /// Line-preserving edits of a text file such as <c>sshd_config</c> or <c>/etc/passwd</c> (feature 011, research R-7):
    /// every line that is not named is kept byte for byte, with its own line ending; a replaced line stays as a comment
    /// with a note; the file's final line break is kept unless a line is added after it.
    /// </summary>
    internal sealed class LineText
    {
        private readonly List<string> _text = new List<string>();
        private readonly List<string> _eol = new List<string>();

        public LineText(string text)
        {
            int i = 0;
            while (i < text.Length)
            {
                int nl = text.IndexOf('\n', i);
                if (nl < 0)
                {
                    _text.Add(text.Substring(i));
                    _eol.Add(string.Empty);
                    break;
                }

                bool cr = nl > i && text[nl - 1] == '\r';
                _text.Add(text.Substring(i, (cr ? nl - 1 : nl) - i));
                _eol.Add(cr ? "\r\n" : "\n");
                i = nl + 1;
            }

            Dominant = text.Contains("\r\n") ? "\r\n" : "\n";
        }

        public int Count => _text.Count;

        /// <summary>The line ending most lines use; used for lines that are added.</summary>
        public string Dominant { get; }

        /// <summary>Line text without its ending; <paramref name="line"/> is 1-based.</summary>
        public string Get(int line) => _text[line - 1];

        public bool Has(int line) => line >= 1 && line <= _text.Count;

        /// <summary>Replaces one line's text, keeping its ending.</summary>
        public void Set(int line, string text) => _text[line - 1] = text;

        /// <summary>Inserts a line before the 1-based <paramref name="line"/> (Count + 1 appends); gives the previous last line an ending when needed.</summary>
        public void Insert(int line, string text)
        {
            if (line == _text.Count + 1 && _text.Count > 0 && _eol[_text.Count - 1].Length == 0)
            {
                _eol[_text.Count - 1] = Dominant;
            }

            _text.Insert(line - 1, text);
            _eol.Insert(line - 1, Dominant);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _text.Count; i++)
            {
                sb.Append(_text[i]).Append(_eol[i]);
            }

            return sb.ToString();
        }
    }

    internal static class SshdEdit
    {
        /// <summary>The note that marks a line this program replaced: <c>hdd-backup 2026-11-08</c>.</summary>
        public static string Note(DateTime date) => "hdd-backup " + date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Keeps line <paramref name="line"/> as a comment (<c># note: old text</c>) and, when <paramref name="replacement"/> is
        /// not null, writes it right after, with the old line's indentation. Returns null when the line is not there.
        /// </summary>
        public static string ReplaceLine(string text, int line, string replacement, string note)
        {
            var lines = new LineText(text);
            if (!lines.Has(line))
            {
                return null;
            }

            string old = lines.Get(line);
            string indent = old.Substring(0, old.Length - old.TrimStart().Length);
            lines.Set(line, "# " + note + ": " + old.Trim());
            if (replacement != null)
            {
                lines.Insert(line + 1, indent + replacement);
            }

            return lines.ToString();
        }

        /// <summary>Adds a new line right after line <paramref name="line"/>; the existing lines are not touched.</summary>
        public static string InsertAfter(string text, int line, string added)
        {
            var lines = new LineText(text);
            if (!lines.Has(line))
            {
                return null;
            }

            lines.Insert(line + 1, added);
            return lines.ToString();
        }

        /// <summary>
        /// Adds a setting where the server reads it as a global one: before the first <c>Match</c> line, or at the end when
        /// there is none, preceded by a comment with the note.
        /// </summary>
        public static string AddSetting(string text, string settingLine, string note)
        {
            var lines = new LineText(text);
            int at = lines.Count + 1;
            for (int i = 1; i <= lines.Count; i++)
            {
                if (IsMatchLine(lines.Get(i)))
                {
                    at = i;
                    break;
                }
            }

            lines.Insert(at, "# " + note);
            lines.Insert(at + 1, settingLine);
            return lines.ToString();
        }

        public static bool IsMatchLine(string line)
        {
            string t = line.TrimStart();
            return t.StartsWith("Match", StringComparison.OrdinalIgnoreCase) && (t.Length == 5 || char.IsWhiteSpace(t[5]));
        }

        /// <summary>The keyword (as written) and the words after it; null when the line is blank, a comment, or uses the <c>key=value</c> form.</summary>
        public static string[] Words(string line)
        {
            string t = line.Trim();
            if (t.Length == 0 || t[0] == '#' || t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0].Contains("="))
            {
                return null;
            }

            return t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>Removes every word equal to <paramref name="token"/> from the list line; the line becomes a comment when no word is left.</summary>
        public static string RemoveToken(string text, int line, string token, string note)
        {
            var lines = new LineText(text);
            if (!lines.Has(line))
            {
                return null;
            }

            var words = Words(lines.Get(line));
            if (words == null)
            {
                return null;
            }

            var rest = words.Skip(1).Where(w => !string.Equals(w, token, StringComparison.Ordinal)).ToList();
            if (rest.Count == words.Length - 1)
            {
                return null;
            }

            return ReplaceLine(text, line, rest.Count == 0 ? null : words[0] + " " + string.Join(" ", rest), note);
        }
    }

    internal static class PasswdEdit
    {
        /// <summary>Sets the login shell (the seventh field) of one account; every other line and field stays. Null when the account is not there.</summary>
        public static string ChangeShell(string text, string account, string shell)
        {
            var lines = new LineText(text);
            for (int i = 1; i <= lines.Count; i++)
            {
                string[] fields = lines.Get(i).Split(':');
                if (fields.Length >= 7 && fields[0] == account)
                {
                    fields[6] = shell;
                    lines.Set(i, string.Join(":", fields));
                    return lines.ToString();
                }
            }

            return null;
        }
    }
}

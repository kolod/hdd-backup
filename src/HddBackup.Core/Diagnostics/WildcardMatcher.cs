using System;

namespace HddBackup.Core.Diagnostics
{
    /// <summary>
    /// Shell-style matching with <c>*</c>, <c>?</c> and bracket sets (<c>[abc]</c>, <c>[a-z]</c>,
    /// <c>[!x]</c>), case-sensitive. Used for <c>Include</c> patterns, drop-in directories and
    /// the user patterns of <c>AllowUsers</c>. <c>*</c> and <c>?</c> never match a slash.
    /// </summary>
    internal static class WildcardMatcher
    {
        public static bool IsMatch(string pattern, string text)
        {
            if (pattern == null)
            {
                throw new ArgumentNullException(nameof(pattern));
            }

            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            return Match(pattern, 0, text, 0);
        }

        private static bool Match(string p, int pi, string t, int ti)
        {
            while (pi < p.Length)
            {
                char c = p[pi];
                if (c == '*')
                {
                    while (pi < p.Length && p[pi] == '*')
                    {
                        pi++;
                    }

                    if (pi == p.Length)
                    {
                        return t.IndexOf('/', ti) < 0;
                    }

                    for (int k = ti; k <= t.Length; k++)
                    {
                        if (Match(p, pi, t, k))
                        {
                            return true;
                        }

                        if (k < t.Length && t[k] == '/')
                        {
                            break;
                        }
                    }

                    return false;
                }

                if (ti >= t.Length)
                {
                    return false;
                }

                if (c == '?')
                {
                    if (t[ti] == '/')
                    {
                        return false;
                    }

                    pi++;
                    ti++;
                    continue;
                }

                if (c == '[')
                {
                    int close = FindClose(p, pi);
                    if (close < 0)
                    {
                        // An unterminated set is a literal bracket.
                        if (t[ti] != '[')
                        {
                            return false;
                        }

                        pi++;
                        ti++;
                        continue;
                    }

                    if (t[ti] == '/' || !SetContains(p, pi + 1, close, t[ti]))
                    {
                        return false;
                    }

                    pi = close + 1;
                    ti++;
                    continue;
                }

                if (c != t[ti])
                {
                    return false;
                }

                pi++;
                ti++;
            }

            return ti == t.Length;
        }

        private static int FindClose(string p, int open)
        {
            int i = open + 1;
            if (i < p.Length && (p[i] == '!' || p[i] == '^'))
            {
                i++;
            }

            if (i < p.Length && p[i] == ']')
            {
                i++;
            }

            for (; i < p.Length; i++)
            {
                if (p[i] == ']')
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool SetContains(string p, int start, int end, char ch)
        {
            bool negate = false;
            int i = start;
            if (i < end && (p[i] == '!' || p[i] == '^'))
            {
                negate = true;
                i++;
            }

            bool found = false;
            while (i < end)
            {
                char lo = p[i];
                if (i + 2 < end && p[i + 1] == '-')
                {
                    char hi = p[i + 2];
                    if (ch >= lo && ch <= hi)
                    {
                        found = true;
                    }

                    i += 3;
                }
                else
                {
                    if (ch == lo)
                    {
                        found = true;
                    }

                    i++;
                }
            }

            return found != negate;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HddBackup.Core.Diagnostics
{
    public enum RootLoginMode
    {
        Yes,
        ProhibitPassword,
        ForcedCommandsOnly,
        No
    }

    /// <summary>
    /// The effective, unconditional view of the OpenSSH server configuration (research R-5): the value
    /// each keyword has when no <c>Match</c> block applies, with the place it comes from.
    /// </summary>
    public sealed class SshSettings
    {
        /// <summary>/etc/ssh/sshd_config exists.</summary>
        public bool ConfigFound { get; internal set; }

        public IReadOnlyList<SettingValue<int>> Ports { get; internal set; }

        public SettingValue<RootLoginMode> PermitRootLogin { get; internal set; }

        public SettingValue<bool> PasswordAuthentication { get; internal set; }

        public SettingValue<bool> KbdInteractiveAuthentication { get; internal set; }

        public SettingValue<bool> UsePam { get; internal set; }

        public SettingValue<bool> PubkeyAuthentication { get; internal set; }

        public SettingValue<bool> PermitEmptyPasswords { get; internal set; }

        /// <summary>Where keys are looked up; tokens (<c>%h</c>, <c>%u</c>) are not expanded here.</summary>
        public SettingValue<IReadOnlyList<string>> AuthorizedKeysFiles { get; internal set; }

        public SettingValue<IReadOnlyList<string>> AllowUsers { get; internal set; }

        public SettingValue<IReadOnlyList<string>> DenyUsers { get; internal set; }

        public SettingValue<IReadOnlyList<string>> AllowGroups { get; internal set; }

        public SettingValue<IReadOnlyList<string>> DenyGroups { get; internal set; }

        /// <summary>Each element is one alternative: the methods that must all succeed. Empty = any single method.</summary>
        public IReadOnlyList<IReadOnlyList<string>> AuthenticationMethods { get; internal set; }

        public bool HasAuthorizedKeysCommand { get; internal set; }

        /// <summary>Keywords (lower case) that also appear inside a <c>Match</c> block.</summary>
        public IReadOnlyList<string> ConditionalKeywords { get; internal set; }

        /// <summary>Password login is possible: <c>PasswordAuthentication</c>, or keyboard-interactive through PAM.</summary>
        public bool PasswordLoginPossible =>
            PasswordAuthentication.Value || (KbdInteractiveAuthentication.Value && UsePam.Value);

        public bool IsConditional(string keyword) => ConditionalKeywords.Contains(keyword.ToLowerInvariant());
    }

    /// <summary>
    /// Parser for <c>/etc/ssh/sshd_config</c> with <c>Include</c>, first-value-wins and <c>Match</c>
    /// handling, following the semantics of <c>sshd -T</c> (which is also the CI oracle).
    /// </summary>
    public static class SshdConfig
    {
        public const string MainFile = "/etc/ssh/sshd_config";
        public const int MaxIncludeDepth = 16;

        public static SshSettings Parse(SystemReader reader)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var ctx = new Context(reader);
            bool found = reader.IsFile(MainFile);
            if (found)
            {
                ProcessFile(ctx, MainFile, 0);
            }

            return ctx.Build(found);
        }

        private static void ProcessFile(Context ctx, string path, int depth)
        {
            string text = ctx.Reader.ReadText(path);
            if (text == null)
            {
                return;
            }

            bool outerMatch = ctx.InMatch;
            var lines = SystemReader.SplitLines(text);
            for (int i = 0; i < lines.Count; i++)
            {
                var tokens = Tokenize(lines[i]);
                if (tokens.Count == 0)
                {
                    continue;
                }

                string keyword = tokens[0].ToLowerInvariant();
                var args = tokens.Skip(1).ToList();
                if (keyword == "match")
                {
                    ctx.InMatch = true;
                    continue;
                }

                if (keyword == "include")
                {
                    if (depth < MaxIncludeDepth)
                    {
                        foreach (string file in ExpandInclude(ctx.Reader, args))
                        {
                            ProcessFile(ctx, file, depth + 1);
                        }
                    }

                    continue;
                }

                ctx.Apply(keyword, args, path, i + 1);
            }

            // A Match block ends with the file that contains it.
            ctx.InMatch = outerMatch;
        }

        /// <summary>Splits a configuration line into the keyword and its arguments (quotes honoured, "#" and "=" handled).</summary>
        internal static List<string> Tokenize(string line)
        {
            var tokens = new List<string>();
            int i = 0;
            int n = line.Length;
            while (i < n)
            {
                while (i < n && char.IsWhiteSpace(line[i]))
                {
                    i++;
                }

                if (i >= n)
                {
                    break;
                }

                if (line[i] == '#')
                {
                    break;
                }

                if (tokens.Count == 1 && line[i] == '=')
                {
                    // One '=' may separate the keyword from its arguments.
                    i++;
                    continue;
                }

                var sb = new StringBuilder();
                bool quoted = false;
                while (i < n)
                {
                    char c = line[i];
                    if (quoted)
                    {
                        if (c == '"')
                        {
                            quoted = false;
                        }
                        else if (c == '\\' && i + 1 < n && (line[i + 1] == '"' || line[i + 1] == '\\'))
                        {
                            sb.Append(line[++i]);
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        i++;
                        continue;
                    }

                    if (c == '"')
                    {
                        quoted = true;
                        i++;
                        continue;
                    }

                    if (char.IsWhiteSpace(c))
                    {
                        break;
                    }

                    if (tokens.Count == 0 && c == '=')
                    {
                        break;
                    }

                    sb.Append(c);
                    i++;
                }

                tokens.Add(sb.ToString());
            }

            return tokens;
        }

        private static IEnumerable<string> ExpandInclude(SystemReader reader, IEnumerable<string> patterns)
        {
            foreach (string raw in patterns)
            {
                string pattern = raw.StartsWith("/", StringComparison.Ordinal) ? raw : "/etc/ssh/" + raw;
                var parts = pattern.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                var current = new List<string> { string.Empty };
                for (int p = 0; p < parts.Length; p++)
                {
                    var next = new List<string>();
                    bool last = p == parts.Length - 1;
                    foreach (string dir in current)
                    {
                        string dirPath = dir.Length == 0 ? "/" : dir;
                        if (parts[p].IndexOfAny(new[] { '*', '?', '[' }) < 0)
                        {
                            next.Add(dir + "/" + parts[p]);
                            continue;
                        }

                        var list = reader.List(dirPath);
                        if (list == null)
                        {
                            continue;
                        }

                        foreach (var e in list)
                        {
                            if (WildcardMatcher.IsMatch(parts[p], e.Name))
                            {
                                next.Add(dir + "/" + e.Name);
                            }
                        }
                    }

                    current = next;
                    if (last)
                    {
                        break;
                    }
                }

                foreach (string path in current.OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (reader.IsFile(path))
                    {
                        yield return path;
                    }
                }
            }
        }

        private sealed class Context
        {
            private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> _conditional = new HashSet<string>(StringComparer.Ordinal);
            private readonly List<SettingValue<int>> _ports = new List<SettingValue<int>>();
            private readonly Dictionary<string, List<string>> _lists = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            private readonly Dictionary<string, Origin> _listOrigin = new Dictionary<string, Origin>(StringComparer.Ordinal);
            private readonly Dictionary<string, Origin> _origin = new Dictionary<string, Origin>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _single = new Dictionary<string, string>(StringComparer.Ordinal);
            private List<IReadOnlyList<string>> _methods;
            private List<string> _keysFiles;
            private bool _keysCommand;

            public Context(SystemReader reader)
            {
                Reader = reader;
            }

            public SystemReader Reader { get; }

            public bool InMatch { get; set; }

            public void Apply(string keyword, List<string> args, string file, int line)
            {
                if (keyword == "challengeresponseauthentication")
                {
                    keyword = "kbdinteractiveauthentication";
                }

                if (InMatch)
                {
                    _conditional.Add(keyword);
                    return;
                }

                var origin = new Origin(file, line);
                switch (keyword)
                {
                    case "port":
                        foreach (string a in args)
                        {
                            if (int.TryParse(a, out int port) && port > 0 && port < 65536)
                            {
                                _ports.Add(new SettingValue<int>(port, file, line, false));
                            }
                        }

                        break;
                    case "allowusers":
                    case "denyusers":
                    case "allowgroups":
                    case "denygroups":
                        if (!_lists.TryGetValue(keyword, out var list))
                        {
                            list = new List<string>();
                            _lists[keyword] = list;
                            _listOrigin[keyword] = origin;
                        }

                        list.AddRange(args);
                        break;
                    case "authorizedkeysfile":
                        if (_keysFiles == null)
                        {
                            _keysFiles = new List<string>(args);
                            _origin[keyword] = origin;
                        }

                        break;
                    case "authorizedkeyscommand":
                        if (args.Count > 0 && !args[0].Equals("none", StringComparison.OrdinalIgnoreCase))
                        {
                            _keysCommand = true;
                        }

                        break;
                    case "authenticationmethods":
                        if (_methods == null)
                        {
                            _methods = new List<IReadOnlyList<string>>();
                            foreach (string alt in args)
                            {
                                if (alt.Equals("any", StringComparison.OrdinalIgnoreCase))
                                {
                                    _methods.Clear();
                                    break;
                                }

                                _methods.Add(alt.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(m => m.ToLowerInvariant()).ToList());
                            }

                            _origin[keyword] = origin;
                        }

                        break;
                    case "permitrootlogin":
                    case "passwordauthentication":
                    case "kbdinteractiveauthentication":
                    case "usepam":
                    case "pubkeyauthentication":
                    case "permitemptypasswords":
                        if (_seen.Add(keyword) && args.Count > 0)
                        {
                            _single[keyword] = args[0].ToLowerInvariant();
                            _origin[keyword] = origin;
                        }

                        break;
                }
            }

            public SshSettings Build(bool found)
            {
                return new SshSettings
                {
                    ConfigFound = found,
                    Ports = _ports.Count > 0 ? _ports : new List<SettingValue<int>> { new SettingValue<int>(22, null, 0, false) },
                    PermitRootLogin = Root(),
                    PasswordAuthentication = Flag("passwordauthentication", true),
                    KbdInteractiveAuthentication = Flag("kbdinteractiveauthentication", true),
                    UsePam = Flag("usepam", false),
                    PubkeyAuthentication = Flag("pubkeyauthentication", true),
                    PermitEmptyPasswords = Flag("permitemptypasswords", false),
                    AuthorizedKeysFiles = new SettingValue<IReadOnlyList<string>>(
                        _keysFiles ?? new List<string> { ".ssh/authorized_keys", ".ssh/authorized_keys2" },
                        _keysFiles == null ? null : _origin["authorizedkeysfile"].File,
                        _keysFiles == null ? 0 : _origin["authorizedkeysfile"].Line,
                        _conditional.Contains("authorizedkeysfile")),
                    AllowUsers = List("allowusers"),
                    DenyUsers = List("denyusers"),
                    AllowGroups = List("allowgroups"),
                    DenyGroups = List("denygroups"),
                    AuthenticationMethods = _methods ?? new List<IReadOnlyList<string>>(),
                    HasAuthorizedKeysCommand = _keysCommand,
                    ConditionalKeywords = _conditional.OrderBy(x => x, StringComparer.Ordinal).ToList()
                };
            }

            private SettingValue<bool> Flag(string keyword, bool fallback)
            {
                bool conditional = _conditional.Contains(keyword);
                if (_single.TryGetValue(keyword, out string v))
                {
                    var o = _origin[keyword];
                    return new SettingValue<bool>(v == "yes" || v == "true", o.File, o.Line, conditional);
                }

                return new SettingValue<bool>(fallback, null, 0, conditional);
            }

            private SettingValue<RootLoginMode> Root()
            {
                bool conditional = _conditional.Contains("permitrootlogin");
                if (!_single.TryGetValue("permitrootlogin", out string v))
                {
                    return new SettingValue<RootLoginMode>(RootLoginMode.ProhibitPassword, null, 0, conditional);
                }

                RootLoginMode mode;
                switch (v)
                {
                    case "yes":
                    case "true":
                        mode = RootLoginMode.Yes;
                        break;
                    case "no":
                    case "false":
                        mode = RootLoginMode.No;
                        break;
                    case "forced-commands-only":
                        mode = RootLoginMode.ForcedCommandsOnly;
                        break;
                    default:
                        mode = RootLoginMode.ProhibitPassword; // prohibit-password, without-password
                        break;
                }

                var o = _origin["permitrootlogin"];
                return new SettingValue<RootLoginMode>(mode, o.File, o.Line, conditional);
            }

            private SettingValue<IReadOnlyList<string>> List(string keyword)
            {
                bool conditional = _conditional.Contains(keyword);
                if (_lists.TryGetValue(keyword, out var list))
                {
                    var o = _listOrigin[keyword];
                    return new SettingValue<IReadOnlyList<string>>(list, o.File, o.Line, conditional);
                }

                return new SettingValue<IReadOnlyList<string>>(new List<string>(), null, 0, conditional);
            }
        }

        private struct Origin
        {
            public Origin(string file, int line)
            {
                File = file;
                Line = line;
            }

            public string File { get; }

            public int Line { get; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace HddBackup.Core.Repair
{
    /// <summary>Why a text is not accepted as one public key (feature 011, research R-8).</summary>
    public enum KeyRefusal
    {
        None,
        Empty,
        PrivateKey,
        Certificate,
        Several,
        UnknownType,
        BadEncoding,
        TypeMismatch,
        TooLong
    }

    [Flags]
    public enum KeyWarnings
    {
        None = 0,

        /// <summary>A key type that current servers ignore (DSA).</summary>
        LegacyType = 1,

        /// <summary>The line starts with options (from=, command=, no-pty ...) that restrict the key.</summary>
        HasOptions = 2
    }

    /// <summary>
    /// One public key line of an <c>authorized_keys</c> file: optional options, type, base64 data, comment. Only the
    /// public half of a key pair is accepted; private keys are recognised and refused without being kept.
    /// </summary>
    public sealed class PublicKey
    {
        public const int MaxLength = 16 * 1024;

        private static readonly string[] Types =
        {
            "ssh-ed25519", "ssh-rsa", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521",
            "sk-ssh-ed25519@openssh.com", "sk-ecdsa-sha2-nistp256@openssh.com", "ssh-dss"
        };

        private PublicKey()
        {
        }

        public string Type { get; private set; }

        /// <summary>The options before the type, as typed; empty when there are none.</summary>
        public string Options { get; private set; } = string.Empty;

        public string Comment { get; private set; } = string.Empty;

        /// <summary><c>SHA256:</c> and the unpadded base64 of the SHA-256 of the key data, as <c>ssh-keygen -l</c> prints it.</summary>
        public string Fingerprint { get; private set; }

        public KeyWarnings Warnings { get; private set; }

        /// <summary>The single line to write into the key file (no line break).</summary>
        public string Line { get; private set; }

        internal byte[] Body { get; private set; }

        public static bool TryParse(string text, out PublicKey key, out KeyRefusal refusal)
        {
            key = null;
            refusal = KeyRefusal.None;
            if (string.IsNullOrWhiteSpace(text))
            {
                refusal = KeyRefusal.Empty;
                return false;
            }

            if (text.IndexOf("PRIVATE KEY", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("-----BEGIN", StringComparison.Ordinal) >= 0
                || text.StartsWith("PuTTY-User-Key-File", StringComparison.Ordinal))
            {
                refusal = KeyRefusal.PrivateKey;
                return false;
            }

            if (text.Length > MaxLength)
            {
                refusal = KeyRefusal.TooLong;
                return false;
            }

            string line = text.TrimEnd('\r', '\n', ' ', '\t').TrimStart(' ', '\t');
            if (line.IndexOf('\n') >= 0 || line.IndexOf('\r') >= 0)
            {
                refusal = KeyRefusal.Several;
                return false;
            }

            var tokens = Tokenize(line);
            if (tokens == null || tokens.Count < 2)
            {
                refusal = tokens == null ? KeyRefusal.BadEncoding : KeyRefusal.UnknownType;
                return false;
            }

            int at = 0;
            string options = string.Empty;
            if (!IsKnownType(tokens[0].Text) && !tokens[0].Text.Contains("-cert-v01@openssh.com"))
            {
                // an options field comes first, then the type
                options = tokens[0].Text;
                at = 1;
                if (tokens.Count < 3)
                {
                    refusal = KeyRefusal.UnknownType;
                    return false;
                }
            }

            string type = tokens[at].Text;
            if (type.IndexOf("-cert-v01@openssh.com", StringComparison.Ordinal) >= 0)
            {
                refusal = KeyRefusal.Certificate;
                return false;
            }

            if (!IsKnownType(type))
            {
                refusal = KeyRefusal.UnknownType;
                return false;
            }

            if (at + 1 >= tokens.Count)
            {
                refusal = KeyRefusal.BadEncoding;
                return false;
            }

            string base64 = tokens[at + 1].Text;
            byte[] body;
            try
            {
                body = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                refusal = KeyRefusal.BadEncoding;
                return false;
            }

            if (body.Length < 8)
            {
                refusal = KeyRefusal.BadEncoding;
                return false;
            }

            long nameLength = ((long)body[0] << 24) | ((long)body[1] << 16) | ((long)body[2] << 8) | body[3];
            if (nameLength < 1 || nameLength > body.Length - 4)
            {
                refusal = KeyRefusal.BadEncoding;
                return false;
            }

            string inner = Encoding.ASCII.GetString(body, 4, (int)nameLength);
            if (!string.Equals(inner, type, StringComparison.Ordinal))
            {
                refusal = KeyRefusal.TypeMismatch;
                return false;
            }

            string comment = tokens.Count > at + 2 ? line.Substring(tokens[at + 2].Start).Trim() : string.Empty;
            string fingerprint;
            using (var sha = SHA256.Create())
            {
                fingerprint = "SHA256:" + Convert.ToBase64String(sha.ComputeHash(body)).TrimEnd('=');
            }

            var warnings = KeyWarnings.None;
            if (type == "ssh-dss")
            {
                warnings |= KeyWarnings.LegacyType;
            }

            if (options.Length > 0)
            {
                warnings |= KeyWarnings.HasOptions;
            }

            key = new PublicKey
            {
                Type = type,
                Options = options,
                Comment = comment,
                Fingerprint = fingerprint,
                Warnings = warnings,
                Body = body,
                Line = (options.Length > 0 ? options + " " : string.Empty) + type + " " + base64 + (comment.Length > 0 ? " " + comment : string.Empty)
            };
            return true;
        }

        /// <summary>True when <paramref name="fileText"/> already holds this key (same type and key data; options and comment do not matter).</summary>
        public bool IsIn(string fileText)
        {
            if (string.IsNullOrEmpty(fileText))
            {
                return false;
            }

            foreach (string raw in fileText.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                if (TryParse(line, out var other, out _) && other.Type == Type && SameBytes(other.Body, Body))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsKnownType(string type) => Array.IndexOf(Types, type) >= 0;

        private struct Token
        {
            public string Text;
            public int Start;
        }

        /// <summary>Splits at white space outside double quotes; null when a quote is not closed.</summary>
        private static List<Token> Tokenize(string line)
        {
            var result = new List<Token>();
            int i = 0;
            while (i < line.Length)
            {
                while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
                {
                    i++;
                }

                if (i >= line.Length)
                {
                    break;
                }

                int start = i;
                bool quoted = false;
                while (i < line.Length && (quoted || (line[i] != ' ' && line[i] != '\t')))
                {
                    if (line[i] == '\\' && quoted && i + 1 < line.Length)
                    {
                        i++;
                    }
                    else if (line[i] == '"')
                    {
                        quoted = !quoted;
                    }

                    i++;
                }

                if (quoted)
                {
                    return null;
                }

                result.Add(new Token { Text = line.Substring(start, i - start), Start = start });
            }

            return result;
        }
    }
}

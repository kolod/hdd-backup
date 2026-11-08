using System;

namespace HddBackup.Core.Editing
{
    /// <summary>The kinds of repair the SSH check wizard can make (feature 011).</summary>
    public enum RepairKind : byte
    {
        None = 0,
        EnableServer = 1,
        UnmaskServer = 2,
        AllowUser = 3,
        UnblockUser = 4,
        AllowGroup = 5,
        EnableKeyAuth = 6,
        RootKeysOnly = 7,
        RootPassword = 8,
        EnablePasswordAuth = 9,
        FixShell = 10,
        FixKeyFileModes = 11,
        AddKey = 12
    }

    /// <summary>
    /// What a group of changes is called, in words the user sees: a repair kind and up to two short arguments
    /// (an account, a unit, a fingerprint). Never key text and never a secret.
    /// </summary>
    public sealed class RepairTitle
    {
        public const int MaxArgument = 120;

        public RepairTitle(RepairKind kind, string arg1 = null, string arg2 = null)
        {
            Kind = kind;
            Arg1 = Clip(arg1);
            Arg2 = Clip(arg2);
        }

        public RepairKind Kind { get; }

        public string Arg1 { get; }

        public string Arg2 { get; }

        private static string Clip(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }

            return s.Length <= MaxArgument ? s : s.Substring(0, MaxArgument);
        }

        public override string ToString() => Kind + (Arg1.Length > 0 ? " " + Arg1 : string.Empty) + (Arg2.Length > 0 ? " " + Arg2 : string.Empty);
    }
}

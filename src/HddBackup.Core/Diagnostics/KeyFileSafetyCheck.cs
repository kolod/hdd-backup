using System;
using System.Collections.Generic;
using System.Linq;
using HddBackup.Core.Browsing;

namespace HddBackup.Core.Diagnostics
{
    /// <summary>Why the server (with StrictModes on) would ignore the key file of an account.</summary>
    public enum KeyFileSafety
    {
        /// <summary>Nothing that StrictModes would refuse was found (or there is nothing to check).</summary>
        Ok,

        /// <summary>The key file is writable by its group or by others.</summary>
        FileUnsafe,

        /// <summary>The folder of the key file (usually .ssh) is writable by its group or by others.</summary>
        SshDirUnsafe,

        /// <summary>The home folder, or a folder above the key file, is writable by its group or by others.</summary>
        HomeWritable,

        /// <summary>The key file or a folder on its way is owned by somebody other than the account and root.</summary>
        WrongOwner
    }

    /// <summary>One file or folder on the way to a key file that the server would not accept.</summary>
    internal sealed class KeyFileProblem
    {
        public string Path { get; set; }

        public FsEntry Entry { get; set; }

        /// <summary>0 = the key file, 1 = its folder, 2 and up = folders above it (the home folder is the last one).</summary>
        public int Level { get; set; }

        public bool IsHome { get; set; }

        public bool WrongOwner { get; set; }

        public bool Writable { get; set; }
    }

    /// <summary>
    /// The check OpenSSH makes with <c>StrictModes yes</c> before it reads <c>authorized_keys</c>: the file and
    /// every folder from it up to the home folder must be owned by the account or by root and must not be writable
    /// by group or others (research R-6 of feature 011). Components that do not exist are skipped, so the check can
    /// also be asked about a key file that is about to be created.
    /// </summary>
    internal static class KeyFileSafetyCheck
    {
        /// <summary>The group-write and other-write permission bits.</summary>
        private const int GroupOtherWrite = 0x12;

        public static KeyFileSafety Evaluate(SystemReader reader, Account account, string keyFile, out string problemPath)
        {
            problemPath = null;
            var first = Problems(reader, account, keyFile).FirstOrDefault();
            if (first == null)
            {
                return KeyFileSafety.Ok;
            }

            problemPath = first.Path;
            if (first.WrongOwner)
            {
                return KeyFileSafety.WrongOwner;
            }

            return first.Level == 0 ? KeyFileSafety.FileUnsafe : first.Level == 1 && !first.IsHome ? KeyFileSafety.SshDirUnsafe : KeyFileSafety.HomeWritable;
        }

        /// <summary>Every existing file or folder from the key file up to the home folder that the server would refuse, nearest first.</summary>
        public static List<KeyFileProblem> Problems(SystemReader reader, Account account, string keyFile)
        {
            var result = new List<KeyFileProblem>();
            if (reader == null || account == null || string.IsNullOrEmpty(keyFile))
            {
                return result;
            }

            string home = Normalize(reader.Resolve(account.Home) ?? account.Home);
            string current = Normalize(reader.Resolve(keyFile) ?? keyFile);
            var chain = new List<string>();
            while (true)
            {
                chain.Add(current);
                if (current.Length == 0 || current == home)
                {
                    break;
                }

                int slash = current.LastIndexOf('/');
                current = slash <= 0 ? string.Empty : current.Substring(0, slash);
            }

            for (int i = 0; i < chain.Count; i++)
            {
                string path = chain[i].Length == 0 ? "/" : chain[i];
                var entry = reader.Lookup(path);
                if (entry == null)
                {
                    continue;
                }

                bool wrongOwner = entry.Uid != account.Uid && entry.Uid != 0;
                bool writable = (entry.Mode & GroupOtherWrite) != 0;
                if (wrongOwner || writable)
                {
                    result.Add(new KeyFileProblem { Path = path, Entry = entry, Level = i, IsHome = chain[i] == home, WrongOwner = wrongOwner, Writable = writable });
                }
            }

            return result;
        }

        /// <summary>No trailing slash; the root folder is the empty string.</summary>
        private static string Normalize(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.TrimEnd('/');
        }
    }
}

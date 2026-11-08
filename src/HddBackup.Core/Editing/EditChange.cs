using System;
using System.Linq;

namespace HddBackup.Core.Editing
{
    public enum EditKind : byte
    {
        EditContent = 1,
        CreateFile = 2,
        CreateDirectory = 3,
        Delete = 4,
        Rename = 5,
        CreateSymlink = 6,
        SetAttributes = 7
    }

    /// <summary>One change of the user, kept in the diff file until it is saved. Holds only the user's own edits, names and paths.</summary>
    public sealed class EditChange
    {
        private EditChange(EditKind kind, string path)
        {
            Kind = kind;
            Path = path;
        }

        public EditKind Kind { get; }

        /// <summary>Absolute path in the file system; for creations, the path of the new entry.</summary>
        public string Path { get; }

        /// <summary>Rename only: the new last component of the name.</summary>
        public string NewName { get; private set; } = string.Empty;

        /// <summary>EditContent and CreateFile: the new bytes.</summary>
        public byte[] Content { get; private set; }

        /// <summary>EditContent, Delete, Rename: what the user saw; a mismatch at save time stops the change.</summary>
        public OriginalStamp Original { get; private set; }

        /// <summary>CreateSymlink: where the link points (1 to 59 bytes in UTF-8).</summary>
        public string LinkTarget { get; private set; } = string.Empty;

        /// <summary>CreateFile, CreateDirectory, SetAttributes: permission bits (0 to 0xFFF); null keeps the default or the current value.</summary>
        public ushort? Mode { get; private set; }

        /// <summary>CreateFile, CreateDirectory, SetAttributes: owner; null keeps the default (the parent's) or the current value.</summary>
        public uint? Uid { get; private set; }

        /// <summary>CreateFile, CreateDirectory, SetAttributes: group; null keeps the default (the parent's) or the current value.</summary>
        public uint? Gid { get; private set; }

        /// <summary>The repair this change belongs to (0 = none); changes of one group are undone together.</summary>
        public int Group { get; private set; }

        /// <summary>What the group is called; null for changes outside a repair.</summary>
        public RepairTitle Title { get; private set; }

        public static EditChange Edit(string path, byte[] content, OriginalStamp original) =>
            new EditChange(EditKind.EditContent, path) { Content = content, Original = original };

        public static EditChange NewFile(string path, byte[] content, ushort? mode = null, uint? uid = null, uint? gid = null) =>
            new EditChange(EditKind.CreateFile, path) { Content = content, Mode = mode, Uid = uid, Gid = gid };

        public static EditChange NewDirectory(string path, ushort? mode = null, uint? uid = null, uint? gid = null) =>
            new EditChange(EditKind.CreateDirectory, path) { Mode = mode, Uid = uid, Gid = gid };

        public static EditChange NewSymlink(string path, string target) =>
            new EditChange(EditKind.CreateSymlink, path) { LinkTarget = target ?? string.Empty };

        /// <summary>Changes permission bits and/or owner of an existing entry; the type bits, times (except ctime) and content stay.</summary>
        public static EditChange Attributes(string path, ushort? mode, uint? uid, uint? gid, OriginalStamp original = null) =>
            new EditChange(EditKind.SetAttributes, path) { Mode = mode, Uid = uid, Gid = gid, Original = original };

        /// <summary>The same change as a member of repair group <paramref name="group"/>.</summary>
        public EditChange InGroup(int group, RepairTitle title)
        {
            var c = Clone(Path);
            c.Group = group;
            c.Title = title;
            return c;
        }

        public static EditChange Remove(string path, OriginalStamp original) =>
            new EditChange(EditKind.Delete, path) { Original = original };

        public static EditChange Move(string path, string newName, OriginalStamp original) =>
            new EditChange(EditKind.Rename, path) { NewName = newName, Original = original };

        /// <summary>Parent directory and last name of an absolute path.</summary>
        public static void Split(string path, out string parent, out string name)
        {
            string t = path.TrimEnd('/');
            int slash = t.LastIndexOf('/');
            parent = slash <= 0 ? "/" : t.Substring(0, slash);
            name = t.Substring(slash + 1);
        }

        public static string Join(string parent, string name) => parent == "/" ? "/" + name : parent + "/" + name;

        internal EditChange WithPath(string path) => Clone(path);

        private EditChange Clone(string path) => new EditChange(Kind, path)
        {
            NewName = NewName,
            Content = Content,
            Original = Original,
            LinkTarget = LinkTarget,
            Mode = Mode,
            Uid = Uid,
            Gid = Gid,
            Group = Group,
            Title = Title
        };

        public override string ToString() => Kind + " " + Path + (NewName.Length > 0 ? " -> " + NewName : string.Empty)
            + (LinkTarget.Length > 0 ? " => " + LinkTarget : string.Empty);

        internal static void Apply(Ext4Editor editor, EditChange c)
        {
            switch (c.Kind)
            {
                case EditKind.EditContent:
                    editor.ReplaceContent(c.Path, c.Content, c.Original);
                    break;
                case EditKind.CreateFile:
                    Split(c.Path, out string pf, out string nf);
                    editor.CreateFile(pf, nf, c.Content ?? new byte[0], new EntryAttributes(c.Mode, c.Uid, c.Gid));
                    break;
                case EditKind.CreateDirectory:
                    Split(c.Path, out string pd, out string nd);
                    editor.CreateDirectory(pd, nd, new EntryAttributes(c.Mode, c.Uid, c.Gid));
                    break;
                case EditKind.Delete:
                    editor.Delete(c.Path);
                    break;
                case EditKind.Rename:
                    editor.Rename(c.Path, c.NewName);
                    break;
                case EditKind.CreateSymlink:
                    Split(c.Path, out string ps, out string ns);
                    editor.CreateSymlink(ps, ns, c.LinkTarget);
                    break;
                case EditKind.SetAttributes:
                    editor.SetAttributes(c.Path, new EntryAttributes(c.Mode, c.Uid, c.Gid), c.Original);
                    break;
                default:
                    throw new InvalidOperationException(c.Kind.ToString());
            }
        }
    }

    /// <summary>Permission bits and owner for a new or an existing entry; null fields are left to the default or kept.</summary>
    public struct EntryAttributes
    {
        public EntryAttributes(ushort? mode, uint? uid, uint? gid)
        {
            Mode = mode;
            Uid = uid;
            Gid = gid;
        }

        public ushort? Mode { get; }

        public uint? Uid { get; }

        public uint? Gid { get; }

        public bool IsEmpty => !Mode.HasValue && !Uid.HasValue && !Gid.HasValue;
    }
}

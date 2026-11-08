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
        Rename = 5
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

        public static EditChange Edit(string path, byte[] content, OriginalStamp original) =>
            new EditChange(EditKind.EditContent, path) { Content = content, Original = original };

        public static EditChange NewFile(string path, byte[] content) =>
            new EditChange(EditKind.CreateFile, path) { Content = content };

        public static EditChange NewDirectory(string path) => new EditChange(EditKind.CreateDirectory, path);

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

        internal EditChange WithPath(string path)
        {
            var c = new EditChange(Kind, path) { NewName = NewName, Content = Content, Original = Original };
            return c;
        }

        public override string ToString() => Kind + " " + Path + (NewName.Length > 0 ? " -> " + NewName : string.Empty);

        internal static void Apply(Ext4Editor editor, EditChange c)
        {
            switch (c.Kind)
            {
                case EditKind.EditContent:
                    editor.ReplaceContent(c.Path, c.Content, c.Original);
                    break;
                case EditKind.CreateFile:
                    Split(c.Path, out string pf, out string nf);
                    editor.CreateFile(pf, nf, c.Content ?? new byte[0]);
                    break;
                case EditKind.CreateDirectory:
                    Split(c.Path, out string pd, out string nd);
                    editor.CreateDirectory(pd, nd);
                    break;
                case EditKind.Delete:
                    editor.Delete(c.Path);
                    break;
                case EditKind.Rename:
                    editor.Rename(c.Path, c.NewName);
                    break;
                default:
                    throw new InvalidOperationException(c.Kind.ToString());
            }
        }
    }
}

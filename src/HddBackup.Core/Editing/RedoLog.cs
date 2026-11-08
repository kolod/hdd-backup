using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HddBackup.Core.Editing
{
    /// <summary>
    /// The write set of a save, kept beside the image while it is replayed (research R-8). Its presence means
    /// "a save was committed": replaying it again gives the same result, so an interrupted save can be finished.
    /// </summary>
    public sealed class RedoLog
    {
        public const string Extension = ".hddbackup-redo";
        private const string Magic = "HDDREDO1";

        public RedoLog(ImageIdentity identity, WriteSet writes)
        {
            Identity = identity;
            Writes = writes;
        }

        /// <summary>The identity of the image before the save started.</summary>
        public ImageIdentity Identity { get; }

        public WriteSet Writes { get; }

        public static string PathFor(string imagePath) => imagePath + Extension;

        public void Write(string path)
        {
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
            {
                w.Write(Encoding.ASCII.GetBytes(Magic));
                w.Write(1);
                Identity.Write(w);
                w.Write(Writes.Writes.Count);
                foreach (var b in Writes.Writes)
                {
                    w.Write(b.Offset);
                    w.Write((byte)b.Phase);
                    w.Write(b.Data.Length);
                    w.Write(b.Data);
                }
            }

            AtomicFile.Write(path, DiffFile.Seal(ms.ToArray()));
        }

        /// <summary>Reads a valid redo log; null when there is none or it is incomplete (then the save was not committed).</summary>
        public static RedoLog Read(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                byte[] body = DiffFile.Unseal(File.ReadAllBytes(path));
                if (body == null)
                {
                    return null;
                }

                using (var r = new BinaryReader(new MemoryStream(body), Encoding.UTF8))
                {
                    if (Encoding.ASCII.GetString(r.ReadBytes(8)) != Magic || r.ReadInt32() != 1)
                    {
                        return null;
                    }

                    var identity = ImageIdentity.Read(r);
                    int count = r.ReadInt32();
                    var set = new WriteSet();
                    for (int i = 0; i < count; i++)
                    {
                        long offset = r.ReadInt64();
                        var phase = (WritePhase)r.ReadByte();
                        int len = r.ReadInt32();
                        set.Add(new BlockWrite(offset, r.ReadBytes(len), phase));
                    }

                    return new RedoLog(identity, set);
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException || ex is IOException || ex is ArgumentException)
            {
                return null;
            }
        }
    }
}

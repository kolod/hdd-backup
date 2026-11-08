using System;
using System.IO;
using HddBackup.Core.Browsing;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Ext4;

namespace HddBackup.Core.Editing
{
    /// <summary>
    /// Edit mode of an ext4 partition on a physical card (feature 010): every change is built, checked and
    /// written to the card at once. There is no diff file and no checksum. Nothing outside the partition is
    /// written, and the volumes of other partitions are not locked or dismounted.
    /// </summary>
    public sealed class CardEditSession : IDisposable
    {
        private readonly IDeviceAccess _access;
        private readonly DeviceInfo _device;
        private readonly long _offset;
        private readonly long _length;

        private CardEditSession(IDeviceAccess access, DeviceInfo device, long offset, long length)
        {
            _access = access;
            _device = device;
            _offset = offset;
            _length = length;
        }

        /// <summary>
        /// Checks that the card may be edited (removable, not the system disk, not write-protected, an ext4
        /// file system that is clean and uses only supported features). Throws <see cref="EditRefusedException"/>;
        /// nothing is written.
        /// </summary>
        public static CardEditSession Open(IDeviceAccess access, DeviceInfo device, BrowsePartition partition)
        {
            if (!device.IsRemovableMedia)
            {
                throw new EditRefusedException(EditRefused.NotRemovable);
            }

            if (device.IsWriteProtected)
            {
                throw new EditRefusedException(EditRefused.WriteProtected);
            }

            if (partition == null || !partition.IsBrowsable)
            {
                throw new EditRefusedException(EditRefused.NotBrowsable);
            }

            using (var read = access.OpenForRead(device))
            using (var part = new PartitionView(read, partition.Offset, partition.Length))
            using (var reader = new Ext4Reader(part))
            {
                var support = Ext4EditSupportCheck.Check(reader.Superblock);
                if (!support.Editable)
                {
                    throw new EditRefusedException(ImageEditSession.MapRefusal(support.Reason), string.Join(", ", support.Features));
                }
            }

            return new CardEditSession(access, device, partition.Offset, partition.Length);
        }

        /// <summary>
        /// Builds one change, checks the file system as it would be afterwards and writes it to the card. When the
        /// result is not <see cref="SaveStatus.Saved"/> the card was not changed, or (Interrupted) was changed only
        /// in steps that leave the file entirely old or entirely new.
        /// </summary>
        public SaveResult Apply(EditChange change, Action<long, long> progress, System.Threading.CancellationToken ct)
        {
            return Apply(new[] { change }, progress, ct);
        }

        /// <summary>
        /// Builds all <paramref name="changes"/> one after the other on one editor (each sees the writes of the
        /// ones before), checks the file system as it would be afterwards once and writes the combined, ordered
        /// write set. If any change cannot be made nothing is written (feature 011, research R-9).
        /// </summary>
        public SaveResult Apply(System.Collections.Generic.IReadOnlyList<EditChange> changes, Action<long, long> progress, System.Threading.CancellationToken ct)
        {
            bool writing = false;
            try
            {
                using (var disk = _access.OpenForPartitionWrite(_device, _offset, _length))
                {
                    var editor = new Ext4Editor(disk);
                    foreach (var change in changes)
                    {
                        ct.ThrowIfCancellationRequested();
                        EditChange.Apply(editor, change);
                    }

                    var set = editor.Writes;
                    var findings = EditPreflight.Check(set, disk);
                    if (findings.Count > 0)
                    {
                        return new SaveResult { Status = SaveStatus.Refused, Error = EditError.Internal, Detail = findings[0] };
                    }

                    ct.ThrowIfCancellationRequested();
                    writing = true;
                    long total = Math.Max(1, set.TotalBytes);
                    set.ApplyTo(disk, done => progress?.Invoke(done, total), ct);
                    return new SaveResult { Status = SaveStatus.Saved, ChangesWritten = changes.Count };
                }
            }
            catch (EditException ex)
            {
                return new SaveResult { Status = SaveStatus.Refused, Error = ex.Code, Detail = ex.Detail, Needed = ex.Needed, Available = ex.Available };
            }
            catch (OperationCanceledException)
            {
                return new SaveResult { Status = writing ? SaveStatus.Interrupted : SaveStatus.Failed };
            }
            catch (ImagingException ex)
            {
                return new SaveResult { Status = writing ? SaveStatus.Interrupted : SaveStatus.Failed, ErrorCode = ex.Code, Offset = ex.Offset, Detail = ex.Detail };
            }
            catch (IOException ex)
            {
                return new SaveResult { Status = writing ? SaveStatus.Interrupted : SaveStatus.Failed, Detail = ex.Message };
            }
        }

        public void Dispose()
        {
        }
    }
}

using System;
using System.Collections.Generic;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Tests.Fakes
{
    /// <summary>Maps <see cref="DeviceInfo.DiskNumber"/> to in-memory devices and records calls.</summary>
    public sealed class FakeDeviceAccess : IDeviceAccess
    {
        private readonly Dictionary<int, IBlockDevice> _devices = new Dictionary<int, IBlockDevice>();

        public PathInfo PathInfo { get; set; } = new PathInfo { FreeBytes = long.MaxValue, FileSystem = "NTFS" };

        public bool FailLock { get; set; }

        public EjectResult EjectResult { get; set; } = new EjectResult(EjectStatus.Ok);

        public int EjectCalls { get; private set; }

        public int ExclusiveOpens { get; private set; }

        public int ExclusiveReleases { get; private set; }

        public int ReadOpens { get; private set; }

        public int ReadReleases { get; private set; }

        public bool AnyWriteAttemptOnReadHandle { get; private set; }

        public DeviceInfo Add(int diskNumber, IBlockDevice device, Action<DeviceInfo> configure = null)
        {
            _devices[diskNumber] = device;
            var info = new DeviceInfo
            {
                DiskNumber = diskNumber,
                Model = "Fake " + diskNumber,
                BusType = BusType.Usb,
                Size = device.Size,
                LogicalSectorSize = device.SectorSize,
                PartitionTable = SafeReadTable(device)
            };
            (device as FaultyBlockDevice)?.Reset();
            configure?.Invoke(info);
            return info;
        }

        public IBlockDevice OpenForRead(DeviceInfo device)
        {
            ReadOpens++;
            return new ReadOnlyHandle(_devices[device.DiskNumber], this);
        }

        public IExclusiveDevice OpenExclusiveForWrite(DeviceInfo device)
        {
            if (FailLock)
            {
                throw new ImagingException(ErrorCode.LockFailed, "fake lock failure");
            }

            ExclusiveOpens++;
            return new Exclusive(_devices[device.DiskNumber], this);
        }

        public EjectResult Eject(DeviceInfo device)
        {
            EjectCalls++;
            return EjectResult;
        }

        public PathInfo GetPathInfo(string path) => PathInfo;

        private static PartitionTable SafeReadTable(IBlockDevice device)
        {
            try
            {
                return PartitionTableReader.Read(device);
            }
            catch (ImagingException)
            {
                return PartitionTable.Damaged;
            }
        }

        private sealed class Exclusive : IExclusiveDevice
        {
            private readonly FakeDeviceAccess _owner;
            private bool _disposed;

            public Exclusive(IBlockDevice device, FakeDeviceAccess owner)
            {
                Device = device;
                _owner = owner;
            }

            public IBlockDevice Device { get; }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _owner.ExclusiveReleases++;
                }
            }
        }

        private sealed class ReadOnlyHandle : IBlockDevice
        {
            private readonly IBlockDevice _inner;
            private readonly FakeDeviceAccess _owner;
            private bool _disposed;

            public ReadOnlyHandle(IBlockDevice inner, FakeDeviceAccess owner)
            {
                _inner = inner;
                _owner = owner;
            }

            public long Size => _inner.Size;

            public int SectorSize => _inner.SectorSize;

            public bool CanWrite => false;

            public int Read(long offset, byte[] buffer, int index, int count) => _inner.Read(offset, buffer, index, count);

            public void Write(long offset, byte[] buffer, int index, int count)
            {
                _owner.AnyWriteAttemptOnReadHandle = true;
                throw new InvalidOperationException("Source must be read-only.");
            }

            public void Flush()
            {
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _owner.ReadReleases++;
                }
            }
        }
    }
}

using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HddBackup.Core.Native
{
    /// <summary>
    /// Win32 declarations. Every API used here exists on Windows 7 SP1 (constitution Principle IV).
    /// </summary>
    internal static class NativeMethods
    {
        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;
        public const uint FileShareRead = 0x1;
        public const uint FileShareWrite = 0x2;
        public const uint OpenExisting = 3;
        public const uint FileFlagNoBuffering = 0x20000000;
        public const uint FileFlagWriteThrough = 0x80000000;
        public const uint FileBegin = 0;

        public const uint IoctlStorageGetDeviceNumber = 0x2D1080;
        public const uint IoctlStorageQueryProperty = 0x2D1400;
        public const uint IoctlStorageEjectMedia = 0x2D4808;
        public const uint IoctlDiskGetDriveGeometryEx = 0x700A0;
        public const uint IoctlDiskGetLengthInfo = 0x7405C;
        public const uint IoctlDiskIsWritable = 0x70024;
        public const uint IoctlDiskUpdateProperties = 0x70140;
        public const uint IoctlVolumeGetVolumeDiskExtents = 0x560000;
        public const uint FsctlLockVolume = 0x90018;
        public const uint FsctlUnlockVolume = 0x9001C;
        public const uint FsctlDismountVolume = 0x90020;

        public const int ErrorFileNotFound = 2;
        public const int ErrorWriteProtect = 19;
        public const int ErrorNotReady = 21;
        public const int ErrorHandleDiskFull = 39;
        public const int ErrorInsufficientBuffer = 122;
        public const int ErrorDiskFull = 112;
        public const int ErrorMoreData = 234;
        public const int ErrorFileTooLarge = 223;
        public const int ErrorNoSuchDevice = 433;
        public const int ErrorNoMoreItems = 259;
        public const int ErrorDeviceNotConnected = 1167;
        public const int ErrorMediaChanged = 1110;
        public const int ErrorNoMediaInDrive = 1112;

        public const uint DigcfPresent = 0x2;
        public const uint DigcfDeviceInterface = 0x10;
        public const uint MemCommit = 0x1000;
        public const uint MemReserve = 0x2000;
        public const uint MemRelease = 0x8000;
        public const uint PageReadWrite = 0x04;
        public const int CrSuccess = 0;

        public static readonly Guid GuidDevInterfaceDisk = new Guid("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, uint toRead, out uint read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteFile(SafeFileHandle file, IntPtr buffer, uint toWrite, out uint written, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long newPosition, uint moveMethod);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FlushFileBuffers(SafeFileHandle file);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inSize,
            IntPtr outBuffer, uint outSize, out uint bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern FindVolumeSafeHandle FindFirstVolume(StringBuilder volumeName, uint length);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindNextVolume(FindVolumeSafeHandle find, StringBuilder volumeName, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindVolumeClose(IntPtr find);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumePathName(string fileName, StringBuilder volumePathName, uint length);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, uint length);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumePathNamesForVolumeName(string volumeName, char[] pathNames, uint length, out uint returnLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, uint max);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetSystemWindowsDirectory(StringBuilder buffer, uint size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeAvailable, out ulong total, out ulong totalFree);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeInformation(
            string rootPath, StringBuilder volumeName, int volumeNameSize, out uint serialNumber,
            out uint maxComponentLength, out uint fileSystemFlags, StringBuilder fileSystemName, int fileSystemNameSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex,
            ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr detailData,
            uint detailDataSize, out uint requiredSize, ref SpDevinfoData deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Request_Device_Eject(
            uint devInst, out int vetoType, StringBuilder vetoName, int nameLength, uint flags);

        /// <summary>Volume serial number and file index identify a file regardless of the path used to reach it.</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

        [StructLayout(LayoutKind.Sequential)]
        public struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SpDeviceInterfaceData
        {
            public int CbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SpDevinfoData
        {
            public int CbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }
    }

    internal sealed class FindVolumeSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public FindVolumeSafeHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle() => NativeMethods.FindVolumeClose(handle);
    }
}

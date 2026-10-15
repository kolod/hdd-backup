using System.Text;
using System.Threading;
using HddBackup.Core.Native;

namespace HddBackup.Core.Devices
{
    /// <summary>
    /// "Safely Remove Hardware" for USB devices: asks Plug and Play to eject the disk's parent
    /// node (the USB mass storage device). Built-in readers only get a media eject: their parent
    /// is the PCI controller itself, which must never be ejected.
    /// </summary>
    public static class DeviceEjector
    {
        private const int Attempts = 3;
        private const int RetryDelayMs = 500;

        public static EjectResult Eject(DeviceInfo device)
        {
            string veto = null;
            if (device.BusType == BusType.Usb
                && device.DevInst != 0
                && NativeMethods.CM_Get_Parent(out uint parent, device.DevInst, 0) == NativeMethods.CrSuccess)
            {
                for (int attempt = 0; attempt < Attempts; attempt++)
                {
                    var name = new StringBuilder(260);
                    int cr = NativeMethods.CM_Request_Device_Eject(parent, out int vetoType, name, name.Capacity, 0);
                    if (cr == NativeMethods.CrSuccess && vetoType == 0)
                    {
                        return new EjectResult(EjectStatus.Ok);
                    }

                    veto = name.Length > 0 ? name.ToString() : veto;
                    Thread.Sleep(RetryDelayMs);
                }

                if (veto != null)
                {
                    return new EjectResult(EjectStatus.Vetoed, veto);
                }
            }

            using (var handle = DiskIoctl.Open(PhysicalDiskDevice.PathFor(device.DiskNumber), NativeMethods.GenericRead))
            {
                if (!handle.IsInvalid && DiskIoctl.Call(handle, NativeMethods.IoctlStorageEjectMedia, null, null, out _, out _))
                {
                    return new EjectResult(EjectStatus.Ok);
                }
            }

            return veto != null ? new EjectResult(EjectStatus.Vetoed, veto) : new EjectResult(EjectStatus.NotSupported);
        }
    }
}

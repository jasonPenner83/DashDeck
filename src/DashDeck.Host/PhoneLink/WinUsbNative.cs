using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DashDeck.Host.PhoneLink;

/// <summary>
/// The Windows calls that find a USB device and talk to it through WinUSB — Microsoft's own in-box
/// driver, bound to the dongle once with Zadig (ADR-0019, ADR-0057). Read-only use of the device's
/// registry key: the interface GUIDs WinUSB was given. Nothing is written.
/// </summary>
internal static class WinUsbNative
{
    public const int DigcfPresent = 0x02;
    public const int DigcfAllClasses = 0x04;
    public const int DigcfDeviceInterface = 0x10;
    public const int DicsFlagGlobal = 0x01;
    public const int DiregDev = 0x01;
    public const int KeyRead = 0x20019;
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x01;
    public const uint FileShareWrite = 0x02;
    public const uint OpenExisting = 3;
    public const uint FileAttributeNormal = 0x80;
    public const uint FileFlagOverlapped = 0x40000000;

    /// <summary>Every USB device registers this interface; a WinUSB-bound one can be opened through it.</summary>
    public static readonly Guid UsbDeviceInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    /// <summary>The interface WinUSB registers for a device bound by its compatible ID.</summary>
    public static readonly Guid GenericWinUsbInterface = new("DEE824EF-729B-4A0E-9C14-B7117D33A817");

    public const byte PipeTypeBulk = 2;
    public const uint ShortPacketTerminate = 0x01;

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
        public int DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct UsbInterfaceDescriptor
    {
        public byte BLength;
        public byte BDescriptorType;
        public byte BInterfaceNumber;
        public byte BAlternateSetting;
        public byte BNumEndpoints;
        public byte BInterfaceClass;
        public byte BInterfaceSubClass;
        public byte BInterfaceProtocol;
        public byte IInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WinUsbPipeInformation
    {
        public int PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW")]
    public static extern IntPtr SetupDiGetClassDevsAll(IntPtr classGuid, string enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid classGuid, int index, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, int detailSize, out int requiredSize, IntPtr devInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SpDevinfoData data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SpDevinfoData data, char[] id, int idSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref SpDevinfoData data, int scope, int hwProfile, int keyType, int samDesired);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegQueryValueEx(IntPtr key, string name, IntPtr reserved, out int type, byte[]? data, ref int size);

    [DllImport("advapi32.dll")]
    public static extern int RegCloseKey(IntPtr key);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_Initialize(SafeFileHandle device, out IntPtr handle);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_Free(IntPtr handle);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_QueryInterfaceSettings(IntPtr handle, byte alternate, out UsbInterfaceDescriptor descriptor);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_QueryPipe(IntPtr handle, byte alternate, byte index, out WinUsbPipeInformation pipe);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_ReadPipe(IntPtr handle, byte pipeId, byte[] buffer, int length, out int transferred, IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_WritePipe(IntPtr handle, byte pipeId, byte[] buffer, int length, out int transferred, IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_SetPipePolicy(IntPtr handle, byte pipeId, uint policy, int valueLength, ref byte value);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_AbortPipe(IntPtr handle, byte pipeId);
}

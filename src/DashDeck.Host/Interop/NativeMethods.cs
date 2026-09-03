using System.Runtime.InteropServices;

namespace DashDeck.Host.Interop;

/// <summary>
/// The Win32 needed to adopt another process's window.
/// </summary>
/// <remarks>
/// <b>This is a genuinely different problem from the airspace one.</b> WebView2 and LibVLC
/// render into child windows of <em>our</em> process, which is why controls cannot float over
/// them. A separate executable is another process with its own window tree, message queue and
/// focus model, and <c>SetParent</c> across that boundary inherits all three.
/// <para>
/// Kept in one file so the unmanaged surface is countable. Everything here is documented and
/// stable; none of it is undocumented behaviour. Plain DllImport rather than LibraryImport:
/// the source generator emits unsafe code, and turning AllowUnsafeBlocks on for the whole
/// shell is a large switch to throw for a dozen trivial signatures.
/// </para>
/// </remarks>
internal static class NativeMethods
{
    public const int GwlStyle = -16;
    public const int GwlExStyle = -20;

    public const long WsChild = 0x40000000L;
    public const long WsPopup = 0x80000000L;
    public const long WsCaption = 0x00C00000L;
    public const long WsThickFrame = 0x00040000L;
    public const long WsVisible = 0x10000000L;
    public const long WsExAppWindow = 0x00040000L;
    public const long WsExToolWindow = 0x00000080L;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr window);

    /// <summary>
    /// Create the container the foreign window is adopted into.
    /// </summary>
    /// <remarks>
    /// A <c>static</c> control, which is a window class Windows has already registered. Using
    /// it avoids registering a class of our own for something whose entire job is to be a
    /// rectangle with a handle.
    /// </remarks>
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string? windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr window);

    // ---- Job objects, so a launched app cannot outlive the dash ----

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    public const int JobObjectExtendedLimitInformation = 9;

    /// <summary>Kill every process in the job when the last handle to it closes.</summary>
    public const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    public struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}


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

    /// <summary>
    /// Sets a window's <b>owner</b> — which is not its parent.
    /// </summary>
    /// <remarks>
    /// The distinction is the whole design. A <em>child</em> is clipped to its parent,
    /// inherits its DPI transform and shares its input plumbing, which is where every bit of
    /// awkwardness in the re-parenting approach came from. An <em>owned</em> window stays
    /// above its owner, minimises and closes with it, and is destroyed with it — while
    /// remaining a real top-level window with its own message loop, focus and DPI.
    /// </remarks>
    public const int GwlpHwndParent = -8;

    /// <summary>Read the owner back. The authoritative check; the setter's return is the previous value.</summary>
    public const uint GwOwner = 4;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public const uint SwpFrameChanged = 0x0020;

    /// <summary>
    /// Resize without asking the window's permission.
    /// </summary>
    /// <remarks>
    /// <b>The one flag that makes hosting a minimum-size application work.</b> A window's
    /// minimum is enforced while it handles <c>WM_WINDOWPOSCHANGING</c>: it is handed the
    /// proposed rectangle and widens it back. Suppressing that message skips the negotiation
    /// entirely, so the window becomes the size it was given and lays its content out to match.
    /// Stremio insists on 1000 x 600 and accepts 900 x 500 with this set.
    /// <para>
    /// The cost is that an application which genuinely cannot lay out below some width will
    /// render badly rather than being clipped. That is the better failure of the two: it is
    /// visible, it is the application's own layout doing its best, and nothing is hidden off
    /// the edge of the screen.
    /// </para>
    /// </remarks>
    public const uint SwpNoSendChanging = 0x0400;

    public const int SwHide = 0;
    public const int SwShowNoActivate = 4;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    /// <summary>
    /// Crop a window to a region, so one that refuses to be small enough cannot spill.
    /// </summary>
    /// <remarks>
    /// Applied by the window manager rather than by the application, which is why it works on
    /// a window belonging to somebody else's process — and why it is the only lever available
    /// when an application enforces a minimum size. It hides content rather than shrinking it;
    /// that is a poor outcome, and better than covering the dash.
    /// </remarks>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr window, int command);

    /// <summary>Bring a window forward — another DashDeck's, when a second one is launched.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryInformationJobObject(
        IntPtr job,
        int infoClass,
        IntPtr info,
        uint length,
        IntPtr returnLength);

    public const int JobObjectExtendedLimitInformation = 9;

    /// <summary>
    /// Every process currently in the job.
    /// </summary>
    /// <remarks>
    /// The job was created to guarantee cleanup, and it turns out to answer a second question
    /// for free: <em>which processes are this application</em>. A launcher that starts the
    /// real program in a child — which is what jpackage does, and what defeated
    /// <c>Process.MainWindowHandle</c> — still has both under the same job.
    /// </remarks>
    public const int JobObjectBasicProcessIdList = 3;

    // ---- Enumerating windows, to find one the job owns ----

    public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr window);

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


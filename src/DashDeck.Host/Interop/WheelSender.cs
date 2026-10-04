using System.Runtime.InteropServices;

namespace DashDeck.Host.Interop;

/// <summary>
/// Turns the mouse wheel for a program on the stage, at a point over it (ADR-0046).
/// </summary>
/// <remarks>
/// <b>For programs that never learned touch.</b> NuvioDesktop — and any program that treats a
/// finger as a mouse — reads a drag as a press-and-move, so a list will not scroll under a
/// finger, in DashDeck or out of it. Every one of them scrolls for a wheel, so the scroll strip
/// beside the program turns finger travel into wheel notches and this delivers them.
/// </remarks>
internal static class WheelSender
{
    /// <summary>
    /// Post a wheel message to the program's window under <paramref name="x"/>, <paramref name="y"/>
    /// (physical screen pixels). Moves nothing and needs no focus.
    /// </summary>
    /// <remarks>
    /// The deepest window under the point, so a program made of child windows gets it where it
    /// would have gone; but only if that window is the program's — anything else at the point
    /// (a tooltip, our own window) and the message goes to the program's own top-level window.
    /// A wheel message's coordinates are the screen point, which is how the program decides
    /// which pane scrolls.
    /// </remarks>
    public static bool Post(IntPtr appWindow, IReadOnlyCollection<uint> processIds, int x, int y, int delta)
    {
        var target = NativeMethods.WindowFromPoint(new NativeMethods.PointInt { X = x, Y = y });

        if (target == IntPtr.Zero || !Belongs(target, processIds))
        {
            target = appWindow;
        }

        var wParam = (IntPtr)((delta & 0xFFFF) << 16);
        var lParam = (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));

        return NativeMethods.PostMessage(target, NativeMethods.WmMouseWheel, wParam, lParam);
    }

    /// <summary>
    /// Turn a real wheel with the pointer over <paramref name="x"/>, <paramref name="y"/>, then put
    /// the pointer back.
    /// </summary>
    /// <remarks>
    /// For a program that ignores a posted wheel because it asks where the pointer really is. The
    /// three steps go in one <c>SendInput</c> call so they reach the input queue in order, with
    /// nothing between them: move there, turn, move back.
    /// </remarks>
    public static bool Inject(int x, int y, int delta)
    {
        if (!NativeMethods.GetCursorPos(out var home))
        {
            return false;
        }

        var left = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
        var top = NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen);
        var width = Math.Max(NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen), 2);
        var height = Math.Max(NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen), 2);

        NativeMethods.MouseInputRecord MoveTo(int px, int py) => new()
        {
            Type = NativeMethods.InputMouse,
            Dx = (int)Math.Round((px - left) * 65535.0 / (width - 1)),
            Dy = (int)Math.Round((py - top) * 65535.0 / (height - 1)),
            Flags = NativeMethods.MouseEventMove | NativeMethods.MouseEventAbsolute | NativeMethods.MouseEventVirtualDesk,
        };

        var inputs = new[]
        {
            MoveTo(x, y),
            new NativeMethods.MouseInputRecord
            {
                Type = NativeMethods.InputMouse,
                MouseData = unchecked((uint)delta),
                Flags = NativeMethods.MouseEventWheel,
            },
            MoveTo(home.X, home.Y),
        };

        var sent = NativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<NativeMethods.MouseInputRecord>());

        return sent == inputs.Length;
    }

    private static bool Belongs(IntPtr window, IReadOnlyCollection<uint> processIds)
    {
        NativeMethods.GetWindowThreadProcessId(window, out var pid);
        return processIds.Contains(pid);
    }
}

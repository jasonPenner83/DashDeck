namespace DashDeck.Host.Interop;

/// <summary>
/// Finds the window an application is actually showing.
/// </summary>
/// <remarks>
/// <b>Written because <c>Process.MainWindowHandle</c> is not good enough.</b> It looks only at
/// the process handed to it, and a launcher that starts the real program in a child process
/// never reports one — which is what jpackage does, and NuvioDesktop is a jpackage build. Its
/// launcher's handle stayed zero for twenty-five seconds while a visible <c>SunAwtFrame</c>
/// sat on its child, so the occupant reported "running outside" for an application that was
/// perfectly adoptable.
/// <para>
/// Enumerating across a whole set of process ids fixes that, and the job object already knows
/// which ids those are.
/// </para>
/// </remarks>
public static class WindowFinder
{
    /// <summary>
    /// The best top-level window belonging to any of <paramref name="processIds"/>.
    /// </summary>
    /// <remarks>
    /// "Best" means visible, top-level, and titled. All three matter: splash screens and
    /// message-only windows are common, invisible ones are never what a person means, and a
    /// window with no title is almost always infrastructure rather than the application.
    /// <para>
    /// Titled ones are preferred but an untitled visible top-level window is accepted as a
    /// second choice, because refusing to adopt anything is worse than adopting the wrong
    /// thing — the occupant reports what happened either way, and the fallback stays visible.
    /// </para>
    /// </remarks>
    public static IntPtr Find(IReadOnlyCollection<uint> processIds)
    {
        if (processIds.Count == 0)
        {
            return IntPtr.Zero;
        }

        var wanted = processIds.ToHashSet();
        var titled = IntPtr.Zero;
        var untitled = IntPtr.Zero;

        NativeMethods.EnumWindows(
            (window, _) =>
            {
                if (!NativeMethods.IsWindowVisible(window)
                    || NativeMethods.GetParent(window) != IntPtr.Zero)
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(window, out var pid);

                if (!wanted.Contains(pid))
                {
                    return true;
                }

                if (NativeMethods.GetWindowTextLength(window) > 0)
                {
                    titled = window;

                    // Stop at the first titled one. Enumeration runs roughly front to back, so
                    // the first is the one most likely to be in front of the user.
                    return false;
                }

                if (untitled == IntPtr.Zero)
                {
                    untitled = window;
                }

                return true;
            },
            IntPtr.Zero);

        return titled != IntPtr.Zero ? titled : untitled;
    }
}

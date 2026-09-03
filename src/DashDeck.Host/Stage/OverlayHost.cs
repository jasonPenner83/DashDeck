using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DashDeck.Host.Interop;

namespace DashDeck.Host.Stage;

/// <summary>
/// Keeps another application's window sitting exactly over the stage.
/// </summary>
/// <remarks>
/// <b>Owned, not re-parented — and that one word is the whole difference.</b> The first
/// attempt made the application a <em>child</em> of the shell with <c>SetParent</c>, which
/// works and feels wrong: a child is clipped by its parent, inherits its DPI transform (so it
/// ignored the <c>Viewbox</c> the whole shell is drawn in), and shares its input plumbing.
/// Every awkward thing about that approach came from those three properties.
/// <para>
/// An <em>owned</em> window has none of them. It stays above its owner, minimises with it and
/// is destroyed with it, while remaining a genuine top-level window: its own message loop, its
/// own focus, its own DPI handling, native input. So the application is left alone and simply
/// <em>placed</em> — over the stage rectangle, tracked as the shell moves — and the status
/// strip, launcher, cards and navigation all stay live around it.
/// </para>
/// <para>
/// The cost is that placement is now our job rather than the layout system's, which is what
/// <see cref="Track"/> is for.
/// </para>
/// </remarks>
public sealed class OverlayHost : IDisposable
{
    /// <summary>
    /// How often placement is re-checked as a backstop.
    /// </summary>
    /// <remarks>
    /// Events cover the ordinary cases — the shell moving, resizing, or re-laying out — but a
    /// window can also be moved by things that raise none of them, and the application itself
    /// may resize on its own. Four times a second is imperceptible and costs a rectangle
    /// comparison.
    /// </remarks>
    private static readonly TimeSpan Backstop = TimeSpan.FromMilliseconds(250);

    private readonly IntPtr _window;
    private readonly FrameworkElement _anchor;
    private readonly DispatcherTimer _timer;

    private Window? _shell;
    private IntPtr _previousOwner;
    private long _originalStyle;
    private Rect _lastPlaced = Rect.Empty;

    /// <summary>The rectangle last asked for, in physical pixels. What the app was offered.</summary>
    public Rect Placed => _lastPlaced;
    private bool _lastVisible;
    private bool _disposed;

    public OverlayHost(IntPtr window, FrameworkElement anchor)
    {
        _window = window;
        _anchor = anchor;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Backstop };
        _timer.Tick += (_, _) => Track();
    }

    /// <summary>True once the window is genuinely owned by the shell.</summary>
    public bool IsOwned { get; private set; }

    /// <summary>
    /// True when the application refused to be as small as the stage and had to be cropped.
    /// </summary>
    /// <remarks>
    /// Worth surfacing rather than hiding: it means part of the application is not on screen,
    /// and the reason is a minimum size the application enforces — nothing the shell can
    /// negotiate away.
    /// </remarks>
    public bool IsClamped { get; private set; }

    /// <summary>The size the window actually took, which is its minimum when clamped.</summary>
    public Size MinimumSize { get; private set; }

    /// <summary>
    /// Take ownership and start following the anchor.
    /// </summary>
    /// <remarks>
    /// The caption and resize frame go as well. They are not needed — the window is positioned
    /// rather than dragged — and a title bar floating in the middle of a dash is exactly the
    /// sort of thing that makes an otherwise sound arrangement look like a workaround.
    /// </remarks>
    public bool Attach()
    {
        _shell = Window.GetWindow(_anchor);

        if (_shell is null || !NativeMethods.IsWindow(_window))
        {
            return false;
        }

        var owner = new WindowInteropHelper(_shell).Handle;

        if (owner == IntPtr.Zero)
        {
            return false;
        }

        _previousOwner = NativeMethods.GetWindow(_window, NativeMethods.GwOwner);
        NativeMethods.SetWindowLongPtr(_window, NativeMethods.GwlpHwndParent, owner);

        // Read it back rather than trusting the setter. Its return value is the *previous*
        // owner, so a failure and a previously-unowned window look identical from it.
        IsOwned = NativeMethods.GetWindow(_window, NativeMethods.GwOwner) == owner;

        _originalStyle = (long)NativeMethods.GetWindowLongPtr(_window, NativeMethods.GwlStyle);

        var style = _originalStyle & ~(NativeMethods.WsCaption | NativeMethods.WsThickFrame);
        NativeMethods.SetWindowLongPtr(_window, NativeMethods.GwlStyle, (IntPtr)style);

        _shell.LocationChanged += OnShellChanged;
        _shell.SizeChanged += OnShellChanged;
        _shell.StateChanged += OnShellChanged;
        _anchor.LayoutUpdated += OnShellChanged;

        _timer.Start();
        Track();

        return IsOwned;
    }

    /// <summary>
    /// Put the window where the anchor is, and hide it when the anchor is not showing.
    /// </summary>
    /// <remarks>
    /// The hiding matters more than it sounds. A re-parented window disappeared for free when
    /// its host did; an owned one is top-level and will happily float over Settings or the card
    /// editor unless it is told not to. Anything the shell puts full-screen over the stage
    /// collapses the anchor to nothing, which is the signal used here.
    /// </remarks>
    public void Track()
    {
        if (_disposed || !NativeMethods.IsWindow(_window) || _anchor.ActualWidth <= 0)
        {
            return;
        }

        var visible = _anchor.IsVisible && _shell is { WindowState: not WindowState.Minimized };

        if (!visible)
        {
            if (_lastVisible)
            {
                NativeMethods.ShowWindow(_window, NativeMethods.SwHide);
                _lastVisible = false;
            }

            return;
        }

        Rect rect;

        try
        {
            // PointToScreen gives device pixels and accounts for the Viewbox scale and the
            // display's DPI on the way — which is precisely what a re-parented child could
            // never do, because it was transformed rather than placed.
            var topLeft = _anchor.PointToScreen(new Point(0, 0));
            var bottomRight = _anchor.PointToScreen(new Point(_anchor.ActualWidth, _anchor.ActualHeight));

            rect = new Rect(topLeft, bottomRight);
        }
        catch (InvalidOperationException)
        {
            // Not connected to a presentation source yet.
            return;
        }

        if (rect == _lastPlaced && _lastVisible)
        {
            return;
        }

        _lastPlaced = rect;
        _lastVisible = true;

        var width = (int)Math.Max(rect.Width, 1);
        var height = (int)Math.Max(rect.Height, 1);

        NativeMethods.SetWindowPos(
            _window,
            IntPtr.Zero,
            (int)rect.X,
            (int)rect.Y,
            width,
            height,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate
                | NativeMethods.SwpShowWindow | NativeMethods.SwpFrameChanged
                | NativeMethods.SwpNoSendChanging);

        Confine(width, height);
    }

    /// <summary>
    /// Check the window actually took the size it was given, and crop it if not.
    /// </summary>
    /// <remarks>
    /// <b>Asking is not the same as getting.</b> An application may enforce a minimum size —
    /// Stremio's is 1000 × 600 — and unhelped it snaps back to that however small a rectangle
    /// it is handed, spilling the surplus over the cards and the navigation strip.
    /// <see cref="NativeMethods.SwpNoSendChanging"/> now stops almost all of that at source, by
    /// skipping the message the minimum is enforced during.
    /// <para>
    /// This stays as the backstop, because that flag is not a guarantee: an application is free
    /// to resize <em>itself</em> afterwards from its own message loop, and some do.
    /// </para>
    /// <para>
    /// Cropping hides content, which is a poor outcome and a better one than an occupant
    /// covering the dash. The size it wanted is recorded so the occupant can say what
    /// happened rather than leaving a mysteriously clipped application on screen.
    /// </para>
    /// </remarks>
    private void Confine(int width, int height)
    {
        if (!NativeMethods.GetWindowRect(_window, out var actual))
        {
            return;
        }

        MinimumSize = new Size(actual.Width, actual.Height);
        IsClamped = actual.Width > width || actual.Height > height;

        // The region is in window coordinates, so it starts at zero whatever the window's
        // position on screen. Cropping unconditionally keeps the one code path: a window that
        // took the size asked for is cropped to exactly itself, which is a no-op.
        var region = NativeMethods.CreateRectRgn(0, 0, width, height);

        // SetWindowRgn takes ownership of the region on success, so it must not be deleted
        // afterwards — a delete here would free a handle the window manager still holds.
        if (NativeMethods.SetWindowRgn(_window, region, true) == 0)
        {
            NativeMethods.DeleteObject(region);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();

        if (_shell is not null)
        {
            _shell.LocationChanged -= OnShellChanged;
            _shell.SizeChanged -= OnShellChanged;
            _shell.StateChanged -= OnShellChanged;
            _anchor.LayoutUpdated -= OnShellChanged;
        }

        // Given back the way it was found. The occupant kills the process anyway, but an
        // application that survives — because the job could not be created, say — should not
        // be left owned by a window that no longer exists and stripped of its title bar.
        if (NativeMethods.IsWindow(_window))
        {
            // The crop goes first: a window handed back still wearing a region would render
            // as a fragment of itself in its own frame, which looks like we broke it.
            NativeMethods.SetWindowRgn(_window, IntPtr.Zero, true);
            NativeMethods.SetWindowLongPtr(_window, NativeMethods.GwlStyle, (IntPtr)_originalStyle);
            NativeMethods.SetWindowLongPtr(_window, NativeMethods.GwlpHwndParent, _previousOwner);
        }
    }

    private void OnShellChanged(object? sender, EventArgs e) => Track();
}

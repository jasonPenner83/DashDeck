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

        NativeMethods.SetWindowPos(
            _window,
            IntPtr.Zero,
            (int)rect.X,
            (int)rect.Y,
            (int)Math.Max(rect.Width, 1),
            (int)Math.Max(rect.Height, 1),
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate
                | NativeMethods.SwpShowWindow | NativeMethods.SwpFrameChanged);
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
            NativeMethods.SetWindowLongPtr(_window, NativeMethods.GwlStyle, (IntPtr)_originalStyle);
            NativeMethods.SetWindowLongPtr(_window, NativeMethods.GwlpHwndParent, _previousOwner);
        }
    }

    private void OnShellChanged(object? sender, EventArgs e) => Track();
}

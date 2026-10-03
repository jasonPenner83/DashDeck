using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DashDeck.Host.Interop;

namespace DashDeck.Host.Stage;

/// <summary>Where an adopted application has got to.</summary>
public enum HostedAppState
{
    /// <summary>The executable is not on this machine.</summary>
    NotInstalled,

    /// <summary>Started, waiting for a window to appear.</summary>
    Starting,

    /// <summary>Adopted into the stage.</summary>
    Hosted,

    /// <summary>Running, but in its own window — adoption did not take.</summary>
    Outside,

    /// <summary>It would not start, or it exited.</summary>
    Failed,
}

/// <summary>
/// A native Windows application, adopted into the stage where that works.
/// </summary>
/// <remarks>
/// <b>The fallback is the design, not an apology.</b> Whether a given executable survives
/// being re-parented is a property of that executable — its window count, its focus handling,
/// whether it spawns dialogs as new top-level windows — and cannot be known from here. So this
/// tries, checks whether it worked, and says which of the two happened. An occupant that
/// silently showed a black rectangle when adoption failed would be indistinguishable from one
/// that was broken.
/// <para>
/// Everything launched goes into a <see cref="ProcessJob"/> first. Without it a hosted app
/// outlives the dash and keeps making noise with no window to close — which is the failure the
/// web occupant already warns about, with no upper bound.
/// </para>
/// </remarks>
public sealed class AppStageOccupant : IStageOccupant
{
    /// <summary>How long to wait for a window before giving up on adoption.</summary>
    /// <remarks>
    /// A JVM application can take several seconds to show anything — NuvioDesktop showed its
    /// first window at about six on a warm start, and a cold one is slower. Twenty is generous
    /// enough not to fail that and short enough that a genuinely broken launch says so.
    /// </remarks>
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(20);

    private readonly AppLaunchSpec _spec;
    private readonly ProcessJob _job = new();
    private readonly Grid _root = new();
    private readonly Grid _area = new();
    private readonly TextBlock _message = new();
    private readonly DispatcherTimer _watch;

    private Process? _process;
    private OverlayHost? _host;
    private IntPtr _window;
    private ScrollStripView? _strip;
    private int _wheelsRefused;
    private DateTimeOffset _startedAt;
    private bool _disposed;

    public AppStageOccupant(AppLaunchSpec spec)
    {
        _spec = spec;

        _watch = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };

        _watch.Tick += (_, _) => Poll();
    }

    /// <inheritdoc />
    public string Name => _spec.Name;

    /// <summary>Where it has got to. Rendered, never guessed at.</summary>
    public HostedAppState State { get; private set; } = HostedAppState.Starting;

    /// <inheritdoc />
    public FrameworkElement CreateView()
    {
        _message.HorizontalAlignment = HorizontalAlignment.Center;
        _message.VerticalAlignment = VerticalAlignment.Center;
        _message.TextAlignment = TextAlignment.Center;
        _message.TextWrapping = TextWrapping.Wrap;
        _message.MaxWidth = 640;
        _message.FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("MonoFont");
        _message.FontSize = 15;
        _message.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextMidBrush");

        // The program is placed over the area, not the whole stage, so the strip beside it is
        // never under the program (ADR-0046).
        _area.Children.Add(_message);
        _root.Children.Add(_area);

        if (_spec.Scroll.Side != ScrollStripSide.Off)
        {
            _strip = new ScrollStripView(_spec.Scroll.Side) { Visibility = Visibility.Collapsed };
            _strip.Scrolled += OnScrolled;

            var stripColumn = new ColumnDefinition { Width = GridLength.Auto };
            var areaColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };

            if (_spec.Scroll.Side == ScrollStripSide.Left)
            {
                _root.ColumnDefinitions.Add(stripColumn);
                _root.ColumnDefinitions.Add(areaColumn);
                Grid.SetColumn(_strip, 0);
                Grid.SetColumn(_area, 1);
            }
            else
            {
                _root.ColumnDefinitions.Add(areaColumn);
                _root.ColumnDefinitions.Add(stripColumn);
                Grid.SetColumn(_area, 0);
                Grid.SetColumn(_strip, 1);
            }

            _root.Children.Add(_strip);
        }

        Start();
        return _root;
    }

    /// <inheritdoc />
    public IReadOnlyList<StageAction> Actions => [new StageAction("RESTART APP", Restart)];

    /// <inheritdoc />
    public string Describe() =>
        $"app={_spec.Name} state={State} path={_spec.Resolve() ?? "(not found)"} " +
        $"pid={_process?.Id.ToString() ?? "-"} job={_job.IsUsable} " +
        $"clamped={_host?.IsClamped == true} " +
        $"offered={_host?.Placed.Width:0}x{_host?.Placed.Height:0} " +
        $"took={_host?.MinimumSize.Width:0}x{_host?.MinimumSize.Height:0} " +
        $"strip={_spec.Scroll.Side.ToString().ToLowerInvariant()}/{_spec.Scroll.Delivery.ToString().ToLowerInvariant()} " +
        $"notches={_strip?.NotchesRaised ?? 0} refused={_wheelsRefused}";

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watch.Stop();

        // Hand the window back before anything is torn down, so the app is not destroyed
        // along with the container it was borrowed into.
        _host?.Dispose();
        _host = null;

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone, or never ours to kill. The job object is the backstop.
        }

        _process?.Dispose();
        _process = null;

        // Closing the job kills anything still in it. This is the line that stops a hosted
        // app outliving the dash.
        _job.Dispose();
    }

    private void Restart()
    {
        _watch.Stop();
        _host?.Dispose();
        _host = null;
        _window = IntPtr.Zero;
        _area.Children.Clear();
        _area.Children.Add(_message);

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Nothing to kill.
        }

        _process = null;
        Start();
    }

    private void Start()
    {
        if (_spec.Resolve() is not { } path)
        {
            Move(HostedAppState.NotInstalled);
            return;
        }

        try
        {
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = _spec.Arguments,
                UseShellExecute = false,
            });
        }
        catch (Exception)
        {
            Move(HostedAppState.Failed);
            return;
        }

        if (_process is null)
        {
            Move(HostedAppState.Failed);
            return;
        }

        _job.Adopt(_process);
        _startedAt = DateTimeOffset.UtcNow;

        Move(HostedAppState.Starting);
        _watch.Start();
    }

    /// <summary>
    /// Wait for a window, then try to take it.
    /// </summary>
    /// <remarks>
    /// Polled rather than hooked. A window-creation hook would be more elegant and would mean
    /// injecting into another process, which is a large amount of machinery and privilege for
    /// something that has to happen exactly once, seconds after a launch.
    /// </remarks>
    private void Poll()
    {
        if (_disposed || _process is null)
        {
            return;
        }

        // A launcher exiting is not a failure if it left children behind — that is precisely
        // what a jpackage stub does. The job is the authority on whether anything is still
        // running, because the launcher's own handle stops being informative the moment it
        // hands off.
        if (_process.HasExited && _job.ProcessIds().Count == 0)
        {
            _watch.Stop();
            Move(HostedAppState.Failed);
            return;
        }

        // Across every process in the job, not just the one we started. A launcher that starts
        // the real program in a child never reports a main window of its own, and jpackage
        // applications — NuvioDesktop among them — are built exactly that way.
        var window = WindowFinder.Find(_job.ProcessIds());

        if (window == IntPtr.Zero)
        {
            if (DateTimeOffset.UtcNow - _startedAt > WindowTimeout)
            {
                // It is running and we never saw a window to take. Left alone rather than
                // killed: it may well be working in its own window, which is worth more than
                // an empty stage.
                _watch.Stop();
                Move(HostedAppState.Outside);
            }

            return;
        }

        _watch.Stop();
        AdoptInto(window);
    }

    /// <summary>
    /// Place the application over the stage rather than inside it.
    /// </summary>
    /// <remarks>
    /// The window is left as a real top-level window and merely <em>owned</em> by the shell,
    /// so it keeps its own message loop, focus and DPI handling and gains none of the clipping
    /// that made re-parenting feel like a workaround. See <see cref="OverlayHost"/>.
    /// </remarks>
    private void AdoptInto(IntPtr window)
    {
        try
        {
            _window = window;
            _host = new OverlayHost(window, _area);

            // Read back rather than assumed. Ownership can be refused, and an application that
            // destroys and recreates its window leaves a handle that is no longer one — either
            // way the honest answer is that it is running outside the stage.
            Move(_host.Attach() && NativeMethods.IsWindow(window)
                ? HostedAppState.Hosted
                : HostedAppState.Outside);
        }
        catch (Exception)
        {
            Move(HostedAppState.Outside);
        }
    }

    private void Move(HostedAppState state)
    {
        State = state;

        _message.Text = state switch
        {
            HostedAppState.NotInstalled =>
                $"{_spec.Name} is not installed on this tablet.\nDashDeck launches applications; it does not ship them.",

            HostedAppState.Starting => $"Starting {_spec.Name}…",

            // Hosted and cropped is still hosted — the application is there and working, with
            // part of it off the edge. Saying so beats leaving a mysteriously clipped window.
            HostedAppState.Hosted when _host is { IsClamped: true } c =>
                $"{_spec.Name} will not go narrower than {c.MinimumSize.Width:0} x {c.MinimumSize.Height:0}.\n" +
                "It is cropped to the stage so it cannot cover the dash. Some of it is off the edge.",

            HostedAppState.Hosted => string.Empty,

            HostedAppState.Outside =>
                $"{_spec.Name} is running in its own window.\nIt would not embed in the stage — some applications will not.\nIt still closes with DashDeck.",

            _ => $"{_spec.Name} would not start, or exited immediately.\n" +
                 "Store-packaged applications do exactly this: the executable is a stub that\n" +
                 "starts the real app in another process and exits. Those cannot be adopted.",
        };

        // The message would otherwise sit on top of the adopted window, which on a child
        // window means invisibly — and on the day adoption half-works, confusingly.
        // A cropped window still needs its explanation visible, and it sits in the part of the
        // stage the application was not allowed to fill.
        _message.Visibility = state is HostedAppState.Hosted && _host is not { IsClamped: true }
            ? Visibility.Collapsed
            : Visibility.Visible;

        // Only beside a program that is there to scroll. Running outside, the program is
        // somewhere else entirely, and a strip beside an empty stage would scroll nothing.
        if (_strip is not null)
        {
            _strip.Visibility = state is HostedAppState.Hosted ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Send the strip's wheel to the program, at the finger's height just inside its edge.
    /// </summary>
    /// <remarks>
    /// At the finger's height so a person chooses which pane scrolls by where they drag — the
    /// list beside the finger, as with a wheel the pane under the pointer. Just inside the edge
    /// because that is where a scroll bar is, and a scroll bar belongs to the pane that scrolls.
    /// </remarks>
    private void OnScrolled(int delta, double y)
    {
        if (_strip is null || _window == IntPtr.Zero || State != HostedAppState.Hosted || !NativeMethods.IsWindow(_window))
        {
            return;
        }

        const double Inside = 12;
        var x = _spec.Scroll.Side == ScrollStripSide.Left ? _strip.ActualWidth + Inside : -Inside;

        Point screen;

        try
        {
            screen = _strip.PointToScreen(new Point(x, y));
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var px = (int)Math.Round(screen.X);
        var py = (int)Math.Round(screen.Y);

        var sent = _spec.Scroll.Delivery == ScrollDelivery.Input
            ? WheelSender.Inject(px, py, delta)
            : WheelSender.Post(_window, _job.ProcessIds(), px, py, delta);

        if (!sent)
        {
            _wheelsRefused++;
        }
    }
}

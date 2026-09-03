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
    /// A JVM application can take several seconds to show anything. Ten is generous enough not
    /// to fail a slow cold start and short enough that a genuinely broken launch says so.
    /// </remarks>
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(10);

    private readonly AppLaunchSpec _spec;
    private readonly ProcessJob _job = new();
    private readonly Grid _root = new();
    private readonly TextBlock _message = new();
    private readonly DispatcherTimer _watch;

    private Process? _process;
    private HostedWindow? _host;
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

        _root.Children.Add(_message);

        Start();
        return _root;
    }

    /// <inheritdoc />
    public FrameworkElement? CreateActionBar() => ActionBar.Row(
        ActionBar.Button("RESTART", Restart, 180),
        ActionBar.Caption(_spec.Detail));

    /// <inheritdoc />
    public string Describe() =>
        $"app={_spec.Name} state={State} path={_spec.Resolve() ?? "(not found)"} " +
        $"pid={_process?.Id.ToString() ?? "-"} job={_job.IsUsable}";

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
        _root.Children.Clear();
        _root.Children.Add(_message);

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

        if (_process.HasExited)
        {
            _watch.Stop();
            Move(HostedAppState.Failed);
            return;
        }

        _process.Refresh();
        var window = _process.MainWindowHandle;

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

    private void AdoptInto(IntPtr window)
    {
        try
        {
            _host = new HostedWindow(window);
            _root.Children.Insert(0, _host);

            // Checked rather than assumed. An app that refuses adoption — or destroys and
            // recreates its window on being re-parented — leaves a handle that is no longer a
            // window, and reporting "hosted" for that would be the black-rectangle failure.
            Move(NativeMethods.IsWindow(window) ? HostedAppState.Hosted : HostedAppState.Outside);
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

            HostedAppState.Hosted => string.Empty,

            HostedAppState.Outside =>
                $"{_spec.Name} is running in its own window.\nIt would not embed in the stage — some applications will not.\nIt still closes with DashDeck.",

            _ => $"{_spec.Name} would not start, or exited immediately.\n" +
                 "Store-packaged applications do exactly this: the executable is a stub that\n" +
                 "starts the real app in another process and exits. Those cannot be adopted.",
        };

        // The message would otherwise sit on top of the adopted window, which on a child
        // window means invisibly — and on the day adoption half-works, confusingly.
        _message.Visibility = state is HostedAppState.Hosted
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}

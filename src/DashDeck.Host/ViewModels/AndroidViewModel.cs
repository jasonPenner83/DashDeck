using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Core.Emulation;
using DashDeck.Core.Location;
using DashDeck.Emulator;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// What the dash hands Android besides its picture and touch (ADR-0075 addendum): the truck's
/// signals for Automotive's vehicle HAL, whether it is night, the tank's size, and the microphone —
/// and how the emulator is to draw.
/// </summary>
/// <param name="Signals">The truck's signals; null when there are none.</param>
/// <param name="IsNight">The dash's own answer — the headlights, or the sun.</param>
/// <param name="TankLitres">The tank's size from the vehicle settings.</param>
/// <param name="Microphone">Let Android hear the tablet's microphone.</param>
/// <param name="Gpu">The emulator's <c>-gpu</c> mode; null for its own choice.</param>
public sealed record AndroidFeeds(IVehicleSignals? Signals, Func<bool> IsNight, Func<double> TankLitres, bool Microphone, string? Gpu = null)
{
    /// <summary>Nothing fed but the position: no signals, always day, no tank, no microphone.</summary>
    public static readonly AndroidFeeds None = new(null, () => false, () => 0, false);
}

/// <summary>One line of the ANDROID cell's hold menu.</summary>
/// <param name="Detail">The AVD it would run, or why there is none.</param>
/// <param name="IsCurrent">The flavour the cell opens now.</param>
/// <param name="IsAvailable">False when there is no AVD of this flavour to run.</param>
public sealed record AndroidMenuItem(AndroidFlavour Flavour, string Label, string Detail, bool IsCurrent, bool IsAvailable);

/// <summary>
/// Android on the dash (ADR-0075): the emulator behind the ANDROID nav cell — full screen or on the
/// stage — its picture, touch, the truck's position, and which flavour.
/// </summary>
/// <remarks>
/// <b>Started on first sight, kept after.</b> The emulator starts the first time a view of it is on
/// screen, and keeps running when that view goes, so coming back is instant (it boots from its
/// snapshot in a second or two, but a running one is there at once). Frames are only asked for while
/// a view is showing, so a hidden Android costs memory and almost no processor. The hold menu's
/// STOP gives the memory back.
/// <para>
/// <b>Frames at the size they are drawn.</b> The panel says its size in screen pixels and frames are
/// asked for at that, so full screen is not a stage-sized picture stretched, nor the stage a
/// full-screen one shrunk. And a regular tablet is <b>turned</b> to the region's shape — upright full
/// screen, on its side on the stage — as Android Studio's rotate button does (<see cref="AndroidChoice.TurnFor"/>).
/// Automotive is never turned.
/// </para>
/// <para>
/// <b>Android the region's own shape.</b> The emulator starts at the stage's size, so on the stage it
/// fills it. For any other shape Android is told the region's (<see cref="AndroidFit"/>, through ADB)
/// and draws that in the middle of its screen; the frames are cropped to it, so no bars are left. Its
/// density keeps one dp the size of one of the dash's units in either placement.
/// </para>
/// <para>
/// <b>Frames are dropped, never queued.</b> A frame arrives on a gRPC thread; if the UI has not drawn
/// the last one yet, the new one is skipped rather than piled up behind it. A picture that falls
/// behind is worse than one that skips.
/// </para>
/// </remarks>
public sealed partial class AndroidViewModel : ObservableObject, IDisposable
{
    /// <summary>Frames are asked for at the stage's size in screen pixels until a panel says its own.</summary>
    private const int StageWidth = 1824;
    private const int StageHeight = 1272;

    private readonly ILocationSource? _location;
    private readonly IClock _clock;
    private readonly AndroidFeeds _feeds;
    private readonly List<ISignalSubscription> _required = [];
    private readonly Action<AndroidFlavour> _remember;
    private readonly Dispatcher _dispatcher;
    private EmulatorSession? _session;
    private CancellationTokenSource? _frames;
    private int _shown;
    private byte[] _back = [];
    private int _drawing;
    private DateTimeOffset _lastFix;
    private (int Width, int Height) _region = (StageWidth, StageHeight);
    private (int Width, int Height) _streaming;
    private int? _turned;
    private double _dpiScale = 2;
    private bool? _microphoneOn;
    private AndroidFit? _fitted;
    private DateTimeOffset _fitAgainAt = DateTimeOffset.MaxValue;

    /// <param name="location">The truck's GPS, handed to Android as its own; null when there is none.</param>
    /// <param name="flavour">Which Android the cell opens, as saved.</param>
    /// <param name="remember">Save a newly chosen flavour.</param>
    /// <param name="clock">For when to try a failed fit again.</param>
    /// <param name="feeds">What else Android is handed; <see cref="AndroidFeeds.None"/> for nothing.</param>
    public AndroidViewModel(ILocationSource? location, AndroidFlavour flavour, Action<AndroidFlavour> remember, IClock clock, AndroidFeeds feeds)
    {
        _location = location;
        _clock = clock;
        _feeds = feeds;
        _flavour = flavour;
        _remember = remember;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _headline = AndroidChoice.Label(flavour);
    }

    /// <summary>Raised when the placement changes, for the shell to move it.</summary>
    public event Action<AndroidView>? PlacementChanged;

    /// <summary>Raised after a flavour is chosen from the menu, so the shell can show it.</summary>
    public event Action? FlavourChosen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFull))]
    [NotifyPropertyChangedFor(nameof(IsOnStage))]
    private AndroidView _view = AndroidView.Default;

    public bool IsFull => View.IsFull;

    public bool IsOnStage => View.IsOnStage;

    [ObservableProperty]
    private AndroidFlavour _flavour;

    /// <summary>The emulated screen, or null before the first frame.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPictureShowing))]
    private WriteableBitmap? _picture;

    /// <summary>What is happening, while there is no picture to show.</summary>
    [ObservableProperty]
    private string _headline;

    [ObservableProperty]
    private string _detail = "Starting the emulator…";

    [ObservableProperty]
    private bool _isMenuOpen;

    [ObservableProperty]
    private IReadOnlyList<AndroidMenuItem> _menuItems = [];

    /// <summary>True once Android is drawing, so the panel shows the picture rather than words.</summary>
    public bool IsPictureShowing => Picture is not null && _session?.State == EmulatorState.Running;

    /// <summary>A tap on the cell: full screen, and started if it is not.</summary>
    public void Tap()
    {
        Move(View.Tapped());

        if (_shown > 0)
        {
            EnsureStarted();
        }
    }

    /// <summary>A double tap: onto the stage.</summary>
    public void DoubleTap() => Move(View.DoubleTapped());

    /// <summary>Taken off the stage by something else being put there: back to full screen, quietly.</summary>
    public void LeftTheStage() => Move(View.Tapped());

    /// <summary>A hold: the menu of flavours, read fresh, so an AVD made a minute ago is in it.</summary>
    public void OpenMenu()
    {
        var avds = ListAvds();
        MenuItems =
        [
            .. new[] { AndroidFlavour.Regular, AndroidFlavour.Automotive }.Select(f =>
            {
                var avd = AndroidChoice.Pick(avds, f);
                var detail = AndroidChoice.Describe(avd, f);
                if (f == Flavour && _session is { State: EmulatorState.Running } running && FeedStatus(running) is { } status)
                {
                    detail += " · " + status;
                }

                return new AndroidMenuItem(f, AndroidChoice.Label(f), detail, f == Flavour, avd is not null);
            }),
        ];
        IsMenuOpen = true;
    }

    [RelayCommand]
    private void CloseMenu() => IsMenuOpen = false;

    /// <summary>Choose a flavour. A different one stops the running Android and starts the other.</summary>
    [RelayCommand]
    private void Choose(AndroidMenuItem? item)
    {
        IsMenuOpen = false;

        if (item is null || !item.IsAvailable)
        {
            return;
        }

        if (item.Flavour != Flavour)
        {
            Flavour = item.Flavour;
            _remember(item.Flavour);
            _ = StopAsync(restart: true);
        }

        FlavourChosen?.Invoke();
    }

    /// <summary>Shut Android down and give its memory back. The next tap starts it again.</summary>
    [RelayCommand]
    private void Stop()
    {
        IsMenuOpen = false;
        _ = StopAsync(restart: false);
    }

    /// <summary>A view of Android has come on screen: start it if needed, and start drawing.</summary>
    public void Attach()
    {
        if (_shown++ == 0)
        {
            EnsureStarted();
            StartFrames();
        }
    }

    /// <summary>A view has gone. When none is left, stop asking for frames; Android keeps running.</summary>
    public void Detach()
    {
        if (_shown > 0 && --_shown == 0)
        {
            StopFrames();
        }
    }

    /// <summary>
    /// The showing panel's size in screen pixels. A new size turns the tablet if its shape changed,
    /// and asks for frames at the new size.
    /// </summary>
    public void Resize(int width, int height, double dpiScale)
    {
        if (width <= 0 || height <= 0 || ((width, height) == _region && dpiScale == _dpiScale))
        {
            return;
        }

        _region = (width, height);
        _dpiScale = dpiScale > 0 ? dpiScale : _dpiScale;
        Shape();

        if (_frames is not null && _streaming != _region)
        {
            StopFrames();
            StartFrames();
        }
    }

    /// <summary>A finger on the picture, as fractions of it as drawn.</summary>
    public void Touch(double x, double y, bool down) => _ = _session?.TouchAsync(x, y, down);

    /// <summary>
    /// The shell's beat: hand Android the truck's newest position, and pick the frames back up if the
    /// stream ended while a view is showing.
    /// </summary>
    public void Beat()
    {
        if (_session is not { State: EmulatorState.Running } session)
        {
            return;
        }

        if (_location?.Latest is { HasFix: true } fix && fix.TakenUtc != _lastFix)
        {
            _lastFix = fix.TakenUtc;
            _ = session.SetPositionAsync(fix);
        }

        Feed(session);

        if (_shown > 0 && _frames is null)
        {
            StartFrames();
        }

        // A fit ADB could not make is tried again now and then, not every beat.
        if (_clock.UtcNow >= _fitAgainAt)
        {
            _fitAgainAt = DateTimeOffset.MaxValue;
            _fitted = null;
            Shape();
        }
    }

    /// <summary>
    /// Tell Android about the truck (<see cref="CarFeed"/>): speed, gear, parking brake, fuel and
    /// engine speed to Automotive, so its driving restrictions follow the truck; night to both.
    /// Only what changed goes, and only once a beat.
    /// </summary>
    private void Feed(EmulatorSession session)
    {
        if (session.Avd.Flavour == AndroidFlavour.Automotive)
        {
            Require();
        }

        Func<string, SignalValue> read = _feeds.Signals is { } signals ? signals.Current : id => SignalValue.Missing(id);
        _ = session.FeedAsync(CarFeed.From(read, _location?.Latest, _feeds.IsNight(), _feeds.TankLitres()));
    }

    /// <summary>
    /// Ask for the signals the feed reads while Automotive runs, so they are polled even when no
    /// gauge shows them. Only those this truck's catalog has.
    /// </summary>
    private void Require()
    {
        if (_required.Count > 0 || _feeds.Signals is not { } signals)
        {
            return;
        }

        foreach (var id in CarFeed.Signals.Where(signals.KnownSignals.Contains))
        {
            _required.Add(signals.Require(id, SignalPriority.Normal, id == CarFeed.Speed ? 2 : id == CarFeed.FuelLevel ? 0.1 : 1));
        }
    }

    private void ReleaseRequired()
    {
        foreach (var required in _required)
        {
            required.Dispose();
        }

        _required.Clear();
    }

    /// <summary>What the hold menu says about the feed, for the flavour running now.</summary>
    private string? FeedStatus(EmulatorSession session)
    {
        var parts = new List<string>();

        if (session.Avd.Flavour == AndroidFlavour.Automotive)
        {
            parts.Add(session.CarProblem is { } problem ? $"car data refused ({problem})"
                : session.CarTold is { SpeedKmh: { } speed, Gear: var gear } ? string.Create(CultureInfo.InvariantCulture, $"car data live · {speed:0} km/h{(gear is { } g ? $" · {g.ToString().ToUpperInvariant()}" : "")}")
                : "no car data yet");
        }
        else if (session.CarProblem is { } problem)
        {
            parts.Add($"night mode refused ({problem})");
        }

        if (_microphoneOn is { } on)
        {
            parts.Add(on ? "mic on" : "mic off");
        }

        if (session.Renderer is { } renderer)
        {
            parts.Add($"drawn by {renderer}");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>One line for <c>--shot</c>.</summary>
    public string Describe() =>
        $"android {View.Placement.ToString().ToLowerInvariant()} · {(_session?.Describe() ?? $"{AndroidChoice.Label(Flavour).ToLowerInvariant()} · not started")}" +
        (Picture is { } p ? $" · picture {p.PixelWidth}×{p.PixelHeight}" : "") +
        (_turned is { } turned ? $" · turned {turned}°" : "") +
        (_session?.Fit is { } fit ? $" · fitted {fit.Describe()}" : "") +
        (_session?.FitProblem is { } problem ? $" · not fitted ({problem})" : "") +
        (_session is { } s && FeedStatus(s) is { } feed ? $" · {feed}" : "");

    private void Move(AndroidView next)
    {
        if (next == View)
        {
            return;
        }

        View = next;
        PlacementChanged?.Invoke(next);
    }

    private void EnsureStarted()
    {
        if (_session is { State: not (EmulatorState.Failed or EmulatorState.Stopped) })
        {
            return;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var sdk = AndroidSdk.Find(AndroidSdk.Candidates(null, Environment.GetEnvironmentVariable, localAppData), File.Exists);

        if (sdk is null)
        {
            Say("NO ANDROID EMULATOR", "Install Android Studio and its Android Emulator, then tap ANDROID again (ADR-0075).");
            return;
        }

        if (AndroidChoice.Pick(ListAvds(), Flavour) is not { } avd)
        {
            Say($"NO {AndroidChoice.Label(Flavour)} DEVICE",
                $"Make an x86_64 {(Flavour == AndroidFlavour.Automotive ? "Automotive" : "phone or tablet")} virtual device in Android Studio's Device Manager — or hold ANDROID to choose the other.");
            return;
        }

        // Started at the stage's own size, so on the stage nothing needs fitting, and full screen is
        // a fit inside it. An AVD started at another size is fitted just the same, only less sharply.
        var session = new EmulatorSession(sdk, avd, skin: $"{StageWidth}x{StageHeight}") { Gpu = _feeds.Gpu };
        _turned = null;
        _fitted = null;
        session.Changed += () => _dispatcher.BeginInvoke(() => OnSessionChanged(session));
        _session = session;
        OnSessionChanged(session);
        _ = session.StartAsync();
    }

    private void OnSessionChanged(EmulatorSession session)
    {
        if (!ReferenceEquals(session, _session))
        {
            return;
        }

        var label = AndroidChoice.Label(session.Avd.Flavour);
        switch (session.State)
        {
            case EmulatorState.Starting or EmulatorState.Booting:
                Say($"STARTING {label}", session.Detail);
                break;
            case EmulatorState.Running:
                Say(label, session.Detail);
                Shape();
                _ = HearAsync(session);
                if (_shown > 0)
                {
                    StartFrames();
                }

                break;
            case EmulatorState.Failed:
                Say($"{label} DID NOT START", session.Detail);
                break;
            case EmulatorState.Stopping:
                Say($"STOPPING {label}", session.Detail);
                break;
        }

        OnPropertyChanged(nameof(IsPictureShowing));
    }

    private void StartFrames()
    {
        if (_frames is not null || _session is not { State: EmulatorState.Running } session)
        {
            return;
        }

        var frames = new CancellationTokenSource();
        var (width, height) = _streaming = _region;
        _frames = frames;

        _ = Task.Run(async () =>
        {
            try
            {
                await session.StreamAsync(width, height, OnFrame, frames.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is Grpc.Core.RpcException or OperationCanceledException or InvalidOperationException)
            {
            }

            // Ended by itself — the emulator restarted, or the stream dropped: let the beat start it again.
            await _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_frames, frames))
                {
                    _frames = null;
                }

                frames.Dispose();
            });
        });
    }

    /// <summary>
    /// Shape Android to the region it is drawn in, once per change: turn a regular tablet to the
    /// region's way round — the frames then come turned and say so, and touch follows them — and
    /// tell Android the region's shape, so the picture fills it.
    /// </summary>
    private void Shape()
    {
        if (_session is not { State: EmulatorState.Running } session)
        {
            return;
        }

        if (AndroidChoice.TurnFor(session.Avd.Flavour, session.DeviceWidth, session.DeviceHeight, _region.Width, _region.Height) is { } degrees
            && degrees != _turned)
        {
            _turned = degrees;
            _ = session.RotateAsync(degrees);
        }

        if (AndroidFit.For(session.DeviceWidth, session.DeviceHeight, _turned == 90, _region.Width, _region.Height, _dpiScale) is { } fit
            && fit != _fitted)
        {
            _fitted = fit;
            _ = FitAsync(session, fit);
        }
    }

    private async Task HearAsync(EmulatorSession session)
    {
        var done = await session.SetMicrophoneAsync(_feeds.Microphone).ConfigureAwait(true);
        if (ReferenceEquals(session, _session))
        {
            _microphoneOn = done ? _feeds.Microphone : null;
        }
    }

    private async Task FitAsync(EmulatorSession session, AndroidFit fit)
    {
        var made = await session.FitAsync(fit).ConfigureAwait(true);

        if (!made && ReferenceEquals(session, _session) && fit == _fitted && session.FitProblem is not null)
        {
            _fitAgainAt = _clock.UtcNow.AddSeconds(15);
        }
    }

    private void StopFrames()
    {
        _frames?.Cancel();
        _frames = null;
    }

    private void OnFrame(EmulatorFrame frame)
    {
        // Still drawing the last one: skip this one rather than queue it.
        if (Interlocked.Exchange(ref _drawing, 1) == 1)
        {
            return;
        }

        // Only the part Android drew the region's shape in, once it has been told one.
        var crop = _session?.Fit is { IsWhole: false } fit ? fit.Crop(frame.Width, frame.Height) : (0, 0, frame.Width, frame.Height);
        var (width, height) = (crop.Width, crop.Height);
        if (width <= 0 || height <= 0)
        {
            Interlocked.Exchange(ref _drawing, 0);
            return;
        }

        var size = width * height * 4;
        if (_back.Length != size)
        {
            _back = new byte[size];
        }

        FramePixels.RgbToBgr32(frame.Rgb.Span, frame.Width, crop, _back);

        _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            try
            {
                if (Picture is not { } picture || picture.PixelWidth != width || picture.PixelHeight != height)
                {
                    picture = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                    Picture = picture;
                }

                picture.WritePixels(new Int32Rect(0, 0, width, height), _back, width * 4, 0);
            }
            finally
            {
                Interlocked.Exchange(ref _drawing, 0);
            }
        });
    }

    private async Task StopAsync(bool restart)
    {
        StopFrames();
        ReleaseRequired();
        _microphoneOn = null;
        var old = _session;
        _session = null;
        Picture = null;

        if (old is not null)
        {
            Say($"STOPPING {AndroidChoice.Label(old.Avd.Flavour)}", "Shutting down, so it starts quickly next time.");
            await old.DisposeAsync().ConfigureAwait(true);
        }

        if (restart && _shown > 0)
        {
            EnsureStarted();
            StartFrames();
        }
        else if (!restart)
        {
            Say($"{AndroidChoice.Label(Flavour)} STOPPED", "Tap ANDROID to start it again.");
        }
    }

    private void Say(string headline, string detail)
    {
        Headline = headline;
        Detail = detail;
    }

    private static IReadOnlyList<AvdInfo> ListAvds()
    {
        var home = AndroidSdk.AvdHome(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        return AndroidChoice.List(
            home,
            folder => Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.ini") : [],
            path =>
            {
                try
                {
                    return File.Exists(path) ? File.ReadAllText(path) : null;
                }
                catch (IOException)
                {
                    return null;
                }
            });
    }

    /// <summary>
    /// With the dash closing: ask Android to shut down and let it finish on its own, so its snapshot
    /// is saved without holding the dash open. Bounded, because the dash has ten seconds in all.
    /// </summary>
    public void Dispose()
    {
        StopFrames();
        ReleaseRequired();

        if (_session is { } session)
        {
            _session = null;
            Task.Run(session.LetGoAsync).Wait(TimeSpan.FromSeconds(3));
        }
    }
}

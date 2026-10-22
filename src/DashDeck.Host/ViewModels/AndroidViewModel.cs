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
using DashDeck.Host.PhoneLink;

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
/// <param name="Gpu">The settings file's <c>androidGpu</c> (<see cref="EmulatorGpu.Choose"/>).</param>
/// <param name="RememberGpu">Save a new <c>androidGpu</c>, from the hold menu's switch.</param>
/// <param name="Picture">The settings file's <c>androidPicture</c> (<see cref="AndroidPicture"/>).</param>
/// <param name="RememberPicture">Save a new <c>androidPicture</c>, from the hold menu.</param>
/// <param name="ScrcpyFolder">The settings file's <c>androidScrcpy</c>: where scrcpy is, when it is not found by itself.</param>
public sealed record AndroidFeeds(
    IVehicleSignals? Signals, Func<bool> IsNight, Func<double> TankLitres, bool Microphone, string Gpu = "auto", Action<string>? RememberGpu = null,
    string Picture = "BALANCED", Action<string>? RememberPicture = null, string? ScrcpyFolder = null)
{
    /// <summary>Nothing fed but the position: no signals, always day, no tank, no microphone.</summary>
    public static readonly AndroidFeeds None = new(null, () => false, () => 0, false, "emulator");
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

    /// <summary>The longest side a phone's picture is drawn at, SHARP; BALANCED and FAST scale it.</summary>
    private const int PhoneLongSide = 1920;

    private readonly ILocationSource? _location;
    private readonly IClock _clock;
    private readonly AndroidFeeds _feeds;
    private string _gpu;
    private (string Name, double Scale) _pictureSize;
    private readonly List<ISignalSubscription> _required = [];
    private readonly Action<AndroidFlavour> _remember;
    private readonly Dispatcher _dispatcher;
    private EmulatorSession? _session;

    // A real phone instead (ADR-0076): its mirror, its decoder, and what ADB last said of it.
    private PhoneSession? _phone;
    private ProjectionVideo? _video;
    private AdbDevice? _lastPhone;
    private bool _lookedForPhone;
    private volatile ScrcpyInstall? _scrcpy;
    private readonly RollingRate _phoneFrames = new(TimeSpan.FromSeconds(15));
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
    private DateTimeOffset _askRendererAt;
    private static readonly System.Diagnostics.Stopwatch FrameClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly RollingRate _drawn = new(TimeSpan.FromSeconds(15));
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
        _gpu = feeds.Gpu;
        _pictureSize = AndroidPicture.Parse(feeds.Picture);
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
    [NotifyPropertyChangedFor(nameof(HasRecents))]
    private AndroidFlavour _flavour;

    /// <summary>True when the flavour has a recent-apps key (<see cref="AndroidKeys.For"/>).</summary>
    public bool HasRecents => AndroidKeys.For(Flavour).Contains(AndroidKey.Recents);

    /// <summary>The hold menu's switch: what it would change to.</summary>
    public string GpuSwitchLabel => EmulatorGpu.Choose(_gpu).Mode is { } mode && !EmulatorGpu.IsSoftware(mode)
        ? "DRAW IN SOFTWARE"
        : "DRAW WITH THE GPU";

    /// <summary>
    /// Switch between drawing on the tablet's GPU and the emulator's own choice — software on the
    /// Surface — saved, and Android restarted, since the emulator draws one way for as long as it runs.
    /// </summary>
    [RelayCommand]
    private void SwitchGpu()
    {
        IsMenuOpen = false;
        _gpu = GpuSwitchLabel == "DRAW IN SOFTWARE" ? "emulator" : "auto";
        _feeds.RememberGpu?.Invoke(_gpu);
        OnPropertyChanged(nameof(GpuSwitchLabel));
        _ = StopAsync(restart: true);
    }

    /// <summary>The hold menu's picture button: the size now, and what a tap changes it to.</summary>
    public string PictureLabel => $"PICTURE: {_pictureSize.Name}";

    public string PictureDetail => $"Tap for {AndroidPicture.Next(_pictureSize).Name} · restarts Android";

    /// <summary>
    /// The next picture size — sharper or quicker — saved, and Android restarted, since the emulator's
    /// screen is set when it starts.
    /// </summary>
    [RelayCommand]
    private void NextPicture()
    {
        IsMenuOpen = false;
        _pictureSize = AndroidPicture.Next(_pictureSize);
        _feeds.RememberPicture?.Invoke(_pictureSize.Name);
        OnPropertyChanged(nameof(PictureLabel));
        OnPropertyChanged(nameof(PictureDetail));
        _ = StopAsync(restart: true);
    }

    /// <summary>One of Android's own keys, from the key bar beside its picture.</summary>
    [RelayCommand]
    private void Press(AndroidKey key) => _ = _phone is { } phone ? phone.PressAsync(key) : _session?.PressAsync(key);

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
    public bool IsPictureShowing => Picture is not null && (_session?.State == EmulatorState.Running || _phone?.State == EmulatorState.Running);

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

    /// <summary>
    /// A hold: the menu of flavours, read fresh, so an AVD made a minute ago is in it — and the phone
    /// looked for again in the background, since ADB can take a second or two to answer.
    /// </summary>
    public void OpenMenu()
    {
        BuildMenu();
        IsMenuOpen = true;
        _ = LookForPhoneAsync();
    }

    private void BuildMenu()
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
            new AndroidMenuItem(
                AndroidFlavour.Phone,
                AndroidChoice.Label(AndroidFlavour.Phone),
                _phone is { State: EmulatorState.Running } phone ? PhoneStatus(phone)
                    : _lookedForPhone ? AdbDevice.Describe(_lastPhone) : "Looking for a phone…",
                Flavour == AndroidFlavour.Phone,
                IsAvailable: true),
        ];
    }

    /// <summary>Ask ADB for the phone, off the UI thread, and show what it said if the menu is still open.</summary>
    private async Task LookForPhoneAsync()
    {
        if (await Task.Run(Tools).ConfigureAwait(true) is not { Adb: { } adb })
        {
            _lookedForPhone = true;
            _lastPhone = null;
            return;
        }

        var (_, output) = await Task.Run(() => Adb.RunAsync(adb, ["devices", "-l"], TimeSpan.FromSeconds(10), CancellationToken.None)).ConfigureAwait(true);
        _lastPhone = AdbDevice.Pick(AdbDevice.Parse(output));
        _lookedForPhone = true;

        if (IsMenuOpen)
        {
            BuildMenu();
        }
    }

    /// <summary>
    /// The <c>adb</c> and scrcpy the phone needs: the SDK's adb when Android Studio is installed — one
    /// ADB server for the emulators and the phone, not two fighting (ADR-0075) — else scrcpy's own.
    /// </summary>
    private (string? Adb, ScrcpyInstall? Scrcpy) Tools()
    {
        // Found once and kept: looking runs scrcpy to ask its version. Looked for again until found.
        var scrcpy = _scrcpy ??= ScrcpyInstall.Find(_feeds.ScrcpyFolder);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var sdk = AndroidSdk.Find(AndroidSdk.Candidates(null, Environment.GetEnvironmentVariable, localAppData), File.Exists);
        var sdkAdb = sdk is null ? null : AndroidSdk.AdbPath(sdk);

        return (sdkAdb is not null && File.Exists(sdkAdb) ? sdkAdb : scrcpy?.AdbPath, scrcpy);
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
            if (_phone is { } phone)
            {
                phone.Showing = true;
            }
        }
    }

    /// <summary>A view has gone. When none is left, stop asking for frames; Android keeps running.</summary>
    public void Detach()
    {
        if (_shown > 0 && --_shown == 0)
        {
            StopFrames();
            if (_phone is { } phone)
            {
                phone.Showing = false;
            }
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
    public void Touch(double x, double y, bool down) => _ = _phone is { } phone ? phone.TouchAsync(x, y, down) : _session?.TouchAsync(x, y, down);

    /// <summary>
    /// The shell's beat: hand Android the truck's newest position, and pick the frames back up if the
    /// stream ended while a view is showing.
    /// </summary>
    public void Beat()
    {
        if (_phone is { State: EmulatorState.Running } phone)
        {
            // A phone has its own GPS, microphone and clock; it is only told whether it is night.
            _ = phone.FeedAsync(new CarState(Night: _feeds.IsNight()));
            if (_clock.UtcNow >= _fitAgainAt)
            {
                _fitAgainAt = DateTimeOffset.MaxValue;
                _fitted = null;
                Shape();
            }

            return;
        }

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

        // What draws Android and what it believes its screen is, asked of Android itself every few seconds.
        if (_clock.UtcNow >= _askRendererAt)
        {
            _askRendererAt = _clock.UtcNow.AddSeconds(5);
            _ = session.ReadRendererAsync();
        }

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
        else if (session.Gpu.Mode is { } mode && !session.GpuFellBack)
        {
            parts.Add($"asked for -gpu {mode}, not yet reported");
        }

        // Android's own answer is the best one; when it has not given it, say what it gave instead.
        if (session.GuestRenderer is null && session.RendererProblem is { } unanswered)
        {
            parts.Add($"Android said: {unanswered}");
        }

        if (session.Machine?.Describe() is { Length: > 0 } machine)
        {
            parts.Add(machine);
        }

        // How fast, at its busiest in the last fifteen seconds — scroll, then open this: frames the
        // emulator sent, and frames drawn, which says which side is the limit.
        parts.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"busiest second: {session.FramesReceived} frames in · {_drawn.BusiestSecond(FrameClock.Elapsed)} drawn{(session.AtFullSpeed ? "" : " · Windows may throttle it")}"));

        // The geometry, for when the picture looks wrong: the device's own screen, how it was turned,
        // the frame the emulator sends, what Android was told, and what Android says it has.
        parts.Add(string.Create(CultureInfo.InvariantCulture, $"device {session.DeviceWidth}×{session.DeviceHeight} turned {_turned ?? 0}°"));
        if (session.LastFrame is { } frame)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"frame {frame.Width}×{frame.Height} q{frame.QuarterTurns}"));
        }

        if (session.Fit is { } fit)
        {
            parts.Add($"told {fit.Describe()}");
        }

        if (session.AndroidScreen is { } screen)
        {
            parts.Add($"Android: {screen}");
        }

        if (session.GpuFellBack)
        {
            parts.Add("GPU refused, started the emulator's way");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>One line for <c>--shot</c>.</summary>
    public string Describe() =>
        $"android {View.Placement.ToString().ToLowerInvariant()} · {(_phone?.Describe() ?? _session?.Describe() ?? $"{AndroidChoice.Label(Flavour).ToLowerInvariant()} · not started")}" +
        (_phone is { } mirrored ? $" · {PhoneStatus(mirrored)}" : "") +
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
        if (_session is { State: not (EmulatorState.Failed or EmulatorState.Stopped) }
            || _phone is { State: not (EmulatorState.Failed or EmulatorState.Stopped) })
        {
            return;
        }

        if (Flavour == AndroidFlavour.Phone)
        {
            _ = StartPhoneAsync();
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
        var session = new EmulatorSession(sdk, avd, skin: AndroidPicture.Skin(StageWidth, StageHeight, _pictureSize.Scale)) { Gpu = EmulatorGpu.Choose(_gpu) };
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
                _ = session.QuickenAsync();
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
        // A phone is told the region's shape outright, no turning, at the picture size chosen.
        if (_phone is { State: EmulatorState.Running } phone)
        {
            if (AndroidFit.ForMirror(_region.Width, _region.Height, _dpiScale, (int)Math.Round(PhoneLongSide * _pictureSize.Scale)) is { } mirror
                && mirror != _fitted)
            {
                _fitted = mirror;
                _ = FitPhoneAsync(phone, mirror);
            }

            return;
        }

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

    private async Task FitPhoneAsync(PhoneSession phone, AndroidFit fit)
    {
        var made = await phone.FitAsync(fit).ConfigureAwait(true);

        if (!made && ReferenceEquals(phone, _phone) && fit == _fitted && phone.FitProblem is not null)
        {
            _fitAgainAt = _clock.UtcNow.AddSeconds(15);
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
                _drawn.Add(FrameClock.Elapsed);
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

        if (_phone is { } phone)
        {
            _phone = null;
            Say($"STOPPING {AndroidChoice.Label(AndroidFlavour.Phone)}", $"Giving the {phone.Phone.Name} its own screen back.");
            await phone.DisposeAsync().ConfigureAwait(true);
            _video?.Dispose();
            _video = null;
        }

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

    /// <summary>
    /// Mirror the phone (ADR-0076): find scrcpy and ADB, find the phone, and start — or say plainly
    /// which of the three is missing and what to do about it.
    /// </summary>
    private async Task StartPhoneAsync()
    {
        var label = AndroidChoice.Label(AndroidFlavour.Phone);
        Say($"STARTING {label}", "Looking for scrcpy and the phone…");

        var (adb, scrcpy) = await Task.Run(Tools).ConfigureAwait(true);
        if (scrcpy is null)
        {
            Say("SCRCPY NOT FOUND",
                "Install scrcpy (winget install Genymobile.scrcpy), or put its folder in the settings file as androidScrcpy, then tap ANDROID again (ADR-0076).");
            return;
        }

        if (adb is null)
        {
            Say("NO ADB", $"scrcpy in {scrcpy.Folder} has no adb.exe beside it, and Android Studio's SDK was not found.");
            return;
        }

        var (_, devices) = await Task.Run(() => Adb.RunAsync(adb, ["devices", "-l"], TimeSpan.FromSeconds(15), CancellationToken.None)).ConfigureAwait(true);
        _lastPhone = AdbDevice.Pick(AdbDevice.Parse(devices));
        _lookedForPhone = true;

        if (_lastPhone is not { State: "device" } found)
        {
            Say(_lastPhone is null ? "NO PHONE" : $"{_lastPhone.Name.ToUpperInvariant()} NOT READY",
                _lastPhone is { State: "unauthorized" }
                    ? "Unlock the phone and tap Allow on its USB debugging prompt, then tap ANDROID again."
                    : "Plug the phone in by USB with USB debugging on (Settings ▸ System ▸ Developer options), then tap ANDROID again.");
            return;
        }

        if (Flavour != AndroidFlavour.Phone || _phone is not null)
        {
            return;
        }

        var video = new ProjectionVideo(_dispatcher);
        video.PictureChanged += picture =>
        {
            if (ReferenceEquals(video, _video))
            {
                Picture = picture;
                OnPropertyChanged(nameof(IsPictureShowing));
            }
        };

        if (!video.Start())
        {
            Say("NO VIDEO DECODER", video.Problem ?? "LibVLC could not start.");
            video.Dispose();
            return;
        }

        var phone = new PhoneSession(adb, found, scrcpy, PhoneLongSide) { Showing = _shown > 0 };
        phone.Video += h264 =>
        {
            _phoneFrames.Add(FrameClock.Elapsed);
            video.Push(h264);
        };
        phone.Changed += () => _dispatcher.BeginInvoke(() => OnPhoneChanged(phone));

        _video = video;
        _phone = phone;
        _fitted = null;
        _ = phone.StartAsync();
    }

    private void OnPhoneChanged(PhoneSession phone)
    {
        if (!ReferenceEquals(phone, _phone))
        {
            return;
        }

        var label = AndroidChoice.Label(AndroidFlavour.Phone);
        switch (phone.State)
        {
            case EmulatorState.Starting or EmulatorState.Booting:
                Say($"STARTING {label}", phone.Detail);
                break;
            case EmulatorState.Running:
                Say(label, phone.Detail);
                Shape();
                break;
            case EmulatorState.Failed:
                Say($"{label} STOPPED", phone.Detail);
                break;
            case EmulatorState.Stopping:
                Say($"STOPPING {label}", phone.Detail);
                break;
        }

        OnPropertyChanged(nameof(IsPictureShowing));
    }

    /// <summary>What the hold menu says of the mirrored phone: its picture, its frames, what it was told.</summary>
    private string PhoneStatus(PhoneSession phone)
    {
        var parts = new List<string>
        {
            phone.Phone.Name,
            $"scrcpy {phone.Version}",
            string.Create(CultureInfo.InvariantCulture, $"picture {phone.VideoWidth}×{phone.VideoHeight}"),
            string.Create(CultureInfo.InvariantCulture, $"busiest second: {_phoneFrames.BusiestSecond(FrameClock.Elapsed)} frames"),
        };

        if (phone.Fit is { } fit)
        {
            parts.Add($"told {fit.Describe()}");
        }

        if (phone.FitProblem is { } fitProblem)
        {
            parts.Add($"not fitted ({fitProblem})");
        }

        if (phone.CarProblem is { } nightProblem)
        {
            parts.Add($"night mode refused ({nightProblem})");
        }

        if (_video?.Problem is { } decoder)
        {
            parts.Add(decoder);
        }

        return string.Join(" · ", parts);
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

        if (_phone is { } phone)
        {
            _phone = null;
            Task.Run(phone.LetGoAsync).Wait(TimeSpan.FromSeconds(3));
            _video?.Dispose();
            _video = null;
        }

        if (_session is { } session)
        {
            _session = null;
            Task.Run(session.LetGoAsync).Wait(TimeSpan.FromSeconds(3));
        }
    }
}

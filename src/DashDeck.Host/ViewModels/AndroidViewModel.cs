using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Core.Phone;
using DashDeck.Host.PhoneLink;
using DashDeck.Phone;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// What the dash hands the phone and keeps for it (ADR-0076): whether it is night, the picture size,
/// where scrcpy is, and the address the phone was last reached at over the network.
/// </summary>
/// <param name="IsNight">The dash's own answer — the headlights, or the sun.</param>
/// <param name="Picture">The settings file's <c>androidPicture</c> (<see cref="AndroidPicture"/>).</param>
/// <param name="RememberPicture">Save a new <c>androidPicture</c>, from the hold menu.</param>
/// <param name="ScrcpyFolder">The settings file's <c>androidScrcpy</c>: where scrcpy is, when it is not found by itself.</param>
/// <param name="PhoneAddress">The settings file's <c>androidPhoneAddress</c>: where the phone answered over the network.</param>
/// <param name="RememberAddress">Save a new <c>androidPhoneAddress</c>.</param>
public sealed record AndroidFeeds(
    Func<bool> IsNight,
    string Picture = "BALANCED",
    Action<string>? RememberPicture = null,
    string? ScrcpyFolder = null,
    string? PhoneAddress = null,
    Action<string>? RememberAddress = null)
{
    /// <summary>Always day, the default picture, nothing remembered.</summary>
    public static readonly AndroidFeeds None = new(() => false);
}

/// <summary>
/// Android on the dash (ADR-0075, ADR-0076): a real phone behind the ANDROID nav cell — full screen or
/// on the stage — mirrored with the owner's scrcpy, by cable or over the network.
/// </summary>
/// <remarks>
/// <b>Started on first sight, kept after.</b> The mirror starts the first time a view of it is on
/// screen and keeps running when that view goes, so coming back is instant. While nothing shows it,
/// its video is dropped rather than decoded. STOP gives the phone its own screen back.
/// <para>
/// <b>The phone does the drawing.</b> Its H.264 is decoded by Android Auto's own decoder
/// (<see cref="ProjectionVideo"/>), into WPF's tree like every other picture on the stage. It is told
/// the shape of the region it fills (<see cref="AndroidFit"/>), so there are no bars, and its units are
/// the dash's own in either placement.
/// </para>
/// <para>
/// <b>Wireless</b> is ADB over the network, switched on once by cable (<see cref="PhoneFinder"/>), and
/// the address remembered. When the mirror drops — the Wi-Fi gone, the cable pulled — it is tried
/// again every ten seconds while a view of it is showing.
/// </para>
/// </remarks>
public sealed partial class AndroidViewModel : ObservableObject, IDisposable
{
    /// <summary>The region a view reports until one does: the stage, in the Surface's pixels.</summary>
    private const int StageWidth = 1824;
    private const int StageHeight = 1272;

    private static readonly System.Diagnostics.Stopwatch FrameClock = System.Diagnostics.Stopwatch.StartNew();

    private readonly IClock _clock;
    private readonly AndroidFeeds _feeds;
    private readonly Dispatcher _dispatcher;
    private readonly RollingRate _phoneFrames = new(TimeSpan.FromSeconds(15));

    private PhoneSession? _phone;
    private ProjectionVideo? _video;
    private AdbDevice? _lastPhone;
    private bool _lookedForPhone;
    private volatile ScrcpyInstall? _scrcpy;
    private string? _address;
    private string? _wirelessProblem;
    private bool _goingWireless;
    private int _shown;
    private (int Width, int Height) _region = (StageWidth, StageHeight);
    private double _dpiScale = 2;
    private AndroidFit? _fitted;
    private DateTimeOffset _fitAgainAt = DateTimeOffset.MaxValue;
    private DateTimeOffset _reconnectAt = DateTimeOffset.MaxValue;
    private (string Name, double Scale) _pictureSize;

    /// <param name="clock">For when to try again — a fit, or a dropped mirror.</param>
    /// <param name="feeds">What else the phone is handed; <see cref="AndroidFeeds.None"/> for nothing.</param>
    public AndroidViewModel(IClock clock, AndroidFeeds feeds)
    {
        _clock = clock;
        _feeds = feeds;
        _pictureSize = AndroidPicture.Parse(feeds.Picture);
        _address = string.IsNullOrWhiteSpace(feeds.PhoneAddress) ? null : feeds.PhoneAddress;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _headline = "ANDROID PHONE";
    }

    /// <summary>Raised when the placement changes, for the shell to move it.</summary>
    public event Action<AndroidView>? PlacementChanged;

    /// <summary>Raised when the menu connects the phone, so the shell can show it.</summary>
    public event Action? ShowRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFull))]
    [NotifyPropertyChangedFor(nameof(IsOnStage))]
    private AndroidView _view = AndroidView.Default;

    public bool IsFull => View.IsFull;

    public bool IsOnStage => View.IsOnStage;

    /// <summary>The phone's screen, or null before the first picture.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPictureShowing))]
    private WriteableBitmap? _picture;

    /// <summary>What is happening, while there is no picture to show.</summary>
    [ObservableProperty]
    private string _headline;

    [ObservableProperty]
    private string _detail = "Plug the phone in, or connect it wirelessly from the hold menu.";

    [ObservableProperty]
    private bool _isMenuOpen;

    /// <summary>The hold menu's line under ANDROID PHONE: the phone, and how it is doing.</summary>
    [ObservableProperty]
    private string _phoneLine = "Looking for the phone…";

    /// <summary>The hold menu's line under CONNECT WIRELESSLY.</summary>
    [ObservableProperty]
    private string _wirelessLine = "";

    /// <summary>True once the phone is drawing, so the panel shows the picture rather than words.</summary>
    public bool IsPictureShowing => Picture is not null && _phone?.State == PhoneState.Running;

    /// <summary>The hold menu's picture button: the size now, and what a tap changes it to.</summary>
    public string PictureLabel => $"PICTURE: {_pictureSize.Name}";

    public string PictureDetail => $"Tap for {AndroidPicture.Next(_pictureSize).Name} · restarts the mirror";

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

    /// <summary>A hold: the menu, with the phone looked for again in the background — ADB can take a second or two.</summary>
    public void OpenMenu()
    {
        DescribeMenu();
        IsMenuOpen = true;
        _ = LookForPhoneAsync();
    }

    [RelayCommand]
    private void CloseMenu() => IsMenuOpen = false;

    /// <summary>The phone's line in the menu: connect it again — or for the first time.</summary>
    [RelayCommand]
    private void Connect()
    {
        IsMenuOpen = false;
        _ = StopAsync(restart: true);
        ShowRequested?.Invoke();
    }

    /// <summary>
    /// Switch the phone, plugged in by cable, to ADB over the network, remember where it answered, and
    /// mirror it from there — or, with no cable, try the remembered address and the hotspot again.
    /// </summary>
    [RelayCommand]
    private async Task GoWireless()
    {
        if (_goingWireless)
        {
            return;
        }

        _goingWireless = true;
        _wirelessProblem = null;
        WirelessLine = "Connecting…";

        try
        {
            var (adb, _) = await Task.Run(Tools).ConfigureAwait(true);
            if (adb is null)
            {
                _wirelessProblem = "scrcpy and its adb were not found";
                return;
            }

            var (found, reachedAt) = await Task.Run(() => PhoneFinder.FindAsync(adb, _address, CancellationToken.None)).ConfigureAwait(true);
            _lastPhone = found;
            _lookedForPhone = true;

            if (found is { State: "device", IsWireless: false } cabled)
            {
                var (address, problem) = await Task.Run(() => PhoneFinder.GoWirelessAsync(adb, cabled, CancellationToken.None)).ConfigureAwait(true);
                if (address is null)
                {
                    _wirelessProblem = problem;
                    return;
                }

                Remember(address);
            }
            else if (found is { State: "device", IsWireless: true } && reachedAt is not null)
            {
                Remember(reachedAt);
            }
            else
            {
                _wirelessProblem = "plug the phone in by USB once, then tap here";
                return;
            }

            // ADB on the phone restarted to listen on the network, which ends a mirror over the cable.
            IsMenuOpen = false;
            _ = StopAsync(restart: true);
            ShowRequested?.Invoke();
        }
        finally
        {
            _goingWireless = false;
            DescribeMenu();
        }
    }

    /// <summary>The next picture size — sharper or quicker — saved, and the mirror restarted at it.</summary>
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

    /// <summary>Stop mirroring and give the phone its own screen back. The next tap starts it again.</summary>
    [RelayCommand]
    private void Stop()
    {
        IsMenuOpen = false;
        _reconnectAt = DateTimeOffset.MaxValue;
        _ = StopAsync(restart: false);
    }

    /// <summary>One of Android's own keys, from the key bar beside its picture.</summary>
    [RelayCommand]
    private void Press(AndroidKey key) => _ = _phone?.PressAsync(key);

    /// <summary>A view of Android has come on screen: start it if needed, and show its video.</summary>
    public void Attach()
    {
        if (_shown++ == 0)
        {
            EnsureStarted();
            if (_phone is { } phone)
            {
                phone.Showing = true;
            }
        }
    }

    /// <summary>A view has gone. When none is left, its video is dropped; the mirror keeps running.</summary>
    public void Detach()
    {
        if (_shown > 0 && --_shown == 0 && _phone is { } phone)
        {
            phone.Showing = false;
        }
    }

    /// <summary>The showing panel's size in screen pixels: the phone is told the new shape.</summary>
    public void Resize(int width, int height, double dpiScale)
    {
        if (width <= 0 || height <= 0 || ((width, height) == _region && dpiScale == _dpiScale))
        {
            return;
        }

        _region = (width, height);
        _dpiScale = dpiScale > 0 ? dpiScale : _dpiScale;
        Shape();
    }

    /// <summary>A finger on the picture, as fractions of it as drawn.</summary>
    public void Touch(double x, double y, bool down) => _ = _phone?.TouchAsync(x, y, down);

    /// <summary>
    /// The shell's beat: tell the phone whether it is night, try a failed fit again, and — while a view
    /// of it is showing — reconnect a mirror that dropped.
    /// </summary>
    public void Beat()
    {
        if (_phone is { State: PhoneState.Running } phone)
        {
            _ = phone.NightAsync(_feeds.IsNight());

            if (_clock.UtcNow >= _fitAgainAt)
            {
                _fitAgainAt = DateTimeOffset.MaxValue;
                _fitted = null;
                Shape();
            }

            return;
        }

        if (_shown > 0 && _clock.UtcNow >= _reconnectAt && _phone is null or { State: PhoneState.Failed })
        {
            _reconnectAt = DateTimeOffset.MaxValue;
            _ = StopAsync(restart: true);
        }
    }

    /// <summary>One line for <c>--shot</c>.</summary>
    public string Describe() =>
        $"android {View.Placement.ToString().ToLowerInvariant()} · {(_phone?.Describe() ?? "phone · not started")}" +
        (Picture is { } p ? $" · picture {p.PixelWidth}×{p.PixelHeight}" : "") +
        (_phone is { } phone ? $" · {PhoneStatus(phone)}" : "");

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
        if (_phone is { State: not (PhoneState.Failed or PhoneState.Stopped) })
        {
            return;
        }

        _ = StartPhoneAsync();
    }

    /// <summary>
    /// Mirror the phone: find scrcpy and its ADB, find the phone — over the network first, then by
    /// cable — and start, or say plainly which is missing and what to do about it.
    /// </summary>
    private async Task StartPhoneAsync()
    {
        Say("STARTING ANDROID PHONE", "Looking for scrcpy and the phone…");

        var (adb, scrcpy) = await Task.Run(Tools).ConfigureAwait(true);
        if (scrcpy is null)
        {
            Say("SCRCPY NOT FOUND",
                "Install scrcpy (winget install Genymobile.scrcpy), or put its folder in the settings file as androidScrcpy, then tap ANDROID again (ADR-0076).");
            return;
        }

        if (adb is null)
        {
            Say("NO ADB", $"scrcpy in {scrcpy.Folder} has no adb.exe beside it, and none was found on PATH.");
            return;
        }

        var (found, _) = await Task.Run(() => PhoneFinder.FindAsync(adb, _address, CancellationToken.None)).ConfigureAwait(true);
        _lastPhone = found;
        _lookedForPhone = true;

        if (found is not { State: "device" } phoneFound)
        {
            Say(found is null ? "NO PHONE" : $"{found.Name.ToUpperInvariant()} NOT READY",
                found is { State: "unauthorized" }
                    ? "Unlock the phone and tap Allow on its USB debugging prompt, then tap ANDROID again."
                    : _address is null
                        ? "Plug the phone in by USB with USB debugging on (Settings ▸ System ▸ Developer options), then tap ANDROID again."
                        : $"Not found by cable, nor at {_address} over the network. Is the tablet on the same Wi-Fi, or on the phone's hotspot? After the phone restarts, plug it in once and tap CONNECT WIRELESSLY.");
            _reconnectAt = _clock.UtcNow.AddSeconds(10);
            return;
        }

        if (_phone is not null)
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

        var phone = new PhoneSession(adb, phoneFound, scrcpy, AndroidPicture.SharpLongSide) { Showing = _shown > 0 };
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

        switch (phone.State)
        {
            case PhoneState.Starting or PhoneState.Connecting:
                Say("STARTING ANDROID PHONE", phone.Detail);
                break;
            case PhoneState.Running:
                Say("ANDROID PHONE", phone.Detail);
                _reconnectAt = DateTimeOffset.MaxValue;
                Shape();
                break;
            case PhoneState.Failed:
                Say("ANDROID PHONE STOPPED", phone.Detail + (_shown > 0 ? " — trying again in ten seconds." : ""));
                _reconnectAt = _clock.UtcNow.AddSeconds(10);
                break;
            case PhoneState.Stopping:
                Say("STOPPING ANDROID PHONE", phone.Detail);
                break;
        }

        OnPropertyChanged(nameof(IsPictureShowing));
    }

    /// <summary>Tell the phone the shape of the region it fills, once per change, at the picture size chosen.</summary>
    private void Shape()
    {
        if (_phone is { State: PhoneState.Running } phone
            && AndroidFit.For(_region.Width, _region.Height, _dpiScale, AndroidPicture.LongSide(_pictureSize)) is { } fit
            && fit != _fitted)
        {
            _fitted = fit;
            _ = FitAsync(phone, fit);
        }
    }

    private async Task FitAsync(PhoneSession phone, AndroidFit fit)
    {
        var made = await phone.FitAsync(fit).ConfigureAwait(true);

        if (!made && ReferenceEquals(phone, _phone) && fit == _fitted && phone.FitProblem is not null)
        {
            _fitAgainAt = _clock.UtcNow.AddSeconds(15);
        }
    }

    private async Task StopAsync(bool restart)
    {
        if (_phone is { } phone)
        {
            _phone = null;
            Picture = null;
            Say("STOPPING ANDROID PHONE", $"Giving the {phone.Phone.Name} its own screen back.");
            await phone.DisposeAsync().ConfigureAwait(true);
            _video?.Dispose();
            _video = null;
        }

        if (restart && _shown > 0)
        {
            EnsureStarted();
        }
        else if (!restart)
        {
            Say("ANDROID PHONE STOPPED", "Tap ANDROID to mirror it again.");
        }
    }

    /// <summary>Ask ADB for the phone, off the UI thread, and show what it said if the menu is still open.</summary>
    private async Task LookForPhoneAsync()
    {
        var (adb, _) = await Task.Run(Tools).ConfigureAwait(true);
        if (adb is null)
        {
            _lastPhone = null;
        }
        else
        {
            var (found, _) = await Task.Run(() => PhoneFinder.FindAsync(adb, _address, CancellationToken.None)).ConfigureAwait(true);
            _lastPhone = found;
        }

        _lookedForPhone = true;

        if (IsMenuOpen)
        {
            DescribeMenu();
        }
    }

    /// <summary>
    /// The scrcpy install and the <c>adb</c> to use: scrcpy's own, so the dash and scrcpy share one ADB
    /// server, else one on <c>PATH</c>. scrcpy is looked for until it is found, then kept.
    /// </summary>
    private (string? Adb, ScrcpyInstall? Scrcpy) Tools()
    {
        var scrcpy = _scrcpy ??= ScrcpyInstall.Find(_feeds.ScrcpyFolder);
        return (scrcpy?.AdbPath ?? OnPath("adb.exe") ?? OnPath("adb"), scrcpy);
    }

    private static string? OnPath(string file) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim('"'), file))
            .FirstOrDefault(File.Exists);

    private void Remember(string address)
    {
        _address = address;
        _feeds.RememberAddress?.Invoke(address);
    }

    /// <summary>The menu's lines: the phone's, and the wireless one.</summary>
    private void DescribeMenu()
    {
        PhoneLine = _phone is { State: PhoneState.Running } phone ? PhoneStatus(phone)
            : _phone is { } starting ? starting.Detail
            : _lookedForPhone ? AdbDevice.Describe(_lastPhone)
            : "Looking for the phone…";

        WirelessLine = _wirelessProblem is { } problem ? $"Not connected: {problem}"
            : _lastPhone is { State: "device", IsWireless: true } ? $"Connected over Wi-Fi at {_lastPhone.Serial} · tap to look again"
            : _address is { } address ? $"Remembered at {address}. With the phone plugged in, tap to switch it again — needed after it restarts"
            : "Plug the phone in by USB, then tap: it is reached over Wi-Fi or its hotspot from then on";
    }

    /// <summary>What the hold menu says of the mirrored phone: how it is reached, its picture, its frames, what it was told.</summary>
    private string PhoneStatus(PhoneSession phone)
    {
        var parts = new List<string>
        {
            AdbDevice.Describe(phone.Phone),
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

        if (phone.NightProblem is { } nightProblem)
        {
            parts.Add($"night mode refused ({nightProblem})");
        }

        if (_video?.Problem is { } decoder)
        {
            parts.Add(decoder);
        }

        return string.Join(" · ", parts);
    }

    private void Say(string headline, string detail)
    {
        Headline = headline;
        Detail = detail;
    }

    /// <summary>With the dash closing: give the phone its screen back, bounded — the dash has ten seconds in all.</summary>
    public void Dispose()
    {
        if (_phone is { } phone)
        {
            _phone = null;
            Task.Run(phone.LetGoAsync).Wait(TimeSpan.FromSeconds(3));
        }

        _video?.Dispose();
        _video = null;
    }
}

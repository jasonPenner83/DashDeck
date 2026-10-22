using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Core.Emulation;
using DashDeck.Core.Location;
using DashDeck.Emulator;

namespace DashDeck.Host.ViewModels;

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
/// <b>Frames are dropped, never queued.</b> A frame arrives on a gRPC thread; if the UI has not drawn
/// the last one yet, the new one is skipped rather than piled up behind it. A picture that falls
/// behind is worse than one that skips.
/// </para>
/// </remarks>
public sealed partial class AndroidViewModel : ObservableObject, IDisposable
{
    /// <summary>Frames are asked for at the stage's size in device pixels; full screen scales them.</summary>
    private const int FrameWidth = 1824;
    private const int FrameHeight = 1272;

    private readonly ILocationSource? _location;
    private readonly Action<AndroidFlavour> _remember;
    private readonly Dispatcher _dispatcher;
    private EmulatorSession? _session;
    private CancellationTokenSource? _frames;
    private int _shown;
    private byte[] _back = [];
    private int _drawing;
    private DateTimeOffset _lastFix;

    /// <param name="location">The truck's GPS, handed to Android as its own; null when there is none.</param>
    /// <param name="flavour">Which Android the cell opens, as saved.</param>
    /// <param name="remember">Save a newly chosen flavour.</param>
    public AndroidViewModel(ILocationSource? location, AndroidFlavour flavour, Action<AndroidFlavour> remember)
    {
        _location = location;
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
                return new AndroidMenuItem(f, AndroidChoice.Label(f), AndroidChoice.Describe(avd, f), f == Flavour, avd is not null);
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

        if (_shown > 0 && _frames is null)
        {
            StartFrames();
        }
    }

    /// <summary>One line for <c>--shot</c>.</summary>
    public string Describe() =>
        $"android {View.Placement.ToString().ToLowerInvariant()} · {(_session?.Describe() ?? $"{AndroidChoice.Label(Flavour).ToLowerInvariant()} · not started")}" +
        (Picture is { } p ? $" · picture {p.PixelWidth}×{p.PixelHeight}" : "");

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

        var session = new EmulatorSession(sdk, avd);
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
        _frames = frames;

        _ = Task.Run(async () =>
        {
            try
            {
                await session.StreamAsync(FrameWidth, FrameHeight, OnFrame, frames.Token).ConfigureAwait(false);
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

        var size = frame.Width * frame.Height * 4;
        if (_back.Length != size)
        {
            _back = new byte[size];
        }

        FramePixels.RgbToBgr32(frame.Rgb, _back);

        _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            try
            {
                if (Picture is not { } picture || picture.PixelWidth != frame.Width || picture.PixelHeight != frame.Height)
                {
                    picture = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
                    Picture = picture;
                }

                picture.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), _back, frame.Width * 4, 0);
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

        if (_session is { } session)
        {
            _session = null;
            Task.Run(session.LetGoAsync).Wait(TimeSpan.FromSeconds(3));
        }
    }
}

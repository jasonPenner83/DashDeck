using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Host.Dash;
using DashDeck.Host.Sensors;
using DashDeck.Host.Settings;
using DashDeck.Host.Stage;
using DashDeck.Host.Theme;
using DashDeck.Vehicle;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// The shell: status strip, the band grid's widgets, and the navigation state.
/// </summary>
/// <remarks>
/// MVVM stops at the shell (ADR-0011). Components hand back views and are never obliged to
/// follow this pattern — but everything the shell renders itself goes through a view-model,
/// so shell logic stays testable without a UI thread.
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly VehicleStack _vehicle;

    // The stage holds up to two occupants (ADR-0026): a non-source "screen" in front, and an
    // audio/video "source" that is either in front (no screen) or playing hidden behind one.
    // Each keeps its own content host for its whole life so a live WebView2 or owned window is
    // never reparented, which would reload it and drop its audio.
    private IStageOccupant? _screen;
    private StageOptionViewModel? _screenOption;
    private IStageOccupant? _source;
    private StageOptionViewModel? _sourceOption;

    private readonly IClock _clock;
    private readonly ThemeService _theme;
    private readonly Brightness.ScreenBrightness _brightness;
    private readonly DispatcherTimer _timer;
    private readonly string? _videoPath;
    private readonly Stage.UserAppStore _userApps;
    private readonly Stage.Launcher.StageLauncherStore _launcher;

    [ObservableProperty]
    private string _clockText = "--:--";

    [ObservableProperty]
    private string _requestRateText = "—— req/s";

    /// <summary>
    /// Where the adapter link is, sampled on the same beat as the clock.
    /// </summary>
    /// <remarks>
    /// Polled in <see cref="Refresh"/> rather than driven by the transport's
    /// <c>StateChanged</c> event, which fires on the vehicle worker thread. The whole shell
    /// updates on a <see cref="DispatcherTimer"/> already, so reading the link there keeps
    /// every UI mutation on the UI thread and needs no marshalling — and a second of latency
    /// on a "lost" banner is nothing beside the seconds the readings themselves take to age
    /// to Stale.
    /// <para>
    /// The strip used to say nothing when the cable came out: it read <c>SYNTHETIC F-150 /
    /// SIMULATED</c> whether or not anything was answering, while the widgets correctly went
    /// Stale around it (F1). Now the banner appears, in the same amber the widgets turn.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdapterLost))]
    [NotifyPropertyChangedFor(nameof(LinkStatusText))]
    private TransportState _linkState = TransportState.Connected;

    /// <summary>True whenever the adapter is anything but connected.</summary>
    public bool IsAdapterLost => LinkState is not TransportState.Connected;

    /// <summary>
    /// Whether the numbers on screen are simulated. The SIM badge binds to this.
    /// </summary>
    /// <remarks>
    /// Changes at most once, from true to false, when the adapter answers after a simulated
    /// start (ADR-0034) — re-announced on the clock beat with the rest of the strip.
    /// </remarks>
    public bool IsSimulated => _vehicle.IsSimulated;

    /// <summary>
    /// Why a configured adapter was not used, if one was configured and did not come up.
    /// </summary>
    /// <remarks>
    /// Surfaced rather than swallowed: coming up simulated when you plugged in an adapter
    /// and expected real data is precisely the confusion the quality flags exist to prevent.
    /// </remarks>
    public string? AdapterFallbackReason => _vehicle.FallbackReason;

    /// <summary>True when a configured adapter failed and the dash came up simulated instead.</summary>
    public bool HasAdapterFallback => !string.IsNullOrWhiteSpace(_vehicle.FallbackReason);

    /// <summary>
    /// The fallback, phrased for the person in the driver's seat.
    /// </summary>
    /// <remarks>
    /// This exists because the reason was previously captured and bound to nothing: the app
    /// knew exactly why it had fallen back to the simulator and showed none of it, which
    /// left "I set the port and it didn't work" with no way to self-diagnose. A diagnostic
    /// that is recorded but never surfaced is not a diagnostic.
    /// </remarks>
    public string AdapterFallbackText =>
        HasAdapterFallback ? $"ADAPTER NOT CONNECTED — SHOWING SIMULATED DATA · {_vehicle.FallbackReason}" : string.Empty;

    /// <summary>What the banner says. A fault is not the same as a pulled cable.</summary>
    public string LinkStatusText => LinkState is TransportState.Faulted
        ? "ADAPTER FAULT"
        : "ADAPTER LOST — RECONNECTING";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDashActive))]
    [NotifyPropertyChangedFor(nameof(IsStereoActive))]
    [NotifyPropertyChangedFor(nameof(IsClimateActive))]
    [NotifyPropertyChangedFor(nameof(IsMapsActive))]
    [NotifyPropertyChangedFor(nameof(IsMapFull))]
    [NotifyPropertyChangedFor(nameof(IsMapVisible))]
    [NotifyPropertyChangedFor(nameof(IsMapSearchVisible))]
    [NotifyPropertyChangedFor(nameof(IsMapOnStageNoticeVisible))]
    [NotifyPropertyChangedFor(nameof(IsAndroidActive))]
    [NotifyPropertyChangedFor(nameof(IsAndroidFull))]
    [NotifyPropertyChangedFor(nameof(IsAndroidVisible))]
    [NotifyPropertyChangedFor(nameof(IsSettingsActive))]
    [NotifyPropertyChangedFor(nameof(IsDestinationUnbuilt))]
    [NotifyPropertyChangedFor(nameof(StageBands))]
    [NotifyPropertyChangedFor(nameof(WidgetBands))]
    [NotifyPropertyChangedFor(nameof(IsOccupantVisible))]
    [NotifyPropertyChangedFor(nameof(IsSourceVisible))]
    [NotifyPropertyChangedFor(nameof(IsFullScreenOpen))]
    [NotifyPropertyChangedFor(nameof(IsDashVisible))]
    [NotifyPropertyChangedFor(nameof(IsClimateVisible))]
    [NotifyPropertyChangedFor(nameof(IsStereoVisible))]
    [NotifyPropertyChangedFor(nameof(StageRunningButHidden))]
    [NotifyPropertyChangedFor(nameof(HiddenOccupantName))]
    private string _activeDestination = "DASH";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StageHasOccupant))]
    [NotifyPropertyChangedFor(nameof(StageIsEmpty))]
    [NotifyPropertyChangedFor(nameof(IsOccupantVisible))]
    [NotifyPropertyChangedFor(nameof(IsSourceVisible))]
    [NotifyPropertyChangedFor(nameof(StageRunningButHidden))]
    private FrameworkElement? _stageContent;

    /// <summary>The audio/video source's view, in its own host behind the screen's, so a live
    /// player is never reparented. Shown when the source is in front; collapsed (alive, still
    /// playing) when a screen is over it (ADR-0026).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StageHasOccupant))]
    [NotifyPropertyChangedFor(nameof(StageIsEmpty))]
    [NotifyPropertyChangedFor(nameof(IsSourceVisible))]
    [NotifyPropertyChangedFor(nameof(StageRunningButHidden))]
    private FrameworkElement? _sourceContent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOccupantVisible))]
    [NotifyPropertyChangedFor(nameof(IsSourceVisible))]
    private bool _isStagePickerOpen;

    /// <summary>
    /// What the current occupant can be told to do, shown in the overflow menu.
    /// </summary>
    /// <remarks>
    /// These lived on a dedicated one-band row, which cost the occupant a quarter of the stage
    /// to carry two buttons — picture you look at constantly, traded for a control you use
    /// once (ADR-0022).
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStageActions))]
    private IReadOnlyList<StageAction> _stageActions = [];

    /// <summary>
    /// The overflow menu, opened from the status strip.
    /// </summary>
    /// <remarks>
    /// Exists because the gesture it replaces did not work. Editing the dash was a 600 ms
    /// hold on a card, implemented on the mouse events — and the card strip has manipulation
    /// enabled for swiping, which swallows touch before it is ever promoted to a mouse event.
    /// It could never have worked with a finger, which is exactly how it was found: in the
    /// truck, by someone trying to use it.
    /// </remarks>
    [ObservableProperty]
    private bool _isMenuOpen;

    /// <summary>
    /// The menu is drawn by the shell, and an occupant that is a window of its own sits above the
    /// shell — so a hosted application has to stand down while the menu is up or the menu opens
    /// behind it. Found in the truck: the sync and restart actions were there and unreachable
    /// with Nuvio playing.
    /// </summary>
    /// <remarks>
    /// Only the hosted occupants act on this (<see cref="Stage.IStageOccupant.SetObscured"/> is a
    /// no-op everywhere else), which matters: collapsing the stage wholesale would hide the video
    /// you are tuning the sync offset against, and that offset is nudged from this very menu.
    /// </remarks>
    partial void OnIsMenuOpenChanged(bool value)
    {
        _screen?.SetObscured(value);
        _source?.SetObscured(value);
    }

    public ShellViewModel(
        VehicleStack vehicle,
        IClock clock,
        ThemeService theme,
        WeatherService weather,
        string? videoPath = null,
        string? startOn = null,
        Components.ComponentHost? components = null,
        string? gpsOverride = null,
        string? mapStyle = null)
    {
        _vehicle = vehicle;
        _clock = clock;
        _theme = theme;
        Weather = weather;

        Shell.StartupTrace.Phase("shell: reading display settings");
        Display = new DisplaySettings();

        // The tablet's backlight, by day and by night (ADR-0068). Started before anything slow so
        // a launch at night dims early; its reads and writes run on a worker of their own.
        Shell.StartupTrace.Phase("shell: the backlight");
        _brightness = new Brightness.ScreenBrightness(
            new Brightness.WmiBacklight(), new Brightness.SettingsBacklightMemory(), () => Display.Backlight);
        _brightness.Start();
        _brightness.Apply(theme.IsNight);

        // The player outlives the panel, so it is made here and disposed with the shell. It is
        // allowed to fail: a missing native LibVLC must cost the music, never the whole dash.

        // Which signals are shown by state name (ADR-0056), for the layouts' selector element.
        Stage.Gauges.SignalStates.Of = id => vehicle.Catalog.TryGet(id, out var definition) ? definition.States : null;

        // The phone's GPS (ADR-0027), if it is turned on or a --gps override forces one. When
        // there is one, the tablet sensors and the phone GPS share a device behind the composite;
        // otherwise it is the tablet's sensors alone, exactly as before.
        Shell.StartupTrace.Phase("shell: starting the phone GPS");
        var location = ResolveLocationSource(gpsOverride, Display, clock);
        IDeviceSensors? device = null;

        // Declared up here because the sensors are built before the map is, and the navigation
        // readouts below have to reach it. Every closure over it runs long after it is filled in.
        MapViewModel? map = null;

        if (location is not null)
        {
            location.Start();
            var phone = new PhoneLocationSensors(location, clock);

            // The navigation readouts (ADR-0066): the dash's own arithmetic over the phone's fix
            // and whatever the map has been pointed at. It reads the phone's aged fix rather than
            // the raw source, so a GPS that goes quiet takes the navigation numbers with it
            // instead of counting down from a position that stopped arriving.
            var navigation = new NavigationSensors(
                () => phone.Fix,
                () => map?.Destination?.Camera,
                () => map?.Directions?.Route);

            device = new CompositeDeviceSensors(new DeviceSensors(), phone, navigation);
        }

        // One service for the whole session: it holds the mount reference and any vehicle
        // declarations, so it must outlive whichever occupant happens to be on the stage.
        Shell.StartupTrace.Phase("shell: the sensor service");
        Sensors = new SensorService(
            SensorCatalog.FromFileOrEmpty(CatalogPath.Find("sensors.device.json")),
            vehicle.Signals,
            clock,
            device);

        _videoPath = videoPath;

        // The user's own stage apps (ADR-0024), loaded once. Adding one raises Changed and the
        // shell rebuilds its options from the store, so a new launcher lights up without a restart.
        Shell.StartupTrace.Phase("shell: the app store");
        _userApps = new Stage.UserAppStore();

        // What the launcher offers, in what order, and which get a button below the stage
        // (ADR-0038): launcher.json if there is one that works, the built-in list otherwise. The
        // built-in one is written out beside it to copy from. A user app being added or removed
        // re-reads it, because the quick bar may name one; either way the buttons rebuild without
        // touching what is on the stage.
        Shell.StartupTrace.Phase("shell: the launcher");
        _launcher = new Stage.Launcher.StageLauncherStore(
            JsonFile.InLocalAppData("launcher.json"),
            () => _userApps.Apps.Select(a => a.Name));
        _launcher.WriteExample();
        _launcher.Changed += (_, _) => RebuildStageOptions();
        _userApps.Changed += (_, _) => _launcher.Reload();

        // Settings owns levelling now, so it needs the sensors (ADR-0022); it also edits the
        // user app store, and refuses names that collide with a built-in.
        // The Sensors section (ADR-0032) reads the running pipeline and edits the user's own
        // signal file, which the next launch lays over the shipped catalog.
        Shell.StartupTrace.Phase("shell: the sensor inventory");
        Inventory = new SensorInventoryViewModel(vehicle, new UserSignalStore(), Sensors, App.RequestRestart, new DiscoveryStore(), clock, folder => new ThemeDialogs().OpenFolder(folder));

        // Which vehicle this is (ADR-0033): read from the truck or typed, decoded once by NHTSA,
        // cached, and applied at the next launch like the rest of the Vehicle section.
        Shell.StartupTrace.Phase("shell: the vehicle identity");
        VehicleIdentity = new VehicleIdentityViewModel(
            new VehicleIdentityStore(),
            new Identity.VpicVinDecoder(),
            vehicle.ProbeAsync,
            clock,
            vehicle.IsSimulated,
            vehicle.AvailablePacks,
            vehicle.ActivePacks,
            vehicle.PackProblem,
            App.RequestRestart);

        // The adapter and the tested-ports list (ADR-0034). Choosing a port while simulated is
        // watched for at once; the dash goes live when it answers, with no restart.
        Shell.StartupTrace.Phase("shell: the adapter ports");
        Adapter = new AdapterPortsViewModel(
            vehicle,
            new SerialPortProbe(),
            clock,
            () => Display.AdapterSerialPort,
            port => Display.AdapterSerialPort = port,
            () => Display.ReservedSerialPorts,
            () => Display.AdapterBaudRate > 0 ? Display.AdapterBaudRate : null,
            App.RequestRestart);

        // Remember what the adapter answered with — the rate, the identity, and the port if
        // Windows moved it — so the next launch connects first time. Raised on the vehicle
        // worker, so marshalled: the settings are bound to the screen.
        var dispatcher = Dispatcher.CurrentDispatcher;
        vehicle.AdapterFound += found => dispatcher.BeginInvoke(() => Remember(found));

        // Found at launch, before anything could subscribe.
        if (vehicle.LiveAdapter is { } atLaunch)
        {
            Remember(atLaunch);
        }

        Shell.StartupTrace.Phase("shell: settings");
        Settings = new SettingsViewModel(
            theme, Display, Sensors, _userApps, _launcher, Inventory, VehicleIdentity, Adapter, location, clock, _brightness);

        // The MAPS destination (ADR-0062). Built before anything touches the stage, for two
        // ordering reasons that are both real: handing the map to the stage goes through the same
        // SetStage path the launcher uses, and AfterStageChange reconciles the map's placement on
        // every stage change — including the opening one, below.
        Shell.StartupTrace.Phase("shell: the map");

        // Address search (ADR-0065) needs the map it is searching on — for where to bias the
        // search and how fast the truck is going — and the map needs the search, to go where it
        // was told. The closures are only ever called after both exist, which is what lets two
        // things that need each other be built in one order. The map itself is declared further
        // up, with the sensors, for the same reason.
        var search = new MapSearchViewModel(
            Maps.AddressLookup.FetchAsync,
            clock,
            () => map?.Camera,
            () => map?.SpeedKmh,
            () => Display.SearchWhileMoving);

        // Directions start from where the truck is now rather than from where the map is looking:
        // a route from the corner you panned to would be a route from nowhere you have been.
        var directions = new MapRouteViewModel(
            Maps.AddressLookup.FetchAsync,
            clock,
            () => map?.Here);

        // The buttons over the map (ADR-0071) open the way they were left, and the range lines read
        // the truck's own distance to empty.
        var saved = SettingsStore.Load();
        map = new MapViewModel(
            location,
            clock,
            mapStyle,
            search,
            directions,
            DashDeck.Core.Maps.MapChoices.From(saved.MapBase, saved.MapOrientation, saved.MapRange),
            chosen => SettingsStore.Update(stored => stored with
            {
                MapBase = chosen.Base.ToString(),
                MapOrientation = chosen.Orientation.ToString(),
                MapRange = chosen.ShowRange,
            }),
            vehicle.Signals);
        Map = map;
        Map.PlacementChanged += _ => OnMapMoved();

        // Android on the dash (ADR-0075): the ANDROID cell's emulator, in the flavour last chosen.
        // Built with the map, before anything touches the stage, for the same reason: handing it to
        // the stage goes through SetStage, and AfterStageChange reconciles its placement.
        Shell.StartupTrace.Phase("shell: android");
        Android = new AndroidViewModel(
            location,
            DashDeck.Core.Emulation.AndroidChoice.Parse(saved.AndroidFlavour),
            flavour => SettingsStore.Update(stored => stored with { AndroidFlavour = flavour.ToString() }),
            clock,
            new AndroidFeeds(
                vehicle.Signals, () => _theme.IsNight, () => Display.FuelTankLitres, saved.AndroidMicrophone,
                DashDeck.Core.Emulation.EmulatorGpu.Parse(saved.AndroidGpu)));
        Android.PlacementChanged += _ => OnAndroidMoved();
        Android.FlavourChosen += () => AndroidTap();

        // The map is drawn by a browser that has never heard of the theme's resources, so it is
        // handed the palette instead — once now, and again whenever one is applied (ADR-0064).
        // A theme worn, an accent picked or dusk falling all restyle a map already on screen.
        Map.Wear(Maps.MapTheme.From(Theme.ThemeResolver.Resolve(theme.Current, theme.IsNight)));
        theme.Applied += resolved => Map.Wear(Maps.MapTheme.From(resolved));

        // The search covers the map's own region rather than floating over it, so the shell has
        // to know when it opens.
        Map.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MapViewModel.IsSearchOpen) or nameof(MapViewModel.IsStepsOpen))
            {
                OnPropertyChanged(nameof(IsMapVisible));
                OnPropertyChanged(nameof(IsMapSearchVisible));
                OnPropertyChanged(nameof(IsMapStepsVisible));
            }
        };

        // The warning lights (ADR-0055): built before the stage, because whether the occupant is
        // drawn depends on whether the warning window is up.
        Shell.StartupTrace.Phase("shell: the warning lights");
        Warnings = BuildWarnings(vehicle, clock, Display);
        Warnings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WarningsViewModel.ShowWindow))
            {
                OnPropertyChanged(nameof(IsOccupantVisible));
                OnPropertyChanged(nameof(IsSourceVisible));
            }
        };

        RebuildStageOptions();

        // The cards come from a file now, not from this constructor. Rates are still declared
        // honestly — the whole app shares one serialised link, and asking for more than you
        // need degrades everyone (ADR-0004) — but the honesty is the user's to keep, so the
        // editor shows what each card costs.
        //
        // Built here, and with no band count, for two ordering reasons that both showed up as
        // a null reference on startup: choosing the opening stage re-syncs the widget bands
        // and so needs this to exist, and StageBands reads IsCardEditorOpen, which reads this.
        // It starts on its own default and SyncWidgetBands settles it below.
        // The words the map knows (ADR-0067), as a third kind of card source beside signals and
        // sensors. Closures again, and this time with no ordering to work around — the map is
        // already built by here. It is always available, unlike the navigation *numbers*, because
        // choosing a destination needs no GPS.
        var readouts = Dash.ReadoutCatalog.ForNavigation(
            () => map.Destination,
            () => map.Directions?.Route);

        Shell.StartupTrace.Phase("shell: the dash and its cards");
        Dashboard = new DashboardViewModel(
            new CardValueFactory(vehicle.Signals, Sensors, readouts),
            ValueChoice.All(vehicle.Catalog, Sensors.Catalog, readouts),
            components);

        // The cards are on the stage now (ADR-0041): the stage's picture is their region, and
        // until CARDS is the occupant no page of them is on screen, so none asks for anything.
        Dashboard.RegionHeight = BandGrid.StageOccupantHeight;
        Dashboard.IsShown = false;

        // Start on whatever was asked for at launch, then on what the launcher file names, and
        // otherwise on the gauges (F12/B6): a truck's idle stage wanting gauges beats a clock, and
        // it settles the "what do we open on" question without restoring an arbitrary last
        // occupant. A name that is not offered falls through rather than leaving the stage empty.
        StageOptionViewModel? Named(string? name) => name is null
            ? null
            : StageOptions.FirstOrDefault(o => o.IsAvailable && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));

        var opening = startOn is not null
            ? Named(startOn)
            : (videoPath is not null ? Named("VIDEO") : null)
                ?? Named(_launcher.Current.StartOn)
                ?? Named("GAUGES")
                ?? StageOptions.FirstOrDefault(o => o.IsAvailable);

        if (opening is not null)
        {
            SetStage(opening);
        }
        else if (string.Equals(startOn, "MAPS", StringComparison.OrdinalIgnoreCase))
        {
            // --stage MAPS means the map, on the stage. It is not a launcher entry (ADR-0062), so
            // Named() cannot find it and the stage would otherwise open empty — and of the map's
            // three placements this is the one that cannot be reached without tapping through.
            Map.SendToStageCommand.Execute(null);
        }

        // Also for the case where nothing opened, so the dash is never packed against the
        // wrong number of bands.
        SyncWidgetBands();

        // DASH is where the shell opens, and choosing it is what makes the console — so make it.
        SyncPanels();

        Dashboard.PropertyChanged += (_, e) =>
        {
            // The card editor is a full-screen view, so it claims the stage the way Settings
            // does (Q17). The stage keeps running underneath either way.
            if (e.PropertyName is nameof(DashboardViewModel.IsCardEditorOpen)
                or nameof(DashboardViewModel.IsEditing)
                or nameof(DashboardViewModel.IsComponentDetailOpen))
            {
                OnPropertyChanged(nameof(StageBands));
                OnPropertyChanged(nameof(WidgetBands));
                OnPropertyChanged(nameof(IsOccupantVisible));
                OnPropertyChanged(nameof(IsCardEditorOpen));
                OnPropertyChanged(nameof(IsComponentDetailOpen));
                OnPropertyChanged(nameof(IsDashVisible));
                OnPropertyChanged(nameof(IsClimateVisible));
                OnPropertyChanged(nameof(IsMapVisible));
                OnPropertyChanged(nameof(IsMapSearchVisible));
                OnPropertyChanged(nameof(IsMapOnStageNoticeVisible));
                OnPropertyChanged(nameof(IsAndroidVisible));
                OnPropertyChanged(nameof(IsFullScreenOpen));
                OnPropertyChanged(nameof(IsDashEditing));
                OnPropertyChanged(nameof(IsNavVisible));
                OnPropertyChanged(nameof(IsSourceVisible));
                OnPropertyChanged(nameof(StageRunningButHidden));
                OnPropertyChanged(nameof(HiddenOccupantName));
                SyncWidgetBands();
            }
        };

        Shell.StartupTrace.Phase("shell: the one-second beat");
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    /// <summary>The warning lights, their popups, and the trouble codes (ADR-0055).</summary>
    public WarningsViewModel Warnings { get; }

    private static WarningsViewModel BuildWarnings(VehicleStack vehicle, IClock clock, DisplaySettings display)
    {
        var (warnings, problems) = Core.Warnings.WarningCatalog.Load(CatalogPath.Find(Core.Warnings.WarningCatalog.FileName));
        var (descriptions, descriptionProblem) = Core.Diagnostics.TroubleCodeDescriptions.Load(CatalogPath.FindFolder("reference"));

        var diagnostics = new Warnings.VehicleDiagnostics(
            vehicle.ProbeAsync,
            () => vehicle.Catalog,
            () => vehicle.Reference,
            () => display.AllowClearCodes,
            descriptions,
            JsonFile.InLocalAppData("actions.log"),
            clock);

        return new WarningsViewModel(
            vehicle.Signals,
            warnings,
            diagnostics,
            new Warnings.StoredWarningPreferences(),
            clock,
            () => display.AllowClearCodes,
            on => display.AllowClearCodes = on,
            [.. problems, .. descriptionProblem is null ? [] : new[] { descriptionProblem }]);
    }

    /// <summary>Display preferences the web occupants follow. Shared, so a change re-zooms live.</summary>
    public DisplaySettings Display { get; }

    /// <summary>One weather fetch for the whole app. The status strip and the clock face share it.</summary>
    public WeatherService Weather { get; }

    /// <summary>The tablet's sensors, resolved truck-first (ADR-0016). Outlives every occupant.</summary>
    public SensorService Sensors { get; }

    /// <summary>The arranged cards filling the bands below the stage.</summary>
    public DashboardViewModel Dashboard { get; }

    /// <summary>Appearance and, in time, the rest.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Keep what the adapter answered with, for the next launch (ADR-0034).</summary>
    private void Remember(AdapterLocation found)
    {
        Display.AdapterBaudRate = found.BaudRate;
        Display.AdapterIdentity = found.Identity;

        if (found.Moved)
        {
            Display.AdapterSerialPort = found.Port;
        }
    }

    /// <summary>The adapter and its tested ports, for Settings ▸ Vehicle.</summary>
    private AdapterPortsViewModel Adapter { get; }

    /// <summary>Which vehicle this is, for Settings ▸ Vehicle.</summary>
    private VehicleIdentityViewModel VehicleIdentity { get; }

    /// <summary>The Sensors section's inventory, refreshed on the clock beat while it is open.</summary>
    private SensorInventoryViewModel Inventory { get; }

    public bool IsDashActive => ActiveDestination == "DASH";

    public bool IsStereoActive => ActiveDestination == "STEREO";

    public bool IsClimateActive => ActiveDestination == "CLIMATE";

    public bool IsMapsActive => ActiveDestination == "MAPS";

    public bool IsAndroidActive => ActiveDestination == "ANDROID";

    public bool IsSettingsActive => ActiveDestination == "SETTINGS";

    /// <summary>
    /// True for destinations that exist in the nav but have nothing behind them yet.
    /// </summary>
    /// <remarks>
    /// Stereo was parked here because the nav is the destination list (B3) and an empty strip
    /// would say less; climate was too, until ADR-0040 gave it a read-only panel. Climate is
    /// built; Stereo is parked again on <c>parked/stereo-music</c>, so it says so once more —
    /// which is exactly what this strip is for.
    /// </remarks>
    public bool IsDestinationUnbuilt => IsStereoActive;

    /// <summary>
    /// The climate panel (ADR-0040), drawn from the climate layout in the region the cards use —
    /// or null whenever CLIMATE is not the destination.
    /// </summary>
    /// <remarks>
    /// Made when CLIMATE is chosen and disposed when it is left, so its signals are declared only
    /// while it is on screen — the same rule as a card page (ADR-0015). Read only: it shows what
    /// the truck reports and sends nothing (ADR-0006).
    /// </remarks>
    [ObservableProperty]
    private FrameworkElement? _climateContent;

    /// <summary>
    /// The console (ADR-0041), drawn from the console layout where the cards used to be — or null
    /// whenever DASH is not the destination. Made and disposed like the climate panel, so it asks
    /// the truck for nothing while it is not on screen.
    /// </summary>
    [ObservableProperty]
    private FrameworkElement? _consoleContent;

    /// <summary>
    /// The map's placement and its placeholder picture (ADR-0062).
    /// </summary>
    /// <remarks>
    /// The one destination with a size of its own: the bottom third, all six bands, or handed to
    /// the stage. The view-model holds which; this shell turns that into bands and regions.
    /// </remarks>
    public MapViewModel Map { get; }

    /// <summary>
    /// The map drawn in the region below the stage — or null whenever it is not there.
    /// </summary>
    /// <remarks>
    /// Made and dropped like the climate panel, though for a different reason: the map declares
    /// no vehicle signals, so there is nothing to stop asking for. It is about what ADR-0058's
    /// map will be — a WebView2 — which is not something to keep alive behind a dash nobody has
    /// navigated to.
    /// </remarks>
    [ObservableProperty]
    private FrameworkElement? _mapContent;

    /// <summary>True when the map has taken all six bands.</summary>
    public bool IsMapFull => IsMapsActive && Map.IsFull;

    /// <summary>
    /// Whether the map is drawing in the region below the stage — at either of its two sizes.
    /// </summary>
    /// <remarks>
    /// One region for both sizes, because the size is <see cref="StageBands"/>'s business: at
    /// full screen the stage collapses to nothing and this fills what is left, which is the
    /// whole six bands. So there is one binding, not two.
    /// </remarks>
    public bool IsMapVisible =>
        IsMapsActive && !Map.IsOnStage && Map.IsMapShowing && !IsCardEditorOpen && !IsComponentDetailOpen;

    /// <summary>
    /// Whether address search is covering the map (ADR-0065).
    /// </summary>
    /// <remarks>
    /// It covers rather than overlays, because a WebView2 is a child window and WPF cannot draw a
    /// keyboard on top of one (ADR-0021). The map is still alive behind it.
    /// </remarks>
    public bool IsMapSearchVisible =>
        IsMapsActive && Map.IsSearchOpen && !IsCardEditorOpen && !IsComponentDetailOpen;

    /// <summary>Whether the turn list is covering the map.</summary>
    public bool IsMapStepsVisible =>
        IsMapsActive && Map.IsStepsOpen && !IsCardEditorOpen && !IsComponentDetailOpen;

    /// <summary>
    /// Whether to say, in the nav's region, that the map is on the stage.
    /// </summary>
    /// <remarks>
    /// Tapping MAPS while the map is on the stage has to answer for itself. Drawing the
    /// placeholder a second time would be a lie about where the map is, and drawing nothing
    /// reads as a destination that broke.
    /// </remarks>
    public bool IsMapOnStageNoticeVisible =>
        IsMapsActive && Map.IsOnStage && !IsCardEditorOpen && !IsComponentDetailOpen;

    /// <summary>
    /// Android on the dash (ADR-0075): full screen from the nav's ANDROID cell, or on the stage.
    /// </summary>
    public AndroidViewModel Android { get; }

    /// <summary>Android's full-screen panel, or null whenever it is not the destination or is on the stage.</summary>
    [ObservableProperty]
    private FrameworkElement? _androidContent;

    /// <summary>True when Android has taken all six bands.</summary>
    public bool IsAndroidFull => IsAndroidActive && !Android.IsOnStage;

    /// <summary>Whether Android is drawing full screen below the status strip.</summary>
    public bool IsAndroidVisible => IsAndroidFull && !IsCardEditorOpen && !IsComponentDetailOpen;

    /// <summary>Where the nav was before ANDROID, to go back to when Android is sent to the stage.</summary>
    private string _beforeAndroid = "DASH";

    private StageOptionViewModel? _androidStageOption;

    /// <summary>Android as a stage option — not in the picker; reached by double-tapping its cell.</summary>
    private StageOptionViewModel AndroidStageOption => _androidStageOption ??= new StageOptionViewModel(
        new StageOption(
            "ANDROID",
            "Android, on the stage",
            () => new AndroidStageOccupant(Android, AndroidTap)));

    /// <summary>
    /// A tap on ANDROID: open it full screen — from anywhere, including off the stage — and start it.
    /// </summary>
    [RelayCommand]
    private void AndroidTap()
    {
        if (!IsAndroidActive)
        {
            _beforeAndroid = ActiveDestination is "SETTINGS" or "ANDROID" ? "DASH" : ActiveDestination;
            ActiveDestination = "ANDROID";
        }

        Android.Tap();
        SyncPanels();
    }

    /// <summary>
    /// A double tap: Android onto the stage, and the nav back where it was, so the dash shows below it.
    /// </summary>
    [RelayCommand]
    private void AndroidDoubleTap()
    {
        Android.DoubleTap();

        if (IsAndroidActive)
        {
            ActiveDestination = _beforeAndroid;
        }
    }

    /// <summary>A hold: the menu of flavours — Android or Android Automotive.</summary>
    [RelayCommand]
    private void AndroidHold() => Android.OpenMenu();

    /// <summary>Follow Android to wherever it has just moved — as <see cref="OnMapMoved"/> does for the map.</summary>
    private void OnAndroidMoved()
    {
        if (Android.IsOnStage)
        {
            SetStage(AndroidStageOption);
        }
        else if (ReferenceEquals(_screenOption, AndroidStageOption))
        {
            DisposeScreen();
            AfterStageChange();
        }

        SyncPanels();

        OnPropertyChanged(nameof(IsAndroidFull));
        OnPropertyChanged(nameof(IsAndroidVisible));
        OnPropertyChanged(nameof(IsFullScreenOpen));
        OnPropertyChanged(nameof(StageBands));
        OnPropertyChanged(nameof(WidgetBands));
        OnPropertyChanged(nameof(IsOccupantVisible));
        OnPropertyChanged(nameof(IsSourceVisible));
        OnPropertyChanged(nameof(StageRunningButHidden));
        OnPropertyChanged(nameof(HiddenOccupantName));
        SyncWidgetBands();
    }

    private StageOptionViewModel? _mapStageOption;

    /// <summary>
    /// The map as a stage option, so handing it off goes through the one path that puts anything
    /// on the stage.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>not</em> in <see cref="StageOptions"/>, and so not in the picker or the
    /// quick bar: the map is reached from the nav, and offering a placeholder in three places
    /// would be three things to undo. When ADR-0058's map lands it belongs in the launcher file
    /// as a <c>map</c> entry like everything else on the stage (ADR-0038).
    /// </remarks>
    private StageOptionViewModel MapStageOption => _mapStageOption ??= new StageOptionViewModel(
        new StageOption(
            "MAPS",
            "The map, on the stage",
            () => new MapStageOccupant(Map, () => Map.BringBackCommand.Execute(null))));

    /// <summary>
    /// Follow the map to wherever it has just moved.
    /// </summary>
    /// <remarks>
    /// The placement is the map's own state and the consequences are the shell's: a size change
    /// re-draws the bands, and the stage handoff makes or removes an occupant. Taking the
    /// occupant off happens here rather than in its <c>Dispose</c>, which would run inside
    /// <see cref="SetStage"/> and re-enter it.
    /// </remarks>
    private void OnMapMoved()
    {
        if (Map.IsOnStage)
        {
            SetStage(MapStageOption);
        }
        else if (ReferenceEquals(_screenOption, MapStageOption))
        {
            // Brought back by its own action while it is still the occupant. The stage is left
            // empty rather than guessing at a replacement — the launcher bar is right there.
            DisposeScreen();
            AfterStageChange();
        }

        SyncPanels();

        OnPropertyChanged(nameof(IsMapFull));
        OnPropertyChanged(nameof(IsMapVisible));
        OnPropertyChanged(nameof(IsMapSearchVisible));
        OnPropertyChanged(nameof(IsMapOnStageNoticeVisible));
        OnPropertyChanged(nameof(IsFullScreenOpen));
        OnPropertyChanged(nameof(StageBands));
        OnPropertyChanged(nameof(WidgetBands));
        OnPropertyChanged(nameof(IsOccupantVisible));
        OnPropertyChanged(nameof(IsSourceVisible));
        OnPropertyChanged(nameof(StageRunningButHidden));
        OnPropertyChanged(nameof(HiddenOccupantName));
        SyncWidgetBands();
    }

    /// <summary>
    /// Where the Stereo panel went (Q14, ADR-0060's addendum).
    /// </summary>
    /// <remarks>
    /// Parked on <c>parked/stereo-music</c>. It is kept as a property so the nav strip and the
    /// window binding stay honest — STEREO is still a destination and still shows nothing —
    /// rather than threading a removal through the layout for something that is coming back.
    /// </remarks>
    public FrameworkElement? StereoContent => null;

    partial void OnActiveDestinationChanged(string value) => SyncPanels();

    /// <summary>Make the panel for the destination now chosen, and dispose the one left.</summary>
    private void SyncPanels()
    {
        if (IsClimateActive && ClimateContent is null)
        {
            ClimateContent = new StageLayoutView(_theme.ClimateLayouts, _vehicle.Signals, _clock, Sensors);
        }
        else if (!IsClimateActive && ClimateContent is StageLayoutView climate)
        {
            ClimateContent = null;
            climate.Dispose();
        }

        if (IsDashActive && ConsoleContent is null)
        {
            ConsoleContent = new StageLayoutView(_theme.ConsoleLayouts, _vehicle.Signals, _clock, Sensors);
        }
        else if (!IsDashActive && ConsoleContent is StageLayoutView console)
        {
            ConsoleContent = null;
            console.Dispose();
        }

        // The map, whenever it is the destination and not on the stage. Dropped on the way out
        // and when it is handed off, so the one placeholder is never drawn in two places at once.
        if (IsMapsActive && !Map.IsOnStage && MapContent is null)
        {
            // Read the position before it is drawn, not a second later on the beat: the readout
            // is only refreshed while the map is on screen, so without this the panel opens
            // showing whatever was true the last time anybody looked at it.
            Map.Refresh();
            MapContent = new Views.MapPanel { DataContext = Map };
        }
        else if ((!IsMapsActive || Map.IsOnStage) && MapContent is { } map)
        {
            MapContent = null;
            (map as IDisposable)?.Dispose();
        }

        // Android full screen, whenever it is the destination and not on the stage. Dropping the
        // panel stops its frames; the emulator itself keeps running (ADR-0075).
        if (IsAndroidFull && AndroidContent is null)
        {
            AndroidContent = new Views.AndroidPanel { DataContext = Android };
        }
        else if (!IsAndroidFull && AndroidContent is not null)
        {
            AndroidContent = null;
        }
    }

    /// <summary>What the console is showing, for <c>--shot</c>, or null when it is not open.</summary>
    public string? DescribeConsole() => (ConsoleContent as StageLayoutView)?.Describe();

    /// <summary>What the climate panel is showing, for <c>--shot</c>, or null when it is not open.</summary>
    public string? DescribeClimate() => (ClimateContent as StageLayoutView)?.Describe();

    /// <summary>
    /// Where the map is and what it would centre on, for <c>--shot</c> — or null when it is
    /// nowhere. Worth having precisely because a placement is not visible in one picture.
    /// </summary>
    public string? DescribeMap() => IsMapsActive || Map.IsOnStage ? Map.Describe() : null;

    /// <summary>What Android is doing and where, for <c>--shot</c> — or null when it is nowhere (ADR-0075).</summary>
    public string? DescribeAndroid() => IsAndroidActive || Android.IsOnStage ? Android.Describe() : null;

    /// <summary>
    /// Switch what sits below the stage.
    /// </summary>
    /// <remarks>
    /// Only the lower region changes. The stage keeps running underneath — that is the whole
    /// point of it being a layer rather than a screen (B2), and it is why choosing Settings
    /// does not stop the music.
    /// <para>
    /// MAPS is the one cell that does something when you are already on it: a second press is
    /// the map's size control (ADR-0062's addendum). It replaced a chip in the panel, because
    /// the nav strip is the only band comfortably reachable from the driver's seat and the cell
    /// was already under the thumb that had just arrived there.
    /// </para>
    /// </remarks>
    [RelayCommand]
    private void Navigate(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        if (IsMapsActive && string.Equals(destination, "MAPS", StringComparison.OrdinalIgnoreCase))
        {
            // On the stage there is no second size — the occupant has the whole picture — and
            // coming back is the only move the map has there. So that is what a second press
            // means, rather than nothing at all: a nav cell that ignores a press reads as broken.
            if (Map.IsOnStage)
            {
                Map.BringBackCommand.Execute(null);
            }
            else
            {
                Map.ResizeCommand.Execute(null);
            }

            return;
        }

        ActiveDestination = destination;
    }

    /// <summary>Outside temperature for the status strip, from the shared forecast.</summary>
    [ObservableProperty]
    private string _outsideText = "——°";

    /// <summary>What it is doing outside, in a word or two.</summary>
    [ObservableProperty]
    private string _conditionText = string.Empty;

    /// <summary>What the link is. Says "synthetic" plainly, because it is (ADR-0005).</summary>
    public string SourceLabel => "SYNTHETIC F-150";

    /// <summary>The scripted drive currently running.</summary>
    public string DriveLabel => _vehicle.DriveName.ToUpperInvariant();

    /// <summary>Everything that can go on the stage, including what is not built.</summary>
    /// <remarks>
    /// Observable and rebuilt in place, because the user's own apps (ADR-0024) can be added and
    /// removed while the dash is running — see <see cref="RebuildStageOptions"/>.
    /// </remarks>
    public ObservableCollection<StageOptionViewModel> StageOptions { get; } = [];

    /// <summary>
    /// The same options, in headed sections — SCREENS, WEB, APPS — for the picker. Rebuilt from
    /// <see cref="StageOptions"/> alongside it, so it follows a user app being added or removed.
    /// Built explicitly rather than with a grouped <c>CollectionView</c>, whose <c>GroupStyle</c>
    /// laid the sections out transposed.
    /// </summary>
    public ObservableCollection<StageGroup> StageGroups { get; } = [];

    /// <summary>
    /// Refill <see cref="StageOptions"/> from the built-ins plus the user's apps.
    /// </summary>
    /// <remarks>
    /// The running occupant is left alone — adding or removing an app must not restart what is
    /// on the stage — so this only rebuilds the list and re-marks which option is current by
    /// name. If the app currently showing was the one removed, it keeps running (it still closes
    /// with DashDeck); its launcher simply stops being highlighted.
    /// </remarks>
    private void RebuildStageOptions()
    {
        var current = Foreground;
        var currentName = current?.Name;

        StageOptions.Clear();

        foreach (var option in StageOption.FromLauncher(
            _launcher.Current, _videoPath, _clock, _vehicle.Signals, Sensors, Weather, Display, _userApps.Apps, _theme.Layouts,
            () => Dashboard is null ? null : new CardsStageOccupant(Dashboard)))
        {
            StageOptions.Add(new StageOptionViewModel(option));
        }

        foreach (var candidate in StageOptions)
        {
            candidate.IsCurrent = candidate.Name == currentName && current is not null;
        }

        // Re-form the headed sections in the order they first appear in the launcher file — the
        // built-in one lists screens, then web, then apps, so SCREENS / WEB / APPS.
        StageGroups.Clear();

        foreach (var group in StageOptions.GroupBy(o => o.GroupLabel))
        {
            StageGroups.Add(new StageGroup(group.Key, [.. group]));
        }

        RefreshQuickOptions();
    }

    /// <summary>The ones shown directly in the launcher row. The rest are behind the grid.</summary>
    public ObservableCollection<StageOptionViewModel> QuickStageOptions { get; } = [];

    /// <summary>How many buttons fit in the launcher row beside the grid button.</summary>
    private const int LauncherSlots = Stage.Launcher.StageLauncher.QuickBarSlots;

    /// <summary>
    /// Decide which options get a button in the row.
    /// </summary>
    /// <remarks>
    /// The rule that matters is the last one: whatever is currently on the stage always has
    /// a button, even when it would otherwise have overflowed into the grid. Without it the
    /// row goes dark while something is plainly playing, and the launcher stops being able
    /// to answer "what is on" — which is half of what it is for.
    /// </remarks>
    private void RefreshQuickOptions()
    {
        // The launcher file's quickBar, in its order (ADR-0038), or the first five available.
        var available = StageOptions.Where(o => o.IsAvailable).ToList();
        var shown = _launcher.Current
            .QuickBarFrom([.. available.Select(o => o.Name)])
            .Select(name => available.First(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var current = StageOptions.FirstOrDefault(o => o.IsCurrent);

        if (current is not null && !shown.Contains(current))
        {
            if (shown.Count < LauncherSlots)
            {
                shown.Add(current);
            }
            else
            {
                shown[^1] = current;
            }
        }

        QuickStageOptions.Clear();

        foreach (var option in shown)
        {
            QuickStageOptions.Add(option);
        }
    }

    /// <summary>The occupant in front — the screen if there is one, otherwise the source. What
    /// the name, the actions and the launcher highlight all reflect.</summary>
    private IStageOccupant? Foreground => _screen ?? _source;

    /// <summary>True when something is actually on the stage, in either slot.</summary>
    public bool StageHasOccupant => _screen is not null || _source is not null;

    /// <summary>
    /// Whether the occupant's view is shown right now.
    /// </summary>
    /// <remarks>
    /// Collapsed while the picker is open, and that is not cosmetic. Every occupant so far
    /// renders into a child window — LibVLC's video surface, WebView2's browser — and a
    /// child window draws over <em>all</em> WPF content regardless of z-order. Leaving the
    /// occupant visible would hide the picker behind it, which is precisely the bug that
    /// made a loaded stage impossible to change.
    /// <para>
    /// Video keeps playing underneath, so audio continues while you choose.
    /// </para>
    /// <para>
    /// Collapsed under the warning window too (ADR-0055), for the same reason: it would draw over it.
    /// </para>
    /// </remarks>
    public bool IsOccupantVisible =>
        StageContent is not null && !IsStagePickerOpen && !IsFullScreenOpen && !Warnings.ShowWindow;

    /// <summary>
    /// Whether the source's own host is the thing on screen — only when no screen is over it.
    /// </summary>
    /// <remarks>
    /// A backgrounded source stays in the tree but Collapsed (alive, still playing) so it is not
    /// reparented; this is what draws it when it is the one in front (ADR-0026).
    /// </remarks>
    public bool IsSourceVisible =>
        SourceContent is not null && _screen is null && !IsStagePickerOpen && !IsFullScreenOpen && !Warnings.ShowWindow;

    /// <summary>True when a card is open in the editor.</summary>
    public bool IsCardEditorOpen => Dashboard.IsCardEditorOpen;

    /// <summary>True when a component's full-screen detail is covering the dash.</summary>
    public bool IsComponentDetailOpen => Dashboard.IsComponentDetailOpen;

    /// <summary>True while the dash is being rearranged.</summary>
    public bool IsDashEditing => Dashboard.IsEditing;

    /// <summary>
    /// True when something is covering the stage entirely.
    /// </summary>
    /// <remarks>
    /// Settings was the first of these and is no longer the only one, so the special case it
    /// used to be has become the general rule Q17 described: a full-screen view takes all six
    /// bands, the stage keeps running underneath, and the nav stays on top so there is always
    /// a way back out.
    /// </remarks>
    public bool IsFullScreenOpen => IsSettingsActive || IsCardEditorOpen || IsComponentDetailOpen || IsMapFull || IsAndroidFull;

    /// <summary>
    /// Whether the cards are showing.
    /// </summary>
    /// <remarks>
    /// Not the same as being on the DASH destination, which is what it was first bound to —
    /// the card editor is opened <em>from</em> the dash and so leaves that destination
    /// active, and the two then rendered into the same region with the cards on top. The
    /// editor was fully drawn and completely invisible underneath them.
    /// </remarks>
    public bool IsDashVisible => IsDashActive && !IsCardEditorOpen && !IsComponentDetailOpen;

    /// <summary>
    /// Whether the climate panel is showing: CLIMATE, with no card editor or component detail
    /// over it — the cards are on the stage now (ADR-0041), so either can open from CLIMATE too.
    /// </summary>
    public bool IsClimateVisible => IsClimateActive && !IsCardEditorOpen && !IsComponentDetailOpen;

    /// <summary>Whether the Stereo panel is showing, on the same terms as the climate panel.</summary>
    public bool IsStereoVisible => false;

    /// <summary>
    /// Whether the destination strip is showing.
    /// </summary>
    /// <remarks>
    /// Replaced by the edit toolbar while the dash is being rearranged. The nav band is the
    /// only one reachable from the driver's seat, so the controls that are in use belong
    /// there — and navigating away mid-edit was never going to be useful anyway.
    /// </remarks>
    public bool IsNavVisible => !IsDashEditing;

    /// <summary>
    /// True when nothing occupies the stage. Rendered as an explicit empty state rather
    /// than filling the space with something invented (Q13).
    /// </summary>
    public bool StageIsEmpty => _screen is null && _source is null;

    /// <summary>What is on the stage, for the chip. Empty stages still say so.</summary>
    public string StageName => Foreground?.Name ?? "EMPTY";

    /// <summary>
    /// The occupant that is running but not on screen, if any — the one the status-strip pill
    /// names and taps back to.
    /// </summary>
    /// <remarks>
    /// Two ways an occupant runs unseen: a source is playing in the background while a screen is
    /// in front (ADR-0026), or the foreground occupant is hidden behind a full-screen view
    /// (F22/ADR-0025). The backgrounded source wins, because getting back to the music is the
    /// point; only when there is none does the pill offer the hidden foreground.
    /// </remarks>
    private IStageOccupant? HiddenOccupant =>
        _source is not null && _screen is not null ? _source
        : Foreground is not null && IsFullScreenOpen ? Foreground
        : null;

    /// <summary>True when something is running but not on screen — see <see cref="HiddenOccupant"/>.</summary>
    public bool StageRunningButHidden => HiddenOccupant is not null;

    /// <summary>What the pill says — the hidden occupant's name.</summary>
    public string HiddenOccupantName => HiddenOccupant?.Name ?? string.Empty;

    /// <summary>
    /// Go back to the running-but-hidden occupant from the status-strip pill: close whatever
    /// full-screen view is covering the stage, and if a source is playing in the background, bring
    /// it forward (F22/ADR-0025, ADR-0026).
    /// </summary>
    [RelayCommand]
    private void ReturnToStage()
    {
        if (Dashboard.IsCardEditorOpen)
        {
            Dashboard.CloseEditorCommand.Execute(null);
        }

        if (Dashboard.IsComponentDetailOpen)
        {
            Dashboard.CloseComponentDetailCommand.Execute(null);
        }

        ActiveDestination = "DASH";

        // A source playing behind a screen: drop the screen and let the source show again. Its
        // view stays put in its own host, so it is never reparented — it just stops being covered.
        if (_source is not null && _screen is not null)
        {
            _screen.Dispose();
            _screen = null;
            _screenOption = null;
            StageContent = null;
            AfterStageChange();
        }
    }

    /// <summary>
    /// How many of the six bands the stage takes. An occupant asks for what it needs;
    /// with none, the stage keeps four and the widgets get two.
    /// </summary>
    /// <remarks>
    /// Always four, unless a full-screen view has taken all six (Q17). Fixed rather than
    /// negotiated: letting each occupant choose meant the cards below moved between two rows
    /// and three whenever the stage changed, which reads as the dash rearranging itself under
    /// you while driving.
    /// </remarks>
    public int StageBands => IsFullScreenOpen ? 0 : BandGrid.StageBands;

    /// <summary>Whatever the stage did not take. Widget rows line up either way.</summary>
    public int WidgetBands => BandGrid.BandCount - StageBands;

    /// <summary>
    /// Tell the dashboard how much room it has.
    /// </summary>
    /// <remarks>
    /// It re-packs on the way in, so a three-band stage gets three rows of cards rather than
    /// two and a spare band (F9). Only ever synced while the dash is the visible destination:
    /// Settings and the card editor both take all six bands, and re-flowing the pages to fit
    /// a region nobody is looking at would rearrange the dash behind their back.
    /// </remarks>
    private void SyncWidgetBands()
    {
        // The cards are on the stage (ADR-0041), whose picture does not change size.
        if (!IsCardEditorOpen)
        {
            Dashboard.RegionHeight = BandGrid.StageOccupantHeight;
        }
    }

    /// <summary>
    /// What the stage occupant is actually doing, when it can say.
    /// </summary>
    /// <remarks>
    /// For <c>--shot</c>, which cannot photograph a video surface drawn into a child window.
    /// </remarks>
    public string? DescribeStage() =>
        _source is not null && _screen is not null
            ? $"{_screen.Describe()} · source backgrounded: {_source.Describe()}"
            : Foreground?.Describe();

    /// <summary>
    /// Open or close the picker.
    /// </summary>
    /// <remarks>
    /// Deliberately not a navigation destination. Navigation switches what sits
    /// <em>below</em> the stage (B3), and the nav strip already has an unsolved overflow
    /// problem past about five entries — spending one of them on something you set once
    /// and then leave would be a poor trade. The control lives on the thing it controls.
    /// </remarks>
    [RelayCommand]
    private void ToggleStagePicker() => IsStagePickerOpen = !IsStagePickerOpen;

    /// <summary>True when the occupant offers anything to do.</summary>
    public bool HasStageActions => StageActions.Count > 0;

    /// <summary>Run one of the occupant's actions and close the menu.</summary>
    [RelayCommand]
    private void RunStageAction(StageAction? action)
    {
        if (action is null)
        {
            return;
        }

        IsMenuOpen = false;
        action.Invoke();
    }

    /// <summary>Two taps to close DashDeck from the menu: the only way out without a keyboard.</summary>
    private readonly Shell.CloseConfirm _close = new(TimeSpan.FromSeconds(4));

    /// <summary>What ends the app. <see cref="App.RequestClose"/>; replaceable for tests.</summary>
    public Action ExitApplication { get; set; } = App.RequestClose;

    /// <summary>The menu's last item: what it does, and that it is armed.</summary>
    public string CloseCaption => _close.IsArmed(_clock.UtcNow) ? "TAP AGAIN TO CLOSE DASHDECK" : "CLOSE DASHDECK";

    /// <summary>True while a second tap would close — drawn in the accent, so the change is seen.</summary>
    public bool IsCloseArmed => _close.IsArmed(_clock.UtcNow);

    /// <summary>
    /// Close DashDeck — on the second tap within a few seconds.
    /// </summary>
    /// <remarks>
    /// Escape closes from a keyboard; on the truck there is none, and the menu's CLOSE used to
    /// only dismiss the menu, which tapping outside it already does.
    /// </remarks>
    [RelayCommand]
    private void CloseApp()
    {
        var close = _close.Tap(_clock.UtcNow);
        OnPropertyChanged(nameof(CloseCaption));
        OnPropertyChanged(nameof(IsCloseArmed));

        if (close)
        {
            IsMenuOpen = false;
            ExitApplication();
        }
    }

    /// <summary>Open or close the overflow menu.</summary>
    [RelayCommand]
    private void ToggleMenu()
    {
        _close.Disarm();
        OnPropertyChanged(nameof(CloseCaption));
        OnPropertyChanged(nameof(IsCloseArmed));

        // Re-read on the way in, because a caption can depend on state: the video occupant's
        // first item says PLAY or PAUSE, and a stale one is worse than no label.
        if (!IsMenuOpen)
        {
            StageActions = Foreground?.Actions ?? [];
        }

        IsMenuOpen = !IsMenuOpen;
    }

    /// <summary>
    /// Go to Settings from the menu.
    /// </summary>
    /// <remarks>
    /// Off the nav strip now. The strip is the only band reachable from the driver's seat, and
    /// spending a permanent quarter of it on something you set once and then leave was a poor
    /// trade — B3 always said the strip is the destination list, and Settings is not a
    /// destination you drive to.
    /// </remarks>
    [RelayCommand]
    private void OpenSettings()
    {
        IsMenuOpen = false;
        ActiveDestination = "SETTINGS";
    }

    /// <summary>Put the dash into edit mode from the menu.</summary>
    [RelayCommand]
    private void EditDash()
    {
        IsMenuOpen = false;

        // The cards are on the stage (ADR-0041): editing them starts by putting them there.
        var cards = _launcher.Current.Entries.FirstOrDefault(e => e.Type == Stage.Launcher.LauncherTypes.Cards)?.Name;
        if (cards is not null && !string.Equals(Foreground?.Name, cards, StringComparison.OrdinalIgnoreCase)
            && StageOptions.FirstOrDefault(o => string.Equals(o.Name, cards, StringComparison.OrdinalIgnoreCase)) is { } option)
        {
            SetStage(option);
        }

        if (IsSettingsActive)
        {
            ActiveDestination = "DASH";
        }

        if (!Dashboard.IsEditing)
        {
            Dashboard.ToggleEditCommand.Execute(null);
        }
    }

    /// <summary>Put something on the stage, or take everything off it.</summary>
    [RelayCommand]
    private void ChooseStage(StageOptionViewModel? option)
    {
        if (option is null || !option.IsAvailable)
        {
            return;
        }

        SetStage(option);
        IsStagePickerOpen = false;
    }

    private void SetStage(StageOptionViewModel option)
    {
        var incomingIsSource = option.AudioVisualSource;
        var frontIsSource = _screen is null && _source is not null;
        var incomingIsBackgroundSource =
            _source is not null && _screen is not null && ReferenceEquals(option, _sourceOption);

        var action = StageSwitch.Decide(
            Display.KeepStageAudio, frontIsSource, incomingIsSource, incomingIsBackgroundSource);

        switch (action)
        {
            case StageSwitchAction.BringForwardSource:
                // The backgrounded source is chosen again: drop the screen over it and let it
                // show. The source itself is never touched, so its audio and place hold.
                DisposeScreen();
                break;

            case StageSwitchAction.BackgroundSourceShowIncoming:
                // The source in front stays alive as the background; the chosen screen goes over
                // it. _source / _sourceOption already hold that source.
                _screen = option.Option.Create?.Invoke();
                _screenOption = _screen is null ? null : option;
                StageContent = _screen?.CreateView();
                break;

            case StageSwitchAction.ReplaceWithSource:
                // One source at a time: dispose the front screen and any background source, then
                // bring the new source up in its own host.
                DisposeScreen();
                DisposeSource();
                _source = option.Option.Create?.Invoke();
                _sourceOption = _source is null ? null : option;
                SourceContent = _source?.CreateView();
                break;

            case StageSwitchAction.ReplaceFrontKeepBackground:
                // Dispose only whatever is in front; a source already in the background keeps
                // playing. Then show the chosen (non-source) screen.
                if (_screen is not null)
                {
                    DisposeScreen();
                }
                else
                {
                    DisposeSource();
                }

                _screen = option.Option.Create?.Invoke();
                _screenOption = _screen is null ? null : option;
                StageContent = _screen?.CreateView();
                break;
        }

        AfterStageChange();
    }

    private void DisposeScreen()
    {
        _screen?.Dispose();
        _screen = null;
        _screenOption = null;
        StageContent = null;
    }

    private void DisposeSource()
    {
        _source?.Dispose();
        _source = null;
        _sourceOption = null;
        SourceContent = null;
    }

    /// <summary>Fire everything that depends on which occupants are on the stage.</summary>
    private void AfterStageChange()
    {
        var foreground = Foreground;

        // Read fresh, because a caption can depend on state — the video occupant's first item
        // says PLAY or PAUSE, and a stale one is worse than none.
        StageActions = foreground?.Actions ?? [];

        var currentName = foreground?.Name;
        foreach (var candidate in StageOptions)
        {
            candidate.IsCurrent = candidate.Name == currentName && foreground is not null;
        }

        // Recomputed after the flags, so a newly-chosen app that lives in the grid gets pulled
        // into the row rather than leaving it looking like nothing is on.
        RefreshQuickOptions();

        OnPropertyChanged(nameof(StageName));
        OnPropertyChanged(nameof(StageHasOccupant));
        OnPropertyChanged(nameof(StageIsEmpty));
        OnPropertyChanged(nameof(IsOccupantVisible));
        OnPropertyChanged(nameof(IsSourceVisible));
        OnPropertyChanged(nameof(StageRunningButHidden));
        OnPropertyChanged(nameof(HiddenOccupantName));
        OnPropertyChanged(nameof(StageBands));
        OnPropertyChanged(nameof(WidgetBands));

        // The stage can take the map away without asking: choosing anything else disposes the
        // occupant. Reconciled here, after every stage change, so the map's placement cannot
        // outlive the occupant that justified it — and not from the occupant's own Dispose,
        // which runs in the middle of SetStage and would re-enter it.
        if (Map.IsOnStage && !ReferenceEquals(_screenOption, MapStageOption))
        {
            Map.BringBackCommand.Execute(null);
        }

        // Android the same way: replaced on the stage, it goes back to full screen — quietly, without
        // taking the nav there. It keeps running (ADR-0075).
        if (Android.IsOnStage && !ReferenceEquals(_screenOption, AndroidStageOption))
        {
            Android.LeftTheStage();
        }

        // A new occupant can claim a different number of bands, which changes how many rows of
        // cards fit and therefore how they page.
        SyncWidgetBands();
    }

    /// <summary>
    /// Which location source to run, if any: a <c>--gps</c> override wins (a <c>host:port</c>, or
    /// <c>synthetic</c> for the desk), otherwise the persisted GPS setting. Null when GPS is off —
    /// and then the sensors are the tablet's alone, exactly as before (ADR-0027).
    /// </summary>
    private static Core.Location.ILocationSource? ResolveLocationSource(
        string? gpsOverride, DisplaySettings display, IClock clock)
    {
        // --gps wins: "synthetic", a COM port for Bluetooth, or a host:port for the network.
        if (!string.IsNullOrWhiteSpace(gpsOverride))
        {
            if (string.Equals(gpsOverride, "synthetic", StringComparison.OrdinalIgnoreCase))
            {
                return new Location.SyntheticLocationSource(clock);
            }

            if (IsSerialPort(gpsOverride))
            {
                return new Location.SerialNmeaLocationSource(gpsOverride.Trim(), clock);
            }

            return TryEndpoint(gpsOverride, out var oh, out var op)
                ? new Location.TcpNmeaLocationSource(oh, op, clock)
                : null;
        }

        if (!display.GpsEnabled)
        {
            return null;
        }

        // Bluetooth (the default) reads a paired phone's COM port; Network dials a host:port.
        if (string.Equals(display.GpsTransport, "Bluetooth", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(display.GpsSerialPort)
                ? null
                : new Location.SerialNmeaLocationSource(display.GpsSerialPort.Trim(), clock);
        }

        return TryEndpoint(display.GpsEndpoint, out var h, out var p)
            ? new Location.TcpNmeaLocationSource(h, p, clock)
            : null;
    }

    /// <summary>True for a Windows COM-port name, e.g. <c>COM7</c> — the Bluetooth override.</summary>
    private static bool IsSerialPort(string value) =>
        value.Trim().StartsWith("COM", StringComparison.OrdinalIgnoreCase);

    /// <summary>Split a <c>host:port</c> endpoint. False for anything that is not one.</summary>
    private static bool TryEndpoint(string? endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        var colon = endpoint.LastIndexOf(':');

        if (colon <= 0 || colon == endpoint.Length - 1)
        {
            return false;
        }

        host = endpoint[..colon].Trim();

        return host.Length > 0
            && int.TryParse(endpoint[(colon + 1)..].Trim(), out port)
            && port is > 0 and <= 65535;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Stop();

        Dashboard.Dispose();
        Sensors.Dispose();
        Warnings.Dispose();

        // The stage is a layer with its own lifecycle (B2) — it outlives navigation, but
        // not the shell. Both slots go: a backgrounded source is still ours to close.
        _screen?.Dispose();
        _source?.Dispose();
        (ClimateContent as IDisposable)?.Dispose();
        (ConsoleContent as IDisposable)?.Dispose();
        (MapContent as IDisposable)?.Dispose();

        // Android is asked to shut down and left to finish, so its snapshot is saved (ADR-0075).
        Android.Dispose();

        // Last, so the backlight is given back once nothing else is left to wait on.
        _brightness.Dispose();

        // The panel is just a view; the player and its ticker are the things holding resources.
    }

    private void Refresh()
    {
        // An armed CLOSE left alone goes back to harmless on its own.
        OnPropertyChanged(nameof(CloseCaption));
        OnPropertyChanged(nameof(IsCloseArmed));

        // The warning lights look on the same beat (ADR-0055).
        Warnings.Refresh();

        // Android gets the truck's newest position, and its frames picked back up if they dropped.
        Android.Beat();

        // IClock, never DateTimeOffset.Now — the convention holds in the UI too, so a
        // replayed drive shows the time the drive happened rather than the time you watched it.
        ClockText = _clock.UtcNow.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

        // Auto re-checks itself on the same beat as the clock: sunset does not need a
        // dedicated timer.
        _theme.Reevaluate();

        // The backlight follows the same answer, so palette and panel never disagree about
        // whether it is night (ADR-0068). Writes nothing unless the level wanted has changed.
        _brightness.Apply(_theme.IsNight);

        // The status strip carries the weather now, so it is refreshed on the same beat as
        // the clock. Reading, never fetching — WeatherService owns the one request.
        if (Weather.Report is { } report)
        {
            OutsideText = string.Create(CultureInfo.CurrentCulture, $"{report.Now.TemperatureC:0}°");
            ConditionText = Stage.Weather.Describe(report.Now.Code);
        }
        else
        {
            OutsideText = "——°";
            ConditionText = Weather.Status is "OFFLINE" ? "Weather unavailable" : "Fetching weather";
        }

        var measured = _vehicle.MeasuredRequestsPerSecond;
        RequestRateText = measured > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{measured:0.#} req/s")
            : "—— req/s";

        // The Sensors section shows live statuses, read on this beat and only while it is on
        // screen — forty rows of status text are not worth refreshing behind the dash.
        if (IsSettingsActive && Settings.IsSensors && Inventory.IsListing)
        {
            Inventory.Refresh();
        }

        // The phone's GPS readout, on the same terms: only while Display is the section being
        // looked at. It is what tells someone in the driver's seat whether the pairing works.
        if (IsSettingsActive && Settings.IsDisplay)
        {
            Settings.RefreshGps();
        }

        // The map's position readout, on the same terms — while MAPS is the destination, or
        // while the map is on the stage and so on screen whatever the nav says.
        if (IsMapsActive || Map.IsOnStage)
        {
            Map.Refresh();
        }

        // The banner the mockups have and the shell never showed. Sampled here, on the UI
        // thread, so nothing has to marshal a worker-thread event onto it.
        LinkState = _vehicle.LinkState;

        // Simulated → live can happen at any moment now (ADR-0034); the badge and the fallback
        // notice follow on the same beat.
        OnPropertyChanged(nameof(IsSimulated));
        OnPropertyChanged(nameof(AdapterFallbackReason));
        OnPropertyChanged(nameof(HasAdapterFallback));
        OnPropertyChanged(nameof(AdapterFallbackText));

        if (IsSettingsActive && Settings.IsVehicle)
        {
            Adapter.Refresh();
        }
    }
}





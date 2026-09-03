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
    private IStageOccupant? _stage;
    private readonly IClock _clock;
    private readonly ThemeService _theme;
    private readonly DispatcherTimer _timer;

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

    /// <summary>What the banner says. A fault is not the same as a pulled cable.</summary>
    public string LinkStatusText => LinkState is TransportState.Faulted
        ? "ADAPTER FAULT"
        : "ADAPTER LOST — RECONNECTING";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDashActive))]
    [NotifyPropertyChangedFor(nameof(IsStereoActive))]
    [NotifyPropertyChangedFor(nameof(IsClimateActive))]
    [NotifyPropertyChangedFor(nameof(IsSettingsActive))]
    [NotifyPropertyChangedFor(nameof(IsDestinationUnbuilt))]
    [NotifyPropertyChangedFor(nameof(StageBands))]
    [NotifyPropertyChangedFor(nameof(WidgetBands))]
    [NotifyPropertyChangedFor(nameof(IsOccupantVisible))]
    [NotifyPropertyChangedFor(nameof(IsFullScreenOpen))]
    [NotifyPropertyChangedFor(nameof(IsDashVisible))]
    private string _activeDestination = "DASH";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StageHasOccupant))]
    [NotifyPropertyChangedFor(nameof(StageIsEmpty))]
    [NotifyPropertyChangedFor(nameof(IsOccupantVisible))]
    private FrameworkElement? _stageContent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOccupantVisible))]
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

    public ShellViewModel(
        VehicleStack vehicle,
        IClock clock,
        ThemeService theme,
        WeatherService weather,
        string? videoPath = null,
        string? startOn = null)
    {
        _vehicle = vehicle;
        _clock = clock;
        _theme = theme;
        Weather = weather;

        Display = new DisplaySettings();

        // One service for the whole session: it holds the mount reference and any vehicle
        // declarations, so it must outlive whichever occupant happens to be on the stage.
        Sensors = new SensorService(
            SensorCatalog.FromFileOrEmpty(CatalogPath.Find("sensors.device.json")),
            vehicle.Signals,
            clock);

        // Settings owns levelling now, so it needs the sensors (ADR-0022).
        Settings = new SettingsViewModel(theme, Display, Sensors);

        StageOptions =
        [
            .. StageOption.All(videoPath, clock, vehicle.Signals, Sensors, weather, Display)
                .Select(o => new StageOptionViewModel(o)),
        ];

        RefreshQuickOptions();

        // The cards come from a file now, not from this constructor. Rates are still declared
        // honestly — the whole app shares one serialised link, and asking for more than you
        // need degrades everyone (ADR-0004) — but the honesty is the user's to keep, so the
        // editor shows what each card costs.
        //
        // Built here, and with no band count, for two ordering reasons that both showed up as
        // a null reference on startup: choosing the opening stage re-syncs the widget bands
        // and so needs this to exist, and StageBands reads IsCardEditorOpen, which reads this.
        // It starts on its own default and SyncWidgetBands settles it below.
        Dashboard = new DashboardViewModel(
            new CardValueFactory(vehicle.Signals, Sensors),
            ValueChoice.All(vehicle.Catalog, Sensors.Catalog));

        // Start on whatever was asked for at launch, and otherwise on the clock. The stage
        // is never empty now: an idle dash showing the time is more use than one announcing
        // that it has nothing to show.
        var opening = startOn is not null
            ? StageOptions.FirstOrDefault(o => string.Equals(o.Name, startOn, StringComparison.OrdinalIgnoreCase))
            : videoPath is not null
                ? StageOptions.First(o => o.Name == "VIDEO")
                : StageOptions.First(o => o.Name == "CLOCK");

        if (opening is not null)
        {
            SetStage(opening);
        }

        // Also for the case where nothing opened, so the dash is never packed against the
        // wrong number of bands.
        SyncWidgetBands();

        Dashboard.PropertyChanged += (_, e) =>
        {
            // The card editor is a full-screen view, so it claims the stage the way Settings
            // does (Q17). The stage keeps running underneath either way.
            if (e.PropertyName is nameof(DashboardViewModel.IsCardEditorOpen)
                or nameof(DashboardViewModel.IsEditing))
            {
                OnPropertyChanged(nameof(StageBands));
                OnPropertyChanged(nameof(WidgetBands));
                OnPropertyChanged(nameof(IsOccupantVisible));
                OnPropertyChanged(nameof(IsCardEditorOpen));
                OnPropertyChanged(nameof(IsDashVisible));
                OnPropertyChanged(nameof(IsFullScreenOpen));
                OnPropertyChanged(nameof(IsDashEditing));
                OnPropertyChanged(nameof(IsNavVisible));
                SyncWidgetBands();
            }
        };

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
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

    public bool IsDashActive => ActiveDestination == "DASH";

    public bool IsStereoActive => ActiveDestination == "STEREO";

    public bool IsClimateActive => ActiveDestination == "CLIMATE";

    public bool IsSettingsActive => ActiveDestination == "SETTINGS";

    /// <summary>
    /// True for destinations that exist in the nav but have nothing behind them yet.
    /// </summary>
    /// <remarks>
    /// Stereo and Climate are drawn because the nav is the destination list (B3) and an
    /// empty strip would say less. Both are parked — Climate in particular cannot simply be
    /// built, since C3 forbids taking over the factory HVAC (Q14).
    /// </remarks>
    public bool IsDestinationUnbuilt => IsStereoActive || IsClimateActive;

    /// <summary>
    /// Switch what sits below the stage.
    /// </summary>
    /// <remarks>
    /// Only the lower region changes. The stage keeps running underneath — that is the whole
    /// point of it being a layer rather than a screen (B2), and it is why choosing Settings
    /// does not stop the music.
    /// </remarks>
    [RelayCommand]
    private void Navigate(string? destination)
    {
        if (!string.IsNullOrWhiteSpace(destination))
        {
            ActiveDestination = destination;
        }
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
    public IReadOnlyList<StageOptionViewModel> StageOptions { get; }

    /// <summary>The ones shown directly in the launcher row. The rest are behind the grid.</summary>
    public ObservableCollection<StageOptionViewModel> QuickStageOptions { get; } = [];

    /// <summary>How many buttons fit in the launcher row beside the grid button.</summary>
    private const int LauncherSlots = 5;

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
        var available = StageOptions.Where(o => o.IsAvailable).ToList();
        var shown = available.Take(LauncherSlots).ToList();
        var current = StageOptions.FirstOrDefault(o => o.IsCurrent);

        if (current is not null && !shown.Contains(current))
        {
            shown[^1] = current;
        }

        QuickStageOptions.Clear();

        foreach (var option in shown)
        {
            QuickStageOptions.Add(option);
        }
    }

    /// <summary>True when something is actually on the stage.</summary>
    public bool StageHasOccupant => StageContent is not null;

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
    /// </remarks>
    public bool IsOccupantVisible =>
        StageContent is not null && !IsStagePickerOpen && !IsFullScreenOpen;

    /// <summary>True when a card is open in the editor.</summary>
    public bool IsCardEditorOpen => Dashboard.IsCardEditorOpen;

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
    public bool IsFullScreenOpen => IsSettingsActive || IsCardEditorOpen;

    /// <summary>
    /// Whether the cards are showing.
    /// </summary>
    /// <remarks>
    /// Not the same as being on the DASH destination, which is what it was first bound to —
    /// the card editor is opened <em>from</em> the dash and so leaves that destination
    /// active, and the two then rendered into the same region with the cards on top. The
    /// editor was fully drawn and completely invisible underneath them.
    /// </remarks>
    public bool IsDashVisible => IsDashActive && !IsCardEditorOpen;

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
    public bool StageIsEmpty => StageContent is null;

    /// <summary>What is on the stage, for the chip. Empty stages still say so.</summary>
    public string StageName => _stage?.Name ?? "EMPTY";

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
        if (IsDashActive && !IsCardEditorOpen)
        {
            Dashboard.WidgetBands = WidgetBands;
        }
    }

    /// <summary>
    /// What the stage occupant is actually doing, when it can say.
    /// </summary>
    /// <remarks>
    /// For <c>--shot</c>, which cannot photograph a video surface drawn into a child window.
    /// </remarks>
    public string? DescribeStage() => _stage?.Describe();

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

    /// <summary>Open or close the overflow menu.</summary>
    [RelayCommand]
    private void ToggleMenu()
    {
        // Re-read on the way in, because a caption can depend on state: the video occupant's
        // first item says PLAY or PAUSE, and a stale one is worse than no label.
        if (!IsMenuOpen)
        {
            StageActions = _stage?.Actions ?? [];
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
        ActiveDestination = "DASH";

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
        // The outgoing occupant goes away properly — video keeps decoding otherwise, and a
        // stage nobody can see is the worst possible consumer of a tablet's battery.
        _stage?.Dispose();

        _stage = option.Option.Create?.Invoke();
        StageContent = _stage?.CreateView();

        // Read every time the menu opens rather than cached, because a caption can depend on
        // state — the video occupant''s first item says PLAY or PAUSE.
        StageActions = _stage?.Actions ?? [];

        foreach (var candidate in StageOptions)
        {
            candidate.IsCurrent = ReferenceEquals(candidate, option) && _stage is not null;
        }

        // Recomputed after the flags, so a newly-chosen app that lives in the grid gets
        // pulled into the row rather than leaving it looking like nothing is on.
        RefreshQuickOptions();

        OnPropertyChanged(nameof(StageName));
        OnPropertyChanged(nameof(StageBands));
        OnPropertyChanged(nameof(WidgetBands));

        // A new occupant can claim a different number of bands, which changes how many rows
        // of cards fit and therefore how they page.
        SyncWidgetBands();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Stop();

        Dashboard.Dispose();
        Sensors.Dispose();

        // The stage is a layer with its own lifecycle (B2) — it outlives navigation, but
        // not the shell.
        _stage?.Dispose();
    }

    private void Refresh()
    {
        // IClock, never DateTimeOffset.Now — the convention holds in the UI too, so a
        // replayed drive shows the time the drive happened rather than the time you watched it.
        ClockText = _clock.UtcNow.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

        // Auto re-checks itself on the same beat as the clock: sunset does not need a
        // dedicated timer.
        _theme.Reevaluate();

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

        // The banner the mockups have and the shell never showed. Sampled here, on the UI
        // thread, so nothing has to marshal a worker-thread event onto it.
        LinkState = _vehicle.LinkState;
    }
}





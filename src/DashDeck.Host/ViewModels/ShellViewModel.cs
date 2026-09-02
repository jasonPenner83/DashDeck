using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Host.Stage;

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
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private string _clockText = "--:--";

    [ObservableProperty]
    private string _requestRateText = "—— req/s";

    [ObservableProperty]
    private string _activeDestination = "DASH";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StageHasOccupant))]
    [NotifyPropertyChangedFor(nameof(StageIsEmpty))]
    [NotifyPropertyChangedFor(nameof(IsOccupantVisible))]
    private FrameworkElement? _stageContent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOccupantVisible))]
    private bool _isStagePickerOpen;

    public ShellViewModel(
        VehicleStack vehicle,
        IClock clock,
        string? videoPath = null,
        string? startOn = null)
    {
        _vehicle = vehicle;
        _clock = clock;

        StageOptions = [.. StageOption.All(videoPath, clock).Select(o => new StageOptionViewModel(o))];

        RefreshQuickOptions();

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

        // Six widgets across two bands. Rates are declared honestly: the whole app shares
        // one serialised link, and asking for more than you need degrades everyone (ADR-0004).
        Widgets =
        [
            new SignalWidgetViewModel(vehicle.Signals, "SPEED", "vehicle.speed", SignalPriority.High, 4, "0"),
            new SignalWidgetViewModel(vehicle.Signals, "RPM", "engine.rpm", SignalPriority.High, 4, "0"),
            new SignalWidgetViewModel(vehicle.Signals, "COOLANT", "engine.coolantTemp", SignalPriority.Normal, 0.5, "0"),
            new SignalWidgetViewModel(vehicle.Signals, "FUEL", "fuel.levelPercent", SignalPriority.Low, 0.2, "0"),
            new SignalWidgetViewModel(vehicle.Signals, "ENGINE LOAD", "engine.load", SignalPriority.Normal, 2, "0"),
            new SignalWidgetViewModel(vehicle.Signals, "FUEL RATE", "engine.fuelRate", SignalPriority.Normal, 2, "0.0"),
        ];

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    /// <summary>The widgets filling the bands below the stage.</summary>
    public IReadOnlyList<SignalWidgetViewModel> Widgets { get; }

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
    public bool IsOccupantVisible => StageContent is not null && !IsStagePickerOpen;

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
    public int StageBands => _stage?.PreferredBands ?? 4;

    /// <summary>Whatever the stage did not take. Widget rows line up either way.</summary>
    public int WidgetBands => BandGrid.BandCount - StageBands;

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
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Stop();

        foreach (var widget in Widgets)
        {
            widget.Dispose();
        }

        // The stage is a layer with its own lifecycle (B2) — it outlives navigation, but
        // not the shell.
        _stage?.Dispose();
    }

    private void Refresh()
    {
        // IClock, never DateTimeOffset.Now — the convention holds in the UI too, so a
        // replayed drive shows the time the drive happened rather than the time you watched it.
        ClockText = _clock.UtcNow.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

        var measured = _vehicle.MeasuredRequestsPerSecond;
        RequestRateText = measured > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{measured:0.#} req/s")
            : "—— req/s";
    }
}

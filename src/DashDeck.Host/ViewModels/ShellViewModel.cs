using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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
    private readonly IStageOccupant? _stage;
    private readonly IClock _clock;
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private string _clockText = "--:--";

    [ObservableProperty]
    private string _requestRateText = "—— req/s";

    [ObservableProperty]
    private string _activeDestination = "DASH";

    public ShellViewModel(VehicleStack vehicle, IClock clock, IStageOccupant? stage = null)
    {
        _vehicle = vehicle;
        _clock = clock;
        _stage = stage;

        if (stage is not null)
        {
            StageContent = stage.CreateView();
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

    /// <summary>The occupant's view, or <see langword="null"/> when the stage is empty.</summary>
    public FrameworkElement? StageContent { get; }

    /// <summary>True when something is actually on the stage.</summary>
    public bool StageHasOccupant => StageContent is not null;

    /// <summary>
    /// True when nothing occupies the stage. Rendered as an explicit empty state rather
    /// than filling the space with something invented (Q13).
    /// </summary>
    public bool StageIsEmpty => StageContent is null;

    /// <summary>What is on the stage, for the chip. Empty stages still say their size.</summary>
    public string StageName => _stage?.Name ?? "EMPTY";

    /// <summary>
    /// How many of the six bands the stage takes. An occupant asks for what it needs;
    /// with none, the stage keeps four and the widgets get two.
    /// </summary>
    public int StageBands => _stage?.PreferredBands ?? 4;

    /// <summary>Whatever the stage did not take. Widget rows line up either way.</summary>
    public int WidgetBands => BandGrid.BandCount - StageBands;

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

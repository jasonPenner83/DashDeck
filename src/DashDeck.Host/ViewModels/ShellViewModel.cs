using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DashDeck.Abstractions;

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
    private readonly IClock _clock;
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private string _clockText = "--:--";

    [ObservableProperty]
    private string _requestRateText = "—— req/s";

    [ObservableProperty]
    private string _activeDestination = "DASH";

    public ShellViewModel(VehicleStack vehicle, IClock clock)
    {
        _vehicle = vehicle;
        _clock = clock;

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

    /// <summary>
    /// Nothing occupies the stage yet — no map, video or media component exists (Q13). The
    /// shell renders that as an explicit empty state rather than pretending otherwise.
    /// </summary>
    public bool StageHasOccupant => false;

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Stop();

        foreach (var widget in Widgets)
        {
            widget.Dispose();
        }
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

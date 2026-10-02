using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Vehicle;
using DashDeck.Vehicle.Diagnostics;

namespace DashDeck.Host.ViewModels;

/// <summary>What the Vehicle section needs to know about the running link. <see cref="VehicleStack"/> in the app.</summary>
public interface IAdapterStatus
{
    bool IsSimulated { get; }

    /// <summary>The adapter being read, or null while simulated.</summary>
    AdapterLocation? LiveAdapter { get; }

    /// <summary>The port being watched for an adapter while simulated.</summary>
    string? WatchedPort { get; }

    /// <summary>Why the chosen adapter is not being read, while it is watched for.</summary>
    string? FallbackReason { get; }

    /// <summary>Why the link last failed — dropped, not found, wrong rate — or null.</summary>
    string? LinkProblem { get; }

    AdapterChoice UseAdapterPort(string? port);
}

/// <summary>Lists ports and tests one. A seam, so the list is testable without serial hardware.</summary>
public interface IPortProbe
{
    IReadOnlyList<string> ListPorts();

    Task<PortTestResult> TestAsync(string port, int? knownBaudRate, CancellationToken ct);
}

/// <summary>The real ports: <see cref="SerialPortTransport"/>, with timeouts short enough to watch.</summary>
public sealed class SerialPortProbe : IPortProbe
{
    public IReadOnlyList<string> ListPorts() => Settings.DisplaySettings.AvailableSerialPorts();

    public Task<PortTestResult> TestAsync(string port, int? knownBaudRate, CancellationToken ct) =>
        PortTester.TestAsync(
            port,
            (name, rate) => new SerialPortTransport(name, rate)
            {
                ResponseTimeout = TimeSpan.FromSeconds(1.5),
                OpenTimeout = TimeSpan.FromSeconds(3),
            },
            knownBaudRate,
            ct);
}

/// <summary>One port in the tested list.</summary>
public sealed partial class PortRowViewModel(string port) : ObservableObject
{
    public string Port { get; } = port;

    /// <summary>One word for what is on it: ADAPTER, NO ADAPTER, IN USE, CONNECTED…</summary>
    [ObservableProperty]
    private string _label = "";

    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    private bool _isAdapter;

    [ObservableProperty]
    private bool _isTesting;

    /// <summary>The port the user chose — what the row highlight binds to.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The quality the label is drawn in: Live for an adapter, Unavailable for the rest.</summary>
    public SignalQuality Quality => IsAdapter ? SignalQuality.Live : SignalQuality.Unavailable;

    partial void OnIsAdapterChanged(bool value) => OnPropertyChanged(nameof(Quality));
}

/// <summary>
/// The OBD-II adapter in Settings ▸ Vehicle: where it is, and a list of tested ports to choose
/// it from (ADR-0034).
/// </summary>
/// <remarks>
/// The port used to be a text box beside a list of COM names, and "I typed the port and nothing
/// happened" had no way to be told apart from a wrong port, a busy port or a missing driver.
/// Now every port is opened briefly and asked who it is — adapter commands only, nothing to the
/// vehicle — and the answer is on the row: the adapter's identity, the rate it answered at and
/// the voltage at the OBD-II port, or why not. Choosing one applies at once while the dash is
/// simulated: it is watched for, and the dash goes live when it answers.
/// </remarks>
public sealed partial class AdapterPortsViewModel : ObservableObject
{
    private readonly IAdapterStatus _status;
    private readonly IPortProbe _probe;
    private readonly IClock _clock;
    private readonly Func<string> _getChosen;
    private readonly Action<string> _setChosen;
    private readonly Func<IReadOnlyCollection<string>> _reserved;
    private readonly Func<int?> _knownBaud;
    private readonly Action? _restart;
    private DateTimeOffset? _lastTested;

    /// <summary>How long a test stays fresh before opening the section tests again.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(1);

    public AdapterPortsViewModel(
        IAdapterStatus status,
        IPortProbe probe,
        IClock clock,
        Func<string> getChosen,
        Action<string> setChosen,
        Func<IReadOnlyCollection<string>> reserved,
        Func<int?> knownBaud,
        Action? restart = null)
    {
        _status = status;
        _probe = probe;
        _clock = clock;
        _getChosen = getChosen;
        _setChosen = setChosen;
        _reserved = reserved;
        _knownBaud = knownBaud;
        _restart = restart;
        Refresh();
    }

    /// <summary>The ports, as tested.</summary>
    public ObservableCollection<PortRowViewModel> Ports { get; } = [];

    /// <summary>True when there is a list to show.</summary>
    public bool HasPorts => Ports.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestPortsCommand))]
    private bool _isTesting;

    /// <summary>What the last test found, or how to start one.</summary>
    [ObservableProperty]
    private string _testSummary = "TEST PORTS opens each one briefly and asks what is on it.";

    /// <summary>Where the link is: LIVE, or SIMULATED and why.</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>The colour of <see cref="StatusText"/>: Live, Stale while watching, Simulated.</summary>
    [ObservableProperty]
    private SignalQuality _statusQuality = SignalQuality.Simulated;

    /// <summary>What the last choice did.</summary>
    [ObservableProperty]
    private string _choiceMessage = "";

    /// <summary>True when the last choice needs a restart to apply.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRestart))]
    private bool _restartNeeded;

    public bool CanRestart => _restart is not null;

    /// <summary>True when RESTART NOW should be offered.</summary>
    public bool ShowRestart => RestartNeeded && CanRestart;

    [RelayCommand]
    private void Restart() => _restart?.Invoke();

    /// <summary>Re-read the link. Called on the shell's one-second beat while the section is open.</summary>
    public void Refresh()
    {
        if (_status.LiveAdapter is { } live)
        {
            StatusText = $"LIVE — {live.Identity} on {live.Port} @ {live.BaudRate.ToString(CultureInfo.InvariantCulture)} baud"
                + (live.Moved ? " · found it on a new port" : "");
            StatusQuality = SignalQuality.Live;
        }
        else if (!_status.IsSimulated)
        {
            // Live, but the cable is out or the adapter is resetting: the link is finding it again.
            StatusText = "LIVE — the adapter dropped; reconnecting by itself. Readings are stale until it answers."
                + (_status.LinkProblem is { } problem ? $" Last: {problem}." : "");
            StatusQuality = SignalQuality.Stale;
        }
        else if (_status.WatchedPort is not null)
        {
            StatusText = $"SIMULATED — {_status.FallbackReason ?? $"looking for the adapter on {_status.WatchedPort}"}.";
            StatusQuality = SignalQuality.Stale;
        }
        else
        {
            StatusText = "SIMULATED — no adapter chosen. Test the ports and pick one.";
            StatusQuality = SignalQuality.Simulated;
        }

        var chosen = _getChosen();

        foreach (var row in Ports)
        {
            row.IsSelected = string.Equals(row.Port, chosen, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Test the ports unless a test is running or one finished within <see cref="Freshness"/>.</summary>
    public Task TestIfStaleAsync() =>
        IsTesting || (_lastTested is { } at && _clock.UtcNow - at < Freshness)
            ? Task.CompletedTask
            : TestPortsAsync();

    private bool CanTest() => !IsTesting;

    /// <summary>Open every port briefly and ask what is on it — all at once, each row filling in as it answers.</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestPortsAsync()
    {
        IsTesting = true;
        TestSummary = "Testing…";

        try
        {
            var chosen = _getChosen().Trim();
            var reserved = new HashSet<string>(_reserved(), StringComparer.OrdinalIgnoreCase);
            var present = _probe.ListPorts();
            var names = present.ToList();

            if (chosen.Length > 0 && !names.Contains(chosen, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(chosen);
            }

            Ports.Clear();

            foreach (var name in names.OrderBy(PortNumber).ThenBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                Ports.Add(new PortRowViewModel(name));
            }

            OnPropertyChanged(nameof(HasPorts));
            Refresh();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));

            await Task.WhenAll(Ports.Select(row => TestRowAsync(row, present, reserved, timeout.Token)));

            var adapters = Ports.Count(r => r.IsAdapter);
            TestSummary = Ports.Count == 0
                ? "No serial ports at all. Is the adapter's USB cable in, and its driver installed?"
                : adapters == 0
                    ? $"{Ports.Count} port{(Ports.Count == 1 ? "" : "s")} tested — no adapter answered. Is it plugged in?"
                    : $"{Ports.Count} port{(Ports.Count == 1 ? "" : "s")} tested — {adapters} adapter{(adapters == 1 ? "" : "s")}. Tap one to use it.";
        }
        finally
        {
            _lastTested = _clock.UtcNow;
            IsTesting = false;
        }
    }

    private async Task TestRowAsync(PortRowViewModel row, IReadOnlyList<string> present, HashSet<string> reserved, CancellationToken ct)
    {
        if (reserved.Contains(row.Port))
        {
            (row.Label, row.Detail) = ("PHONE GPS", "DashDeck's phone GPS — not tested.");
            return;
        }

        if (_status.LiveAdapter is { } live && string.Equals(live.Port, row.Port, StringComparison.OrdinalIgnoreCase))
        {
            row.IsAdapter = true;
            (row.Label, row.Detail) = ("CONNECTED", $"{live.Identity} · {live.BaudRate.ToString(CultureInfo.InvariantCulture)} baud · being read now");
            return;
        }

        if (!present.Contains(row.Port, StringComparer.OrdinalIgnoreCase))
        {
            (row.Label, row.Detail) = ("NOT HERE", "Not present right now — plug the adapter in. DashDeck keeps looking for it.");
            return;
        }

        row.IsTesting = true;
        row.Label = "TESTING…";

        try
        {
            var result = await _probe.TestAsync(row.Port, _knownBaud(), ct);

            row.IsAdapter = result.IsAdapter;
            row.Label = result.Outcome switch
            {
                PortTestOutcome.Adapter => "ADAPTER",
                PortTestOutcome.InUse => "IN USE",
                PortTestOutcome.NoAdapter => "NO ADAPTER",
                PortTestOutcome.Skipped => "SKIPPED",
                _ => "WON'T OPEN",
            };
            row.Detail = result.Detail;
        }
        catch (OperationCanceledException)
        {
            (row.Label, row.Detail) = ("NO ANSWER", "Took too long to answer.");
        }
        finally
        {
            row.IsTesting = false;
        }
    }

    /// <summary>COM3 before COM10.</summary>
    private static int PortNumber(string port) =>
        int.TryParse(new string(port.Where(char.IsDigit).ToArray()), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;

    /// <summary>Use a port.</summary>
    [RelayCommand]
    private void Choose(PortRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        _setChosen(row.Port);
        Apply(_status.UseAdapterPort(row.Port), row);
        Refresh();
    }

    /// <summary>No adapter: the simulator, and nothing watched.</summary>
    [RelayCommand]
    private void UseSimulator()
    {
        _setChosen("");
        Apply(_status.UseAdapterPort(null), null);
        Refresh();
    }

    private void Apply(AdapterChoice outcome, PortRowViewModel? row)
    {
        RestartNeeded = outcome is AdapterChoice.NextLaunch;

        ChoiceMessage = outcome switch
        {
            AdapterChoice.Watching when row is { IsAdapter: true } =>
                $"Using {row.Port}. The dash switches to live data as soon as the adapter answers — no restart.",
            AdapterChoice.Watching =>
                $"Using {row!.Port}. It didn't answer as an adapter when tested; DashDeck keeps trying it and switches to live data when it does.",
            AdapterChoice.AlreadyConnected => $"{row?.Port} is the adapter being read now.",
            AdapterChoice.NextLaunch => "Saved. The dash is reading another adapter now, so this applies at the next launch.",
            _ => "Using the simulator. No adapter is opened.",
        };
    }
}

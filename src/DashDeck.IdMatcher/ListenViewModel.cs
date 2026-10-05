using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Core.Discovery.Hunt;
using DashDeck.Vehicle;
using DashDeck.Vehicle.Elm;
using DashDeck.Vehicle.Monitor;

namespace DashDeck.IdMatcher;

/// <summary>
/// The LISTEN tab (ADR-0054): hear the bus silently, do something in the truck, see what changed.
/// </summary>
/// <remarks>
/// It holds the adapter itself, so it cannot run with the tap: one program, one adapter. Nothing is
/// sent on the bus — the adapter is set up with AT commands only and listens with
/// <c>ATCSM1</c>, acknowledging nothing (ADR-0044).
/// </remarks>
public sealed partial class ListenViewModel : ObservableObject, IDisposable
{
    private readonly Func<(string Port, int Baud)?> _adapter;
    private readonly Func<bool> _tapRunning;
    private readonly Func<int?> _measuredPins311;
    private readonly IClock _clock;
    private CancellationTokenSource? _stop;
    private Task? _run;
    private CanMonitor? _monitor;
    private int? _rate;

    /// <param name="adapter">The port and baud chosen at the top of the window, or null when none is.</param>
    /// <param name="tapRunning">True while the tap holds the adapter.</param>
    /// <param name="measuredPins311">The pins 3/11 rate the user's vehicle file gives, or null.</param>
    /// <param name="clock">Time, for the saved capture's name.</param>
    public ListenViewModel(Func<(string Port, int Baud)?> adapter, Func<bool> tapRunning, Func<int?> measuredPins311, IClock clock)
    {
        _adapter = adapter;
        _tapRunning = tapRunning;
        _measuredPins311 = measuredPins311;
        _clock = clock;
    }

    public BusWatch Watch { get; private set; } = new();

    public ObservableCollection<WatchedId> Ids { get; } = [];

    public ObservableCollection<BroadcastCandidate> Ranked { get; } = [];

    [ObservableProperty]
    private WatchedId? _selectedId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListenButton))]
    private bool _isListening;

    public string ListenButton => IsListening ? "STOP LISTENING" : "LISTEN";

    /// <summary>Listen on OBD pins 3 and 11 rather than the main bus.</summary>
    [ObservableProperty]
    private bool _onPins311;

    /// <summary>Show only identifiers with something changed since MARK.</summary>
    [ObservableProperty]
    private bool _onlyChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoiseButton))]
    private bool _learningNoise;

    public string NoiseButton => LearningNoise ? "DONE LEARNING NOISE" : "LEARN NOISE";

    [ObservableProperty]
    private string _summary = "LISTEN hears the bus without sending anything. Then: LEARN NOISE while nothing happens, MARK (F5), do one thing in the truck, and see what changed.";

    [ObservableProperty]
    private string _rankSummary = "To narrow down: hold the truck one way and press STATE A (F6), the other way and STATE B (F7). Two or three of each, then RANK (F8).";

    [RelayCommand]
    private async Task ToggleListenAsync()
    {
        if (IsListening)
        {
            await StopAsync();
            return;
        }

        if (_tapRunning())
        {
            Summary = "The tap holds the adapter. STOP TAP first — one program, one adapter, and listening shares it with nothing.";
            return;
        }

        if (_adapter() is not { } adapter)
        {
            Summary = "Choose the adapter's port and baud at the top of the window first.";
            return;
        }

        Watch = new BusWatch();
        Ids.Clear();
        Ranked.Clear();
        OnlyChanged = false;
        LearningNoise = false;
        _stop = new CancellationTokenSource();
        IsListening = true;
        Summary = $"Opening {adapter.Port}…";
        _run = RunAsync(adapter.Port, adapter.Baud, OnPins311 ? CanBus.Ms : CanBus.Hs, _stop.Token);
    }

    private async Task RunAsync(string port, int baud, CanBus bus, CancellationToken ct)
    {
        var transport = new SerialPortTransport(port, baud);
        try
        {
            // AT commands only: the adapter is reset and set up; nothing reaches the bus.
            var adapter = new ElmAdapter(transport);
            await adapter.InitializeAsync(ct);
            _monitor = new CanMonitor(transport, _clock);

            _rate = null;
            if (bus == CanBus.Ms)
            {
                _rate = _measuredPins311();
                if (_rate is null)
                {
                    Summary = "Finding the rate of pins 3/11 by listening — silently, nothing is sent…";
                    _rate = await _monitor.DetectPins311Async(ct);
                    if (_rate is null)
                    {
                        Summary = "Nothing heard on pins 3/11 at 125 or 500 kbit/s. Is the ignition on?";
                        return;
                    }
                }
            }

            Summary = Listening(bus);
            await foreach (var frame in _monitor.ListenAsync(bus, null, ct, _rate))
            {
                Watch.Add(frame);
            }

            if (_monitor.Problem is { } problem && !ct.IsCancellationRequested)
            {
                Summary = $"Listening stopped: {problem}";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or ArgumentException)
        {
            Summary = $"Could not listen on {port}: {ex.Message} Close DashDeck, the ID hunter, SerialTap and FORScan first.";
        }
        finally
        {
            try
            {
                if (_monitor is { } monitor && transport.State == TransportState.Connected)
                {
                    using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await monitor.RestoreAsync(restore.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or TimeoutException)
            {
            }

            await transport.DisposeAsync();
            _monitor = null;
        }
    }

    private string Listening(CanBus bus) =>
        bus == CanBus.Hs ? "Listening to HS-CAN, silently." : $"Listening to pins 3/11 at {_rate / 1000} kbit/s, silently.";

    private async Task StopAsync()
    {
        _stop?.Cancel();
        if (_run is { } run)
        {
            await Task.WhenAny(run, Task.Delay(3000));
        }

        _stop?.Dispose();
        _stop = null;
        _run = null;
        IsListening = false;
        Summary = $"Stopped. {Watch.Frames} frames heard on {Ids.Count} identifiers. SAVE CAPTURE keeps them.";
    }

    [RelayCommand]
    private void ToggleNoise()
    {
        LearningNoise = !LearningNoise;
        Watch.LearnNoise(LearningNoise);
        Summary = LearningNoise
            ? "Learning noise: leave everything as it is. Whatever moves now (counters, engine values) is marked ~ and ignored after. Press again when done — 10 to 20 seconds is plenty."
            : "Noise learned. Now MARK (F5), do one thing in the truck, and look for [brackets].";
    }

    [RelayCommand]
    private void Mark()
    {
        if (LearningNoise)
        {
            ToggleNoise();
        }

        Watch.Mark();
        OnlyChanged = true;
        Summary = $"MARK at {_clock.UtcNow.ToLocalTime():HH:mm:ss}. Do one thing in the truck — open the tailgate, press the wheel-heat button. Changed bytes show in [brackets]; MARK again to start over.";
    }

    [RelayCommand]
    private void HoldA() => Hold("A", 0);

    [RelayCommand]
    private void HoldB() => Hold("B", 1);

    private void Hold(string label, double state)
    {
        Watch.HoldState(label, state);
        var phases = Watch.Phases;
        RankSummary = $"Holding {label} (closed or off for A, open or on for B — keep it the same each time). States so far: {string.Join(" ", phases.Select(p => p.Label))}. Wait a couple of seconds in each.";
    }

    [RelayCommand]
    private void Rank()
    {
        Watch.EndState();
        var ranked = Watch.Rank();
        Ranked.Clear();
        foreach (var candidate in ranked.Take(40))
        {
            Ranked.Add(candidate);
        }

        var phases = Watch.Phases;
        RankSummary = ranked.Count == 0
            ? phases.Count < 2 || phases.Select(p => p.State).Distinct().Count() < 2
                ? "Hold at least one A and one B first."
                : "Nothing told the states apart. Hold each a little longer, or try the other bus."
            : $"{ranked.Count} field{(ranked.Count == 1 ? "" : "s")} told {phases.Count} states apart; best first. One that separates every A from every B, steady within each, is the one.";
    }

    [RelayCommand]
    private void ClearStates()
    {
        Watch.ClearStates();
        Ranked.Clear();
        RankSummary = "States cleared.";
    }

    [RelayCommand]
    private void SaveCapture()
    {
        var frames = Watch.AllFrames();
        if (frames.Count == 0)
        {
            Summary = "Nothing heard yet to save.";
            return;
        }

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DashDeck", "listen");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"listen-{_clock.UtcNow.ToLocalTime():yyyyMMdd-HHmmss}.csv");
        using (var writer = new StreamWriter(path))
        {
            writer.WriteLine("ms,bus,id,data");
            foreach (var f in frames)
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{f.At.TotalMilliseconds:0},{(f.Bus == CanBus.Ms ? "pins311" : "hs")},{f.IdText},{f.DataText}"));
            }

            foreach (var phase in Watch.Phases)
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"# state {phase.Label} {phase.Start.TotalMilliseconds:0}-{phase.End.TotalMilliseconds:0} ms"));
            }
        }

        Summary = $"Saved {frames.Count} frames to {path}. It may hold the VIN — keep it out of the repository.";
    }

    /// <summary>Redraw the list. Called on the window's timer.</summary>
    public void Refresh()
    {
        // A listen that ended by itself — the port would not open, nothing on pins 3/11.
        if (IsListening && _run is { IsCompleted: true })
        {
            IsListening = false;
        }

        var selected = SelectedId?.Id;
        var rows = Watch.Snapshot().Where(r => !OnlyChanged || r.ChangedSinceMark != 0 || r.NewSinceMark).ToList();

        // Rows changed in place where they can be, so the list does not jump about.
        for (var i = 0; i < rows.Count; i++)
        {
            if (i < Ids.Count)
            {
                if (Ids[i] != rows[i])
                {
                    Ids[i] = rows[i];
                }
            }
            else
            {
                Ids.Add(rows[i]);
            }
        }

        while (Ids.Count > rows.Count)
        {
            Ids.RemoveAt(Ids.Count - 1);
        }

        SelectedId = selected is { } id ? Ids.FirstOrDefault(r => r.Id == id) : null;
    }

    partial void OnOnlyChangedChanged(bool value) => Refresh();

    public void Dispose()
    {
        _stop?.Cancel();
        _stop?.Dispose();
    }
}

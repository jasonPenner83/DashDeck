using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Core.Discovery.Matching;
using DashDeck.Vehicle.Tap;
using Microsoft.Win32;

namespace DashDeck.IdMatcher;

/// <summary>
/// The ID matcher (ADR-0050): DashDeck's signals and which need the truck's identifier; the traffic
/// FORScan makes through the built-in tap; and matching one to the other.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IClock _clock = SystemClock.Instance;
    private readonly IdentifierTable _table = new();
    private readonly TrafficReader _reader = new();
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<IdentifierKey, IdentifierRow> _rows = [];
    private readonly object _feedGate = new();

    private SignalCatalog? _standard;
    private IReadOnlyList<VehiclePack> _packs = [];
    private IReadOnlySet<byte>? _supported;
    private CancellationTokenSource? _tapStop;
    private Task? _tapRun;
    private SerialPort? _serial;
    private StreamWriter? _tapLog;

    public MainViewModel()
    {
        _reader.Observed += _table.Add;

        foreach (var unit in Enum.GetValues<ShownUnit>())
        {
            Units.Add(new UnitChoice(unit, UnitConversion.Label(unit)));
        }

        _selectedUnit = Units.First(u => u.Unit == ShownUnit.Fahrenheit);
        OverlayPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DashDeck", "signals.user.json");
        RefreshPorts();
        LoadCatalog();

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    // ── The tap ───────────────────────────────────────────────────────────────

    public ObservableCollection<string> Ports { get; } = [];

    [ObservableProperty]
    private string _selectedPort = "";

    [ObservableProperty]
    private string _baudText = "115200";

    [ObservableProperty]
    private string _listenText = "35000";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TapButton))]
    private bool _isTapRunning;

    public string TapButton => IsTapRunning ? "STOP TAP" : "START TAP";

    [ObservableProperty]
    private string _status = "Pick the adapter's port and START TAP, or open a saved tap log (Ctrl+O).";

    [RelayCommand]
    private void RefreshPorts()
    {
        var chosen = SelectedPort;
        Ports.Clear();
        foreach (var port in SerialPort.GetPortNames().Distinct().OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal))
        {
            Ports.Add(port);
        }

        SelectedPort = Ports.Contains(chosen) ? chosen : Ports.FirstOrDefault() ?? "";
    }

    [RelayCommand]
    private async Task ToggleTapAsync()
    {
        if (IsTapRunning)
        {
            await StopTapAsync();
            return;
        }

        if (!int.TryParse(BaudText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var baud) ||
            !int.TryParse(ListenText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var listen) ||
            string.IsNullOrWhiteSpace(SelectedPort))
        {
            Status = "Choose a port, and a number for the baud rate and the listening port.";
            return;
        }

        try
        {
            _serial = new SerialPort(SelectedPort, baud, Parity.None, 8, StopBits.One) { Handshake = Handshake.None, DtrEnable = true, RtsEnable = true };
            _serial.Open();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException)
        {
            _serial?.Dispose();
            _serial = null;
            Status = $"Could not open {SelectedPort}: {ex.Message} Close DashDeck, the ID hunter, SerialTap and FORScan first.";
            return;
        }

        // Every session is also saved as a tap log, so it can be opened and matched again later.
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DashDeck", "tap");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"tap-{_clock.UtcNow.ToLocalTime():yyyyMMdd-HHmmss}.log");
        _tapLog = new StreamWriter(path, append: false, System.Text.Encoding.UTF8) { AutoFlush = true };
        _tapLog.WriteLine($"# SerialTap  {SelectedPort} @ {baud} baud  listening on 127.0.0.1:{listen}  started {_clock.UtcNow.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}  (IdMatcher)");
        var log = _tapLog;

        var recorder = new TapRecorder(_clock, line => log.WriteLine(line));
        recorder.Line += line =>
        {
            lock (_feedGate)
            {
                _reader.Feed(line);
            }
        };

        var bridge = new TapBridge(_serial.BaseStream, recorder, new IPEndPoint(IPAddress.Loopback, listen));
        _tapStop = new CancellationTokenSource();
        var token = _tapStop.Token;
        _tapRun = Task.Run(() => bridge.RunAsync(token), token);
        IsTapRunning = true;
        Status = $"Holding {SelectedPort} at {baud} baud. In FORScan: Settings ▸ Connection, type WiFi, IP 127.0.0.1, port {listen}; then connect. Saving to {path}";

        _ = _tapRun.ContinueWith(
            t => Application.Current.Dispatcher.Invoke(async () =>
            {
                if (IsTapRunning)
                {
                    Status = t.Exception is { } ex ? $"The tap stopped: {ex.GetBaseException().Message}" : "The tap stopped: the adapter went away.";
                    await StopTapAsync();
                }
            }),
            TaskScheduler.Default);
    }

    private async Task StopTapAsync()
    {
        IsTapRunning = false;
        _tapStop?.Cancel();

        // Closing the port is what ends a serial read on Windows; the bridge allows for it.
        try
        {
            _serial?.Close();
        }
        catch (IOException)
        {
        }

        if (_tapRun is { } run)
        {
            await Task.WhenAny(run, Task.Delay(2000));
        }

        _serial?.Dispose();
        _serial = null;
        _tapLog?.Dispose();
        _tapLog = null;
        _tapRun = null;
        Status = "Tap stopped. The session is saved as a tap log.";
    }

    [RelayCommand]
    private async Task OpenTapLogAsync()
    {
        if (IsTapRunning)
        {
            Status = "Stop the tap before opening a saved log.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Open a tap log",
            Filter = "Tap logs (*.log)|*.log|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DashDeck", "tap"),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ClearTraffic();
        var text = await File.ReadAllTextAsync(dialog.FileName);
        await Task.Run(() =>
        {
            lock (_feedGate)
            {
                foreach (var line in TapLogFile.Read(text))
                {
                    _reader.Feed(line);
                }
            }
        });

        Status = $"Opened {Path.GetFileName(dialog.FileName)}: {_table.Snapshot().Count} identifiers heard.";
        Tick();
    }

    [RelayCommand]
    private void ClearTraffic()
    {
        lock (_feedGate)
        {
            _table.Clear();
        }

        _rows.Clear();
        Identifiers.Clear();
        Samples.Clear();
        Candidates.Clear();
        _supported = null;
        RefreshStandings();
    }

    // ── DashDeck's signals ────────────────────────────────────────────────────

    public ObservableCollection<SignalRow> Signals { get; } = [];

    [ObservableProperty]
    private bool _onlyNeedingId = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetText))]
    private SignalRow? _selectedSignal;

    [ObservableProperty]
    private string _overlayPath = "";

    [ObservableProperty]
    private string _catalogStatus = "";

    public string TargetText => SelectedSignal is { } s
        ? $"Pairing with {s.Id} — {s.Name} ({(s.Unit.Length > 0 ? s.Unit : "no unit")}). Esc to stop pairing."
        : "Not pairing: matches become new signals. Pick a DashDeck signal on the left to pair one.";

    partial void OnOnlyNeedingIdChanged(bool value) => RefreshStandings();

    partial void OnSelectedSignalChanged(SignalRow? value)
    {
        if (value is null)
        {
            return;
        }

        NameText = value.Name;

        // FORScan shows what its settings say; keep the unit chosen unless it is of another kind.
        var targetKind = UnitConversion.Parse(value.Unit);
        if (targetKind != ShownUnit.None && UnitConversion.ToMetric(0, targetKind).Unit != UnitConversion.ToMetric(0, SelectedUnit.Unit).Unit)
        {
            SelectedUnit = Units.First(u => u.Unit == targetKind);
        }
    }

    partial void OnOverlayPathChanged(string value) => RefreshStandings();

    private void LoadCatalog()
    {
        var folder = FindCatalog();
        if (folder is null)
        {
            CatalogStatus = "DashDeck's catalog was not found beside this program — the signal list is empty.";
            return;
        }

        try
        {
            _standard = SignalCatalog.FromFile(Path.Combine(folder, "signals.obd2-standard.json"));
            var (packs, problems) = VehiclePacks.LoadFolder(Path.Combine(folder, "vehicles"));
            _packs = packs;
            CatalogStatus = $"{_standard.Definitions.Count} standard signals, {packs.Sum(p => p.Signals.Count)} from {packs.Count} vehicle pack(s)." +
                (problems.Count > 0 ? " " + string.Join(" ", problems) : "");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            CatalogStatus = $"DashDeck's catalog would not load: {ex.Message}";
        }

        RefreshStandings();
    }

    private IReadOnlyList<SignalDefinition> ReadOverlay(out string? problem)
    {
        problem = null;
        if (!File.Exists(OverlayPath))
        {
            return [];
        }

        try
        {
            return SignalCatalog.ParseList(File.ReadAllText(OverlayPath));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            problem = $"Your overlay would not read: {ex.Message}";
            return [];
        }
    }

    private void RefreshStandings()
    {
        if (_standard is null)
        {
            return;
        }

        var overlay = ReadOverlay(out var problem);
        var standings = SignalPairing.Stand(_standard, _packs, overlay, _supported);
        var selectedId = SelectedSignal?.Id;

        Signals.Clear();
        foreach (var standing in standings.Where(s => !OnlyNeedingId || s.NeedsId))
        {
            Signals.Add(new SignalRow(standing));
        }

        SelectedSignal = selectedId is null ? null : Signals.FirstOrDefault(s => s.Id == selectedId);
        var needing = standings.Count(s => s.NeedsId);
        SignalSummary = $"{needing} of {standings.Count} need the truck's ID" +
            (_supported is null ? " — standard PIDs the truck lacks show once FORScan connects through the tap." : ".") +
            (problem is null ? "" : " " + problem);
    }

    [ObservableProperty]
    private string _signalSummary = "";

    [RelayCommand]
    private void StopPairing() => SelectedSignal = null;

    [RelayCommand]
    private void BrowseOverlay()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Where DashDeck reads your signals (signals.user.json)",
            FileName = "signals.user.json",
            Filter = "JSON (*.json)|*.json",
            OverwritePrompt = false,
            InitialDirectory = Path.GetDirectoryName(OverlayPath),
        };

        if (dialog.ShowDialog() == true)
        {
            OverlayPath = dialog.FileName;
        }
    }

    private static string? FindCatalog()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "catalog");
            if (File.Exists(Path.Combine(candidate, "signals.obd2-standard.json")))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return null;
    }

    // ── The traffic ───────────────────────────────────────────────────────────

    public ObservableCollection<IdentifierRow> Identifiers { get; } = [];

    [ObservableProperty]
    private IdentifierRow? _selectedIdentifier;

    partial void OnSelectedIdentifierChanged(IdentifierRow? value)
    {
        Samples.Clear();
        Candidates.Clear();
    }

    /// <summary>Select the identifier heard most recently for the first time: what FORScan just started asking.</summary>
    [RelayCommand]
    private void SelectNewest()
    {
        if (Identifiers.OrderByDescending(r => r.Stats.FirstSeen).FirstOrDefault() is { } newest)
        {
            SelectedIdentifier = newest;
        }
    }

    private void Tick()
    {
        var now = _clock.UtcNow;

        foreach (var stats in _table.Snapshot())
        {
            if (!_rows.TryGetValue(stats.Key, out var row))
            {
                row = new IdentifierRow(stats);
                _rows[stats.Key] = row;
                Identifiers.Add(row);
            }

            row.Refresh(now);
        }

        if (_supported is null && SignalPairing.SupportedPids(_table) is { } supported)
        {
            _supported = supported;
            RefreshStandings();
        }

        if (SelectedIdentifier is { } selected && Candidates.Count > 0)
        {
            RefreshReadsNow(selected);
        }
    }

    // ── Matching, live ────────────────────────────────────────────────────────

    public ObservableCollection<UnitChoice> Units { get; } = [];

    [ObservableProperty]
    private UnitChoice _selectedUnit;

    [ObservableProperty]
    private string _nameText = "";

    [ObservableProperty]
    private string _shownText = "";

    public ObservableCollection<SampleRow> Samples { get; } = [];

    public ObservableCollection<CandidateRow> Candidates { get; } = [];

    [ObservableProperty]
    private CandidateRow? _selectedCandidate;

    [ObservableProperty]
    private string _matchHint = "Select an identifier, type the value FORScan shows, press Enter. Again as it changes.";

    /// <summary>Take the value typed as FORScan shows it, beside the selected identifier's answer right now.</summary>
    [RelayCommand]
    private void AddSample()
    {
        if (SelectedIdentifier is not { } row)
        {
            MatchHint = "Select an identifier first (Ctrl+N picks the newest).";
            return;
        }

        var typed = ShownText.Trim().Replace(',', '.');
        if (!double.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            MatchHint = "Type the number FORScan shows, then Enter.";
            return;
        }

        var payload = row.Stats.LastPayload;
        if (payload.Length == 0)
        {
            MatchHint = "That identifier has no answer yet.";
            return;
        }

        var (value, factor, _) = UnitConversion.ToMetric(number, SelectedUnit.Unit);
        var sample = new Sample(_clock.UtcNow, payload, value, UnitConversion.Tolerance(typed, number) * factor);
        Samples.Add(new SampleRow(sample, $"{typed} {SelectedUnit.Label}", Convert.ToHexString(payload)));
        ShownText = "";
        Refit();
    }

    [RelayCommand]
    private void ClearSamples()
    {
        Samples.Clear();
        Candidates.Clear();
        MatchHint = "Samples cleared.";
    }

    private void Refit()
    {
        var candidates = ScalingFitter.FromSamples([.. Samples.Select(s => s.Sample)]);
        var unit = UnitConversion.ToMetric(0, SelectedUnit.Unit).Unit;
        Candidates.Clear();

        foreach (var candidate in candidates)
        {
            Candidates.Add(new CandidateRow(candidate, "", unit));
        }

        SelectedCandidate = Candidates.FirstOrDefault();
        var distinct = Samples.Select(s => s.Raw).Distinct().Count();

        MatchHint = Candidates.Count == 0
            ? "Nothing fits every sample. Check the unit, or that this is the identifier FORScan is reading."
            : distinct < 3
                ? $"{Candidates.Count} fit. Let the value change and add more — three different raw values settle it."
                : $"{Candidates.Count} fit; the top one is well supported. Ctrl+Enter accepts it.";

        if (SelectedIdentifier is { } row)
        {
            RefreshReadsNow(row);
        }
    }

    private void RefreshReadsNow(IdentifierRow row)
    {
        var payload = row.Stats.LastPayload;
        for (var i = 0; i < Candidates.Count; i++)
        {
            var c = Candidates[i];
            var reads = c.Candidate.Apply(payload) is { } v ? $"{v.ToString("0.##", CultureInfo.InvariantCulture)} {c.Unit}" : "";
            if (reads != c.ReadsNow)
            {
                var selected = ReferenceEquals(SelectedCandidate, c);
                Candidates[i] = c with { ReadsNow = reads };
                if (selected)
                {
                    SelectedCandidate = Candidates[i];
                }
            }
        }
    }

    /// <summary>Accept the selected candidate for the selected identifier — paired with the selected DashDeck signal, if any.</summary>
    [RelayCommand]
    private void Accept()
    {
        if (SelectedIdentifier is not { } row || SelectedCandidate is not { } candidate)
        {
            MatchHint = "Select an identifier and a candidate first.";
            return;
        }

        var name = NameText.Trim().Length > 0 ? NameText.Trim() : row.Key.ToString();
        var evidence = $"{Samples.Count} value{(Samples.Count == 1 ? "" : "s")} typed from FORScan, {candidate.Candidate.DistinctRaw} different raw";
        AddAccepted(new AcceptedMatch(row.Key, name, candidate.Candidate, candidate.Unit, evidence), SelectedSignal?.Definition);
        row.MatchedName = name;
        Samples.Clear();
        Candidates.Clear();
        MatchHint = $"Accepted {name}. Next: select another value in FORScan, then Ctrl+N.";
    }

    // ── Matching, from a PID log ──────────────────────────────────────────────

    public ObservableCollection<LogRow> LogRows { get; } = [];

    [ObservableProperty]
    private string _logSummary = "Record FORScan's PID log while the tap runs, then import it here (Ctrl+I).";

    [RelayCommand]
    private async Task ImportCsvAsync()
    {
        if (_table.Snapshot().Count == 0)
        {
            LogSummary = "There is no traffic yet. Run the tap while FORScan logs, or open the tap log from that session first.";
            return;
        }

        var dialog = new OpenFileDialog { Title = "Import FORScan's PID log", Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var log = ForscanCsv.Parse(await File.ReadAllTextAsync(dialog.FileName));
        LogSummary = "Matching…";
        var result = await Task.Run(() =>
        {
            lock (_feedGate)
            {
                return LogMatcher.Match(log, _table);
            }
        });

        LogRows.Clear();
        foreach (var column in result.Columns)
        {
            LogRows.Add(new LogRow(column, UnitConversion.ToMetric(0, UnitConversion.Parse(column.Column.Unit)).Unit));
        }

        var matched = result.Columns.Count(c => c.Best is not null);
        LogSummary = $"{Path.GetFileName(dialog.FileName)}: {matched} of {result.Columns.Count} columns matched, " +
            $"lined up at {result.LogStart.ToLocalTime():HH:mm:ss.fff}" + (log.ClockTimes ? " (times of day)" : " (counted from the log's start)") +
            (log.Problems.Count > 0 ? ". " + string.Join(" ", log.Problems) : ".") +
            " Ticked rows (R² ≥ 0.98) are accepted with ACCEPT TICKED.";
    }

    [RelayCommand]
    private void AcceptTicked()
    {
        foreach (var row in LogRows.Where(r => r.Accept && r.Match.Best is not null).ToList())
        {
            var (key, scaling) = row.Match.Best!.Value;
            var metric = ScalingFitter.ToMetric(scaling, UnitConversion.Parse(row.Match.Column.Unit));
            var evidence = $"FORScan PID log \"{row.Match.Column.Name}\", {row.Match.Column.Points.Count} points, R² {scaling.R2:0.0000}";
            AddAccepted(new AcceptedMatch(key, row.Match.Column.Name, metric, row.Unit, evidence), null);
            row.Accept = false;
            if (_rows.TryGetValue(key, out var idRow))
            {
                idRow.MatchedName = row.Match.Column.Name;
            }
        }
    }

    // ── Accepted, and saving to DashDeck ──────────────────────────────────────

    public ObservableCollection<AcceptedRow> Accepted { get; } = [];

    [ObservableProperty]
    private AcceptedRow? _selectedAccepted;

    [ObservableProperty]
    private string _saveStatus = "";

    private void AddAccepted(AcceptedMatch match, SignalDefinition? target)
    {
        if (target is not null && !SignalPairing.UnitsAgree(match.Unit, target.Decode.Unit))
        {
            SaveStatus = $"Note: matched in {match.Unit}, but {target.Id} is kept in {target.Decode.Unit}. Check the unit before saving.";
        }

        var row = new AcceptedRow(match, target);
        Accepted.Add(row);
        SelectedAccepted = row;
    }

    /// <summary>Pair the selected accepted match with the selected DashDeck signal.</summary>
    [RelayCommand]
    private void PairSelected()
    {
        if (SelectedAccepted is { } row && SelectedSignal is { } signal)
        {
            row.Target = signal.Definition;
            if (!SignalPairing.UnitsAgree(row.Match.Unit, signal.Unit))
            {
                SaveStatus = $"Note: matched in {row.Match.Unit}, but {signal.Id} is kept in {signal.Unit}.";
            }
        }
    }

    [RelayCommand]
    private void RemoveAccepted()
    {
        if (SelectedAccepted is { } row)
        {
            Accepted.Remove(row);
        }
    }

    /// <summary>Write the accepted matches into the overlay DashDeck reads, replacing any with the same id.</summary>
    [RelayCommand]
    private void SaveToDashDeck()
    {
        if (Accepted.Count == 0)
        {
            SaveStatus = "Nothing accepted yet.";
            return;
        }

        var overlay = ReadOverlay(out var problem).ToList();
        if (problem is not null)
        {
            SaveStatus = problem + " Not saved, so nothing of yours is lost.";
            return;
        }

        foreach (var definition in Accepted.Select(a => a.ToDefinition()))
        {
            overlay.RemoveAll(d => d.Id == definition.Id);
            overlay.Add(definition);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OverlayPath)!);
            File.WriteAllText(OverlayPath, SignalCatalog.ToJson(overlay));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveStatus = $"Could not write {OverlayPath}: {ex.Message}";
            return;
        }

        SaveStatus = $"Saved {Accepted.Count} to {OverlayPath}. DashDeck reads it at its next launch (on the tablet: Settings ▸ Sensors ▸ RESTART NOW). Confirm each with TEST there.";
        RefreshStandings();
    }

    /// <summary>The accepted matches as vehicle pack entries: on the clipboard, and in a file.</summary>
    [RelayCommand]
    private void ExportPack()
    {
        if (Accepted.Count == 0)
        {
            SaveStatus = "Nothing accepted yet.";
            return;
        }

        var text = MatchExport.ToPackEntries(Accepted.Select(a => (a.ToDefinition(),
            $"{a.Match.Key} — matched to FORScan's \"{a.Match.Name}\": {a.Match.Scaling.Formula} {a.Match.Unit}. {a.Match.Evidence}. Confirm with TEST before committing.")));

        Clipboard.SetText(text);

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DashDeck", "matches");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"pack-entries-{_clock.UtcNow.ToLocalTime():yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, text);
        SaveStatus = $"Copied {Accepted.Count} vehicle pack entries to the clipboard, and saved {path}. Send that file to add them to the F-150 pack.";
    }

    public void Dispose()
    {
        _timer.Stop();
        _tapStop?.Cancel();

        try
        {
            _serial?.Close();
        }
        catch (IOException)
        {
        }

        _serial?.Dispose();
        _tapLog?.Dispose();
        _tapStop?.Dispose();
    }
}

/// <summary>A unit in the drop-down.</summary>
public sealed record UnitChoice(ShownUnit Unit, string Label)
{
    public override string ToString() => Label;
}

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Core;
using DashDeck.Core.Catalog;
using DashDeck.Core.Discovery;
using DashDeck.Host.Sensors;
using DashDeck.Host.Settings;
using DashDeck.Vehicle;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// What the Sensors section needs from the running vehicle pipeline. <see cref="VehicleStack"/>
/// in the app; a fake in the tests.
/// </summary>
public interface ISignalInventorySource
{
    /// <summary>The catalog as it ships.</summary>
    SignalCatalog Shipped { get; }

    /// <summary>The catalog the pipeline is running — shipped plus the overlay read at launch.</summary>
    SignalCatalog Catalog { get; }

    /// <summary>Why the overlay was dropped at launch, when it was.</summary>
    string? OverlayError { get; }

    /// <summary>Latest readings. Read only — the inventory never declares demand.</summary>
    IVehicleSignals Signals { get; }

    /// <summary>What polling has learned about one signal.</summary>
    SignalPollStatus StatusOf(string signalId);

    /// <summary>One question to the adapter, outside the plan.</summary>
    Task<PidResponse> ProbeAsync(PidRequest request, CancellationToken ct);

    /// <summary>The vehicle packs laid over the catalog — where module names come from (ADR-0035).</summary>
    IReadOnlyList<VehiclePack> ActivePacks { get; }

    /// <summary>True while the synthetic truck is answering, so a sweep's results can say so.</summary>
    bool IsSimulated { get; }
}

/// <summary>Where a definition comes from.</summary>
public enum SignalOrigin
{
    /// <summary>The shipped catalog, untouched.</summary>
    Shipped,

    /// <summary>The user's file, with an id the shipped catalog does not have.</summary>
    Yours,

    /// <summary>The user's file, correcting a shipped definition with the same id.</summary>
    Override,
}

/// <summary>One vehicle signal in the inventory.</summary>
public sealed partial class SignalRowViewModel(SignalDefinition definition, SignalOrigin origin, bool pending) : ObservableObject
{
    public SignalDefinition Definition { get; } = definition;

    public string Id => Definition.Id;

    public string Caption => Definition.Name.ToUpperInvariant();

    public SignalOrigin Origin { get; } = origin;

    /// <summary>True when the file says something the running pipeline does not have yet.</summary>
    public bool IsPending { get; } = pending;

    /// <summary>The quiet second line: id, bus, the request on the wire, and the module it goes to.</summary>
    public string Detail => string.Create(
        CultureInfo.InvariantCulture,
        $"{Definition.Id}  ·  {(Definition.Bus is CanBus.Ms ? "MS" : "HS")}  ·  {Definition.Mode:X2} {(Definition.Pid <= 0xFF ? Definition.Pid.ToString("X2", CultureInfo.InvariantCulture) : Definition.Pid.ToString("X4", CultureInfo.InvariantCulture))}{(Definition.ModuleAddress is { } module ? $"  ·  module {module:X3}" : "")}");

    /// <summary>A tag for anything that is not plain shipped data.</summary>
    public string OriginLabel => Origin switch
    {
        SignalOrigin.Yours => "YOURS",
        SignalOrigin.Override => "CORRECTED",
        _ => "",
    };

    /// <summary>What the truck has said, in a word or a number.</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>The quality the status is drawn in — the same four colours as every card.</summary>
    [ObservableProperty]
    private SignalQuality _quality = SignalQuality.Unavailable;

    /// <summary>
    /// Re-read the status. Read only: the inventory never asks the truck for a value, because
    /// listing forty signals must not quietly put forty signals into the plan (ADR-0004).
    /// </summary>
    public void Refresh(ISignalInventorySource vehicle)
    {
        if (IsPending)
        {
            StatusText = "NEXT LAUNCH";
            Quality = SignalQuality.Unavailable;
            return;
        }

        var value = vehicle.Signals.Current(Id);
        var status = vehicle.StatusOf(Id);

        (StatusText, Quality) = status switch
        {
            SignalPollStatus.Retired => ("NO ANSWER", SignalQuality.Unavailable),
            SignalPollStatus.NotAsked => ("NOT ASKED", SignalQuality.Unavailable),
            _ when value.IsUsable => (Reading(value), value.Quality),
            _ when value.Quality is SignalQuality.Stale => (Reading(value), SignalQuality.Stale),
            _ => ("WAITING", SignalQuality.Unavailable),
        };
    }

    private static string Reading(SignalValue value) =>
        string.Create(CultureInfo.CurrentCulture, $"{value.Value:0.##} {value.Unit}").Trim();
}

/// <summary>One function group of signals, as the picker groups them.</summary>
public sealed record SignalGroupViewModel(string Name, IReadOnlyList<SignalRowViewModel> Rows)
{
    public string Header => $"{Name.ToUpperInvariant()}  ·  {Rows.Count}";
}

/// <summary>One tablet sensor, listed for reference — it is hardware, not something to define.</summary>
public sealed partial class TabletSensorRowViewModel(SensorDefinition definition) : ObservableObject
{
    public SensorDefinition Definition { get; } = definition;

    public string Caption => Definition.Name.ToUpperInvariant();

    /// <summary>Which hardware, and which vehicle signal would take over from it.</summary>
    public string Detail => Definition.Prefer is { } prefer
        ? $"{Definition.Id}  ·  {Definition.Source.ToString().ToUpperInvariant()}  ·  truck first: {prefer}"
        : $"{Definition.Id}  ·  {Definition.Source.ToString().ToUpperInvariant()}";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private SignalQuality _quality = SignalQuality.Unavailable;

    public void Refresh(SensorService sensors)
    {
        var reading = sensors.Read(Definition.Id);
        StatusText = reading.IsUsable
            ? string.Create(CultureInfo.CurrentCulture, $"{reading.Value:0.#} {Definition.Unit}  ·  {reading.Source}").Trim()
            : reading.Source;
        Quality = reading.Quality;
    }
}

/// <summary>A PID the truck says it supports and the catalog does not define.</summary>
public sealed record MissingPidViewModel(int Pid, CanBus Bus, string Name, bool HasStandardDecode)
{
    public string Caption => Name.ToUpperInvariant();

    public string Detail => string.Create(
        CultureInfo.InvariantCulture,
        $"{(Bus is CanBus.Ms ? "MS" : "HS")}  ·  01 {Pid:X2}  ·  {(HasStandardDecode ? "standard formula known" : "formula to be worked out")}");
}

/// <summary>
/// The Sensors section: every value DashDeck can show, where each comes from, and the tools to
/// find and define the ones it cannot show yet (ADR-0032).
/// </summary>
/// <remarks>
/// Three jobs, in the order a person reaches for them.
/// <list type="bullet">
/// <item><b>Show.</b> Every vehicle signal, grouped as the card picker groups them, with what
/// the truck has said about each — a value, <c>NOT ASKED</c> because nothing on screen wants
/// it, <c>NO ANSWER</c> because the truck refused it — and every tablet sensor with the source
/// it is reading from. Status only: listing a signal never declares demand for it.</item>
/// <item><b>Find.</b> SCAN asks the truck which mode 01 PIDs it supports and sets that against
/// the catalog, both ways: PIDs it supports that nothing defines, and defined signals it says
/// it does not support.</item>
/// <item><b>Define.</b> Add, correct and remove definitions in the user's overlay file, with a
/// TEST that asks the truck before anything is saved. Applied at the next launch, like every
/// other setting that shapes the pipeline — and a RESTART is one tap away.</item>
/// </list>
/// </remarks>
public sealed partial class SensorInventoryViewModel : ObservableObject
{
    private readonly ISignalInventorySource _vehicle;
    private readonly UserSignalStore _store;
    private readonly SensorService? _sensors;
    private readonly Action? _restart;
    private readonly IReadOnlyList<SignalDefinition> _loadedOverlay;
    private List<SignalRowViewModel> _rows = [];

    public SensorInventoryViewModel(
        ISignalInventorySource vehicle,
        UserSignalStore store,
        SensorService? sensors = null,
        Action? restart = null,
        DiscoveryStore? discovery = null,
        IClock? clock = null)
    {
        _vehicle = vehicle;
        _store = store;
        _sensors = sensors;
        _restart = restart;

        // What the pipeline was started with — the yardstick for "needs a restart". Taken from
        // the running catalog rather than the file, so a dropped overlay reads as pending too.
        _loadedOverlay = [.. store.Definitions.Where(d =>
            vehicle.Catalog.TryGet(d.Id, out var running) && running == d)];

        TabletSensors = sensors is null
            ? []
            : [.. sensors.Catalog.Definitions.Select(d => new TabletSensorRowViewModel(d))];

        // Scans and sweeps are kept across launches (discovery.json), so a restart does not cost
        // another minute parked to see what the truck already said.
        Modules = new ModuleDiscoveryViewModel(vehicle, DefineDiscovered, discovery, clock);

        store.Changed += (_, _) => Rebuild();
        Rebuild();
    }

    // ── Show ──────────────────────────────────────────────────────────────────

    /// <summary>The vehicle signals, grouped by function, after the filter.</summary>
    public ObservableCollection<SignalGroupViewModel> Groups { get; } = [];

    /// <summary>The tablet's own sensors and the phone's GPS (ADR-0017, ADR-0027).</summary>
    public IReadOnlyList<TabletSensorRowViewModel> TabletSensors { get; }

    /// <summary>True when there are tablet sensors to list.</summary>
    public bool HasTabletSensors => TabletSensors.Count > 0;

    /// <summary>Narrows the list by name, id or group as it is typed.</summary>
    [ObservableProperty]
    private string _filter = "";

    partial void OnFilterChanged(string value) => Regroup();

    /// <summary>One line totting it up.</summary>
    [ObservableProperty]
    private string _summary = "";

    /// <summary>Where the user's file is. Shown, so it can be found and backed up.</summary>
    public string UserFilePath => _store.Path;

    /// <summary>
    /// Why the overlay is not in effect, or why the file could not be read or written — or
    /// null. Never silent: a dropped overlay looks exactly like signals that vanished.
    /// </summary>
    public string? Problem => _store.LastError is { } storeError
        ? $"Your signal file could not be read or written: {storeError}"
        : _vehicle.OverlayError is { } overlayError
            ? $"Your signal file was not applied at launch, so only the shipped catalog is running. {overlayError}"
            : null;

    /// <summary>True when the file differs from what the pipeline was started with.</summary>
    public bool RestartNeeded =>
        _store.Definitions.Count != _loadedOverlay.Count
        || _store.Definitions.Any(d => !_loadedOverlay.Contains(d));

    /// <summary>Whether this build can restart itself; tests and the dev path cannot.</summary>
    public bool CanRestart => _restart is not null;

    [RelayCommand]
    private void Restart() => _restart?.Invoke();

    /// <summary>Rebuild the rows from the shipped catalog and the file.</summary>
    private void Rebuild()
    {
        var shipped = _vehicle.Shipped;
        var (merged, _) = VehicleStack.ApplyOverlay(shipped, _store.Definitions);

        _rows = [.. merged.Definitions.Select(d =>
        {
            var origin = !_store.Contains(d.Id) ? SignalOrigin.Shipped
                : shipped.TryGet(d.Id, out _) ? SignalOrigin.Override
                : SignalOrigin.Yours;

            var pending = !_vehicle.Catalog.TryGet(d.Id, out var running) || running != d;
            return new SignalRowViewModel(d, origin, pending);
        })];

        // A PID defined since the scan is not missing any more.
        foreach (var found in Missing.Where(m => _rows.Any(r =>
            r.Definition.Mode == 0x01 && r.Definition.Bus == m.Bus && r.Definition.Pid == m.Pid)).ToList())
        {
            Missing.Remove(found);
        }

        Regroup();
        Refresh();

        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(RestartNeeded));
    }

    private void Regroup()
    {
        var filter = Filter.Trim();

        var visible = _rows.Where(r => filter.Length == 0
            || r.Definition.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || r.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || r.Definition.Category.Contains(filter, StringComparison.OrdinalIgnoreCase));

        Groups.Clear();

        foreach (var group in visible
            .GroupBy(r => r.Definition.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            Groups.Add(new SignalGroupViewModel(group.Key, [.. group.OrderBy(r => r.Definition.Name, StringComparer.OrdinalIgnoreCase)]));
        }
    }

    /// <summary>Re-read every status. Called on the shell's one-second beat while the section is open.</summary>
    public void Refresh()
    {
        foreach (var row in _rows)
        {
            row.Refresh(_vehicle);
        }

        if (_sensors is not null)
        {
            foreach (var row in TabletSensors)
            {
                row.Refresh(_sensors);
            }
        }

        var answering = _rows.Count(r => r.Quality is SignalQuality.Live or SignalQuality.Simulated);
        var yours = _rows.Count(r => r.Origin is not SignalOrigin.Shipped);

        Summary = string.Create(
            CultureInfo.CurrentCulture,
            $"{_rows.Count} vehicle signals  ·  {answering} answering now  ·  {yours} yours  ·  {TabletSensors.Count} tablet sensors");
    }

    // ── Find ──────────────────────────────────────────────────────────────────

    /// <summary>PIDs the truck supports that nothing in the catalog defines.</summary>
    public ObservableCollection<MissingPidViewModel> Missing { get; } = [];

    /// <summary>Catalog signals the truck says it does not support.</summary>
    public ObservableCollection<SignalRowViewModel> Unsupported { get; } = [];

    /// <summary>What the last scan found, or how to start one.</summary>
    [ObservableProperty]
    private string _scanStatus = "Ask the truck which standard PIDs it supports, and compare with the catalog.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _isScanning;

    /// <summary>True once a scan has finished, so its results block shows.</summary>
    [ObservableProperty]
    private bool _hasScanned;

    private bool CanScan() => !IsScanning;

    /// <summary>
    /// Ask both buses which mode 01 PIDs they support, then set that against the catalog.
    /// </summary>
    /// <remarks>
    /// A dozen requests at most, through the same serialised adapter the plan uses. MS-CAN is
    /// asked too and usually says nothing — the body modules there are not emissions ECUs —
    /// which is reported as such rather than hidden, because it is the honest answer to "why
    /// did the scan not find tyre pressures".
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        IsScanning = true;
        ScanStatus = "Scanning…";

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var hs = await PidScanner.ScanAsync(_vehicle.ProbeAsync, CanBus.Hs, timeout.Token);
            var ms = await PidScanner.ScanAsync(_vehicle.ProbeAsync, CanBus.Ms, timeout.Token);

            ApplyScan([hs, ms]);
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "The scan ran out of time. Is the adapter connected?";
        }
        finally
        {
            IsScanning = false;
            HasScanned = true;
        }
    }

    /// <summary>Set scan results against the catalog. Separate from the scan so it can be tested directly.</summary>
    internal void ApplyScan(IReadOnlyList<PidScanResult> results)
    {
        Missing.Clear();
        Unsupported.Clear();

        var defined = _rows
            .Where(r => r.Definition.Mode == 0x01)
            .ToLookup(r => (r.Definition.Bus, (int)r.Definition.Pid));

        foreach (var result in results.Where(r => r.Answered))
        {
            foreach (var pid in result.Supported.Where(p => !defined.Contains((result.Bus, p))))
            {
                Missing.Add(new MissingPidViewModel(
                    pid,
                    result.Bus,
                    StandardPids.NameOf(pid),
                    StandardPids.TryGet(pid, out var entry) && entry.HasDecode));
            }

            // Only standard-range PIDs on a bus that answered can be called unsupported: a bus
            // that said nothing has not said no, and Ford's own modes are not in the bitmaps.
            foreach (var row in _rows.Where(r => r.Definition.Mode == 0x01
                && r.Definition.Bus == result.Bus
                && r.Definition.Pid <= 0xFF
                && !result.Supported.Contains(r.Definition.Pid)))
            {
                Unsupported.Add(row);
            }
        }

        var lines = results.Select(r => r.Answered
            ? $"{BusName(r.Bus)}: {r.Supported.Count} PIDs supported"
            : $"{BusName(r.Bus)}: {r.Problem}");

        ScanStatus = string.Join("  ·  ", lines)
            + $"  ·  {Missing.Count} not in the catalog  ·  {Unsupported.Count} defined but unsupported";
    }

    private static string BusName(CanBus bus) => bus is CanBus.Ms ? "MS-CAN" : "HS-CAN";

    // ── Define ────────────────────────────────────────────────────────────────

    /// <summary>The open editor, or null while the list is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(IsListing))]
    private SignalEditorViewModel? _editor;

    public bool IsEditing => Editor is not null;

    public bool IsListing => Editor is null;

    /// <summary>Correct a shipped signal, or edit one of yours.</summary>
    [RelayCommand]
    private void Edit(SignalRowViewModel? row)
    {
        if (row is not null)
        {
            Editor = MakeEditor(row.Definition, isNew: false, row.Origin, note: null);
        }
    }

    /// <summary>Start a signal from nothing — the way a Ford PID found by hand gets in.</summary>
    [RelayCommand]
    private void New()
    {
        var blank = new SignalDefinition
        {
            Id = "",
            Name = "",
            Category = "Other",
            Mode = 0x22,
            Pid = 0,
            Decode = new DecodeSpec(0, 1, false, 1, 0, ""),
        };

        Editor = MakeEditor(blank, isNew: true, SignalOrigin.Yours,
            "Ford's own values are mode 22 with a two-byte PID. TEST asks the truck before you save.");
    }

    /// <summary>Define a PID the scan found, starting from the standard's formula where it has one.</summary>
    [RelayCommand]
    private void AddMissing(MissingPidViewModel? missing)
    {
        if (missing is null)
        {
            return;
        }

        var suggestion = StandardPids.Suggest(missing.Pid, missing.Bus);
        var note = missing.HasStandardDecode
            ? "Formula from SAE J1979. TEST it against the truck before you save."
            : "The standard does not give this as one number. TEST it, read the bytes, and work the formula out before trusting it.";

        // A suggested id can collide with a signal of yours on another bus; make it unique
        // rather than make the person discover the clash.
        var id = suggestion.Id;
        for (var n = 2; IdTaken(id); n++)
        {
            id = $"{suggestion.Id}{n}";
        }

        Editor = MakeEditor(suggestion with { Id = id }, isNew: true, SignalOrigin.Yours, note);
    }

    /// <summary>The module sweep and the identifier sweep (ADR-0035).</summary>
    public ModuleDiscoveryViewModel Modules { get; }

    /// <summary>Open the editor on something a sweep found.</summary>
    private void DefineDiscovered(SignalDefinition suggestion, string note)
    {
        var id = suggestion.Id;
        for (var n = 2; IdTaken(id); n++)
        {
            id = $"{suggestion.Id}{n}";
        }

        Editor = MakeEditor(suggestion with { Id = id }, isNew: true, SignalOrigin.Yours, note);
    }

    private bool IdTaken(string id) => _rows.Any(r => string.Equals(r.Id, id, StringComparison.Ordinal));

    private SignalEditorViewModel MakeEditor(SignalDefinition start, bool isNew, SignalOrigin origin, string? note) =>
        new(
            start,
            isNew,
            origin,
            note,
            _vehicle.ProbeAsync,
            IdTaken,
            address => ModuleNames.Likely(address, _vehicle.ActivePacks),
            save: definition =>
            {
                _store.Upsert(definition);
                CloseEditorIfSaved();
            },
            cancel: () => Editor = null,
            remove: id =>
            {
                _store.Remove(id);
                CloseEditorIfSaved();
            });

    /// <summary>Close on success; on a write failure, stay open so nothing typed is lost.</summary>
    private void CloseEditorIfSaved()
    {
        if (_store.LastError is null)
        {
            Editor = null;
        }

        OnPropertyChanged(nameof(Problem));
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Core.Discovery;
using DashDeck.Host.Settings;
using DashDeck.Vehicle;

namespace DashDeck.Host.ViewModels;

/// <summary>One module the sweep found.</summary>
public sealed partial class ModuleRowViewModel(DiscoveredModule module, string? likelyName) : ObservableObject
{
    public DiscoveredModule Module { get; } = module;

    public CanBus Bus => Module.Bus;

    public ushort Address => Module.Address;

    /// <summary>The address, as FORScan and a signal's <c>module</c> field write it.</summary>
    public string AddressText => Address.ToString("X3", CultureInfo.InvariantCulture);

    /// <summary>"726  ·  BCM — body control (likely)", or the address alone.</summary>
    public string Caption => likelyName is null
        ? $"MODULE {AddressText}"
        : $"{AddressText}  ·  {likelyName.ToUpperInvariant()}";

    /// <summary>Which bus, what it said when asked its part number, and which ranges are saved.</summary>
    public string Detail => string.Create(
        CultureInfo.InvariantCulture,
        $"{(Bus is CanBus.Ms ? "MS-CAN" : "HS-CAN")}  ·  answers on {Address + 8:X3}  ·  {(Module.PartNumber is { } part ? $"part {part}" : Module.RefusalCode is { } code ? $"part number: {ModuleScanner.DescribeRefusal(code)}" : "no part number")}{(likelyName is null ? "" : "  ·  name is likely, not read")}{(SweptRanges.Count == 0 ? "" : $"  ·  swept {string.Join(", ", SweptRanges)}")}");

    /// <summary>The identifier ranges with a saved sweep, so a module's row says what is already known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private IReadOnlyList<string> _sweptRanges = [];

    /// <summary>True while this is the module the identifier sweep is pointed at.</summary>
    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>One identifier a module answered.</summary>
public sealed record IdentifierRowViewModel(ModuleRowViewModel Module, FoundIdentifier Found)
{
    public string Caption => string.Create(CultureInfo.InvariantCulture, $"22 {Found.Did:X4}");

    /// <summary>The bytes, and the text in them when they are text.</summary>
    public string Detail
    {
        get
        {
            if (Found.RefusalCode is { } code)
            {
                return $"there, but {ModuleScanner.DescribeRefusal(code)}";
            }

            var hex = string.Join(' ', Found.Data.Take(16).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))
                + (Found.Data.Length > 16 ? " …" : "");

            return ModuleScanner.Text(Found.Data) is { } text
                ? $"{Found.Data.Length} bytes  ·  \"{text}\""
                : $"{Found.Data.Length} byte{(Found.Data.Length == 1 ? "" : "s")}  ·  {hex}";
        }
    }

    /// <summary>A value a signal could be made of: answered, short enough to be one number.</summary>
    public bool CanDefine => Found.RefusalCode is null && Found.Data.Length is > 0 and <= 4;
}

/// <summary>
/// One identifier under WATCH: what it said first and now, its range, how often it changed, and
/// its first byte less 40 — the usual way a temperature is sent.
/// </summary>
public sealed record WatchRowViewModel(ModuleRowViewModel Module, WatchedIdentifier Item)
{
    public string Caption => string.Create(CultureInfo.InvariantCulture, $"22 {Item.Did:X4}");

    /// <summary>MOVED ×7, or still.</summary>
    public string Badge => Item.HasChanged
        ? string.Create(CultureInfo.InvariantCulture, $"MOVED ×{Item.Changes}")
        : "STILL";

    public bool HasChanged => Item.HasChanged;

    /// <summary>First and now in hex, then the whole value's low–high in decimal.</summary>
    public string Detail => string.Create(
        CultureInfo.InvariantCulture,
        $"first {Hex(Item.First)}  →  now {Hex(Item.Current)}  ·  low–high {Item.Low}–{Item.High}{(Item.Missed ? "  ·  no answer last pass" : "")}");

    /// <summary>The first byte less 40, first and now: how Ford and OBD send most temperatures, in °C.</summary>
    public string Temperature => string.Create(
        CultureInfo.InvariantCulture,
        $"A−40:  {Item.First[0] - 40} → {Item.Current[0] - 40} °C");

    /// <summary>The identifier as the sweep would show it, with its latest bytes — for DEFINE.</summary>
    public IdentifierRowViewModel AsIdentifier => new(Module, new FoundIdentifier(Item.Did, Item.Current, null));

    private static string Hex(byte[] data) =>
        string.Join(' ', data.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
}

/// <summary>A preset identifier range, chosen with a chip.</summary>
public sealed partial class DidRangeOption(string label, ushort first, ushort last) : ObservableObject
{
    public string Label { get; } = label;

    public ushort First { get; } = first;

    public ushort Last { get; } = last;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// The MODULES block of Settings ▸ Sensors: find the modules on both buses, then ask one of
/// them which identifiers it answers (ADR-0035).
/// </summary>
/// <remarks>
/// The standard scan above it asks the broadcast, which only the engine computer answers. This
/// is how the rest of the truck gets found — the same way FORScan lists its modules: ask every
/// address. Then pick a module and sweep a range of its identifiers; any that answer with a
/// short value can be made into a signal with the editor and its TEST, exactly as a hand-typed
/// one would.
/// <para>
/// Both sweeps only read, and both are refused while the truck is moving: they take most of
/// the adapter's time while they run, and the dash goes stale while they do.
/// </para>
/// </remarks>
public sealed partial class ModuleDiscoveryViewModel : ObservableObject
{
    private readonly ISignalInventorySource _vehicle;
    private readonly Action<SignalDefinition, string> _define;
    private readonly DiscoveryStore? _store;
    private readonly IClock _clock;
    private CancellationTokenSource? _running;

    /// <param name="vehicle">The running pipeline.</param>
    /// <param name="define">Open the signal editor on a suggested definition, with a note.</param>
    /// <param name="store">Where results are kept across launches, or null to keep nothing.</param>
    /// <param name="clock">For when a scan or sweep ran.</param>
    public ModuleDiscoveryViewModel(
        ISignalInventorySource vehicle,
        Action<SignalDefinition, string> define,
        DiscoveryStore? store = null,
        IClock? clock = null)
    {
        _vehicle = vehicle;
        _define = define;
        _store = store;
        _clock = clock ?? SystemClock.Instance;

        Ranges =
        [
            new("F100–F1FF  IDENTITY", 0xF100, 0xF1FF),
            new("DD00–DDFF", 0xDD00, 0xDDFF),
            new("F400–F4FF", 0xF400, 0xF4FF),
            new("0000–0FFF", 0x0000, 0x0FFF),
            new("1000–1FFF", 0x1000, 0x1FFF),
            new("4000–4FFF", 0x4000, 0x4FFF),
        ];

        SelectRange(Ranges[0]);

        // What the truck answered last time, so a restart does not cost another minute parked.
        if (store?.LoadModules() is { } saved)
        {
            ApplyModules(saved, store.ModulesScannedUtc);
        }
    }

    /// <summary>Modules that answered, in bus then address order.</summary>
    public ObservableCollection<ModuleRowViewModel> Found { get; } = [];

    /// <summary>What the selected module answered.</summary>
    public ObservableCollection<IdentifierRowViewModel> Identifiers { get; } = [];

    /// <summary>The ranges offered as chips.</summary>
    public IReadOnlyList<DidRangeOption> Ranges { get; }

    /// <summary>What the last sweep found, or how to start one.</summary>
    [ObservableProperty]
    private string _status = "Ask every address on both buses whether a module is there — the way FORScan lists them. About a minute, parked.";

    /// <summary>Where the running sweep has got to.</summary>
    [ObservableProperty]
    private string _progressText = "";

    /// <summary>0 to 1, for the bar.</summary>
    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanModulesCommand), nameof(SweepCommand), nameof(StopCommand), nameof(WatchCommand), nameof(DefineWatchedCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isSweeping;

    /// <summary>What WATCH is seeing, most-moved first. Empty when not watching.</summary>
    public ObservableCollection<WatchRowViewModel> Watching { get; } = [];

    /// <summary>Where the watch has got to, or what it found.</summary>
    [ObservableProperty]
    private string _watchStatus = "";

    /// <summary>True while there are watch results to show.</summary>
    public bool HasWatch => Watching.Count > 0;

    public bool IsIdle => !IsSweeping;

    /// <summary>True once the module sweep has finished, so its list shows.</summary>
    [ObservableProperty]
    private bool _hasScanned;

    /// <summary>The module identifiers are swept on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectionCaption))]
    [NotifyCanExecuteChangedFor(nameof(SweepCommand), nameof(WatchCommand))]
    private ModuleRowViewModel? _selected;

    public bool HasSelection => Selected is not null;

    public string SelectionCaption => Selected is null ? "" : $"IDENTIFIERS IN {Selected.AddressText} ON {(Selected.Bus is CanBus.Ms ? "MS" : "HS")}-CAN";

    /// <summary>First identifier of the range, hex.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SweepCommand))]
    [NotifyPropertyChangedFor(nameof(RangeProblem))]
    private string _fromText = "";

    /// <summary>Last identifier of the range, hex.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SweepCommand))]
    [NotifyPropertyChangedFor(nameof(RangeProblem))]
    private string _toText = "";

    /// <summary>What the identifier sweep found, or how to start one.</summary>
    [ObservableProperty]
    private string _sweepStatus = "";

    /// <summary>Why the typed range cannot be swept, or null.</summary>
    public string? RangeProblem => TryRange(out _, out _, out var problem) ? null : problem;

    [RelayCommand]
    private void SelectRange(DidRangeOption? option)
    {
        if (option is null)
        {
            return;
        }

        foreach (var range in Ranges)
        {
            range.IsSelected = range == option;
        }

        FromText = option.First.ToString("X4", CultureInfo.InvariantCulture);
        ToText = option.Last.ToString("X4", CultureInfo.InvariantCulture);
        ShowSaved();
    }

    [RelayCommand]
    private void Select(ModuleRowViewModel? module)
    {
        if (module is null || IsSweeping)
        {
            return;
        }

        foreach (var row in Found)
        {
            row.IsSelected = row == module;
        }

        if (Selected != module)
        {
            ClearWatch();
            Identifiers.Clear();
            SweepStatus = "Pick a range and sweep it. A module answers every identifier it is asked — with a value or a no — so a range costs one exchange each.";
        }

        Selected = module;
        ShowSaved();
    }

    /// <summary>
    /// Show the saved sweep of the selected module and range, if there is one — what it answered
    /// last time, and when. SWEEP asks again and replaces it.
    /// </summary>
    private void ShowSaved()
    {
        if (IsSweeping || Selected is not { } module || _store is null || !TryRange(out var first, out var last, out _))
        {
            return;
        }

        if (_store.LoadSweep(module.Bus, module.Address, first, last) is { } saved)
        {
            ApplyIdentifiers(module, saved.Result, saved.SweptUtc);
        }
        else
        {
            Identifiers.Clear();
            ClearWatch();
            SweepStatus = string.Create(CultureInfo.CurrentCulture, $"{first:X4}–{last:X4} has not been swept on {module.AddressText}. SWEEP asks it.");
            WatchCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanStart() => !IsSweeping;

    /// <summary>Ask every address on both buses.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task ScanModulesAsync()
    {
        if (await RefuseIfMovingAsync() is { } refusal)
        {
            Status = refusal;
            return;
        }

        using var cts = Begin();
        Status = "Asking every address on HS-CAN, then MS-CAN…";

        try
        {
            var result = await ModuleScanner.ScanAsync(_vehicle.ProbeAsync, [CanBus.Hs, CanBus.Ms], Reporter(), cts.Token);

            // Kept for the next launch — the real truck only, never the synthetic one.
            if (!_vehicle.IsSimulated && result.Completed)
            {
                _store?.SaveModules(result, _clock.UtcNow);
            }

            ApplyModules(result);
        }
        finally
        {
            End();
        }
    }

    /// <summary>Show a module sweep's results. Separate from the sweep so it can be tested directly.</summary>
    /// <param name="result">The scan.</param>
    /// <param name="savedUtc">When it ran, when it is a saved one being shown again at launch.</param>
    internal void ApplyModules(ModuleScanResult result, DateTimeOffset? savedUtc = null)
    {
        Found.Clear();
        Identifiers.Clear();
        Selected = null;

        foreach (var module in result.Modules.OrderBy(m => m.Bus).ThenBy(m => m.Address))
        {
            Found.Add(new ModuleRowViewModel(module, ModuleNames.Likely(module.Address, _vehicle.ActivePacks))
            {
                SweptRanges = _store?.SweptRanges(module.Bus, module.Address) ?? [],
            });
        }

        var hs = result.Modules.Count(m => m.Bus is CanBus.Hs);
        var ms = result.Modules.Count(m => m.Bus is CanBus.Ms);

        var parts = new List<string>
        {
            result.Problems.TryGetValue(CanBus.Hs, out var hsProblem) ? $"HS-CAN: {hsProblem}" : $"HS-CAN: {hs} module{(hs == 1 ? "" : "s")}",
            result.Problems.TryGetValue(CanBus.Ms, out var msProblem) ? $"MS-CAN: {msProblem}" : $"MS-CAN: {ms} module{(ms == 1 ? "" : "s")}",
        };

        if (!result.Completed)
        {
            parts.Add("stopped before the end");
        }

        if (savedUtc is { } at)
        {
            parts.Insert(0, string.Create(CultureInfo.CurrentCulture, $"SAVED SCAN, {at.ToLocalTime():d MMM HH:mm}"));
        }
        else if (_vehicle.IsSimulated)
        {
            parts.Add("SIMULATED — these are the synthetic truck's modules, and they are not saved");
        }

        Status = string.Join("  ·  ", parts);
        HasScanned = true;
    }

    private bool CanSweep() => !IsSweeping && Selected is not null && TryRange(out _, out _, out _);

    /// <summary>Ask the selected module for every identifier in the range.</summary>
    [RelayCommand(CanExecute = nameof(CanSweep))]
    private async Task SweepAsync()
    {
        if (Selected is not { } module || !TryRange(out var first, out var last, out _))
        {
            return;
        }

        if (await RefuseIfMovingAsync() is { } refusal)
        {
            SweepStatus = refusal;
            return;
        }

        using var cts = Begin();
        Identifiers.Clear();
        SweepStatus = $"Asking {module.AddressText} for 22 {first:X4}–{last:X4}…";

        try
        {
            var result = await DidScanner.ScanAsync(_vehicle.ProbeAsync, module.Bus, module.Address, first, last, Reporter(), cts.Token);

            // Kept for the next launch, as for the module scan — a stopped sweep too: what it
            // found before STOP is real, and the status says it stopped.
            if (!_vehicle.IsSimulated && _store is not null)
            {
                _store.SaveSweep(module.Bus, module.Address, first, last, result, _clock.UtcNow);
                module.SweptRanges = _store.SweptRanges(module.Bus, module.Address);
            }

            ApplyIdentifiers(module, result);
        }
        finally
        {
            End();
        }
    }

    /// <summary>Show an identifier sweep's results — a fresh one, or a saved one with when it ran.</summary>
    internal void ApplyIdentifiers(ModuleRowViewModel module, DidSweepResult result, DateTimeOffset? savedUtc = null)
    {
        ClearWatch();
        Identifiers.Clear();

        foreach (var found in result.Found)
        {
            Identifiers.Add(new IdentifierRowViewModel(module, found));
        }

        WatchCommand.NotifyCanExecuteChanged();

        var answered = result.Found.Count(f => f.RefusalCode is null);
        var declined = result.Found.Count - answered;

        SweepStatus = string.Create(
            CultureInfo.CurrentCulture,
            $"{(savedUtc is { } at ? $"SAVED SWEEP, {at.ToLocalTime():d MMM HH:mm}  ·  " : "")}{result.Asked} asked  ·  {answered} answered  ·  {declined} there but declined{(result.Problem is { } problem ? $"  ·  {problem}" : "")}{(savedUtc is null && _vehicle.IsSimulated ? "  ·  SIMULATED, not saved" : "")}");
    }

    [RelayCommand(CanExecute = nameof(IsSweeping))]
    private void Stop() => _running?.Cancel();

    private bool CanWatch() => !IsSweeping && Selected is not null && Identifiers.Any(i => i.CanDefine);

    /// <summary>
    /// Ask the identifiers shown — the ones that answered with a number — over and over, until STOP,
    /// and keep the ones that move at the top.
    /// </summary>
    /// <remarks>
    /// The way through a haystack: start it, do one thing to the truck — blip the throttle, or let
    /// it idle while it warms — and see what moved with it. Each pass asks only what the sweep found,
    /// so a hundred identifiers is about five seconds a pass. It is a sweep that does not end, so it
    /// is refused while moving like one, and stops by itself if the truck starts moving.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanWatch))]
    private async Task WatchAsync()
    {
        if (Selected is not { } module)
        {
            return;
        }

        if (await RefuseIfMovingAsync() is { } refusal)
        {
            WatchStatus = refusal;
            return;
        }

        var watch = new IdentifierWatch(Identifiers.Select(i => i.Found));
        using var cts = Begin();
        ClearWatch();
        WatchStatus = string.Create(
            CultureInfo.CurrentCulture,
            $"Watching {watch.Items.Count} identifiers on {module.AddressText}. Now do one thing — blip the throttle, or let it idle and warm — and see what moves. STOP when done.");

        var stoppedBy = "STOP";

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var finished = await watch.PassAsync(_vehicle.ProbeAsync, module.Bus, module.Address, WatchReporter(watch), cts.Token);
                ShowWatch(module, watch);

                if (!finished)
                {
                    break;
                }

                if (await RefuseIfMovingAsync() is not null)
                {
                    stoppedBy = "the truck moving";
                    break;
                }
            }
        }
        finally
        {
            End();
        }

        WatchStatus = string.Create(
            CultureInfo.CurrentCulture,
            $"Stopped by {stoppedBy} after {watch.Passes} pass{(watch.Passes == 1 ? "" : "es")}  ·  {watch.ChangedCount} of {watch.Items.Count} moved{(_vehicle.IsSimulated ? "  ·  SIMULATED" : "")}. Tap one to DEFINE and TEST it.");
    }

    /// <summary>Redraw the watch list, most-moved first. Once a pass, so rows do not jump about mid-pass.</summary>
    private void ShowWatch(ModuleRowViewModel module, IdentifierWatch watch)
    {
        Watching.Clear();
        foreach (var item in watch.Ranked())
        {
            Watching.Add(new WatchRowViewModel(module, item));
        }

        OnPropertyChanged(nameof(HasWatch));
    }

    private void ClearWatch()
    {
        if (Watching.Count == 0 && WatchStatus.Length == 0)
        {
            return;
        }

        Watching.Clear();
        WatchStatus = "";
        OnPropertyChanged(nameof(HasWatch));
    }

    /// <summary>Progress for a watch: which pass, where in it, and how many have moved.</summary>
    private Progress<SweepProgress> WatchReporter(IdentifierWatch watch) => new(p =>
    {
        Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
        ProgressText = string.Create(CultureInfo.CurrentCulture, $"pass {watch.Passes + 1}  ·  {p.Current}  ·  {p.Done} of {p.Total}  ·  {p.Found} moved");
    });

    /// <summary>DEFINE from a watch row: the editor, with the latest bytes, ready to TEST. After STOP.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void DefineWatched(WatchRowViewModel? row)
    {
        if (row is not null && !IsSweeping)
        {
            Define(row.AsIdentifier);
        }
    }

    /// <summary>Open the editor on an identifier, with its module, bus and length filled in.</summary>
    [RelayCommand]
    private void Define(IdentifierRowViewModel? row)
    {
        if (row is null || !row.CanDefine)
        {
            return;
        }

        var length = row.Found.Data.Length switch
        {
            1 => 1,
            2 => 2,
            _ => 4,
        };

        var suggestion = new SignalDefinition
        {
            Id = string.Create(CultureInfo.InvariantCulture, $"module{row.Module.AddressText.ToLowerInvariant()}.did{row.Found.Did:x4}"),
            Name = string.Create(CultureInfo.InvariantCulture, $"{row.Module.AddressText} 22 {row.Found.Did:X4}"),
            Category = "Discovered",
            Bus = row.Module.Bus,
            Mode = ModuleScanner.ReadDataByIdentifier,
            Pid = row.Found.Did,
            Module = row.Module.AddressText,
            Decode = new DecodeSpec(0, length, false, 1, 0, ""),
            DefaultRateHz = 1,
        };

        _define(suggestion,
            "Found by the identifier sweep. The formula is a placeholder — raw bytes, scale 1. TEST it while the thing it measures changes (a door, the throttle, the temperature) and work the formula out before you trust it.");
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Refuse to sweep while the truck is moving. Asks the speed once, directly; a desk with no
    /// truck, or the synthetic one, has no speed worth refusing on.
    /// </summary>
    private async Task<string?> RefuseIfMovingAsync()
    {
        if (_vehicle.IsSimulated)
        {
            return null;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var speed = await _vehicle.ProbeAsync(new PidRequest(0x01, 0x0D, CanBus.Hs), timeout.Token);

            return speed.IsSuccess && speed.Data.Length > 0 && speed.Data[0] > 0
                ? "The truck is moving. A sweep takes most of the adapter's time and the dash goes stale while it runs — park first."
                : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private CancellationTokenSource Begin()
    {
        var cts = new CancellationTokenSource();
        _running = cts;
        Progress = 0;
        ProgressText = "";
        IsSweeping = true;
        return cts;
    }

    private void End()
    {
        _running = null;
        IsSweeping = false;
        ProgressText = "";
    }

    /// <summary>Progress, posted back to the UI thread by <see cref="Progress{T}"/>.</summary>
    private Progress<SweepProgress> Reporter() => new(p =>
    {
        Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
        ProgressText = string.Create(CultureInfo.CurrentCulture, $"{p.Current}  ·  {p.Done} of {p.Total}  ·  {p.Found} found");
    });

    private bool TryRange(out ushort first, out ushort last, out string? problem)
    {
        first = last = 0;

        if (!SignalEditorViewModel.TryHex(FromText, 0xFFFF, out var from) || !SignalEditorViewModel.TryHex(ToText, 0xFFFF, out var to))
        {
            problem = "Both ends of the range are hex, up to four digits.";
            return false;
        }

        if (to < from)
        {
            problem = "The range ends before it starts.";
            return false;
        }

        if (to - from + 1 > DidScanner.MaxSpan)
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"At most {DidScanner.MaxSpan} identifiers ({DidScanner.MaxSpan:X} hex) per sweep — about four minutes.");
            return false;
        }

        first = (ushort)from;
        last = (ushort)to;
        problem = null;
        return true;
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Core.Discovery;
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

    /// <summary>Which bus, and what it said when asked its part number.</summary>
    public string Detail => string.Create(
        CultureInfo.InvariantCulture,
        $"{(Bus is CanBus.Ms ? "MS-CAN" : "HS-CAN")}  ·  answers on {Address + 8:X3}  ·  {(Module.PartNumber is { } part ? $"part {part}" : Module.RefusalCode is { } code ? $"part number: {ModuleScanner.DescribeRefusal(code)}" : "no part number")}{(likelyName is null ? "" : "  ·  name is likely, not read")}");

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
    private CancellationTokenSource? _running;

    /// <param name="vehicle">The running pipeline.</param>
    /// <param name="define">Open the signal editor on a suggested definition, with a note.</param>
    public ModuleDiscoveryViewModel(ISignalInventorySource vehicle, Action<SignalDefinition, string> define)
    {
        _vehicle = vehicle;
        _define = define;

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
    [NotifyCanExecuteChangedFor(nameof(ScanModulesCommand), nameof(SweepCommand), nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isSweeping;

    public bool IsIdle => !IsSweeping;

    /// <summary>True once the module sweep has finished, so its list shows.</summary>
    [ObservableProperty]
    private bool _hasScanned;

    /// <summary>The module identifiers are swept on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectionCaption))]
    [NotifyCanExecuteChangedFor(nameof(SweepCommand))]
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
            Identifiers.Clear();
            SweepStatus = "Pick a range and sweep it. A module answers every identifier it is asked — with a value or a no — so a range costs one exchange each.";
        }

        Selected = module;
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
            ApplyModules(result);
        }
        finally
        {
            End();
        }
    }

    /// <summary>Show a module sweep's results. Separate from the sweep so it can be tested directly.</summary>
    internal void ApplyModules(ModuleScanResult result)
    {
        Found.Clear();
        Identifiers.Clear();
        Selected = null;

        foreach (var module in result.Modules.OrderBy(m => m.Bus).ThenBy(m => m.Address))
        {
            Found.Add(new ModuleRowViewModel(module, ModuleNames.Likely(module.Address, _vehicle.ActivePacks)));
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

        if (_vehicle.IsSimulated)
        {
            parts.Add("SIMULATED — these are the synthetic truck's modules");
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
            ApplyIdentifiers(module, result);
        }
        finally
        {
            End();
        }
    }

    /// <summary>Show an identifier sweep's results.</summary>
    internal void ApplyIdentifiers(ModuleRowViewModel module, DidSweepResult result)
    {
        Identifiers.Clear();

        foreach (var found in result.Found)
        {
            Identifiers.Add(new IdentifierRowViewModel(module, found));
        }

        var answered = result.Found.Count(f => f.RefusalCode is null);
        var declined = result.Found.Count - answered;

        SweepStatus = string.Create(
            CultureInfo.CurrentCulture,
            $"{result.Asked} asked  ·  {answered} answered  ·  {declined} there but declined{(result.Problem is { } problem ? $"  ·  {problem}" : "")}{(_vehicle.IsSimulated ? "  ·  SIMULATED" : "")}");
    }

    [RelayCommand(CanExecute = nameof(IsSweeping))]
    private void Stop() => _running?.Cancel();

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

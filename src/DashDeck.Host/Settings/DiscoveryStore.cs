using System.Globalization;
using System.Text.Json.Serialization;
using DashDeck.Abstractions;
using DashDeck.Core.Discovery;

namespace DashDeck.Host.Settings;

/// <summary>One module as saved.</summary>
public sealed record SavedModule
{
    [JsonPropertyName("bus")]
    public string Bus { get; init; } = "hs";

    /// <summary>Hex, as FORScan writes it: <c>7E0</c>.</summary>
    [JsonPropertyName("address")]
    public string Address { get; init; } = "";

    [JsonPropertyName("partNumber")]
    public string? PartNumber { get; init; }

    [JsonPropertyName("refusalCode")]
    public byte? RefusalCode { get; init; }
}

/// <summary>The last module scan.</summary>
public sealed record SavedModuleScan
{
    [JsonPropertyName("scannedUtc")]
    public DateTimeOffset ScannedUtc { get; init; }

    [JsonPropertyName("completed")]
    public bool Completed { get; init; } = true;

    /// <summary>Why a bus said nothing, by bus (<c>hs</c>, <c>ms</c>).</summary>
    [JsonPropertyName("problems")]
    public Dictionary<string, string> Problems { get; init; } = [];

    [JsonPropertyName("modules")]
    public List<SavedModule> Modules { get; init; } = [];
}

/// <summary>One identifier as saved: its bytes in hex, or the code it was refused with.</summary>
public sealed record SavedIdentifier
{
    [JsonPropertyName("did")]
    public string Did { get; init; } = "";

    [JsonPropertyName("data")]
    public string Data { get; init; } = "";

    [JsonPropertyName("refusalCode")]
    public byte? RefusalCode { get; init; }
}

/// <summary>One identifier sweep: a module, a range, and what answered.</summary>
public sealed record SavedSweep
{
    [JsonPropertyName("bus")]
    public string Bus { get; init; } = "hs";

    [JsonPropertyName("module")]
    public string Module { get; init; } = "";

    [JsonPropertyName("first")]
    public string First { get; init; } = "";

    [JsonPropertyName("last")]
    public string Last { get; init; } = "";

    [JsonPropertyName("sweptUtc")]
    public DateTimeOffset SweptUtc { get; init; }

    [JsonPropertyName("asked")]
    public int Asked { get; init; }

    [JsonPropertyName("problem")]
    public string? Problem { get; init; }

    [JsonPropertyName("found")]
    public List<SavedIdentifier> Found { get; init; } = [];
}

/// <summary>The file shape.</summary>
public sealed record DiscoveryFile
{
    [JsonPropertyName("moduleScan")]
    public SavedModuleScan? ModuleScan { get; init; }

    [JsonPropertyName("sweeps")]
    public List<SavedSweep> Sweeps { get; init; } = [];
}

/// <summary>
/// What the MODULES block found on the truck, kept across launches —
/// <c>%LOCALAPPDATA%\DashDeck\discovery.json</c>.
/// </summary>
/// <remarks>
/// A module scan is a minute and an identifier sweep up to four, parked, with the dash going stale
/// while they run; losing them to a restart meant doing them again. The last module scan is kept,
/// and the latest sweep of each module and range, so going back to a module shows what it answered.
/// <para>
/// <b>Only the real truck is saved.</b> The synthetic truck's modules would overwrite the real ones
/// the first time the dash ran at a desk. The file is the tablet's own and never leaves it; it holds
/// what the modules answered, which includes the VIN if the identity range of the engine computer
/// was swept — so it is not something to post publicly.
/// </para>
/// </remarks>
public sealed class DiscoveryStore
{
    private readonly string _path;
    private DiscoveryFile _file;

    /// <summary>The standard store, reading <c>%LOCALAPPDATA%\DashDeck\discovery.json</c>.</summary>
    public DiscoveryStore()
        : this(JsonFile.InLocalAppData("discovery.json"))
    {
    }

    /// <summary>A store at an explicit path. For tests, which must not touch the real profile.</summary>
    public DiscoveryStore(string path)
    {
        _path = path;
        _file = JsonFile.Load<DiscoveryFile>(path, out var error) ?? new DiscoveryFile();
        LastError = error;
    }

    /// <summary>Where the file is.</summary>
    public string Path => _path;

    /// <summary>Why the last load or save failed, if it did.</summary>
    public string? LastError { get; private set; }

    /// <summary>When the saved module scan ran, or null when there is none.</summary>
    public DateTimeOffset? ModulesScannedUtc => _file.ModuleScan?.ScannedUtc;

    /// <summary>The saved module scan as the scanner returned it, or null.</summary>
    public ModuleScanResult? LoadModules()
    {
        if (_file.ModuleScan is not { } scan)
        {
            return null;
        }

        var modules = new List<DiscoveredModule>();
        foreach (var m in scan.Modules)
        {
            if (TryAddress(m.Address, out var address))
            {
                modules.Add(new DiscoveredModule(ParseBus(m.Bus), address, m.PartNumber, m.RefusalCode));
            }
        }

        var problems = scan.Problems.ToDictionary(p => ParseBus(p.Key), p => p.Value);
        return new ModuleScanResult(modules, problems, scan.Completed);
    }

    /// <summary>Keep a module scan, replacing the last one. Sweeps of modules it still found are kept.</summary>
    public void SaveModules(ModuleScanResult result, DateTimeOffset nowUtc)
    {
        var scan = new SavedModuleScan
        {
            ScannedUtc = nowUtc,
            Completed = result.Completed,
            Problems = result.Problems.ToDictionary(p => BusName(p.Key), p => p.Value),
            Modules = [.. result.Modules.Select(m => new SavedModule
            {
                Bus = BusName(m.Bus),
                Address = Hex(m.Address, 3),
                PartNumber = m.PartNumber,
                RefusalCode = m.RefusalCode,
            })],
        };

        _file = _file with { ModuleScan = scan };
        Save();
    }

    /// <summary>The latest saved sweep of exactly this module and range, or null.</summary>
    public (DidSweepResult Result, DateTimeOffset SweptUtc)? LoadSweep(CanBus bus, ushort module, ushort first, ushort last)
    {
        var saved = Find(bus, module, first, last);
        if (saved is null)
        {
            return null;
        }

        var found = new List<FoundIdentifier>();
        foreach (var f in saved.Found)
        {
            if (TryAddress(f.Did, out var did) && TryBytes(f.Data, out var data))
            {
                found.Add(new FoundIdentifier(did, data, f.RefusalCode));
            }
        }

        return (new DidSweepResult(found, saved.Asked, saved.Problem), saved.SweptUtc);
    }

    /// <summary>The ranges swept on a module, in hex, for its row: <c>F400–F4FF</c>.</summary>
    public IReadOnlyList<string> SweptRanges(CanBus bus, ushort module) =>
        [.. _file.Sweeps
            .Where(s => s.Bus == BusName(bus) && s.Module == Hex(module, 3))
            .OrderBy(s => s.First, StringComparer.Ordinal)
            .Select(s => $"{s.First}–{s.Last}")];

    /// <summary>Keep a sweep, replacing an earlier one of the same module and range.</summary>
    public void SaveSweep(CanBus bus, ushort module, ushort first, ushort last, DidSweepResult result, DateTimeOffset nowUtc)
    {
        var sweep = new SavedSweep
        {
            Bus = BusName(bus),
            Module = Hex(module, 3),
            First = Hex(first, 4),
            Last = Hex(last, 4),
            SweptUtc = nowUtc,
            Asked = result.Asked,
            Problem = result.Problem,
            Found = [.. result.Found.Select(f => new SavedIdentifier
            {
                Did = Hex(f.Did, 4),
                Data = Convert.ToHexString(f.Data),
                RefusalCode = f.RefusalCode,
            })],
        };

        var earlier = Find(bus, module, first, last);
        var sweeps = _file.Sweeps.Where(s => !ReferenceEquals(s, earlier)).ToList();
        sweeps.Add(sweep);
        _file = _file with { Sweeps = sweeps };
        Save();
    }

    private SavedSweep? Find(CanBus bus, ushort module, ushort first, ushort last) =>
        _file.Sweeps.LastOrDefault(s =>
            s.Bus == BusName(bus) && s.Module == Hex(module, 3) && s.First == Hex(first, 4) && s.Last == Hex(last, 4));

    private void Save()
    {
        JsonFile.Save(_path, _file, out var error);
        LastError = error;
    }

    private static string BusName(CanBus bus) => bus is CanBus.Ms ? "ms" : "hs";

    private static CanBus ParseBus(string bus) => string.Equals(bus, "ms", StringComparison.OrdinalIgnoreCase) ? CanBus.Ms : CanBus.Hs;

    private static string Hex(ushort value, int digits) => value.ToString($"X{digits}", CultureInfo.InvariantCulture);

    private static bool TryAddress(string text, out ushort value) =>
        ushort.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);

    private static bool TryBytes(string hex, out byte[] data)
    {
        try
        {
            data = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            data = [];
            return false;
        }
    }
}

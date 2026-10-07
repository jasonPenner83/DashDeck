using System.Globalization;
using System.Text.Json;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;

namespace DashDeck.Core.Discovery;

/// <summary>One entry in the SAE J1979 mode 01 table.</summary>
/// <param name="Pid">The PID.</param>
/// <param name="Id">A suggested catalog id.</param>
/// <param name="Name">What the standard calls it.</param>
/// <param name="Category">The picker group it would sort under.</param>
/// <param name="Decode">
/// The standard's formula, when it is a single linear value the catalog can express; null when
/// the PID is a bitfield, an enum or a multi-value record a decode spec cannot describe.
/// </param>
/// <param name="Min">The standard's lower bound, when there is a decode.</param>
/// <param name="Max">The standard's upper bound, when there is a decode.</param>
public sealed record StandardPid(
    int Pid,
    string Id,
    string Name,
    string Category,
    DecodeSpec? Decode,
    double? Min,
    double? Max)
{
    /// <summary>True when the formula is the standard's, not a placeholder to be filled in.</summary>
    public bool HasDecode => Decode is not null;
}

/// <summary>An identifier range offered for a module sweep — "F100–F1FF  IDENTITY".</summary>
public sealed record IdentifierRange(string Name, ushort First, ushort Last);

/// <summary>
/// What the public standards say, read from <c>catalog/reference/</c> (ADR-0052): the SAE J1979
/// mode 01 table, the module ids ISO 15765-4 names, and the identity question and identifier
/// ranges ISO 14229 defines.
/// </summary>
/// <remarks>
/// <b>A starting point, never a reading.</b> The mode 01 formulas only pre-fill the definition
/// editor when a scan finds a PID the catalog lacks; nothing here is polled on its own. Nothing
/// here is any one vehicle's either — that goes in the user's own vehicle file, which
/// <see cref="With"/> lays over this.
/// </remarks>
public sealed class ObdReference
{
    private readonly Dictionary<int, StandardPid> _byPid;

    private ObdReference(
        IEnumerable<StandardPid> mode01,
        IReadOnlyDictionary<ushort, string> modules,
        ushort identityDid,
        IReadOnlyList<IdentifierRange> ranges)
    {
        _byPid = mode01.ToDictionary(p => p.Pid);
        Modules = modules;
        IdentityDid = identityDid;
        IdentifierRanges = ranges;
    }

    /// <summary>Nothing known: names are hex labels, and the sweep has no ranges to offer.</summary>
    public static ObdReference Empty { get; } = new([], new Dictionary<ushort, string>(), 0xF187, []);

    /// <summary>Every mode 01 PID the table knows.</summary>
    public IReadOnlyCollection<StandardPid> Mode01 => _byPid.Values;

    /// <summary>Names for module ids, by request id.</summary>
    public IReadOnlyDictionary<ushort, string> Modules { get; }

    /// <summary>The identifier each address is asked by a module scan.</summary>
    public ushort IdentityDid { get; }

    /// <summary>The ranges offered for an identifier sweep, in order.</summary>
    public IReadOnlyList<IdentifierRange> IdentifierRanges { get; }

    public bool TryGet(int pid, out StandardPid entry) => _byPid.TryGetValue(pid, out entry!);

    /// <summary>What the standard calls a PID, or a plain hex label for one it does not list.</summary>
    public string NameOf(int pid) =>
        TryGet(pid, out var entry) ? entry.Name : string.Create(CultureInfo.InvariantCulture, $"Mode 01 PID {pid:X2}");

    /// <summary>
    /// A definition to start the editor from — the standard's formula where it has one, a
    /// one-byte raw placeholder where it does not.
    /// </summary>
    public SignalDefinition Suggest(int pid, CanBus bus)
    {
        if (TryGet(pid, out var entry) && entry.Decode is { } decode)
        {
            return new SignalDefinition
            {
                Id = entry.Id,
                Name = entry.Name,
                Category = entry.Category,
                Bus = bus,
                Pid = (ushort)pid,
                Decode = decode,
                DefaultRateHz = 1,
                Min = entry.Min,
                Max = entry.Max,
            };
        }

        return new SignalDefinition
        {
            Id = entry?.Id ?? string.Create(CultureInfo.InvariantCulture, $"obd2.pid{pid:X2}"),
            Name = NameOf(pid),
            Category = entry?.Category ?? "Other",
            Bus = bus,
            Pid = (ushort)pid,
            Decode = new DecodeSpec(0, 1, false, 1, 0, ""),
            DefaultRateHz = 1,
        };
    }

    /// <summary>
    /// This, with what the vehicle files add: their module names first, their identity question
    /// instead of the standard's, their identifier ranges after the standard's.
    /// </summary>
    public ObdReference With(IEnumerable<VehiclePack> packs)
    {
        var list = packs.ToList();
        if (list.Count == 0)
        {
            return this;
        }

        var modules = new Dictionary<ushort, string>(Modules);
        var ranges = new List<IdentifierRange>(IdentifierRanges);
        var identity = IdentityDid;

        foreach (var pack in list)
        {
            foreach (var (key, name) in pack.Modules)
            {
                if (SignalDefinition.ParseModule(key) is { } address)
                {
                    modules[address] = name;
                }
            }

            if (ParseHex(pack.IdentityDid) is { } did)
            {
                identity = did;
            }

            foreach (var range in pack.IdentifierRanges)
            {
                if (ToRange(range) is { } parsed && !ranges.Any(r => r.First == parsed.First && r.Last == parsed.Last))
                {
                    ranges.Add(parsed);
                }
            }
        }

        return new ObdReference(_byPid.Values, modules, identity, ranges);
    }

    // ── Reading the files ─────────────────────────────────────────────────────

    public const string Mode01File = "sae-j1979-mode01.json";

    public const string DiagnosticsFile = "iso-diagnostics.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Read <c>reference/</c> under a catalog folder. A missing or unreadable file costs what it
    /// held and a problem, never the dash.
    /// </summary>
    public static (ObdReference Reference, IReadOnlyList<string> Problems) Load(string? catalogFolder)
    {
        var problems = new List<string>();
        var folder = catalogFolder is null ? null : Path.Combine(catalogFolder, "reference");

        string? Read(string name)
        {
            var path = folder is null ? null : Path.Combine(folder, name);
            if (path is null || !File.Exists(path))
            {
                problems.Add($"reference/{name} was not found");
                return null;
            }

            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException ex)
            {
                problems.Add($"reference/{name}: {ex.Message}");
                return null;
            }
        }

        var mode01 = Read(Mode01File);
        var diagnostics = Read(DiagnosticsFile);

        try
        {
            return (Parse(mode01, diagnostics), problems);
        }
        catch (InvalidDataException ex)
        {
            problems.Add(ex.Message);
            return (Empty, problems);
        }
    }

    /// <summary>Read the two reference files' text; either may be null.</summary>
    /// <exception cref="InvalidDataException">A file is not the shape it should be.</exception>
    public static ObdReference Parse(string? mode01Json, string? diagnosticsJson)
    {
        var pids = new List<StandardPid>();
        if (mode01Json is not null)
        {
            var file = Deserialize<Mode01FileModel>(mode01Json, Mode01File);
            foreach (var row in file.Mode01)
            {
                if (ParseHex(row.Pid) is not { } pid || pid > 0xFF)
                {
                    throw new InvalidDataException($"{Mode01File}: '{row.Pid}' is not a mode 01 PID");
                }

                pids.Add(new StandardPid(
                    pid,
                    row.Id ?? string.Create(CultureInfo.InvariantCulture, $"obd2.pid{pid:X2}"),
                    row.Name,
                    row.Category ?? "Other",
                    row.Decode,
                    row.Decode is null ? null : row.Min,
                    row.Decode is null ? null : row.Max));
            }
        }

        var modules = new Dictionary<ushort, string>();
        ushort identity = Empty.IdentityDid;
        var ranges = new List<IdentifierRange>();

        if (diagnosticsJson is not null)
        {
            var file = Deserialize<DiagnosticsFileModel>(diagnosticsJson, DiagnosticsFile);
            foreach (var (key, name) in file.Modules)
            {
                modules[SignalDefinition.ParseModule(key)
                    ?? throw new InvalidDataException($"{DiagnosticsFile}: '{key}' is not a module address")] = name;
            }

            if (file.IdentityDid is not null)
            {
                identity = ParseHex(file.IdentityDid)
                    ?? throw new InvalidDataException($"{DiagnosticsFile}: identityDid '{file.IdentityDid}' is not four hex digits");
            }

            foreach (var range in file.IdentifierRanges)
            {
                ranges.Add(ToRange(range)
                    ?? throw new InvalidDataException($"{DiagnosticsFile}: range '{range.Name}' needs from ≤ to, four hex digits each"));
            }
        }

        if (pids.GroupBy(p => p.Pid).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            throw new InvalidDataException($"{Mode01File}: PID {twice.Key:X2} is listed twice");
        }

        return new ObdReference(pids, modules, identity, ranges);
    }

    internal static IdentifierRange? ToRange(IdentifierRangeEntry entry) =>
        ParseHex(entry.From) is { } first && ParseHex(entry.To) is { } last && first <= last
            ? new IdentifierRange(entry.Name ?? $"{first:X4}–{last:X4}", first, last)
            : null;

    internal static ushort? ParseHex(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return trimmed.Length is > 0 and <= 4
            && ushort.TryParse(trimmed, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    private static T Deserialize<T>(string json, string name)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new InvalidDataException($"{name}: empty");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{name}: {ex.Message}", ex);
        }
    }

    private sealed record Mode01FileModel
    {
        public IReadOnlyList<Mode01Row> Mode01 { get; init; } = [];
    }

    private sealed record Mode01Row
    {
        public required string Pid { get; init; }

        public string? Id { get; init; }

        public required string Name { get; init; }

        public string? Category { get; init; }

        public DecodeSpec? Decode { get; init; }

        public double? Min { get; init; }

        public double? Max { get; init; }
    }

    private sealed record DiagnosticsFileModel
    {
        public IReadOnlyDictionary<string, string> Modules { get; init; } = new Dictionary<string, string>();

        public string? IdentityDid { get; init; }

        public IReadOnlyList<IdentifierRangeEntry> IdentifierRanges { get; init; } = [];
    }
}

/// <summary>An identifier range as a file writes it: <c>{ "name": "…", "from": "DD00", "to": "DDFF" }</c>.</summary>
public sealed record IdentifierRangeEntry
{
    public string? Name { get; init; }

    public required string From { get; init; }

    public required string To { get; init; }
}

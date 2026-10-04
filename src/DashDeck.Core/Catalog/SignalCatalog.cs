using System.Text.Json;
using DashDeck.Abstractions;

namespace DashDeck.Core.Catalog;

/// <summary>
/// The loaded set of signal definitions.
/// </summary>
/// <remarks>
/// The same catalog drives the synthetic vehicle and the real truck, so a signal that
/// exists in simulation is guaranteed to have a real definition waiting for it.
/// </remarks>
public sealed class SignalCatalog
{
    private readonly Dictionary<string, SignalDefinition> _byId;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private SignalCatalog(IEnumerable<SignalDefinition> definitions)
    {
        _byId = definitions.ToDictionary(d => d.Id, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> Ids => _byId.Keys;

    public IReadOnlyCollection<SignalDefinition> Definitions => _byId.Values;

    public int Count => _byId.Count;

    public SignalDefinition this[string id] => _byId[id];

    public bool TryGet(string id, out SignalDefinition definition) => _byId.TryGetValue(id, out definition!);

    public static SignalCatalog FromJson(string json)
    {
        var definitions = JsonSerializer.Deserialize<List<SignalDefinition>>(json, JsonOptions)
            ?? throw new InvalidDataException("Signal catalog JSON did not deserialize to a list.");

        Validate(definitions);
        return new SignalCatalog(definitions);
    }

    public static SignalCatalog FromFile(string path) => FromJson(File.ReadAllText(path));

    /// <summary>Build a catalog from definitions already in hand, with the same validation.</summary>
    public static SignalCatalog FromDefinitions(IEnumerable<SignalDefinition> definitions)
    {
        var list = definitions.ToList();
        Validate(list);
        return new SignalCatalog(list);
    }

    /// <summary>
    /// The shipped catalog with the user's own definitions laid over it (ADR-0032).
    /// </summary>
    /// <remarks>
    /// A user definition with a shipped id <em>replaces</em> that definition — which is how a
    /// standard PID is corrected without editing the shipped file — and one with a new id is
    /// added. The shipped file is never written: it is known-good, it is overwritten by every
    /// deploy, and the trial and error of discovery belongs somewhere it can be undone.
    /// <para>
    /// Validated as one catalog, so an overlay cannot smuggle in a duplicate or a nonsensical
    /// decode spec that the shipped file alone would have been refused for.
    /// </para>
    /// </remarks>
    public static SignalCatalog Overlay(SignalCatalog shipped, IEnumerable<SignalDefinition> user)
    {
        var overlay = user.ToList();
        var replaced = overlay.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

        return FromDefinitions(shipped.Definitions
            .Where(d => !replaced.Contains(d.Id))
            .Concat(overlay));
    }

    /// <summary>Parse a list of definitions without validating them as a catalog.</summary>
    /// <remarks>For the user's overlay file, which is only meaningful merged over the shipped one.</remarks>
    public static List<SignalDefinition> ParseList(string json) =>
        JsonSerializer.Deserialize<List<SignalDefinition>>(json, JsonOptions)
            ?? throw new InvalidDataException("Signal catalog JSON did not deserialize to a list.");

    /// <summary>Write definitions in the same shape the catalog files use.</summary>
    public static string ToJson(IEnumerable<SignalDefinition> definitions) =>
        JsonSerializer.Serialize(definitions.ToList(), WriteOptions);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,

        // A unit is "°" or "λ"; the default encoder would write them as \u escapes, which
        // makes a file meant to be read and hand-edited needlessly hostile.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// What is wrong with one definition, or nothing.
    /// </summary>
    /// <remarks>
    /// The per-definition half of the load-time validation, exposed so an editor can refuse a
    /// definition as it is typed rather than after it has been saved and the next launch has
    /// dropped the whole overlay for it.
    /// </remarks>
    public static IReadOnlyList<string> Check(SignalDefinition d)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(d.Id))
        {
            problems.Add("a definition has an empty id");
        }

        if (string.IsNullOrWhiteSpace(d.Name))
        {
            problems.Add($"'{d.Id}': name must not be empty");
        }

        if (d.Decode is null)
        {
            problems.Add($"'{d.Id}': decode is missing");
            return problems;
        }

        if (d.Decode.ByteLength is not (1 or 2 or 4))
        {
            problems.Add($"'{d.Id}': byteLength must be 1, 2 or 4 (got {d.Decode.ByteLength})");
        }

        if (d.Decode.Mask is <= 0)
        {
            problems.Add($"'{d.Id}': mask must be a positive number of bits to keep");
        }

        if (d.Decode.ByteOffset < 0)
        {
            problems.Add($"'{d.Id}': byteOffset must not be negative");
        }

        if (d.Decode.Scale == 0 || !double.IsFinite(d.Decode.Scale) || !double.IsFinite(d.Decode.Offset))
        {
            problems.Add($"'{d.Id}': scale must be a non-zero number and offset a number");
        }

        if (d.DefaultRateHz <= 0 || !double.IsFinite(d.DefaultRateHz))
        {
            problems.Add($"'{d.Id}': defaultRateHz must be positive");
        }

        if (d.Min is not null && d.Max is not null && d.Min > d.Max)
        {
            problems.Add($"'{d.Id}': min is greater than max");
        }

        if (!d.Placeholder && d.Mode == 0x01 && d.Pid == 0)
        {
            problems.Add($"'{d.Id}': pid is missing — a signal whose identifier is still to be found is a placeholder (\"placeholder\": true)");
        }

        if (!string.IsNullOrWhiteSpace(d.Module) && d.ModuleAddress is null)
        {
            problems.Add($"'{d.Id}': module must be a module address in hex, 700–7F7 with the 8s digit clear (e.g. 726), not '{d.Module}'");
        }

        return problems;
    }

    /// <summary>
    /// Reject a bad catalog at load rather than letting it half-work. A duplicate id or a
    /// nonsensical decode spec would otherwise surface as a mysteriously wrong number on
    /// a dash, which is the worst possible way to find out.
    /// </summary>
    private static void Validate(List<SignalDefinition> definitions)
    {
        var problems = new List<string>();

        foreach (var group in definitions.GroupBy(d => d.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"duplicate signal id '{group.Key}'");
        }

        foreach (var d in definitions)
        {
            problems.AddRange(Check(d));
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException(
                "Signal catalog is invalid:" + Environment.NewLine +
                string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
        }
    }

    /// <summary>Every distinct bus the loaded signals need.</summary>
    public IReadOnlySet<CanBus> RequiredBuses => _byId.Values.Select(d => d.Bus).ToHashSet();
}

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
            if (string.IsNullOrWhiteSpace(d.Id))
            {
                problems.Add("a definition has an empty id");
            }

            if (d.Decode.ByteLength is not (1 or 2 or 4))
            {
                problems.Add($"'{d.Id}': byteLength must be 1, 2 or 4 (got {d.Decode.ByteLength})");
            }

            if (d.Decode.ByteOffset < 0)
            {
                problems.Add($"'{d.Id}': byteOffset must not be negative");
            }

            if (d.DefaultRateHz <= 0)
            {
                problems.Add($"'{d.Id}': defaultRateHz must be positive");
            }

            if (d.Min is not null && d.Max is not null && d.Min > d.Max)
            {
                problems.Add($"'{d.Id}': min is greater than max");
            }
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

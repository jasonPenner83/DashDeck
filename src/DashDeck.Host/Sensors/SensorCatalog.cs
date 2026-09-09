using System.IO;
using System.Text.Json;

namespace DashDeck.Host.Sensors;

/// <summary>
/// The loaded set of sensor definitions.
/// </summary>
/// <remarks>
/// Lives in the Host rather than in Core, unlike the signal catalog. Device sensors are a
/// shell concern: the engine runs headless on any OS and has no tablet to read, so putting
/// this in Core would add a concept the engine cannot use and would not build against
/// (ADR-0010).
/// <para>
/// It ships beside the signal catalog and is found the same way, because from the tablet's
/// point of view they are both just data files that travel with the binary.
/// </para>
/// </remarks>
public sealed class SensorCatalog
{
    private readonly Dictionary<string, SensorDefinition> _byId;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private SensorCatalog(IEnumerable<SensorDefinition> definitions) =>
        _byId = definitions.ToDictionary(d => d.Id, StringComparer.Ordinal);

    /// <summary>An empty catalog. What you get when the file is missing.</summary>
    public static SensorCatalog Empty { get; } = new([]);

    public IReadOnlyCollection<SensorDefinition> Definitions => _byId.Values;

    public int Count => _byId.Count;

    public bool TryGet(string id, out SensorDefinition definition) =>
        _byId.TryGetValue(id, out definition!);

    public static SensorCatalog FromJson(string json)
    {
        var definitions = JsonSerializer.Deserialize<List<SensorDefinition>>(json, JsonOptions)
            ?? throw new InvalidDataException("Sensor catalog JSON did not deserialize to a list.");

        Validate(definitions);
        return new SensorCatalog(definitions);
    }

    /// <summary>
    /// Load the catalog, or an empty one if there is no file.
    /// </summary>
    /// <remarks>
    /// Missing is tolerated; malformed is not. A dash with no sensor catalog loses the
    /// compass and the G meter and still starts, which is the right trade for a data file
    /// that is not load-bearing. A catalog with a duplicate id or an unknown channel is a
    /// mistake somebody made and should be told about.
    /// </remarks>
    public static SensorCatalog FromFileOrEmpty(string? path) =>
        path is not null && File.Exists(path) ? FromJson(File.ReadAllText(path)) : Empty;

    /// <summary>
    /// Reject a bad catalog at load rather than letting it half-work.
    /// </summary>
    /// <remarks>
    /// The same rule the signal catalog applies, and for the same reason: a definition that
    /// half-works surfaces as a mysteriously wrong number on a dash, which is the worst
    /// possible way to find out.
    /// </remarks>
    private static void Validate(List<SensorDefinition> definitions)
    {
        var problems = new List<string>();

        foreach (var group in definitions.GroupBy(d => d.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"duplicate sensor id '{group.Key}'");
        }

        foreach (var d in definitions)
        {
            if (string.IsNullOrWhiteSpace(d.Id))
            {
                problems.Add("a definition has an empty id");
            }

            if (d.DefaultRateHz <= 0)
            {
                problems.Add($"'{d.Id}': defaultRateHz must be positive");
            }

            if (d.Min is not null && d.Max is not null && d.Min > d.Max)
            {
                problems.Add($"'{d.Id}': min is greater than max");
            }

            // A channel the source cannot produce is the mistake most likely to be made by
            // hand, and the one that would otherwise read as a permanently absent sensor.
            var valid = d.Source switch
            {
                SensorSource.Compass => d.Channel is SensorChannel.Heading,
                SensorSource.Inclinometer => d.Channel is SensorChannel.Pitch or SensorChannel.Roll,
                SensorSource.Accelerometer => d.Channel is SensorChannel.Lateral or SensorChannel.Longitudinal,
                SensorSource.Gps => d.Channel is SensorChannel.Latitude or SensorChannel.Longitude or SensorChannel.GroundSpeed,
                _ => false,
            };

            if (!valid)
            {
                problems.Add($"'{d.Id}': a {d.Source} cannot produce {d.Channel}");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException(
                "Sensor catalog is invalid:" + Environment.NewLine +
                string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
        }
    }
}

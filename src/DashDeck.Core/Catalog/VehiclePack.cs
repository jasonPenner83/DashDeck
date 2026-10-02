using System.Text.Json;
using DashDeck.Core.Identity;

namespace DashDeck.Core.Catalog;

/// <summary>
/// Which vehicles a pack is for. Every field set must match; a field left out matches anything.
/// </summary>
/// <remarks>
/// Compared against the decoded <see cref="VehicleIdentity"/>, case-insensitively and with
/// displacement to a tenth of a litre, because decoders disagree about "F150" versus
/// "F-150" far less than they disagree about capitals, and about 2.7 versus 2.694 constantly.
/// A pack with no make at all is refused at load: it would match every vehicle on the road.
/// </remarks>
public sealed record VehiclePackMatch
{
    public string? Make { get; init; }

    public string? Model { get; init; }

    public int? YearMin { get; init; }

    public int? YearMax { get; init; }

    public double? DisplacementLitres { get; init; }

    public bool Matches(VehicleIdentity vehicle)
    {
        if (!Same(Make, vehicle.Make) || !Same(Model, vehicle.Model))
        {
            return false;
        }

        if ((YearMin is not null || YearMax is not null) && vehicle.ModelYear is not { } year)
        {
            return false;
        }

        if ((YearMin is { } min && vehicle.ModelYear < min) || (YearMax is { } max && vehicle.ModelYear > max))
        {
            return false;
        }

        return DisplacementLitres is not { } litres
            || (vehicle.DisplacementLitres is { } actual && Math.Abs(actual - litres) < 0.05);
    }

    private static bool Same(string? wanted, string? actual) =>
        wanted is null || string.Equals(Simplify(wanted), Simplify(actual), StringComparison.OrdinalIgnoreCase);

    // "F-150", "F150" and "F 150" are one truck.
    private static string Simplify(string? text) =>
        new((text ?? "").Where(char.IsLetterOrDigit).ToArray());
}

/// <summary>
/// Signals for one kind of vehicle, beyond the legislated standard set (ADR-0033).
/// </summary>
/// <remarks>
/// The standard catalog is what every OBD-II vehicle shares. A manufacturer's own values —
/// Ford's transmission temperature, its real oil temperature — are mode 22 PIDs that differ
/// by make, model, year and engine, so they live in a pack per vehicle in
/// <c>catalog/vehicles/</c>, and the one that matches the decoded VIN is laid over the standard
/// set at launch. That is how somebody else's truck gets its own signals without anyone editing
/// a file meant for Jason's.
/// <para>
/// A pack holds only what has been confirmed on a real vehicle with TEST (ADR-0032). An empty
/// pack is a correct pack; a guessed PID is not.
/// </para>
/// </remarks>
public sealed record VehiclePack
{
    /// <summary>What the screen calls it, e.g. "Ford F-150 2.7 EcoBoost (2018–2020)".</summary>
    public required string Name { get; init; }

    public required VehiclePackMatch Match { get; init; }

    public IReadOnlyList<SignalDefinition> Signals { get; init; } = [];

    /// <summary>The file it came from, for error messages.</summary>
    public string? FileName { get; init; }
}

/// <summary>Loading the packs and picking the ones that fit.</summary>
public static class VehiclePacks
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Read one pack.
    /// </summary>
    /// <exception cref="InvalidDataException">The JSON is unreadable, has no make to match on, or a signal is invalid.</exception>
    public static VehiclePack Parse(string json, string? fileName = null)
    {
        VehiclePack? pack;

        try
        {
            pack = JsonSerializer.Deserialize<VehiclePack>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{fileName ?? "pack"}: {ex.Message}", ex);
        }

        if (pack is null)
        {
            throw new InvalidDataException($"{fileName ?? "pack"}: empty");
        }

        if (string.IsNullOrWhiteSpace(pack.Match.Make))
        {
            throw new InvalidDataException($"{fileName ?? pack.Name}: match.make is required, or the pack would fit every vehicle");
        }

        var problems = pack.Signals.SelectMany(SignalCatalog.Check).ToList();
        problems.AddRange(pack.Signals
            .GroupBy(d => d.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"duplicate signal id '{g.Key}'"));

        if (problems.Count > 0)
        {
            throw new InvalidDataException($"{fileName ?? pack.Name}: {string.Join("; ", problems)}");
        }

        return pack with { FileName = fileName };
    }

    /// <summary>
    /// Every pack in a folder, and why any could not be read. Never throws: a bad pack is left
    /// out and reported, and the standard catalog still runs.
    /// </summary>
    public static (IReadOnlyList<VehiclePack> Packs, IReadOnlyList<string> Problems) LoadFolder(string? directory)
    {
        var packs = new List<VehiclePack>();
        var problems = new List<string>();

        if (directory is null || !Directory.Exists(directory))
        {
            return (packs, problems);
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                packs.Add(Parse(File.ReadAllText(path), Path.GetFileName(path)));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                problems.Add(ex.Message);
            }
        }

        return (packs, problems);
    }

    /// <summary>The packs that fit this vehicle. None for a vehicle nothing is known about.</summary>
    public static IReadOnlyList<VehiclePack> Select(IEnumerable<VehiclePack> packs, VehicleIdentity vehicle) =>
        vehicle.IsKnown ? [.. packs.Where(p => p.Match.Matches(vehicle))] : [];

    /// <summary>
    /// The standard catalog with the matching packs laid over it — the base the user's own
    /// overlay then goes on (ADR-0032). Validated as one catalog.
    /// </summary>
    public static SignalCatalog Apply(SignalCatalog standard, IEnumerable<VehiclePack> packs)
    {
        var catalog = standard;

        foreach (var pack in packs)
        {
            catalog = SignalCatalog.Overlay(catalog, pack.Signals);
        }

        return catalog;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DashDeck.IdHunter;

/// <summary>One thing to find, and how the guide finds it (ADR-0044). Read from <c>targets.json</c>.</summary>
internal sealed record HuntTarget
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>The DashDeck signal it would fill, if one is waiting (<c>warning.door</c>).</summary>
    public string Signal { get; init; } = "";

    /// <summary><c>listen</c>, <c>follow</c> or <c>match</c>.</summary>
    public string Method { get; init; } = "";

    /// <summary>What state the truck must be in before starting.</summary>
    public string Needs { get; init; } = "";

    // listen
    public IReadOnlyList<string> Buses { get; init; } = ["ms", "hs"];

    public int Hold { get; init; } = 5;

    public IReadOnlyList<HuntStep> Steps { get; init; } = [];

    // follow and match
    public IReadOnlyList<string> Modules { get; init; } = [];

    public IReadOnlyList<string> Ranges { get; init; } = [];

    /// <summary>Where the simulator's invented identifiers are, for <c>--simulate</c>.</summary>
    public IReadOnlyList<string> SimulateRanges { get; init; } = [];

    // follow
    public string Reference { get; init; } = "coolant";

    public string Instructions { get; init; } = "";

    public double Minutes { get; init; } = 5;

    public bool Blips { get; init; }

    // match
    public int Rounds { get; init; } = 1;

    public string Between { get; init; } = "";

    public IReadOnlyList<HuntReading> Readings { get; init; } = [];
}

/// <summary>One state a listen holds the truck in.</summary>
internal sealed record HuntStep
{
    public string Label { get; init; } = "";

    public double State { get; init; }

    /// <summary>What the person is asked to do to get there.</summary>
    public string Do { get; init; } = "";

    /// <summary>Under <c>--simulate</c>, the synthetic cabin controls set for this step.</summary>
    public Dictionary<string, double> Simulate { get; init; } = [];
}

/// <summary>One number a match asks the person to read off the cluster.</summary>
internal sealed record HuntReading
{
    public string Label { get; init; } = "";

    public string Ask { get; init; } = "";

    /// <summary>Under <c>--simulate</c>, the synthetic value shown as the cluster's.</summary>
    public string Simulate { get; init; } = "";
}

internal sealed record HuntTargetFile
{
    public IReadOnlyList<HuntTarget> Targets { get; init; } = [];

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Read the checklist; a target with no name or an unknown method is left out and named.</summary>
    public static (IReadOnlyList<HuntTarget> Targets, IReadOnlyList<string> Problems) Load(string path)
    {
        var file = JsonSerializer.Deserialize<HuntTargetFile>(File.ReadAllText(path), Options) ?? new HuntTargetFile();
        var problems = new List<string>();
        var kept = new List<HuntTarget>();

        foreach (var target in file.Targets)
        {
            if (target.Name.Length == 0 || target.Method is not ("listen" or "follow" or "match"))
            {
                problems.Add($"'{target.Id}': needs a name and a method of listen, follow or match.");
            }
            else if (target.Method == "listen" && target.Steps.Select(s => s.State).Distinct().Count() < 2)
            {
                problems.Add($"'{target.Id}': a listen needs steps in at least two states.");
            }
            else if (target.Method == "match" && target.Readings.Count == 0)
            {
                problems.Add($"'{target.Id}': a match needs at least one reading.");
            }
            else
            {
                kept.Add(target);
            }
        }

        return (kept, problems);
    }
}

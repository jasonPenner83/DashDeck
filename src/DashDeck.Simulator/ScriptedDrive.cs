namespace DashDeck.Simulator;

/// <summary>What the truck is doing for a stretch of time.</summary>
/// <param name="Name">Label, for debug output and test failure messages.</param>
/// <param name="Seconds">Duration of the segment.</param>
/// <param name="TargetSpeedKph">Speed the driver is aiming for.</param>
/// <param name="GradePercent">Road grade. Positive is uphill; costs fuel.</param>
/// <param name="TowingKg">Trailer mass, if any.</param>
public sealed record DriveSegment(
    string Name,
    double Seconds,
    double TargetSpeedKph,
    double GradePercent = 0,
    double TowingKg = 0);

/// <summary>
/// A repeatable drive with a known outcome.
/// </summary>
/// <remarks>
/// The reason mock-first development is better here rather than merely cheaper: a scripted
/// drive has an exact expected fuel consumption, so the trip computer's arithmetic can be
/// asserted rather than eyeballed. A real drive can never provide that.
/// </remarks>
public sealed record ScriptedDrive(string Name, string Description, IReadOnlyList<DriveSegment> Segments)
{
    public double TotalSeconds => Segments.Sum(s => s.Seconds);
}

/// <summary>The built-in drives. Kept as regression fixtures after hardware bring-up.</summary>
public static class Drives
{
    /// <summary>Cold start, warm-up, stop-and-go city driving. The P0 demonstration drive.</summary>
    public static readonly ScriptedDrive ColdStartCity = new(
        "cold-start-city",
        "Cold start in a Manitoba autumn, warm-up idle, then stop-and-go city driving.",
        [
            new DriveSegment("cold idle", 45, 0),
            new DriveSegment("pull away", 15, 40),
            new DriveSegment("city cruise", 60, 50),
            new DriveSegment("stop at lights", 25, 0),
            new DriveSegment("accelerate", 15, 60),
            new DriveSegment("city cruise", 90, 55),
            new DriveSegment("stop", 20, 0),
            new DriveSegment("pull away", 20, 45),
            new DriveSegment("city cruise", 70, 50),
            new DriveSegment("park and idle", 20, 0),
        ]);

    /// <summary>Steady highway running. The cleanest fixture for fuel-economy arithmetic.</summary>
    public static readonly ScriptedDrive HighwayCruise = new(
        "highway-cruise",
        "Warm engine, sustained 100 km/h cruise on flat ground.",
        [
            new DriveSegment("merge", 30, 100),
            new DriveSegment("cruise", 600, 100),
            new DriveSegment("exit", 30, 0),
        ]);

    /// <summary>Loaded, uphill, hot. Exercises the parts of the model that stress the engine.</summary>
    public static readonly ScriptedDrive TowingPull = new(
        "towing-pull",
        "Towing 2,000 kg up a sustained 6% grade at highway speed.",
        [
            new DriveSegment("rolling start", 40, 80, GradePercent: 2, TowingKg: 2000),
            new DriveSegment("the climb", 420, 90, GradePercent: 6, TowingKg: 2000),
            new DriveSegment("crest and level", 120, 95, GradePercent: 0, TowingKg: 2000),
        ]);

    /// <summary>Parked and running. Useful for staleness and idle-timeout behaviour.</summary>
    public static readonly ScriptedDrive Idle = new(
        "idle",
        "Parked with the engine running.",
        [new DriveSegment("idle", 300, 0)]);

    public static readonly IReadOnlyList<ScriptedDrive> All =
        [ColdStartCity, HighwayCruise, TowingPull, Idle];

    public static ScriptedDrive ByName(string name) =>
        All.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException(
            $"Unknown drive '{name}'. Known drives: {string.Join(", ", All.Select(d => d.Name))}.",
            nameof(name));
}

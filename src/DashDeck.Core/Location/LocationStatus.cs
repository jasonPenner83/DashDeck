using System.Globalization;
using DashDeck.Abstractions;

namespace DashDeck.Core.Location;

/// <summary>
/// How a fix reads on a screen — one line, the same words everywhere.
/// </summary>
/// <remarks>
/// The console probe and the Settings readout answer the same question (is the phone's GPS
/// getting here, and what does it say), so they say it the same way rather than each inventing a
/// format. Quality leads, because a position with no quality beside it is the one thing this dash
/// never shows; the age is separate, because a fix that stopped arriving looks identical to a
/// live one until you see how old it is.
/// </remarks>
public static class LocationStatus
{
    /// <summary>Quality, position, speed and course. Speed and course read <c>——</c> when absent.</summary>
    public static string Describe(LocationFix fix)
    {
        var speed = double.IsNaN(fix.GroundSpeedKmh)
            ? "—— km/h"
            : string.Create(CultureInfo.InvariantCulture, $"{fix.GroundSpeedKmh:0.#} km/h");

        var course = double.IsNaN(fix.CourseDegrees)
            ? "course ———"
            : string.Create(CultureInfo.InvariantCulture, $"course {fix.CourseDegrees:0}°");

        if (!fix.HasFix)
        {
            return $"{Quality(fix.Quality)} — no position";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Quality(fix.Quality)} — {fix.Latitude:0.00000}, {fix.Longitude:0.00000} · {speed} · {course}");
    }

    /// <summary>
    /// How long ago the receiver produced it, in the shape a glance can read. Negative ages — a
    /// phone whose clock runs ahead — read as <c>just now</c> rather than as a time in the future.
    /// </summary>
    public static string Age(LocationFix fix, DateTimeOffset nowUtc)
    {
        var age = nowUtc - fix.TakenUtc;

        if (age <= TimeSpan.FromSeconds(1))
        {
            return "just now";
        }

        return age < TimeSpan.FromMinutes(1)
            ? string.Create(CultureInfo.InvariantCulture, $"{age.TotalSeconds:0} s ago")
            : string.Create(CultureInfo.InvariantCulture, $"{age.TotalMinutes:0} min ago");
    }

    /// <summary>The word for a quality, as the rest of the dash says it.</summary>
    public static string Quality(SignalQuality quality) => quality switch
    {
        SignalQuality.Live => "FIX",
        SignalQuality.Simulated => "SIMULATED",
        SignalQuality.Stale => "STALE",
        _ => "NO FIX",
    };
}

using System.Text.Json.Serialization;
using DashDeck.Host.Settings;

namespace DashDeck.Host.Sensors;

/// <summary>
/// What "level, pointing forward" means for this tablet in this mount.
/// </summary>
/// <remarks>
/// <b>Nothing the accelerometer or inclinometer says is usable without this.</b> A Surface on
/// a kickstand reads 69° of pitch and 0.91 g on one axis while sitting perfectly still, and a
/// windscreen mount is no better. Rendering that raw would put a third of a g of cornering
/// force on screen in a parked truck — a confidently wrong number, which is the failure this
/// project refuses everywhere else.
/// <para>
/// So the reference is captured once, parked and level, and every reading is expressed
/// relative to it. Attitude is a simple difference. Acceleration is not: gravity has to be
/// removed as a <em>vector</em> and what remains resolved into the truck's own axes, because
/// the mount is at an arbitrary angle and the tablet's idea of "sideways" is not the truck's.
/// </para>
/// <para>
/// Its own file rather than more fields in <c>settings.json</c>, which is described as
/// deliberately small and deliberately flat and would stop being either. It is also a
/// different kind of thing — a property of the mount, not a preference — and re-levelling
/// should not be entangled with the theme.
/// </para>
/// </remarks>
public sealed record MountReference
{
    /// <summary>Inclinometer pitch when level, in degrees.</summary>
    [JsonPropertyName("pitch")]
    public double Pitch { get; init; }

    /// <summary>Inclinometer roll when level, in degrees.</summary>
    [JsonPropertyName("roll")]
    public double Roll { get; init; }

    /// <summary>The gravity vector in tablet axes when level, in g.</summary>
    [JsonPropertyName("gx")]
    public double Gx { get; init; }

    [JsonPropertyName("gy")]
    public double Gy { get; init; }

    [JsonPropertyName("gz")]
    public double Gz { get; init; }

    /// <summary>When it was captured. Shown, because a stale reference is worse than none.</summary>
    [JsonPropertyName("capturedUtc")]
    public DateTimeOffset? CapturedUtc { get; init; }

    /// <summary>
    /// True when this reference was actually taken rather than defaulted.
    /// </summary>
    /// <remarks>
    /// A zero gravity vector is not a plausible measurement — the tablet is always in a
    /// gravitational field — so its absence is a reliable "never levelled".
    /// </remarks>
    [JsonIgnore]
    public bool IsSet => CapturedUtc is not null && Magnitude > 0.5;

    [JsonIgnore]
    private double Magnitude => Math.Sqrt((Gx * Gx) + (Gy * Gy) + (Gz * Gz));

    /// <summary>
    /// Resolve a measured acceleration into the truck's own axes, with gravity removed.
    /// </summary>
    /// <remarks>
    /// Three steps, and the middle one is the one that is easy to get wrong.
    /// <list type="number">
    ///   <item>
    ///     Subtract the reference gravity vector. What remains is linear acceleration — the
    ///     part that is the truck moving rather than the earth pulling.
    ///   </item>
    ///   <item>
    ///     Build the truck's axes from the mount, not from the tablet. "Down" is the
    ///     reference gravity direction; <b>across</b> is the tablet's own X axis with any
    ///     component of down removed, which is what makes an arbitrarily tilted mount work;
    ///     <b>forward</b> is the cross product of the two, so the frame is orthonormal by
    ///     construction rather than by assumption.
    ///   </item>
    ///   <item>Project onto those axes.</item>
    /// </list>
    /// <para>
    /// This assumes the tablet's X axis runs across the vehicle — true for any portrait dash
    /// mount, and the reason the shell is portrait-first anyway. A tablet mounted at 45° of
    /// yaw would need the yaw captured too, and is not supported.
    /// </para>
    /// </remarks>
    public (double Lateral, double Longitudinal) Resolve(double ax, double ay, double az)
    {
        if (!IsSet)
        {
            return (0, 0);
        }

        var magnitude = Magnitude;
        var (dx, dy, dz) = (ax - Gx, ay - Gy, az - Gz);

        // Down, normalised.
        var (ux, uy, uz) = (Gx / magnitude, Gy / magnitude, Gz / magnitude);

        // Across: the tablet's X axis with the "down" component projected out.
        var dot = ux;
        var (lx, ly, lz) = (1 - (dot * ux), -(dot * uy), -(dot * uz));
        var lLength = Math.Sqrt((lx * lx) + (ly * ly) + (lz * lz));

        if (lLength < 1e-6)
        {
            // The tablet is lying on its side hard enough that "across" is undefined. Rather
            // than dividing by nearly zero and rendering a wild number, say nothing.
            return (0, 0);
        }

        (lx, ly, lz) = (lx / lLength, ly / lLength, lz / lLength);

        // Forward = down × across, which is orthogonal to both by construction.
        var (fx, fy, fz) = ((uy * lz) - (uz * ly), (uz * lx) - (ux * lz), (ux * ly) - (uy * lx));

        return (
            (dx * lx) + (dy * ly) + (dz * lz),
            (dx * fx) + (dy * fy) + (dz * fz));
    }
}

/// <summary>Loads and saves the mount reference. Same folder, same promises, as everything else.</summary>
public static class MountReferenceStore
{
    /// <summary>Why the last load or save failed, if it did.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Where it lives. Beside the settings and the dashboard, outside the app folder.</summary>
    public static string Path => JsonFile.InLocalAppData("mount.json");

    /// <summary>Read the reference, or an unset one.</summary>
    public static MountReference Load()
    {
        var reference = JsonFile.Load<MountReference>(Path, out var error);
        LastError = error;
        return reference ?? new MountReference();
    }

    /// <summary>Write it out. Never throws.</summary>
    public static void Save(MountReference reference)
    {
        JsonFile.Save(Path, reference, out var error);
        LastError = error;
    }
}

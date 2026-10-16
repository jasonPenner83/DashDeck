using System.Globalization;

namespace DashDeck.Core.Updates;

/// <summary>
/// A version of DashDeck, as it appears in the three places that disagree about how to write it.
/// </summary>
/// <remarks>
/// The tags say <c>v.0.9.0</c> — with a stray dot that arrived at v.0.8.0 and stayed — the release
/// assets say <c>v0.9.0</c>, and the assembly says <c>0.9.0.0</c> or <c>0.9.0+1a2b3c</c>. All four
/// mean the same build, so the comparison has to read all four rather than the tagging being
/// tidied up under four years of releases.
/// <para>
/// Three parts only. A fourth, as assemblies carry, is dropped rather than compared: nothing in
/// this project has ever meant anything by it, and treating 0.9.0.0 as older than 0.9.0.1 would
/// invent a distinction out of a zero.
/// </para>
/// </remarks>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch)
    : IComparable<ReleaseVersion>
{
    /// <summary>
    /// Read a version from a tag, an asset name's version part, or an assembly version. False for
    /// anything that is not one, which includes the empty string and <c>unknown</c>.
    /// </summary>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();

        // Build metadata and pre-release suffixes: 0.9.0+1a2b3c, 0.9.0-rc1. Neither is compared.
        foreach (var cut in new[] { '+', '-' })
        {
            var at = s.IndexOf(cut);

            if (at > 0)
            {
                s = s[..at];
            }
        }

        // A leading v, and the stray dot after it that this project's tags carry.
        if (s.Length > 0 && (s[0] is 'v' or 'V'))
        {
            s = s[1..];

            if (s.StartsWith('.'))
            {
                s = s[1..];
            }
        }

        var parts = s.Split('.');

        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        if (!TryPart(parts[0], out var major) || !TryPart(parts[1], out var minor))
        {
            return false;
        }

        var patch = 0;

        if (parts.Length >= 3 && !TryPart(parts[2], out patch))
        {
            return false;
        }

        // A fourth part must still be a number to be ignored — "0.9.0.beta" is not a version.
        if (parts.Length == 4 && !TryPart(parts[3], out _))
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, patch);
        return true;
    }

    /// <summary>Whether <paramref name="other"/> is worth downloading over this one.</summary>
    public bool IsOlderThan(ReleaseVersion other) => CompareTo(other) < 0;

    /// <inheritdoc />
    public int CompareTo(ReleaseVersion other)
    {
        var major = Major.CompareTo(other.Major);

        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);

        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    /// <summary>Plain three-part form, as a screen should show it.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    private static bool TryPart(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}

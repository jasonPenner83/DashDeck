using System.Globalization;

namespace DashDeck.Core.Emulation;

/// <summary>Where Android is drawn when it is showing (ADR-0075).</summary>
public enum AndroidPlacement
{
    /// <summary>All six bands, from the nav's ANDROID cell — the stage collapsed behind it.</summary>
    Full,

    /// <summary>Handed to the stage as an occupant, with the dash below it.</summary>
    Stage,
}

/// <summary>
/// Which placement Android is in, and what the ANDROID cell's three presses do to it (ADR-0075).
/// </summary>
/// <remarks>
/// Pure, like <see cref="Maps.MapView"/>, so the rules are tested without a UI thread. Two
/// placements rather than the map's three: an emulated device in the bottom third would be a
/// picture too small to touch.
/// </remarks>
public readonly record struct AndroidView(AndroidPlacement Placement)
{
    /// <summary>Where Android opens: full screen.</summary>
    public static readonly AndroidView Default = new(AndroidPlacement.Full);

    public bool IsFull => Placement is AndroidPlacement.Full;

    public bool IsOnStage => Placement is AndroidPlacement.Stage;

    /// <summary>A tap on the cell: full screen, from wherever it was — so a tap brings it back off the stage.</summary>
    public AndroidView Tapped() => new(AndroidPlacement.Full);

    /// <summary>A double tap: onto the stage. Idempotent.</summary>
    public AndroidView DoubleTapped() => new(AndroidPlacement.Stage);
}

/// <summary>What a press on a nav cell was.</summary>
public enum PressKind
{
    /// <summary>Down and up, quickly.</summary>
    Tap,

    /// <summary>A second tap soon after the first.</summary>
    DoubleTap,

    /// <summary>Held down — acted on while still held, so a lift after it means nothing.</summary>
    Hold,
}

/// <summary>
/// Telling a tap from a double tap from a hold on one cell (ADR-0075).
/// </summary>
/// <remarks>
/// A tap is acted on at once, never held back to see whether a second follows: the first tap of a
/// double tap opens Android full screen and the second sends it on to the stage, so acting at once
/// costs nothing and a single tap is never slow. A hold is acted on when the time is up, with the
/// finger still down, because a menu that waits for the lift feels broken.
/// </remarks>
public static class PressGesture
{
    /// <summary>How long a press is held before it is a hold.</summary>
    public static readonly TimeSpan HoldAfter = TimeSpan.FromMilliseconds(550);

    /// <summary>How soon a second tap must follow the first to make a double tap.</summary>
    public static readonly TimeSpan DoubleTapWithin = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// What a lift means, or null when it means nothing — the hold already happened.
    /// </summary>
    /// <param name="down">When the finger went down.</param>
    /// <param name="up">When it lifted.</param>
    /// <param name="previousTap">When the last press that counted as a tap lifted, or null.</param>
    /// <param name="holdFired">True when this press already opened the hold's menu.</param>
    public static PressKind? Released(TimeSpan down, TimeSpan up, TimeSpan? previousTap, bool holdFired)
    {
        if (holdFired)
        {
            return null;
        }

        if (up - down >= HoldAfter)
        {
            return PressKind.Hold;
        }

        return previousTap is { } last && up - last <= DoubleTapWithin && up >= last ? PressKind.DoubleTap : PressKind.Tap;
    }
}

/// <summary>Finding the AVDs and picking one for each flavour (ADR-0075).</summary>
public static class AndroidChoice
{
    /// <summary>What the hold menu calls a flavour.</summary>
    public static string Label(AndroidFlavour flavour) =>
        flavour == AndroidFlavour.Automotive ? "ANDROID AUTOMOTIVE" : "ANDROID";

    /// <summary>A flavour from its saved name, regular when the name is unknown.</summary>
    public static AndroidFlavour Parse(string? saved) =>
        Enum.TryParse<AndroidFlavour>(saved, ignoreCase: true, out var flavour) ? flavour : AndroidFlavour.Regular;

    /// <summary>
    /// Every AVD in the folder, read from the files the emulator keeps: <c>&lt;name&gt;.ini</c> points
    /// at the AVD's folder, whose <c>config.ini</c> says what it is. No process is started.
    /// </summary>
    public static IReadOnlyList<AvdInfo> List(string avdHome, Func<string, IEnumerable<string>> filesIn, Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(filesIn);
        ArgumentNullException.ThrowIfNull(read);

        var avds = new List<AvdInfo>();

        foreach (var pointer in filesIn(avdHome).Where(f => f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(pointer);
            var folder = (read(pointer) ?? "").Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("path=", StringComparison.OrdinalIgnoreCase))?[5..].Trim();

            var config = read(Path.Combine(folder ?? Path.Combine(avdHome, name + ".avd"), "config.ini"))
                ?? (folder is null ? null : read(Path.Combine(avdHome, name + ".avd", "config.ini")));

            if (config is not null)
            {
                avds.Add(AvdInfo.Parse(name, config));
            }
        }

        return avds;
    }

    /// <summary>
    /// The AVD to run for a flavour: the one named before if it is still there and of that flavour,
    /// else the first of that flavour, preferring x86_64 — the image that runs fast on an Intel tablet.
    /// Null when there is none.
    /// </summary>
    public static AvdInfo? Pick(IReadOnlyList<AvdInfo> avds, AndroidFlavour flavour, string? named = null)
    {
        ArgumentNullException.ThrowIfNull(avds);

        var ofFlavour = avds.Where(a => a.Flavour == flavour).ToList();

        return ofFlavour.FirstOrDefault(a => string.Equals(a.Name, named, StringComparison.Ordinal))
            ?? ofFlavour.FirstOrDefault(a => a.Abi == "x86_64")
            ?? ofFlavour.FirstOrDefault();
    }

    /// <summary>
    /// A point on the picture as drawn, as fractions of it, in the device's own pixels — or null off
    /// the picture. The device's size is the emulator's, which is not the picture's: frames are asked
    /// for at the stage's size and scaled.
    /// </summary>
    public static (int X, int Y)? ToDevice(double x, double y, int deviceWidth, int deviceHeight)
    {
        if (deviceWidth <= 0 || deviceHeight <= 0 || double.IsNaN(x) || double.IsNaN(y) || x is < 0 or > 1 || y is < 0 or > 1)
        {
            return null;
        }

        return ((int)Math.Min(deviceWidth - 1, Math.Round(x * deviceWidth)), (int)Math.Min(deviceHeight - 1, Math.Round(y * deviceHeight)));
    }

    /// <summary>One line for the menu under a flavour: the AVD it would run, or why it cannot.</summary>
    public static string Describe(AvdInfo? avd, AndroidFlavour flavour) => avd is null
        ? string.Create(CultureInfo.InvariantCulture, $"No {(flavour == AndroidFlavour.Automotive ? "Automotive" : "phone or tablet")} AVD — make one in Android Studio")
        : avd.Width > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{avd.Name} · {avd.Width}×{avd.Height}")
            : avd.Name;
}

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
    public static (int X, int Y)? ToDevice(double x, double y, int deviceWidth, int deviceHeight) =>
        ToDevice(x, y, deviceWidth, deviceHeight, quarterTurns: 0);

    /// <summary>
    /// A point on a picture the emulator turned upright — after the device was rotated — in the
    /// device's own, unturned pixels, which is what touch is given in. <paramref name="quarterTurns"/>
    /// is the frame's own rotation (the emulator's <c>SkinRotation</c>, 0–3), and the mapping is the
    /// one Android Studio's embedded emulator uses.
    /// </summary>
    /// <param name="deviceWidth">The device's width unturned — its native screen.</param>
    public static (int X, int Y)? ToDevice(double x, double y, int deviceWidth, int deviceHeight, int quarterTurns)
    {
        if (deviceWidth <= 0 || deviceHeight <= 0 || double.IsNaN(x) || double.IsNaN(y) || x is < 0 or > 1 || y is < 0 or > 1)
        {
            return null;
        }

        var (u, v) = (((quarterTurns % 4) + 4) % 4) switch
        {
            1 => (1 - y, x),
            2 => (1 - x, 1 - y),
            3 => (y, 1 - x),
            _ => (x, y),
        };

        return ((int)Math.Min(deviceWidth - 1, Math.Round(u * deviceWidth)), (int)Math.Min(deviceHeight - 1, Math.Round(v * deviceHeight)));
    }

    /// <summary>
    /// How far to turn the device so it fills the region it is drawn in: 90° when the region's shape
    /// is the other way round from the device's own screen, else 0 — or null when it is not to be
    /// turned at all.
    /// </summary>
    /// <remarks>
    /// Automotive is never turned: a car's screen does not rotate, its interface does not follow the
    /// device's rotation, and the emulator would turn the picture of an interface that stayed put —
    /// sideways. A region within a few percent of square is left as it is.
    /// </remarks>
    public static int? TurnFor(AndroidFlavour flavour, int deviceWidth, int deviceHeight, double regionWidth, double regionHeight)
    {
        if (flavour == AndroidFlavour.Automotive || deviceWidth <= 0 || deviceHeight <= 0 || !(regionWidth > 0) || !(regionHeight > 0))
        {
            return null;
        }

        var region = regionWidth / regionHeight;
        if (Math.Abs(region - 1) < 0.05)
        {
            return null;
        }

        var deviceIsWide = deviceWidth >= deviceHeight;
        var regionIsWide = region > 1;

        return deviceIsWide == regionIsWide ? 0 : 90;
    }

    /// <summary>One line for the menu under a flavour: the AVD it would run, or why it cannot.</summary>
    public static string Describe(AvdInfo? avd, AndroidFlavour flavour) => avd is null
        ? string.Create(CultureInfo.InvariantCulture, $"No {(flavour == AndroidFlavour.Automotive ? "Automotive" : "phone or tablet")} AVD — make one in Android Studio")
        : avd.Width > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{avd.Name} · {avd.Width}×{avd.Height}")
            : avd.Name;
}

/// <summary>Android's own keys, as the dash's key bar presses them (ADR-0075 addendum).</summary>
public enum AndroidKey
{
    Back,
    Home,
    Recents,
}

/// <summary>What the emulator calls each key, and which flavour has it.</summary>
public static class AndroidKeys
{
    /// <summary>
    /// The emulator's name for a key — the W3C key value it translates, as Android Studio's own
    /// toolbar sends them.
    /// </summary>
    public static string Name(AndroidKey key) => key switch
    {
        AndroidKey.Back => "GoBack",
        AndroidKey.Home => "GoHome",
        AndroidKey.Recents => "AppSwitch",
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    /// <summary>
    /// The keys a flavour has: Automotive has no recent apps — its own bar is the way between them.
    /// </summary>
    public static IReadOnlyList<AndroidKey> For(AndroidFlavour flavour) =>
        flavour == AndroidFlavour.Automotive ? [AndroidKey.Back, AndroidKey.Home] : [AndroidKey.Back, AndroidKey.Home, AndroidKey.Recents];
}

/// <summary>How much of the tablet to give the emulator (ADR-0075).</summary>
public static class EmulatorResources
{
    /// <summary>
    /// Memory and processor cores for the emulator, or null to leave the AVD's own. A 16 GB tablet
    /// gives Android 4 GB, where the AVDs made in Android Studio ask for 2 — and the AVDs on the
    /// Surface stalled with a launcher that stopped responding. An 8 GB one keeps the AVD's own, to
    /// leave the dash and Windows room. Cores: half the logical processors, two to eight. Six of the
    /// Surface's eight was tried and was worse: it has four real cores, and the emulator's own threads
    /// — translating Android's drawing, reading frames back — and the dash run beside Android's, so
    /// with six for Android they fought for the rest (the processor at 85 %, the GPU idle).
    /// </summary>
    public static (int? MemoryMb, int? Cores) For(long totalMemoryBytes, int logicalProcessors)
    {
        const long GiB = 1L << 30;

        int? memory = totalMemoryBytes >= 14 * GiB ? 4096 : null;
        int? cores = logicalProcessors >= 4 ? Math.Clamp(logicalProcessors / 2, 2, 8) : null;

        return (memory, cores);
    }
}

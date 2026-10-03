namespace DashDeck.Host.Stage.Gauges;

/// <summary>
/// The warning lights' icons (ADR-0041): SVG path data on a 24 × 24 grid, drawn for DashDeck in
/// the spirit of the ISO 2575 symbols a dash uses — simple enough to read at a glance, original
/// so the repository can publish them.
/// </summary>
/// <remarks>
/// A layout names one (<c>"icon": "oil"</c>) or gives its own path data instead
/// (<c>"icon": "M4 4h16v16H4z"</c>). Kept free of WPF so the names can be checked when a layout
/// loads; the face turns the data into a shape.
/// </remarks>
public static class WarningIcons
{
    /// <summary>One icon: its path data, and whether holes in it are cut by the even-odd rule.</summary>
    public sealed record Icon(string Path, bool EvenOdd = false);

    private static readonly Dictionary<string, Icon> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        // An engine block in profile: the check-engine light.
        ["checkEngine"] = new("M 3 10 h 2 V 8 h 3 V 6 h 7 v 2 h 2 l 2 2 h 2 v -2 h 2 v 8 h -2 v -2 h -2 l -2 3 H 9 l -2 -2 H 5 v 2 H 3 z"),

        // An oil can with a drip.
        ["oil"] = new("M 2 9 h 3 l 1 2 h 5 l 2 -2 9 2 -7 7 H 6 l -2 -4 H 2 z M 19.5 15.5 c .8 1.2 1.3 2 1.3 2.6 a 1.3 1.3 0 0 1 -2.6 0 c 0 -.6 .5 -1.4 1.3 -2.6 z"),

        // A battery with its terminals and + and −.
        ["battery"] = new("M 2 7 h 20 v 13 H 2 z M 5 4 h 4 v 3 H 5 z M 15 4 h 4 v 3 h -4 z M 5 12.5 h 5 v 1.6 H 5 z M 15.2 10.6 h 1.6 v 2.4 h 2.4 v 1.6 h -2.4 V 17 h -1.6 v -2.4 h -2.4 v -1.6 h 2.4 z", true),

        // A thermometer standing in waves: the coolant is hot.
        ["coolant"] = new("M 11 2 h 2 v 10.3 a 3.2 3.2 0 1 1 -2 0 z M 14 4 h 3 v 1.6 h -3 z M 14 7 h 3 v 1.6 h -3 z M 14 10 h 3 v 1.6 h -3 z M 1 20 c 2 0 2 -1.6 4 -1.6 s 2 1.6 4 1.6 2 -1.6 4 -1.6 2 1.6 4 1.6 2 -1.6 4 -1.6 2 1.6 2 1.6 v 1.8 s 0 -1.6 -2 -1.6 -2 1.6 -4 1.6 -2 -1.6 -4 -1.6 -2 1.6 -4 1.6 -2 -1.6 -4 -1.6 -2 1.6 -4 1.6 z"),

        // A fuel pump with its hose.
        ["fuel"] = new("M 4 3 h 10 v 18 H 4 z M 6 5 v 5 h 6 V 5 z M 14 9 h 1.5 l 2 2 v 6.5 a .8 .8 0 0 0 1.6 0 V 9.5 L 16.4 7 l 1.1 -1.1 3.1 3.1 v 8.5 a 2.4 2.4 0 0 1 -4.8 0 V 11.6 l -.8 -.8 H 14 z", true),

        // A seated figure with a belt across it.
        ["seatbelt"] = new("M 12 1.5 a 2.6 2.6 0 1 1 0 5.2 2.6 2.6 0 0 1 0 -5.2 z M 8.2 8 h 7.6 l 1.4 7.5 h -2.4 L 14 22.5 h -4 l -.8 -7 H 6.8 z M 6.4 9.6 l 11.4 7.6 -1 1.5 L 5.4 11.1 z"),

        // A door standing open from the side of a car.
        ["door"] = new("M 4 3 h 9.5 l 5.5 6 v 12 H 4 z M 6 5 v 5.5 h 10.6 L 13 5 z M 13.5 13 h 3 v 1.6 h -3 z", true),

        // A circle with an exclamation mark: the brake or parking brake.
        ["brake"] = new("M 12 3.5 a 8.5 8.5 0 1 1 0 17 8.5 8.5 0 0 1 0 -17 z M 11 7 h 2 v 7 h -2 z M 11 15.5 h 2 v 2 h -2 z M 2.6 6.5 l 1.4 1 C 3 9 2.6 10.5 2.6 12 s .4 3 1.4 4.5 l -1.4 1 C 1.4 15.8 .8 13.9 .8 12 s .6 -3.8 1.8 -5.5 z M 21.4 6.5 c 1.2 1.7 1.8 3.6 1.8 5.5 s -.6 3.8 -1.8 5.5 l -1.4 -1 c 1 -1.5 1.4 -3 1.4 -4.5 s -.4 -3 -1.4 -4.5 z", true),

        // A tyre in section with an exclamation mark: low tyre pressure.
        ["tpms"] = new("M 5 6 c 1 -1.8 3.5 -2.5 7 -2.5 s 6 .7 7 2.5 l 1.2 7 c 0 3.2 -1 5.8 -3 7 H 16 l -1 -2 H 9 l -1 2 H 6.8 c -2 -1.2 -3 -3.8 -3 -7 z M 11 7.5 h 2 v 6 h -2 z M 11 15 h 2 v 2 h -2 z", true),
    };

    /// <summary>The built-in names, for a file's error message and the reference.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. ByName.Keys];

    /// <summary>A built-in name, or text that looks like path data (it starts with a move).</summary>
    public static bool IsKnown(string icon) =>
        ByName.ContainsKey(icon.Trim()) || IsPathData(icon);

    /// <summary>The icon to draw: a built-in by name, or the layout's own path data.</summary>
    public static Icon? Find(string icon) =>
        ByName.TryGetValue(icon.Trim(), out var known) ? known
        : IsPathData(icon) ? new Icon(icon.Trim(), true)
        : null;

    private static bool IsPathData(string icon) => icon.TrimStart() is ['M' or 'm', ..];
}

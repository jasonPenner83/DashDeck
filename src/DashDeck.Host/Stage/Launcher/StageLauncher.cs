using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DashDeck.Host.Stage.Launcher;

/// <summary>Where a launcher came from.</summary>
public enum LauncherOrigin
{
    /// <summary>Compiled in: what DashDeck offers when there is no file.</summary>
    BuiltIn,

    /// <summary>The user's <c>launcher.json</c>.</summary>
    Yours,
}

/// <summary>
/// The kinds of thing an entry can put on the stage (ADR-0038). Each is one occupant class; the
/// file says which, and with what.
/// </summary>
public static class LauncherTypes
{
    /// <summary>A stage layout (ADR-0037) — the theme's, or the one named by <c>layout</c>.</summary>
    public const string Gauges = "gauges";

    /// <summary>Time and weather.</summary>
    public const string Clock = "clock";

    /// <summary>
    /// Heading, attitude and G — the <c>compass</c> stage layout (ADR-0039): yours if you saved a
    /// <c>compass.json</c>, the built-in otherwise, or another named by <c>layout</c>.
    /// </summary>
    public const string Compass = "compass";

    /// <summary>Android Auto and CarPlay through the dongle (ADR-0019).</summary>
    public const string Phone = "phone";

    /// <summary>A local video file — <c>path</c>, or asked for when chosen.</summary>
    public const string Video = "video";

    /// <summary>A page in WebView2 — <c>url</c>, and optionally <c>zoom</c>.</summary>
    public const string Web = "web";

    /// <summary>A native Windows program — <c>paths</c>, most likely first, and <c>arguments</c>.</summary>
    public const string App = "app";

    /// <summary>Where the apps added in Settings ▸ Apps go in the order. At the end when absent.</summary>
    public const string UserApps = "userApps";

    /// <summary>
    /// Your cards (ADR-0041): the arranged dash that used to sit below the stage. Always offered —
    /// a file that never mentions it gets one at the end of the grid, because the cards have
    /// nowhere else to be; one that hides it has chosen to.
    /// </summary>
    public const string Cards = "cards";

    public static IReadOnlyList<string> All { get; } = [Gauges, Cards, Clock, Compass, Phone, Video, Web, App, UserApps];

    /// <summary>Types drawn by DashDeck itself — the SCREENS heading.</summary>
    public static bool IsScreen(string type) => type is Gauges or Clock or Compass or Phone or Video;

    /// <summary>Whether an entry of this type keeps playing behind a silent screen when it does not say (ADR-0026).</summary>
    public static bool PlaysByDefault(string type) => type is Phone or Video;
}

/// <summary>
/// One button the launcher offers: what it is called, what it puts on the stage, and how.
/// </summary>
/// <remarks>
/// Only the fields its <see cref="Type"/> reads matter; the rest are ignored, so an entry can be
/// changed from <c>web</c> to <c>app</c> by changing the type and adding a path.
/// </remarks>
public sealed record LauncherEntry
{
    /// <summary>The caption on the button, and the stage's name while it is showing. Upper-cased.</summary>
    public string Name { get; init; } = "";

    /// <summary>One of <see cref="LauncherTypes"/>.</summary>
    public string Type { get; init; } = "";

    /// <summary>The line under the name in the picker. A sensible one is made up when absent.</summary>
    public string? Detail { get; init; }

    /// <summary>The picker heading it sits under. SCREENS, WEB or APPS by type when absent.</summary>
    public string? Group { get; init; }

    /// <summary>Kept in the file but not offered — a way to take MUSIC away without losing its URL.</summary>
    public bool Hidden { get; init; }

    /// <summary>Plays audio, and keeps playing behind a silent screen (ADR-0026). Phone and video default to true.</summary>
    public bool? KeepPlaying { get; init; }

    /// <summary><c>gauges</c>: the stage layout to show, by file name (<c>lcars</c>) or id. The theme's when absent.</summary>
    public string? Layout { get; init; }

    /// <summary><c>web</c>: the page. http or https.</summary>
    public string? Url { get; init; }

    /// <summary><c>web</c>: the page's zoom, 0.25–5. Follows Settings ▸ Display when absent.</summary>
    public double? Zoom { get; init; }

    /// <summary><c>video</c>: the file to play. Asked for when absent or missing.</summary>
    public string? Path { get; init; }

    /// <summary><c>app</c>: where the program might be, most likely first. <c>%LOCALAPPDATA%</c> and friends are expanded.</summary>
    public IReadOnlyList<string>? Paths { get; init; }

    /// <summary><c>app</c>: its command line.</summary>
    public string? Arguments { get; init; }

    /// <summary>Whether it plays, with the type's default applied.</summary>
    [JsonIgnore]
    public bool PlaysAudio => KeepPlaying ?? LauncherTypes.PlaysByDefault(Type);

    /// <summary>The picker heading, with the type's default applied.</summary>
    [JsonIgnore]
    public string GroupLabel => !string.IsNullOrWhiteSpace(Group)
        ? Group.Trim().ToUpperInvariant()
        : Type switch
        {
            LauncherTypes.Web => "WEB",
            LauncherTypes.App or LauncherTypes.UserApps => "APPS",
            _ => "SCREENS",
        };
}

/// <summary>
/// Everything the stage launcher offers, in order, and which of it gets a button below the stage
/// (ADR-0038).
/// </summary>
/// <remarks>
/// <b>The file is the whole list.</b> A <c>launcher.json</c> replaces the built-in one outright
/// rather than being merged into it, because order is the point: an entry left out is not offered,
/// and the order written is the order shown. The built-in list is the same file, compiled in and
/// written out beside yours as <c>launcher.example.json</c> to copy from.
/// <para>
/// Like a stage layout, an entry that cannot work is left out and named in <see cref="Problems"/>;
/// the rest still load. A file that cannot be read at all, or keeps nothing, leaves the built-in
/// list in place — never an empty launcher.
/// </para>
/// </remarks>
public sealed record StageLauncher
{
    /// <summary>How many buttons fit in the bar beside the grid button.</summary>
    public const int QuickBarSlots = 5;

    /// <summary>What this launcher is for, in a sentence.</summary>
    public string? Description { get; init; }

    /// <summary>What the stage shows when DashDeck starts. GAUGES when absent.</summary>
    public string? StartOn { get; init; }

    /// <summary>
    /// The names that get a button below the stage, in order — at most <see cref="QuickBarSlots"/>.
    /// The first five available entries when absent. Whatever is on the stage always has a button.
    /// </summary>
    public IReadOnlyList<string>? QuickBar { get; init; }

    /// <summary>Everything offered, in the order the picker lists it.</summary>
    public IReadOnlyList<LauncherEntry> Entries { get; init; } = [];

    [JsonIgnore]
    public LauncherOrigin Origin { get; init; } = LauncherOrigin.BuiltIn;

    [JsonIgnore]
    public string? FilePath { get; init; }

    /// <summary>Entries left out, and anything else worth fixing.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>The entries that are offered: everything not hidden.</summary>
    [JsonIgnore]
    public IEnumerable<LauncherEntry> Offered => Entries.Where(e => !e.Hidden);

    // ── Reading and writing ───────────────────────────────────────────────────

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Read a launcher. An entry that cannot work is left out and named in <see cref="Problems"/>;
    /// a quick-bar or start name that matches nothing is dropped and named.
    /// </summary>
    /// <param name="json">The file's text. Comments and trailing commas are allowed.</param>
    /// <param name="filePath">Where it came from, for messages.</param>
    /// <param name="origin">Built in or yours.</param>
    /// <param name="extraNames">
    /// Names that will be offered from outside the file — the apps added in Settings ▸ Apps — so the
    /// quick bar and <see cref="StartOn"/> may name them.
    /// </param>
    /// <exception cref="InvalidDataException">Not JSON, or nothing in it can be offered.</exception>
    public static StageLauncher Parse(
        string json,
        string? filePath = null,
        LauncherOrigin origin = LauncherOrigin.Yours,
        IEnumerable<string>? extraNames = null)
    {
        StageLauncher? file;
        try
        {
            file = JsonSerializer.Deserialize<StageLauncher>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"not a readable launcher file: {ex.Message}", ex);
        }

        if (file is null)
        {
            throw new InvalidDataException("the file is empty");
        }

        var problems = new List<string>();
        var kept = new List<LauncherEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sawUserApps = false;

        for (var i = 0; i < file.Entries.Count; i++)
        {
            var entry = file.Entries[i];
            var type = (entry.Type ?? "").Trim();
            var known = LauncherTypes.All.FirstOrDefault(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase));
            var name = (entry.Name ?? "").Trim().ToUpperInvariant();
            var label = name.Length > 0 ? name : $"entry {i + 1}";

            if (known is null)
            {
                problems.Add($"{label}: type '{type}' is not one of {string.Join(", ", LauncherTypes.All)} — left out");
                continue;
            }

            entry = entry with { Type = known, Name = name };

            if (known == LauncherTypes.UserApps)
            {
                if (sawUserApps)
                {
                    problems.Add("userApps appears twice — the second is left out");
                    continue;
                }

                sawUserApps = true;
                kept.Add(entry with { Name = "" });
                continue;
            }

            if (name.Length == 0)
            {
                problems.Add($"entry {i + 1} ({known}) has no name — left out");
                continue;
            }

            if (!names.Add(name))
            {
                problems.Add($"{name} appears twice — the second is left out");
                continue;
            }

            if (Refusal(entry) is { } refusal)
            {
                problems.Add($"{name}: {refusal} — left out");
                names.Remove(name);
                continue;
            }

            if (entry.Zoom is { } zoom && !(zoom >= 0.25 && zoom <= 5))
            {
                problems.Add($"{name}: zoom {zoom} is outside 0.25–5 — following Settings ▸ Display instead");
                entry = entry with { Zoom = null };
            }

            kept.Add(entry);
        }

        if (!kept.Any(e => e.Type != LauncherTypes.UserApps && !e.Hidden))
        {
            throw new InvalidDataException(problems.Count > 0
                ? $"nothing in it can be offered ({string.Join("; ", problems)})"
                : "it offers nothing — every entry is hidden, or there are none");
        }

        // Names a person can point at: what the file offers, plus apps from Settings ▸ Apps.
        var offered = new HashSet<string>(
            kept.Where(e => !e.Hidden && e.Name.Length > 0).Select(e => e.Name),
            StringComparer.OrdinalIgnoreCase);
        foreach (var extra in extraNames ?? [])
        {
            offered.Add(extra.Trim());
        }

        IReadOnlyList<string>? quickBar = null;
        if (file.QuickBar is { } wanted)
        {
            var bar = new List<string>();
            foreach (var raw in wanted)
            {
                var name = (raw ?? "").Trim().ToUpperInvariant();
                if (!offered.Contains(name))
                {
                    problems.Add($"quickBar: '{raw}' is not an offered entry — left out");
                }
                else if (bar.Contains(name))
                {
                    problems.Add($"quickBar: {name} appears twice — once is enough");
                }
                else if (bar.Count == QuickBarSlots)
                {
                    problems.Add($"quickBar: only {QuickBarSlots} fit beside the grid button — {name} is left out");
                }
                else
                {
                    bar.Add(name);
                }
            }

            quickBar = bar;
        }

        var startOn = file.StartOn?.Trim().ToUpperInvariant();
        if (startOn is { Length: > 0 } && !offered.Contains(startOn))
        {
            problems.Add($"startOn: '{file.StartOn}' is not an offered entry — starting on the first one");
            startOn = null;
        }

        return file with
        {
            Entries = kept,
            QuickBar = quickBar,
            StartOn = startOn is { Length: > 0 } ? startOn : null,
            Problems = problems,
            FilePath = filePath,
            Origin = origin,
        };
    }

    /// <summary>The launcher as a file, ready to edit by hand.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, WriteOptions);

    /// <summary>Why an entry cannot work at all, or null.</summary>
    private static string? Refusal(LauncherEntry e) => e.Type switch
    {
        LauncherTypes.Web when string.IsNullOrWhiteSpace(e.Url) => "a web entry needs a url",
        LauncherTypes.Web when !Uri.TryCreate(e.Url!.Trim(), UriKind.Absolute, out var uri)
                               || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) =>
            $"'{e.Url}' is not an http or https address",
        LauncherTypes.App when e.Paths is null || !e.Paths.Any(p => !string.IsNullOrWhiteSpace(p)) =>
            "an app entry needs paths — where the program might be",
        _ => null,
    };

    /// <summary>
    /// The quick-bar names, in order, from what is offered: the file's list, or the first
    /// <see cref="QuickBarSlots"/> available. The current occupant is placed by the caller.
    /// </summary>
    /// <param name="available">Every choosable name, in picker order.</param>
    public IReadOnlyList<string> QuickBarFrom(IReadOnlyList<string> available)
    {
        if (QuickBar is null)
        {
            return [.. available.Take(QuickBarSlots)];
        }

        return [.. QuickBar.Where(n => available.Contains(n, StringComparer.OrdinalIgnoreCase))];
    }

    // ── The built-in launcher ─────────────────────────────────────────────────

    /// <summary>
    /// What DashDeck offered before the launcher was a file — the same entries, in the same order.
    /// Written out as <c>launcher.example.json</c> to copy from.
    /// </summary>
    public static StageLauncher BuiltIn { get; } = Parse(BuiltInJson, null, LauncherOrigin.BuiltIn);

    /// <summary>What heads <c>launcher.example.json</c>.</summary>
    public const string ExampleHeader = """
    // The built-in stage launcher (ADR-0038), written out as a reference. DashDeck uses its own
    // compiled-in copy, so editing THIS file changes nothing and it is put back at every launch.
    //
    // To make your own: tap MAKE IT MINE in Settings ▸ Apps, or copy this file to launcher.json
    // in the same folder. Edit that, and tap RELOAD (or restart). Your file replaces this list
    // outright — order is the point, so anything you leave out is not offered. Every field is
    // explained in docs/writing-a-launcher.md.

    """;

    /// <summary>What heads a <c>launcher.json</c> made by MAKE IT MINE.</summary>
    public const string YoursHeader = """
    // Your stage launcher (ADR-0038), started from the built-in one. DashDeck reads THIS file:
    // edit it, then tap RELOAD in Settings ▸ Apps. It replaces the built-in list outright, so
    // anything you leave out is not offered. Delete the file to go back to the built-in list.
    // Every field is explained in docs/writing-a-launcher.md; launcher.example.json beside it
    // is the built-in list, kept current.

    """;

    /// <summary>The built-in launcher as text, comments and all.</summary>
    public const string BuiltInJson = """
    {
      "description": "What DashDeck offers out of the box.",

      // What the stage shows when DashDeck starts.
      "startOn": "GAUGES",

      // The buttons below the stage, left to right — at most five. Whatever is on the stage
      // always has a button, taking the last place if it is not one of these. Leave this out
      // and the first five entries get the buttons.
      "quickBar": [ "GAUGES", "CARDS", "CLOCK", "COMPASS", "PHONE" ],

      // Everything the nine-dot grid offers, in order. Headings (group) come in the order they
      // first appear.
      "entries": [
        // ── Drawn by DashDeck ──
        // The stage layout the theme names — or pin one: "layout": "lcars".
        { "name": "GAUGES", "type": "gauges", "detail": "Your stage layout — boost, oil, volts by default" },
        // Your cards: add, arrange and resize them with MODIFY WIDGETS. Always offered.
        { "name": "CARDS", "type": "cards", "detail": "Your cards" },
        { "name": "CLOCK", "type": "clock", "detail": "Time and weather" },
        { "name": "COMPASS", "type": "compass", "detail": "Heading, attitude, G" },
        { "name": "PHONE", "type": "phone", "detail": "Android Auto · CarPlay" },
        // Add "path" to always play one file; without it, it asks.
        { "name": "VIDEO", "type": "video" },

        // ── Pages in WebView2 ──
        // "zoom" fixes the page's zoom; without it, Settings ▸ Display decides.
        { "name": "MAPS", "type": "web", "url": "https://www.openstreetmap.org" },
        { "name": "SPOTIFY", "type": "web", "url": "https://open.spotify.com", "keepPlaying": true },
        { "name": "MUSIC", "type": "web", "url": "https://music.apple.com", "keepPlaying": true },

        // ── Windows programs ──
        // The first path that exists is launched; %LOCALAPPDATA% and friends are expanded.
        {
          "name": "NUVIO", "type": "app", "detail": "NuvioDesktop", "keepPlaying": true,
          "paths": [
            "%ProgramFiles%\\Nuvio\\Nuvio.exe",
            "%ProgramFiles%\\NuvioDesktop\\NuvioDesktop.exe",
            "%LOCALAPPDATA%\\Programs\\Nuvio\\Nuvio.exe",
            "%LOCALAPPDATA%\\Nuvio\\Nuvio.exe"
          ]
        },
        {
          "name": "STREMIO", "type": "app", "detail": "Stremio desktop", "keepPlaying": true,
          "paths": [
            "%LOCALAPPDATA%\\Programs\\StremioService\\stremio-shell-ng.exe",
            "%LOCALAPPDATA%\\Programs\\LNV\\Stremio-4\\stremio.exe",
            "%ProgramFiles%\\Stremio\\stremio.exe",
            "%ProgramFiles(x86)%\\Stremio\\stremio.exe"
          ]
        },
        // A plain Win32 window, to tell "our plumbing is wrong" from "that app will not be hosted".
        { "name": "PROBE", "type": "app", "detail": "Proves window adoption", "paths": [ "%WINDIR%\\System32\\charmap.exe" ] },

        // The apps you add in Settings ▸ Apps go here. Move this line to move them.
        { "type": "userApps" }
      ]
    }
    """;
}

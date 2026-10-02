using DashDeck.Abstractions;
using DashDeck.Host.Sensors;
using DashDeck.Host.Settings;
using DashDeck.Host.Stage.Launcher;
using Microsoft.Win32;

namespace DashDeck.Host.Stage;

/// <summary>
/// One thing that can be put on the stage, as offered by the picker.
/// </summary>
/// <remarks>
/// An option is not an occupant — it is the <em>offer</em> of one. Nothing is constructed
/// until it is chosen, which matters because constructing the video occupant loads VLC.
/// Unavailable options are still listed, greyed, because "Nuvio, not built yet" tells you
/// more about where this is going than an empty row does.
/// </remarks>
/// <param name="Name">Short uppercase name, matching the stage chip.</param>
/// <param name="Detail">One line under it — what it is, or why it is unavailable.</param>
/// <param name="Create">
/// Builds the occupant, or returns <see langword="null"/> for an empty stage. A
/// <see langword="null"/> factory means the option cannot be chosen at all.
/// </param>
/// <param name="Group">The picker heading, when the launcher file names one; otherwise by kind.</param>
public sealed record StageOption(
    string Name,
    string Detail,
    Func<IStageOccupant?>? Create,
    StageKind Kind = StageKind.Screen,
    bool AudioVisualSource = false,
    string? Group = null)
{
    /// <summary>False for the placeholders — listed, but not choosable.</summary>
    public bool IsAvailable => Create is not null;

    /// <summary>The picker heading this option groups under.</summary>
    public string GroupLabel => Group ?? Kind switch
    {
        StageKind.Web => "WEB",
        StageKind.App => "APPS",
        _ => "SCREENS",
    };

    /// <summary>
    /// Build the list the launcher shows, from the launcher file (ADR-0038).
    /// </summary>
    /// <remarks>
    /// Everything that used to be listed here in code — the screens, the three web pages, NUVIO,
    /// STREMIO and PROBE with their install paths — is an entry in <see cref="StageLauncher.BuiltIn"/>
    /// now, and a <c>launcher.json</c> replaces it. This only turns each entry into the occupant
    /// its type names. The order is the file's.
    /// </remarks>
    /// <param name="launcher">The launcher in use.</param>
    /// <param name="videoPath">
    /// A file from <c>--video</c>, if one was given. It wins over a VIDEO entry's own path. Without
    /// either it still works — it asks which file when chosen.
    /// </param>
    /// <param name="clock">Injected, because nothing here reads the wall clock directly.</param>
    /// <param name="signals">Named-signal access, for occupants that want vehicle data.</param>
    /// <param name="sensors">
    /// The tablet's sensors, resolved truck-first. Passed in rather than built here because it
    /// outlives any one occupant: it holds the mount reference and the vehicle declarations.
    /// </param>
    /// <param name="userApps">
    /// Native apps the user added through the settings UI (ADR-0024), placed where the file's
    /// <c>userApps</c> entry is, or at the end.
    /// </param>
    /// <param name="layouts">The stage layouts, for GAUGES and any entry that pins one.</param>
    public static IReadOnlyList<StageOption> FromLauncher(
        StageLauncher launcher,
        string? videoPath,
        IClock clock,
        IVehicleSignals signals,
        SensorService sensors,
        WeatherService weather,
        DisplaySettings display,
        IReadOnlyList<UserAppEntry>? userApps = null,
        Gauges.StageLayoutService? layouts = null)
    {
        var options = new List<StageOption>();
        var placedUserApps = false;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in launcher.Offered)
        {
            if (entry.Type == LauncherTypes.UserApps)
            {
                AddUserApps(entry.GroupLabel);
                continue;
            }

            if (taken.Add(entry.Name))
            {
                options.Add(FromEntry(entry, videoPath, clock, signals, sensors, weather, display, layouts));
            }
        }

        if (!placedUserApps)
        {
            AddUserApps(null);
        }

        return options;

        void AddUserApps(string? group)
        {
            placedUserApps = true;

            // The user's own apps, added through Settings (ADR-0024). A launcher whose executable
            // has since moved still lists — its detail line says so up front, and the occupant
            // itself says "not installed" when chosen. One that shares a name with a file entry is
            // left out: the file is the more deliberate of the two, and Settings refuses the clash.
            foreach (var app in userApps ?? [])
            {
                var name = app.Name.Trim().ToUpperInvariant();
                if (name.Length == 0 || !taken.Add(name))
                {
                    continue;
                }

                options.Add(new StageOption(
                    name,
                    app.IsInstalled ? System.IO.Path.GetFileName(app.Path) : "not found — check the path",
                    () => new AppStageOccupant(AppLaunchSpec.FromUser(app)),
                    StageKind.App,
                    AudioVisualSource: app.KeepPlaying,
                    Group: group == "APPS" ? null : group));
            }
        }
    }

    /// <summary>One file entry as an option. The entry has already been validated by <see cref="StageLauncher.Parse"/>.</summary>
    private static StageOption FromEntry(
        LauncherEntry entry,
        string? videoPath,
        IClock clock,
        IVehicleSignals signals,
        SensorService sensors,
        WeatherService weather,
        DisplaySettings display,
        Gauges.StageLayoutService? layouts)
    {
        var name = entry.Name;
        var group = string.IsNullOrWhiteSpace(entry.Group) ? null : entry.GroupLabel;
        var plays = entry.PlaysAudio;

        StageOption Screen(string detail, Func<IStageOccupant?> create) =>
            new(name, entry.Detail ?? detail, () => NamedOccupant.As(name, create()), StageKind.Screen, plays, group);

        switch (entry.Type)
        {
            case LauncherTypes.Gauges:
            case LauncherTypes.Compass:
                // A stage layout (ADR-0037). GAUGES shows the theme's through the shared service;
                // COMPASS shows the compass layout (ADR-0039) — yours if you saved a compass.json,
                // the built-in otherwise; either can pin another by name, as TOWING does.
                var service = layouts ?? new Gauges.StageLayoutService(
                    new Gauges.StageLayoutLibrary(null, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dashdeck-no-stage-layouts")),
                    () => null,
                    null);
                var pinned = entry.Layout is { Length: > 0 } named ? named
                    : entry.Type == LauncherTypes.Compass ? Gauges.StageLayout.CompassSlug
                    : null;
                return Screen(
                    entry.Type == LauncherTypes.Compass && entry.Layout is null ? "Heading, attitude, G"
                        : pinned is not null ? $"Stage layout: {pinned}" : "Your stage layout",
                    () => new GaugesStageOccupant(
                        signals,
                        clock,
                        pinned is not null ? Gauges.StageLayoutService.Pinned(service.Library, pinned) : service,
                        name,
                        sensors));

            case LauncherTypes.Clock:
                return Screen("Time and weather", () => new ClockWeatherStageOccupant(clock, weather));

            case LauncherTypes.Phone:
                // Android Auto and CarPlay through a Carlinkit dongle (ADR-0019), against a
                // synthetic one until it is bought, and saying so.
                return Screen("Android Auto · CarPlay", () => new PhoneLinkStageOccupant(clock));

            case LauncherTypes.Video:
                var file = videoPath ?? entry.Path;
                return Screen(
                    file is null ? "Pick a file" : System.IO.Path.GetFileName(file),
                    () => CreateVideo(file is null ? null : Environment.ExpandEnvironmentVariables(file)));

            case LauncherTypes.Web:
                // WebView2 pages. A player that gates on Widevine probes for it and says so rather
                // than presenting a page that looks fine and refuses to make a sound.
                var url = entry.Url!.Trim();
                return new StageOption(
                    name,
                    entry.Detail ?? new Uri(url).Host,
                    () => new WebStageOccupant(name, url, display, entry.Zoom),
                    StageKind.Web,
                    plays,
                    group);

            default:
                // A separate Windows program, owned and placed over the stage where that works
                // (ADR-0021), otherwise left in its own window. Listed even when it is not
                // installed, because "not installed" says more than a missing row.
                var spec = AppLaunchSpec.FromLauncher(entry);
                var detail = entry.Detail ?? System.IO.Path.GetFileName(spec.Candidates.FirstOrDefault() ?? "");
                return new StageOption(
                    name,
                    spec.IsInstalled ? detail : $"{detail} — not installed",
                    () => new AppStageOccupant(spec),
                    StageKind.App,
                    plays,
                    group);
        }
    }

    private static IStageOccupant? CreateVideo(string? videoPath)
    {
        var path = videoPath;

        if (path is null || !System.IO.File.Exists(path))
        {
            var dialog = new OpenFileDialog
            {
                Title = "Play on the stage",
                Filter = "Video|*.mp4;*.mkv;*.avi;*.mov;*.m4v;*.webm;*.ts|Every file|*.*",
            };

            if (dialog.ShowDialog() != true)
            {
                return null;
            }

            path = dialog.FileName;
        }

        return new VideoStageOccupant(path);
    }
}


using System.Windows;
using DashDeck.Abstractions;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host;

/// <summary>
/// Application entry point. Starts the vehicle stack, then shows the shell.
/// </summary>
/// <remarks>
/// Launched by hand, every time (constraint C5). There is no power-state machine and no
/// ignition integration — but connect, disconnect, sleep and resume all have to be
/// non-events that recover on their own.
/// </remarks>
public partial class App : Application
{
    private VehicleStack? _vehicle;
    private ShellViewModel? _shell;
    private Theme.ThemeService? _theme;
    private Stage.WeatherService? _weather;
    private Shell.SingleInstance? _instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One DashDeck at a time, before anything opens the adapter: the serial port can only be
        // held once, and a second tap on the icon during a slow start used to make a second dash
        // on no truck. Another one running is brought forward instead.
        _instance = Shell.SingleInstance.Claim();
        if (_instance is null)
        {
            Shutdown(0);
            return;
        }

        // Never fail silently. A dash that vanishes tells you nothing; one that leaves a
        // log can be diagnosed later, from the passenger seat or the kitchen table.
        DispatcherUnhandledException += (_, args) =>
        {
            Fail("Unhandled dispatcher exception", args.Exception);
            args.Handled = true;
            Shutdown(1);
        };

        // Which scripted drive to run. Defaults to the cold start, because a warm-up is
        // where quality transitions actually happen and the shell has to render them.
        var drive = PositionalArg(e.Args) ?? "cold-start-city";

        // The adapter, if one is configured in Settings -> Vehicle. A --port argument wins,
        // so a real adapter can be tried without changing stored settings. Empty means the
        // synthetic truck, which is the right default for a tablet that spends most of its
        // life away from the truck (ADR-0005).
        var stored = Settings.SettingsStore.Load();
        var adapterPort = ArgValue(e.Args, "--port") ?? stored.AdapterSerialPort;

        // How to find it (ADR-0034): the rate and identity it answered with last time go first,
        // so a launch in the truck connects on the first try, and a renumbered port is recognised.
        // The phone's Bluetooth GPS port is never opened in the search.
        DashDeck.Vehicle.AdapterLinkOptions? adapter = DashDeck.Vehicle.Diagnostics.AdapterSelection.TryResolvePort(adapterPort, out var port)
            ? new DashDeck.Vehicle.AdapterLinkOptions
            {
                PreferredPort = port,
                KnownBaudRate = stored.AdapterBaudRate > 0 ? stored.AdapterBaudRate : null,
                KnownIdentity = string.IsNullOrWhiteSpace(stored.AdapterIdentity) ? null : stored.AdapterIdentity,
                ReservedPorts = stored.GpsEnabled
                    && string.Equals(stored.GpsTransport, "Bluetooth", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(stored.GpsSerialPort)
                        ? [stored.GpsSerialPort.Trim()]
                        : [],
            }
            : null;

        // Which vehicle this is, decoded from its VIN and cached (ADR-0033). It picks the vehicle
        // signal pack and fills the component profile; unknown is fine — the standard set runs.
        var identity = new Settings.VehicleIdentityStore().Identity;

        try
        {
            // The vehicle's pack and the user's own signals (ADR-0032), read once at launch like
            // every other choice that shapes the pipeline. A bad file is reported, never fatal.
            _vehicle = await VehicleStack.StartAsync(
                adapter,
                drive,
                new CatalogSources(identity, new Settings.UserSignalStore().Definitions),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // No silent failure. A dash that comes up blank because the catalog was missing
            // is worse than one that says so.
            Fail("Could not start the vehicle stack", ex);
            Shutdown(1);
            return;
        }

        // Development affordance: --components <outfile> loads plugins/, writes a report of
        // what loaded and why, and exits. The component host draws nothing yet (that is the
        // next step), so this is how it is verified end to end before it has a face.
        if (ArgValue(e.Args, "--components") is { } reportPath)
        {
            var root = Components.PluginPath.FindRoot();
            var host = new Components.ComponentHost(_vehicle.Signals, SystemClock.Instance, root ?? "plugins", VehicleProfile.Empty);
            await host.LoadAllAsync(CancellationToken.None);

            // Let a background worker actually run, so the report shows it working rather than
            // only loading. --components-dwell <seconds> holds before reporting.
            if (ArgValue(e.Args, "--components-dwell") is { } dwell &&
                double.TryParse(dwell, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds));
            }

            var report = new System.Text.StringBuilder();
            report.AppendLine($"plugins root: {root ?? "(not found)"}");
            report.AppendLine($"components: {host.Components.Count}");

            foreach (var component in host.Components)
            {
                report.AppendLine(
                    $"  {component.Id}  state={component.State}" +
                    (component.LastError is { } error ? $"  ({error})" : string.Empty));
            }

            report.AppendLine("log:");

            foreach (var line in host.Log)
            {
                report.AppendLine($"  {line}");
            }

            System.IO.File.WriteAllText(reportPath, report.ToString());
            Shutdown(0);
            return;
        }

        // --video <path> starts with video already on the stage. Everything else is chosen
        // from the picker at runtime, so this is a convenience rather than the only way in.
        string? stagedVideo = null;

        if (ArgValue(e.Args, "--video") is { } videoPath)
        {
            if (System.IO.File.Exists(videoPath))
            {
                stagedVideo = System.IO.Path.GetFullPath(videoPath);
            }
            else
            {
                // Say so rather than starting with an inexplicably empty stage.
                Fail("Video file not found", new System.IO.FileNotFoundException(videoPath));
            }
        }

        // Built before the shell: it writes the palette into Application.Resources, so the
        // window comes up already wearing the right one rather than repainting into it.
        // One weather fetch for the whole application. Built before the theme, because Auto
        // day/night reads sunrise and sunset from it rather than fetching its own.
        _weather = new Stage.WeatherService(SystemClock.Instance);
        _theme = new Theme.ThemeService(SystemClock.Instance, _weather);

        // --theme <DAY|NIGHT|AUTO> forces a palette, for looking at one without waiting for
        // sunset. --accent <NAME|#RRGGBB> forces an accent, preset or custom.
        //
        // Both go through Preview so they are not remembered. They are for looking, and a
        // look should not become the setting.
        Theme.ThemeMode? previewMode =
            ArgValue(e.Args, "--theme") is { } themeName &&
            Enum.TryParse<Theme.ThemeMode>(themeName, ignoreCase: true, out var mode)
                ? mode
                : null;

        Theme.AccentOption? previewAccent = null;

        if (ArgValue(e.Args, "--accent") is { } accentName)
        {
            previewAccent = Theme.AccentOption.All.FirstOrDefault(a =>
                string.Equals(a.Name, accentName, StringComparison.OrdinalIgnoreCase));

            // Not a preset name, so try it as a colour — the same path the settings box uses,
            // validation included, so the flag cannot set something the UI would refuse.
            if (previewAccent is null &&
                Theme.AccentValidation.TryParse(accentName, out var custom) &&
                Theme.AccentValidation.Check(custom).IsUsable)
            {
                previewAccent = new Theme.AccentOption("CUSTOM", custom);
            }
        }

        // --theme-name <id|name> wears a theme without remembering it (ADR-0036), for a screenshot:
        // "--theme-name lcars" or "--theme-name shipped/lcars-inspired".
        Theme.ThemeDefinition? previewTheme = null;

        if (ArgValue(e.Args, "--theme-name") is { } wanted)
        {
            previewTheme = _theme.Library.Find(wanted)
                ?? _theme.Library.Themes.FirstOrDefault(t =>
                    t.Name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase) ||
                    t.Id.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        }

        if (previewMode is not null || previewAccent is not null || previewTheme is not null)
        {
            _theme.Preview(previewMode, previewAccent, previewTheme);
        }

        // The component host, loaded once at startup. A component's widget can now sit on the
        // dash (ADR-0023); a headless one runs from here too. Loading never throws for bad
        // component content, so a broken plugin cannot stop the shell coming up.
        // The truck's own facts, read from settings at launch and lent to every component
        // (ADR-0029). A snapshot: a tank does not change size while you drive, so a change in
        // Settings applies on the next start, like the GPS transport.
        // What the vehicle is joined it in apiVersion 1.2 (ADR-0033) — everything decoded from the
        // VIN except the VIN itself, which no component needs.
        var vehicle = identity.ToProfile(Settings.SettingsStore.Load().FuelTankLitres);

        var componentHost = new Components.ComponentHost(
            _vehicle.Signals, SystemClock.Instance, Components.PluginPath.FindRoot() ?? "plugins", vehicle);
        await componentHost.LoadAllAsync(CancellationToken.None);

        // --stage <NAME> opens on a named occupant: CLOCK, VIDEO, NUVIO, MAPS or STREMIO.
        // --gps <host:port|synthetic> forces a phone-GPS source (ADR-0027) for a screenshot or a
        // quick test, over whatever the persisted setting says.
        _shell = new ShellViewModel(
            _vehicle,
            SystemClock.Instance,
            _theme,
            _weather,
            stagedVideo,
            ArgValue(e.Args, "--stage"),
            componentHost,
            ArgValue(e.Args, "--gps"));

        // --nav <DEST> opens on a destination below the stage, so Settings can be reviewed
        // without a finger.
        if (ArgValue(e.Args, "--nav") is { } destination)
        {
            _shell.ActiveDestination = destination.ToUpperInvariant();
        }

        // Development affordance: --settings-section <NAME> opens Settings on a named section
        // (APPEARANCE, MOUNT, DISPLAY, APPS, DIAGNOSTICS), which otherwise needs a tap on the rail.
        if (ArgValue(e.Args, "--settings-section") is { } sectionName
            && _shell.Settings.Sections.FirstOrDefault(
                s => string.Equals(s.Name, sectionName, StringComparison.OrdinalIgnoreCase)) is { } section)
        {
            _shell.Settings.SelectSectionCommand.Execute(section);
        }

        // Development affordance: --picker opens the stage picker at launch, so a state
        // that normally needs a finger can be reviewed like any other.
        if (e.Args.Contains("--picker"))
        {
            _shell.IsStagePickerOpen = true;
        }

        // The overflow menu, which is otherwise a tap on the status strip.
        if (e.Args.Contains("--menu"))
        {
            _shell.ToggleMenuCommand.Execute(null);
        }

        // Same again for edit mode, which is otherwise a 600 ms hold on a card.
        if (e.Args.Contains("--edit"))
        {
            _shell.Dashboard.ToggleEditCommand.Execute(null);
        }

        // --level captures the mount reference at startup, so the compass can be reviewed in
        // a screenshot without a finger. Real levelling is a button on the stage, done parked.
        if (e.Args.Contains("--level"))
        {
            _shell.Sensors.Level();
        }

        // --page <n> opens on a later page of cards, so paging can be reviewed in a
        // screenshot rather than only by swiping.
        if (ArgValue(e.Args, "--page") is { } page && int.TryParse(page, out var pageIndex))
        {
            _shell.Dashboard.GoToPageCommand.Execute(pageIndex);
        }

        // --edit-card <n> opens the card editor on the nth card. The editor is otherwise
        // three deliberate gestures deep, which is three too many to reach in a screenshot.
        if (ArgValue(e.Args, "--edit-card") is { } card && int.TryParse(card, out var cardIndex))
        {
            _shell.Dashboard.OpenCardAt(cardIndex);
        }

        // --detail <n> opens the nth card's component detail, which is otherwise a tap.
        if (ArgValue(e.Args, "--detail") is { } detail && int.TryParse(detail, out var detailIndex))
        {
            _shell.Dashboard.OpenComponentDetailAt(detailIndex);
        }

        var window = new MainWindow { DataContext = _shell };
        MainWindow = window;
        window.Show();

        // --tap-detail exercises the real touch-tap routing (hit-test -> button command), which a
        // screenshot cannot otherwise reach because it drives the app with a mouse. Runs once the
        // dash has laid out, so the card it taps actually has bounds.
        if (e.Args.Contains("--tap-detail"))
        {
            _ = window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                FindVisualChild<Views.DashboardView>(window)?.TapFirstDetailCard());
        }

        // Development affordance: --unplug <seconds> pulls the adapter mid-run, so the
        // degraded state can be watched happening rather than only reasoned about.
        if (ArgValue(e.Args, "--unplug") is { } unplugAt)
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(
                    double.Parse(unplugAt, System.Globalization.CultureInfo.InvariantCulture)),
            };

            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _vehicle?.Unplug();
            };

            timer.Start();
        }

        // Development affordance: --shot <path> renders the layout to a PNG once the drive
        // has produced some data, then exits. Lets the shell be reviewed without a screen
        // grab, which on a 200%-scaled tablet is more trouble than it sounds.
        if (ArgValue(e.Args, "--shot") is { } shotPath)
        {
            var after = ArgValue(e.Args, "--shot-after") is { } s
                ? double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)
                : 20;

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(after),
            };

            timer.Tick += (_, _) =>
            {
                timer.Stop();
                window.SaveDesignSurface(shotPath);

                // The video surface lives in a child window and never appears in the
                // render, so record what the player says it is doing beside the image.
                if (_shell?.DescribeStage() is { } state)
                {
                    var climate = _shell.DescribeClimate() is { } c ? $"{Environment.NewLine}climate: {c}" : "";
                    var console = _shell.DescribeConsole() is { } d ? $"{Environment.NewLine}console: {d}" : "";
                    System.IO.File.WriteAllText(shotPath + ".txt", state + climate + console);
                }

                Shutdown();
            };

            timer.Start();
        }
    }

    /// <summary>First descendant of a given type in the visual tree. For the dev tap flag.</summary>
    private static T? FindVisualChild<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T match)
        {
            return match;
        }

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);

        for (var i = 0; i < count; i++)
        {
            if (FindVisualChild<T>(System.Windows.Media.VisualTreeHelper.GetChild(node, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// The one positional argument: the drive name.
    /// </summary>
    /// <remarks>
    /// It has to skip the value that follows a flag. Taking the first argument without a
    /// leading dash looks equivalent and is not — <c>--nav SETTINGS</c> makes SETTINGS the
    /// first such argument, and the shell died on startup with "Unknown drive 'SETTINGS'".
    /// So the scan has to know which flags take a value, which means <see cref="Switches"/>
    /// has to be kept honest as flags are added.
    /// </remarks>
    private static string? PositionalArg(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                // Skip its value too, unless this flag is a switch and has none.
                if (!Switches.Contains(args[i]))
                {
                    i++;
                }

                continue;
            }

            return args[i];
        }

        return null;
    }

    /// <summary>The flags that take no value. Everything else consumes the argument after it.</summary>
    private static readonly HashSet<string> Switches = ["--picker", "--edit", "--level", "--menu"];

    /// <summary>
    /// Record a fatal error where it can be read later.
    /// </summary>
    /// <remarks>
    /// Settings and logs live in <c>%LOCALAPPDATA%</c>, never the registry — the Surface is
    /// a personal device and uninstalling DashDeck is deleting a folder (constraint C1).
    /// </remarks>
    private static void Fail(string what, Exception ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DashDeck");

            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, "crash.log"),
                $"{DateTimeOffset.Now:O}  {what}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never be the thing that takes the dash down.
        }
    }

    /// <summary>Set by <see cref="RequestRestart"/>; acted on in <see cref="OnExit"/>.</summary>
    private static bool _restartRequested;

    /// <summary>
    /// Close and start again, with the same arguments — how a change that applies at the next
    /// launch, like a new signal definition (ADR-0032), is applied without leaving the truck.
    /// </summary>
    /// <remarks>
    /// The new process is started from <see cref="OnExit"/>, <em>after</em> the vehicle stack has
    /// been disposed, not here. On the truck the adapter is a serial port only one process can
    /// hold, and a successor started first would find it still open and come up with no truck.
    /// </remarks>
    public static void RequestRestart()
    {
        _restartRequested = true;
        Current.Shutdown(0);
    }

    /// <summary>
    /// Close DashDeck — the menu's CLOSE DASHDECK, for a touch screen with no Escape key.
    /// </summary>
    public static void RequestClose() => Current.Shutdown(0);

    /// <summary>
    /// How long the vehicle pipeline gets to close the adapter cleanly before shutdown moves on.
    /// </summary>
    private static readonly TimeSpan VehicleCloseBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long after shutdown starts the process is ended regardless. A dash that has closed its
    /// window must not linger in Task Manager holding the serial port, whatever is stuck.
    /// </summary>
    private static readonly TimeSpan ExitBackstop = TimeSpan.FromSeconds(10);

    protected override void OnExit(ExitEventArgs e)
    {
        // The backstop first, so nothing below can keep the process alive past it: a serial port
        // that will not close after a yanked cable, a media player that will not stop. Background,
        // so a clean exit simply takes it down with the process.
        var exitCode = e.ApplicationExitCode;
        new System.Threading.Thread(() =>
        {
            System.Threading.Thread.Sleep(ExitBackstop);
            Fail("Shutdown overran; ending the process", new TimeoutException($"still running {ExitBackstop.TotalSeconds:0} s after exit began"));
            Environment.Exit(exitCode);
        })
        {
            IsBackground = true,
            Name = "DashDeck exit backstop",
        }.Start();

        // The stage occupants go with the shell: hosted apps are killed with their job, players
        // and browsers are disposed.
        _shell?.Dispose();
        _weather?.Dispose();

        if (_vehicle is not null)
        {
            // Off the UI thread, with a time limit. Waiting for it *on* the UI thread deadlocked:
            // any continuation that wanted the dispatcher waited for a dispatcher that was waiting
            // for it, so the window closed and the process stayed in Task Manager.
            var vehicle = _vehicle;

            try
            {
                var closed = Task.Run(async () => await vehicle.DisposeAsync().ConfigureAwait(false))
                    .Wait(VehicleCloseBudget);

                if (!closed)
                {
                    Fail("Shutdown", new TimeoutException($"the vehicle pipeline did not close within {VehicleCloseBudget.TotalSeconds:0} s"));
                }
            }
            catch (AggregateException ex)
            {
                // Closing is best effort; a failure to close is logged, never a reason to stay open.
                Fail("Shutdown", ex.InnerException ?? ex);
            }
        }

        // The port is closed (or abandoned to the backstop): let a successor in. Released before the
        // restart below starts one, so RESTART NOW never waits on itself.
        _instance?.Release();

        if (_restartRequested && Environment.ProcessPath is { } exe)
        {
            var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };

            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
            {
                start.ArgumentList.Add(arg);
            }

            try
            {
                System.Diagnostics.Process.Start(start);
            }
            catch (Exception ex)
            {
                Fail("Could not restart", ex);
            }
        }

        base.OnExit(e);
    }
}

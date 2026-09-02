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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        var drive = e.Args.FirstOrDefault(a => !a.StartsWith('-')) ?? "cold-start-city";

        try
        {
            _vehicle = await VehicleStack.StartSyntheticAsync(drive, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // No silent failure. A dash that comes up blank because the catalog was missing
            // is worse than one that says so.
            Fail("Could not start the vehicle stack", ex);
            Shutdown(1);
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
        _theme = new Theme.ThemeService(SystemClock.Instance);

        // --theme <DAY|NIGHT|AUTO> forces a palette, for looking at one without waiting for
        // sunset.
        if (ArgValue(e.Args, "--theme") is { } themeName &&
            Enum.TryParse<Theme.ThemeMode>(themeName, ignoreCase: true, out var mode))
        {
            _theme.Mode = mode;
        }

        // --accent <NAME> forces an accent, for looking at one without tapping through.
        if (ArgValue(e.Args, "--accent") is { } accentName &&
            Theme.AccentOption.All.FirstOrDefault(a =>
                string.Equals(a.Name, accentName, StringComparison.OrdinalIgnoreCase)) is { } chosen)
        {
            _theme.Accent = chosen;
        }

        // --stage <NAME> opens on a named occupant: CLOCK, VIDEO, NUVIO, MAPS or STREMIO.
        _shell = new ShellViewModel(
            _vehicle,
            SystemClock.Instance,
            _theme,
            stagedVideo,
            ArgValue(e.Args, "--stage"));

        // --nav <DEST> opens on a destination below the stage, so Settings can be reviewed
        // without a finger.
        if (ArgValue(e.Args, "--nav") is { } destination)
        {
            _shell.ActiveDestination = destination.ToUpperInvariant();
        }

        // Development affordance: --picker opens the stage picker at launch, so a state
        // that normally needs a finger can be reviewed like any other.
        if (e.Args.Contains("--picker"))
        {
            _shell.IsStagePickerOpen = true;
        }

        var window = new MainWindow { DataContext = _shell };
        MainWindow = window;
        window.Show();

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
                    System.IO.File.WriteAllText(shotPath + ".txt", state);
                }

                Shutdown();
            };

            timer.Start();
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

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

    protected override void OnExit(ExitEventArgs e)
    {
        _shell?.Dispose();

        if (_vehicle is not null)
        {
            // Blocking on shutdown is acceptable; blocking anywhere else is not.
            _vehicle.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnExit(e);
    }
}

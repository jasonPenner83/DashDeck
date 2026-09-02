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

        _shell = new ShellViewModel(_vehicle, SystemClock.Instance);

        var window = new MainWindow { DataContext = _shell };
        MainWindow = window;
        window.Show();

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

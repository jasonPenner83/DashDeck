using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Core.Tools;
using DashDeck.Host.Settings;
using DashDeck.Host.Shell;

namespace DashDeck.Host.ViewModels;

/// <summary>One fact about this install, for the list.</summary>
/// <param name="Label">What it is.</param>
/// <param name="Value">What it says.</param>
public readonly record struct AboutFact(string Label, string Value);

/// <summary>One folder worth opening, with a button beside it.</summary>
public sealed partial class FolderRow(string label, string path) : ObservableObject
{
    public string Label { get; } = label;

    public string Path { get; } = path;

    /// <summary>Whether it is actually there, so a missing folder reads as missing.</summary>
    public string State => Directory.Exists(Path) ? "" : "not created yet";
}

/// <summary>One of the tools that ship beside the dash, with a button that opens it (ADR-0079).</summary>
public sealed partial class ToolRow(DashTool tool, string path) : ObservableObject
{
    public string Name => tool.Name;

    public string What => tool.What;

    public string Path { get; } = path;

    /// <summary>Whether this install has it: a build run from source has no tools beside it.</summary>
    public bool IsThere => File.Exists(Path);

    public string State => IsThere ? "" : "not in this install";

    /// <summary>True while a second tap would close the dash and open the tool.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    private bool _isArmed;

    public string Caption => IsArmed ? "TAP AGAIN: DASHDECK CLOSES" : "OPEN";

    /// <summary>The two taps, as the menu's CLOSE has them.</summary>
    internal CloseConfirm Confirm { get; } = new(TimeSpan.FromSeconds(4));
}

/// <summary>
/// Settings ▸ ABOUT: what this build is, and where it keeps things.
/// </summary>
/// <remarks>
/// <b>Why this is worth a section.</b> Every answer here was something that had to be dug out of a
/// terminal or a source file at some point during bring-up: which build is on the tablet, which
/// vehicle pack it matched, where the layouts and captures live, what the adapter identified
/// itself as. In the truck there is no keyboard and no terminal, so a screen that answers them —
/// with a button that opens the folder — is the difference between fixing something in the cab and
/// writing it down to fix later.
/// <para>
/// It is all read-only. Nothing here changes anything, which is also why it is safe to reach for
/// while the engine is running. The one thing that acts is a tool's OPEN, which closes the dash so
/// the tool can have the adapter (ADR-0079).
/// </para>
/// </remarks>
public sealed partial class AboutViewModel : ObservableObject
{
    private readonly DisplaySettings _settings;
    private readonly IClock _clock;
    private readonly string _toolsFolder;
    private readonly Func<string, string?> _openTool;
    private readonly Func<Task<string>> _keyboard;
    private int _refreshes;

    /// <param name="settings">What the facts read.</param>
    /// <param name="clock">For the tools' two taps.</param>
    /// <param name="toolsFolder">Where the tools are; the dash's own folder.</param>
    /// <param name="openTool">Opens a tool and closes the dash, or says why it could not.</param>
    /// <param name="keyboard">What the touch keyboard rule says now.</param>
    public AboutViewModel(
        DisplaySettings settings,
        IClock? clock = null,
        string? toolsFolder = null,
        Func<string, string?>? openTool = null,
        Func<Task<string>>? keyboard = null)
    {
        _settings = settings;
        _clock = clock ?? SystemClock.Instance;
        _toolsFolder = toolsFolder ?? AppContext.BaseDirectory;
        _openTool = openTool ?? OpenAndClose;
        _keyboard = keyboard ?? (async () => (await TouchKeyboard.DecideAsync().ConfigureAwait(true)).Why);
        Refresh();
    }

    /// <summary>
    /// The manual update (ADR-0061). It sits here because this is the section that already answers
    /// "which build is this", and that is the question an update is the answer to.
    /// </summary>
    public UpdateViewModel Update { get; } = new();

    /// <summary>What this build is.</summary>
    public ObservableCollection<AboutFact> Facts { get; } = [];

    /// <summary>The folders DashDeck reads and writes, each with a button.</summary>
    public ObservableCollection<FolderRow> Folders { get; } = [];

    /// <summary>The tools beside the dash, each with a button.</summary>
    public ObservableCollection<ToolRow> Tools { get; } = [];

    /// <summary>Set when a folder could not be opened, so a dead button says why.</summary>
    [ObservableProperty]
    private string _problem = "";

    /// <summary>Re-read everything.</summary>
    [RelayCommand]
    private void Refresh()
    {
        Facts.Clear();
        Folders.Clear();
        Tools.Clear();
        var refresh = ++_refreshes;

        var assembly = Assembly.GetExecutingAssembly();

        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        Facts.Add(new AboutFact("Version", version));
        Facts.Add(new AboutFact("Built", BuildTime(assembly)));
        Facts.Add(new AboutFact("Runtime", Environment.Version.ToString()));
        Facts.Add(new AboutFact("Windows", Environment.OSVersion.VersionString));
        Facts.Add(new AboutFact("Machine", Environment.MachineName));
        Facts.Add(new AboutFact("Screen", $"{BandGrid.DesignWidth:0} x {BandGrid.DesignHeight:0} design surface"));
        Facts.Add(new AboutFact(KeyboardFact, "checking…"));
        TellKeyboard(refresh);

        // The things that were actually hunted for during bring-up.
        Facts.Add(new AboutFact(
            "Adapter",
            string.IsNullOrWhiteSpace(_settings.AdapterIdentity) ? "not identified yet" : _settings.AdapterIdentity));

        Facts.Add(new AboutFact(
            "Adapter port",
            string.IsNullOrWhiteSpace(_settings.AdapterSerialPort)
                ? "none chosen"
                : $"{_settings.AdapterSerialPort} at {_settings.AdapterBaudRate} baud"));

        Facts.Add(new AboutFact("Fuel tank", $"{_settings.FuelTankLitres:0.#} L"));

        Facts.Add(new AboutFact(
            "Writes to the vehicle",
            _settings.AllowClearCodes ? "clearing codes is ALLOWED" : "never (read-only)"));

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DashDeck");

        Folders.Add(new FolderRow("Settings and data", root));
        Folders.Add(new FolderRow("Stage layouts", Path.Combine(root, "stage")));
        Folders.Add(new FolderRow("Console layouts", Path.Combine(root, "console")));
        Folders.Add(new FolderRow("Climate layouts", Path.Combine(root, "climate")));
        Folders.Add(new FolderRow("Themes", Path.Combine(root, "themes")));
        Folders.Add(new FolderRow("CAN databases", Path.Combine(root, "dbc")));
        Folders.Add(new FolderRow("Captures", Path.Combine(root, "captures")));

        foreach (var tool in DashTools.All)
        {
            Tools.Add(new ToolRow(tool, Path.Combine(_toolsFolder, Path.Combine(tool.RelativePath.Split('\\')))));
        }
    }

    private const string KeyboardFact = "Touch keyboard";

    /// <summary>Fill in the keyboard's line once Windows has listed its keyboards.</summary>
    private async void TellKeyboard(int refresh)
    {
        string why;

        try
        {
            why = await _keyboard().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            why = $"could not tell: {ex.Message}";
        }

        // A refresh in between has its own line coming.
        if (refresh != _refreshes)
        {
            return;
        }

        for (var i = 0; i < Facts.Count; i++)
        {
            if (Facts[i].Label == KeyboardFact)
            {
                Facts[i] = new AboutFact(KeyboardFact, why);
            }
        }
    }

    /// <summary>
    /// Open a tool: the first tap arms it, the second closes the dash and the tool opens once it has
    /// (ADR-0079).
    /// </summary>
    /// <remarks>
    /// Every tool wants the adapter, which serves one program at a time, so the dash has to go. Two
    /// taps because that is what ending the dash takes everywhere else.
    /// </remarks>
    [RelayCommand]
    private void OpenTool(ToolRow? row)
    {
        if (row is null)
        {
            return;
        }

        Problem = "";

        if (!row.IsThere)
        {
            Problem = $"{row.Name} is not in this install: {row.Path}";
            return;
        }

        foreach (var other in Tools.Where(t => !ReferenceEquals(t, row)))
        {
            other.Confirm.Disarm();
            other.IsArmed = false;
        }

        var open = row.Confirm.Tap(_clock.UtcNow);
        row.IsArmed = !open;

        if (open && _openTool(row.Path) is { } problem)
        {
            Problem = problem;
        }
    }

    /// <summary>Hand the tool to a batch file that waits for the dash to exit, then close the dash.</summary>
    private static string? OpenAndClose(string path)
    {
        try
        {
            var script = Path.Combine(Path.GetTempPath(), $"dashdeck-tool-{Environment.ProcessId}.cmd");
            File.WriteAllText(script, DashTools.LaunchScript(Environment.ProcessId, path));

            Process.Start(new ProcessStartInfo(script)
            {
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex)
        {
            return $"Could not open {path}: {ex.Message}";
        }

        App.RequestClose();
        return null;
    }

    /// <summary>Open a folder in Explorer, creating it if it is not there yet.</summary>
    /// <remarks>
    /// Creating it rather than refusing: an empty folder is where a layout or a capture is meant to
    /// go, and the usual reason to open one is to put something in it.
    /// </remarks>
    [RelayCommand]
    private void OpenFolder(FolderRow? row)
    {
        if (row is null)
        {
            return;
        }

        Problem = "";

        try
        {
            Directory.CreateDirectory(row.Path);
            Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Problem = $"Could not open {row.Path}: {ex.Message}";
        }
    }

    /// <summary>
    /// When this build was compiled, from the PE header's timestamp.
    /// </summary>
    /// <remarks>
    /// Deterministic builds replace that timestamp with a hash, which reads as a date in 1900-odd
    /// — so a date that is obviously wrong is reported as unknown rather than shown. Better to say
    /// nothing than to put a confident fiction on a diagnostics screen.
    /// </remarks>
    private static string BuildTime(Assembly assembly)
    {
        try
        {
            var written = File.GetLastWriteTime(assembly.Location);
            return written.Year < 2000
                ? "unknown (deterministic build)"
                : written.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "unknown";
        }
    }
}

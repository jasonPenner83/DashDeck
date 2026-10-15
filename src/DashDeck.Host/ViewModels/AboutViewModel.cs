using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Host.Settings;

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
/// while the engine is running.
/// </para>
/// </remarks>
public sealed partial class AboutViewModel : ObservableObject
{
    private readonly DisplaySettings _settings;

    public AboutViewModel(DisplaySettings settings)
    {
        _settings = settings;
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

    /// <summary>Set when a folder could not be opened, so a dead button says why.</summary>
    [ObservableProperty]
    private string _problem = "";

    /// <summary>Re-read everything.</summary>
    [RelayCommand]
    private void Refresh()
    {
        Facts.Clear();
        Folders.Clear();

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

        if (!string.IsNullOrWhiteSpace(_settings.MusicFolder))
        {
            Folders.Add(new FolderRow("Music", _settings.MusicFolder));
        }
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

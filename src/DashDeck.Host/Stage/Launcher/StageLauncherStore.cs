using System.IO;

namespace DashDeck.Host.Stage.Launcher;

/// <summary>
/// The launcher in use (ADR-0038): <c>launcher.json</c> if there is one that works, the built-in
/// list otherwise.
/// </summary>
/// <remarks>
/// Same promise as the theme and stage-layout libraries: a bad file costs a warning, never the
/// launcher. RELOAD reads the file again and raises <see cref="Changed"/>, and the shell rebuilds
/// its buttons without touching what is on the stage.
/// </remarks>
public sealed class StageLauncherStore
{
    private readonly Func<IEnumerable<string>> _extraNames;

    /// <param name="userPath">Where <c>launcher.json</c> is — <c>%LOCALAPPDATA%\DashDeck\launcher.json</c> in the app.</param>
    /// <param name="extraNames">The names of the apps added in Settings ▸ Apps, which the quick bar may name.</param>
    public StageLauncherStore(string userPath, Func<IEnumerable<string>>? extraNames = null)
    {
        UserPath = userPath;
        _extraNames = extraNames ?? (() => []);
        Load();
    }

    /// <summary>Where your launcher file goes.</summary>
    public string UserPath { get; }

    /// <summary>The built-in list, written out beside yours to copy from.</summary>
    public string ExamplePath => Path.Combine(Path.GetDirectoryName(UserPath) ?? "", "launcher.example.json");

    /// <summary>The launcher in use.</summary>
    public StageLauncher Current { get; private set; } = StageLauncher.BuiltIn;

    /// <summary>Why your file is not the one in use, when it is not. Null when it is, or there is none.</summary>
    public string? FileProblem { get; private set; }

    /// <summary>Everything worth fixing: the file-level problem, then the entries left out.</summary>
    public IReadOnlyList<string> Problems =>
    [
        .. FileProblem is null ? [] : new[] { FileProblem },
        .. Current.Problems,
    ];

    /// <summary>Raised after <see cref="Reload"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>Read the file again — after a hand edit, or after an app was added in Settings.</summary>
    public void Reload()
    {
        Load();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Write the built-in list beside yours as <c>launcher.example.json</c>, if it is not already
    /// there as written. Returns why not, or null.
    /// </summary>
    public string? WriteExample()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ExamplePath)!);
            var content = StageLauncher.ExampleHeader + StageLauncher.BuiltInJson + Environment.NewLine;
            if (!File.Exists(ExamplePath) || File.ReadAllText(ExamplePath) != content)
            {
                File.WriteAllText(ExamplePath, content);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private void Load()
    {
        FileProblem = null;
        var extras = _extraNames().ToArray();

        if (!File.Exists(UserPath))
        {
            Current = extras.Length == 0
                ? StageLauncher.BuiltIn
                : StageLauncher.Parse(StageLauncher.BuiltInJson, null, LauncherOrigin.BuiltIn, extras);
            return;
        }

        try
        {
            Current = StageLauncher.Parse(File.ReadAllText(UserPath), UserPath, LauncherOrigin.Yours, extras);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Current = StageLauncher.BuiltIn;
            FileProblem = $"{Path.GetFileName(UserPath)} was not used — {ex.Message}. Showing the built-in launcher.";
        }
    }
}

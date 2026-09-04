using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using DashDeck.Host.Settings;

namespace DashDeck.Host.Stage;

/// <summary>
/// One native application the user added through the settings UI, rather than one shipped in
/// code.
/// </summary>
/// <remarks>
/// The same three facts a built-in <see cref="AppLaunchSpec"/> carries, minus the install-path
/// guessing: a UI-added app was browsed to, so its path is a single concrete file rather than a
/// list of likely places. Every property is defaulted and the JSON names are stable, so a file
/// written by an older or newer build still loads — the same forward/back promise the other
/// stores make.
/// </remarks>
public sealed record UserAppEntry
{
    /// <summary>Short name for the launcher, shown on the stage chip. Upper-cased when used.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>The executable the user browsed to.</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    /// <summary>Anything the app needs on its command line — how you aim a browser at a page.</summary>
    [JsonPropertyName("arguments")]
    public string Arguments { get; init; } = "";

    /// <summary>True when the executable is actually on this machine right now.</summary>
    [JsonIgnore]
    public bool IsInstalled => !string.IsNullOrWhiteSpace(Path) && System.IO.File.Exists(Path);
}

/// <summary>The file shape: a list under one key, so it versions like the dashboard file.</summary>
internal sealed record UserAppFile
{
    [JsonPropertyName("apps")]
    public IReadOnlyList<UserAppEntry> Apps { get; init; } = [];
}

/// <summary>
/// The apps the user has added, loaded from and saved to <c>apps.json</c>.
/// </summary>
/// <remarks>
/// <b>An instance with an event, unlike the static <see cref="SettingsStore"/>.</b> The stage
/// picker is built from this list, and adding an app should light up a launcher without a
/// restart — so the store raises <see cref="Changed"/> when the list is edited and the shell
/// rebuilds its options from it. Same folder and the same never-throw promise as every other
/// store here (<see cref="JsonFile"/>).
/// </remarks>
public sealed class UserAppStore
{
    private readonly string _path;

    /// <summary>The standard store, reading <c>%LOCALAPPDATA%\DashDeck\apps.json</c>.</summary>
    public UserAppStore()
        : this(JsonFile.InLocalAppData("apps.json"))
    {
    }

    /// <summary>A store at an explicit path. For tests, which must not touch the real profile.</summary>
    public UserAppStore(string path)
    {
        _path = path;
        var file = JsonFile.Load<UserAppFile>(_path, out var error);
        LastError = error;
        Apps = [.. file?.Apps ?? []];
    }

    /// <summary>The apps, in the order they were added. Bound directly by the settings list.</summary>
    public ObservableCollection<UserAppEntry> Apps { get; }

    /// <summary>Raised after any add or remove, once the file is written.</summary>
    public event EventHandler? Changed;

    /// <summary>Why the last load or save failed, if it did.</summary>
    public string? LastError { get; private set; }

    /// <summary>Where the file is. Shown in settings, because a file you cannot find is one you cannot back up.</summary>
    public string Path => _path;

    /// <summary>Add an app and persist.</summary>
    public void Add(UserAppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Apps.Add(entry);
        SaveAndNotify();
    }

    /// <summary>Remove an app and persist. A no-op if it was not there.</summary>
    public void Remove(UserAppEntry entry)
    {
        if (Apps.Remove(entry))
        {
            SaveAndNotify();
        }
    }

    private void SaveAndNotify()
    {
        JsonFile.Save(_path, new UserAppFile { Apps = [.. Apps] }, out var error);
        LastError = error;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

using System.IO;
using DashDeck.Core.Catalog;

namespace DashDeck.Host.Settings;

/// <summary>
/// The signals the user has added or corrected, kept apart from the shipped catalog (ADR-0032).
/// </summary>
/// <remarks>
/// The shipped catalog sits beside the executable, which <c>publish.ps1</c> deletes and rewrites
/// on every deploy, and it is the known-good standard set besides. So a signal found on the
/// truck — or a shipped one corrected — is written here, in <c>%LOCALAPPDATA%\DashDeck\</c>
/// with the rest of the user's choices, and laid over the shipped catalog at launch by
/// <see cref="SignalCatalog.Overlay"/>. Same file shape as the catalog, so a definition that
/// proves itself can be lifted into the shipped file by copying it.
/// <para>
/// <b>Nothing here throws</b>, for the reason <see cref="JsonFile"/> gives: a corrupt file is
/// an empty overlay and a recorded reason, never a dash that will not start.
/// </para>
/// </remarks>
public sealed class UserSignalStore
{
    public UserSignalStore()
        : this(JsonFile.InLocalAppData("signals.user.json"))
    {
    }

    public UserSignalStore(string path)
    {
        Path = path;
        Definitions = Load(path, out var error);
        LastError = error;
    }

    /// <summary>Where the file is. Shown in Settings, so it can be found and backed up.</summary>
    public string Path { get; }

    /// <summary>The user's definitions, in file order.</summary>
    public IReadOnlyList<SignalDefinition> Definitions { get; private set; }

    /// <summary>Why the last load or save failed, if it did.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised after every successful change, so the inventory can redraw.</summary>
    public event EventHandler? Changed;

    /// <summary>True when the user has a definition with this id.</summary>
    public bool Contains(string id) =>
        Definitions.Any(d => string.Equals(d.Id, id, StringComparison.Ordinal));

    /// <summary>Add a definition, or replace the user's own one with the same id.</summary>
    public void Upsert(SignalDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var list = Definitions.ToList();
        var index = list.FindIndex(d => string.Equals(d.Id, definition.Id, StringComparison.Ordinal));

        if (index >= 0)
        {
            list[index] = definition;
        }
        else
        {
            list.Add(definition);
        }

        Write(list);
    }

    /// <summary>
    /// Drop the user's definition. For a correction, the shipped one comes back; for an
    /// addition, the signal goes.
    /// </summary>
    public bool Remove(string id)
    {
        var list = Definitions.Where(d => !string.Equals(d.Id, id, StringComparison.Ordinal)).ToList();

        if (list.Count == Definitions.Count)
        {
            return false;
        }

        Write(list);
        return true;
    }

    /// <summary>Read the file, or nothing if it is absent or unreadable. Never throws.</summary>
    public static IReadOnlyList<SignalDefinition> Load(string path, out string? error)
    {
        error = null;

        try
        {
            return File.Exists(path) ? SignalCatalog.ParseList(File.ReadAllText(path)) : [];
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return [];
        }
    }

    private void Write(List<SignalDefinition> list)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, SignalCatalog.ToJson(list));
            Definitions = list;
            LastError = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}

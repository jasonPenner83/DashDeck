using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Host.Stage.Launcher;

namespace DashDeck.Host.ViewModels;

/// <summary>One entry in the launcher, as Settings lists it.</summary>
public sealed record LauncherRowViewModel(string Name, string Detail, bool OnQuickBar, bool Hidden)
{
    /// <summary>Where it shows: on the bar, in the grid only, or not at all.</summary>
    public string Place => Hidden ? "HIDDEN" : OnQuickBar ? "BAR" : "GRID";
}

/// <summary>
/// The STAGE LAUNCHER block of Settings ▸ Apps (ADR-0038): what the launcher file offers, in its
/// order, and the buttons to make it yours and pick up an edit.
/// </summary>
/// <remarks>
/// Read-only, like the theme token reference: the file is the editor for now, and this says what
/// it currently holds and what is wrong with it. MAKE IT MINE copies the built-in list to
/// <c>launcher.json</c> so there is something to edit; RELOAD reads it again.
/// </remarks>
public sealed partial class LauncherSettingsViewModel : ObservableObject
{
    private readonly StageLauncherStore _store;
    private readonly IThemeDialogs _dialogs;

    public LauncherSettingsViewModel(StageLauncherStore store, IThemeDialogs dialogs)
    {
        _store = store;
        _dialogs = dialogs;
        store.Changed += (_, _) => Rebuild();
        Rebuild();
    }

    /// <summary>Every entry in file order, hidden ones included.</summary>
    public ObservableCollection<LauncherRowViewModel> Entries { get; } = [];

    /// <summary>Which list is in use, in words.</summary>
    public string Source => _store.Current.Origin is LauncherOrigin.Yours
        ? $"YOUR FILE — {Path.GetFileName(_store.UserPath)}"
        : "BUILT IN";

    /// <summary>Entries left out, and why your file is not in use when it is not.</summary>
    public IReadOnlyList<string> Warnings => _store.Problems;

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>Where your launcher file goes.</summary>
    public string UserPath => _store.UserPath;

    /// <summary>The last thing a button did, or why it could not.</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>True while there is no <c>launcher.json</c> yet.</summary>
    public bool CanMakeYours => !File.Exists(_store.UserPath);

    [RelayCommand]
    private void Reload()
    {
        _store.Reload();
        Status = _store.Current.Origin is LauncherOrigin.Yours
            ? $"Reloaded {Path.GetFileName(_store.UserPath)}: {_store.Current.Offered.Count(e => e.Name.Length > 0)} entries."
            : "Reloaded: using the built-in launcher.";
    }

    [RelayCommand(CanExecute = nameof(CanMakeYours))]
    private void MakeYours()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_store.UserPath)!);
            File.WriteAllText(_store.UserPath, StageLauncher.YoursHeader + StageLauncher.BuiltInJson + Environment.NewLine);
            _store.Reload();
            Status = $"Created {Path.GetFileName(_store.UserPath)} from the built-in list. Edit it by hand, then RELOAD.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not create it: {ex.Message}";
        }

        MakeYoursCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanMakeYours));
    }

    [RelayCommand]
    private void OpenFolder()
    {
        _store.WriteExample();
        _dialogs.OpenFolder(Path.GetDirectoryName(_store.UserPath)!);
    }

    private void Rebuild()
    {
        var launcher = _store.Current;
        var bar = new HashSet<string>(launcher.QuickBar ?? [], StringComparer.OrdinalIgnoreCase);
        var defaultBar = launcher.QuickBar is null
            ? launcher.Offered.Where(e => e.Type != LauncherTypes.UserApps).Take(StageLauncher.QuickBarSlots).Select(e => e.Name).ToHashSet()
            : bar;

        Entries.Clear();
        foreach (var entry in launcher.Entries)
        {
            if (entry.Type == LauncherTypes.UserApps)
            {
                Entries.Add(new LauncherRowViewModel("YOUR APPS", "userApps — the apps added below, in their order", false, entry.Hidden));
                continue;
            }

            var what = entry.Type switch
            {
                LauncherTypes.Web => $"web · {entry.Url}",
                LauncherTypes.App => $"app · {entry.Paths?.FirstOrDefault()}",
                LauncherTypes.Gauges when entry.Layout is { Length: > 0 } layout => $"gauges · layout {layout}",
                LauncherTypes.Compass => $"compass · layout {entry.Layout ?? "compass"}",
                LauncherTypes.Video when entry.Path is { Length: > 0 } file => $"video · {file}",
                _ => entry.Type,
            };

            if (entry.PlaysAudio)
            {
                what += " · keeps playing";
            }

            Entries.Add(new LauncherRowViewModel(entry.Name, what, defaultBar.Contains(entry.Name), entry.Hidden));
        }

        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Warnings));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(CanMakeYours));
        MakeYoursCommand.NotifyCanExecuteChanged();
    }
}

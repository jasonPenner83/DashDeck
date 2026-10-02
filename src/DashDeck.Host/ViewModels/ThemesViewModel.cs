using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Host.Theme;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// What the Themes section needs from whatever applies themes: <see cref="ThemeService"/> in the
/// app, a fake in the tests.
/// </summary>
public interface IThemeHost : INotifyPropertyChanged
{
    ThemeLibrary Library { get; }

    /// <summary>The theme being worn.</summary>
    ThemeDefinition Current { get; }

    /// <summary>What is wrong with it, one line each.</summary>
    IReadOnlyList<string> Problems { get; }

    /// <summary>Wear a theme, now, and remember it.</summary>
    void Wear(ThemeDefinition theme);

    /// <summary>Read the folders again and re-apply.</summary>
    void Reload();
}

/// <summary>The file pickers the section needs. Windows dialogs in the app; canned answers in tests.</summary>
public interface IThemeDialogs
{
    /// <summary>Ask for a theme file to import, or null if cancelled.</summary>
    string? PickThemeFile();

    /// <summary>Ask for a folder to export into, or null if cancelled.</summary>
    string? PickExportFolder();

    /// <summary>Show a folder in Explorer.</summary>
    void OpenFolder(string folder);
}

/// <summary>One theme in the list.</summary>
public sealed partial class ThemeRowViewModel : ObservableObject
{
    public ThemeRowViewModel(ThemeDefinition theme)
    {
        Theme = theme;
        var day = ThemeResolver.Resolve(theme, night: false);

        // What it looks like at a glance: background, a surface, the accent, a button, a caption.
        Swatches =
        [
            day.Colour("canvas").ToString(),
            day.Colour("surface").Over(day.Colour("canvas")).ToString(),
            day.Colour("accent").ToString(),
            day.Colour("buttonBackground").Over(day.Colour("canvas")).ToString(),
            day.Colour("caption").ToString(),
        ];

        HasProblems = day.Problems.Count > 0 || ThemeResolver.Legibility(day).Count > 0;
    }

    public ThemeDefinition Theme { get; }

    public string Caption => Theme.Name.ToUpperInvariant();

    /// <summary>Where it came from, and who made it.</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string> { OriginLabel };
            if (Theme.Author.Length > 0)
            {
                parts.Add($"by {Theme.Author}");
            }

            parts.Add($"{Theme.Tokens.Count} token{(Theme.Tokens.Count == 1 ? "" : "s")} changed");
            if (HasProblems)
            {
                parts.Add("has warnings");
            }

            return string.Join("  ·  ", parts);
        }
    }

    public string OriginLabel => Theme.Origin switch
    {
        ThemeOrigin.Shipped => "SHIPPED",
        ThemeOrigin.Yours => "YOURS",
        _ => "BUILT IN",
    };

    public string Description => Theme.Description;

    /// <summary>Five colours as <c>#RRGGBB</c>, painted as a strip beside the name.</summary>
    public IReadOnlyList<string> Swatches { get; }

    public bool HasProblems { get; }

    /// <summary>Only the user's own can be deleted.</summary>
    public bool CanDelete => Theme.Origin is ThemeOrigin.Yours;

    [ObservableProperty]
    private bool _isCurrent;
}

/// <summary>One token and what the current theme sets it to — the reference for editing a file by hand.</summary>
public sealed record ThemeTokenRowViewModel(string Key, string Kind, string Value, bool IsSet, string Description)
{
    /// <summary>Paintable when it is a colour.</summary>
    public string? Swatch => Kind == "colour" ? Value : null;

    public string Detail => IsSet ? $"{Kind}  ·  set by this theme  ·  {Description}" : $"{Kind}  ·  default  ·  {Description}";
}

/// <summary>
/// Settings ▸ Themes (ADR-0036): choose a theme, bring one in, send one out, start your own.
/// </summary>
/// <remarks>
/// Modelled on Home Assistant: a theme is a file of named tokens, the list is what is in the
/// folders, and RELOAD picks up an edit made by hand. There is no in-app editor in this round —
/// SAVE AS makes your own copy, OPEN FOLDER shows where it is, and the token reference below the
/// list says what every token is and what the current theme sets it to.
/// </remarks>
public sealed partial class ThemesViewModel : ObservableObject
{
    private readonly IThemeHost _host;
    private readonly IThemeDialogs _dialogs;

    public ThemesViewModel(IThemeHost host, IThemeDialogs dialogs)
    {
        _host = host;
        _dialogs = dialogs;

        host.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IThemeHost.Current) or nameof(IThemeHost.Problems))
            {
                Sync();
            }
        };

        Rebuild();
    }

    public ObservableCollection<ThemeRowViewModel> Themes { get; } = [];

    /// <summary>Every token, and the current theme's value for it.</summary>
    public ObservableCollection<ThemeTokenRowViewModel> Tokens { get; } = [];

    /// <summary>The theme being worn, in capitals.</summary>
    public string CurrentName => _host.Current.Name.ToUpperInvariant();

    /// <summary>What is wrong with the current theme, and any file that would not load.</summary>
    public IReadOnlyList<string> Warnings =>
    [
        .. _host.Problems,
        .. _host.Library.Problems.Select(p => $"{p.File} was not loaded: {p.Reason}"),
    ];

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>Where your themes live.</summary>
    public string UserFolder => _host.Library.UserFolder;

    /// <summary>The last thing a button did, or why it could not.</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>The name SAVE AS will use.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAsCommand))]
    private string _saveAsName = "";

    [RelayCommand]
    private void Wear(ThemeRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        _host.Wear(row.Theme);
        Status = $"Wearing {row.Theme.Name}.";
    }

    [RelayCommand]
    private void Reload()
    {
        _host.Reload();
        Rebuild();
        Status = string.Create(CultureInfo.CurrentCulture, $"Reloaded: {_host.Library.Themes.Count} themes.");
    }

    [RelayCommand]
    private void Import()
    {
        if (_dialogs.PickThemeFile() is not { } path)
        {
            return;
        }

        try
        {
            var imported = _host.Library.Import(path);
            _host.Wear(imported);
            Rebuild();
            Status = $"Imported {imported.Name}, and wearing it.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Status = $"Could not import {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Export()
    {
        if (_dialogs.PickExportFolder() is not { } folder)
        {
            return;
        }

        try
        {
            var path = _host.Library.Export(_host.Current, folder);
            Status = $"Exported to {path}.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Status = $"Could not export: {ex.Message}";
        }
    }

    private bool CanSaveAs() => !string.IsNullOrWhiteSpace(SaveAsName);

    [RelayCommand(CanExecute = nameof(CanSaveAs))]
    private void SaveAs()
    {
        try
        {
            var mine = _host.Library.SaveAs(_host.Current, SaveAsName);
            _host.Wear(mine);
            Rebuild();
            SaveAsName = "";
            Status = $"Saved as {mine.Name} in your themes folder. Edit {Path.GetFileName(mine.FilePath)} by hand, then RELOAD.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Delete(ThemeRowViewModel? row)
    {
        if (row is null || !row.CanDelete)
        {
            return;
        }

        var wasCurrent = row.Theme.Id == _host.Current.Id;

        try
        {
            if (_host.Library.Delete(row.Theme))
            {
                if (wasCurrent)
                {
                    _host.Wear(ThemeDefinition.BuiltIn);
                }

                Rebuild();
                Status = $"Deleted {row.Theme.Name}.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not delete: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(UserFolder);
        _dialogs.OpenFolder(UserFolder);
    }

    private void Rebuild()
    {
        Themes.Clear();
        foreach (var theme in _host.Library.Themes)
        {
            Themes.Add(new ThemeRowViewModel(theme));
        }

        Sync();
    }

    private void Sync()
    {
        foreach (var row in Themes)
        {
            row.IsCurrent = row.Theme.Id == _host.Current.Id;
        }

        var day = ThemeResolver.Resolve(_host.Current, night: false);
        Tokens.Clear();

        foreach (var token in ThemeTokens.All)
        {
            var value = token.Kind switch
            {
                TokenKind.Colour => day.Colour(token.Key).ToString(),
                TokenKind.Number => day.Number(token.Key).ToString(CultureInfo.InvariantCulture),
                _ => day.Fonts[token.Key],
            };

            Tokens.Add(new ThemeTokenRowViewModel(
                token.Key,
                token.Kind.ToString().ToLowerInvariant(),
                value,
                _host.Current.Tokens.ContainsKey(token.Key),
                token.Description));
        }

        OnPropertyChanged(nameof(CurrentName));
        OnPropertyChanged(nameof(Warnings));
        OnPropertyChanged(nameof(HasWarnings));
    }
}

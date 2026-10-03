using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Host.Stage.Gauges;

namespace DashDeck.Host.ViewModels;

/// <summary>One choice in the stage layout list: FOLLOW THEME, or a layout.</summary>
public sealed partial class StageLayoutRowViewModel(string choice, string caption, string detail, StageLayout? layout) : ObservableObject
{
    /// <summary><see cref="StageLayoutService.FollowTheme"/> or a layout id.</summary>
    public string Choice { get; } = choice;

    public string Caption { get; } = caption;

    public string Detail { get; } = detail;

    public StageLayout? Layout { get; } = layout;

    public bool CanDelete => Layout?.Origin is LayoutOrigin.Yours;

    [ObservableProperty]
    private bool _isCurrent;
}

/// <summary>
/// Settings ▸ Themes ▸ STAGE LAYOUT (ADR-0037) and CLIMATE LAYOUT (ADR-0040): which layout the
/// GAUGES stage, or the CLIMATE panel, shows, and the loop for writing your own — SAVE AS, OPEN
/// FOLDER, edit, RELOAD. One view model and one template for both; the words follow the canvas.
/// </summary>
public sealed partial class StageLayoutsViewModel : ObservableObject
{
    private readonly StageLayoutService _layouts;
    private readonly IThemeDialogs _dialogs;

    public StageLayoutsViewModel(StageLayoutService layouts, IThemeDialogs dialogs)
    {
        _layouts = layouts;
        _dialogs = dialogs;
        layouts.LayoutChanged += (_, _) => Rebuild();
        Rebuild();
    }

    public ObservableCollection<StageLayoutRowViewModel> Rows { get; } = [];

    private bool IsClimate => _layouts.Library.Canvas == LayoutCanvas.Climate;

    private bool IsConsole => _layouts.Library.Canvas == LayoutCanvas.Console;

    /// <summary>What this surface is called in a sentence: the stage, the climate panel.</summary>
    private string Surface => _layouts.Library.Canvas.Name;

    /// <summary>The block's heading.</summary>
    public string Heading => IsConsole ? "CONSOLE LAYOUT" : IsClimate ? "CLIMATE LAYOUT" : "STAGE LAYOUT";

    /// <summary>What the block is for.</summary>
    public string Intro => IsConsole
        ? "What DASH draws below the stage: speed, rpm, fuel, range, warning lights, odometer and economy, where each sits and how it looks — a JSON file like the stage's. Your cards are on the stage now, behind the CARDS button."
        : IsClimate
        ? "What the CLIMATE panel draws in place of the cards: set temperatures, fan, airflow, seats and switches, where each sits and how it looks — a JSON file like the stage's. It shows what the truck reports and changes nothing."
        : "What the GAUGES stage draws: every gauge, label, clock and panel, where it sits, what it reads and how it looks — a JSON file, like a Home Assistant dashboard. A theme can bring its own.";

    /// <summary>Where the files are, and how to make your own.</summary>
    public string FolderNote => IsConsole
        ? $"Your console layouts are kept in {UserFolder}. The examples folder inside it has the built-in Clean console and every shipped one as a file to copy from. Save as the name a theme uses (lcars) to replace its console. The console is {_layouts.Library.Canvas.Width:0} × {_layouts.Library.Canvas.Height:0}."
        : IsClimate
        ? $"Your climate layouts are kept in {UserFolder}. The examples folder inside it has the built-in Clean panel and every shipped one as a file to copy from. Save as the name a theme uses (lcars) to replace its panel. The panel is {_layouts.Library.Canvas.Width:0} × {_layouts.Library.Canvas.Height:0}."
        : $"Your layouts are kept in {UserFolder}. The examples folder inside it has every shipped layout as a file to copy from. Save as the name a theme uses (lcars) to replace its stage. The stage is {_layouts.Library.Canvas.Width:0} × {_layouts.Library.Canvas.Height:0}; RELOAD STAGE LAYOUT in the three-dot menu redraws it without leaving the stage.";

    /// <summary>The layout showing, in capitals.</summary>
    public string CurrentName => _layouts.Current.Name.ToUpperInvariant();

    /// <summary>Why it is that one.</summary>
    public string Reason => _layouts.Reason;

    /// <summary>What is wrong with the layout showing, and any file that would not load.</summary>
    public IReadOnlyList<string> Warnings =>
    [
        .. _layouts.Current.Problems,
        .. _layouts.Library.Problems.Select(p => $"{p.File} was not loaded: {p.Reason}"),
    ];

    public bool HasWarnings => Warnings.Count > 0;

    public string UserFolder => _layouts.Library.UserFolder;

    [ObservableProperty]
    private string _status = "";

    /// <summary>The file name SAVE AS uses. Saving as "lcars" replaces the shipped LCARS stage for the theme.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAsCommand))]
    private string _saveAsName = "";

    [RelayCommand]
    private void Choose(StageLayoutRowViewModel? row)
    {
        if (row is not null)
        {
            _layouts.Choose(row.Choice);
            Status = row.Layout is null ? $"The {Surface} follows the theme." : $"The {Surface} shows {row.Layout.Name}.";
        }
    }

    [RelayCommand]
    private void Reload()
    {
        _layouts.Reload();
        Status = $"Reloaded: {_layouts.Library.Layouts.Count} layouts.";
    }

    private bool CanSaveAs() => !string.IsNullOrWhiteSpace(SaveAsName);

    [RelayCommand(CanExecute = nameof(CanSaveAs))]
    private void SaveAs()
    {
        try
        {
            var mine = _layouts.Library.SaveAs(_layouts.Current, SaveAsName);

            // Saved under the name the theme asks for ("lcars"), it now replaces the shipped one
            // for that theme, and following the theme picks it up. Otherwise show it outright.
            _layouts.Reload();
            if (_layouts.Current.Id != mine.Id)
            {
                _layouts.Choose(mine.Id);
            }

            SaveAsName = "";
            Status = $"Saved as {Path.GetFileName(mine.FilePath)} in your {Path.GetFileName(UserFolder)} folder. Edit it, then RELOAD.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = $"Could not save: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Delete(StageLayoutRowViewModel? row)
    {
        if (row?.Layout is not { Origin: LayoutOrigin.Yours } layout)
        {
            return;
        }

        try
        {
            var wasChosen = _layouts.Choice == layout.Id;
            _layouts.Library.Delete(layout);

            // Deleting the one chosen hands the stage back to the theme rather than to a fallback.
            if (wasChosen)
            {
                _layouts.Choose(StageLayoutService.FollowTheme);
            }
            else
            {
                _layouts.Reload();
            }
            Status = $"Deleted {layout.Name}.";
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
        _layouts.Library.WriteExamples();
        _dialogs.OpenFolder(UserFolder);
    }

    private void Rebuild()
    {
        Rows.Clear();
        Rows.Add(new StageLayoutRowViewModel(StageLayoutService.FollowTheme, "FOLLOW THE THEME", IsConsole
            ? "Each theme can bring its own console — LCARS brings an LCARS one; the rest wear Clean."
            : IsClimate
            ? "Each theme can bring its own climate panel — LCARS brings an LCARS one; the rest wear Clean."
            : "Each theme brings its own stage — LCARS brings the LCARS stage, the DashDeck look the F-150 cluster.", null));

        foreach (var layout in _layouts.Library.Layouts)
        {
            var origin = layout.Origin switch
            {
                LayoutOrigin.Yours => "YOURS",
                LayoutOrigin.Shipped => "SHIPPED",
                _ => "BUILT IN",
            };

            Rows.Add(new StageLayoutRowViewModel(layout.Id, layout.Name.ToUpperInvariant(),
                $"{origin}  ·  {layout.Slug}  ·  {layout.Elements.Count} elements{(layout.Problems.Count > 0 ? "  ·  has warnings" : "")}", layout));
        }

        foreach (var row in Rows)
        {
            row.IsCurrent = row.Choice == _layouts.Choice;
        }

        OnPropertyChanged(nameof(CurrentName));
        OnPropertyChanged(nameof(Reason));
        OnPropertyChanged(nameof(Warnings));
        OnPropertyChanged(nameof(HasWarnings));
    }
}

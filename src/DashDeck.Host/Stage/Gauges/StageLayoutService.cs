using CommunityToolkit.Mvvm.ComponentModel;

namespace DashDeck.Host.Stage.Gauges;

/// <summary>
/// Which stage layout is showing (ADR-0037): the one the theme names, or one chosen outright.
/// </summary>
/// <remarks>
/// A theme can name its own stage — <c>"stageLayout": "lcars"</c> — so wearing LCARS brings the
/// LCARS stage with it, and wearing the DashDeck look brings back the F-150 cluster. Choosing a
/// layout in Settings overrides that until FOLLOW THEME is chosen again. A name that matches
/// nothing falls back to the built-in cluster and says so: never a blank stage.
/// </remarks>
public sealed partial class StageLayoutService : ObservableObject
{
    /// <summary>The stored choice that means "whatever the theme names".</summary>
    public const string FollowTheme = "theme";

    private readonly Func<string?> _themeLayout;
    private readonly Action<string>? _persist;

    public StageLayoutService(StageLayoutLibrary library, Func<string?> themeLayout, string? choice, Action<string>? persist = null)
    {
        Library = library;
        _themeLayout = themeLayout;
        _persist = persist;
        _choice = string.IsNullOrWhiteSpace(choice) ? FollowTheme : choice;
        Resolve(raise: false);
    }

    /// <summary>
    /// A service that always shows one layout, by file name or id — what a launcher entry with
    /// <c>"layout": "towing"</c> uses (ADR-0038). It shares the library, so RELOAD still picks up
    /// a hand edit, and ignores the theme and the Settings choice.
    /// </summary>
    public static StageLayoutService Pinned(StageLayoutLibrary library, string layout)
    {
        var pinned = new StageLayoutService(library, () => layout, FollowTheme);
        pinned._pinned = true;
        pinned.Resolve(raise: false);
        return pinned;
    }

    private bool _pinned;

    public StageLayoutLibrary Library { get; }

    /// <summary><see cref="FollowTheme"/>, or a layout id.</summary>
    [ObservableProperty]
    private string _choice;

    /// <summary>The layout the stage shows.</summary>
    [ObservableProperty]
    private StageLayout _current = StageLayout.BuiltIn;

    /// <summary>Why it is this one, in words: "Following the theme", "Chosen", or why the wanted one is missing.</summary>
    [ObservableProperty]
    private string _reason = "";

    /// <summary>Raised whenever the stage should be drawn again: a new layout, or the same one reloaded.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>Show this layout, or <see cref="FollowTheme"/>, and remember it.</summary>
    public void Choose(string choice)
    {
        Choice = string.IsNullOrWhiteSpace(choice) ? FollowTheme : choice;
        _persist?.Invoke(Choice);
        Resolve(raise: true);
    }

    /// <summary>Read the folders again and redraw — for a layout edited by hand.</summary>
    public void Reload()
    {
        Library.Reload();
        Resolve(raise: true);
    }

    /// <summary>The theme changed; if the stage follows it, follow.</summary>
    public void ThemeChanged()
    {
        if (Choice == FollowTheme)
        {
            Resolve(raise: true);
        }
    }

    private void Resolve(bool raise)
    {
        if (Choice != FollowTheme)
        {
            if (Library.Find(Choice) is { } chosen)
            {
                Current = chosen;
                Reason = "Chosen in Settings.";
            }
            else
            {
                Current = StageLayout.BuiltIn;
                Reason = $"The chosen layout '{Choice}' is gone — showing the built-in cluster.";
            }
        }
        else if (_themeLayout() is { Length: > 0 } named)
        {
            if (Library.FindForTheme(named) is { } themed)
            {
                Current = themed;
                Reason = _pinned ? "Pinned by the launcher." : "Following the theme.";
            }
            else
            {
                Current = StageLayout.BuiltIn;
                Reason = _pinned
                    ? $"The launcher names '{named}', which is not in the stage folders — showing the built-in cluster."
                    : $"The theme names '{named}', which is not in the stage folders — showing the built-in cluster.";
            }
        }
        else
        {
            Current = StageLayout.BuiltIn;
            Reason = "Following the theme, which names no layout of its own.";
        }

        if (raise)
        {
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

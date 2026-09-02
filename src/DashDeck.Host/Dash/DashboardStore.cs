using DashDeck.Host.Settings;

namespace DashDeck.Host.Dash;

/// <summary>
/// Loads and saves the arranged dashboard.
/// </summary>
/// <remarks>
/// Its own file rather than a section of <c>settings.json</c>, which is described as
/// deliberately small and deliberately flat and would stop being either. A list of cards is
/// also the one piece of configuration worth handing to somebody else — "here is my
/// dashboard" is a file you can send, and that only works if it is a file.
/// <para>
/// Same folder and therefore the same guarantee: <c>%LOCALAPPDATA%\DashDeck\</c> is outside
/// the self-contained folder that <c>publish.ps1</c> deletes and rewrites, so an arranged
/// dash survives an update (ADR-0014).
/// </para>
/// </remarks>
/// <summary>
/// Where an arranged dashboard is read from and written to.
/// </summary>
/// <remarks>
/// A seam, and a deliberate one. Without it the view-model reaches straight into
/// <c>%LOCALAPPDATA%</c>, which means the activation rule — the thing that makes paging safe
/// for the request budget — could only be checked by looking at a running dash and believing
/// what it seemed to be doing. That is the same shape as the daylight-backoff bug: a
/// behaviour with real consequences and no seam to test it through, found in a deployed
/// build rather than in a test.
/// </remarks>
public interface IDashboardStore
{
    /// <summary>The arrangement, or null if there has never been one.</summary>
    DashboardLayout? Load();

    /// <summary>Write the arrangement out. Must never throw.</summary>
    void Save(DashboardLayout layout);
}

/// <summary>The real one: a JSON file beside the settings.</summary>
public sealed class FileDashboardStore : IDashboardStore
{
    /// <inheritdoc />
    public DashboardLayout? Load() => DashboardStore.Load();

    /// <inheritdoc />
    public void Save(DashboardLayout layout) => DashboardStore.Save(layout);
}

public static class DashboardStore
{
    /// <summary>Why the last load or save failed, if it did.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Where the dashboard file is. Shown in the editor.</summary>
    public static string Path => JsonFile.InLocalAppData("dashboard.json");

    /// <summary>
    /// Read the arranged dashboard, or <see langword="null"/> if there has never been one.
    /// </summary>
    /// <remarks>
    /// Null and empty are different answers and the caller needs both. Null means "never
    /// configured" and gets <see cref="DashboardLayout.Default"/>; an empty card list means
    /// the user removed every card, which is a choice and must survive a restart rather
    /// than being helpfully undone.
    /// </remarks>
    public static DashboardLayout? Load()
    {
        var layout = JsonFile.Load<DashboardLayout>(Path, out var error);
        LastError = error;
        return layout;
    }

    /// <summary>Write the arrangement out. Never throws.</summary>
    public static void Save(DashboardLayout layout)
    {
        JsonFile.Save(Path, layout, out var error);
        LastError = error;
    }
}

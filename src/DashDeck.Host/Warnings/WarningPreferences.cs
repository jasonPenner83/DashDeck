using DashDeck.Host.Settings;

namespace DashDeck.Host.Warnings;

/// <summary>What is kept about warnings between launches: popup choices and standing dismissals.</summary>
public interface IWarningPreferences
{
    IReadOnlyDictionary<string, bool> Popups { get; }

    void SetPopup(string id, bool? popup);

    IReadOnlyDictionary<string, double?> Dismissals { get; }

    void SaveDismissals(IReadOnlyDictionary<string, double?> dismissals);
}

/// <summary>Kept in <c>settings.json</c>, beside the rest.</summary>
public sealed class StoredWarningPreferences : IWarningPreferences
{
    public IReadOnlyDictionary<string, bool> Popups => SettingsStore.Load().WarningPopups;

    public void SetPopup(string id, bool? popup) => SettingsStore.Update(s =>
    {
        var popups = new Dictionary<string, bool>(s.WarningPopups);
        if (popup is { } on)
        {
            popups[id] = on;
        }
        else
        {
            popups.Remove(id);
        }

        return s with { WarningPopups = popups };
    });

    public IReadOnlyDictionary<string, double?> Dismissals => SettingsStore.Load().DismissedWarnings;

    public void SaveDismissals(IReadOnlyDictionary<string, double?> dismissals) =>
        SettingsStore.Update(s => s with { DismissedWarnings = new Dictionary<string, double?>(dismissals) });
}

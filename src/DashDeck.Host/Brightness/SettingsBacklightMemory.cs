using DashDeck.Core.Brightness;
using DashDeck.Host.Settings;

namespace DashDeck.Host.Brightness;

/// <summary>
/// Keeps a borrowed backlight level in <c>settings.json</c>, beside everything else (ADR-0068).
/// </summary>
/// <remarks>
/// The settings file rather than a file of its own because it is already written the moment
/// anything changes, already tolerant of being cut off mid-write, and already the one place a
/// person looks to see what DashDeck has remembered.
/// </remarks>
public sealed class SettingsBacklightMemory : IBacklightMemory
{
    /// <inheritdoc />
    public BacklightLeft? Load()
    {
        var stored = SettingsStore.Load();

        return stored.BacklightOriginal is { } original && stored.BacklightSet is { } set
            ? new BacklightLeft(original, set)
            : null;
    }

    /// <inheritdoc />
    public void Save(BacklightLeft? left) =>
        SettingsStore.Update(stored => stored with
        {
            BacklightOriginal = left?.Original,
            BacklightSet = left?.Set,
        });
}

namespace DashDeck.Abstractions;

/// <summary>
/// The component's settings, read as the user has configured them.
/// </summary>
/// <remarks>
/// Read-only from the component's side: settings are edited through the host's UI, backed by
/// the schema a manifest declares, and delivered here. A component reads a value and is told
/// when the user changes one, so it can react without being restarted.
/// <para>
/// Values are strings for the same reason storage is: the host stores and edits them without
/// needing to understand their meaning. A component parses its own — a rate, a colour, a
/// toggle — and should treat a missing or unparseable value as "use the default" rather than
/// failing, because a settings file can be older than the component reading it.
/// </para>
/// </remarks>
public interface IComponentSettings
{
    /// <summary>The value for <paramref name="key"/>, or null if the user has set none.</summary>
    string? Get(string key);

    /// <summary>Raised when any setting changes, so a component can re-read without a restart.</summary>
    event Action? Changed;
}

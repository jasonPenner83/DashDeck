namespace DashDeck.Abstractions;

/// <summary>
/// A component's private store. Small keyed strings, isolated to the one component.
/// </summary>
/// <remarks>
/// Strings rather than typed objects, because the contract has to survive a component being
/// recompiled against a newer SDK without the host understanding its data — the component
/// owns the shape and serialises its own JSON. The host owns only <em>where</em> it lands,
/// which is a per-component scope no other component can name.
/// <para>
/// For persistence a component actually cares about, not a cache: a dash loses power without
/// notice, so a write returns only once it is durable. Keep writes small and occasional —
/// a trip odometer every few seconds, not a telemetry firehose.
/// </para>
/// </remarks>
public interface IComponentStorage
{
    /// <summary>The value stored under <paramref name="key"/>, or null if there is none.</summary>
    Task<string?> ReadAsync(string key, CancellationToken ct = default);

    /// <summary>Store <paramref name="value"/> under <paramref name="key"/>, durably.</summary>
    Task WriteAsync(string key, string value, CancellationToken ct = default);

    /// <summary>Remove <paramref name="key"/>. A no-op if it was not present.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
}

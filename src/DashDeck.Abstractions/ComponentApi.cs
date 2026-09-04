namespace DashDeck.Abstractions;

/// <summary>
/// The version of the component contract this assembly defines.
/// </summary>
/// <remarks>
/// <b>This is the promise the ecosystem is built on</b> (ADR-0008): a component written
/// against <c>apiVersion 1.0</c> keeps loading as the host advances, because the host serves
/// a <em>range</em> of contract versions and this is the one the assembly in a component's
/// hands actually is. Host <c>v1.4.0</c> serving <c>apiVersion 1.0</c> is normal and must
/// stay so.
/// <para>
/// Bumped only when the contract itself changes — a new member, a changed signature — never
/// when the app version moves. A major bump invalidates every existing component and so
/// requires its own ADR. Because the population of authors is still small, that cost is low
/// now and steep later, which is the whole reason the first component is built through this
/// contract rather than around it: to exercise the shape before it ossifies.
/// </para>
/// </remarks>
public static class ComponentApi
{
    /// <summary>The contract version, <c>major.minor</c>. Additive changes bump the minor.</summary>
    public const string Version = "1.0";
}

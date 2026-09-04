namespace DashDeck.Abstractions;

/// <summary>
/// A component: the unit the ecosystem is built to make cheap to add (ADR-0002).
/// </summary>
/// <remarks>
/// Every component implements this, UI or not. The view half lives in a separate
/// <c>net10.0-windows</c> assembly (<c>IDashComponentView</c>, ADR-0010) so a headless
/// component — a trip logger, a Home Assistant bridge — never drags in a UI framework.
/// <para>
/// <b>Lifecycle is tied to visibility, and it is enforced, not advisory.</b> A component
/// off the current page is deactivated so its signal declarations withdraw and it stops
/// spending the adapter's shared budget (ADR-0004, ADR-0015). The four transitions below
/// are the whole contract; the host calls them, wrapped and time-boxed, and a component
/// that throws or hangs in one is contained rather than trusted (ADR-0002).
/// </para>
/// <para>
/// <b>Assume you will be stopped.</b> A dash is closed by having its power pulled, so
/// <see cref="StopAsync"/> may never be reached. Persist anything worth keeping as you go,
/// through <see cref="IComponentContext.Storage"/>, not in a shutdown handler.
/// </para>
/// </remarks>
public interface IDashComponent
{
    /// <summary>
    /// Stable identity, matching the manifest's <c>id</c>.
    /// </summary>
    /// <remarks>
    /// Reverse-DNS by convention (<c>com.jpenner.tripcomputer</c>), so two authors cannot
    /// collide. The host trusts the manifest for identity and only checks this agrees.
    /// </remarks>
    string Id { get; }

    /// <summary>
    /// Hand the component its context. Called once, before any other method.
    /// </summary>
    /// <remarks>
    /// The one place a component is given the host's services — signals, storage, settings,
    /// the clock. Hold the context; there is no second delivery. Do the cheap wiring here
    /// and leave anything expensive for <see cref="StartAsync"/>, because a component is
    /// initialised whether or not it is ever shown.
    /// </remarks>
    Task InitializeAsync(IComponentContext context, CancellationToken ct);

    /// <summary>Became visible. Acquire what only a shown component needs.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Hidden — went off the visible page. Release expensive resources and let signal
    /// declarations withdraw; the component may be started again without warning.
    /// </summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>
    /// The app was backgrounded or the vehicle link dropped. Heavier than a stop: expect no
    /// data until <see cref="ResumeAsync"/>.
    /// </summary>
    Task SuspendAsync(CancellationToken ct);

    /// <summary>The app is foregrounded or the vehicle is back. Pick up where suspend left off.</summary>
    Task ResumeAsync(CancellationToken ct);
}

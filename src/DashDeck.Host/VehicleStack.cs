using DashDeck.Abstractions;
using DashDeck.Core;
using DashDeck.Core.Catalog;
using DashDeck.Core.Identity;
using DashDeck.Core.Link;
using DashDeck.Simulator;
using DashDeck.Vehicle;
using DashDeck.Vehicle.Diagnostics;
using DashDeck.Vehicle.Elm;

// System.Windows.Shapes.Path is in scope in a WPF project, so the file-system one is
// aliased rather than imported.
using IoFile = System.IO.File;
using IoPath = System.IO.Path;

namespace DashDeck.Host;

/// <summary>
/// Builds and owns the vehicle pipeline the shell renders.
/// </summary>
/// <remarks>
/// Deliberately the same pipeline the debug console drives â€” synthetic transport, ELM
/// adapter, catalog, arbiter, state bus â€” with nothing shell-specific in it. The shell is
/// just another consumer of the state bus, which is the property that lets a real adapter
/// replace the bottom layer later without anything above it changing (ADR-0003).
/// </remarks>
public sealed class VehicleStack : IAsyncDisposable, ViewModels.ISignalInventorySource, ViewModels.IAdapterStatus
{
    private readonly VehicleService _service;
    private readonly SwitchableTransport _switchable;
    private readonly IReadOnlyCollection<string> _reservedPorts;
    private AdapterLinkTransport? _link;
    private AdapterFailover? _failover;

    private VehicleStack(
        VehicleService service,
        SwitchableTransport switchable,
        string driveName,
        LoadedCatalog loaded,
        AdapterLinkTransport? link,
        IReadOnlyCollection<string> reservedPorts)
    {
        _service = service;
        _switchable = switchable;
        DriveName = driveName;
        Loaded = loaded;
        _reservedPorts = reservedPorts;
        Adopt(link);
    }

    /// <summary>Raised on the vehicle worker's thread whenever the adapter is found (ADR-0034).</summary>
    public event Action<AdapterLocation>? AdapterFound;

    /// <summary>The real adapter, while the dash is reading it; null while simulated.</summary>
    public AdapterLocation? LiveAdapter => IsSimulated ? null : _link?.Current;

    /// <summary>
    /// Pause DashDeck's own search for the adapter while the ports are tested, so the two never
    /// open the same port at once and the test never reports DashDeck itself as "another program".
    /// </summary>
    /// <remarks>
    /// Nothing to pause when there is no link, or when it is live and connected — the test leaves
    /// the connected port alone. While it is searching or reconnecting, the link waits; polling
    /// waits with it, which costs nothing, since readings are stale until it answers anyway.
    /// </remarks>
    public async Task<IDisposable> PauseSearchAsync(CancellationToken ct)
    {
        if (_link is not { } link || (!IsSimulated && link.State == TransportState.Connected))
        {
            return NoPause.Instance;
        }

        return await link.PauseAsync(ct);
    }

    private sealed class NoPause : IDisposable
    {
        public static readonly NoPause Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>Why the link last failed — found nothing, dropped, wrong rate — or null.</summary>
    public string? LinkProblem => _link?.LastProblem;

    /// <summary>The port being watched for an adapter while simulated, or null when none is chosen.</summary>
    public string? WatchedPort => IsSimulated && _failover is { IsWatching: true } ? _link?.PreferredPort : null;

    private void Adopt(AdapterLinkTransport? link)
    {
        _link = link;

        if (link is not null)
        {
            link.Located += location => AdapterFound?.Invoke(location);

            // Everything the link does, in a file a person can read after a test in the truck.
            link.Logged += AdapterLog.Append;
        }
    }

    /// <summary>How the running catalog was put together: standard, vehicle packs, your overlay.</summary>
    public LoadedCatalog Loaded { get; }

    /// <summary>The vehicle packs laid over the standard set at launch (ADR-0033). Empty for most vehicles.</summary>
    public IReadOnlyList<VehiclePack> ActivePacks => Loaded.ActivePacks;

    /// <summary>Every pack that ships, so Settings can say which one a newly decoded VIN would pick.</summary>
    public IReadOnlyList<VehiclePack> AvailablePacks => Loaded.AvailablePacks;

    /// <summary>Why a pack was left out, when one was. Never fatal.</summary>
    public string? PackProblem => Loaded.PackProblem;

    /// <summary>
    /// The catalog as it ships for this vehicle — the standard set plus any matching vehicle
    /// pack (ADR-0033) — before the user's overlay (ADR-0032).
    /// </summary>
    public SignalCatalog Shipped => Loaded.Shipped;

    /// <summary>
    /// Why the user's overlay was not applied at launch, when it was not.
    /// </summary>
    /// <remarks>
    /// A bad overlay is dropped whole and the shipped catalog runs alone. Half-applying it would
    /// leave the dash running a catalog nobody wrote; refusing to start over a hand-edited file
    /// would be worse. The Sensors section shows this so the drop is never silent.
    /// </remarks>
    public string? OverlayError => Loaded.OverlayError;

    /// <summary>What polling has learned about a signal, for the settings inventory.</summary>
    public SignalPollStatus StatusOf(string signalId) => _service.StatusOf(signalId);

    /// <summary>One question to the adapter outside the plan — a scan or a TEST (ADR-0032).</summary>
    public Task<PidResponse> ProbeAsync(PidRequest request, CancellationToken ct) =>
        _service.ProbeAsync(request, ct);

    /// <summary>Named-signal access. This is all the UI is allowed to know about.</summary>
    public IVehicleSignals Signals => _service.Bus;

    /// <summary>
    /// The synthetic truck, or null when a real adapter is driving the stack.
    /// </summary>
    /// <remarks>
    /// Nullable rather than always-present because the ground-truth figures and the
    /// unplug/replug diagnostics it backs only exist in simulation — a real truck has no
    /// "pretend the cable came out" button, and no exact fuel figure to compare against.
    /// </remarks>
    /// <remarks>
    /// Not fixed at launch any more: the dash starts on the simulator and moves to the truck when
    /// the adapter answers (ADR-0034), and from then on this is null.
    /// </remarks>
    public SyntheticTransport? Synthetic => _switchable.Current as SyntheticTransport;

    /// <summary>
    /// True when the numbers on screen come from the simulator rather than a vehicle.
    /// </summary>
    /// <remarks>
    /// The status strip's SIM badge binds to this. It used to be hard-coded, which was
    /// correct while the app could only ever be simulated — but a badge that says SIM over
    /// real data is worse than no badge at all, because it teaches you to ignore it.
    /// </remarks>
    public bool IsSimulated => Synthetic is not null;

    /// <summary>
    /// Why the real adapter is not being read, when one is chosen and has not answered — and
    /// that it is being watched for. Null when none is chosen, or once the dash is live.
    /// </summary>
    public string? FallbackReason => IsSimulated && _link is { } link && _failover is { IsWatching: true }
        ? $"{link.LastProblem ?? $"looking for the adapter on {link.PreferredPort}"} — switches to live when it answers"
        : null;

    /// <summary>What the stack is actually talking to, for the status strip.</summary>
    public string TransportDescription => _switchable.Description;

    /// <summary>
    /// Where the link to the adapter currently is, for the status strip to render.
    /// </summary>
    /// <remarks>
    /// Read through the transport interface, not the synthetic type, so the strip watches
    /// "the adapter" rather than "the simulator" -- the day a real OBDLink replaces the bottom
    /// layer (ADR-0003) this keeps answering without change. Disconnection is a state, not an
    /// error (see <see cref="DashDeck.Vehicle.TransportState"/>): the strip shows it, the
    /// pipeline recovers on its own, and nothing above here needs a restart (constraint C5).
    /// </remarks>
    public TransportState LinkState => _switchable.State;

    /// <summary>
    /// The loaded catalog, so the card editor can offer what actually exists.
    /// </summary>
    /// <remarks>
    /// Exposed for one purpose: a picker cannot offer signals it cannot enumerate, and
    /// <see cref="IVehicleSignals.KnownSignals"/> gives ids alone â€” no name, no unit, no
    /// sensible default rate, which makes for a list of <c>engine.mafRate</c> rather than
    /// one a person can read.
    /// <para>
    /// The UI still never sees a <c>SignalDefinition</c>. It is projected into
    /// <see cref="Dash.ValueChoice"/> at the boundary, which deliberately drops mode, PID,
    /// bus and the decode spec â€” components subscribe to named signals and know nothing
    /// about PIDs, and an editor that picks signals is held to the same line.
    /// </para>
    /// </remarks>
    public SignalCatalog Catalog => Loaded.Catalog;

    /// <summary>Which scripted drive is running.</summary>
    public string DriveName { get; }

    private ElmAdapter? _elm;

    /// <summary>
    /// How requests are going out (ADR-0049): whether the fast way is in use, and how many went
    /// each way. For Settings ▸ Vehicle, to confirm it on the truck.
    /// </summary>
    public string RequestPathText => _elm is not { } elm
        ? ""
        : elm.FastRequestsActive
            ? $"Fast requests in use: {elm.FastCount} fast, {elm.FallbackCount} asked again the slow way."
            : elm.FastRequests
                ? "Fast requests are on, but this adapter cannot do them; requests go the standard way."
                : "Fast requests are off; requests go the standard way.";

    /// <summary>Measured, not claimed â€” the number Q12 exists to replace with a real one.</summary>
    public double MeasuredRequestsPerSecond => _service.MeasuredRequestsPerSecond;

    /// <summary>
    /// Pull the adapter, as if the cable came out. Readings stop, and every value on screen
    /// should age into <see cref="SignalQuality.Stale"/> rather than freezing at its last
    /// number.
    /// </summary>
    public void Unplug() => Synthetic?.Unplug();

    /// <summary>Plug it back in. The dash must recover without a restart (constraint C5).</summary>
    public void Replug() => Synthetic?.Replug();

    /// <summary>
    /// Start the synthetic vehicle. No adapter and no truck are involved (ADR-0005), and
    /// every value it produces is flagged <see cref="SignalQuality.Simulated"/> so it can
    /// never be mistaken on screen for a real reading.
    /// </summary>
    /// <param name="driveName">Which scripted drive to run.</param>
    /// <param name="sources">The vehicle and the user's overlay; read once, at launch.</param>
    /// <param name="cancellationToken">Cancels start-up.</param>
    public static Task<VehicleStack> StartSyntheticAsync(
        string driveName,
        CatalogSources sources,
        CancellationToken cancellationToken) =>
        StartAsync(adapter: null, driveName, sources, cancellationToken);

    /// <summary>
    /// How long launch waits for the chosen adapter before coming up simulated and watching
    /// for it instead. Only the chosen port is tried in that time; the others are the watcher's.
    /// </summary>
    public static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Start the stack the way the app does: the chosen adapter if it answers, the synthetic
    /// truck otherwise — and, when an adapter is chosen but silent, keep watching for it.
    /// </summary>
    /// <remarks>
    /// Falling back rather than failing is deliberate (ADR-0031). Most of this app's life is
    /// spent on a desk with no vehicle attached (ADR-0005), and a dash that comes up dead there
    /// would be worse than one that comes up simulated and says so. The honesty is carried
    /// by <see cref="IsSimulated"/> and by every value's <see cref="SignalQuality"/>, not by
    /// refusing to run.
    /// <para>
    /// What changed (ADR-0034) is that falling back is no longer final. The tablet starts in the
    /// house and is carried to the truck; the bottom of the pipeline is a
    /// <see cref="SwitchableTransport"/>, and an <see cref="AdapterFailover"/> keeps looking —
    /// the chosen port, then the others for the same adapter — and moves the dash to it,
    /// without a restart, the moment it answers.
    /// </para>
    /// </remarks>
    /// <param name="adapter">Which adapter to use, or null for the simulator alone.</param>
    /// <param name="driveName">The scripted drive the simulator runs meanwhile.</param>
    /// <param name="sources">The vehicle and the user's overlay; read once, at launch.</param>
    /// <param name="cancellationToken">Cancels start-up.</param>
    public static async Task<VehicleStack> StartAsync(
        AdapterLinkOptions? adapter,
        string driveName,
        CatalogSources sources,
        CancellationToken cancellationToken,
        bool fastRequests = true)
    {
        var loaded = LoadCatalog(sources);
        var drive = Drives.ByName(driveName);
        var reserved = adapter?.ReservedPorts ?? [];

        AdapterLinkTransport? link = null;
        AdapterLocation? found = null;

        if (adapter is not null)
        {
            link = AdapterLinkTransport.ForSerialPorts(adapter);

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(StartupBudget);

            try
            {
                found = await link.TryLocateAsync(budget.Token, relocate: false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Out of start-up time. The watcher carries on from here.
            }
        }

        // The rate of the bus on pins 3 and 11, from the vehicle pack when the truck's has been
        // measured (Q21): sending at the wrong one makes error frames on that bus. The synthetic
        // truck is given the same, so the desk behaves like the cab.
        var pins311 = VehiclePacks.Pins311BitRate(loaded.ActivePacks) ?? 125000;

        IVehicleTransport bottom = found is not null
            ? link!
            : new SyntheticTransport(new SimulatedF150(drive)) { Pins311BitRate = pins311 };

        var switchable = new SwitchableTransport(bottom);

        // Standard values from the engine computer, asked so the adapter stops waiting once it has
        // the one answer (ADR-0049). Settings ▸ Vehicle can turn it off; it applies at launch.
        var elm = new ElmAdapter(switchable) { Pins311BitRate = pins311, FastRequests = fastRequests };
        var service = new VehicleService(elm, loaded.Catalog)
        {
            Quality = found is not null ? SignalQuality.Live : SignalQuality.Simulated,
        };

        await service.StartAsync(cancellationToken);

        var stack = new VehicleStack(service, switchable, drive.Name, loaded, link, reserved) { _elm = elm };

        if (found is not null)
        {
            stack.AdapterFound?.Invoke(found);
        }
        else if (link is not null)
        {
            stack.Watch();
        }

        return stack;
    }

    /// <summary>Start (or restart) looking for the adapter in the background.</summary>
    private void Watch()
    {
        var link = _link!;

        _failover ??= new AdapterFailover(
            _service,
            _switchable,
            async ct => await link.TryLocateAsync(ct).ConfigureAwait(false) is not null ? link : null);

        _failover.Start();
    }

    /// <summary>
    /// Use a different adapter port — from the tested-ports list in Settings ▸ Vehicle.
    /// </summary>
    /// <returns>
    /// Whether it applies now: while simulated, the new port is watched for at once and the dash
    /// goes live when it answers; once live on another port, it takes a restart.
    /// </returns>
    public AdapterChoice UseAdapterPort(string? port)
    {
        if (!IsSimulated)
        {
            return string.Equals(port, _link?.Current?.Port, StringComparison.OrdinalIgnoreCase)
                ? AdapterChoice.AlreadyConnected
                : AdapterChoice.NextLaunch;
        }

        if (!AdapterSelection.TryResolvePort(port, out var chosen))
        {
            _ = _failover?.StopAsync();
            return AdapterChoice.Simulator;
        }

        if (_link is null)
        {
            Adopt(AdapterLinkTransport.ForSerialPorts(new AdapterLinkOptions
            {
                PreferredPort = chosen,
                ReservedPorts = _reservedPorts,
            }));
        }
        else
        {
            _link.Prefer(chosen);
        }

        Watch();
        return AdapterChoice.Watching;
    }

    /// <summary>
    /// Put the catalog together: the standard set, then the vehicle packs the decoded VIN
    /// matches (ADR-0033), then the user's own overlay (ADR-0032).
    /// </summary>
    /// <remarks>
    /// Only the standard file is load-bearing. A pack that will not load, or will not combine
    /// with the standard set, is left out and reported; an overlay that will not combine is
    /// dropped and reported. Either way the dash starts.
    /// </remarks>
    internal static LoadedCatalog LoadCatalog(CatalogSources sources)
    {
        var standard = SignalCatalog.FromFile(FindCatalog());
        var (available, problems) = VehiclePacks.LoadFolder(CatalogPath.FindFolder("vehicles"));
        var active = VehiclePacks.Select(available, sources.Vehicle);
        var packProblem = problems.Count > 0 ? string.Join(" ", problems) : null;

        SignalCatalog shipped;

        try
        {
            shipped = VehiclePacks.Apply(standard, active);
        }
        catch (System.IO.InvalidDataException ex)
        {
            shipped = standard;
            active = [];
            packProblem = ex.Message;
        }

        var (catalog, overlayError) = ApplyOverlay(shipped, sources.UserSignals);
        return new LoadedCatalog(shipped, catalog, overlayError, active, available, packProblem);
    }

    /// <summary>The shipped catalog with the overlay laid over it, or alone and a reason.</summary>
    internal static (SignalCatalog Catalog, string? Error) ApplyOverlay(
        SignalCatalog shipped,
        IReadOnlyList<SignalDefinition> userSignals)
    {
        if (userSignals.Count == 0)
        {
            return (shipped, null);
        }

        try
        {
            return (SignalCatalog.Overlay(shipped, userSignals), null);
        }
        catch (System.IO.InvalidDataException ex)
        {
            return (shipped, ex.Message);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every await here is <c>ConfigureAwait(false)</c>. Without it, an await that did not finish
    /// at once tried to come back to the UI thread — which, on shutdown, is the thread blocked
    /// waiting for this to finish. The window closed and the process never did: DashDeck stayed
    /// in Task Manager, holding the adapter's serial port.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_failover is not null)
        {
            await _failover.DisposeAsync().ConfigureAwait(false);
        }

        await _service.DisposeAsync().ConfigureAwait(false);

        // The link is disposed with the pipeline when it is at the bottom of it; while it is only
        // being watched for, it is this stack's to close — the serial port must be free for the
        // next launch (App.RequestRestart).
        if (_link is not null && IsSimulated)
        {
            await _link.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Walk up from the binary looking for the signal catalog.
    /// </summary>
    /// <remarks>
    /// The catalog is data, not code (ADR-0004), so it is not embedded. A packaged build
    /// will ship it beside the executable, which this finds on the first iteration.
    /// </remarks>
    private static string FindCatalog() =>
        CatalogPath.Find("signals.obd2-standard.json")
        ?? throw new System.IO.FileNotFoundException(
            "Could not find catalog/signals.obd2-standard.json by walking up from the binary. " +
            "Run from inside the repository.");
}

/// <summary>What the catalog is built from at launch, besides the shipped files.</summary>
/// <param name="Vehicle">The decoded vehicle, which picks the signal pack (ADR-0033).</param>
/// <param name="UserSignals">The user's own overlay (ADR-0032).</param>
public sealed record CatalogSources(VehicleIdentity Vehicle, IReadOnlyList<SignalDefinition> UserSignals)
{
    /// <summary>No vehicle known and no overlay — the standard set alone.</summary>
    public static CatalogSources None { get; } = new(VehicleIdentity.Unknown, []);
}

/// <summary>How the running catalog was put together, and what was left out of it.</summary>
public sealed record LoadedCatalog(
    SignalCatalog Shipped,
    SignalCatalog Catalog,
    string? OverlayError,
    IReadOnlyList<VehiclePack> ActivePacks,
    IReadOnlyList<VehiclePack> AvailablePacks,
    string? PackProblem);

/// <summary>What choosing an adapter port did.</summary>
public enum AdapterChoice
{
    /// <summary>Simulated now; watching the port, and live when the adapter answers.</summary>
    Watching,

    /// <summary>That port is the one being read already.</summary>
    AlreadyConnected,

    /// <summary>Live on another port; the change applies at the next launch.</summary>
    NextLaunch,

    /// <summary>No adapter: the simulator, and nothing watched.</summary>
    Simulator,
}

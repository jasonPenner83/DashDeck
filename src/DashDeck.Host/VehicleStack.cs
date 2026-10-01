using DashDeck.Abstractions;
using DashDeck.Core;
using DashDeck.Core.Catalog;
using DashDeck.Core.Identity;
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
public sealed class VehicleStack : IAsyncDisposable, ViewModels.ISignalInventorySource
{
    private readonly VehicleService _service;
    private readonly IVehicleTransport _transport;

    private VehicleStack(
        VehicleService service,
        IVehicleTransport transport,
        SyntheticTransport? synthetic,
        string driveName,
        LoadedCatalog loaded,
        string? fallbackReason = null)
    {
        _service = service;
        _transport = transport;
        Synthetic = synthetic;
        DriveName = driveName;
        Loaded = loaded;
        FallbackReason = fallbackReason;
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
    public SyntheticTransport? Synthetic { get; }

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
    /// Why the real adapter was not used, when one was configured and did not come up.
    /// Null when nothing was configured, or when the adapter is working.
    /// </summary>
    public string? FallbackReason { get; }

    /// <summary>What the stack is actually talking to, for the status strip.</summary>
    public string TransportDescription => _transport.Description;

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
    public TransportState LinkState => _transport.State;

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
    public static async Task<VehicleStack> StartSyntheticAsync(
        string driveName,
        CatalogSources sources,
        CancellationToken cancellationToken)
    {
        var drive = Drives.ByName(driveName);
        var loaded = LoadCatalog(sources);
        var catalog = loaded.Catalog;
        var synthetic = new SyntheticTransport(new SimulatedF150(drive));

        var service = new VehicleService(new ElmAdapter(synthetic), catalog)
        {
            Quality = SignalQuality.Simulated,
        };

        await service.StartAsync(cancellationToken);
        return new VehicleStack(service, synthetic, synthetic, drive.Name, loaded);
    }

    /// <summary>
    /// Start against a real OBD-II adapter on a serial port.
    /// </summary>
    /// <remarks>
    /// The baud rate is negotiated rather than assumed: the OBDLink EX ships at 115200 but
    /// its STN chip reaches 2 Mbps and other software raises it, and opening at the wrong
    /// rate does not fail — it returns mojibake that reads as a broken adapter.
    /// <para>
    /// Everything above the transport is identical to the synthetic path. That is the
    /// property ADR-0003 was for, and this method existing at ten lines is the evidence it
    /// held.
    /// </para>
    /// </remarks>
    public static async Task<VehicleStack> StartLiveAsync(
        string portName,
        CatalogSources sources,
        CancellationToken cancellationToken)
    {
        var loaded = LoadCatalog(sources);
        var catalog = loaded.Catalog;

        var (baud, _) = await BaudNegotiator.FindAsync(
            rate => new SerialPortTransport(portName, rate) { ResponseTimeout = TimeSpan.FromSeconds(2) },
            ct: cancellationToken);

        var transport = new SerialPortTransport(portName, baud);
        var service = new VehicleService(new ElmAdapter(transport), catalog)
        {
            Quality = SignalQuality.Live,
        };

        await service.StartAsync(cancellationToken);
        return new VehicleStack(service, transport, synthetic: null, $"{portName} @ {baud}", loaded);
    }

    /// <summary>
    /// Start the stack the way the app does: the configured adapter if there is one, the
    /// synthetic truck otherwise.
    /// </summary>
    /// <remarks>
    /// Falling back rather than failing is deliberate. Most of this app's life is spent on
    /// a desk with no vehicle attached (ADR-0005), and a dash that comes up dead there
    /// would be worse than one that comes up simulated and says so. The honesty is carried
    /// by <see cref="IsSimulated"/> and by every value's <see cref="SignalQuality"/>, not by
    /// refusing to run.
    /// <para>
    /// A configured port that does not come up is reported in <see cref="FallbackReason"/>
    /// rather than swallowed: silently simulating when you expected real data is exactly
    /// the confusion the quality flags exist to prevent.
    /// </para>
    /// </remarks>
    public static async Task<VehicleStack> StartAsync(
        string? adapterPort,
        string driveName,
        CatalogSources sources,
        CancellationToken cancellationToken)
    {
        if (!AdapterSelection.TryResolvePort(adapterPort, out var port))
        {
            return await StartSyntheticAsync(driveName, sources, cancellationToken);
        }

        try
        {
            return await StartLiveAsync(port, sources, cancellationToken);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or TimeoutException)
        {
            var fallback = await StartSyntheticAsync(driveName, sources, cancellationToken);

            return new VehicleStack(
                fallback._service,
                fallback._transport,
                fallback.Synthetic,
                fallback.DriveName,
                fallback.Loaded,
                $"{port}: {ex.Message}");
        }
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
    public ValueTask DisposeAsync() => _service.DisposeAsync();

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

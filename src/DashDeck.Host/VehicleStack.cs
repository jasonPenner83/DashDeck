using DashDeck.Abstractions;
using DashDeck.Core;
using DashDeck.Core.Catalog;
using DashDeck.Simulator;
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

    private VehicleStack(
        VehicleService service,
        SyntheticTransport synthetic,
        string driveName,
        SignalCatalog shipped,
        SignalCatalog catalog,
        string? overlayError)
    {
        _service = service;
        Synthetic = synthetic;
        DriveName = driveName;
        Shipped = shipped;
        Catalog = catalog;
        OverlayError = overlayError;
    }

    /// <summary>The catalog as it ships, before the user's overlay (ADR-0030).</summary>
    public SignalCatalog Shipped { get; }

    /// <summary>
    /// Why the user's overlay was not applied at launch, when it was not.
    /// </summary>
    /// <remarks>
    /// A bad overlay is dropped whole and the shipped catalog runs alone. Half-applying it would
    /// leave the dash running a catalog nobody wrote; refusing to start over a hand-edited file
    /// would be worse. The Sensors section shows this so the drop is never silent.
    /// </remarks>
    public string? OverlayError { get; }

    /// <summary>What polling has learned about a signal, for the settings inventory.</summary>
    public SignalPollStatus StatusOf(string signalId) => _service.StatusOf(signalId);

    /// <summary>One question to the adapter outside the plan — a scan or a TEST (ADR-0030).</summary>
    public Task<DashDeck.Vehicle.PidResponse> ProbeAsync(DashDeck.Vehicle.PidRequest request, CancellationToken ct) =>
        _service.ProbeAsync(request, ct);

    /// <summary>Named-signal access. This is all the UI is allowed to know about.</summary>
    public IVehicleSignals Signals => _service.Bus;

    /// <summary>The synthetic truck, for the ground-truth figures the status strip shows.</summary>
    public SyntheticTransport Synthetic { get; }

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
    public DashDeck.Vehicle.TransportState LinkState => Synthetic.State;

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
    public SignalCatalog Catalog { get; }

    /// <summary>Which scripted drive is running.</summary>
    public string DriveName { get; }

    /// <summary>Measured, not claimed â€” the number Q12 exists to replace with a real one.</summary>
    public double MeasuredRequestsPerSecond => _service.MeasuredRequestsPerSecond;

    /// <summary>
    /// Pull the adapter, as if the cable came out. Readings stop, and every value on screen
    /// should age into <see cref="SignalQuality.Stale"/> rather than freezing at its last
    /// number.
    /// </summary>
    public void Unplug() => Synthetic.Unplug();

    /// <summary>Plug it back in. The dash must recover without a restart (constraint C5).</summary>
    public void Replug() => Synthetic.Replug();

    /// <summary>
    /// Start the synthetic vehicle. No adapter and no truck are involved (ADR-0005), and
    /// every value it produces is flagged <see cref="SignalQuality.Simulated"/> so it can
    /// never be mistaken on screen for a real reading.
    /// </summary>
    /// <param name="driveName">Which scripted drive to run.</param>
    /// <param name="userSignals">The user's overlay (ADR-0030); read once, here, at launch.</param>
    /// <param name="cancellationToken">Cancels start-up.</param>
    public static async Task<VehicleStack> StartSyntheticAsync(
        string driveName,
        IReadOnlyList<SignalDefinition> userSignals,
        CancellationToken cancellationToken)
    {
        var drive = Drives.ByName(driveName);
        var shipped = SignalCatalog.FromFile(FindCatalog());
        var (catalog, overlayError) = ApplyOverlay(shipped, userSignals);
        var synthetic = new SyntheticTransport(new SimulatedF150(drive));

        var service = new VehicleService(new ElmAdapter(synthetic), catalog)
        {
            Quality = SignalQuality.Simulated,
        };

        await service.StartAsync(cancellationToken);
        return new VehicleStack(service, synthetic, drive.Name, shipped, catalog, overlayError);
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


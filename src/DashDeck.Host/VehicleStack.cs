using DashDeck.Abstractions;
using DashDeck.Core;
using DashDeck.Core.Catalog;
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
public sealed class VehicleStack : IAsyncDisposable
{
    private readonly VehicleService _service;
    private readonly IVehicleTransport _transport;

    private VehicleStack(
        VehicleService service,
        IVehicleTransport transport,
        SyntheticTransport? synthetic,
        string driveName,
        SignalCatalog catalog,
        string? fallbackReason = null)
    {
        _service = service;
        _transport = transport;
        Synthetic = synthetic;
        DriveName = driveName;
        Catalog = catalog;
        FallbackReason = fallbackReason;
    }

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
    public void Unplug() => Synthetic?.Unplug();

    /// <summary>Plug it back in. The dash must recover without a restart (constraint C5).</summary>
    public void Replug() => Synthetic?.Replug();

    /// <summary>
    /// Start the synthetic vehicle. No adapter and no truck are involved (ADR-0005), and
    /// every value it produces is flagged <see cref="SignalQuality.Simulated"/> so it can
    /// never be mistaken on screen for a real reading.
    /// </summary>
    public static async Task<VehicleStack> StartSyntheticAsync(
        string driveName,
        CancellationToken cancellationToken)
    {
        var drive = Drives.ByName(driveName);
        var catalog = SignalCatalog.FromFile(FindCatalog());
        var synthetic = new SyntheticTransport(new SimulatedF150(drive));

        var service = new VehicleService(new ElmAdapter(synthetic), catalog)
        {
            Quality = SignalQuality.Simulated,
        };

        await service.StartAsync(cancellationToken);
        return new VehicleStack(service, synthetic, synthetic, drive.Name, catalog);
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
        CancellationToken cancellationToken)
    {
        var catalog = SignalCatalog.FromFile(FindCatalog());

        var (baud, _) = await BaudNegotiator.FindAsync(
            rate => new SerialPortTransport(portName, rate) { ResponseTimeout = TimeSpan.FromSeconds(2) },
            ct: cancellationToken);

        var transport = new SerialPortTransport(portName, baud);
        var service = new VehicleService(new ElmAdapter(transport), catalog)
        {
            Quality = SignalQuality.Live,
        };

        await service.StartAsync(cancellationToken);
        return new VehicleStack(service, transport, synthetic: null, $"{portName} @ {baud}", catalog);
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
        CancellationToken cancellationToken)
    {
        if (!AdapterSelection.TryResolvePort(adapterPort, out var port))
        {
            return await StartSyntheticAsync(driveName, cancellationToken);
        }

        try
        {
            return await StartLiveAsync(port, cancellationToken);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or TimeoutException)
        {
            var fallback = await StartSyntheticAsync(driveName, cancellationToken);

            return new VehicleStack(
                fallback._service,
                fallback._transport,
                fallback.Synthetic,
                fallback.DriveName,
                fallback.Catalog,
                $"{port}: {ex.Message}");
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


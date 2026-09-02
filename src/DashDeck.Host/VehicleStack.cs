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
/// Deliberately the same pipeline the debug console drives — synthetic transport, ELM
/// adapter, catalog, arbiter, state bus — with nothing shell-specific in it. The shell is
/// just another consumer of the state bus, which is the property that lets a real adapter
/// replace the bottom layer later without anything above it changing (ADR-0003).
/// </remarks>
public sealed class VehicleStack : IAsyncDisposable
{
    private readonly VehicleService _service;

    private VehicleStack(VehicleService service, SyntheticTransport synthetic, string driveName, SignalCatalog catalog)
    {
        _service = service;
        Synthetic = synthetic;
        DriveName = driveName;
        Catalog = catalog;
    }

    /// <summary>Named-signal access. This is all the UI is allowed to know about.</summary>
    public IVehicleSignals Signals => _service.Bus;

    /// <summary>The synthetic truck, for the ground-truth figures the status strip shows.</summary>
    public SyntheticTransport Synthetic { get; }

    /// <summary>
    /// The loaded catalog, so the card editor can offer what actually exists.
    /// </summary>
    /// <remarks>
    /// Exposed for one purpose: a picker cannot offer signals it cannot enumerate, and
    /// <see cref="IVehicleSignals.KnownSignals"/> gives ids alone — no name, no unit, no
    /// sensible default rate, which makes for a list of <c>engine.mafRate</c> rather than
    /// one a person can read.
    /// <para>
    /// The UI still never sees a <c>SignalDefinition</c>. It is projected into
    /// <see cref="Dash.SignalChoice"/> at the boundary, which deliberately drops mode, PID,
    /// bus and the decode spec — components subscribe to named signals and know nothing
    /// about PIDs, and an editor that picks signals is held to the same line.
    /// </para>
    /// </remarks>
    public SignalCatalog Catalog { get; }

    /// <summary>Which scripted drive is running.</summary>
    public string DriveName { get; }

    /// <summary>Measured, not claimed — the number Q12 exists to replace with a real one.</summary>
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
        return new VehicleStack(service, synthetic, drive.Name, catalog);
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

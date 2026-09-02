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

    private VehicleStack(VehicleService service, SyntheticTransport synthetic, string driveName)
    {
        _service = service;
        Synthetic = synthetic;
        DriveName = driveName;
    }

    /// <summary>Named-signal access. This is all the UI is allowed to know about.</summary>
    public IVehicleSignals Signals => _service.Bus;

    /// <summary>The synthetic truck, for the ground-truth figures the status strip shows.</summary>
    public SyntheticTransport Synthetic { get; }

    /// <summary>Which scripted drive is running.</summary>
    public string DriveName { get; }

    /// <summary>Measured, not claimed — the number Q12 exists to replace with a real one.</summary>
    public double MeasuredRequestsPerSecond => _service.MeasuredRequestsPerSecond;

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
        return new VehicleStack(service, synthetic, drive.Name);
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
    private static string FindCatalog()
    {
        var dir = AppContext.BaseDirectory;

        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = IoPath.Combine(dir, "catalog", "signals.obd2-standard.json");
            if (IoFile.Exists(candidate))
            {
                return candidate;
            }

            dir = IoPath.GetDirectoryName(dir.TrimEnd(IoPath.DirectorySeparatorChar));
        }

        throw new System.IO.FileNotFoundException(
            "Could not find catalog/signals.obd2-standard.json by walking up from the binary. " +
            "Run from inside the repository.");
    }
}

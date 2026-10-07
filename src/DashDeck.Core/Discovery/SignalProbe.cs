using DashDeck.Core.Catalog;
using DashDeck.Vehicle;

namespace DashDeck.Core.Discovery;

/// <summary>
/// One reading of a catalog signal, asked by name — for tools that need speed or rpm beside what
/// they are doing, without knowing where either comes from (ADR-0052).
/// </summary>
/// <remarks>
/// The sweeps refuse to run while moving and the watch records rpm beside each pass. Both used to
/// ask mode 01 PIDs <c>0D</c> and <c>0C</c> by number; now the catalog says where each lives and
/// how it decodes, so a vehicle file or overlay that moves one moves it for the tools too.
/// </remarks>
public static class SignalProbe
{
    /// <summary>Vehicle speed, km/h.</summary>
    public const string Speed = "vehicle.speed";

    /// <summary>Engine speed, rpm.</summary>
    public const string Rpm = "engine.rpm";

    /// <summary>Engine coolant temperature, °C.</summary>
    public const string Coolant = "engine.coolantTemp";

    /// <summary>The signal's value now, or null when the catalog lacks it, it is a placeholder, or nothing answered.</summary>
    public static async Task<double?> ReadAsync(
        SignalCatalog catalog,
        string signalId,
        Func<PidRequest, CancellationToken, Task<PidResponse>> ask,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(ask);

        if (!catalog.TryGet(signalId, out var definition) || definition.Placeholder)
        {
            return null;
        }

        var spec = definition.ToRequest();
        var reply = await ask(new PidRequest(spec.Mode, spec.Pid, spec.Bus, spec.Module), ct).ConfigureAwait(false);
        return reply.IsSuccess ? definition.Decode.Decode(reply.Data) : null;
    }
}

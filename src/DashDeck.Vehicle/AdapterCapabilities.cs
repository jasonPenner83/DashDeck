using DashDeck.Abstractions;

namespace DashDeck.Vehicle;

/// <summary>
/// What the connected adapter can actually do. How the rest of the system discovers the
/// shape of the hardware instead of assuming it.
/// </summary>
/// <param name="Name">Adapter identification string, e.g. from ATI.</param>
/// <param name="Buses">Buses reachable at all.</param>
/// <param name="SimultaneousBusAccess">
/// True when the adapter switches buses electronically. A toggle-switch adapter is false
/// here, and a large part of the signal catalog is then permanently unreachable —
/// which is exactly why the OBDLink EX was chosen (ADR-0007).
/// </param>
/// <param name="MaxRequestsPerSecond">
/// Measured or conservatively assumed ceiling for the whole app. The arbiter divides this
/// among every subscriber. This is not a guess to be optimistic with: the synthetic
/// vehicle deliberately reports the pessimistic Bluetooth-era figure until a real one is
/// measured on the truck, because a simulator with too much headroom produces components
/// that fail on first contact.
/// </param>
public sealed record AdapterCapabilities(
    string Name,
    IReadOnlySet<CanBus> Buses,
    bool SimultaneousBusAccess,
    double MaxRequestsPerSecond)
{
    public bool Supports(CanBus bus) => Buses.Contains(bus);
}

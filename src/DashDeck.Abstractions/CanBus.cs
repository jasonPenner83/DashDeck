namespace DashDeck.Abstractions;

/// <summary>
/// The vehicle networks reachable from the OBD-II connector on a 2019 F-150.
/// </summary>
/// <remarks>
/// Every signal declares the bus it lives on. Powertrain data is on HS-CAN; body,
/// comfort and TPMS data is on MS-CAN. An adapter without electronic bus switching can
/// only see one at a time, which is why <see cref="CanBus"/> is part of the contract
/// rather than an implementation detail — see ADR-0007.
/// </remarks>
public enum CanBus
{
    /// <summary>High-speed CAN, 500 kbps, OBD-II pins 6 and 14. Powertrain and chassis.</summary>
    Hs,

    /// <summary>Medium-speed CAN, 125 kbps, OBD-II pins 3 and 11. Body, comfort, TPMS.</summary>
    Ms,
}

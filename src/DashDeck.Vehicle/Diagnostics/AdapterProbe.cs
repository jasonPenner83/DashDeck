using DashDeck.Abstractions;

namespace DashDeck.Vehicle.Diagnostics;

/// <summary>Whether a vehicle was found on the other side of the adapter.</summary>
public enum VehiclePresence
{
    /// <summary>Could not tell.</summary>
    Unknown,

    /// <summary>The adapter is powered and talking, but no vehicle bus answered.</summary>
    NotDetected,

    /// <summary>A vehicle answered an OBD-II request.</summary>
    Present,
}

/// <summary>One command sent during a probe, and what came back.</summary>
/// <remarks>
/// Every exchange is kept verbatim. When a bring-up goes wrong in a cold truck, the raw
/// transcript is the only thing that explains why, and it can be read by someone who was
/// not there.
/// </remarks>
public sealed record ProbeStep(string Command, string Response, bool Ok, string Note);

/// <summary>What the adapter is and what it can do, independent of any vehicle.</summary>
public sealed record AdapterProbeReport
{
    public required string PortName { get; init; }

    public required int BaudRate { get; init; }

    /// <summary>Reply to <c>ATI</c>, e.g. <c>ELM327 v1.5</c> or an STN identifier.</summary>
    public string? Identity { get; init; }

    /// <summary>Reply to <c>AT@1</c> — the manufacturer's device description.</summary>
    public string? DeviceDescription { get; init; }

    /// <summary>Reply to <c>STI</c> — present only on genuine STN-based adapters.</summary>
    public string? StnFirmware { get; init; }

    /// <summary>
    /// True when the extended <c>ST</c> command set answered.
    /// </summary>
    /// <remarks>
    /// This is the check that distinguishes a real OBDLink from an ELM327 clone. A clone
    /// answers <c>?</c> to <c>STI</c>, and crucially cannot switch to MS-CAN — which would
    /// make a large part of the signal catalog permanently unreachable (ADR-0007).
    /// </remarks>
    public bool SupportsStCommands { get; init; }

    /// <summary>True when the adapter accepted the MS-CAN bus-switch command.</summary>
    public bool MsCanSwitchAccepted { get; init; }

    /// <summary>
    /// Reply to <c>ATRV</c> — voltage measured at OBD-II pin 16.
    /// </summary>
    /// <remarks>
    /// Vehicle power, not USB power, so it distinguishes three situations that otherwise
    /// look alike: near zero means the adapter is not in a vehicle at all; around 12 V
    /// means plugged in with the ignition off; around 14 V means the engine is running and
    /// the alternator is charging.
    /// </remarks>
    public string? ObdVoltage { get; init; }

    public VehiclePresence Vehicle { get; init; }

    public required IReadOnlyList<ProbeStep> Steps { get; init; }

    /// <summary>True when the adapter itself is healthy, regardless of whether a truck is attached.</summary>
    public bool AdapterHealthy => Identity is not null;
}

/// <summary>
/// Interrogates the adapter before any vehicle data is attempted.
/// </summary>
/// <remarks>
/// Deliberately split from vehicle work so it can run on a desk with the adapter on USB
/// and nothing else. That is the right first test: it clears the driver, the COM port, the
/// baud rate, the command protocol and the adapter's identity in a warm room, leaving only
/// genuinely vehicle-side unknowns for the truck.
/// </remarks>
public static class AdapterProbe
{
    private const string MsCanCommand = "STP53";
    private const string HsCanCommand = "STP33";

    public static async Task<AdapterProbeReport> RunAsync(
        IVehicleTransport transport,
        string portName,
        int baudRate,
        CancellationToken ct)
    {
        var steps = new List<ProbeStep>();

        if (transport.State != TransportState.Connected)
        {
            await transport.ConnectAsync(ct).ConfigureAwait(false);
        }

        async Task<string> SendAsync(string command, string note)
        {
            string response;
            bool ok;

            try
            {
                response = await transport.ExchangeAsync(command, ct).ConfigureAwait(false);
                ok = response.Length > 0 && !IsRejected(response);
            }
            catch (IOException ex)
            {
                response = $"<{ex.GetType().Name}: {ex.Message}>";
                ok = false;
            }

            steps.Add(new ProbeStep(command, Tidy(response), ok, note));
            return response;
        }

        await SendAsync("ATZ", "reset the interpreter").ConfigureAwait(false);
        await SendAsync("ATE0", "echo off").ConfigureAwait(false);
        await SendAsync("ATL0", "linefeeds off").ConfigureAwait(false);
        await SendAsync("ATS0", "spaces off").ConfigureAwait(false);
        await SendAsync("ATH0", "headers off").ConfigureAwait(false);

        var identity = Tidy(await SendAsync("ATI", "adapter identity").ConfigureAwait(false));
        var description = Tidy(await SendAsync("AT@1", "device description").ConfigureAwait(false));
        var voltage = Tidy(await SendAsync("ATRV", "voltage at the OBD connector").ConfigureAwait(false));

        var stn = Tidy(await SendAsync("STI", "STN firmware — absent on ELM327 clones").ConfigureAwait(false));
        var supportsSt = stn.Length > 0 && !IsRejected(stn);

        var msCan = Tidy(await SendAsync(MsCanCommand, "switch to MS-CAN (pins 3/11)").ConfigureAwait(false));
        var msCanOk = supportsSt && !IsRejected(msCan);

        await SendAsync(HsCanCommand, "switch back to HS-CAN (pins 6/14)").ConfigureAwait(false);
        await SendAsync("ATSP6", "protocol ISO 15765-4, 11-bit, 500 kbps").ConfigureAwait(false);

        // Only now ask the vehicle for something. With no truck attached this is expected
        // to fail, and that failure is a result rather than an error.
        var probe = await SendAsync("0100", "ask the vehicle which PIDs it supports").ConfigureAwait(false);

        return new AdapterProbeReport
        {
            PortName = portName,
            BaudRate = baudRate,
            Identity = identity.Length > 0 && !IsRejected(identity) ? identity : null,
            DeviceDescription = description.Length > 0 && !IsRejected(description) ? description : null,
            StnFirmware = supportsSt ? stn : null,
            SupportsStCommands = supportsSt,
            MsCanSwitchAccepted = msCanOk,
            ObdVoltage = voltage.Length > 0 && !IsRejected(voltage) ? voltage : null,
            Vehicle = ClassifyPresence(probe),
            Steps = steps,
        };
    }

    /// <summary>
    /// Decide whether a truck is on the other end.
    /// </summary>
    /// <remarks>
    /// <c>UNABLE TO CONNECT</c> means the adapter looked for a bus and found none — the
    /// normal answer on a bench. <c>NO DATA</c> means a bus was found but nothing replied,
    /// which on a vehicle usually means ignition off.
    /// </remarks>
    private static VehiclePresence ClassifyPresence(string response)
    {
        var upper = response.ToUpperInvariant();

        if (upper.Contains("UNABLE TO CONNECT", StringComparison.Ordinal) ||
            upper.Contains("BUS INIT", StringComparison.Ordinal) ||
            upper.Contains("CAN ERROR", StringComparison.Ordinal))
        {
            return VehiclePresence.NotDetected;
        }

        if (upper.Contains("4100", StringComparison.Ordinal) ||
            upper.Contains("41 00", StringComparison.Ordinal))
        {
            return VehiclePresence.Present;
        }

        return VehiclePresence.Unknown;
    }

    private static bool IsRejected(string response)
    {
        var upper = response.ToUpperInvariant();
        return upper.Contains('?', StringComparison.Ordinal) ||
               upper.Contains("ERROR", StringComparison.Ordinal);
    }

    private static string Tidy(string raw) => raw
        .Replace(">", string.Empty, StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Trim();
}

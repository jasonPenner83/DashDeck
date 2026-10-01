using System.Globalization;
using System.Text;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Vehicle.Diagnostics;

namespace DashDeck.BringUp;

/// <summary>Measured round-trip timing for one PID, hammered repeatedly.</summary>
public sealed record ThroughputResult(int Requests, int Failures, double MeanMs, double MinMs, double P95Ms)
{
    /// <summary>
    /// The sustainable request ceiling implied by mean service time.
    /// </summary>
    /// <remarks>
    /// Derived from service time, not from achieved requests per second. Achieved rate
    /// cannot tell "the adapter could not go faster" from "nothing needed polling", and
    /// using it as a capability measure produces a feedback death spiral — a mistake this
    /// project has already made once and fixed.
    /// </remarks>
    public double CeilingHz => MeanMs > 0 ? 1000.0 / MeanMs : 0;
}

/// <summary>Everything a bring-up run learned, in one object.</summary>
public sealed record BringUpResult
{
    public required DateTimeOffset StartedUtc { get; init; }

    public required AdapterProbeReport Adapter { get; init; }

    public PidSupportReport? HsSupport { get; init; }

    public PidSupportReport? MsSupport { get; init; }

    public ThroughputResult? Throughput { get; init; }

    public IReadOnlyList<(string SignalId, bool Supported, string Note)> CatalogCoverage { get; init; } = [];

    public IReadOnlyList<(string SignalId, double Value, string Unit)> LiveSample { get; init; } = [];

    public string? CapturePath { get; init; }
}

/// <summary>
/// Renders a bring-up run as markdown, to be committed next to the capture.
/// </summary>
/// <remarks>
/// The point is that someone who was not in the truck can read what happened. The raw
/// command transcript is included for exactly that reason: when a bring-up goes sideways in
/// a cold cab, the transcript is the only thing that explains why.
/// </remarks>
public static class BringUpReportWriter
{
    public static string ToMarkdown(BringUpResult result, SignalCatalog catalog)
    {
        var sb = new StringBuilder();
        var a = result.Adapter;

        sb.AppendLine(CultureInfo.InvariantCulture, $"# DashDeck bring-up — {result.StartedUtc:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();

        sb.AppendLine("## Adapter");
        sb.AppendLine();
        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Port | `{a.PortName}` @ {a.BaudRate} baud |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Identity | {a.Identity ?? "**no answer**"} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Device | {a.DeviceDescription ?? "—"} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| STN firmware | {a.StnFirmware ?? "**not an STN adapter**"} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| ST command set | {YesNo(a.SupportsStCommands)} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| MS-CAN switch | {YesNo(a.MsCanSwitchAccepted)} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| OBD pin-16 voltage | {a.ObdVoltage ?? "—"} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Vehicle | {a.Vehicle} |");
        sb.AppendLine();

        if (!a.SupportsStCommands)
        {
            sb.AppendLine("> **The ST command set did not answer.** On a genuine OBDLink it should. " +
                          "Without it the adapter cannot switch to MS-CAN, which makes TPMS, door " +
                          "state and drivetrain mode permanently unreachable (ADR-0007).");
            sb.AppendLine();
        }

        if (a.Vehicle == VehiclePresence.NotDetected)
        {
            sb.AppendLine("> No vehicle bus found. Expected when the adapter is on a desk; if this " +
                          "appears in the truck, check the OBD-II connector is seated and the " +
                          "ignition is on.");
            sb.AppendLine();
        }

        if (result.HsSupport is { AnyResponse: true } hs)
        {
            sb.AppendLine("## PIDs the vehicle supports (HS-CAN)");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Ranges probed: {string.Join(", ", hs.RangesProbed.Select(r => $"0x{r:X2}"))}. " +
                $"{hs.DataPids.Count} data PIDs supported.");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(string.Join(" ", hs.DataPids.Select(p => $"0x{p:X2}")));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (result.CatalogCoverage.Count > 0)
        {
            sb.AppendLine("## Catalog coverage");
            sb.AppendLine();
            sb.AppendLine("Which signal definitions this truck can actually feed.");
            sb.AppendLine();
            sb.AppendLine("| Signal | Supported | Note |");
            sb.AppendLine("|---|---|---|");

            foreach (var (id, supported, note) in result.CatalogCoverage)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| `{id}` | {YesNo(supported)} | {note} |");
            }

            sb.AppendLine();
        }

        if (result.Throughput is { } t)
        {
            sb.AppendLine("## Measured throughput");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{t.Requests} requests, {t.Failures} failed. " +
                $"Mean {t.MeanMs:0.#} ms, min {t.MinMs:0.#} ms, p95 {t.P95Ms:0.#} ms.");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"**Sustainable ceiling: {t.CeilingHz:0.#} requests/second**, shared by every component.");
            sb.AppendLine();
            sb.AppendLine("This is the answer to open question Q12. Set `AssumedRequestsPerSecond` " +
                          "and the simulator's latency from it, and decide whether live gauges are viable.");
            sb.AppendLine();
        }

        if (result.LiveSample.Count > 0)
        {
            sb.AppendLine("## Live sample");
            sb.AppendLine();

            foreach (var (id, value, unit) in result.LiveSample)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- `{id}` = {value:0.##} {unit}");
            }

            sb.AppendLine();
        }

        if (result.CapturePath is not null)
        {
            sb.AppendLine("## Capture");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Replayable session recorded to `{result.CapturePath}`. Replay it with:");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"dotnet run --project src/DashDeck.DebugConsole -- --replay {result.CapturePath}");
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("## Transcript");
        sb.AppendLine();
        sb.AppendLine("Every command and its raw reply, so this run can be diagnosed by someone who was not there.");
        sb.AppendLine();
        sb.AppendLine("| # | Command | Reply | | Why |");
        sb.AppendLine("|---|---|---|---|---|");

        for (var i = 0; i < a.Steps.Count; i++)
        {
            var step = a.Steps[i];
            var reply = step.Response.Length > 60 ? step.Response[..60] + "…" : step.Response;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {i + 1} | `{step.Command}` | `{reply}` | {(step.Ok ? "ok" : "—")} | {step.Note} |");
        }

        sb.AppendLine();
        _ = catalog;
        return sb.ToString();
    }

    private static string YesNo(bool value) => value ? "yes" : "**no**";
}

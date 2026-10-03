using System.Runtime.CompilerServices;
using DashDeck.Abstractions;

namespace DashDeck.Vehicle.Monitor;

/// <summary>
/// Listens to a bus through the adapter's monitor mode, silently (ADR-0044).
/// </summary>
/// <remarks>
/// <para>
/// <b>Silent is the point.</b> <c>ATCSM1</c> keeps the adapter from acknowledging what it hears, so
/// listening puts nothing on the bus at all — not even the acknowledge bit every other node sends.
/// Everything else here is an adapter setting: headers on so each frame carries its identifier,
/// CAN formatting off so all eight bytes are shown raw, and a receive filter when only one
/// identifier is wanted.
/// </para>
/// <para>
/// These settings are not the ones requests need. Whoever owns an <see cref="Elm.ElmAdapter"/> on
/// the same transport must call its <c>InitializeAsync</c> again after listening.
/// </para>
/// </remarks>
public sealed class CanMonitor
{
    private readonly IStreamingTransport _transport;
    private readonly IClock _clock;

    public CanMonitor(IStreamingTransport transport, IClock? clock = null)
    {
        _transport = transport;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>Lines read in the last listen that were not frames — status, prompts, noise.</summary>
    public int Unreadable { get; private set; }

    /// <summary>How many times the last listen was told frames were lost.</summary>
    public int Overflows { get; private set; }

    /// <summary>Frames read in the last listen.</summary>
    public int Frames { get; private set; }

    /// <summary>When the last listen began hearing — what each frame's <see cref="CanFrame.At"/> counts from.</summary>
    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>Why the last listen ended early, or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>How many times the last listen was restarted after the adapter's buffer filled.</summary>
    public int Restarts { get; private set; }

    /// <summary>
    /// Listen to <paramref name="bus"/> until <paramref name="ct"/> is cancelled, yielding each
    /// frame. With <paramref name="onlyId"/>, the adapter passes that identifier alone, which keeps
    /// a busy bus within what the serial link can carry. <paramref name="pins311BitRate"/> sets the
    /// rate of the bus on pins 3 and 11 when it is not the 125 kbit/s the adapter assumes.
    /// </summary>
    /// <remarks>
    /// A busy bus outruns the serial link: the adapter fills its buffer, says so, and stops. It is
    /// started again straight away, so the listen becomes a run of bursts — each a fair sample of
    /// what the bus is saying — rather than ending after the first.
    /// </remarks>
    public async IAsyncEnumerable<CanFrame> ListenAsync(
        CanBus bus,
        uint? onlyId,
        [EnumeratorCancellation] CancellationToken ct,
        int? pins311BitRate = null)
    {
        Unreadable = 0;
        Overflows = 0;
        Frames = 0;
        Restarts = 0;
        Problem = null;

        var setup = new List<string> { bus == CanBus.Ms ? "STP53" : "STP33" };
        if (bus == CanBus.Ms && pins311BitRate is { } rate and not 125000)
        {
            setup.Add($"STPBR{rate}");
        }

        setup.AddRange(
        [
            "ATCSM1",   // silent: acknowledge nothing
            "ATH1",     // headers on
            "ATS0",     // spaces off: a third fewer characters a frame over the serial link
            "ATCAF0",   // CAN formatting off: every byte, raw
            "ATD0",     // no length byte
            onlyId is { } id ? (id > 0x7FF ? $"ATCRA{id:X8}" : $"ATCRA{id:X3}") : "ATAR",
        ]);

        foreach (var command in setup)
        {
            var reply = await _transport.ExchangeAsync(command, ct).ConfigureAwait(false);
            if (reply.Contains('?', StringComparison.Ordinal) && command != "ATD0")
            {
                Problem = $"The adapter refused {command}.";
                yield break;
            }
        }

        var start = _clock.UtcNow;
        StartedAt = start;
        var monitor = "STMA";
        var first = true;

        while (!ct.IsCancellationRequested)
        {
            var notStn = false;
            var overflowed = false;

            await foreach (var line in _transport.StreamAsync(monitor, ct).ConfigureAwait(false))
            {
                if (first && line.Trim() == "?" && monitor == "STMA")
                {
                    // Not an STN: the plain ELM monitor does the same, more slowly.
                    notStn = true;
                    break;
                }

                first = false;

                if (MonitorLine.TryParse(line, out var frameId, out var data))
                {
                    Frames++;
                    yield return new CanFrame(_clock.UtcNow - start, bus, frameId, data);
                }
                else if (MonitorLine.IsOverflow(line))
                {
                    Overflows++;
                    overflowed = true;
                }
                else
                {
                    Unreadable++;
                }
            }

            if (notStn)
            {
                monitor = "ATMA";
                first = false;
                continue;
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (overflowed)
            {
                Restarts++;
                continue;
            }

            Problem = "The adapter stopped listening by itself.";
            break;
        }
    }

    /// <summary>
    /// Put the adapter's display settings back the way requests expect. A full
    /// <c>InitializeAsync</c> on the adapter does this and more; this is for a caller without one.
    /// </summary>
    public async Task RestoreAsync(CancellationToken ct)
    {
        foreach (var command in (string[])["ATAR", "ATCAF1", "ATH0", "ATS0", "STP33"])
        {
            await _transport.ExchangeAsync(command, ct).ConfigureAwait(false);
        }
    }
}

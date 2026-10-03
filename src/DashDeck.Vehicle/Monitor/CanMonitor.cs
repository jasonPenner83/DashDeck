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

    /// <summary>
    /// Listen to <paramref name="bus"/> until <paramref name="ct"/> is cancelled, yielding each
    /// frame. With <paramref name="onlyId"/>, the adapter passes that identifier alone, which keeps
    /// a busy bus within what the serial link can carry.
    /// </summary>
    public async IAsyncEnumerable<CanFrame> ListenAsync(CanBus bus, uint? onlyId, [EnumeratorCancellation] CancellationToken ct)
    {
        Unreadable = 0;
        Overflows = 0;
        Frames = 0;
        Problem = null;

        string[] setup =
        [
            bus == CanBus.Ms ? "STP53" : "STP33",
            "ATCSM1",   // silent: acknowledge nothing
            "ATH1",     // headers on
            "ATS1",     // spaces on
            "ATCAF0",   // CAN formatting off: every byte, raw
            "ATD0",     // no length byte
            onlyId is { } id ? (id > 0x7FF ? $"ATCRA{id:X8}" : $"ATCRA{id:X3}") : "ATAR",
        ];

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
        var command2 = "STMA";
        var first = true;

        while (true)
        {
            var retry = false;

            await foreach (var line in _transport.StreamAsync(command2, ct).ConfigureAwait(false))
            {
                if (first && line.Trim() == "?" && command2 == "STMA")
                {
                    // Not an STN: the plain ELM monitor does the same, more slowly.
                    retry = true;
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
                }
                else
                {
                    Unreadable++;
                }
            }

            if (!retry)
            {
                break;
            }

            command2 = "ATMA";
            first = false;
        }

        if (!ct.IsCancellationRequested && Problem is null)
        {
            Problem = Overflows > 0
                ? "The adapter stopped listening: its buffer filled. Listen to fewer identifiers."
                : "The adapter stopped listening by itself.";
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

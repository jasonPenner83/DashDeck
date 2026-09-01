using System.Text.Json;
using DashDeck.Abstractions;

namespace DashDeck.Vehicle.Recording;

/// <summary>One request/response exchange, as it happened.</summary>
public sealed record RecordedExchange(double OffsetMs, string Command, string Response);

/// <summary>
/// Wraps a transport and writes every exchange to a JSONL file.
/// </summary>
/// <remarks>
/// Always available, not a debug-only feature. A five-minute drive that reproduces a
/// problem is worth more than any amount of interactive debugging, particularly when the
/// hardware in question is a truck that has to be driven somewhere to reproduce anything.
/// </remarks>
public sealed class RecordingTransport : IVehicleTransport
{
    private readonly IVehicleTransport _inner;
    private readonly IClock _clock;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private DateTimeOffset? _start;
    private int _sinceFlush;

    private const int FlushInterval = 64;

    public RecordingTransport(IVehicleTransport inner, string path, IClock? clock = null)
    {
        _inner = inner;
        _clock = clock ?? SystemClock.Instance;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        // Deliberately NOT AutoFlush. Flushing on every exchange puts a disk write in the
        // middle of the request loop and roughly halves achieved throughput — the recorder
        // would then be distorting the very timing it exists to capture. Buffered writes are
        // flushed on dispose and periodically below.
        _writer = new StreamWriter(path, append: false);
    }

    public TransportState State => _inner.State;

    public string Description => $"{_inner.Description} (recording)";

    public event Action<TransportState>? StateChanged
    {
        add => _inner.StateChanged += value;
        remove => _inner.StateChanged -= value;
    }

    public Task ConnectAsync(CancellationToken ct) => _inner.ConnectAsync(ct);

    public async Task<string> ExchangeAsync(string command, CancellationToken ct)
    {
        _start ??= _clock.UtcNow;
        var response = await _inner.ExchangeAsync(command, ct).ConfigureAwait(false);

        var entry = new RecordedExchange(
            (_clock.UtcNow - _start.Value).TotalMilliseconds, command, response);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(entry)).ConfigureAwait(false);

            // Flush occasionally so a crash mid-drive still leaves a usable capture, without
            // paying for it on every request.
            if (++_sinceFlush >= FlushInterval)
            {
                _sinceFlush = 0;
                await _writer.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }

        return response;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync().ConfigureAwait(false);
        _writeGate.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}

using System.Net;
using System.Net.Sockets;

namespace DashDeck.Vehicle.Tap;

/// <summary>
/// Passes bytes between a program on the network and the adapter's serial port, recording both ways.
/// </summary>
/// <remarks>
/// <b>Why a bridge and not a monitor.</b> Windows lets one program open a COM port. A monitor that
/// watches another program's port has to sit underneath it as a filter driver, which is what the
/// commercial ones install — and why they break. Here the tap opens the port itself and offers it
/// on a TCP port, and FORScan connects as it would to a Wi-Fi adapter. Every byte passes through
/// this class, so nothing needs to be intercepted.
/// <para>
/// The serial port stays open between connections, so FORScan can disconnect and connect again
/// without the adapter resetting under it. One program at a time; a second is turned away.
/// </para>
/// </remarks>
public sealed class TapBridge
{
    private readonly Stream _adapter;
    private readonly TapRecorder _recorder;
    private readonly IPEndPoint _listenOn;
    private int _connected;
    private volatile NetworkStream? _client;

    public TapBridge(Stream adapter, TapRecorder recorder, IPEndPoint listenOn)
    {
        _adapter = adapter;
        _recorder = recorder;
        _listenOn = listenOn;
    }

    /// <summary>Where it actually listens, once started (the port, if 0 was asked for).</summary>
    public IPEndPoint? Listening { get; private set; }

    /// <summary>Raised once the listener is open.</summary>
    public event Action<IPEndPoint>? Started;

    /// <summary>Listen and relay until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancel)
    {
        // Ends when asked to, or when the adapter goes away — a tap with no adapter has nothing to do.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var ct = stop.Token;
        var listener = new TcpListener(_listenOn);
        listener.Start();
        Listening = (IPEndPoint)listener.LocalEndpoint;
        Started?.Invoke(Listening);

        // Bytes the adapter sends while nobody is connected (a late answer after a disconnect) are
        // read and recorded so they never arrive at the next connection as stale garbage.
        Task session = Task.CompletedTask;
        var pumpFromAdapter = PumpFromAdapterAsync(ct).ContinueWith(
            _ => stop.Cancel(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient socket;

                try
                {
                    socket = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (Interlocked.CompareExchange(ref _connected, 1, 0) != 0)
                {
                    _recorder.Note($"refused a second connection from {socket.Client.RemoteEndPoint}");
                    socket.Dispose();
                    continue;
                }

                socket.NoDelay = true;
                _recorder.Note($"connected: {socket.Client.RemoteEndPoint}");
                var stream = socket.GetStream();
                _client = stream;

                // Served on its own so the loop goes back to accepting, and can turn a second
                // program away rather than leaving it waiting in the queue.
                session = ServeAsync(socket, stream, ct);
            }
        }
        finally
        {
            listener.Stop();

            stop.Cancel();
            await session.ConfigureAwait(false);

            // A serial read on Windows does not always notice cancellation; closing the port, which
            // the caller does next, is what ends it. So it is given a moment, not waited for.
            await Task.WhenAny(pumpFromAdapter, Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None)).ConfigureAwait(false);

            _recorder.Flush();
        }
    }

    /// <summary>One connected program, until it hangs up or the tap stops.</summary>
    private async Task ServeAsync(TcpClient socket, NetworkStream stream, CancellationToken ct)
    {
        try
        {
            await PumpToAdapterAsync(stream, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Writing to the adapter failed: it was unplugged. The adapter pump says so and stops the tap.
        }
        finally
        {
            _client = null;
            _recorder.Flush();
            _recorder.Note("disconnected");
            socket.Dispose();
            Interlocked.Exchange(ref _connected, 0);
        }
    }

    /// <summary>Program → adapter, until the program hangs up.</summary>
    private async Task PumpToAdapterAsync(NetworkStream from, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested)
        {
            int read;

            try
            {
                read = await from.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                return;
            }

            if (read == 0)
            {
                return;
            }

            _recorder.Add(TapDirection.ToAdapter, buffer.AsSpan(0, read));
            await _adapter.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            await _adapter.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Adapter → whoever is connected, for as long as the tap runs.</summary>
    private async Task PumpFromAdapterAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested)
        {
            int read;

            try
            {
                read = await _adapter.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _recorder.Flush();
                _recorder.Note($"adapter lost: {ex.Message}");
                return;
            }

            if (read == 0)
            {
                // A serial stream does not end; a test stream does.
                return;
            }

            _recorder.Add(TapDirection.FromAdapter, buffer.AsSpan(0, read));

            if (_client is { } to)
            {
                try
                {
                    await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    // The program went away mid-answer; the other pump notices and tidies up.
                }
            }
        }
    }
}

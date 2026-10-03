using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Text;
using DashDeck.Abstractions;

namespace DashDeck.Vehicle;

/// <summary>
/// Talks to an ELM327-compatible adapter over a virtual COM port — the OBDLink EX's FTDI
/// bridge, in our case (ADR-0007).
/// </summary>
/// <remarks>
/// Framing is by the adapter's <c>&gt;</c> prompt, not by newlines: a response may span
/// several lines and only the prompt reliably marks the end of one.
/// <para>
/// Two details matter more than they look. The input buffer is drained before every write,
/// because a timed-out request leaves its late response sitting in the buffer and the next
/// command would read it as its own answer — putting a real value against the wrong
/// signal. And disconnection is a normal state with automatic reconnect, because a USB
/// cable in a truck gets knocked and the tablet sleeps (risk R3).
/// </para>
/// </remarks>
public sealed class SerialPortTransport : IStreamingTransport
{
    /// <summary>The OBDLink EX ships at 115200 baud.</summary>
    public const int DefaultBaudRate = 115200;

    private readonly string _portName;
    private readonly int _baudRate;
    private readonly byte[] _readBuffer = new byte[1024];

    private SerialPort? _port;
    private TransportState _state = TransportState.Disconnected;

    public SerialPortTransport(string portName, int baudRate = DefaultBaudRate)
    {
        _portName = portName;
        _baudRate = baudRate;
    }

    public TransportState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            StateChanged?.Invoke(value);
        }
    }

    public string Description => $"{_portName} @ {_baudRate} baud";

    public event Action<TransportState>? StateChanged;

    /// <summary>
    /// How long to wait for a response. Generous by default: an adapter searching for a
    /// vehicle bus can take several seconds before it gives up.
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Serial ports visible to the OS. On Windows these are <c>COMn</c>.</summary>
    public static IReadOnlyList<string> AvailablePorts() => SerialPort.GetPortNames();

    /// <summary>
    /// How long to wait for the OS to open the port.
    /// </summary>
    /// <remarks>
    /// Usually instant. A Bluetooth virtual COM port is the exception: opening the outgoing one
    /// makes Windows try to reach the paired device, and that can block for many seconds — long
    /// enough to stall a port test or a reconnect if nothing bounds it (ADR-0034).
    /// </remarks>
    public TimeSpan OpenTimeout { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>The port this transport opens.</summary>
    public string PortName => _portName;

    /// <summary>The rate it opens at.</summary>
    public int BaudRate => _baudRate;

    public async Task ConnectAsync(CancellationToken ct)
    {
        State = TransportState.Connecting;

        try
        {
            _port?.Dispose();

            var port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
            {
                // Some adapters hold their interpreter in reset until these assert.
                DtrEnable = true,
                RtsEnable = true,
                Handshake = Handshake.None,
                ReadTimeout = (int)ResponseTimeout.TotalMilliseconds,
                WriteTimeout = 2000,
                NewLine = "\r",
            };

            _port = port;

            // SerialPort.Open is synchronous and can block (see OpenTimeout), so it is bounded.
            // On a timeout the open is abandoned; disposing the port releases it if it lands late.
            try
            {
                await Task.Run(port.Open, ct).WaitAsync(OpenTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                port.Dispose();
                _port = null;
                throw new IOException($"{_portName} did not open within {OpenTimeout.TotalSeconds:0} s.", ex);
            }

            port.DiscardInBuffer();
            port.DiscardOutBuffer();

            State = TransportState.Connected;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException)
        {
            State = TransportState.Faulted;

            if (ex is IOException { InnerException: TimeoutException })
            {
                throw;
            }

            throw new IOException(
                $"Could not open {_portName}. Check the adapter is plugged in, the FTDI driver " +
                "is installed (a red LED on the adapter means it is not), and that nothing else " +
                "— FORScan, OBDwiz — is holding the port.",
                ex);
        }
    }

    public async Task<string> ExchangeAsync(string command, CancellationToken ct)
    {
        if (_port is not { IsOpen: true })
        {
            await ReconnectAsync(ct).ConfigureAwait(false);
        }

        var port = _port ?? throw new IOException($"{_portName} is not open.");

        try
        {
            // Drain anything left from a previous timeout. Without this, a late response is
            // read as the answer to the next question.
            if (port.BytesToRead > 0)
            {
                port.DiscardInBuffer();
            }

            var payload = Encoding.ASCII.GetBytes(command.TrimEnd('\r') + "\r");
            await port.BaseStream.WriteAsync(payload, ct).ConfigureAwait(false);
            await port.BaseStream.FlushAsync(ct).ConfigureAwait(false);

            return await ReadToPromptAsync(port, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
        {
            // The cable was pulled, or the tablet slept. Normal, not exceptional. A yanked USB
            // serial device surfaces as any of these — access denied and object-disposed as often
            // as a plain I/O error — and every one of them means the same thing (ADR-0034).
            State = TransportState.Disconnected;
            throw new IOException($"{_portName} dropped mid-exchange.", ex);
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(string command, [EnumeratorCancellation] CancellationToken ct)
    {
        if (_port is not { IsOpen: true })
        {
            await ReconnectAsync(ct).ConfigureAwait(false);
        }

        var port = _port ?? throw new IOException($"{_portName} is not open.");

        if (port.BytesToRead > 0)
        {
            port.DiscardInBuffer();
        }

        var payload = Encoding.ASCII.GetBytes(command.TrimEnd('\r') + "\r");
        await port.BaseStream.WriteAsync(payload, ct).ConfigureAwait(false);
        await port.BaseStream.FlushAsync(ct).ConfigureAwait(false);

        var pending = new StringBuilder();
        var ended = false;

        while (!ended && !ct.IsCancellationRequested)
        {
            // Polled rather than an async read: a quiet bus can say nothing for seconds, and a
            // serial read cannot be relied on to give up when cancelled — a read still waiting
            // would then swallow the STOPPED and prompt that end the monitor.
            int read;
            try
            {
                if (port.BytesToRead == 0)
                {
                    await Task.Delay(10, ct).ConfigureAwait(false);
                    continue;
                }

                read = port.Read(_readBuffer, 0, Math.Min(_readBuffer.Length, port.BytesToRead));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
            {
                State = TransportState.Disconnected;
                throw new IOException($"{_portName} dropped while listening.", ex);
            }

            if (read <= 0)
            {
                continue;
            }

            pending.Append(Encoding.ASCII.GetString(_readBuffer, 0, read));

            var text = pending.ToString();
            var cut = text.LastIndexOfAny(['\r', '\n', '>']);
            if (cut < 0)
            {
                continue;
            }

            ended = text.AsSpan(0, cut + 1).Contains('>');
            pending.Clear().Append(text.AsSpan(cut + 1));

            foreach (var line in text[..(cut + 1)].Split(['\r', '\n', '>'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return line;
            }
        }

        if (!ended)
        {
            // Any character stops a monitor; it answers STOPPED and its prompt. Read to that prompt
            // so the next exchange does not take the tail of the monitor for its answer.
            try
            {
                await port.BaseStream.WriteAsync("\r"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
                await ReadToPromptAsync(port, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
            {
                State = TransportState.Disconnected;
            }
        }
    }

    /// <summary>Read until the adapter's prompt, the only reliable end marker.</summary>
    private async Task<string> ReadToPromptAsync(SerialPort port, CancellationToken ct)
    {
        var builder = new StringBuilder();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ResponseTimeout);

        try
        {
            while (!timeout.IsCancellationRequested)
            {
                var read = await port.BaseStream
                    .ReadAsync(_readBuffer.AsMemory(), timeout.Token)
                    .ConfigureAwait(false);

                if (read <= 0)
                {
                    continue;
                }

                builder.Append(Encoding.ASCII.GetString(_readBuffer, 0, read));

                if (builder.ToString().Contains('>', StringComparison.Ordinal))
                {
                    return builder.ToString();
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own timeout, not the caller's cancellation. Return what arrived; the
            // parser classifies an empty or partial response as a timeout.
        }

        return builder.ToString();
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        int[] delays = [200, 500, 1000, 2000];

        for (var attempt = 0; attempt < delays.Length; attempt++)
        {
            try
            {
                await ConnectAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < delays.Length - 1)
            {
                await Task.Delay(delays[attempt], ct).ConfigureAwait(false);
            }
        }

        throw new IOException($"Could not reopen {_portName} after {delays.Length} attempts.");
    }

    /// <remarks>
    /// <b>Never throws.</b> Closing a port whose USB device has been pulled out throws — access
    /// denied, object disposed, I/O — depending on the driver and the moment. This used to catch
    /// only the I/O case, and the others escaped from a disconnect, through the link, and stopped
    /// the polling loop for good: the cable went back in and nothing ever asked again. Found in
    /// the truck (ADR-0034).
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        var port = _port;
        _port = null;

        try
        {
            if (port is { IsOpen: true })
            {
                port.Close();
            }
        }
        catch (Exception)
        {
            // Nothing useful to do while tearing down a port that is already gone.
        }

        try
        {
            port?.Dispose();
        }
        catch (Exception)
        {
            // Likewise.
        }

        State = TransportState.Disconnected;
        return ValueTask.CompletedTask;
    }
}

using System.IO;
using System.Net.Sockets;
using System.Text;
using DashDeck.Abstractions;

namespace DashDeck.Host.Location;

/// <summary>
/// Reads NMEA over TCP from a GPS-share app on the phone.
/// </summary>
/// <remarks>
/// A socket, not a driver — so it costs nothing against C1's two-driver limit (ADR-0027). The
/// phone runs an app that serves NMEA on a port; DashDeck connects to it, reads lines on a
/// background worker, and keeps the newest fix for the UI poll. A dropped connection ages the
/// last fix to Unavailable and reconnects, so parking out of Wi-Fi range degrades honestly rather
/// than freezing a stale dot. Transport-agnostic by design: Bluetooth would be another source
/// behind this same seam.
/// </remarks>
public sealed class TcpNmeaLocationSource : ILocationSource
{
    private readonly string _host;
    private readonly int _port;
    private readonly IClock _clock;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private LocationFix? _latest;
    private Task? _loop;
    private bool _started;

    public TcpNmeaLocationSource(string host, int port, IClock clock)
    {
        _host = host;
        _port = port;
        _clock = clock;
    }

    /// <inheritdoc />
    public string Name => $"tcp {_host}:{_port}";

    /// <inheritdoc />
    public LocationFix? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <inheritdoc />
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(_host, _port, ct);

                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);

                string? line;
                while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
                {
                    if (NmeaParser.TryParse(line, _clock.UtcNow, out var fix))
                    {
                        lock (_gate)
                        {
                            _latest = fix;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // The link dropped or never came up. The last fix, if any, is no longer current.
                lock (_gate)
                {
                    if (_latest is { } last)
                    {
                        _latest = last with { Quality = SignalQuality.Unavailable };
                    }
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Cancel();

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // Shutting down; a reader blocked on a dead socket is not worth waiting on.
        }

        _cts.Dispose();
    }
}

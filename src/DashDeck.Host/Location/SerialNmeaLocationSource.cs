using System.IO.Ports;
using DashDeck.Abstractions;

namespace DashDeck.Host.Location;

/// <summary>
/// Reads NMEA over Bluetooth, from a paired phone that appears as a virtual COM port.
/// </summary>
/// <remarks>
/// The other transport under ADR-0027's seam, and the one meant to be the default: pair the phone
/// once and it just works whenever both are on — no Wi-Fi, no IP address, no hotspot. Windows'
/// in-box serial-over-Bluetooth turns the pairing into a COM port, so this is a plain
/// <see cref="SerialPort"/> read and needs no driver install (C1 untouched). Same read loop and
/// same reconnect-on-drop behaviour as <see cref="TcpNmeaLocationSource"/>; the newest fix is kept
/// for the UI poll and a lost link ages it rather than freezing it.
/// </remarks>
public sealed class SerialNmeaLocationSource : ILocationSource
{
    // Bluetooth SPP ignores the baud rate — the link runs at its own speed — but SerialPort
    // needs a value, and 9600 is the classic NMEA rate.
    private const int Baud = 9600;

    private readonly string _portName;
    private readonly IClock _clock;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private LocationFix? _latest;
    private Task? _loop;
    private bool _started;

    public SerialNmeaLocationSource(string portName, IClock clock)
    {
        _portName = portName;
        _clock = clock;
    }

    /// <inheritdoc />
    public string Name => $"bluetooth {_portName}";

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
                using var port = new SerialPort(_portName, Baud) { NewLine = "\n", ReadTimeout = 1500 };
                port.Open();

                while (!ct.IsCancellationRequested)
                {
                    string line;

                    try
                    {
                        line = port.ReadLine();
                    }
                    catch (TimeoutException)
                    {
                        // No sentence this window; loop back and re-check cancellation.
                        continue;
                    }

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
                // The port is gone, busy, or unpaired. The last fix, if any, is no longer current.
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
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Shutting down; a reader blocked on a dead port is not worth waiting on.
        }

        _cts.Dispose();
    }
}

using DashDeck.Abstractions;

namespace DashDeck.Vehicle.Elm;

/// <summary>
/// Drives an ELM327-compatible adapter (the OBDLink EX and its STN2230, in our case)
/// over any transport.
/// </summary>
/// <remarks>
/// Requests are serialised: the adapter is one physical resource and interleaving
/// commands corrupts responses. That serialisation is precisely the scarcity the request
/// arbiter exists to schedule.
/// </remarks>
public sealed class ElmAdapter : IVehicleAdapter
{
    private readonly IVehicleTransport _transport;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Request ceiling, measured on the truck rather than assumed.
    /// </summary>
    /// <remarks>
    /// Measured 2026-10-01 on the 2019 F-150 with an OBDLink EX (STN2232 v5.12.4) over USB
    /// at 115200: mean 52.5 ms round trip over 60 requests, min 47.8 ms, p95 71.4 ms, no
    /// failures. That is ~19 requests/second for the whole app, and it closes open
    /// question Q12.
    /// <para>
    /// Note what this is *not*: FORScan reports a 15 ms "min delay" for the same adapter,
    /// which is its inter-command gap, not a round trip. The vehicle's own response time
    /// dominates, so the usable rate is about a quarter of what that figure suggests.
    /// Risk R1 therefore stands largely as written — the move from Bluetooth to USB bought
    /// far less than hoped, and smooth high-rate gauges need request batching rather than
    /// a faster link.
    /// </para>
    /// </remarks>
    public const double AssumedRequestsPerSecond = 19.0;

    private CanBus _selectedBus = CanBus.Hs;

    /// <summary>The module the adapter is addressing, or null for the broadcast it starts on.</summary>
    private ushort? _selectedHeader;

    public ElmAdapter(IVehicleTransport transport, IClock? clock = null)
    {
        _transport = transport;
        _clock = clock ?? SystemClock.Instance;
        _transport.StateChanged += OnTransportStateChanged;
    }

    /// <summary>True once <see cref="InitializeAsync"/> has configured the adapter.</summary>
    private bool _initialized;

    /// <summary>
    /// Set when the link came back after being lost, so the adapter is configured again before
    /// the next request.
    /// </summary>
    /// <remarks>
    /// A reconnect is not a resume. Unplugging the USB cable power-cycles the adapter, and it
    /// comes back with echo on, spaces on, the protocol on automatic and the CAN transceiver
    /// on HS — while this class still believed its own settings and its own record of the
    /// selected bus. The first MS-CAN request after a knock to the cable went out on the wrong
    /// bus and read as NO DATA. So any return to Connected after the first initialisation,
    /// including a switch from the simulator to the real adapter (ADR-0034), re-runs it.
    /// </remarks>
    private volatile bool _needsConfigure;

    /// <summary>How many times the adapter has been configured — 1 after start-up, more after reconnects.</summary>
    public int ConfigureCount { get; private set; }

    private void OnTransportStateChanged(TransportState state)
    {
        if (state == TransportState.Connected && _initialized)
        {
            _needsConfigure = true;
        }
    }

    public AdapterCapabilities? Capabilities { get; private set; }

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (_transport.State != TransportState.Connected)
        {
            await _transport.ConnectAsync(ct).ConfigureAwait(false);
        }

        await ConfigureAsync(ct).ConfigureAwait(false);
        _initialized = true;
        _needsConfigure = false;
    }

    /// <summary>Put the adapter in the state every request assumes, and learn what it can reach.</summary>
    private async Task ConfigureAsync(CancellationToken ct)
    {
        // Whatever the adapter was doing, ATZ puts it back on HS-CAN with defaults — the
        // broadcast header and no receive filter among them.
        _selectedBus = CanBus.Hs;
        _selectedHeader = null;

        await _transport.ExchangeAsync("ATZ", ct).ConfigureAwait(false);      // reset
        await _transport.ExchangeAsync("ATE0", ct).ConfigureAwait(false);     // echo off
        await _transport.ExchangeAsync("ATL0", ct).ConfigureAwait(false);     // linefeeds off
        await _transport.ExchangeAsync("ATS0", ct).ConfigureAwait(false);     // spaces off
        await _transport.ExchangeAsync("ATH0", ct).ConfigureAwait(false);     // headers off
        await _transport.ExchangeAsync("ATSP6", ct).ConfigureAwait(false);    // ISO 15765-4, 11-bit, 500k

        var identity = (await _transport.ExchangeAsync("ATI", ct).ConfigureAwait(false))
            .Replace(">", string.Empty, StringComparison.Ordinal)
            .Trim();

        var buses = await ProbeBusesAsync(ct).ConfigureAwait(false);

        Capabilities = new AdapterCapabilities(
            Name: string.IsNullOrWhiteSpace(identity) ? "Unknown ELM-compatible adapter" : identity,
            Buses: buses,
            SimultaneousBusAccess: buses.Count > 1,
            MaxRequestsPerSecond: AssumedRequestsPerSecond);

        ConfigureCount++;
    }

    /// <summary>
    /// Find out which buses this adapter can actually reach, rather than assuming.
    /// An adapter that cannot switch to MS-CAN makes a large part of the catalog
    /// unreachable, and the app needs to say so plainly instead of showing blanks.
    /// </summary>
    private async Task<IReadOnlySet<CanBus>> ProbeBusesAsync(CancellationToken ct)
    {
        var buses = new HashSet<CanBus> { CanBus.Hs };

        var reply = (await _transport.ExchangeAsync(MsCanCommand, ct).ConfigureAwait(false))
            .ToUpperInvariant();

        if (!reply.Contains('?', StringComparison.Ordinal) &&
            !reply.Contains("ERROR", StringComparison.Ordinal))
        {
            buses.Add(CanBus.Ms);

            // The probe did not just ask a question, it physically moved the adapter to
            // MS-CAN. Record that, or the switch back below is skipped as a no-op and every
            // subsequent request goes out on the wrong bus and answers NO DATA.
            _selectedBus = CanBus.Ms;
        }

        await SelectBusAsync(CanBus.Hs, ct).ConfigureAwait(false);
        return buses;
    }

    // STN extended command: switch the CAN transceiver to the MS-CAN pins (3 and 11).
    private const string MsCanCommand = "STP53";
    private const string HsCanCommand = "STP33";

    private async Task SelectBusAsync(CanBus bus, CancellationToken ct)
    {
        if (_selectedBus == bus)
        {
            return;
        }

        await _transport.ExchangeAsync(bus == CanBus.Ms ? MsCanCommand : HsCanCommand, ct)
            .ConfigureAwait(false);
        _selectedBus = bus;
    }

    /// <summary>
    /// Point the adapter at one module, or back at the broadcast (ADR-0035).
    /// </summary>
    /// <remarks>
    /// Three things move together. <c>ATSH</c> sets the id requests go out on; <c>ATCRA</c>
    /// listens only for that module's answer, so another module chattering on the same bus is
    /// not read as the reply; and the flow-control header is set explicitly, because a reply
    /// longer than one frame (a part number, a VIN) needs the adapter to tell <em>that</em>
    /// module to carry on, and the automatic choice is only documented for the
    /// <c>7E0</c>–<c>7E7</c> engine range. All three are adapter settings — nothing here is
    /// sent to the vehicle. Returns false when the adapter refuses them.
    /// </remarks>
    private async Task<bool> SelectHeaderAsync(ushort? header, CancellationToken ct)
    {
        if (_selectedHeader == header)
        {
            return true;
        }

        string[] commands = header is { } id
            ? [$"ATSH{id:X3}", $"ATCRA{id + 8:X3}", $"ATFCSH{id:X3}", "ATFCSD300000", "ATFCSM1"]
            : [$"ATSH{PidRequest.Broadcast:X3}", "ATAR", "ATFCSM0"];

        foreach (var command in commands)
        {
            var reply = await _transport.ExchangeAsync(command, ct).ConfigureAwait(false);
            if (reply.Contains('?', StringComparison.Ordinal))
            {
                // Half-applied is unknown, not "still the old one": 0 is neither the broadcast
                // nor a module, so whatever the next request wants is set again in full.
                _selectedHeader = 0;
                return false;
            }
        }

        _selectedHeader = header;
        return true;
    }

    public async Task<PidResponse> RequestAsync(PidRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_needsConfigure)
            {
                await ConfigureAsync(ct).ConfigureAwait(false);

                // Cleared after, not before: the reconnect that configuring itself may cause
                // raises Connected again, and this configuration already covers it.
                _needsConfigure = false;
            }

            if (Capabilities is { } caps && !caps.Supports(request.Bus))
            {
                return PidResponse.Failed(request, PidFailure.NoData, _clock.UtcNow);
            }

            await SelectBusAsync(request.Bus, ct).ConfigureAwait(false);

            if (request.Header is { } header && !PidRequest.IsModuleAddress(header))
            {
                return PidResponse.Failed(request, PidFailure.Malformed, _clock.UtcNow);
            }

            if (!await SelectHeaderAsync(request.Header, ct).ConfigureAwait(false))
            {
                return PidResponse.Failed(request, PidFailure.BusError, _clock.UtcNow);
            }

            var raw = await _transport.ExchangeAsync(request.ToCommand(), ct).ConfigureAwait(false);
            return ElmResponseParser.Parse(request, raw, _clock.UtcNow);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A dropped cable mid-request is routine, not exceptional. The transport
            // reconnects on its own; this request simply has no answer — and whatever the
            // adapter was configured as may not survive the reconnect, so configure again.
            // Any exception, not only IOException: a pulled USB device has surfaced as access
            // denied and object disposed too, and one of those escaping used to end polling.
            if (_initialized)
            {
                _needsConfigure = true;
            }

            return PidResponse.Failed(request, PidFailure.Timeout, _clock.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _transport.StateChanged -= OnTransportStateChanged;
        _gate.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}

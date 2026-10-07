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

    /// <summary>
    /// The ceiling claimed when fast requests are on (ADR-0049). Not a measurement of DashDeck:
    /// FORScan, through the serial tap on 2026-10-04, averaged 19.6–20.2 ms a request on the same
    /// truck and adapter asking the same way, about 50 a second. The arbiter still plans against
    /// what it measures, never above this, so a slower reality simply reads as a lower number.
    /// </summary>
    public const double FastRequestsPerSecond = 45.0;

    /// <summary>Where the engine computer answers. The fast path listens for this id alone.</summary>
    private const ushort EngineResponse = 0x7E8;

    private bool _fastRequests;

    /// <summary>
    /// Ask the engine computer's standard values with a receive filter and a response count, so
    /// the adapter stops waiting the moment the one answer arrives (ADR-0049). Off by default; the
    /// dash turns it on from Settings ▸ Vehicle. Takes effect at the next configuration.
    /// </summary>
    public bool FastRequests
    {
        get => _fastRequests;
        set
        {
            if (_fastRequests != value)
            {
                _fastRequests = value;
                if (_initialized)
                {
                    _needsConfigure = true;
                }
            }
        }
    }

    /// <summary>
    /// True when fast requests are on <em>and</em> this adapter can do them. Read after
    /// <see cref="InitializeAsync"/>.
    /// </summary>
    public bool FastRequestsActive => _fastActive;

    private volatile bool _fastActive;

    /// <summary>True while the broadcast's answers are filtered to the engine computer (<c>ATCRA7E8</c>).</summary>
    private bool _broadcastFiltered;

    /// <summary>
    /// Requests that went back to the slow path for good: a value only another module gives,
    /// whose answer the filter would hide.
    /// </summary>
    private readonly HashSet<(byte Mode, ushort Pid)> _slowOnly = [];

    /// <summary>
    /// How many times in a row each request came back empty the fast way and answered the slow way.
    /// </summary>
    /// <remarks>
    /// Once is not evidence: the truck drops the odd answer whichever way it is asked (the
    /// simulator drops 2%), and the slow retry then succeeds by chance. Sending rpm the slow way for
    /// good on one dropped answer made the adapter switch its filter on and off around every other
    /// request, which was slower than never filtering at all.
    /// </remarks>
    private readonly Dictionary<(byte Mode, ushort Pid), int> _hiddenStreak = [];

    /// <summary>Fast-empty, slow-answered this many times in a row and a request goes slow for good.</summary>
    public const int HiddenAnswersBeforeSlow = 3;

    /// <summary>How many requests went the fast way, and how many fell back to the slow one.</summary>
    public int FastCount { get; private set; }

    /// <summary>Fast requests that were asked again the slow way: NO DATA, or a busy reply.</summary>
    public int FallbackCount { get; private set; }

    private CanBus _selectedBus = CanBus.Hs;

    private int? _pins311BitRate = 125000;

    /// <summary>
    /// The rate of the bus on OBD pins 3 and 11, which <c>STP53</c> opens at 125 kbit/s — Ford's
    /// MS-CAN. Newer trucks put a 500 kbit/s bus there; sending at the wrong rate makes error frames
    /// on it, so a caller that has measured the rate (by listening, ADR-0044) sets it here.
    /// </summary>
    /// <remarks>
    /// Null when nobody has measured it: then nothing is sent on pins 3 and 11 at all, and a
    /// request there answers NO DATA without reaching the vehicle (ADR-0052). The dash sets null
    /// for a real vehicle unless the user's own vehicle file gives a rate.
    /// </remarks>
    public int? Pins311BitRate
    {
        get => _pins311BitRate;
        set
        {
            if (_pins311BitRate != value)
            {
                _pins311BitRate = value;
                if (_initialized)
                {
                    _needsConfigure = true;
                }
            }
        }
    }

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
        _broadcastFiltered = false;

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

        _fastActive = _fastRequests && SupportsResponseCount(identity);

        Capabilities = new AdapterCapabilities(
            Name: string.IsNullOrWhiteSpace(identity) ? "Unknown ELM-compatible adapter" : identity,
            Buses: buses,
            SimultaneousBusAccess: buses.Count > 1,
            MaxRequestsPerSecond: _fastActive ? FastRequestsPerSecond : AssumedRequestsPerSecond);

        ConfigureCount++;
    }

    /// <summary>
    /// Whether an adapter that calls itself <paramref name="identity"/> understands a response
    /// count after a request (<c>010C1</c>).
    /// </summary>
    /// <remarks>
    /// The ELM327 added it in v1.3; every STN chip (the OBDLink's) has it. Clones claim any version
    /// they like, which is why a refusal at run time also turns the fast path off.
    /// </remarks>
    public static bool SupportsResponseCount(string identity)
    {
        if (identity.Contains("STN", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var match = System.Text.RegularExpressions.Regex.Match(identity, @"ELM327\s+v(\d+)\.(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        var major = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minor = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        return major > 1 || (major == 1 && minor >= 3);
    }

    /// <summary>
    /// Whether a request may go the fast way (ADR-0049): narrow on purpose.
    /// </summary>
    /// <remarks>
    /// Only what is certain to be one single-frame answer from the engine computer:
    /// <list type="bullet">
    /// <item>standard mode 01 — every answer fits one CAN frame, so a count of one cannot cut it
    /// short;</item>
    /// <item>on the main bus, to the broadcast — not a named module, not pins 3/11;</item>
    /// <item>not a supported-PID bitmap (<c>00</c>, <c>20</c>, …) — several modules answer those,
    /// and a scan wants to know;</item>
    /// <item>not a request already sent back to the slow path because the filter hid its
    /// answer.</item>
    /// </list>
    /// Mode 22 identifiers, the VIN and module sweeps stay slow until their answers' lengths are
    /// known; a count of one on a multi-frame answer is not something to guess about.
    /// </remarks>
    public static bool IsFastEligible(PidRequest request) =>
        request.Mode == 0x01
        && request.Bus == CanBus.Hs
        && request.Header is null
        && request.Pid <= 0xFF
        && request.Pid % 0x20 != 0;

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

        if (bus == CanBus.Ms && _pins311BitRate is { } rate && rate != 125000)
        {
            await _transport.ExchangeAsync($"STPBR{rate}", ct).ConfigureAwait(false);
        }

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

        // Either way the filter the fast path relies on is gone: a module's ATCRA replaced it, or
        // the broadcast's ATAR cleared it.
        _broadcastFiltered = false;
        return true;
    }

    /// <summary>Filter the broadcast's answers to the engine computer, or stop filtering them.</summary>
    private async Task<bool> FilterBroadcastAsync(bool filtered, CancellationToken ct)
    {
        if (_broadcastFiltered == filtered)
        {
            return true;
        }

        var reply = await _transport.ExchangeAsync(filtered ? $"ATCRA{EngineResponse:X3}" : "ATAR", ct).ConfigureAwait(false);
        if (reply.Contains('?', StringComparison.Ordinal))
        {
            return false;
        }

        _broadcastFiltered = filtered;
        return true;
    }

    /// <summary>
    /// One request the fast way: filtered to the engine computer, with a count of one.
    /// Returns null when it should be asked again the slow way.
    /// </summary>
    /// <remarks>
    /// Two answers are not trusted and go round again the slow way, which waits as long as it
    /// always has:
    /// <list type="bullet">
    /// <item><b>NO DATA</b> — perhaps the engine computer does not have it but another module does,
    /// and the filter hid that module's answer. If the slow way finds it, that request stays slow
    /// for good.</item>
    /// <item><b>"Busy, answer follows"</b> (<c>7F 01 78</c>) alone — with a count of one the adapter
    /// stopped listening before the real answer came. The slow way waits for it. A late answer
    /// that lands on a later request cannot be read as that request's: the parser checks that
    /// an answer names the PID that was asked for.</item>
    /// </list>
    /// A <c>?</c> means the adapter does not understand the count after all; the fast path is
    /// turned off until the next configuration.
    /// </remarks>
    private async Task<PidResponse?> RequestFastAsync(PidRequest request, CancellationToken ct)
    {
        if (!await FilterBroadcastAsync(true, ct).ConfigureAwait(false))
        {
            _fastActive = false;
            return null;
        }

        var raw = await _transport.ExchangeAsync(request.ToCommand() + "1", ct).ConfigureAwait(false);
        var trimmed = raw.Replace(">", string.Empty, StringComparison.Ordinal).Trim();

        if (trimmed == "?")
        {
            _fastActive = false;
            return null;
        }

        if (ElmResponseParser.IsOnlyPending(raw))
        {
            return null;
        }

        var response = ElmResponseParser.Parse(request, raw, _clock.UtcNow);
        if (response.Failure == PidFailure.NoData)
        {
            return null;
        }

        FastCount++;
        return response;
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

            // An unmeasured bus is never sent on: a wrong rate puts error frames on it (ADR-0052).
            if (request.Bus == CanBus.Ms && _pins311BitRate is null)
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

            var key = (request.Mode, request.Pid);
            var fast = _fastActive && IsFastEligible(request) && !_slowOnly.Contains(key);

            if (fast)
            {
                if (await RequestFastAsync(request, ct).ConfigureAwait(false) is { } quick)
                {
                    _hiddenStreak.Remove(key);
                    return quick;
                }

                FallbackCount++;
            }

            // The slow way: every module that answers the broadcast is heard, and the adapter waits
            // its full time for them.
            if (request.Header is null && !await FilterBroadcastAsync(false, ct).ConfigureAwait(false))
            {
                return PidResponse.Failed(request, PidFailure.BusError, _clock.UtcNow);
            }

            var raw = await _transport.ExchangeAsync(request.ToCommand(), ct).ConfigureAwait(false);
            var response = ElmResponseParser.Parse(request, raw, _clock.UtcNow);

            if (fast && response.IsSuccess)
            {
                // The engine computer said no and the slow way found it. Again and again, and it is
                // another module that answers this one — the filter would hide it every time.
                var streak = _hiddenStreak.GetValueOrDefault(key) + 1;
                _hiddenStreak[key] = streak;

                if (streak >= HiddenAnswersBeforeSlow)
                {
                    _slowOnly.Add(key);
                    _hiddenStreak.Remove(key);
                }
            }

            return response;
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

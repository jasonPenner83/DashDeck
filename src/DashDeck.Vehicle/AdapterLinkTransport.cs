using DashDeck.Abstractions;
using DashDeck.Vehicle.Diagnostics;

namespace DashDeck.Vehicle;

/// <summary>Where the adapter was found, and what it said it is.</summary>
/// <param name="Port">The serial port, e.g. <c>COM3</c>.</param>
/// <param name="BaudRate">The rate it answered at.</param>
/// <param name="Identity">Its reply to <c>ATI</c>, e.g. <c>ELM327 v1.4b</c> or an STN identifier.</param>
/// <param name="Moved">True when it answered on a port other than the preferred one.</param>
public sealed record AdapterLocation(string Port, int BaudRate, string Identity, bool Moved);

/// <summary>How to find the adapter.</summary>
public sealed record AdapterLinkOptions
{
    /// <summary>The port the user chose. Tried first, always.</summary>
    public required string PreferredPort { get; init; }

    /// <summary>The rate that worked last time; tried before the others.</summary>
    public int? KnownBaudRate { get; init; }

    /// <summary>
    /// What the adapter said it was last time. When set, an adapter found on another port is
    /// adopted only if it says the same — so a second ELM device is never mistaken for this one.
    /// </summary>
    public string? KnownIdentity { get; init; }

    /// <summary>Look on the other ports when the preferred one is missing or silent.</summary>
    public bool Relocate { get; init; } = true;

    /// <summary>Ports never to open — the phone's Bluetooth GPS, for one.</summary>
    public IReadOnlyCollection<string> ReservedPorts { get; init; } = [];

    /// <summary>
    /// Rates to try when the known one does not answer. Shorter than the bring-up tool's list:
    /// the link tries again every few seconds, so a fast miss beats a thorough one.
    /// </summary>
    public IReadOnlyList<int> CandidateRates { get; init; } = [115200, 2000000, 1000000, 500000, 230400, 38400, 9600];
}

/// <summary>
/// The serial link to the OBD-II adapter, made to survive a truck (ADR-0034).
/// </summary>
/// <remarks>
/// <see cref="SerialPortTransport"/> is a pipe: one port, one rate. This wraps it in the
/// behaviour the pipe cannot have on its own:
/// <list type="bullet">
/// <item><b>Finding the rate.</b> The rate that worked last time first, then the others,
/// keeping one only when the adapter answers <c>ATI</c> with an identity. A reply that turns to
/// mojibake mid-drive — the adapter power-cycled back to its factory rate — drops the link and
/// finds the rate again.</item>
/// <item><b>Finding the port.</b> Windows can give the same adapter a different COM number on
/// a different USB socket. When the chosen port is missing or silent, the others are tried, and
/// one is adopted only if it is an adapter — and, once one has been seen, the same one.</item>
/// <item><b>Pacing.</b> A missing adapter is looked for again after a growing pause, never in
/// a tight loop: failed attempts fail fast in between, as a pulled cable should.</item>
/// </list>
/// It only ever sends adapter commands while looking — <c>ATZ</c> and <c>ATI</c> — and nothing
/// to the vehicle (ADR-0006).
/// </remarks>
public sealed class AdapterLinkTransport : IVehicleTransport
{
    private readonly Func<string, int, IVehicleTransport> _open;
    private readonly Func<IReadOnlyList<string>> _listPorts;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AdapterLinkOptions _options;
    private IVehicleTransport? _inner;
    private TransportState _state = TransportState.Disconnected;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private int _failedAttempts;

    /// <summary>Pauses between attempts to find a missing adapter, growing to the last.</summary>
    public static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    /// <param name="options">Which port, which rate, which adapter.</param>
    /// <param name="open">Makes a transport for a port at a rate. A <see cref="SerialPortTransport"/> in the app.</param>
    /// <param name="listPorts">The ports the OS can see right now.</param>
    /// <param name="clock">For pacing the retries.</param>
    public AdapterLinkTransport(
        AdapterLinkOptions options,
        Func<string, int, IVehicleTransport> open,
        Func<IReadOnlyList<string>> listPorts,
        IClock? clock = null)
    {
        _options = options;
        _open = open;
        _listPorts = listPorts;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>The serial link the app uses: real ports, real rates.</summary>
    /// <remarks>
    /// Two seconds per reply rather than the transport's five: long enough for a real adapter's
    /// reset (about one) and its first bus search, short enough that a silent port is passed over
    /// quickly.
    /// </remarks>
    public static AdapterLinkTransport ForSerialPorts(AdapterLinkOptions options, IClock? clock = null) =>
        new(
            options,
            (port, rate) => new SerialPortTransport(port, rate) { ResponseTimeout = TimeSpan.FromSeconds(2) },
            SerialPortTransport.AvailablePorts,
            clock);

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

    public event Action<TransportState>? StateChanged;

    /// <summary>
    /// One line per thing the link did — found, dropped and why, failed to find and why — for a
    /// log a person can read after a test in the truck (ADR-0034).
    /// </summary>
    public event Action<string>? Logged;

    private void Log(string line) => Logged?.Invoke(line);

    /// <summary>Raised each time the adapter is found — on a port, at a rate, with an identity.</summary>
    public event Action<AdapterLocation>? Located;

    /// <summary>Where the adapter is, while it is connected; null otherwise.</summary>
    public AdapterLocation? Current { get; private set; }

    /// <summary>Why the last attempt to find the adapter failed, phrased for a person.</summary>
    public string? LastProblem { get; private set; }

    /// <summary>The port the user chose.</summary>
    public string PreferredPort => _options.PreferredPort;

    public string Description => Current is { } at
        ? $"{at.Identity} on {at.Port} @ {at.BaudRate} baud"
        : $"{_options.PreferredPort} (not connected)";

    /// <summary>
    /// Change which port to look on first. Takes effect at the next attempt; a connected link is
    /// left alone.
    /// </summary>
    public void Prefer(string port)
    {
        _options = _options with { PreferredPort = port, KnownIdentity = null };
        _nextAttempt = DateTimeOffset.MinValue;
        _failedAttempts = 0;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (await TryLocateAsync(ct).ConfigureAwait(false) is null)
        {
            throw new IOException(LastProblem ?? "The adapter was not found.");
        }
    }

    /// <summary>
    /// Look for the adapter once, now. Null when it was not found, with the reason in
    /// <see cref="LastProblem"/>.
    /// </summary>
    /// <param name="ct">Cancels the attempt.</param>
    /// <param name="relocate">
    /// False to try only the chosen port — what launch does, so a missing adapter costs the dash
    /// no start-up time; the other ports are left to the background watcher.
    /// </param>
    public async Task<AdapterLocation?> TryLocateAsync(CancellationToken ct, bool relocate = true)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            return await LocateAsync(ct, relocate).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AdapterLocation?> LocateAsync(CancellationToken ct, bool relocate = true)
    {
        await DropAsync().ConfigureAwait(false);
        State = TransportState.Connecting;

        var present = _listPorts();
        var preferred = _options.PreferredPort;
        var reserved = new HashSet<string>(_options.ReservedPorts, StringComparer.OrdinalIgnoreCase);
        var preferredPresent = present.Contains(preferred, StringComparer.OrdinalIgnoreCase);
        string? preferredProblem = preferredPresent ? null : $"{preferred} isn't there — is the adapter plugged in?";

        if (preferredPresent)
        {
            var (found, problem) = await TryPortAsync(preferred, moved: false, ct).ConfigureAwait(false);

            if (found is not null)
            {
                return found;
            }

            preferredProblem = problem;
        }

        if (relocate && _options.Relocate)
        {
            foreach (var port in present.Where(p =>
                !string.Equals(p, preferred, StringComparison.OrdinalIgnoreCase) && !reserved.Contains(p)))
            {
                var (found, _) = await TryPortAsync(port, moved: true, ct).ConfigureAwait(false);

                if (found is not null)
                {
                    return found;
                }
            }
        }

        LastProblem = preferredProblem;
        Log($"not found: {LastProblem}");
        State = TransportState.Disconnected;
        return null;
    }

    /// <summary>Open one port at each plausible rate until the adapter answers, or the port refuses.</summary>
    private async Task<(AdapterLocation? Found, string? Problem)> TryPortAsync(string port, bool moved, CancellationToken ct)
    {
        var rates = (_options.KnownBaudRate is { } known ? new[] { known } : [])
            .Concat(_options.CandidateRates)
            .Distinct();

        string problem = $"nothing on {port} answered as an OBD-II adapter";

        foreach (var rate in rates)
        {
            ct.ThrowIfCancellationRequested();
            var transport = _open(port, rate);

            try
            {
                await transport.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await transport.DisposeAsync().ConfigureAwait(false);

                // The port itself would not open: in use, gone, or stuck. Another rate cannot help.
                return (null, Describe(port, ex));
            }

            try
            {
                // Silence to a reset means nothing is listening at this rate, or at all — skip the
                // rest of the handshake rather than wait out two more timeouts.
                if (Clean(await transport.ExchangeAsync("ATZ", ct).ConfigureAwait(false)).Length == 0)
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                await transport.ExchangeAsync("ATE0", ct).ConfigureAwait(false);
                var identity = Clean(await transport.ExchangeAsync("ATI", ct).ConfigureAwait(false));

                if (BaudNegotiator.LooksLikeAdapter(identity) && IsTheAdapter(identity, moved))
                {
                    _inner = transport;
                    _inner.StateChanged += OnInnerStateChanged;
                    Current = new AdapterLocation(port, rate, identity, moved);
                    _options = _options with { KnownBaudRate = rate, KnownIdentity = identity };
                    LastProblem = null;
                    _failedAttempts = 0;
                    State = TransportState.Connected;
                    Log($"found {identity} on {port} @ {rate} baud{(moved ? " (moved)" : "")}");
                    Located?.Invoke(Current);
                    return (Current, null);
                }

                if (BaudNegotiator.LooksLikeAdapter(identity))
                {
                    // An adapter, but not this one: stop here rather than adopt it.
                    await transport.DisposeAsync().ConfigureAwait(false);
                    return (null, $"{port} has a different adapter ({identity})");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A drop mid-handshake: try the next rate.
            }

            await transport.DisposeAsync().ConfigureAwait(false);
        }

        return (null, problem);
    }

    /// <summary>
    /// On the chosen port any adapter is accepted. Elsewhere, once an adapter has been seen, only
    /// the same one is — an identity is the only thing that ties a COM number to a device.
    /// </summary>
    private bool IsTheAdapter(string identity, bool moved) =>
        !moved
        || _options.KnownIdentity is not { Length: > 0 } known
        || string.Equals(Squash(known), Squash(identity), StringComparison.OrdinalIgnoreCase);

    private static string Squash(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static string Clean(string reply) => reply
        .Replace(">", string.Empty, StringComparison.Ordinal)
        .Replace("ATI", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Trim();

    private static string Describe(string port, Exception ex) => (ex.InnerException ?? ex) switch
    {
        UnauthorizedAccessException => $"{port} is held open by something else — unplug the adapter's USB for 10 s to free it",
        TimeoutException => $"{port} did not open in time",
        _ => $"{port} could not be opened",
    };

    public async Task<string> ExchangeAsync(string command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (_inner is not { State: TransportState.Connected })
            {
                if (_clock.UtcNow < _nextAttempt)
                {
                    throw new IOException(LastProblem ?? "Waiting to look for the adapter again.");
                }

                if (await LocateAsync(ct).ConfigureAwait(false) is null)
                {
                    _nextAttempt = _clock.UtcNow + Backoff[Math.Min(_failedAttempts, Backoff.Length - 1)];
                    _failedAttempts++;
                    throw new IOException(LastProblem ?? "The adapter was not found.");
                }
            }

            var inner = _inner!;
            string reply;

            try
            {
                reply = await inner.ExchangeAsync(command, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Pulled, slept, or reset — whatever the driver called it. Look again on the very
                // next request. Always an IOException upward: that is the one "the link dropped"
                // the adapter above handles.
                LastProblem = $"{Current?.Port ?? _options.PreferredPort} dropped";
                Log($"dropped: {ex.GetType().Name}: {ex.Message}");
                await DropAsync().ConfigureAwait(false);
                _nextAttempt = DateTimeOffset.MinValue;
                throw ex as IOException ?? new IOException(LastProblem, ex);
            }

            if (!BaudNegotiator.IsMostlyPrintable(reply))
            {
                // The adapter reset to another rate under us. Find the rate again next request.
                LastProblem = $"{Current?.Port ?? _options.PreferredPort} answered at the wrong rate — finding it again";
                Log(LastProblem);
                await DropAsync().ConfigureAwait(false);
                _nextAttempt = DateTimeOffset.MinValue;
                throw new IOException(LastProblem);
            }

            return reply;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnInnerStateChanged(TransportState state)
    {
        if (state is TransportState.Disconnected or TransportState.Faulted)
        {
            State = TransportState.Disconnected;
        }
    }

    private async Task DropAsync()
    {
        if (_inner is { } inner)
        {
            inner.StateChanged -= OnInnerStateChanged;
            _inner = null;

            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A port that is gone may refuse to close. It must never stop the link reconnecting.
                Log($"closing the old port: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Current = null;
        State = TransportState.Disconnected;
    }

    /// <summary>
    /// Hold the link still — no search, no reconnect, no exchange — until the returned handle is
    /// disposed.
    /// </summary>
    /// <remarks>
    /// For the port test in Settings ▸ Vehicle. Both open serial ports, and a port can only be
    /// open once: when the background search and the test reached the same port together, one of
    /// them lost with "access denied" and reported the port as held by another program — DashDeck
    /// blaming FORScan for itself (found in the truck, 2026-10-02). Waits for an attempt already
    /// under way to finish first.
    /// </remarks>
    public async Task<IDisposable> PauseAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        Log("paused for a port test");
        return new Release(this);
    }

    private sealed class Release(AdapterLinkTransport link) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            try
            {
                link._gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // The link closed while paused; nothing left to resume.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DropAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

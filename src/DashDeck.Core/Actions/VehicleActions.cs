using System.Globalization;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Core.Discovery;
using DashDeck.Vehicle;

namespace DashDeck.Core.Actions;

/// <summary>
/// The confirm gesture, gate 4 of ADR-0006: a press held for long enough, just now.
/// </summary>
/// <param name="HeldFor">How long the button was held.</param>
/// <param name="At">When the hold finished.</param>
public sealed record ActionConfirmation(TimeSpan HeldFor, DateTimeOffset At)
{
    /// <summary>How long a hold must be.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(2);

    /// <summary>How long a hold stays good for: it confirms the action that follows it, not one later.</summary>
    public static readonly TimeSpan GoodFor = TimeSpan.FromSeconds(10);
}

/// <summary>How an action went.</summary>
public enum ActionOutcome
{
    /// <summary>A gate said no; nothing was sent.</summary>
    Refused,

    /// <summary>Sent, and the vehicle said it was done.</summary>
    Done,

    /// <summary>Sent, and the vehicle declined it (a negative response).</summary>
    Declined,

    /// <summary>Sent, and no answer came.</summary>
    NoAnswer,
}

/// <summary>An action's outcome, and the words for it.</summary>
public sealed record ActionResult(ActionOutcome Outcome, string Message)
{
    public bool Sent => Outcome != ActionOutcome.Refused;
}

/// <summary>Gate 3, the interlock, as read: may it go, and if not why.</summary>
public sealed record ActionInterlock(bool Allowed, string Reason, double? SpeedKph, double? Rpm);

/// <summary>Gate 5: where every attempt is written.</summary>
public interface IActionLog
{
    /// <summary>Append a line; false when it could not be written.</summary>
    bool Write(DateTimeOffset at, string line);
}

/// <summary>
/// The one place DashDeck writes to a vehicle (ADR-0006, ADR-0055). One action so far: clearing the
/// diagnostic trouble codes.
/// </summary>
/// <remarks>
/// Every action passes all five of ADR-0006's gates, or nothing is sent:
/// <list type="number">
/// <item><b>Permission.</b> The action is the shell's alone. Components see
/// <see cref="IComponentContext.Actions"/> as null and their manifest has no permission that names a
/// write; nothing in <c>DashDeck.Abstractions</c> reaches this class.</item>
/// <item><b>Master switch</b>, off by default: Settings ▸ Diagnostics ▸ ALLOW CLEARING CODES.</item>
/// <item><b>Interlock</b>, read fresh from the truck at the moment of asking: stopped (speed 0) with
/// the engine off (rpm 0) and the engine computer answering — the ignition on.</item>
/// <item><b>Confirm gesture</b>: a two-second hold, used once, within ten seconds.</item>
/// <item><b>Audit log</b>: every attempt, refused or sent, with what the screen showed and what came
/// back, in <c>%LOCALAPPDATA%\DashDeck\actions.log</c>.</item>
/// </list>
/// </remarks>
public sealed class VehicleActions
{
    /// <summary>Speed below this is stopped, km/h.</summary>
    public const double StoppedKph = 0.5;

    /// <summary>Engine speed below this is off, rpm.</summary>
    public const double EngineOffRpm = 50;

    private readonly Func<PidRequest, CancellationToken, Task<PidResponse>> _ask;
    private readonly Func<SignalCatalog> _catalog;
    private readonly Func<bool> _enabled;
    private readonly IActionLog _log;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _one = new(1, 1);
    private DateTimeOffset? _lastConfirmation;

    /// <param name="ask">Straight to the adapter, outside the polling plan (<c>VehicleService.ProbeAsync</c>).</param>
    /// <param name="catalog">Where speed and rpm live.</param>
    /// <param name="enabled">The master switch, read at the moment of asking.</param>
    /// <param name="log">The audit log.</param>
    /// <param name="clock">Time.</param>
    public VehicleActions(
        Func<PidRequest, CancellationToken, Task<PidResponse>> ask,
        Func<SignalCatalog> catalog,
        Func<bool> enabled,
        IActionLog log,
        IClock clock)
    {
        _ask = ask;
        _catalog = catalog;
        _enabled = enabled;
        _log = log;
        _clock = clock;
    }

    /// <summary>Gate 2: whether the master switch is on now.</summary>
    public bool IsEnabled => _enabled();

    /// <summary>
    /// Gate 3, read from the truck now: may the codes be cleared? Stopped, engine off, ignition on.
    /// </summary>
    public async Task<ActionInterlock> CheckClearCodesAsync(CancellationToken ct)
    {
        var catalog = _catalog();
        var speed = await SignalProbe.ReadAsync(catalog, SignalProbe.Speed, _ask, ct).ConfigureAwait(false);
        var rpm = await SignalProbe.ReadAsync(catalog, SignalProbe.Rpm, _ask, ct).ConfigureAwait(false);

        if (speed is null || rpm is null)
        {
            return new ActionInterlock(false, "The engine computer is not answering. Turn the ignition on (push START without the brake) and try again.", speed, rpm);
        }

        if (speed >= StoppedKph)
        {
            return new ActionInterlock(false, "Only when stopped.", speed, rpm);
        }

        if (rpm >= EngineOffRpm)
        {
            return new ActionInterlock(false, "Only with the engine off and the ignition on: switch the engine off, then push START without the brake.", speed, rpm);
        }

        return new ActionInterlock(true, "Stopped, engine off, ignition on.", speed, rpm);
    }

    /// <summary>
    /// Clear the trouble codes in every emissions module (mode 04 to the broadcast): the
    /// check-engine light goes off, and the readiness monitors and freeze frame are reset.
    /// </summary>
    /// <param name="confirmation">The hold that confirmed it.</param>
    /// <param name="shown">What the screen showed the driver they were clearing, for the log.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<ActionResult> ClearDiagnosticCodesAsync(ActionConfirmation confirmation, string shown, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        const string Action = "CLEAR CODES";

        if (!await _one.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return Refuse(Action, shown, "Already clearing.");
        }

        try
        {
            if (!_enabled())
            {
                return Refuse(Action, shown, "Clearing codes is switched off. Turn on ALLOW CLEARING CODES in Settings ▸ Diagnostics.");
            }

            var now = _clock.UtcNow;
            if (confirmation.HeldFor < ActionConfirmation.Hold)
            {
                return Refuse(Action, shown, "Hold the button until it fills.");
            }

            if (now - confirmation.At > ActionConfirmation.GoodFor || confirmation.At > now + TimeSpan.FromSeconds(1))
            {
                return Refuse(Action, shown, "That hold is too old. Hold the button again.");
            }

            if (_lastConfirmation == confirmation.At)
            {
                return Refuse(Action, shown, "That hold was already used. Hold the button again.");
            }

            _lastConfirmation = confirmation.At;

            var interlock = await CheckClearCodesAsync(ct).ConfigureAwait(false);
            if (!interlock.Allowed)
            {
                return Refuse(Action, shown, interlock.Reason, interlock);
            }

            // No record, no write: a log that cannot be written stops the action rather than the dash.
            if (!Write(Action, $"SENDING mode 04 to the broadcast — {Readings(interlock)}; clearing: {shown}"))
            {
                return new ActionResult(ActionOutcome.Refused, "The audit log could not be written, so nothing was sent.");
            }

            var reply = await _ask(PidRequest.Service(ObdService.ClearCodes, CanBus.Hs), ct).ConfigureAwait(false);
            var result = reply switch
            {
                { IsSuccess: true } => new ActionResult(ActionOutcome.Done, "Codes cleared. The light goes out now; if the fault is still there, it comes back."),
                { Failure: PidFailure.Rejected, NegativeCode: var code } => new ActionResult(
                    ActionOutcome.Declined,
                    string.Create(CultureInfo.InvariantCulture, $"The engine computer declined (code {code:X2}{(code == 0x22 ? ": conditions not right — engine off, ignition on" : "")}).")),
                _ => new ActionResult(ActionOutcome.NoAnswer, $"No answer ({reply.Failure}). The codes may or may not be cleared — read them again."),
            };

            Write(Action, $"{result.Outcome.ToString().ToUpperInvariant()} — {result.Message}");
            return result;
        }
        finally
        {
            _one.Release();
        }
    }

    private ActionResult Refuse(string action, string shown, string why, ActionInterlock? interlock = null)
    {
        Write(action, $"REFUSED — {why}{(interlock is null ? "" : $" ({Readings(interlock)})")}; would have cleared: {shown}");
        return new ActionResult(ActionOutcome.Refused, why);
    }

    private static string Readings(ActionInterlock i) => string.Create(
        CultureInfo.InvariantCulture,
        $"speed {(i.SpeedKph is { } s ? s.ToString("0.#", CultureInfo.InvariantCulture) : "?")} km/h, rpm {(i.Rpm is { } r ? r.ToString("0", CultureInfo.InvariantCulture) : "?")}");

    private bool Write(string action, string text) => _log.Write(_clock.UtcNow, $"{action}: {text}");
}

/// <summary>The audit log as a file: one line per attempt, appended, never rewritten.</summary>
public sealed class ActionLogFile(string path) : IActionLog
{
    public string Path { get; } = path;

    public bool Write(DateTimeOffset at, string line)
    {
        try
        {
            if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
            }

            File.AppendAllText(Path, string.Create(CultureInfo.InvariantCulture, $"{at.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}  {line}{Environment.NewLine}"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log failing to write is not a reason to stop the dash — but it is one to say so.
            LastProblem = ex.Message;
            return false;
        }
    }

    public string? LastProblem { get; private set; }
}

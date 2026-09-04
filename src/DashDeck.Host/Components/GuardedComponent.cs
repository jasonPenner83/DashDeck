using DashDeck.Abstractions;

namespace DashDeck.Host.Components;

/// <summary>Where a component has got to, across its whole life in the host.</summary>
public enum ComponentState
{
    /// <summary>Loaded, initialised, and currently shown.</summary>
    Active,

    /// <summary>Healthy but not on the visible page — signal declarations withdrawn.</summary>
    Stopped,

    /// <summary>The app is backgrounded or the vehicle link dropped.</summary>
    Suspended,

    /// <summary>A call into it threw or hung. Transient — it may be tried again.</summary>
    Faulted,

    /// <summary>Faulted too many times. The host has stopped calling it (ADR-0002).</summary>
    Disabled,

    /// <summary>Its <c>apiVersion</c> is not one the host serves. Never instantiated.</summary>
    Incompatible,

    /// <summary>Its manifest, assembly or type was unusable. Never instantiated.</summary>
    Rejected,
}

/// <summary>
/// A component with a wall around it.
/// </summary>
/// <remarks>
/// <b>ADR-0002 accepted knowingly that a bad component can crash the dash, and promised to
/// mitigate it — this is the mitigation.</b> Every call into component code goes through
/// here: it is time-boxed, its exceptions are caught, and a component that throws or hangs is
/// counted against and, past a threshold, disabled — replaced on screen by a "component
/// stopped" state rather than being allowed to take the shell down or wedge the UI thread.
/// <para>
/// In-process, a hung call cannot truly be killed; what the guard guarantees is that the host
/// stops <em>waiting</em> on it and stops calling a component that does it repeatedly. That is
/// the honest limit of containment without a process boundary (ADR-0002's rejected
/// alternative), and it is enough to keep one wedged component from freezing the dash.
/// </para>
/// </remarks>
internal sealed class GuardedComponent(
    IDashComponent component,
    IComponentLogger logger,
    TimeSpan? callTimeout = null)
{
    /// <summary>
    /// How long any one lifecycle call may take before it is treated as hung. Five seconds in
    /// production — a JVM component can take a moment to warm up — and overridable so a test of
    /// the hang path does not have to wait that long for each of them.
    /// </summary>
    private readonly TimeSpan _callTimeout = callTimeout ?? TimeSpan.FromSeconds(5);

    /// <summary>Faults tolerated before the host gives up on the component for the session.</summary>
    private const int FaultsBeforeDisable = 3;

    private int _faults;

    /// <summary>The component's id, from the instance itself.</summary>
    public string Id => component.Id;

    /// <summary>Where it is now. Rendered, never guessed at.</summary>
    public ComponentState State { get; private set; } = ComponentState.Stopped;

    /// <summary>What went wrong last, for the "component stopped" state to show.</summary>
    public string? LastError { get; private set; }

    public Task<bool> InitializeAsync(IComponentContext context, CancellationToken ct) =>
        Guard("initialize", token => component.InitializeAsync(context, token), ComponentState.Stopped, ct);

    public Task<bool> ActivateAsync(CancellationToken ct) =>
        Guard("start", component.StartAsync, ComponentState.Active, ct);

    public Task<bool> DeactivateAsync(CancellationToken ct) =>
        Guard("stop", component.StopAsync, ComponentState.Stopped, ct);

    public Task<bool> SuspendAsync(CancellationToken ct) =>
        Guard("suspend", component.SuspendAsync, ComponentState.Suspended, ct);

    public Task<bool> ResumeAsync(CancellationToken ct) =>
        Guard("resume", component.ResumeAsync, ComponentState.Active, ct);

    private async Task<bool> Guard(
        string verb,
        Func<CancellationToken, Task> call,
        ComponentState onSuccess,
        CancellationToken ct)
    {
        // A disabled component is not called again, so a broken one costs nothing per frame.
        if (State is ComponentState.Disabled)
        {
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var work = SafeInvoke(call, linked.Token);
        var timeout = Task.Delay(_callTimeout, ct);
        var finished = await Task.WhenAny(work, timeout).ConfigureAwait(false);

        if (finished == timeout && !work.IsCompleted)
        {
            // Ask it to stop, then stop waiting whether or not it obliges. The orphaned task
            // has its exceptions observed by SafeInvoke, so it cannot resurface as an
            // unobserved-exception crash later.
            linked.Cancel();
            return Fault(verb, $"did not return within {_callTimeout.TotalSeconds:0}s");
        }

        if (await work.ConfigureAwait(false) is { } error)
        {
            return Fault(verb, error);
        }

        State = onSuccess;
        LastError = null;
        return true;
    }

    /// <summary>Run the call, returning null on success or the failure message on throw.</summary>
    private static async Task<string?> SafeInvoke(Func<CancellationToken, Task> call, CancellationToken ct)
    {
        try
        {
            await call(ct).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return "was cancelled";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private bool Fault(string verb, string detail)
    {
        _faults++;
        LastError = $"{verb} {detail}";

        if (_faults >= FaultsBeforeDisable)
        {
            State = ComponentState.Disabled;
            logger.Log(LogLevel.Error, $"disabled after {_faults} faults; last: {LastError}");
        }
        else
        {
            State = ComponentState.Faulted;
            logger.Log(LogLevel.Warning, $"fault {_faults}/{FaultsBeforeDisable}: {LastError}");
        }

        return false;
    }
}

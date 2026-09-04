using DashDeck.Host.Components;

namespace DashDeck.Host.Tests.Components;

/// <summary>
/// The wall around a component (ADR-0002). A component that throws or hangs must be contained,
/// not trusted — these are the tests that prove the containment happens rather than being
/// merely intended.
/// </summary>
public class GuardedComponentTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task A_healthy_call_moves_the_state_and_reports_success()
    {
        var guard = new GuardedComponent(new ScriptedComponent(), new CapturingLogger());

        Assert.True(await guard.ActivateAsync(CancellationToken.None));
        Assert.Equal(ComponentState.Active, guard.State);

        Assert.True(await guard.DeactivateAsync(CancellationToken.None));
        Assert.Equal(ComponentState.Stopped, guard.State);
    }

    [Fact]
    public async Task A_throwing_call_is_caught_and_faulted_not_propagated()
    {
        var component = new ScriptedComponent
        {
            OnStart = _ => throw new InvalidOperationException("boom"),
        };
        var log = new CapturingLogger();
        var guard = new GuardedComponent(component, log);

        // The exception does not escape — the host never sees it.
        var ok = await guard.ActivateAsync(CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(ComponentState.Faulted, guard.State);
        Assert.Contains("boom", guard.LastError);
    }

    [Fact]
    public async Task Repeated_faults_disable_the_component_and_stop_calling_it()
    {
        var component = new ScriptedComponent
        {
            OnStart = _ => throw new InvalidOperationException("boom"),
        };
        var guard = new GuardedComponent(component, new CapturingLogger());

        await guard.ActivateAsync(CancellationToken.None);
        Assert.Equal(ComponentState.Faulted, guard.State);

        await guard.ActivateAsync(CancellationToken.None);
        Assert.Equal(ComponentState.Faulted, guard.State);

        await guard.ActivateAsync(CancellationToken.None);
        Assert.Equal(ComponentState.Disabled, guard.State);

        // Once disabled it is not entered again: a broken component costs nothing per frame.
        await guard.ActivateAsync(CancellationToken.None);
        Assert.Equal(3, component.StartCalls);
    }

    [Fact]
    public async Task A_hung_call_times_out_rather_than_wedging_the_host()
    {
        var component = new ScriptedComponent
        {
            // Never returns until cancelled — the wedged-UI-thread failure this exists to stop.
            OnStart = ct => Task.Delay(Timeout.Infinite, ct),
        };
        var guard = new GuardedComponent(component, new CapturingLogger(), Short);

        var ok = await guard.ActivateAsync(CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(ComponentState.Faulted, guard.State);
        Assert.Contains("did not return", guard.LastError);
    }
}

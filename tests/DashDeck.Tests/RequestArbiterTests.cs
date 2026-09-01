using DashDeck.Abstractions;
using DashDeck.Core.Arbitration;

namespace DashDeck.Tests;

/// <summary>
/// The arbiter is the load-bearing decision of the project (ADR-0004): it is what lets
/// components be added freely despite a fixed request budget. These tests pin down the
/// behaviour the ecosystem promise depends on.
/// </summary>
public class RequestArbiterTests
{
    private static RequestArbiter Arbiter(double budgetHz) =>
        new(TestCatalog.Load()) { BudgetHz = budgetHz };

    [Fact]
    public void Merges_duplicate_declarations_into_one_poll()
    {
        var arbiter = Arbiter(20);

        using var a = arbiter.Declare("vehicle.speed", SignalPriority.Normal, 1);
        using var b = arbiter.Declare("vehicle.speed", SignalPriority.Normal, 4);

        var entries = arbiter.CurrentPlan.Entries;

        Assert.Single(entries);
        Assert.Equal(4, entries[0].RateHz, 3);
    }

    [Fact]
    public void Merged_declaration_takes_the_higher_priority()
    {
        var arbiter = Arbiter(20);

        using var low = arbiter.Declare("vehicle.speed", SignalPriority.Low, 1);
        using var high = arbiter.Declare("vehicle.speed", SignalPriority.High, 1);

        Assert.Equal(SignalPriority.High, arbiter.CurrentPlan.Entries[0].Priority);
    }

    [Fact]
    public void Within_budget_everyone_gets_what_they_asked_for()
    {
        var arbiter = Arbiter(20);

        using var speed = arbiter.Declare("vehicle.speed", SignalPriority.High, 4);
        using var rpm = arbiter.Declare("engine.rpm", SignalPriority.High, 4);

        Assert.False(arbiter.CurrentPlan.IsDegraded);
        Assert.Equal(4, speed.EffectiveRateHz, 3);
        Assert.Equal(4, rpm.EffectiveRateHz, 3);
    }

    [Fact]
    public void Over_budget_high_priority_is_served_first_and_low_is_shed()
    {
        // The behaviour that matters when the ecosystem grows: adding a component must not
        // quietly degrade the signals another component depends on.
        var arbiter = Arbiter(8);

        using var speed = arbiter.Declare("vehicle.speed", SignalPriority.High, 4);
        using var rpm = arbiter.Declare("engine.rpm", SignalPriority.High, 4);
        using var load = arbiter.Declare("engine.load", SignalPriority.Low, 4);

        Assert.True(arbiter.CurrentPlan.IsDegraded);
        Assert.Equal(4, speed.EffectiveRateHz, 3);
        Assert.Equal(4, rpm.EffectiveRateHz, 3);
        Assert.Equal(0, load.EffectiveRateHz, 3);
    }

    [Fact]
    public void A_tier_that_does_not_fit_degrades_proportionally_together()
    {
        var arbiter = Arbiter(4);

        using var speed = arbiter.Declare("vehicle.speed", SignalPriority.High, 4);
        using var rpm = arbiter.Declare("engine.rpm", SignalPriority.High, 4);

        // 8 Hz of demand into a 4 Hz budget: both halve rather than one winning outright.
        Assert.Equal(2, speed.EffectiveRateHz, 3);
        Assert.Equal(2, rpm.EffectiveRateHz, 3);
        Assert.Equal(4, arbiter.CurrentPlan.AllocatedHz, 3);
    }

    [Fact]
    public void Subscribers_are_told_when_their_rate_changes()
    {
        // Degradation is explicit. A component must be able to render "1 Hz" honestly
        // rather than appear frozen.
        var arbiter = Arbiter(20);
        using var speed = arbiter.Declare("vehicle.speed", SignalPriority.Normal, 4);

        double? notified = null;
        speed.EffectiveRateChanged += rate => notified = rate;

        arbiter.BudgetHz = 2;

        Assert.NotNull(notified);
        Assert.Equal(2, notified!.Value, 3);
    }

    [Fact]
    public void Withdrawing_a_declaration_frees_budget_for_others()
    {
        var arbiter = Arbiter(4);

        var speed = arbiter.Declare("vehicle.speed", SignalPriority.High, 4);
        using var load = arbiter.Declare("engine.load", SignalPriority.Low, 4);

        Assert.Equal(0, load.EffectiveRateHz, 3);

        // A component going off-screen releases its demand; others should recover.
        speed.Dispose();

        Assert.Equal(4, load.EffectiveRateHz, 3);
    }

    [Fact]
    public void Declaring_an_unknown_signal_fails_loudly()
    {
        // Reaching for a PID that is not in the catalog is a mistake to surface at the
        // point of the mistake, not a blank tile discovered while driving.
        var arbiter = Arbiter(10);

        var ex = Assert.Throws<ArgumentException>(
            () => arbiter.Declare("engine.transmissionTemp", SignalPriority.Normal, 1));

        Assert.Contains("not in the catalog", ex.Message, StringComparison.Ordinal);
    }
}

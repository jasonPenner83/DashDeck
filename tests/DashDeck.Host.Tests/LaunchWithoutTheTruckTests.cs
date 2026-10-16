using DashDeck.Abstractions;
using DashDeck.Host;
using DashDeck.Vehicle;

namespace DashDeck.Host.Tests;

/// <summary>
/// The window must not wait on the truck (Q41).
/// </summary>
/// <remarks>
/// Finding the adapter used to happen before anything was drawn, with a twelve-second budget. A
/// Bluetooth COM port whose phone is asleep spends all of it — and because an abandoned
/// <c>SerialPort.Open</c> sat on a thread-pool thread, several of them starved the pool and a
/// launch came up a minute late with no window at all. The port fix was one half; this is the
/// other: the stack comes up simulated and the watcher attaches the adapter when it answers.
/// </remarks>
public sealed class LaunchWithoutTheTruckTests
{
    private static CatalogSources Sources() => CatalogSources.None;

    [Fact]
    public async Task A_launch_with_an_unreachable_adapter_comes_up_at_once_and_simulated()
    {
        // A port that cannot answer, which is what a paired-but-sleeping Bluetooth port is.
        var adapter = new AdapterLinkOptions { PreferredPort = "COM_nonexistent_for_this_test" };

        var started = System.Diagnostics.Stopwatch.StartNew();

        await using var stack = await VehicleStack.StartAsync(
            adapter,
            "cold-start-city",
            Sources(),
            CancellationToken.None,
            locateBudget: TimeSpan.Zero);

        started.Stop();

        // The number that matters: the shell is handed a stack in well under the old budget.
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(5),
            $"starting took {started.Elapsed.TotalSeconds:0.0} s — the window waits on this");

        Assert.True(stack.IsSimulated, "with no adapter found it must come up simulated, not blank");
    }

    [Fact]
    public async Task The_budget_is_still_honoured_when_a_caller_asks_for_one()
    {
        // The dev tools and the tests still want the old behaviour; only the shell opts out.
        var adapter = new AdapterLinkOptions { PreferredPort = "COM_nonexistent_for_this_test" };

        await using var stack = await VehicleStack.StartAsync(
            adapter,
            "cold-start-city",
            Sources(),
            CancellationToken.None,
            locateBudget: TimeSpan.FromSeconds(2));

        Assert.True(stack.IsSimulated);
    }

    [Fact]
    public async Task No_adapter_at_all_is_unchanged()
    {
        await using var stack = await VehicleStack.StartAsync(
            adapter: null,
            "cold-start-city",
            Sources(),
            CancellationToken.None);

        Assert.True(stack.IsSimulated);
    }
}

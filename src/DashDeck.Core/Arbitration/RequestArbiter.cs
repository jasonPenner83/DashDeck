using DashDeck.Abstractions;
using DashDeck.Core.Catalog;

namespace DashDeck.Core.Arbitration;

/// <summary>One signal's place in the current polling plan.</summary>
public sealed record PlanEntry(SignalDefinition Signal, SignalPriority Priority, double RateHz)
{
    public double IntervalSeconds => RateHz > 0 ? 1.0 / RateHz : double.PositiveInfinity;
}

/// <summary>The merged, budget-respecting polling plan for every live declaration.</summary>
public sealed record PollingPlan(IReadOnlyList<PlanEntry> Entries, double BudgetHz, double DemandHz)
{
    public double AllocatedHz => Entries.Sum(e => e.RateHz);

    public bool IsDegraded => DemandHz > BudgetHz + 1e-9;

    public static readonly PollingPlan Empty = new([], 0, 0);
}

/// <summary>
/// Merges every component's declaration into a single deduplicated polling plan that fits
/// the adapter's request budget.
/// </summary>
/// <remarks>
/// This is the load-bearing decision of the whole project (ADR-0004). The adapter is one
/// serialised resource with a finite ceiling, while the entire premise is that components
/// can be added freely. Without central scheduling, every component gets slower as the
/// ecosystem grows — the exact failure that stops people adding components.
/// <para>
/// Allocation is strictly by priority: each tier is satisfied in full while budget
/// remains. The tier that does not fit is scaled down proportionally, and tiers below it
/// get nothing and are reported as shed. Degradation is explicit — subscribers are told
/// their effective rate so they can render "1 Hz" honestly rather than appear frozen.
/// </para>
/// </remarks>
public sealed class RequestArbiter
{
    private readonly SignalCatalog _catalog;
    private readonly List<Declaration> _declarations = [];
    private readonly Lock _sync = new();

    public RequestArbiter(SignalCatalog catalog) => _catalog = catalog;

    /// <summary>Raised whenever the plan is rebuilt, so the polling loop can adopt it.</summary>
    public event Action<PollingPlan>? PlanChanged;

    /// <summary>The adapter's total request budget. Setting it re-plans immediately.</summary>
    public double BudgetHz
    {
        get;
        set
        {
            field = value;
            Replan();
        }
    } = 1.0;

    public PollingPlan CurrentPlan { get; private set; } = PollingPlan.Empty;

    /// <summary>Register a demand. Dispose the result to withdraw it.</summary>
    public ISignalSubscription Declare(string signalId, SignalPriority priority, double rateHz)
    {
        if (!_catalog.TryGet(signalId, out var definition))
        {
            throw new ArgumentException(
                $"Signal '{signalId}' is not in the catalog. Add a definition rather than " +
                "reaching for a PID directly.",
                nameof(signalId));
        }

        if (rateHz <= 0)
        {
            rateHz = definition.DefaultRateHz;
        }

        // A derived signal is never asked of the truck (ADR-0041): whatever derives it holds its
        // own declarations for its inputs. Declaring it costs no budget and is always granted.
        if (definition.IsDerived)
        {
            return new DerivedDeclaration(signalId, priority, rateHz);
        }

        var declaration = new Declaration(this, signalId, priority, rateHz);

        lock (_sync)
        {
            _declarations.Add(declaration);
        }

        Replan();
        return declaration;
    }

    private void Withdraw(Declaration declaration)
    {
        lock (_sync)
        {
            _declarations.Remove(declaration);
        }

        Replan();
    }

    private void Replan()
    {
        List<Declaration> snapshot;
        lock (_sync)
        {
            snapshot = [.. _declarations];
        }

        // Merge duplicates: two components wanting the same signal produce one poll, at the
        // higher rate and the higher priority.
        var merged = snapshot
            .GroupBy(d => d.SignalId, StringComparer.Ordinal)
            .Select(g => new
            {
                Definition = _catalog[g.Key],
                Priority = g.Max(d => d.Priority),
                RateHz = g.Max(d => d.RequestedRateHz),
            })
            .OrderByDescending(x => x.Priority)
            .ThenByDescending(x => x.RateHz)
            .ToList();

        var demand = merged.Sum(x => x.RateHz);
        var entries = new List<PlanEntry>(merged.Count);
        var remaining = BudgetHz;

        foreach (var tier in merged.GroupBy(x => x.Priority).OrderByDescending(g => g.Key))
        {
            var tierDemand = tier.Sum(x => x.RateHz);

            if (tierDemand <= remaining + 1e-9)
            {
                entries.AddRange(tier.Select(x => new PlanEntry(x.Definition, x.Priority, x.RateHz)));
                remaining -= tierDemand;
                continue;
            }

            // This tier does not fit. Scale it proportionally rather than dropping members
            // arbitrarily, so every signal in the tier degrades together.
            var factor = remaining > 0 ? remaining / tierDemand : 0;
            entries.AddRange(tier.Select(x => new PlanEntry(x.Definition, x.Priority, x.RateHz * factor)));
            remaining = 0;
        }

        var plan = new PollingPlan(entries, BudgetHz, demand);
        CurrentPlan = plan;

        // Tell every declaration what it actually got.
        var allocated = entries.ToDictionary(e => e.Signal.Id, e => e.RateHz, StringComparer.Ordinal);
        foreach (var declaration in snapshot)
        {
            declaration.SetEffectiveRate(allocated.GetValueOrDefault(declaration.SignalId));
        }

        PlanChanged?.Invoke(plan);
    }

    /// <summary>A declaration of a derived signal: granted in full, and nothing to withdraw.</summary>
    private sealed class DerivedDeclaration(string signalId, SignalPriority priority, double rateHz) : ISignalSubscription
    {
        public string SignalId { get; } = signalId;

        public SignalPriority Priority { get; } = priority;

        public double RequestedRateHz { get; } = rateHz;

        public double EffectiveRateHz => RequestedRateHz;

        public event Action<double>? EffectiveRateChanged
        {
            add { }
            remove { }
        }

        public void Dispose()
        {
        }
    }

    private sealed class Declaration(
        RequestArbiter owner, string signalId, SignalPriority priority, double rateHz)
        : ISignalSubscription
    {
        private bool _disposed;

        public string SignalId { get; } = signalId;

        public SignalPriority Priority { get; } = priority;

        public double RequestedRateHz { get; } = rateHz;

        public double EffectiveRateHz { get; private set; }

        public event Action<double>? EffectiveRateChanged;

        public void SetEffectiveRate(double rate)
        {
            if (Math.Abs(rate - EffectiveRateHz) < 1e-9)
            {
                return;
            }

            EffectiveRateHz = rate;
            EffectiveRateChanged?.Invoke(rate);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner.Withdraw(this);
        }
    }
}

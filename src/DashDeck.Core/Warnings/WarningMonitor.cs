using DashDeck.Abstractions;

namespace DashDeck.Core.Warnings;

/// <summary>What happened to a warning on one beat.</summary>
public enum WarningChange
{
    /// <summary>It came on, and has not been dismissed.</summary>
    Raised,

    /// <summary>It was dismissed while on, and something new — a new trouble code — brought it back.</summary>
    Reraised,

    /// <summary>It went off.</summary>
    Cleared,
}

/// <summary>One warning's state now.</summary>
public sealed record WarningState(
    WarningDefinition Definition,
    bool IsLit,
    bool IsDismissed,
    DateTimeOffset? LitSince,
    double? Count)
{
    public string Id => Definition.Id;

    /// <summary>Lit, and the driver has not said they have seen it.</summary>
    public bool NeedsAttention => IsLit && !IsDismissed;
}

/// <summary>
/// Watches the warning lights and decides when one interrupts the screen (ADR-0055).
/// </summary>
/// <remarks>
/// Fed on the shell's one-second beat with what the bus says now; it asks nothing of the truck
/// itself. The rules:
/// <list type="bullet">
/// <item>A light is lit after reading lit for its hold time, and off after reading off as long —
/// a voltage dip while cranking, or one odd answer, never changes it.</item>
/// <item>Only a <see cref="SignalQuality.Live"/> or <see cref="SignalQuality.Simulated"/> reading is
/// evidence. Stale or Unavailable — the ignition off, the adapter lost, a placeholder on a real
/// truck — changes nothing either way: a light is never claimed off for want of an answer.</item>
/// <item>Dismissed stays dismissed while the light stays on. It pops again when the light goes off
/// and comes back, or when its count (stored trouble codes) rises past where it was dismissed.</item>
/// <item>A dismissal outlives the program (<see cref="Dismissals"/>, <see cref="Restore"/>): a
/// check-engine light that has been on for a week does not pop at every start.</item>
/// </list>
/// </remarks>
public sealed class WarningMonitor
{
    /// <summary>Moving, for a <see cref="WarningDefinition.WhenMoving"/> warning: above walking pace.</summary>
    public const double MovingKph = 5;

    private readonly Lock _gate = new();
    private readonly List<Entry> _entries;

    private sealed class Entry(WarningDefinition definition)
    {
        public WarningDefinition Definition { get; } = definition;
        public bool Lit;
        public bool Dismissed;
        public double? DismissedCount;
        public DateTimeOffset? LitSince;
        public DateTimeOffset? OnSince;
        public DateTimeOffset? OffSince;
        public double? Count;
    }

    public WarningMonitor(IEnumerable<WarningDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _entries = [.. definitions.Select(d => new Entry(d))];
    }

    public IReadOnlyList<WarningDefinition> Definitions => [.. _entries.Select(e => e.Definition)];

    /// <summary>Every signal the monitor reads: each light's, and each count.</summary>
    public IReadOnlyList<string> Signals =>
        [.. _entries.SelectMany(e => new[] { e.Definition.Signal, e.Definition.CountSignal }).OfType<string>().Distinct()];

    /// <summary>Every warning's state now, in file order.</summary>
    public IReadOnlyList<WarningState> States
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Select(State)];
            }
        }
    }

    /// <summary>
    /// Look at the lights. <paramref name="read"/> gives each signal's value now; <paramref name="speedKph"/>
    /// is the truck's speed, or null when it is not known (a when-moving warning then waits).
    /// </summary>
    public IReadOnlyList<(WarningState State, WarningChange Change)> Update(
        DateTimeOffset now,
        Func<string, SignalValue> read,
        double? speedKph)
    {
        ArgumentNullException.ThrowIfNull(read);
        var changes = new List<(WarningState, WarningChange)>();

        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                if (Step(entry, now, read, speedKph) is { } change)
                {
                    changes.Add((State(entry), change));
                }
            }
        }

        return changes;
    }

    private static WarningChange? Step(Entry entry, DateTimeOffset now, Func<string, SignalValue> read, double? speedKph)
    {
        var definition = entry.Definition;
        var hold = TimeSpan.FromSeconds(definition.HoldSeconds);

        if (definition.CountSignal is { } countSignal && read(countSignal) is { IsUsable: true } count && !double.IsNaN(count.Value))
        {
            entry.Count = count.Value;

            // Dismissed before the count was known: it counts from the first one heard.
            if (entry.Dismissed && entry.DismissedCount is null)
            {
                entry.DismissedCount = count.Value;
            }
        }

        var value = read(definition.Signal);
        bool? lit = value.IsUsable && !double.IsNaN(value.Value) ? definition.IsLit(value.Value) : null;

        if (lit is true && definition.WhenMoving)
        {
            lit = speedKph is { } speed ? speed > MovingKph : null;
        }

        if (lit is null)
        {
            // No evidence: nothing changes, and neither run of readings carries on past the gap.
            entry.OnSince = null;
            entry.OffSince = null;
            return null;
        }

        if (lit.Value)
        {
            entry.OffSince = null;
            entry.OnSince ??= now;

            if (!entry.Lit && now - entry.OnSince >= hold)
            {
                entry.Lit = true;
                entry.LitSince = now;
                return entry.Dismissed ? null : WarningChange.Raised;
            }

            if (entry.Lit && entry.Dismissed && entry.Count is { } c && entry.DismissedCount is { } at && c > at)
            {
                entry.Dismissed = false;
                entry.DismissedCount = null;
                return WarningChange.Reraised;
            }

            return null;
        }

        entry.OnSince = null;
        entry.OffSince ??= now;

        if (now - entry.OffSince >= hold)
        {
            // Seen off: whatever was dismissed is forgotten, so the next time it comes on it pops.
            var wasLit = entry.Lit;
            entry.Lit = false;
            entry.LitSince = null;
            entry.Dismissed = false;
            entry.DismissedCount = null;
            return wasLit ? WarningChange.Cleared : null;
        }

        return null;
    }

    /// <summary>The driver has seen it: no more popping until it goes off and on, or its count rises.</summary>
    public void Dismiss(string id)
    {
        lock (_gate)
        {
            if (_entries.FirstOrDefault(e => e.Definition.Id == id) is { } entry)
            {
                entry.Dismissed = true;
                entry.DismissedCount = entry.Count;
            }
        }
    }

    /// <summary>The warnings dismissed now, and the count each was dismissed at — what to keep between launches.</summary>
    public IReadOnlyDictionary<string, double?> Dismissals
    {
        get
        {
            lock (_gate)
            {
                return _entries.Where(e => e.Dismissed).ToDictionary(e => e.Definition.Id, e => e.DismissedCount);
            }
        }
    }

    /// <summary>
    /// Dismissals kept from the last run. Each holds until its light is seen off, exactly as if it had
    /// been dismissed in this one.
    /// </summary>
    public void Restore(IReadOnlyDictionary<string, double?> dismissals)
    {
        ArgumentNullException.ThrowIfNull(dismissals);
        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                if (dismissals.TryGetValue(entry.Definition.Id, out var count))
                {
                    entry.Dismissed = true;
                    entry.DismissedCount = count;
                }
            }
        }
    }

    /// <summary>
    /// The warning that should be on screen: lit, not dismissed, and wanted as a popup — red before
    /// amber, then the newest.
    /// </summary>
    public WarningState? Alert(Func<WarningDefinition, bool> popup)
    {
        ArgumentNullException.ThrowIfNull(popup);
        return States
            .Where(s => s.NeedsAttention && popup(s.Definition))
            .OrderByDescending(s => s.Definition.Severity)
            .ThenByDescending(s => s.LitSince)
            .FirstOrDefault();
    }

    private static WarningState State(Entry e) => new(e.Definition, e.Lit, e.Dismissed, e.LitSince, e.Count);
}

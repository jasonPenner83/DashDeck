using System.Globalization;

namespace DashDeck.Simulator;

/// <summary>
/// Faults set on the synthetic truck by hand — trouble codes, and any quantity held at a value — so
/// a warning can be raised at a desk (ADR-0055). <c>--fault</c> on the dash's command line.
/// </summary>
/// <remarks>
/// Each fault can start and stop at a time into the drive (<c>@from-to</c>, seconds), so a light can
/// go off and come back for testing dismissal. Clearing the codes (mode 04) clears the codes set so
/// far; one set to start later still appears, as a fault that is really there comes back.
/// </remarks>
public sealed class SimulatedFaults
{
    private readonly Lock _gate = new();
    private readonly List<Fault> _faults = [];
    private double? _clearedAt;

    private sealed record Fault(string? Code, bool Pending, string? Name, double Value, double From, double? Until);

    /// <summary>Times codes were cleared — the simulator's own record of mode 04.</summary>
    public int ClearCount { get; private set; }

    /// <summary>
    /// Add a fault from its text: <c>P0420</c> (stored), <c>P0171/pending</c>, <c>warning.oilPressure</c>
    /// (held at 1), <c>engine.coolantTemp=118</c>; each optionally <c>@30</c> or <c>@30-90</c>.
    /// </summary>
    /// <returns>Null when added, or why it could not be read.</returns>
    public string? Add(string text)
    {
        var t = (text ?? "").Trim();
        double from = 0;
        double? until = null;

        var at = t.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            var times = t[(at + 1)..].Split('-', 2);
            if (!double.TryParse(times[0], NumberStyles.Float, CultureInfo.InvariantCulture, out from) ||
                (times.Length == 2 && !double.TryParse(times[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var to)))
            {
                return $"'{text}': the time after @ is seconds, or seconds-seconds.";
            }

            until = times.Length == 2 ? double.Parse(times[1], CultureInfo.InvariantCulture) : null;
            t = t[..at];
        }

        var pending = t.EndsWith("/pending", StringComparison.OrdinalIgnoreCase);
        if (pending)
        {
            t = t[..^"/pending".Length];
        }

        if (t.Length == 5 && "PCBU".Contains(char.ToUpperInvariant(t[0]), StringComparison.Ordinal) && t[1..].All(Uri.IsHexDigit))
        {
            lock (_gate)
            {
                _faults.Add(new Fault(t.ToUpperInvariant(), pending, null, 0, from, until));
            }

            return null;
        }

        var eq = t.IndexOf('=', StringComparison.Ordinal);
        var name = eq >= 0 ? t[..eq] : t;
        var value = 1.0;
        if (name.Length == 0 || (eq >= 0 && !double.TryParse(t[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out value)))
        {
            return $"'{text}': a fault is a code (P0420, P0171/pending) or a name, optionally =value.";
        }

        lock (_gate)
        {
            _faults.Add(new Fault(null, false, name, value, from, until));
        }

        return null;
    }

    /// <summary>The codes standing at a time into the drive, stored or pending.</summary>
    public IReadOnlyList<string> Codes(double elapsed, bool pending)
    {
        lock (_gate)
        {
            return [.. _faults
                .Where(f => f.Code is not null && f.Pending == pending && Active(f, elapsed) && (_clearedAt is not { } c || f.From > c))
                .Select(f => f.Code!)];
        }
    }

    /// <summary>A quantity held by a fault at this time, if one is.</summary>
    public bool TryHeld(string name, double elapsed, out double value)
    {
        lock (_gate)
        {
            var fault = _faults.LastOrDefault(f => f.Name == name && Active(f, elapsed));
            value = fault?.Value ?? 0;
            return fault is not null;
        }
    }

    /// <summary>Mode 04: the codes standing now are gone.</summary>
    public void ClearCodes(double elapsed)
    {
        lock (_gate)
        {
            _clearedAt = elapsed;
            ClearCount++;
        }
    }

    private static bool Active(Fault f, double elapsed) => elapsed >= f.From && (f.Until is not { } u || elapsed < u);
}

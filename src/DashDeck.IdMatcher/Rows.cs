using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DashDeck.Core.Discovery.Matching;

namespace DashDeck.IdMatcher;

/// <summary>One identifier in the list, refreshed from what the tap has heard.</summary>
public sealed partial class IdentifierRow : ObservableObject
{
    public IdentifierRow(IdentifierStats stats) => Stats = stats;

    public IdentifierStats Stats { get; }

    public IdentifierKey Key => Stats.Key;

    public string Module => Key.ModuleText ?? "7DF";

    public string Bus => Key.BusText;

    public string Mode => Key.Mode.ToString("X2", CultureInfo.InvariantCulture);

    public string Pid => Key.PidText;

    [ObservableProperty]
    private string _raw = "";

    [ObservableProperty]
    private string _numbers = "";

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private int _changes;

    /// <summary>Heard for the first time in the last ten seconds: what FORScan just started asking.</summary>
    [ObservableProperty]
    private bool _isNew;

    /// <summary>Its answer changed in the last three seconds.</summary>
    [ObservableProperty]
    private bool _isMoving;

    [ObservableProperty]
    private string _matchedName = "";

    private int _lastChanges;
    private DateTimeOffset _lastMoved;

    public void Refresh(DateTimeOffset now)
    {
        var payload = Stats.LastPayload;
        Raw = Convert.ToHexString(payload);
        Numbers = payload.Length switch
        {
            0 => "",
            1 => payload[0].ToString(CultureInfo.InvariantCulture),
            _ => string.Join("  ", payload.Select(b => b.ToString(CultureInfo.InvariantCulture)))
                 + $"   (A·256+B = {(payload[0] << 8) | payload[1]})",
        };
        Count = Stats.Count;

        if (Stats.Changes != _lastChanges)
        {
            _lastChanges = Stats.Changes;
            _lastMoved = now;
        }

        Changes = Stats.Changes;
        IsNew = now - Stats.FirstSeen < TimeSpan.FromSeconds(10);
        IsMoving = now - _lastMoved < TimeSpan.FromSeconds(3) && Stats.Changes > 0;
    }
}

/// <summary>A value typed as FORScan showed it.</summary>
public sealed record SampleRow(Sample Sample, string Shown, string Raw)
{
    public string Time => Sample.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>A candidate scaling, with what it reads now.</summary>
public sealed record CandidateRow(ScalingCandidate Candidate, string ReadsNow, string Unit)
{
    public string Formula => Candidate.Formula;

    public string Evidence => Candidate.Fitted
        ? $"{Candidate.DistinctRaw} raw values · fitted"
        : $"{Candidate.DistinctRaw} raw value{(Candidate.DistinctRaw == 1 ? "" : "s")}";

    public string Error => Candidate.WorstError.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>A logged column and its best match.</summary>
public sealed partial class LogRow : ObservableObject
{
    public LogRow(ColumnMatch match, string unit)
    {
        Match = match;
        Unit = unit;
        _accept = match.Best is { Scaling.R2: >= 0.98 };
    }

    public ColumnMatch Match { get; }

    public string Unit { get; }

    public string Column => Match.Column.Unit is { } u ? $"{Match.Column.Name} ({u})" : Match.Column.Name;

    public string Identifier => Match.Best?.Key.ToString() ?? "—";

    public string Formula => Match.Best?.Scaling.Formula ?? Match.Problem ?? "";

    public string Fit => Match.Best?.Scaling.R2 is { } r2 ? r2.ToString("0.0000", CultureInfo.InvariantCulture) : "";

    public string RunnersUp => string.Join("   ", Match.Candidates.Skip(1).Select(c => $"{c.Key.Mode:X2} {c.Key.PidText} @{c.Key.ModuleText ?? "7DF"} R²={c.Scaling.R2:0.000}"));

    public bool CanAccept => Match.Best is not null;

    [ObservableProperty]
    private bool _accept;
}

/// <summary>An accepted match in the bottom list.</summary>
public sealed record AcceptedRow(AcceptedMatch Match)
{
    public string Identifier => Match.Key.ToString();

    public string Name => Match.Name;

    public string Formula => $"{Match.Scaling.Formula} {Match.Unit}";

    public string Evidence => Match.Evidence;
}

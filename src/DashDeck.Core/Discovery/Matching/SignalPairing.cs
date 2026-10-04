using DashDeck.Core.Catalog;

namespace DashDeck.Core.Discovery.Matching;

/// <summary>Where a DashDeck signal stands: does it have the truck's identifier yet?</summary>
public enum PairingStatus
{
    /// <summary>A stand-in mode and PID (<see cref="SignalDefinition.Placeholder"/>): needs an ID.</summary>
    Placeholder,

    /// <summary>A standard PID this truck's supported-PID answers say it does not have: needs a Ford ID.</summary>
    NotOnThisTruck,

    /// <summary>A standard PID the truck has, or one not yet asked about.</summary>
    Standard,

    /// <summary>Found and confirmed: in the vehicle pack.</summary>
    Pack,

    /// <summary>In the user's overlay (<c>signals.user.json</c>) — paired here, or edited in Settings.</summary>
    Yours,

    /// <summary>Typed by hand, not measured: waits for TEST on the truck before the dash offers it (ADR-0051).</summary>
    Unconfirmed,
}

/// <summary>One DashDeck signal, and where it stands.</summary>
/// <param name="BuiltIn">The built-in definition under it (standard or pack), or null for one of yours.</param>
public sealed record SignalStanding(SignalDefinition Definition, PairingStatus Status, SignalDefinition? BuiltIn = null)
{
    /// <summary>Hidden by the person (ADR-0051).</summary>
    public bool Hidden => Definition.Hidden;

    /// <summary>True when it still needs the truck's identifier.</summary>
    public bool NeedsId => Status is PairingStatus.Placeholder or PairingStatus.NotOnThisTruck;
}

/// <summary>
/// DashDeck's signals as the ID matcher manages them: which still need the truck's identifier, and
/// pairing a match to one (ADR-0050).
/// </summary>
/// <remarks>
/// <b>The same layers the dash reads</b> — standard, then the vehicle pack, then the user's overlay —
/// so what the matcher calls paired is what the dash will use. Pairing writes to the overlay, never to
/// a shipped file: on the tablet, the dash reads it at its next launch; for the repository, the export
/// is a vehicle pack entry, which TEST confirms first (ADR-0032).
/// </remarks>
public static class SignalPairing
{
    /// <summary>Every signal and its standing, needing-an-ID first, then by category and name.</summary>
    public static IReadOnlyList<SignalStanding> Stand(
        SignalCatalog standard,
        IEnumerable<VehiclePack> packs,
        IEnumerable<SignalDefinition> overlay,
        IReadOnlySet<byte>? supportedPids = null)
    {
        var fromPack = packs.SelectMany(p => p.Signals).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.Last());
        var fromOverlay = overlay.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.Last());

        var ids = standard.Definitions.Select(d => d.Id)
            .Concat(fromPack.Keys)
            .Concat(fromOverlay.Keys)
            .Distinct();

        var result = new List<SignalStanding>();

        foreach (var id in ids)
        {
            SignalDefinition? builtIn = fromPack.TryGetValue(id, out var packed)
                ? packed
                : standard.TryGet(id, out var shipped) ? shipped : null;

            var definition = fromOverlay.TryGetValue(id, out var yours) ? yours : builtIn!;
            var status =
                definition.Placeholder ? PairingStatus.Placeholder
                : definition.Unconfirmed ? PairingStatus.Unconfirmed
                : yours is not null && (builtIn is null || yours with { Hidden = false } != builtIn) ? PairingStatus.Yours
                : builtIn is not null && fromPack.ContainsKey(id) ? PairingStatus.Pack
                : supportedPids is not null && IsStandardMode01(definition) && !supportedPids.Contains((byte)definition.Pid)
                    ? PairingStatus.NotOnThisTruck
                    : PairingStatus.Standard;

            result.Add(new SignalStanding(definition, status, builtIn));
        }

        return [.. result
            .OrderBy(s => s.NeedsId ? 0 : 1)
            .ThenBy(s => s.Definition.Category, StringComparer.Ordinal)
            .ThenBy(s => s.Definition.Name, StringComparer.Ordinal)];
    }

    private static bool IsStandardMode01(SignalDefinition d) =>
        d.Mode == 0x01 && d.Bus == Abstractions.CanBus.Hs && d.Module is null && d.Pid is > 0 and <= 0xFF && d.Pid % 0x20 != 0;

    /// <summary>
    /// The PIDs the engine computer says it supports, from its answers to <c>01 00</c>, <c>01 20</c>…
    /// as FORScan asks them on connecting. Null until the first of them has been heard.
    /// </summary>
    public static IReadOnlySet<byte>? SupportedPids(IdentifierTable table)
    {
        HashSet<byte>? supported = null;

        for (var basePid = 0; basePid <= 0xC0; basePid += 0x20)
        {
            var key = new IdentifierKey(Abstractions.CanBus.Hs, IdentifierKey.Broadcast, 0x01, (ushort)basePid);
            if (table.Find(key) is not { } stats || stats.LastPayload.Length < 4)
            {
                continue;
            }

            supported ??= [];
            var bits = stats.LastPayload;

            for (var bit = 0; bit < 32; bit++)
            {
                if ((bits[bit / 8] & (0x80 >> (bit % 8))) != 0)
                {
                    supported.Add((byte)(basePid + bit + 1));
                }
            }
        }

        return supported;
    }

    /// <summary>
    /// A match as a definition: the target's id, name, category, range and rate, with the truck's
    /// bus, module, mode, PID and decode — in the target's unit when that is a conversion away.
    /// </summary>
    public static SignalDefinition Pair(AcceptedMatch match, SignalDefinition? target)
    {
        var scaling = match.Scaling;
        var unit = match.Unit;
        var (scale, offset) = (scaling.Scale, scaling.Offset);

        if (target is not null && FromMetric(match.Unit, target.Decode.Unit) is { } convert)
        {
            (scale, offset) = (scale * convert.Factor, (offset * convert.Factor) + convert.Add);
            unit = target.Decode.Unit;
        }
        else if (target is not null && unit.Length == 0)
        {
            // Matched as a bare number (a switch, a count): it is in whatever unit the signal says.
            unit = target.Decode.Unit;
        }

        var decode = new DecodeSpec(scaling.Window.Offset, scaling.Window.Length, scaling.Window.Signed, scale, offset, unit);

        return new SignalDefinition
        {
            Id = target?.Id ?? $"ford.{MatchExport.Slug(match.Name)}",
            Name = target?.Name ?? match.Name,
            Category = target?.Category ?? "Other",
            Bus = match.Key.Bus,
            Mode = match.Key.Mode,
            Pid = match.Key.Pid,
            Module = match.Key.ModuleText,
            Decode = decode,
            DefaultRateHz = target?.DefaultRateHz ?? 1,
            Min = target?.Min,
            Max = target?.Max,
        };
    }

    /// <summary>How to turn a metric value into <paramref name="target"/>'s unit, when it is a conversion away.</summary>
    public static (double Factor, double Add)? FromMetric(string metric, string target)
    {
        if (string.Equals(metric, target, StringComparison.Ordinal))
        {
            return null;
        }

        return (metric, target) switch
        {
            ("°C", "°F") => (9.0 / 5, 32),
            ("kPa", "psi") => (1 / 6.894757, 0),
            ("kPa", "bar") => (0.01, 0),
            ("km/h", "mph") => (1 / 1.609344, 0),
            ("L/h", "gal/h") => (1 / 3.785411784, 0),
            ("L", "gal") => (1 / 3.785411784, 0),
            ("km", "mi") => (1 / 1.609344, 0),
            _ => null,
        };
    }

    /// <summary>True when a match in <paramref name="metric"/> can fill a signal in <paramref name="target"/>.</summary>
    public static bool UnitsAgree(string metric, string target) =>
        string.Equals(metric, target, StringComparison.Ordinal) || FromMetric(metric, target) is not null
        || metric.Length == 0 || target.Length == 0;
}

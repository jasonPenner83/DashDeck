using System.Globalization;
using System.Text.RegularExpressions;

namespace DashDeck.Core.Discovery.Hunt;

/// <summary>One signal in a CAN database: where its bits are in a frame, and how they become a value.</summary>
/// <param name="Name">Its name in the file, e.g. <c>EngOil_Te_Actl</c>.</param>
/// <param name="StartBit">The DBC start bit: the least significant bit for Intel order, the most significant for Motorola.</param>
/// <param name="Length">How many bits.</param>
/// <param name="BigEndian">Motorola byte order (<c>@0</c>) rather than Intel (<c>@1</c>).</param>
/// <param name="Signed">Two's complement.</param>
/// <param name="Scale">Multiplied first.</param>
/// <param name="Offset">Then added.</param>
/// <param name="Unit">What the value is in.</param>
public sealed record CanSignal(string Name, int StartBit, int Length, bool BigEndian, bool Signed, double Scale, double Offset, string Unit)
{
    /// <summary>Text for some raw values, from the file's <c>VAL_</c> table: 1 → "Ajar".</summary>
    public IReadOnlyDictionary<long, string> Values { get; init; } = new Dictionary<long, string>();

    /// <summary>The raw number in a frame's data, or null when the frame is too short for it.</summary>
    public long? Raw(byte[] data)
    {
        if (Length is < 1 or > 64)
        {
            return null;
        }

        ulong value = 0;

        if (BigEndian)
        {
            // Motorola: the start bit is the most significant, and the bits run down within a
            // byte, then on to the most significant bit of the next byte.
            var position = StartBit;
            for (var i = 0; i < Length; i++)
            {
                var index = position / 8;
                if (index >= data.Length)
                {
                    return null;
                }

                value = (value << 1) | (uint)((data[index] >> (position % 8)) & 1);
                position = position % 8 == 0 ? position + 15 : position - 1;
            }
        }
        else
        {
            for (var i = 0; i < Length; i++)
            {
                var position = StartBit + i;
                var index = position / 8;
                if (index >= data.Length)
                {
                    return null;
                }

                value |= (ulong)(uint)((data[index] >> (position % 8)) & 1) << i;
            }
        }

        if (Signed && Length < 64 && (value & (1UL << (Length - 1))) != 0)
        {
            return (long)value - (1L << Length);
        }

        return (long)value;
    }

    /// <summary>The value in its unit, or null when the frame is too short.</summary>
    public double? Decode(byte[] data) => Raw(data) is { } raw ? (raw * Scale) + Offset : null;

    /// <summary>The value as a person reads it: number and unit, and the file's text for it if it has one.</summary>
    public string Describe(byte[] data)
    {
        if (Raw(data) is not { } raw)
        {
            return "—";
        }

        var value = ((raw * Scale) + Offset).ToString("0.###", CultureInfo.InvariantCulture);
        return Values.TryGetValue(raw, out var text) ? $"{value} ({text})" : $"{value} {Unit}".TrimEnd();
    }

    /// <summary>Where the bits are, for the findings: <c>start 15 len 8 BE</c>.</summary>
    public string Layout => string.Create(CultureInfo.InvariantCulture,
        $"start {StartBit} len {Length} {(BigEndian ? "BE" : "LE")}{(Signed ? " signed" : "")}");

    /// <summary>How the raw number becomes the value: <c>× 0.25 + −40 °C</c>.</summary>
    public string Scaling => string.Create(CultureInfo.InvariantCulture, $"× {Scale:0.######} + {Offset:0.###} {Unit}").TrimEnd();
}

/// <summary>One message in a CAN database: an identifier and the signals its frame carries.</summary>
public sealed record CanMessage(uint Id, string Name, int Length, string Sender, IReadOnlyList<CanSignal> Signals)
{
    /// <summary>The identifier as the adapter prints it.</summary>
    public string IdText => Id > 0x7FF ? Id.ToString("X8", CultureInfo.InvariantCulture) : Id.ToString("X3", CultureInfo.InvariantCulture);

    /// <summary>Longer than a classic CAN frame: CAN FD only, which this adapter cannot hear.</summary>
    public bool CanFdOnly => Length > 8;
}

/// <summary>
/// A CAN database — a <c>.dbc</c> file, or the same text as <c>.dbcx</c> — read for its messages and
/// signals, so the ID hunter can check them against the truck (ADR-0044).
/// </summary>
/// <remarks>
/// Only what checking needs is read: messages (<c>BO_</c>), signals (<c>SG_</c>) and value tables
/// (<c>VAL_</c>). A file is the person's own and stays on their tablet: what it says is a lead, and
/// only what the truck confirms is kept.
/// </remarks>
public sealed class CanDatabase
{
    private static readonly Regex Message = new(@"^BO_\s+(\d+)\s+(\w+)\s*:\s*(\d+)\s+(\S+)", RegexOptions.Compiled);

    private static readonly Regex Signal = new(
        @"^\s*SG_\s+(\w+)\s*(?:\w+\s*)?:\s*(\d+)\|(\d+)@([01])([+-])\s*\(\s*([^,\s]+)\s*,\s*([^)\s]+)\s*\)\s*\[[^\]]*\]\s*""([^""]*)""",
        RegexOptions.Compiled);

    private static readonly Regex ValueTable = new(@"^VAL_\s+(\d+)\s+(\w+)\s+(.*);", RegexOptions.Compiled);

    private static readonly Regex ValueEntry = new(@"(-?\d+)\s+""([^""]*)""", RegexOptions.Compiled);

    private CanDatabase(IReadOnlyList<CanMessage> messages, int skipped)
    {
        Messages = messages;
        Skipped = skipped;
    }

    public IReadOnlyList<CanMessage> Messages { get; }

    /// <summary>Signal lines that could not be read — counted, not fatal.</summary>
    public int Skipped { get; }

    public int SignalCount => Messages.Sum(m => m.Signals.Count);

    public static CanDatabase Load(string path) => Parse(File.ReadAllText(path, System.Text.Encoding.Latin1));

    public static CanDatabase Parse(string text)
    {
        var messages = new List<(uint Id, string Name, int Length, string Sender, List<CanSignal> Signals)>();
        var values = new Dictionary<(uint, string), Dictionary<long, string>>();
        var skipped = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (Message.Match(line) is { Success: true } m)
            {
                var id = uint.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                messages.Add((id & 0x1FFFFFFF, m.Groups[2].Value, int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), m.Groups[4].Value, []));
                continue;
            }

            if (line.TrimStart().StartsWith("SG_ ", StringComparison.Ordinal))
            {
                if (messages.Count > 0 && Signal.Match(line) is { Success: true } s &&
                    double.TryParse(s.Groups[6].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) &&
                    double.TryParse(s.Groups[7].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var offset))
                {
                    messages[^1].Signals.Add(new CanSignal(
                        s.Groups[1].Value,
                        int.Parse(s.Groups[2].Value, CultureInfo.InvariantCulture),
                        int.Parse(s.Groups[3].Value, CultureInfo.InvariantCulture),
                        s.Groups[4].Value == "0",
                        s.Groups[5].Value == "-",
                        scale,
                        offset,
                        s.Groups[8].Value));
                }
                else
                {
                    skipped++;
                }

                continue;
            }

            if (ValueTable.Match(line) is { Success: true } v)
            {
                var table = new Dictionary<long, string>();
                foreach (Match entry in ValueEntry.Matches(v.Groups[3].Value))
                {
                    table[long.Parse(entry.Groups[1].Value, CultureInfo.InvariantCulture)] = entry.Groups[2].Value;
                }

                values[(uint.Parse(v.Groups[1].Value, CultureInfo.InvariantCulture) & 0x1FFFFFFF, v.Groups[2].Value)] = table;
            }
        }

        return new CanDatabase(
            [
                .. messages.Select(m => new CanMessage(
                    m.Id,
                    m.Name,
                    m.Length,
                    m.Sender,
                    [.. m.Signals.Select(s => values.TryGetValue((m.Id, s.Name), out var t) ? s with { Values = t } : s)])),
            ],
            skipped);
    }

    /// <summary>
    /// Signals whose name, or whose message's name, contains any of the words — case aside. Words
    /// are separated by spaces or commas: <c>oil tire</c>.
    /// </summary>
    public IReadOnlyList<(CanMessage Message, CanSignal Signal)> Search(string words)
    {
        var terms = words.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        return
        [
            .. from m in Messages
               from s in m.Signals
               where terms.Any(t => s.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                                    m.Name.Contains(t, StringComparison.OrdinalIgnoreCase))
               orderby m.Id, s.StartBit
               select (m, s),
        ];
    }
}

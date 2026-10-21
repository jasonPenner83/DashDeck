using System.Globalization;
using DashDeck.Core.Dbc;

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
    public long? Raw(byte[] data) => DbcBits.Raw(data, StartBit, Length, BigEndian, Signed);

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
/// Read by <see cref="DbcReader"/> (ADR-0073), and kept to what checking needs: messages, their
/// signals and value tables. A file is the person's own and stays on their tablet: what it says is
/// a lead, and only what the truck confirms is kept.
/// </remarks>
public sealed class CanDatabase
{
    private CanDatabase(IReadOnlyList<CanMessage> messages, int skipped)
    {
        Messages = messages;
        Skipped = skipped;
    }

    public IReadOnlyList<CanMessage> Messages { get; }

    /// <summary>Statements that could not be read — counted, not fatal.</summary>
    public int Skipped { get; }

    public int SignalCount => Messages.Sum(m => m.Signals.Count);

    public static CanDatabase Load(string path) => From(DbcReader.Load(path));

    public static CanDatabase Parse(string text) => From(DbcReader.Read(text));

    private static CanDatabase From(DbcReadResult read) => new(
        [
            .. read.Document.Messages.Select(m => new CanMessage(
                m.Id,
                m.Name,
                m.Length,
                m.Sender,
                [
                    .. m.Signals.Select(s => new CanSignal(s.Name, s.StartBit, s.Length, s.BigEndian, s.Signed, s.Scale, s.Offset, s.Unit)
                    {
                        Values = s.Values.GroupBy(v => v.Raw).ToDictionary(g => g.Key, g => g.Last().Text),
                    }),
                ])),
        ],
        read.Problems.Count);

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

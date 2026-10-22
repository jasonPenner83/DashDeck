using System.Globalization;

namespace DashDeck.Core.Dbc;

/// <summary>One raw value's text in a signal's value table: 1 → "Ajar".</summary>
public sealed record DbcValueText(long Raw, string Text);

/// <summary>A run of switch values, inclusive, from an <c>SG_MUL_VAL_</c> line: <c>13-13</c>.</summary>
public sealed record DbcMultiplexRange(long From, long To)
{
    /// <summary>True for a single value — the only kind DashDeck's own answers use.</summary>
    public bool IsSingle => From == To;
}

/// <summary>
/// Which multiplexor a signal hangs from, and at which of its values — an extended-multiplexing
/// <c>SG_MUL_VAL_</c> line. Without one, a multiplexed signal hangs from the message's only
/// top-level multiplexor at its own <see cref="DbcSignal.MultiplexValue"/>.
/// </summary>
public sealed record DbcSwitch(string Multiplexor, IReadOnlyList<DbcMultiplexRange> Ranges);

/// <summary>
/// One signal of a message: where its bits are, how they become a value, and how it is multiplexed.
/// </summary>
/// <param name="Name">Its name, unique within the message.</param>
/// <param name="StartBit">The DBC start bit: the least significant bit for Intel order, the most significant for Motorola.</param>
/// <param name="Length">How many bits, 1 to 64.</param>
/// <param name="BigEndian">Motorola byte order (<c>@0</c>) rather than Intel (<c>@1</c>).</param>
/// <param name="Signed">Two's complement (<c>-</c>) rather than unsigned (<c>+</c>).</param>
/// <param name="Scale">Multiplied first.</param>
/// <param name="Offset">Then added.</param>
/// <param name="Minimum">The physical range's low end. <c>[0|0]</c> means the file gives none.</param>
/// <param name="Maximum">The physical range's high end.</param>
/// <param name="Unit">What the value is in.</param>
public sealed record DbcSignal(
    string Name,
    int StartBit,
    int Length,
    bool BigEndian,
    bool Signed,
    double Scale,
    double Offset,
    double Minimum,
    double Maximum,
    string Unit)
{
    /// <summary>True for a multiplexor: <c>M</c>, or <c>m13M</c> when it is itself multiplexed.</summary>
    public bool IsMultiplexor { get; init; }

    /// <summary>The value of its multiplexor it appears at — the 13 of <c>m13</c> — or null when it is always there.</summary>
    public long? MultiplexValue { get; init; }

    /// <summary>The <c>SG_MUL_VAL_</c> line naming its multiplexor, or null when the file has none for it.</summary>
    public DbcSwitch? Switch { get; init; }

    /// <summary>Who receives it. Empty is written as <c>Vector__XXX</c>, the format's "nobody in particular".</summary>
    public IReadOnlyList<string> Receivers { get; init; } = [];

    /// <summary>Its <c>CM_ SG_</c> comment, or null.</summary>
    public string? Comment { get; init; }

    /// <summary>Its <c>VAL_</c> table, in the file's order.</summary>
    public IReadOnlyList<DbcValueText> Values { get; init; } = [];

    /// <summary>The indicator between the name and the colon: <c>""</c>, <c>M</c>, <c>m13</c> or <c>m13M</c>.</summary>
    public string MultiplexIndicator =>
        (MultiplexValue is { } value ? "m" + value.ToString(CultureInfo.InvariantCulture) : "") + (IsMultiplexor ? "M" : "");

    /// <summary>True when the file gives a physical range — anything but <c>[0|0]</c>.</summary>
    public bool HasRange => Minimum != 0 || Maximum != 0;

    /// <summary>The raw number in a frame's data, or null when the frame is too short for it.</summary>
    public long? Raw(ReadOnlySpan<byte> data) => DbcBits.Raw(data, StartBit, Length, BigEndian, Signed);

    /// <summary>The value in its unit, or null when the frame is too short.</summary>
    public double? Decode(ReadOnlySpan<byte> data) => Raw(data) is { } raw ? (raw * Scale) + Offset : null;

    /// <summary>The text the value table gives a raw value, or null.</summary>
    public string? TextOf(long raw) => Values.FirstOrDefault(v => v.Raw == raw)?.Text;
}

/// <summary>One message: an identifier and the signals its frame carries.</summary>
/// <param name="RawId">The identifier exactly as the file writes it, with bit 31 set for an extended (29-bit) one.</param>
/// <param name="Name">Its name, e.g. <c>Module_7E0</c>.</param>
/// <param name="Length">Its data length in bytes; more than 8 is CAN FD.</param>
/// <param name="Sender">The node that sends it, <c>Vector__XXX</c> for none in particular.</param>
public sealed record DbcMessage(uint RawId, string Name, int Length, string Sender)
{
    /// <summary>Bit 31 of a DBC identifier: this is an extended frame.</summary>
    public const uint ExtendedFlag = 0x80000000;

    public IReadOnlyList<DbcSignal> Signals { get; init; } = [];

    /// <summary>Its <c>CM_ BO_</c> comment, or null.</summary>
    public string? Comment { get; init; }

    /// <summary>The identifier on the bus, without the extended flag.</summary>
    public uint Id => RawId & 0x1FFFFFFF;

    /// <summary>True for a 29-bit identifier.</summary>
    public bool IsExtended => (RawId & ExtendedFlag) != 0;

    /// <summary>The signal of that name, or null.</summary>
    public DbcSignal? Signal(string name) => Signals.FirstOrDefault(s => s.Name == name);
}

/// <summary>Which kind of thing an attribute belongs to: the database itself, a node, a message, a signal, an environment variable.</summary>
public enum DbcObjectKind
{
    /// <summary>The database as a whole — no keyword.</summary>
    Network,

    /// <summary><c>BU_</c>.</summary>
    Node,

    /// <summary><c>BO_</c>.</summary>
    Message,

    /// <summary><c>SG_</c>.</summary>
    Signal,

    /// <summary><c>EV_</c>.</summary>
    EnvironmentVariable,
}

/// <summary>A value as the file writes it: a number, a word, or a quoted string.</summary>
/// <param name="Text">The text, without quotes and unescaped.</param>
/// <param name="Quoted">True when it was, and is to be, written in quotes.</param>
public sealed record DbcToken(string Text, bool Quoted)
{
    public static DbcToken String(string text) => new(text, true);

    public static DbcToken Number(double value) => new(DbcNumber.Format(value), false);
}

/// <summary>A <c>BA_DEF_</c> line: an attribute's name, what it belongs to, its type and that type's arguments.</summary>
/// <param name="Type"><c>INT</c>, <c>HEX</c>, <c>FLOAT</c>, <c>STRING</c> or <c>ENUM</c>.</param>
/// <param name="Arguments">The range for a number, the names for an enum, nothing for a string.</param>
public sealed record DbcAttributeDefinition(DbcObjectKind Kind, string Name, string Type, IReadOnlyList<DbcToken> Arguments);

/// <summary>A <c>BA_</c> line: one attribute's value on one object.</summary>
/// <param name="Target">The node or environment variable's name, or the signal's name; null for the network or a message.</param>
/// <param name="RawMessageId">The message's identifier as written, for a message or a signal; otherwise null.</param>
public sealed record DbcAttributeValue(string Name, DbcObjectKind Kind, string? Target, uint? RawMessageId, DbcToken Value);

/// <summary>A named <c>VAL_TABLE_</c>, shared by any signal that names it.</summary>
public sealed record DbcValueTable(string Name, IReadOnlyList<DbcValueText> Values);

/// <summary>A statement DashDeck does not read, kept word for word so writing the file back loses nothing.</summary>
/// <param name="Keyword">Its first word: <c>BO_TX_BU_</c>, <c>EV_</c>, <c>SIG_VALTYPE_</c>…</param>
/// <param name="Text">The whole statement as it appeared, through its semicolon.</param>
public sealed record DbcVerbatim(string Keyword, string Text);

/// <summary>
/// A CAN database — the <c>.dbc</c> format — as data (ADR-0073).
/// </summary>
/// <remarks>
/// Everything a file says is either read into this or kept word for word in
/// <see cref="Verbatim"/>, so a file DashDeck reads and writes back has lost nothing, even what was
/// made in another tool. <see cref="DbcReader"/> reads one and <see cref="DbcWriter"/> writes one.
/// </remarks>
public sealed record DbcDocument
{
    /// <summary>The <c>NS_</c> list a new file is written with: what Vector's own tools write.</summary>
    public static readonly IReadOnlyList<string> StandardNewSymbols =
    [
        "NS_DESC_", "CM_", "BA_DEF_", "BA_", "VAL_", "CAT_DEF_", "CAT_", "FILTER", "BA_DEF_DEF_", "EV_DATA_",
        "ENVVAR_DATA_", "SGTYPE_", "SGTYPE_VAL_", "BA_DEF_SGTYPE_", "BA_SGTYPE_", "SIG_TYPE_REF_", "VAL_TABLE_",
        "SIG_GROUP_", "SIG_VALTYPE_", "SIGTYPE_VALTYPE_", "BO_TX_BU_", "BA_DEF_REL_", "BA_REL_", "BA_DEF_DEF_REL_",
        "BU_SG_REL_", "BU_EV_REL_", "BU_BO_REL_", "SG_MUL_VAL_",
    ];

    /// <summary>An empty database, as a new file starts.</summary>
    public static DbcDocument Empty { get; } = new() { NewSymbols = StandardNewSymbols };

    /// <summary>The <c>VERSION</c> string.</summary>
    public string Version { get; init; } = "";

    /// <summary>The <c>NS_</c> list, as read.</summary>
    public IReadOnlyList<string> NewSymbols { get; init; } = [];

    /// <summary>Whatever followed <c>BS_:</c> on its line — almost always nothing.</summary>
    public string BitTiming { get; init; } = "";

    /// <summary>The <c>BU_</c> nodes.</summary>
    public IReadOnlyList<string> Nodes { get; init; } = [];

    public IReadOnlyList<DbcValueTable> ValueTables { get; init; } = [];

    public IReadOnlyList<DbcMessage> Messages { get; init; } = [];

    /// <summary>The database's own <c>CM_</c> comment, or null.</summary>
    public string? Comment { get; init; }

    /// <summary><c>CM_ BU_</c> comments, by node.</summary>
    public IReadOnlyList<(string Node, string Comment)> NodeComments { get; init; } = [];

    public IReadOnlyList<DbcAttributeDefinition> AttributeDefinitions { get; init; } = [];

    /// <summary><c>BA_DEF_DEF_</c> defaults, by attribute name.</summary>
    public IReadOnlyList<(string Name, DbcToken Value)> AttributeDefaults { get; init; } = [];

    public IReadOnlyList<DbcAttributeValue> AttributeValues { get; init; } = [];

    /// <summary>Statements read but not understood, in the order they came.</summary>
    public IReadOnlyList<DbcVerbatim> Verbatim { get; init; } = [];

    /// <summary>The message with that identifier on the bus, or null.</summary>
    public DbcMessage? Message(uint id) => Messages.FirstOrDefault(m => m.Id == id);

    /// <summary>The database's own value of an attribute, or null when it has none.</summary>
    public DbcToken? NetworkAttribute(string name) =>
        AttributeValues.LastOrDefault(a => a.Kind == DbcObjectKind.Network && a.Name == name)?.Value;

    /// <summary>The same database with one message replaced, or added when it has no message with that raw identifier.</summary>
    public DbcDocument WithMessage(DbcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var index = Messages.ToList().FindIndex(m => m.RawId == message.RawId);
        var messages = Messages.ToList();

        if (index < 0)
        {
            messages.Add(message);
        }
        else
        {
            messages[index] = message;
        }

        return this with { Messages = messages };
    }

    /// <summary>The same database with the attribute defined, unless a definition of that name is already there.</summary>
    public DbcDocument WithDefinition(DbcAttributeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return AttributeDefinitions.Any(d => d.Name == definition.Name)
            ? this
            : this with { AttributeDefinitions = [.. AttributeDefinitions, definition] };
    }

    /// <summary>The same database with its own value of an attribute set, replacing any it had.</summary>
    public DbcDocument WithNetworkAttribute(string name, DbcToken value) =>
        this with
        {
            AttributeValues =
            [
                .. AttributeValues.Where(a => !(a.Kind == DbcObjectKind.Network && a.Name == name)),
                new DbcAttributeValue(name, DbcObjectKind.Network, null, null, value),
            ],
        };
}

/// <summary>Reading a signal's bits out of a frame, in either byte order.</summary>
public static class DbcBits
{
    /// <summary>
    /// The raw number at a DBC position, or null when the frame is too short for it or the length
    /// is not 1–64.
    /// </summary>
    /// <remarks>
    /// Motorola: the start bit is the most significant, and the bits run down within a byte, then on
    /// to the most significant bit of the next byte. Intel: the start bit is the least significant,
    /// and the bits run up through the bytes.
    /// </remarks>
    public static long? Raw(ReadOnlySpan<byte> data, int startBit, int length, bool bigEndian, bool signed)
    {
        if (length is < 1 or > 64 || startBit < 0)
        {
            return null;
        }

        ulong value = 0;
        var position = bigEndian ? startBit : 0;

        for (var i = 0; i < length; i++)
        {
            if (!bigEndian)
            {
                position = startBit + i;
            }

            var index = position / 8;
            if (index >= data.Length)
            {
                return null;
            }

            var bit = (ulong)((data[index] >> (position % 8)) & 1);

            if (bigEndian)
            {
                value = (value << 1) | bit;
                position = Next(position);
            }
            else
            {
                value |= bit << i;
            }
        }

        if (signed && length < 64 && (value & (1UL << (length - 1))) != 0)
        {
            return (long)value - (1L << length);
        }

        return (long)value;
    }

    /// <summary>The Motorola position after this one, one bit less significant.</summary>
    public static int Next(int position) => position % 8 == 0 ? position + 15 : position - 1;

    /// <summary>
    /// Each bit of a field, most significant first, as its place in the frame read big-endian — byte
    /// 0's top bit is 0, its bottom bit 7, byte 1's top bit 8. A Motorola field is always a run of
    /// these; an Intel one is only when it stays inside one byte.
    /// </summary>
    public static IEnumerable<int> BigEndianPlaces(int startBit, int length, bool bigEndian)
    {
        if (bigEndian)
        {
            var position = startBit;
            for (var i = 0; i < length; i++)
            {
                yield return Place(position);
                position = Next(position);
            }
        }
        else
        {
            for (var i = length - 1; i >= 0; i--)
            {
                yield return Place(startBit + i);
            }
        }

        static int Place(int position) => ((position / 8) * 8) + 7 - (position % 8);
    }
}

/// <summary>Numbers as a DBC file writes them.</summary>
public static class DbcNumber
{
    /// <summary>The shortest text that reads back as the same number, invariant: <c>0.25</c>, <c>-40</c>, <c>1E-05</c>.</summary>
    public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

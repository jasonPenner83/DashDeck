using System.Globalization;
using System.Text;

namespace DashDeck.Core.Dbc;

/// <summary>
/// Writes a CAN database (ADR-0073).
/// </summary>
/// <remarks>
/// In the order Vector's own tools write it, which every other tool reads. What
/// <see cref="DbcReader"/> kept word for word goes back word for word: transmitter lists and
/// environment variables before the comments, everything else at the end. Reading what this
/// writes gives the same database again.
/// </remarks>
public static class DbcWriter
{
    /// <summary>Write a file, Latin-1 as Vector's tools expect, with Windows line endings.</summary>
    public static void Save(DbcDocument document, string path) =>
        File.WriteAllText(path, Write(document).ReplaceLineEndings("\r\n"), Encoding.Latin1);

    /// <summary>A database as text, with <c>\n</c> line endings.</summary>
    public static string Write(DbcDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var text = new StringBuilder();

        text.Append("VERSION ").Append(Quote(document.Version)).Append("\n\n\n");

        text.Append("NS_ :\n");
        foreach (var symbol in document.NewSymbols)
        {
            text.Append('\t').Append(symbol).Append('\n');
        }

        text.Append('\n');
        text.Append("BS_:").Append(document.BitTiming.Length > 0 ? " " + document.BitTiming : "").Append("\n\n");
        text.Append("BU_:").Append(string.Concat(document.Nodes.Select(n => " " + n))).Append("\n");

        foreach (var table in document.ValueTables)
        {
            text.Append("VAL_TABLE_ ").Append(table.Name).Append(Values(table.Values)).Append(" ;\n");
        }

        text.Append("\n\n");

        foreach (var message in document.Messages)
        {
            text.Append(Invariant($"BO_ {message.RawId} {message.Name}: {message.Length} {message.Sender}\n"));

            foreach (var signal in message.Signals)
            {
                var indicator = signal.MultiplexIndicator.Length > 0 ? " " + signal.MultiplexIndicator : "";
                var receivers = signal.Receivers.Count > 0 ? string.Join(",", signal.Receivers) : "Vector__XXX";

                text.Append(Invariant(
                    $" SG_ {signal.Name}{indicator} : {signal.StartBit}|{signal.Length}@{(signal.BigEndian ? 0 : 1)}{(signal.Signed ? '-' : '+')}"))
                    .Append(Invariant($" ({Number(signal.Scale)},{Number(signal.Offset)}) [{Number(signal.Minimum)}|{Number(signal.Maximum)}]"))
                    .Append(' ').Append(Quote(signal.Unit)).Append(' ').Append(receivers).Append('\n');
            }

            text.Append('\n');
        }

        text.Append('\n');

        foreach (var verbatim in document.Verbatim.Where(v => DbcReader.EarlyVerbatim.Contains(v.Keyword)))
        {
            text.Append(verbatim.Text).Append('\n');
        }

        if (document.Comment is { } comment)
        {
            text.Append("CM_ ").Append(Quote(comment)).Append(";\n");
        }

        foreach (var (node, nodeComment) in document.NodeComments)
        {
            text.Append("CM_ BU_ ").Append(node).Append(' ').Append(Quote(nodeComment)).Append(";\n");
        }

        foreach (var message in document.Messages)
        {
            if (message.Comment is { } messageComment)
            {
                text.Append(Invariant($"CM_ BO_ {message.RawId} ")).Append(Quote(messageComment)).Append(";\n");
            }

            foreach (var signal in message.Signals.Where(s => s.Comment is not null))
            {
                text.Append(Invariant($"CM_ SG_ {message.RawId} {signal.Name} ")).Append(Quote(signal.Comment!)).Append(";\n");
            }
        }

        foreach (var definition in document.AttributeDefinitions)
        {
            var separator = definition.Type == "ENUM" ? "," : " ";
            var arguments = definition.Arguments.Count > 0 ? " " + string.Join(separator, definition.Arguments.Select(Token)) : "";
            text.Append("BA_DEF_ ").Append(Kind(definition.Kind)).Append(Quote(definition.Name)).Append(' ')
                .Append(definition.Type).Append(arguments).Append(";\n");
        }

        foreach (var (name, value) in document.AttributeDefaults)
        {
            text.Append("BA_DEF_DEF_ ").Append(Quote(name)).Append(' ').Append(Token(value)).Append(";\n");
        }

        foreach (var value in document.AttributeValues)
        {
            text.Append("BA_ ").Append(Quote(value.Name)).Append(' ').Append(Kind(value.Kind));

            if (value.RawMessageId is { } id)
            {
                text.Append(id.ToString(CultureInfo.InvariantCulture)).Append(' ');
            }

            if (value.Target is { } target)
            {
                text.Append(target).Append(' ');
            }

            text.Append(Token(value.Value)).Append(";\n");
        }

        foreach (var message in document.Messages)
        {
            foreach (var signal in message.Signals.Where(s => s.Values.Count > 0))
            {
                text.Append(Invariant($"VAL_ {message.RawId} {signal.Name}")).Append(Values(signal.Values)).Append(" ;\n");
            }
        }

        foreach (var message in document.Messages)
        {
            foreach (var signal in message.Signals.Where(s => s.Switch is not null))
            {
                var ranges = string.Join(", ", signal.Switch!.Ranges.Select(r => Invariant($"{r.From}-{r.To}")));
                text.Append(Invariant($"SG_MUL_VAL_ {message.RawId} {signal.Name} {signal.Switch.Multiplexor} {ranges};\n"));
            }
        }

        foreach (var verbatim in document.Verbatim.Where(v => !DbcReader.EarlyVerbatim.Contains(v.Keyword)))
        {
            text.Append(verbatim.Text).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>A string in quotes, with a quote or backslash inside escaped.</summary>
    public static string Quote(string text) =>
        "\"" + (text ?? "").Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Values(IReadOnlyList<DbcValueText> values) =>
        string.Concat(values.Select(v => " " + v.Raw.ToString(CultureInfo.InvariantCulture) + " " + Quote(v.Text)));

    private static string Token(DbcToken token) => token.Quoted ? Quote(token.Text) : token.Text;

    private static string Kind(DbcObjectKind kind) => kind switch
    {
        DbcObjectKind.Node => "BU_ ",
        DbcObjectKind.Message => "BO_ ",
        DbcObjectKind.Signal => "SG_ ",
        DbcObjectKind.EnvironmentVariable => "EV_ ",
        _ => "",
    };

    private static string Number(double value) => DbcNumber.Format(value);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

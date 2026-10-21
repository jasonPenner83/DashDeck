using System.Globalization;
using System.Text;

namespace DashDeck.Core.Dbc;

/// <summary>What reading a file made of it, and what it could not read.</summary>
/// <param name="Problems">One line per statement that could not be read: its line number and why. Not fatal.</param>
public sealed record DbcReadResult(DbcDocument Document, IReadOnlyList<string> Problems);

/// <summary>
/// Reads a CAN database (ADR-0073).
/// </summary>
/// <remarks>
/// A tokenizer and one small parser per statement, rather than a regular expression per line, because
/// the format is not line-shaped: a comment runs over as many lines as it likes, a node list can wrap,
/// and two statements can share a line. Statements it does not read are kept word for word
/// (<see cref="DbcDocument.Verbatim"/>), and one it cannot read is reported and skipped rather than
/// ending the file, so a file from any tool loads with whatever in it DashDeck can use.
/// </remarks>
public static class DbcReader
{
    /// <summary>The words a statement can start with — enough to find the next one after a bad one.</summary>
    private static readonly HashSet<string> Keywords =
    [
        "VERSION", "NS_", "BS_", "BU_", "BO_", "SG_", "CM_", "BA_DEF_", "BA_DEF_DEF_", "BA_", "VAL_", "VAL_TABLE_",
        "SG_MUL_VAL_", "BO_TX_BU_", "EV_", "ENVVAR_DATA_", "SGTYPE_", "SGTYPE_VAL_", "BA_DEF_SGTYPE_", "BA_SGTYPE_",
        "SIG_TYPE_REF_", "SIG_GROUP_", "SIG_VALTYPE_", "SIGTYPE_VALTYPE_", "BA_DEF_REL_", "BA_REL_",
        "BA_DEF_DEF_REL_", "BU_SG_REL_", "BU_EV_REL_", "BU_BO_REL_", "CAT_DEF_", "CAT_", "FILTER", "NS_DESC_",
        "EV_DATA_",
    ];

    /// <summary>Statements that come before the comments when written back; anything else unread goes at the end.</summary>
    internal static readonly HashSet<string> EarlyVerbatim = ["BO_TX_BU_", "EV_", "ENVVAR_DATA_", "SGTYPE_", "SGTYPE_VAL_"];

    /// <summary>Read a file. Latin-1, as Vector's tools write it, so no byte is ever refused.</summary>
    public static DbcReadResult Load(string path) => Read(File.ReadAllText(path, Encoding.Latin1));

    /// <summary>Read a database from its text.</summary>
    public static DbcReadResult Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Parser(text).Run();
    }

    private enum Kind
    {
        Word,
        Number,
        String,
        Symbol,
    }

    private readonly record struct Token(Kind Kind, string Text, int Line, int Column, int Start, int End)
    {
        public bool Is(string text) => Kind is Kind.Word or Kind.Symbol && Text == text;
    }

    private sealed class ReadException(string message) : Exception(message);

    private sealed class Parser
    {
        private readonly string _text;
        private readonly List<Token> _tokens;
        private readonly List<string> _problems = [];

        private readonly List<MessageBuilder> _messages = [];
        private readonly List<DbcValueTable> _valueTables = [];
        private readonly List<(string, string)> _nodeComments = [];
        private readonly List<DbcAttributeDefinition> _definitions = [];
        private readonly List<(string, DbcToken)> _defaults = [];
        private readonly List<DbcAttributeValue> _values = [];
        private readonly List<DbcVerbatim> _verbatim = [];
        private readonly List<string> _newSymbols = [];
        private readonly List<string> _nodes = [];
        private string _version = "";
        private string _bitTiming = "";
        private string? _comment;

        private int _at;

        public Parser(string text)
        {
            _text = text;
            _tokens = Tokenize(text, _problems);
        }

        private bool AtEnd => _at >= _tokens.Count;

        private Token Peek => _tokens[_at];

        public DbcReadResult Run()
        {
            while (!AtEnd)
            {
                var start = _at;
                var first = Peek;

                try
                {
                    Statement(first);
                }
                catch (ReadException e)
                {
                    _problems.Add(string.Create(CultureInfo.InvariantCulture, $"line {first.Line}: {first.Text} — {e.Message}"));
                    _at = Math.Max(_at, start + 1);
                    SkipToStatement(first.Line);
                }
            }

            return new DbcReadResult(Build(), _problems);
        }

        private DbcDocument Build() => new()
        {
            Version = _version,
            NewSymbols = _newSymbols,
            BitTiming = _bitTiming,
            Nodes = _nodes,
            ValueTables = _valueTables,
            Messages = [.. _messages.Select(m => m.Build())],
            Comment = _comment,
            NodeComments = _nodeComments,
            AttributeDefinitions = _definitions,
            AttributeDefaults = _defaults,
            AttributeValues = _values,
            Verbatim = _verbatim,
        };

        private void Statement(Token first)
        {
            if (first.Kind != Kind.Word)
            {
                throw new ReadException("not the start of a statement");
            }

            switch (first.Text)
            {
                case "VERSION":
                    _at++;
                    _version = String();
                    break;
                case "NS_":
                    NewSymbols();
                    break;
                case "BS_":
                    BitTiming();
                    break;
                case "BU_":
                    Nodes();
                    break;
                case "BO_":
                    Message();
                    break;
                case "SG_":
                    Signal();
                    break;
                case "CM_":
                    Comment();
                    break;
                case "BA_DEF_":
                    Definition();
                    break;
                case "BA_DEF_DEF_":
                    Default();
                    break;
                case "BA_":
                    AttributeValue();
                    break;
                case "VAL_TABLE_":
                    ValueTable();
                    break;
                case "VAL_":
                    if (!SignalValues())
                    {
                        Verbatim();
                    }

                    break;
                case "SG_MUL_VAL_":
                    if (!Switch())
                    {
                        Verbatim();
                    }

                    break;
                default:
                    Verbatim();
                    break;
            }
        }

        private void NewSymbols()
        {
            _at++;
            Expect(":");

            // The list holds keywords itself, indented, so it ends at the first word back at the margin.
            while (!AtEnd && Peek.Column > 0)
            {
                _newSymbols.Add(Take().Text);
            }
        }

        private void BitTiming()
        {
            var line = Take().Line;
            Expect(":");

            var from = -1;
            var to = -1;
            while (!AtEnd && Peek.Line == line)
            {
                var token = Take();
                from = from < 0 ? token.Start : from;
                to = token.End;
            }

            _bitTiming = from < 0 ? "" : _text[from..to].Trim();
        }

        private void Nodes()
        {
            _at++;
            Expect(":");

            // A long list wraps onto indented lines; the next statement starts at the margin.
            while (!AtEnd && Peek.Kind == Kind.Word && !Keywords.Contains(Peek.Text)
                && (Peek.Column > 0 || Peek.Line == _tokens[_at - 1].Line))
            {
                _nodes.Add(Take().Text);
            }
        }

        private void Message()
        {
            _at++;
            var id = UnsignedId();
            var name = Word();
            Expect(":");
            var length = Integer();
            var sender = Word();
            _messages.Add(new MessageBuilder(new DbcMessage(id, name, length, sender)));
        }

        private void Signal()
        {
            var line = Take().Line;

            if (_messages.Count == 0)
            {
                throw new ReadException("a signal before any message");
            }

            var name = Word();
            var multiplexor = false;
            long? value = null;

            if (!Peek.Is(":"))
            {
                (multiplexor, value) = Indicator(Word());
            }

            Expect(":");
            var start = Integer();
            Expect("|");
            var length = Integer();
            Expect("@");
            var order = Integer();
            var sign = Take();

            if (order is not (0 or 1) || !(sign.Is("+") || sign.Is("-")))
            {
                throw new ReadException("the byte order and sign must be @0 or @1 then + or −");
            }

            Expect("(");
            var scale = Real();
            Expect(",");
            var offset = Real();
            Expect(")");
            Expect("[");
            var minimum = Real();
            Expect("|");
            var maximum = Real();
            Expect("]");
            var unit = String();

            var receivers = new List<string>();
            while (!AtEnd && Peek.Line == line && (Peek.Kind == Kind.Word || Peek.Is(",")))
            {
                var token = Take();
                if (token.Kind == Kind.Word && token.Text != "Vector__XXX")
                {
                    receivers.Add(token.Text);
                }
            }

            var message = _messages[^1];
            if (message.Signals.Any(s => s.Name == name))
            {
                throw new ReadException(string.Create(CultureInfo.InvariantCulture, $"{name} is already a signal of {message.Message.Name}"));
            }

            message.Signals.Add(new DbcSignal(name, start, length, order == 0, sign.Is("-"), scale, offset, minimum, maximum, unit)
            {
                IsMultiplexor = multiplexor,
                MultiplexValue = value,
                Receivers = receivers,
            });
        }

        private static (bool Multiplexor, long? Value) Indicator(string text)
        {
            if (text == "M")
            {
                return (true, null);
            }

            var multiplexor = text.EndsWith('M');
            var digits = multiplexor ? text[1..^1] : text[1..];

            if (text.Length < 2 || text[0] != 'm' || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                throw new ReadException(string.Create(CultureInfo.InvariantCulture, $"{text} is not a multiplex indicator (M, m3 or m3M)"));
            }

            return (multiplexor, value);
        }

        private void Comment()
        {
            var start = _at++;

            if (Peek.Kind == Kind.String)
            {
                _comment = String();
            }
            else
            {
                var kind = Word();
                switch (kind)
                {
                    case "BU_":
                        var node = Word();
                        _nodeComments.Add((node, String()));
                        break;
                    case "BO_":
                        var id = UnsignedId();
                        var text = String();
                        if (MessageFor(id) is not { } message)
                        {
                            // For a message the file does not have: keep it rather than lose it.
                            _at = start;
                            Verbatim();
                            return;
                        }

                        message.Comment = text;
                        break;
                    case "SG_":
                        var signalId = UnsignedId();
                        var signal = Word();
                        var signalText = String();
                        if (!Update(signalId, signal, s => s with { Comment = signalText }))
                        {
                            _at = start;
                            Verbatim();
                            return;
                        }

                        break;
                    default:
                        // An environment variable's comment, or a kind this does not know: keep it.
                        _at = start;
                        Verbatim();
                        return;
                }
            }

            Expect(";");
        }

        private void Definition()
        {
            _at++;
            var kind = ObjectKind();
            var name = String();
            var type = Word();
            var arguments = new List<DbcToken>();

            while (!AtEnd && !Peek.Is(";"))
            {
                var token = Take();
                if (!token.Is(","))
                {
                    arguments.Add(Value(token));
                }
            }

            Expect(";");
            _definitions.Add(new DbcAttributeDefinition(kind, name, type, arguments));
        }

        private void Default()
        {
            _at++;
            var name = String();
            var value = Value(Take());
            Expect(";");
            _defaults.Add((name, value));
        }

        private void AttributeValue()
        {
            _at++;
            var name = String();
            var kind = ObjectKind();
            string? target = null;
            uint? message = null;

            switch (kind)
            {
                case DbcObjectKind.Node:
                case DbcObjectKind.EnvironmentVariable:
                    target = Word();
                    break;
                case DbcObjectKind.Message:
                    message = UnsignedId();
                    break;
                case DbcObjectKind.Signal:
                    message = UnsignedId();
                    target = Word();
                    break;
            }

            var value = Value(Take());
            Expect(";");
            _values.Add(new DbcAttributeValue(name, kind, target, message, value));
        }

        private void ValueTable()
        {
            _at++;
            var name = Word();
            _valueTables.Add(new DbcValueTable(name, ValueTexts()));
        }

        /// <summary>A message's signal's value table. False, having read nothing, for an environment variable's.</summary>
        private bool SignalValues()
        {
            if (_at + 1 >= _tokens.Count || _tokens[_at + 1].Kind != Kind.Number)
            {
                return false;
            }

            var start = _at;
            _at++;
            var id = UnsignedId();
            var name = Word();
            var values = ValueTexts();

            if (!Update(id, name, s => s with { Values = values }))
            {
                _at = start;
                return false;
            }

            return true;
        }

        /// <summary>An extended-multiplexing line. False, having read nothing, when its signal is not in the file.</summary>
        private bool Switch()
        {
            var start = _at;
            _at++;
            var id = UnsignedId();
            var signal = Word();
            var multiplexor = Word();
            var ranges = new List<DbcMultiplexRange>();

            while (!AtEnd && !Peek.Is(";"))
            {
                var from = Long();
                Expect("-");
                var to = Long();
                ranges.Add(new DbcMultiplexRange(from, to));

                if (Peek.Is(","))
                {
                    _at++;
                }
            }

            Expect(";");

            if (!Update(id, signal, s => s with { Switch = new DbcSwitch(multiplexor, ranges) }))
            {
                _at = start;
                return false;
            }

            return true;
        }

        private List<DbcValueText> ValueTexts()
        {
            var values = new List<DbcValueText>();

            while (!AtEnd && !Peek.Is(";"))
            {
                var raw = Long();
                values.Add(new DbcValueText(raw, String()));
            }

            Expect(";");
            return values;
        }

        /// <summary>Keep a statement word for word, through its semicolon or up to the next statement.</summary>
        private void Verbatim()
        {
            var first = Take();
            var end = first.End;

            while (!AtEnd)
            {
                var token = Peek;

                if (token.Line > first.Line && IsStatementStart(_at))
                {
                    break;
                }

                _at++;
                end = token.End;

                if (token.Is(";"))
                {
                    break;
                }
            }

            _verbatim.Add(new DbcVerbatim(first.Text, _text[first.Start..end]));
        }

        private bool IsStatementStart(int index)
        {
            var token = _tokens[index];
            return token.Kind == Kind.Word && Keywords.Contains(token.Text)
                && (index == 0 || _tokens[index - 1].Line < token.Line);
        }

        private void SkipToStatement(int line)
        {
            while (!AtEnd && !(Peek.Line > line && IsStatementStart(_at)))
            {
                _at++;
            }
        }

        private MessageBuilder? MessageFor(uint rawId) => _messages.LastOrDefault(m => m.Message.RawId == rawId);

        private bool Update(uint rawId, string signal, Func<DbcSignal, DbcSignal> change)
        {
            if (MessageFor(rawId) is not { } message)
            {
                return false;
            }

            var index = message.Signals.FindIndex(s => s.Name == signal);
            if (index < 0)
            {
                return false;
            }

            message.Signals[index] = change(message.Signals[index]);
            return true;
        }

        private DbcObjectKind ObjectKind()
        {
            if (Peek.Kind != Kind.Word)
            {
                return DbcObjectKind.Network;
            }

            var kind = Peek.Text switch
            {
                "BU_" => DbcObjectKind.Node,
                "BO_" => DbcObjectKind.Message,
                "SG_" => DbcObjectKind.Signal,
                "EV_" => DbcObjectKind.EnvironmentVariable,
                _ => throw new ReadException(string.Create(CultureInfo.InvariantCulture, $"{Peek.Text} is not BU_, BO_, SG_ or EV_")),
            };

            _at++;
            return kind;
        }

        private static DbcToken Value(Token token) => token.Kind switch
        {
            Kind.String => new DbcToken(token.Text, true),
            Kind.Number or Kind.Word => new DbcToken(token.Text, false),
            _ => throw new ReadException(string.Create(CultureInfo.InvariantCulture, $"{token.Text} is not a value")),
        };

        private Token Take()
        {
            if (AtEnd)
            {
                throw new ReadException("the file ends in the middle of it");
            }

            return _tokens[_at++];
        }

        private void Expect(string symbol)
        {
            var token = Take();
            if (!token.Is(symbol))
            {
                throw new ReadException(string.Create(CultureInfo.InvariantCulture, $"expected {symbol}, found {token.Text}"));
            }
        }

        private string Word()
        {
            var token = Take();
            return token.Kind == Kind.Word
                ? token.Text
                : throw new ReadException(string.Create(CultureInfo.InvariantCulture, $"expected a name, found {token.Text}"));
        }

        private string String()
        {
            var token = Take();
            return token.Kind == Kind.String
                ? token.Text
                : throw new ReadException(string.Create(CultureInfo.InvariantCulture, $"expected a quoted string, found {token.Text}"));
        }

        private int Integer()
        {
            var token = Take();
            return token.Kind == Kind.Number && int.TryParse(token.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new ReadException(string.Create(CultureInfo.InvariantCulture, $"expected a whole number, found {token.Text}"));
        }

        private long Long()
        {
            var token = Take();
            return token.Kind == Kind.Number && long.TryParse(token.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new ReadException(string.Create(CultureInfo.InvariantCulture, $"expected a whole number, found {token.Text}"));
        }

        private uint UnsignedId()
        {
            var token = Take();
            return token.Kind == Kind.Number && uint.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new ReadException(string.Create(CultureInfo.InvariantCulture, $"expected an identifier, found {token.Text}"));
        }

        private double Real()
        {
            var token = Take();
            return token.Kind == Kind.Number && double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new ReadException(string.Create(CultureInfo.InvariantCulture, $"expected a number, found {token.Text}"));
        }
    }

    private sealed class MessageBuilder(DbcMessage message)
    {
        public DbcMessage Message { get; } = message;

        public List<DbcSignal> Signals { get; } = [];

        public string? Comment { get; set; }

        public DbcMessage Build() => Message with { Signals = Signals, Comment = Comment };
    }

    private static List<Token> Tokenize(string text, List<string> problems)
    {
        var tokens = new List<Token>();
        var line = 1;
        var lineStart = 0;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\n')
            {
                line++;
                lineStart = ++i;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;
            var startLine = line;
            var column = i - lineStart;

            if (c == '"')
            {
                var value = new StringBuilder();
                i++;

                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\')
                    {
                        i++;
                    }
                    else if (text[i] == '\n')
                    {
                        line++;
                        lineStart = i + 1;
                    }

                    value.Append(text[i]);
                    i++;
                }

                if (i >= text.Length)
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"line {startLine}: a quoted string is never closed"));
                }

                i = Math.Min(i + 1, text.Length);
                tokens.Add(new Token(Kind.String, value.ToString(), startLine, column, start, i));
                continue;
            }

            if (IsNumberStart(text, i))
            {
                i++;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'
                    || (text[i] is 'e' or 'E' && i + 1 < text.Length && (char.IsAsciiDigit(text[i + 1]) || text[i + 1] is '+' or '-'))
                    || (text[i] is '+' or '-' && text[i - 1] is 'e' or 'E')))
                {
                    i++;
                }

                tokens.Add(new Token(Kind.Number, text[start..i], startLine, column, start, i));
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(Kind.Word, text[start..i], startLine, column, start, i));
                continue;
            }

            i++;
            tokens.Add(new Token(Kind.Symbol, c.ToString(), startLine, column, start, i));
        }

        return tokens;
    }

    /// <summary>
    /// A digit, or a sign before one that does not follow a number or a name — so <c>-40</c> in
    /// <c>(1,-40)</c> is a number, while the <c>-</c> of <c>@1-</c> and of a range's <c>13-13</c> is not.
    /// </summary>
    private static bool IsNumberStart(string text, int i)
    {
        var c = text[i];

        if (char.IsAsciiDigit(c))
        {
            return true;
        }

        if (c is not ('-' or '+') || i + 1 >= text.Length || !(char.IsAsciiDigit(text[i + 1]) || text[i + 1] == '.'))
        {
            return false;
        }

        return i == 0 || !(char.IsAsciiLetterOrDigit(text[i - 1]) || text[i - 1] is '_' or '.');
    }
}

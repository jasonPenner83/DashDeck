using System.Globalization;

namespace DashDeck.Core.Calculated;

/// <summary>A formula that will not read, with where and why.</summary>
public sealed class FormulaException(string message) : Exception(message);

/// <summary>
/// A formula from the catalog, read once and worked out as often as asked (ADR-0081).
/// </summary>
/// <remarks>
/// <para>
/// <b>Arithmetic and nothing else.</b> Numbers, names, <c>+ - * /</c>, brackets, comparisons
/// (<c>&lt; &lt;= &gt; &gt;= == !=</c>, which give 1 or 0), <c>&amp;&amp;</c> and <c>||</c>, and five
/// functions: <c>min</c>, <c>max</c>, <c>abs</c>, <c>clamp(x, low, high)</c> and
/// <c>if(condition, then, otherwise)</c>. A formula cannot reach a file, the network or the truck:
/// it can only name values it is handed.
/// </para>
/// <para>
/// Read when the catalog loads, so a formula with a typing mistake is refused there with the place
/// it went wrong, as a bad decode is, rather than turning up as a wrong number on the dash.
/// </para>
/// </remarks>
public sealed class Formula
{
    private readonly Node _root;

    private Formula(string text, Node root)
    {
        Text = text;
        _root = root;
        Names = root.Names().Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The formula as written.</summary>
    public string Text { get; }

    /// <summary>Every name it uses, in the order they first appear.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>Read a formula.</summary>
    /// <exception cref="FormulaException">It will not read.</exception>
    public static Formula Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new Parser(text);
        return new Formula(text, parser.ParseAll());
    }

    /// <summary>Read a formula, or say why not.</summary>
    public static Formula? TryParse(string? text, out string? problem)
    {
        problem = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            problem = "the formula is empty";
            return null;
        }

        try
        {
            return Parse(text);
        }
        catch (FormulaException ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>Work it out. A name <paramref name="value"/> does not know is NaN, and so is the answer.</summary>
    public double Evaluate(Func<string, double> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return _root.Evaluate(value);
    }

    public override string ToString() => Text;

    // ── The tree ──────────────────────────────────────────────────────────────

    private abstract record Node
    {
        public abstract double Evaluate(Func<string, double> value);

        public abstract IEnumerable<string> Names();
    }

    private sealed record Number(double Value) : Node
    {
        public override double Evaluate(Func<string, double> value) => Value;

        public override IEnumerable<string> Names() => [];
    }

    private sealed record Name(string Id) : Node
    {
        public override double Evaluate(Func<string, double> value) => value(Id);

        public override IEnumerable<string> Names() => [Id];
    }

    private sealed record Negate(Node Operand) : Node
    {
        public override double Evaluate(Func<string, double> value) => -Operand.Evaluate(value);

        public override IEnumerable<string> Names() => Operand.Names();
    }

    private sealed record Binary(string Op, Node Left, Node Right) : Node
    {
        public override double Evaluate(Func<string, double> value)
        {
            var a = Left.Evaluate(value);

            // Only the side that is needed is worked out, so `speed > 0 && x / speed` is safe.
            switch (Op)
            {
                case "&&":
                    return Truth(a) ? Bool(Truth(Right.Evaluate(value))) : 0;
                case "||":
                    return Truth(a) ? 1 : Bool(Truth(Right.Evaluate(value)));
            }

            var b = Right.Evaluate(value);

            return Op switch
            {
                "+" => a + b,
                "-" => a - b,
                "*" => a * b,
                "/" => a / b,
                "<" => Bool(a < b),
                "<=" => Bool(a <= b),
                ">" => Bool(a > b),
                ">=" => Bool(a >= b),
                "==" => Bool(a == b),
                "!=" => Bool(a != b),
                _ => double.NaN,
            };
        }

        public override IEnumerable<string> Names() => Left.Names().Concat(Right.Names());
    }

    private sealed record Call(string Function, IReadOnlyList<Node> Arguments) : Node
    {
        public override double Evaluate(Func<string, double> value)
        {
            if (Function == "if")
            {
                // Only the branch taken is worked out.
                return Truth(Arguments[0].Evaluate(value)) ? Arguments[1].Evaluate(value) : Arguments[2].Evaluate(value);
            }

            var args = Arguments.Select(a => a.Evaluate(value)).ToList();

            if (args.Any(double.IsNaN))
            {
                return double.NaN;
            }

            return Function switch
            {
                "min" => args.Min(),
                "max" => args.Max(),
                "abs" => Math.Abs(args[0]),
                "clamp" => args[1] <= args[2] ? Math.Clamp(args[0], args[1], args[2]) : double.NaN,
                _ => double.NaN,
            };
        }

        public override IEnumerable<string> Names() => Arguments.SelectMany(a => a.Names());
    }

    private static bool Truth(double v) => !double.IsNaN(v) && v != 0;

    private static double Bool(bool b) => b ? 1 : 0;

    /// <summary>The functions there are, and how many arguments each takes (-1: one or more).</summary>
    private static readonly Dictionary<string, int> Functions = new(StringComparer.Ordinal)
    {
        ["min"] = -1,
        ["max"] = -1,
        ["abs"] = 1,
        ["clamp"] = 3,
        ["if"] = 3,
    };

    // ── Reading it ────────────────────────────────────────────────────────────

    private sealed class Parser(string text)
    {
        private int _at;

        public Node ParseAll()
        {
            var node = Or();
            Skip();

            if (_at < text.Length)
            {
                throw Fail($"'{text[_at]}' was not expected here");
            }

            return node;
        }

        private Node Or()
        {
            var node = And();

            while (Take("||"))
            {
                node = new Binary("||", node, And());
            }

            return node;
        }

        private Node And()
        {
            var node = Compare();

            while (Take("&&"))
            {
                node = new Binary("&&", node, Compare());
            }

            return node;
        }

        private Node Compare()
        {
            var node = Sum();

            foreach (var op in new[] { "<=", ">=", "==", "!=", "<", ">" })
            {
                if (Take(op))
                {
                    return new Binary(op, node, Sum());
                }
            }

            return node;
        }

        private Node Sum()
        {
            var node = Product();

            while (true)
            {
                if (Take("+"))
                {
                    node = new Binary("+", node, Product());
                }
                else if (Take("-"))
                {
                    node = new Binary("-", node, Product());
                }
                else
                {
                    return node;
                }
            }
        }

        private Node Product()
        {
            var node = Unary();

            while (true)
            {
                if (Take("*"))
                {
                    node = new Binary("*", node, Unary());
                }
                else if (Take("/"))
                {
                    node = new Binary("/", node, Unary());
                }
                else
                {
                    return node;
                }
            }
        }

        private Node Unary() => Take("-") ? new Negate(Unary()) : Primary();

        private Node Primary()
        {
            Skip();

            if (_at >= text.Length)
            {
                throw Fail("the formula ends where a number or a name was expected");
            }

            var c = text[_at];

            if (Take("("))
            {
                var inner = Or();
                Expect(")");
                return inner;
            }

            if (char.IsAsciiDigit(c) || c == '.')
            {
                return ReadNumber();
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = _at;

                while (_at < text.Length && (char.IsAsciiLetterOrDigit(text[_at]) || text[_at] == '_'))
                {
                    _at++;
                }

                var name = text[start.._at];

                if (!Take("("))
                {
                    return new Name(name);
                }

                if (!Functions.TryGetValue(name, out var arity))
                {
                    throw Fail($"'{name}' is not a function: there are {string.Join(", ", Functions.Keys)}", start);
                }

                var args = new List<Node> { Or() };

                while (Take(","))
                {
                    args.Add(Or());
                }

                Expect(")");

                if (arity >= 0 && args.Count != arity)
                {
                    throw Fail($"{name} takes {arity} value{(arity == 1 ? "" : "s")}, not {args.Count}", start);
                }

                return new Call(name, args);
            }

            throw Fail($"'{c}' was not expected here");
        }

        private Number ReadNumber()
        {
            var start = _at;

            while (_at < text.Length && (char.IsAsciiDigit(text[_at]) || text[_at] == '.'))
            {
                _at++;
            }

            // An exponent: 3.05e-5.
            if (_at < text.Length && text[_at] is 'e' or 'E')
            {
                var mark = _at++;

                if (_at < text.Length && text[_at] is '+' or '-')
                {
                    _at++;
                }

                if (_at < text.Length && char.IsAsciiDigit(text[_at]))
                {
                    while (_at < text.Length && char.IsAsciiDigit(text[_at]))
                    {
                        _at++;
                    }
                }
                else
                {
                    _at = mark;
                }
            }

            var written = text[start.._at];

            return double.TryParse(written, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                ? new Number(n)
                : throw Fail($"'{written}' is not a number", start);
        }

        private bool Take(string token)
        {
            Skip();

            if (string.CompareOrdinal(text, _at, token, 0, token.Length) != 0)
            {
                return false;
            }

            // '<' must not take the first half of '<='.
            if (token is "<" or ">" && _at + 1 < text.Length && text[_at + 1] == '=')
            {
                return false;
            }

            _at += token.Length;
            return true;
        }

        private void Expect(string token)
        {
            if (!Take(token))
            {
                throw Fail(_at < text.Length ? $"'{token}' was expected, not '{text[_at]}'" : $"'{token}' was expected before the end");
            }
        }

        private void Skip()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at]))
            {
                _at++;
            }
        }

        private FormulaException Fail(string why, int? at = null) =>
            new($"{why} (at character {(at ?? _at) + 1} of \"{text}\")");
    }
}

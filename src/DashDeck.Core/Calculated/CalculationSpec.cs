using System.Text.Json;
using System.Text.Json.Serialization;

namespace DashDeck.Core.Calculated;

/// <summary>
/// How a calculated signal is worked out, as the catalog writes it (ADR-0081).
/// </summary>
/// <remarks>
/// <code>
/// "calculate": {
///   "unit": "L/h",
///   "inputs": { "map": "engine.intakeManifoldPressure", "lambda": { "signal": "fuel.commandedLambda", "default": 1 } },
///   "constants": { "ve": 0.85 },
///   "formula": "map * ve / lambda"
/// }
/// </code>
/// Either one <see cref="Formula"/>, or <see cref="Cases"/>: the first whose <c>when</c> holds is
/// used, and each can carry its own unit. The names a formula may use are its inputs, its
/// constants, and the facts every calculation is given (<see cref="Calculations.Facts"/>).
/// </remarks>
public sealed record CalculationSpec
{
    /// <summary>The unit of the result, unless a case says another.</summary>
    public string? Unit { get; init; }

    /// <summary>The signals it reads, by the name the formula calls them.</summary>
    public IReadOnlyDictionary<string, CalculationInput> Inputs { get; init; } = new Dictionary<string, CalculationInput>();

    /// <summary>Fixed numbers, by name, so the formula says what they are rather than just using them.</summary>
    public IReadOnlyDictionary<string, double>? Constants { get; init; }

    /// <summary>The formula, when there is one way to work it out.</summary>
    public string? Formula { get; init; }

    /// <summary>Several ways, tried in order, when it depends on something (stopped or moving).</summary>
    public IReadOnlyList<CalculationCase>? Cases { get; init; }

    /// <summary>Every formula it holds, a case's condition included, in order.</summary>
    [JsonIgnore]
    public IEnumerable<string> Formulas =>
        Formula is { } f ? [f] : (Cases ?? []).SelectMany(c => c.When is { } w ? new[] { w, c.Formula } : [c.Formula]);
}

/// <summary>One way of working a calculated signal out, used when its condition holds.</summary>
/// <param name="When">The condition, a formula that is true when not 0; null for the last, catch-all case.</param>
/// <param name="Formula">The formula.</param>
/// <param name="Unit">The result's unit in this case, or null for the calculation's.</param>
public sealed record CalculationCase(string? When, string Formula, string? Unit = null);

/// <summary>
/// One signal a calculation reads. Written as the signal's id alone when it is needed, or as
/// <c>{ "signal": …, "default": … }</c> when the calculation can do without it.
/// </summary>
[JsonConverter(typeof(CalculationInputConverter))]
public sealed record CalculationInput(string Signal, double? Default = null)
{
    /// <summary>True when there is no reading without it.</summary>
    public bool Required => Default is null;
}

/// <summary>Reads and writes an input as a plain id or as an object with a default.</summary>
public sealed class CalculationInputConverter : JsonConverter<CalculationInput>
{
    public override CalculationInput Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new CalculationInput(reader.GetString() ?? "");
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("an input is a signal id, or { \"signal\": …, \"default\": … }");
        }

        string? signal = null;
        double? fallback = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();

            if (string.Equals(name, "signal", StringComparison.OrdinalIgnoreCase))
            {
                signal = reader.GetString();
            }
            else if (string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
            {
                fallback = reader.TokenType == JsonTokenType.Null ? null : reader.GetDouble();
            }
            else
            {
                reader.Skip();
            }
        }

        return new CalculationInput(signal ?? "", fallback);
    }

    public override void Write(Utf8JsonWriter writer, CalculationInput value, JsonSerializerOptions options)
    {
        if (value.Default is not { } fallback)
        {
            writer.WriteStringValue(value.Signal);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("signal", value.Signal);
        writer.WriteNumber("default", fallback);
        writer.WriteEndObject();
    }
}

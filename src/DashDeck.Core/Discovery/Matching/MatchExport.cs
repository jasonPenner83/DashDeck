using DashDeck.Core.Catalog;
using System.Globalization;
using System.Text;

namespace DashDeck.Core.Discovery.Matching;

/// <summary>A match the person accepted: an identifier, what FORScan calls it, and how to read it.</summary>
/// <param name="Evidence">How it was found, for the comment above it: "5 typed samples, 3 raw values" or "PID log, R² 0.9993".</param>
public sealed record AcceptedMatch(IdentifierKey Key, string Name, ScalingCandidate Scaling, string Unit, string Evidence);

/// <summary>
/// Writes accepted matches as entries for a vehicle pack's <c>signals</c> list (ADR-0050).
/// </summary>
/// <remarks>
/// Ready to paste, not ready to ship: each goes through TEST in Settings ▸ Sensors on the truck
/// (ADR-0032) before it reaches the pack, and the comment carries what it was matched against so
/// the commit can say so. The id is a suggestion from the name, under <c>ford.</c>, to edit.
/// </remarks>
public static class MatchExport
{
    public static string ToPackEntries(IEnumerable<AcceptedMatch> matches)
    {
        var text = new StringBuilder();
        var first = true;

        foreach (var m in matches)
        {
            if (!first)
            {
                text.AppendLine(",");
            }

            first = false;
            var d = m.Scaling;
            var inv = CultureInfo.InvariantCulture;

            text.AppendLine(inv, $"  // {m.Key} — matched to FORScan's \"{m.Name}\": {d.Formula} {m.Unit}. {m.Evidence}. Confirm with TEST before committing.");
            text.AppendLine("  {");
            text.AppendLine(inv, $"    \"id\": \"ford.{Slug(m.Name)}\",");
            text.AppendLine(inv, $"    \"name\": \"{Escape(m.Name)}\",");
            text.AppendLine(inv, $"    \"category\": \"Other\",");
            text.AppendLine(inv, $"    \"bus\": \"{m.Key.Bus}\",");
            text.AppendLine(inv, $"    \"mode\": {m.Key.Mode},");
            text.AppendLine(inv, $"    \"pid\": {m.Key.Pid},");
            if (m.Key.ModuleText is { } module)
            {
                text.AppendLine(inv, $"    \"module\": \"{module}\",");
            }

            text.AppendLine(inv, $"    \"decode\": {{ \"byteOffset\": {d.Window.Offset}, \"byteLength\": {d.Window.Length}, \"signed\": {(d.Window.Signed ? "true" : "false")}, \"scale\": {Num(d.Scale)}, \"offset\": {Num(d.Offset)}, \"unit\": \"{Escape(m.Unit)}\"{MaskText(d.Mask)} }},");
            if (d.States is { Count: > 0 } states)
            {
                text.AppendLine(inv, $"    \"states\": {StatesText(states)},");
            }

            text.AppendLine("    \"defaultRateHz\": 1");
            text.Append("  }");
        }

        text.AppendLine();
        return text.ToString();
    }

    /// <summary>Definitions — pairings — as pack entries, each under its comment.</summary>
    public static string ToPackEntries(IEnumerable<(SignalDefinition Definition, string Comment)> entries)
    {
        var text = new StringBuilder();
        var first = true;
        var inv = CultureInfo.InvariantCulture;

        foreach (var (d, comment) in entries)
        {
            if (!first)
            {
                text.AppendLine(",");
            }

            first = false;
            text.AppendLine(inv, $"  // {comment}");
            text.AppendLine("  {");
            text.AppendLine(inv, $"    \"id\": \"{Escape(d.Id)}\",");
            text.AppendLine(inv, $"    \"name\": \"{Escape(d.Name)}\",");
            text.AppendLine(inv, $"    \"category\": \"{Escape(d.Category)}\",");
            text.AppendLine(inv, $"    \"bus\": \"{d.Bus}\",");
            text.AppendLine(inv, $"    \"mode\": {d.Mode},");
            text.AppendLine(inv, $"    \"pid\": {d.Pid},");
            if (d.Module is { } module)
            {
                text.AppendLine(inv, $"    \"module\": \"{module}\",");
            }

            var c = d.Decode;
            text.AppendLine(inv, $"    \"decode\": {{ \"byteOffset\": {c.ByteOffset}, \"byteLength\": {c.ByteLength}, \"signed\": {(c.Signed ? "true" : "false")}, \"scale\": {Num(c.Scale)}, \"offset\": {Num(c.Offset)}, \"unit\": \"{Escape(c.Unit)}\"{MaskText(c.Mask)} }},");
            if (d.States is { Count: > 0 } states)
            {
                text.AppendLine(inv, $"    \"states\": {StatesText(states)},");
            }

            if (d.Min is { } min)
            {
                text.AppendLine(inv, $"    \"min\": {Num(min)},");
            }

            if (d.Max is { } max)
            {
                text.AppendLine(inv, $"    \"max\": {Num(max)},");
            }

            text.AppendLine(inv, $"    \"defaultRateHz\": {Num(d.DefaultRateHz)}");
            text.Append("  }");
        }

        text.AppendLine();
        return text.ToString();
    }

    private static string StatesText(IEnumerable<SignalState> states) =>
        "[ " + string.Join(", ", states.Select(st => $"{{ \"value\": {Num(st.Value)}, \"name\": \"{Escape(st.Name)}\" }}")) + " ]";

    private static string MaskText(long? mask) =>
        mask is { } m ? string.Create(CultureInfo.InvariantCulture, $", \"mask\": {m}") : "";

    private static string Num(double v) => v.ToString("0.##########", CultureInfo.InvariantCulture);

    private static string Escape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    internal static string Slug(string name)
    {
        var words = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return "value";
        }

        return string.Concat(words.Select((w, i) => i == 0
            ? w.ToLowerInvariant()
            : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }
}

using System.Globalization;
using System.Text;
using DashDeck.Abstractions;

namespace DashDeck.IdHunter;

/// <summary>
/// The folder a hunt writes to: <c>findings.csv</c>, the one to send, and a capture per run
/// (ADR-0044).
/// </summary>
internal sealed class HuntOutput
{
    public const string FindingsHeader =
        "time,target,signal,method,bus,module,frame_id,did,field,reading,hint,evidence,score,verdict,note";

    private readonly IClock _clock;

    public HuntOutput(string folder, IClock clock)
    {
        Folder = folder;
        _clock = clock;
        Directory.CreateDirectory(folder);

        var readme = Path.Combine(folder, "README.txt");
        if (!File.Exists(readme))
        {
            File.WriteAllText(readme,
                "DashDeck ID hunter output (ADR-0044).\r\n\r\n" +
                "findings.csv  - every candidate the guide ranked, and what you said when it was checked.\r\n" +
                "                Send this one.\r\n" +
                "listen-*.csv  - every frame heard during a listen, with the step it was heard in.\r\n" +
                "follow-*.csv  - every pass of a watch, with the reference beside it.\r\n" +
                "match-*.csv   - what you read off the cluster, and what each identifier said then.\r\n" +
                "sweep-*.csv   - every identifier a module answered.\r\n\r\n" +
                "These can hold the truck's VIN (some modules answer it, and some frames carry it).\r\n" +
                "Send them to a person, never commit them to the public repository.\r\n");
        }
    }

    public string Folder { get; }

    public string FindingsPath => Path.Combine(Folder, "findings.csv");

    /// <summary>Add a row to <c>findings.csv</c>, writing its header first if it is new.</summary>
    public void Finding(params object?[] fields)
    {
        var isNew = !File.Exists(FindingsPath);
        using var writer = new StreamWriter(FindingsPath, append: true, Encoding.UTF8);
        if (isNew)
        {
            writer.WriteLine(FindingsHeader);
        }

        writer.WriteLine(Row(fields));
    }

    /// <summary>A capture file in the folder, named after what made it.</summary>
    public StreamWriter Capture(string name, string header)
    {
        var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '-'));
        var writer = new StreamWriter(Path.Combine(Folder, safe + ".csv"), append: false, Encoding.UTF8);
        writer.WriteLine(header);
        return writer;
    }

    public static string Row(params object?[] fields) => string.Join(',', fields.Select(Field));

    public static string Field(object? value)
    {
        var text = value switch
        {
            null => "",
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : text;
    }

    /// <summary>The local time, for the findings' first column.</summary>
    public string Now() => _clock.UtcNow.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

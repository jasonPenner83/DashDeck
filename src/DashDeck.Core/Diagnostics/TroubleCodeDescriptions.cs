using System.Text.Json;

namespace DashDeck.Core.Diagnostics;

/// <summary>
/// What a trouble code is about, read from <c>catalog/reference/dtc-descriptions.json</c>
/// (ADR-0055): a few standard codes by name, and every other by its group.
/// </summary>
public sealed class TroubleCodeDescriptions
{
    public const string FileName = "dtc-descriptions.json";

    private readonly IReadOnlyDictionary<string, string> _groups;
    private readonly IReadOnlyDictionary<string, string> _codes;

    private TroubleCodeDescriptions(IReadOnlyDictionary<string, string> groups, IReadOnlyDictionary<string, string> codes)
    {
        _groups = groups;
        _codes = codes;
    }

    /// <summary>Knows nothing: every code is described by its letter alone.</summary>
    public static TroubleCodeDescriptions Empty { get; } = new(new Dictionary<string, string>(), new Dictionary<string, string>());

    /// <summary>What the code is about: its own name if the file has one, its group's otherwise.</summary>
    public string Describe(TroubleCode code)
    {
        var text = code.Text;
        if (_codes.TryGetValue(text, out var exact))
        {
            return exact;
        }

        for (var length = text.Length - 1; length >= 1; length--)
        {
            if (_groups.TryGetValue(text[..length], out var group))
            {
                return group;
            }
        }

        return code.System switch
        {
            'P' => "Powertrain",
            'C' => "Chassis",
            'B' => "Body",
            _ => "Network communication",
        };
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record File(Dictionary<string, string>? Groups, Dictionary<string, string>? Codes);

    /// <exception cref="InvalidDataException">The text is not the file's shape.</exception>
    public static TroubleCodeDescriptions Parse(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<File>(json, Options) ?? throw new InvalidDataException($"{FileName}: empty");
            return new TroubleCodeDescriptions(
                new Dictionary<string, string>(file.Groups ?? [], StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(file.Codes ?? [], StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FileName}: {ex.Message}", ex);
        }
    }

    /// <summary>The file in a reference folder; empty, with the reason, when it is missing or bad.</summary>
    public static (TroubleCodeDescriptions Descriptions, string? Problem) Load(string? referenceFolder)
    {
        if (referenceFolder is null)
        {
            return (Empty, null);
        }

        var path = Path.Combine(referenceFolder, FileName);
        if (!System.IO.File.Exists(path))
        {
            return (Empty, $"{FileName} is missing; codes are described by their letter only.");
        }

        try
        {
            return (Parse(System.IO.File.ReadAllText(path)), null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (Empty, ex.Message);
        }
    }
}

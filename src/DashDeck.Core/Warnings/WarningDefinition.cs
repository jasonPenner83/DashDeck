using System.Text.Json;
using System.Text.Json.Serialization;

namespace DashDeck.Core.Warnings;

/// <summary>How serious a warning is: what colour it is drawn in and what it asks of the driver.</summary>
public enum WarningSeverity
{
    /// <summary>Amber: have it looked at.</summary>
    Caution,

    /// <summary>Red: stop when it is safe.</summary>
    Stop,
}

/// <summary>One warning light DashDeck watches, from <c>catalog/warnings.json</c> (ADR-0055).</summary>
public sealed record WarningDefinition
{
    public required string Id { get; init; }

    /// <summary>The window's heading: <c>CHECK ENGINE</c>.</summary>
    public required string Title { get; init; }

    /// <summary>One word for the banner while moving: <c>ENGINE</c>.</summary>
    public string? Reason { get; init; }

    /// <summary>A console warning icon's name (<c>checkEngine</c>, <c>oil</c>…).</summary>
    public string Icon { get; init; } = "checkEngine";

    /// <summary>The catalog signal whose value lights it.</summary>
    public required string Signal { get; init; }

    /// <summary>Lit when this bit of the value is set.</summary>
    public int? Bit { get; init; }

    /// <summary>Lit when the value is under this.</summary>
    public double? Below { get; init; }

    /// <summary>Lit when the value is exactly this.</summary>
    public double? EqualsValue { get; init; }

    /// <summary>Lit when the value is at least this — 1 unless another condition is given.</summary>
    public double? OnAt { get; init; }

    public WarningSeverity Severity { get; init; } = WarningSeverity.Caution;

    /// <summary>Whether it interrupts the screen unless the user says otherwise.</summary>
    public bool Popup { get; init; }

    /// <summary>Lit only counts while the truck moves.</summary>
    public bool WhenMoving { get; init; }

    /// <summary>How long it must read lit — or off — before it changes.</summary>
    public double HoldSeconds { get; init; } = 2;

    /// <summary>A count that, rising, pops a dismissed warning again.</summary>
    public string? CountSignal { get; init; }

    /// <summary>The window reads the trouble codes and offers to clear them.</summary>
    public bool Codes { get; init; }

    /// <summary>What to do, in a sentence or three.</summary>
    public string Advice { get; init; } = "";

    /// <summary>The banner's word: <see cref="Reason"/>, or the title.</summary>
    public string BannerWord => string.IsNullOrWhiteSpace(Reason) ? Title : Reason;

    /// <summary>Whether a value lights it: bit, below, equals, or at least onAt — checked in that order.</summary>
    public bool IsLit(double value)
    {
        if (double.IsNaN(value))
        {
            return false;
        }

        if (Bit is { } bit)
        {
            return (((long)Math.Round(value) >> Math.Clamp(bit, 0, 62)) & 1) == 1;
        }

        if (Below is { } below)
        {
            return value < below;
        }

        if (EqualsValue is { } equals)
        {
            return Math.Abs(value - equals) < 1e-6;
        }

        return value >= (OnAt ?? 1);
    }
}

/// <summary>The warnings file, read.</summary>
public static class WarningCatalog
{
    public const string FileName = "warnings.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record Row
    {
        public string? Id { get; init; }
        public string? Title { get; init; }
        public string? Reason { get; init; }
        public string? Icon { get; init; }
        public string? Signal { get; init; }
        public int? Bit { get; init; }
        public double? Below { get; init; }
        [JsonPropertyName("equals")]
        public double? EqualsValue { get; init; }
        public double? OnAt { get; init; }
        public WarningSeverity? Severity { get; init; }
        public bool? Popup { get; init; }
        public bool? WhenMoving { get; init; }
        public double? HoldSeconds { get; init; }
        public string? CountSignal { get; init; }
        public bool? Codes { get; init; }
        public string? Advice { get; init; }
    }

    /// <summary>
    /// The warnings in the text. A row without an id, title or signal is left out and named; a
    /// file that is not a list at all is an error.
    /// </summary>
    /// <exception cref="InvalidDataException">The text is not a list of warnings.</exception>
    public static (IReadOnlyList<WarningDefinition> Warnings, IReadOnlyList<string> Problems) Parse(string json)
    {
        List<Row>? rows;
        try
        {
            rows = JsonSerializer.Deserialize<List<Row>>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FileName}: {ex.Message}", ex);
        }

        var warnings = new List<WarningDefinition>();
        var problems = new List<string>();

        foreach (var (row, index) in (rows ?? []).Select((r, i) => (r, i)))
        {
            if (string.IsNullOrWhiteSpace(row.Id) || string.IsNullOrWhiteSpace(row.Title) || string.IsNullOrWhiteSpace(row.Signal))
            {
                problems.Add($"{FileName}: warning {index + 1} needs an id, a title and a signal; left out.");
                continue;
            }

            if (warnings.Any(w => w.Id == row.Id))
            {
                problems.Add($"{FileName}: '{row.Id}' is named twice; the second is left out.");
                continue;
            }

            warnings.Add(new WarningDefinition
            {
                Id = row.Id,
                Title = row.Title,
                Reason = row.Reason,
                Icon = row.Icon ?? "checkEngine",
                Signal = row.Signal,
                Bit = row.Bit,
                Below = row.Below,
                EqualsValue = row.EqualsValue,
                OnAt = row.OnAt,
                Severity = row.Severity ?? WarningSeverity.Caution,
                Popup = row.Popup ?? false,
                WhenMoving = row.WhenMoving ?? false,
                HoldSeconds = Math.Clamp(row.HoldSeconds ?? 2, 0, 600),
                CountSignal = row.CountSignal,
                Codes = row.Codes ?? false,
                Advice = row.Advice ?? "",
            });
        }

        return (warnings, problems);
    }

    /// <summary>The file at a path: none, with the reason, when it is missing or bad.</summary>
    public static (IReadOnlyList<WarningDefinition> Warnings, IReadOnlyList<string> Problems) Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return ([], [$"{FileName} was not found; no warning will pop up."]);
        }

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ([], [ex.Message]);
        }
    }
}

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DashDeck.Host.Components;

/// <summary>What a component offers the host. A manifest names these in <c>surfaces</c>.</summary>
public enum ComponentSurface
{
    /// <summary>A widget in the band grid. The common case.</summary>
    Widget,

    /// <summary>A full-screen view opened from the widget.</summary>
    FullScreen,

    /// <summary>An item in the status strip. Not yet hosted.</summary>
    StatusItem,

    /// <summary>Runs with no UI at all — a logger, a bridge.</summary>
    BackgroundWorker,
}

/// <summary>Where the component's code is, inside its folder.</summary>
/// <param name="Assembly">The DLL, relative to the manifest.</param>
/// <param name="Type">The fully-qualified type implementing <c>IDashComponent</c>.</param>
public sealed record ComponentEntry(
    [property: JsonPropertyName("assembly")] string Assembly = "",
    [property: JsonPropertyName("type")] string Type = "");

/// <summary>A signal a component declares, straight from the manifest.</summary>
/// <param name="Id">A catalog signal id.</param>
/// <param name="Priority">What survives when the budget is oversubscribed.</param>
/// <param name="RateHz">Requested rate; zero means the catalog default.</param>
public sealed record ManifestSignal(
    [property: JsonPropertyName("id")] string Id = "",
    [property: JsonPropertyName("priority")] string Priority = "Normal",
    [property: JsonPropertyName("rateHz")] double RateHz = 0);

/// <summary>Widget sizing hints.</summary>
/// <param name="PreferredSize">e.g. <c>2x1</c>.</param>
/// <param name="MinSize">Smallest the host may draw it.</param>
public sealed record ManifestWidget(
    [property: JsonPropertyName("preferredSize")] string PreferredSize = "1x1",
    [property: JsonPropertyName("minSize")] string MinSize = "1x1");

/// <summary>
/// A component's <c>component.json</c>, parsed. The host reads this before it loads any code.
/// </summary>
/// <remarks>
/// <b>The manifest is the trust and compatibility boundary</b> (ADR-0002, ADR-0008). It is
/// read and validated first, so a component that names a signal the catalog does not define,
/// or an <c>apiVersion</c> the host does not serve, is rejected with a message rather than
/// half-loaded — and its assembly is never even mapped. Tolerant of unknown fields, because a
/// manifest written against a newer SDK should degrade, not fail to parse.
/// </remarks>
public sealed record ComponentManifest
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "0.0.0";

    [JsonPropertyName("author")]
    public string Author { get; init; } = "";

    /// <summary>The contract version this component was built against (ADR-0008).</summary>
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; init; } = "";

    [JsonPropertyName("entry")]
    public ComponentEntry Entry { get; init; } = new();

    [JsonPropertyName("surfaces")]
    public IReadOnlyList<string> Surfaces { get; init; } = [];

    [JsonPropertyName("widget")]
    public ManifestWidget Widget { get; init; } = new();

    [JsonPropertyName("signals")]
    public IReadOnlyList<ManifestSignal> Signals { get; init; } = [];

    [JsonPropertyName("permissions")]
    public IReadOnlyList<string> Permissions { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The surfaces this manifest declares, parsed; unknown names dropped.</summary>
    public IReadOnlyList<ComponentSurface> ParsedSurfaces =>
        [.. Surfaces
            .Select(s => Enum.TryParse<ComponentSurface>(s, ignoreCase: true, out var v) ? (ComponentSurface?)v : null)
            .Where(v => v is not null)
            .Select(v => v!.Value)];

    /// <summary>True when the component says it draws a widget.</summary>
    public bool HasWidget => ParsedSurfaces.Contains(ComponentSurface.Widget);

    /// <summary>
    /// Read and shallow-validate a manifest file. Returns the manifest, or a reason it was
    /// rejected — never throws for bad content, because a malformed component is an expected
    /// thing to find in a folder, not an exceptional one.
    /// </summary>
    public static ComponentManifestResult Read(string path)
    {
        string text;

        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return ComponentManifestResult.Rejected(path, $"could not be read: {ex.Message}");
        }

        ComponentManifest? manifest;

        try
        {
            manifest = JsonSerializer.Deserialize<ComponentManifest>(text, Options);
        }
        catch (JsonException ex)
        {
            return ComponentManifestResult.Rejected(path, $"is not valid JSON: {ex.Message}");
        }

        if (manifest is null)
        {
            return ComponentManifestResult.Rejected(path, "is empty.");
        }

        // Shape checks only — that the fields the host needs are present. Whether the signals
        // exist and the apiVersion is served are checked later, by the host, against things a
        // manifest cannot see (the catalog, the supported range).
        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            return ComponentManifestResult.Rejected(path, "has no id.");
        }

        if (string.IsNullOrWhiteSpace(manifest.ApiVersion))
        {
            return ComponentManifestResult.Rejected(path, $"'{manifest.Id}' declares no apiVersion.");
        }

        // A background-only worker needs no assembly of a different kind, but every component
        // is code, so an entry is always required.
        if (string.IsNullOrWhiteSpace(manifest.Entry.Assembly) || string.IsNullOrWhiteSpace(manifest.Entry.Type))
        {
            return ComponentManifestResult.Rejected(path, $"'{manifest.Id}' has no entry assembly and type.");
        }

        return ComponentManifestResult.Accepted(manifest);
    }
}

/// <summary>The outcome of reading a manifest: one it accepted, or a reason it did not.</summary>
public sealed record ComponentManifestResult
{
    private ComponentManifestResult() { }

    /// <summary>The parsed manifest, when <see cref="IsAccepted"/>.</summary>
    public ComponentManifest? Manifest { get; private init; }

    /// <summary>Why it was rejected, when it was.</summary>
    public string? Reason { get; private init; }

    /// <summary>The manifest path, for naming the thing that failed.</summary>
    public string? Path { get; private init; }

    /// <summary>True when a usable manifest came back.</summary>
    public bool IsAccepted => Manifest is not null;

    internal static ComponentManifestResult Accepted(ComponentManifest manifest) =>
        new() { Manifest = manifest };

    internal static ComponentManifestResult Rejected(string path, string reason) =>
        new() { Path = path, Reason = $"Manifest {reason}" };
}

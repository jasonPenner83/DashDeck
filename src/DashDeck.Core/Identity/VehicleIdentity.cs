using System.Globalization;
using System.Text.Json;
using DashDeck.Abstractions;

namespace DashDeck.Core.Identity;

/// <summary>
/// What the vehicle is: decoded from its VIN, then corrected by hand where the decoder was
/// wrong or silent (ADR-0033).
/// </summary>
/// <remarks>
/// Every field is optional. A decoder answers what it knows — vPIC rarely knows a trim and
/// never a tank size — and the person fills or overrides the rest. Null means "not known",
/// never "zero".
/// </remarks>
public sealed record VehicleIdentity
{
    /// <summary>The VIN. Kept on the tablet; never put on <see cref="VehicleProfile"/>.</summary>
    public string? Vin { get; init; }

    public int? ModelYear { get; init; }

    public string? Make { get; init; }

    public string? Model { get; init; }

    public string? Trim { get; init; }

    /// <summary>Engine displacement in litres, e.g. 2.7.</summary>
    public double? DisplacementLitres { get; init; }

    public int? Cylinders { get; init; }

    /// <summary>True for a turbocharged engine, false for one that is not, null when unknown.</summary>
    public bool? Turbocharged { get; init; }

    /// <summary>The primary fuel, as the decoder names it: "Gasoline", "Diesel", "Electric".</summary>
    public string? FuelType { get; init; }

    /// <summary>Where the facts came from, e.g. "NHTSA vPIC" or "Entered by hand".</summary>
    public string? Source { get; init; }

    /// <summary>When the decoder answered.</summary>
    public DateTimeOffset? DecodedUtc { get; init; }

    /// <summary>Nothing known.</summary>
    public static VehicleIdentity Unknown { get; } = new();

    /// <summary>True when anything at all is known about the vehicle.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsKnown => ModelYear is not null || !string.IsNullOrWhiteSpace(Make) || !string.IsNullOrWhiteSpace(Model);

    /// <summary>One line for a screen: <c>2019 FORD F-150 XLT · 2.7 L V6 turbo · Gasoline</c>.</summary>
    public string Describe()
    {
        var name = string.Join(' ', new[] { ModelYear?.ToString(CultureInfo.InvariantCulture), Make, Model, Trim }
            .Where(p => !string.IsNullOrWhiteSpace(p)));

        var engine = string.Join(' ', new[]
        {
            DisplacementLitres is { } litres ? string.Create(CultureInfo.InvariantCulture, $"{litres:0.0#} L") : null,
            Cylinders is { } cylinders ? $"{cylinders} cyl" : null,
            Turbocharged is true ? "turbo" : null,
        }.Where(p => p is not null));

        return string.Join("  ·  ", new[] { name, engine, FuelType }.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    /// <summary>
    /// The component-facing facts: everything but the VIN, merged with the facts no decoder
    /// knows (the tank).
    /// </summary>
    public VehicleProfile ToProfile(double fuelTankLitres) => new()
    {
        FuelTankLitres = fuelTankLitres,
        ModelYear = ModelYear ?? 0,
        Make = Make ?? "",
        Model = Model ?? "",
        Trim = Trim ?? "",
        EngineDisplacementLitres = DisplacementLitres ?? 0,
        EngineCylinders = Cylinders ?? 0,
        Turbocharged = Turbocharged,
        FuelType = FuelType ?? "",
    };
}

/// <summary>What a decoder made of a VIN.</summary>
/// <param name="Identity">What was decoded, or null when nothing was.</param>
/// <param name="Problem">Why nothing was, or a warning about what was (a failed check digit, a partial decode).</param>
public sealed record VinDecodeResult(VehicleIdentity? Identity, string? Problem);

/// <summary>
/// NHTSA's vPIC VIN decoder: the request, and reading its answer.
/// </summary>
/// <remarks>
/// vPIC is the US government's public decoder — free, no key, and it covers every vehicle sold
/// in North America and a good share of others, from the manufacturers' own submissions. It is
/// the only network dependency here, and it is asked once, when a person presses LOOK UP; the
/// answer is cached on the tablet so the truck needs no signal afterwards.
/// <para>
/// Kept in the engine, apart from the HTTP call, so how its answer is read is tested on any OS.
/// </para>
/// </remarks>
public static class VpicDecoding
{
    /// <summary>The decoder's name, as the screen shows the source.</summary>
    public const string SourceName = "NHTSA vPIC";

    /// <summary>The flat-format decode endpoint for one VIN.</summary>
    public static Uri RequestUri(string vin) =>
        new($"https://vpic.nhtsa.dot.gov/api/vehicles/DecodeVinValues/{Uri.EscapeDataString(vin)}?format=json");

    /// <summary>
    /// Read a <c>DecodeVinValues</c> answer.
    /// </summary>
    /// <remarks>
    /// Every value arrives as a string and an unknown one as an empty string. vPIC answers a
    /// partial decode — a VIN it half-recognises — with a non-zero <c>ErrorCode</c> and whatever
    /// it could work out; that is kept, with its explanation as a warning, rather than thrown
    /// away. Only an answer with no make and no model year is a failure.
    /// </remarks>
    public static VinDecodeResult Parse(string json, string vin, DateTimeOffset decodedUtc)
    {
        JsonElement result;

        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("Results", out var results)
                || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
            {
                return new VinDecodeResult(null, "the decoder's answer had no results");
            }

            result = results[0].Clone();
        }
        catch (JsonException)
        {
            return new VinDecodeResult(null, "the decoder's answer could not be read");
        }

        string? Text(string name) =>
            result.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;

        var identity = new VehicleIdentity
        {
            Vin = vin,
            ModelYear = int.TryParse(Text("ModelYear"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : null,
            Make = Text("Make"),
            Model = Text("Model"),
            Trim = Text("Trim"),
            DisplacementLitres = double.TryParse(Text("DisplacementL"), NumberStyles.Float, CultureInfo.InvariantCulture, out var litres) && litres > 0
                ? Math.Round(litres, 1)
                : null,
            Cylinders = int.TryParse(Text("EngineCylinders"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cylinders) && cylinders > 0
                ? cylinders
                : null,
            Turbocharged = Text("Turbo") switch
            {
                "Yes" => true,
                "No" => false,
                _ => null,
            },
            FuelType = Text("FuelTypePrimary"),
            Source = SourceName,
            DecodedUtc = decodedUtc,
        };

        if (!identity.IsKnown)
        {
            return new VinDecodeResult(null, Text("ErrorText") ?? "the decoder did not recognise this VIN");
        }

        // "0" is a clean decode. Anything else still decoded something; say what the decoder said.
        var code = Text("ErrorCode") ?? "0";
        var clean = code.Split(',').All(c => c.Trim() == "0");

        return new VinDecodeResult(identity, clean ? null : Text("ErrorText"));
    }
}

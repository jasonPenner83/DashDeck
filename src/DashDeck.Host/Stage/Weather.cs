using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DashDeck.Host.Stage;

/// <summary>Conditions right now.</summary>
public sealed record WeatherNow(double TemperatureC, double FeelsLikeC, int Code);

/// <summary>One day of the forecast.</summary>
public sealed record WeatherDay(DateOnly Date, double MaxC, double MinC, int Code);

/// <summary>Everything the clock face shows about the weather.</summary>
/// <remarks>
/// <see cref="Daylight"/> is not for the clock face — it is what the theme's Auto mode
/// decides day from night with, until a headlight signal exists to do it properly.
/// </remarks>
public sealed record WeatherReport(
    WeatherNow Now,
    IReadOnlyList<WeatherDay> Forecast,
    (DateTimeOffset Sunrise, DateTimeOffset Sunset)? Daylight);

/// <summary>
/// Weather from Open-Meteo.
/// </summary>
/// <remarks>
/// Chosen for one reason above the others: <b>no API key</b>. A key is configuration, and
/// configuration on a tablet that is meant to work by being copied into a folder is a
/// liability — one more thing to lose, expire, or forget to carry across a rebuild. It is
/// also free for non-commercial use and returns the local timezone, so nothing here has to
/// guess at offsets.
/// </remarks>
public static class Weather
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Why the last fetch failed, if it did.
    /// </summary>
    /// <remarks>
    /// Surfaced through the occupant's <c>Describe()</c>. "Weather unavailable" with no way
    /// to find out why is the kind of dead end that wastes an evening.
    /// </remarks>
    public static string? LastError { get; private set; }

    /// <summary>
    /// Where the weather is for.
    /// </summary>
    /// <remarks>
    /// Winnipeg, hard-coded. Windows' location service would need a permission prompt and
    /// a capability the tablet does not otherwise use (constraint C1), and IP geolocation
    /// means another service to depend on. A constant is honest until the truck actually
    /// reports a position — at which point this becomes a signal like any other.
    /// </remarks>
    public const double DefaultLatitude = 49.8951;
    public const double DefaultLongitude = -97.1384;

    public static async Task<WeatherReport?> FetchAsync(
        double latitude,
        double longitude,
        CancellationToken ct)
    {
        var url =
            $"https://api.open-meteo.com/v1/forecast?latitude={latitude:0.####}&longitude={longitude:0.####}" +
            "&current=temperature_2m,apparent_temperature,weather_code" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset" +
            "&timezone=auto&forecast_days=4";

        try
        {
            // Read as text first, then parse.
            //
            // Open-Meteo does not fail with a status code when it is having trouble: it
            // answers **HTTP 200, Content-Type application/json**, with a plain sentence in
            // the body — "Unexpected error while streaming data: timeoutReached". Deserialising
            // that directly gives "'U' is an invalid start of a value", which says nothing
            // about whose fault it is and cost real time to diagnose the first time. Twice.
            var body = await Http.GetStringAsync(url, ct).ConfigureAwait(false);

            if (body.Length == 0 || (body[0] is not '{' and not '['))
            {
                LastError = $"Open-Meteo returned a non-JSON body: {Truncate(body)}";
                return null;
            }

            var response = System.Text.Json.JsonSerializer.Deserialize<OpenMeteoResponse>(
                body,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

            if (response?.Current is null || response.Daily is null)
            {
                LastError = "Open-Meteo returned JSON with no current or daily block.";
                return null;
            }

            var days = new List<WeatherDay>();

            for (var i = 0; i < response.Daily.Time.Count; i++)
            {
                days.Add(new WeatherDay(
                    DateOnly.Parse(response.Daily.Time[i], CultureInfo.InvariantCulture),
                    response.Daily.Max[i],
                    response.Daily.Min[i],
                    response.Daily.Code[i]));
            }

            // Today's sunrise and sunset, local — Open-Meteo returns them in the location's
            // own timezone because the query asks for timezone=auto.
            (DateTimeOffset, DateTimeOffset)? daylight = null;

            if (response.Daily.Sunrise?.Count > 0 && response.Daily.Sunset?.Count > 0)
            {
                daylight = (
                    new DateTimeOffset(DateTime.Parse(response.Daily.Sunrise[0], CultureInfo.InvariantCulture)),
                    new DateTimeOffset(DateTime.Parse(response.Daily.Sunset[0], CultureInfo.InvariantCulture)));
            }

            return new WeatherReport(
                new WeatherNow(
                    response.Current.Temperature,
                    response.Current.ApparentTemperature,
                    response.Current.Code),
                days,
                daylight);
        }
        catch (Exception ex)
        {
            // Offline, slow, or the service is having a day. The caller renders that as
            // unavailable rather than showing a number it cannot stand behind — the same
            // rule the vehicle signals follow.
            //
            // The reason is kept, though. "Weather unavailable" with no way to find out why
            // is the kind of dead end that wastes an evening.
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>Enough of a bad response to recognise it, not enough to fill a log.</summary>
    private static string Truncate(string text) =>
        text.Length <= 120 ? text.Trim() : text[..120].Trim() + "…";

    /// <summary>
    /// A WMO weather code as something a person reads at a glance.
    /// </summary>
    /// <remarks>
    /// Words rather than glyphs, deliberately. The dash's whole visual language is
    /// typographic — big numerals, mono captions — and a row of tiny weather icons would be
    /// the one place asking to be squinted at.
    /// </remarks>
    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mainly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 or 63 or 65 => "Rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 => "Snow",
        77 => "Snow grains",
        80 or 81 or 82 => "Showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm, hail",
        _ => "—",
    };

    private sealed record OpenMeteoResponse(
        [property: JsonPropertyName("current")] CurrentBlock? Current,
        [property: JsonPropertyName("daily")] DailyBlock? Daily);

    private sealed record CurrentBlock(
        [property: JsonPropertyName("temperature_2m")] double Temperature,
        [property: JsonPropertyName("apparent_temperature")] double ApparentTemperature,
        [property: JsonPropertyName("weather_code")] int Code);

    private sealed record DailyBlock(
        [property: JsonPropertyName("time")] IReadOnlyList<string> Time,
        [property: JsonPropertyName("weather_code")] IReadOnlyList<int> Code,
        [property: JsonPropertyName("temperature_2m_max")] IReadOnlyList<double> Max,
        [property: JsonPropertyName("temperature_2m_min")] IReadOnlyList<double> Min,
        [property: JsonPropertyName("sunrise")] IReadOnlyList<string>? Sunrise,
        [property: JsonPropertyName("sunset")] IReadOnlyList<string>? Sunset);
}

using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DashDeck.Host.Converters;

/// <summary>
/// A weather code to a drawable icon.
/// </summary>
/// <remarks>
/// The clock stage said conditions in words; an icon reads faster at a glance and across the
/// room, which is what that stage is for. The geometries are monochrome single paths (Material
/// weather set, 24×24), so a <c>Path</c> fills them with a theme brush and they follow day and
/// night like everything else — an icon with a baked-in colour would be the one thing on the
/// stage that did not.
/// <para>
/// Grouped by <b>what to draw</b>, not by every WMO code: drizzle, rain, freezing rain and
/// showers are all "rain" to a glance, so they share the drop. The precise word still exists
/// for anyone who wants it (<see cref="DashDeck.Host.Stage.Weather.Describe"/>).
/// </para>
/// </remarks>
public sealed class WeatherIconConverter : IValueConverter
{
    private const string Sunny =
        "M12,7A5,5 0 0,1 17,12A5,5 0 0,1 12,17A5,5 0 0,1 7,12A5,5 0 0,1 12,7M12,9A3,3 0 0,0 9,12A3,3 0 0,0 12,15A3,3 0 0,0 15,12A3,3 0 0,0 12,9M12,2L14.39,5.42C13.65,5.15 12.84,5 12,5C11.16,5 10.35,5.15 9.61,5.42L12,2M3.34,7L7.5,6.65C6.9,7.16 6.36,7.78 5.94,8.5C5.5,9.24 5.25,10 5.11,10.79L3.34,7M3.36,17L5.12,13.23C5.26,14 5.53,14.78 5.95,15.5C6.37,16.24 6.91,16.86 7.5,17.37L3.36,17M20.65,7L18.88,10.79C18.74,10 18.47,9.23 18.05,8.5C17.63,7.78 17.1,7.15 16.5,6.64L20.65,7M20.64,17L16.5,17.36C17.09,16.85 17.62,16.23 18.04,15.5C18.46,14.77 18.73,14 18.87,13.21L20.64,17M12,22L9.59,18.56C10.33,18.83 11.14,19 12,19C12.82,19 13.63,18.83 14.37,18.56L12,22Z";

    private const string PartlyCloudy =
        "M12.74,5.47C15.1,6.5 16.35,9.03 15.92,11.46C17.19,12.56 18,14.19 18,16V16.17C18.31,16.06 18.65,16 19,16A3,3 0 0,1 22,19A3,3 0 0,1 19,22H6A4,4 0 0,1 2,18A4,4 0 0,1 6,14H6.27C5,12.45 4.6,10.24 5.5,8.26C6.72,5.5 9.97,4.24 12.74,5.47M11.93,7.3C10.16,6.5 8.09,7.31 7.31,9.07C6.85,10.09 6.93,11.22 7.41,12.13C8.5,10.83 10.16,10 12,10C12.7,10 13.38,10.12 14,10.34C13.94,9.06 13.18,7.86 11.93,7.3M13.55,3.64C13,3.4 12.45,3.23 11.88,3.12L14.2,1.67L14.65,4.32C14.29,4.09 13.93,3.86 13.55,3.64M6.09,4.44C5.6,4.79 5.17,5.19 4.8,5.63L4.91,2.82L7.87,3.5C7.25,3.61 6.65,3.85 6.09,4.19M18,9.71C17.91,9.12 17.78,8.55 17.59,8L19.97,9.5L17.7,10.68C17.82,10.36 17.91,10.03 18,9.71M4.87,9.31C4.96,9.9 5.09,10.47 5.28,11L2.9,9.5L5.17,8.33C5.05,8.65 4.96,9 4.87,9.31Z";

    private const string Cloudy =
        "M6,19A5,5 0 0,1 1,14A5,5 0 0,1 6,9C7,6.65 9.3,5 12,5C15.43,5 18.24,7.66 18.5,11.03L19,11A4,4 0 0,1 23,15A4,4 0 0,1 19,19H6M19,13H17V12A5,5 0 0,0 12,7C9.5,7 7.45,8.82 7.06,11.19C6.73,11.07 6.37,11 6,11A3,3 0 0,0 3,14A3,3 0 0,0 6,17H19A2,2 0 0,0 21,15A2,2 0 0,0 19,13Z";

    private const string Fog =
        "M3,15H13A1,1 0 0,1 14,16A1,1 0 0,1 13,17H3A1,1 0 0,1 2,16A1,1 0 0,1 3,15M16,15H21A1,1 0 0,1 22,16A1,1 0 0,1 21,17H16A1,1 0 0,1 15,16A1,1 0 0,1 16,15M1,12A1,1 0 0,1 2,11H16A1,1 0 0,1 17,12A1,1 0 0,1 16,13H2A1,1 0 0,1 1,12M20,13A1,1 0 0,1 19,12A1,1 0 0,1 20,11A1,1 0 0,1 21,12A1,1 0 0,1 20,13M5,9A1,1 0 0,1 4,8A1,1 0 0,1 5,7H15A1,1 0 0,1 16,8A1,1 0 0,1 15,9H5Z";

    private const string Rainy =
        "M6,14.03A1,1 0 0,1 7,15.03C7,15.58 6.55,16.03 6,16.03C3.24,16.03 1,13.79 1,11.03C1,8.27 3.24,6.03 6,6.03C7,3.68 9.3,2.03 12,2.03C15.43,2.03 18.24,4.69 18.5,8.06L19,8.03A4,4 0 0,1 23,12.03C23,14.23 21.21,16.03 19,16.03H18C17.45,16.03 17,15.58 17,15.03C17,14.47 17.45,14.03 18,14.03H19A2,2 0 0,0 21,12.03A2,2 0 0,0 19,10.03H17V9.03C17,6.27 14.76,4.03 12,4.03C9.5,4.03 7.45,5.84 7.06,8.21C6.73,8.09 6.37,8.03 6,8.03A3,3 0 0,0 3,11.03A3,3 0 0,0 6,14.03M12,14.15C12.18,14.39 12.37,14.66 12.56,14.94C13,15.56 14,17.03 14,18.03A2,2 0 0,1 12,20.03A2,2 0 0,1 10,18.03C10,17.03 11,15.56 11.44,14.94L12,14.15Z";

    private const string Snowy =
        "M6,14A1,1 0 0,1 7,15A1,1 0 0,1 6,16A5,5 0 0,1 1,11A5,5 0 0,1 6,6C7,3.65 9.3,2 12,2C15.43,2 18.24,4.66 18.5,8.03L19,8A4,4 0 0,1 23,12A4,4 0 0,1 19,16H18A1,1 0 0,1 17,15A1,1 0 0,1 18,14H19A2,2 0 0,0 21,12A2,2 0 0,0 19,10H17V9A5,5 0 0,0 12,4C9.5,4 7.45,5.82 7.06,8.19C6.73,8.07 6.37,8 6,8A3,3 0 0,0 3,11A3,3 0 0,0 6,14M7.88,18.75L9.18,18.05L7.88,17.34C7.4,17.08 7.22,16.5 7.47,16C7.73,15.55 8.31,15.37 8.8,15.63L10,16.29V14.66C10,14.11 10.45,13.66 11,13.66C11.55,13.66 12,14.11 12,14.66V16.29L13.2,15.63C13.69,15.37 14.27,15.55 14.53,16C14.78,16.5 14.6,17.08 14.12,17.34L12.82,18.05L14.12,18.75C14.6,19 14.78,19.6 14.53,20.08C14.27,20.55 13.69,20.73 13.2,20.47L12,19.81V21.44C12,22 11.55,22.44 11,22.44C10.45,22.44 10,22 10,21.44V19.81L8.8,20.47C8.31,20.73 7.73,20.55 7.47,20.08C7.22,19.6 7.4,19 7.88,18.75Z";

    private const string Thunder =
        "M6,16A5,5 0 0,1 1,11A5,5 0 0,1 6,6C7,3.65 9.3,2 12,2C15.43,2 18.24,4.66 18.5,8.03L19,8A4,4 0 0,1 23,12A4,4 0 0,1 19,16H18A1,1 0 0,1 17,15A1,1 0 0,1 18,14H19A2,2 0 0,0 21,12A2,2 0 0,0 19,10H17V9A5,5 0 0,0 12,4C9.5,4 7.45,5.82 7.06,8.19C6.73,8.07 6.37,8 6,8A3,3 0 0,0 3,11A3,3 0 0,0 6,14A1,1 0 0,1 7,15A1,1 0 0,1 6,16M11.31,14L12.75,11H10.25L13.44,17H14.06L12.75,20H15.25L12.06,14H11.31Z";

    private static readonly Dictionary<string, Geometry> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var code = value as int? ?? -1;

        var data = code switch
        {
            0 => Sunny,
            1 or 2 => PartlyCloudy,
            3 => Cloudy,
            45 or 48 => Fog,
            71 or 73 or 75 or 77 or 85 or 86 => Snowy,
            95 or 96 or 99 => Thunder,
            >= 51 and <= 82 => Rainy,   // drizzle, rain, freezing rain, showers — all "rain"
            _ => Cloudy,
        };

        if (!Cache.TryGetValue(data, out var geometry))
        {
            geometry = Geometry.Parse(data);
            geometry.Freeze();
            Cache[data] = geometry;
        }

        return geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

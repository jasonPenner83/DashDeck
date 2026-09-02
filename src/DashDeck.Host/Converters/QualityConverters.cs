using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DashDeck.Abstractions;

namespace DashDeck.Host.Converters;

/// <summary>
/// Shared colours for the four signal qualities.
/// </summary>
/// <remarks>
/// Quality is rendered, never hidden — and always as colour <i>plus</i> a word, so the
/// distinction does not rest on hue alone. It has to survive a sunlit windscreen, a
/// tweaked accent (B1), and whoever is glancing at it.
/// </remarks>
internal static class QualityPalette
{
    internal static readonly SolidColorBrush Fault = Frozen("#FFE5544B");

    internal static readonly SolidColorBrush Live = Frozen("#FF5BC77E");
    internal static readonly SolidColorBrush Stale = Frozen("#FFE0B23C");
    internal static readonly SolidColorBrush Unavailable = Frozen("#FF6B665E");
    internal static readonly SolidColorBrush Simulated = Frozen("#FF8DA6FF");

    internal static readonly SolidColorBrush ValueUsable = Frozen("#FFF4F1EB");
    internal static readonly SolidColorBrush ValueStale = Frozen("#FF6B665E");
    internal static readonly SolidColorBrush ValueAbsent = Frozen("#FF3A3630");

    internal static SolidColorBrush For(SignalQuality quality) => quality switch
    {
        SignalQuality.Live => Live,
        SignalQuality.Stale => Stale,
        SignalQuality.Simulated => Simulated,
        _ => Unavailable,
    };

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Signal quality to the chip's colour.</summary>
public sealed class QualityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        QualityPalette.For(value as SignalQuality? ?? SignalQuality.Unavailable);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Signal quality to the word shown beside the chip.</summary>
public sealed class QualityToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as SignalQuality? ?? SignalQuality.Unavailable) switch
        {
            SignalQuality.Live => "LIVE",
            SignalQuality.Stale => "STALE",
            SignalQuality.Simulated => "SIM",
            _ => "UNAVAIL",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Signal quality to the colour of the number itself.
/// </summary>
/// <remarks>
/// A stale reading dims rather than disappearing, and an absent one is barely there at all.
/// The number never lies about how much it can be trusted.
/// </remarks>
public sealed class QualityToValueBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as SignalQuality? ?? SignalQuality.Unavailable) switch
        {
            SignalQuality.Live or SignalQuality.Simulated => QualityPalette.ValueUsable,
            SignalQuality.Stale => QualityPalette.ValueStale,
            _ => QualityPalette.ValueAbsent,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

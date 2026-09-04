using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DashDeck.Host.Converters;

/// <summary>
/// A 0-to-1 fraction as a star <see cref="GridLength"/>, for the filled part of a bar.
/// </summary>
/// <remarks>
/// A bar drawn from two star columns rather than from a pixel width, because a card is 270
/// wide at one column and 560 at two and the same template has to serve both. Stars are the
/// one measurement in WPF that already knows how wide its parent is.
/// <para>
/// The floor is not cosmetic: two zero-star columns collapse to nothing and WPF renders the
/// row as if neither existed, so an empty bar would vanish rather than read as empty.
/// </para>
/// </remarks>
public sealed class FractionToStarConverter : IValueConverter
{
    /// <summary>Set to invert — the unfilled remainder rather than the filled part.</summary>
    public bool Inverse { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fraction = value is double d && !double.IsNaN(d) ? Math.Clamp(d, 0, 1) : 0;
        return new GridLength(Math.Max(Inverse ? 1 - fraction : fraction, 0.0001), GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// True becomes one band, false becomes nothing.
/// </summary>
/// <remarks>
/// For the stage's action bar row, which is either exactly one band tall or not there at all.
/// Collapsing to zero rather than hiding the content matters: an occupant without a bar must
/// get the whole stage, not the whole stage minus an invisible band.
/// </remarks>
public sealed class BoolToBandLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new GridLength(value is true ? BandGrid.BandHeight : 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when false. For the things that show only when a mode is off.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bound string is empty — for a text-box watermark.</summary>
public sealed class EmptyToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// True when both bound values are equal.
/// </summary>
/// <remarks>
/// For the page dots: each one is a page, and whether it is lit depends on a number that
/// lives on the dashboard rather than on the page. Comparing the two in a multi-binding
/// keeps the pages plain data — a per-dot view-model that had to be told when the page
/// changed would be a second copy of the same state, and the copy that is wrong is always
/// the one being rendered.
/// </remarks>
public sealed class EqualityConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && Equals(values[0], values[1]);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

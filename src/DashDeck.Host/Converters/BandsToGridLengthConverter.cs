using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DashDeck.Host.Converters;

/// <summary>
/// A band count to a row height, so the stage and the region below it split the six bands
/// between them at runtime rather than being hard-coded to one arrangement.
/// </summary>
/// <remarks>
/// This is what makes the stage a negotiated size instead of a fixed one: an occupant asks
/// for what it needs — three bands for 16:9 video, four for a map — and whatever sits below
/// takes the remainder. Widget rows still line up either way, because a band <em>is</em> a
/// widget row.
/// </remarks>
public sealed class BandsToGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new GridLength(BandGrid.Height(value as int? ?? 0));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

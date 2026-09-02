using System.Windows;
using DashDeck.Abstractions;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Stage;

/// <summary>
/// The stage when nothing else has been chosen: a clock, and the weather.
/// </summary>
/// <remarks>
/// This replaced an explicitly empty stage. "Nothing" was honest while there was nothing to
/// put there, but an idle dash showing a large clock is more use than an idle dash
/// announcing its own emptiness — and it gives the four bands something to justify holding.
/// <para>
/// Notably the first occupant that is plain WPF rather than a hosted child window, so it
/// composes with everything around it. The airspace rules that shaped the launcher do not
/// apply to it.
/// </para>
/// </remarks>
public sealed class ClockWeatherStageOccupant(IClock clock, WeatherService weather) : IStageOccupant
{
    private readonly ClockWeatherViewModel _viewModel = new(clock, weather);

    /// <inheritdoc />
    public string Name => "CLOCK";

    /// <inheritdoc />
    public FrameworkElement CreateView() => new ClockWeatherView { DataContext = _viewModel };

    /// <inheritdoc />
    public string Describe() =>
        $"clock={_viewModel.TimeText} weather={_viewModel.WeatherStatus} " +
        $"temp={_viewModel.TemperatureText} forecast={_viewModel.Forecast.Count} days" +
        (Weather.LastError is { } error ? $" error=[{error}]" : string.Empty);

    /// <inheritdoc />
    public void Dispose() => _viewModel.Dispose();
}

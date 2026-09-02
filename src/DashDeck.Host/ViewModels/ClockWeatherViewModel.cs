using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DashDeck.Abstractions;
using DashDeck.Host.Stage;

namespace DashDeck.Host.ViewModels;

/// <summary>One day in the forecast strip.</summary>
public sealed record ForecastDay(string Day, string High, string Low, string Condition);

/// <summary>
/// The idle stage: what time it is, and what it is doing outside.
/// </summary>
/// <remarks>
/// The stage is never empty now — this is what sits there when nothing else has been
/// chosen. It is also the first occupant that renders as ordinary WPF rather than into a
/// child window, so it composes with the rest of the shell normally: no airspace problem,
/// and controls could be drawn over it if there were ever a reason to.
/// </remarks>
public sealed partial class ClockWeatherViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// How often the forecast is refetched.
    /// </summary>
    /// <remarks>
    /// Weather does not change faster than this, and the tablet is on someone's Wi-Fi.
    /// Polling harder would be rude to a free service and buy nothing.
    /// </remarks>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(15);

    private readonly IClock _clock;
    private readonly DispatcherTimer _tick;
    private readonly CancellationTokenSource _stopping = new();

    private DateTimeOffset _fetchedAt;
    private bool _disposed;

    [ObservableProperty]
    private string _timeText = "--:--";

    [ObservableProperty]
    private string _dateText = string.Empty;

    [ObservableProperty]
    private string _temperatureText = "——";

    [ObservableProperty]
    private string _conditionText = "Weather unavailable";

    [ObservableProperty]
    private string _feelsLikeText = string.Empty;

    [ObservableProperty]
    private string _weatherStatus = "FETCHING";

    [ObservableProperty]
    private bool _hasWeather;

    [ObservableProperty]
    private IReadOnlyList<ForecastDay> _forecast = [];

    public ClockWeatherViewModel(IClock clock)
    {
        _clock = clock;

        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };

        _tick.Tick += (_, _) => TickClock();
        _tick.Start();

        TickClock();
        _ = RefreshWeatherAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tick.Stop();
        _stopping.Cancel();
        _stopping.Dispose();
    }

    private void TickClock()
    {
        // IClock, not DateTimeOffset.Now — the convention holds here too, so a replayed
        // drive shows the time the drive happened.
        var now = _clock.UtcNow.ToLocalTime();

        TimeText = now.ToString("HH:mm", CultureInfo.CurrentCulture);
        DateText = now.ToString("dddd d MMMM", CultureInfo.CurrentCulture);

        if (HasWeather)
        {
            var age = now - _fetchedAt;

            // Say how old it is once it stops being current, rather than presenting an
            // hours-old temperature as though it were now.
            WeatherStatus = age > RefreshEvery + TimeSpan.FromMinutes(5)
                ? $"STALE {age.TotalMinutes:0}m"
                : "LIVE";

            if (age > RefreshEvery)
            {
                _ = RefreshWeatherAsync();
            }
        }
    }

    private async Task RefreshWeatherAsync()
    {
        var report = await Weather.FetchAsync(
            Weather.DefaultLatitude,
            Weather.DefaultLongitude,
            _stopping.Token).ConfigureAwait(true);

        if (_disposed)
        {
            return;
        }

        if (report is null)
        {
            // No number is better than a wrong one. The clock keeps working regardless,
            // which is the point of the two being on the same face.
            HasWeather = false;
            TemperatureText = "——";
            ConditionText = "Weather unavailable";
            FeelsLikeText = string.Empty;
            WeatherStatus = "OFFLINE";
            Forecast = [];
            return;
        }

        _fetchedAt = _clock.UtcNow.ToLocalTime();

        HasWeather = true;
        TemperatureText = report.Now.TemperatureC.ToString("0", CultureInfo.CurrentCulture);
        ConditionText = Weather.Describe(report.Now.Code);
        FeelsLikeText = string.Create(
            CultureInfo.CurrentCulture,
            $"feels {report.Now.FeelsLikeC:0}°");
        WeatherStatus = "LIVE";

        Forecast =
        [
            .. report.Forecast.Select(d => new ForecastDay(
                d.Date.ToDateTime(TimeOnly.MinValue).ToString("ddd", CultureInfo.CurrentCulture).ToUpperInvariant(),
                d.MaxC.ToString("0", CultureInfo.CurrentCulture),
                d.MinC.ToString("0", CultureInfo.CurrentCulture),
                Weather.Describe(d.Code))),
        ];
    }
}

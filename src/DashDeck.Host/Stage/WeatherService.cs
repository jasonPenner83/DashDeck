using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DashDeck.Abstractions;

namespace DashDeck.Host.Stage;

/// <summary>
/// One fetch of the forecast, shared by everything that wants it.
/// </summary>
/// <remarks>
/// <b>There were two fetchers before this, and there was nearly a third.</b> The clock face
/// fetched for its own display and <c>ThemeService</c> fetched the same endpoint again for
/// sunrise and sunset, on separate timers with separate backoff. That is the arrangement that
/// produced the worst bug in this project's history — one request per second, indefinitely, at
/// a free keyless API, because one of those two backoffs was timed from the last success and
/// the success never came. Adding weather to the status strip would have made three.
/// <para>
/// So the fetching lives here, once, with one backoff, and the consumers observe. It is owned
/// by the shell rather than by any occupant, because the clock face comes and goes with the
/// stage and the status strip never does.
/// </para>
/// </remarks>
public sealed partial class WeatherService : ObservableObject, IDisposable
{
    /// <summary>Weather does not change faster than this, and polling harder would be rude.</summary>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long to wait after a failed attempt.
    /// </summary>
    /// <remarks>
    /// Timed from the <em>attempt</em>, not the success. That distinction is the whole fix for
    /// the once-a-second bug: a backoff measured from a success that never arrives is not a
    /// backoff at all.
    /// </remarks>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    private readonly IClock _clock;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _stopping = new();

    private DateTimeOffset _attemptedAt = DateTimeOffset.MinValue;
    private bool _inFlight;
    private bool _disposed;

    [ObservableProperty]
    private WeatherReport? _report;

    /// <summary>LIVE, STALE, OFFLINE or FETCHING. Rendered, never hidden.</summary>
    [ObservableProperty]
    private string _status = "FETCHING";

    public WeatherService(IClock clock)
    {
        _clock = clock;

        // A minute is plenty: this only decides whether a fetch is due and whether what we
        // have has aged out. The fetch itself happens at most every fifteen.
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };

        _timer.Tick += (_, _) => Poll();
        _timer.Start();

        Poll();
    }

    /// <summary>Raised after a successful fetch, for consumers that cache derived values.</summary>
    public event Action<WeatherReport>? Updated;

    /// <summary>True when there is a reading worth showing.</summary>
    public bool HasWeather => Report is not null;

    /// <summary>Sunrise and sunset, for the theme's Auto mode.</summary>
    public (DateTimeOffset Sunrise, DateTimeOffset Sunset)? Daylight => Report?.Daylight;

    /// <summary>When the last successful fetch landed.</summary>
    public DateTimeOffset? FetchedAt { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _stopping.Cancel();
        _stopping.Dispose();
    }

    private void Poll()
    {
        if (Report is not null && FetchedAt is { } fetched)
        {
            var age = _clock.UtcNow - fetched;

            // Say how old it is once it stops being current, rather than presenting an
            // hours-old temperature as though it were now.
            Status = age > RefreshEvery + TimeSpan.FromMinutes(5)
                ? $"STALE {age.TotalMinutes:0}m"
                : "LIVE";
        }

        var due = Report is null ? RetryAfter : RefreshEvery;

        if (!_inFlight && _clock.UtcNow - _attemptedAt > due)
        {
            _ = RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        // Stamped before the await and cleared in a finally, so a failure — or a request
        // still in flight — cannot leave the poll thinking another attempt is due.
        _attemptedAt = _clock.UtcNow;
        _inFlight = true;

        try
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
                // No number is better than a wrong one. Anything already fetched is kept and
                // ages visibly rather than being blanked by one bad request.
                if (Report is null)
                {
                    Status = "OFFLINE";
                }

                return;
            }

            Report = report;
            FetchedAt = _clock.UtcNow;
            Status = "LIVE";

            Updated?.Invoke(report);
        }
        finally
        {
            _inFlight = false;
        }
    }
}

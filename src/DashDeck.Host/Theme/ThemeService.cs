using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DashDeck.Abstractions;
using DashDeck.Host.Stage;

namespace DashDeck.Host.Theme;

/// <summary>How the day/night palette is chosen.</summary>
public enum ThemeMode
{
    /// <summary>Full brightness, always.</summary>
    Day,

    /// <summary>Dimmed, always.</summary>
    Night,

    /// <summary>Decided for you. See <see cref="ThemeService"/> for what actually decides it.</summary>
    Auto,
}

/// <summary>
/// Owns the live palette: day or night, and which accent.
/// </summary>
/// <remarks>
/// Applied by mutating the <see cref="SolidColorBrush"/> instances already sitting in
/// <c>Application.Resources</c>. Everything in the shell binds to those brushes by
/// <c>StaticResource</c>, which resolves to the instance rather than a copy — so changing a
/// brush's colour repaints every control using it, with no <c>DynamicResource</c> churn and
/// no XAML changes anywhere. The brushes must therefore stay unfrozen, which is why none of
/// them are declared with <c>PresentationOptions:Freeze</c>.
/// </remarks>
public sealed partial class ThemeService : ObservableObject
{
    private readonly IClock _clock;

    private (DateTimeOffset Sunrise, DateTimeOffset Sunset)? _daylight;
    private DateTimeOffset _daylightFetchedAt;

    [ObservableProperty]
    private ThemeMode _mode = ThemeMode.Auto;

    [ObservableProperty]
    private AccentOption _accent = AccentOption.Ember;

    [ObservableProperty]
    private bool _isNight;

    /// <summary>
    /// What Auto is currently deciding from — shown in settings, because an automatic
    /// setting that will not say why it did something is infuriating.
    /// </summary>
    [ObservableProperty]
    private string _autoSource = "Waiting for sunrise and sunset";

    public ThemeService(IClock clock)
    {
        _clock = clock;
        Apply();
        _ = RefreshDaylightAsync();
    }

    /// <summary>Re-evaluate Auto. Called on a timer by the shell.</summary>
    public void Reevaluate()
    {
        if (Mode is ThemeMode.Auto)
        {
            Apply();
        }

        // Sunrise and sunset move; refetch about once a day.
        if (_clock.UtcNow - _daylightFetchedAt > TimeSpan.FromHours(12))
        {
            _ = RefreshDaylightAsync();
        }
    }

    partial void OnModeChanged(ThemeMode value) => Apply();

    partial void OnAccentChanged(AccentOption value) => Apply();

    private void Apply()
    {
        IsNight = Mode switch
        {
            ThemeMode.Day => false,
            ThemeMode.Night => true,
            _ => ResolveAuto(),
        };

        var palette = IsNight ? ThemePalette.Night : ThemePalette.Day;
        var accent = IsNight ? ThemePalette.Dim(Accent.Colour, 0.82) : Accent.Colour;

        SetBrush("CanvasBrush", palette.Canvas);
        SetBrush("SurfaceBrush", palette.Surface);
        SetBrush("RaisedBrush", palette.Raised);
        SetBrush("HairlineBrush", palette.Hairline);
        SetBrush("HairlineStrongBrush", palette.HairlineStrong);
        SetBrush("TextHighBrush", palette.TextHigh);
        SetBrush("TextMidBrush", palette.TextMid);
        SetBrush("TextLowBrush", palette.TextLow);
        SetBrush("TextFaintBrush", palette.TextFaint);

        SetBrush("AccentBrush", accent);
        SetBrush("AccentWashBrush", Color.FromArgb(0x14, accent.R, accent.G, accent.B));
    }

    /// <summary>
    /// Decide day or night without being told.
    /// </summary>
    /// <remarks>
    /// <b>Headlights would be the right signal</b> — the driver has already made the
    /// judgement, and a tunnel or a heavy prairie storm is night as far as a screen is
    /// concerned. It is not available: lighting status is not in the legislated OBD-II set,
    /// it lives on the Ford body module over MS-CAN, and reaching it depends on PID
    /// discovery against the real truck (R2) and on what the Gateway Module lets through
    /// (R4, Q5).
    /// <para>
    /// So Auto runs on sunrise and sunset, which are already being fetched for the clock
    /// face and are right most of the time. When a headlight signal exists it becomes just
    /// another named signal and this method changes; nothing above it does.
    /// </para>
    /// </remarks>
    private bool ResolveAuto()
    {
        if (_daylight is not { } window)
        {
            // No answer yet. Day is the safer default — a dash that is too bright is
            // annoying, one that is too dim in sunlight is unreadable.
            AutoSource = "Sunrise and sunset unavailable — assuming day";
            return false;
        }

        var now = _clock.UtcNow.ToLocalTime();
        var night = now < window.Sunrise || now >= window.Sunset;

        AutoSource = string.Create(
            System.Globalization.CultureInfo.CurrentCulture,
            $"Sunrise {window.Sunrise:HH:mm}, sunset {window.Sunset:HH:mm}");

        return night;
    }

    private async Task RefreshDaylightAsync()
    {
        var report = await Weather.FetchAsync(
            Weather.DefaultLatitude,
            Weather.DefaultLongitude,
            CancellationToken.None).ConfigureAwait(true);

        if (report?.Daylight is not { } daylight)
        {
            return;
        }

        _daylight = daylight;
        _daylightFetchedAt = _clock.UtcNow;

        if (Mode is ThemeMode.Auto)
        {
            Apply();
        }
    }

    /// <summary>
    /// Replace a token's brush.
    /// </summary>
    /// <remarks>
    /// Replacement rather than mutation, and the theme keys are referenced with
    /// <c>DynamicResource</c> rather than <c>StaticResource</c>, because the obvious approach
    /// silently does nothing: WPF freezes the <see cref="SolidColorBrush"/> instances in a
    /// compiled resource dictionary, so setting <c>.Color</c> on one is a no-op — and a
    /// <c>StaticResource</c> reference has already captured the old instance anyway.
    /// <para>
    /// Found by measuring pixels in a screenshot after day and night rendered identically.
    /// </para>
    /// </remarks>
    private static void SetBrush(string key, Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();

        Application.Current.Resources[key] = brush;
    }
}

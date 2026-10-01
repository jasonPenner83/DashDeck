using CommunityToolkit.Mvvm.ComponentModel;

namespace DashDeck.Host.Settings;

/// <summary>
/// Display preferences that are not the theme.
/// </summary>
/// <remarks>
/// Separate from <c>ThemeService</c> because it is a different kind of thing — the theme owns
/// a live palette and rewrites brushes, this owns a number — and because splitting the owners
/// is what forced <see cref="SettingsStore.Update"/> to exist. Both write the same file, and
/// neither may clobber the other.
/// <para>
/// Observed rather than polled: a web occupant subscribes and re-zooms itself, so changing
/// the scale takes effect on whatever is on the stage without leaving Settings.
/// </para>
/// </remarks>
public sealed partial class DisplaySettings : ObservableObject
{
    /// <summary>
    /// The scales offered. Weighted downwards, because that is the direction that helps.
    /// </summary>
    /// <remarks>
    /// The web players are laid out for a phone held at arm's length; on a 912-wide stage a
    /// hundred per cent spends most of the width on padding. Above 1 is offered anyway —
    /// somebody reading a map at a glance may want the opposite of what somebody browsing a
    /// library wants.
    /// </remarks>
    public static readonly IReadOnlyList<double> Choices = [0.5, 0.6, 0.7, 0.8, 0.9, 1.0, 1.25];

    /// <summary>What WebView2 will accept. Anything outside it throws rather than clamping.</summary>
    public const double MinimumScale = 0.25;

    public const double MaximumScale = 5.0;

    private bool _loaded;

    [ObservableProperty]
    private double _webScale = 1.0;

    /// <summary>
    /// Keep an audio/video source playing in the background when the stage switches to a silent
    /// occupant (ADR-0026). Read at switch time by the shell; persisted like the scale.
    /// </summary>
    [ObservableProperty]
    private bool _keepStageAudio;

    /// <summary>Take GPS from the phone over the network (ADR-0027). Applied at next launch.</summary>
    [ObservableProperty]
    private bool _gpsEnabled;

    /// <summary>The phone's GPS-share endpoint, <c>host:port</c>. Applied at next launch.</summary>
    [ObservableProperty]
    private string _gpsEndpoint = "";

    /// <summary>How the phone's GPS arrives — <c>Bluetooth</c> or <c>Network</c>. Applied at next launch.</summary>
    [ObservableProperty]
    private string _gpsTransport = "Bluetooth";

    /// <summary>The paired phone's virtual COM port for the Bluetooth transport. Applied at next launch.</summary>
    [ObservableProperty]
    private string _gpsSerialPort = "";

    /// <summary>Usable fuel tank, litres (ADR-0029). Read into the vehicle profile at launch, so a
    /// change applies the next time — a tank does not change size while you drive.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FuelTankGallons))]
    private double _fuelTankLitres = 136;

    /// <summary>
    /// The OBD-II adapter's COM port. Empty runs the synthetic truck. Applied at next launch.
    /// </summary>
    /// <remarks>
    /// Saved on every keystroke rather than on focus loss, unlike the tank size beside it.
    /// A tank size needs focus loss because half-typed digits are a different number; a
    /// port name does not, and the field is one people type and then immediately relaunch —
    /// losing it that way costs more than the extra writes.
    /// </remarks>
    [ObservableProperty]
    private string _adapterSerialPort = "";

    public DisplaySettings()
    {
        var stored = SettingsStore.Load();
        WebScale = Clamp(stored.WebScale);
        KeepStageAudio = stored.KeepStageAudio;
        GpsEnabled = stored.GpsEnabled;
        GpsEndpoint = stored.GpsEndpoint;
        GpsTransport = stored.GpsTransport;
        GpsSerialPort = stored.GpsSerialPort;
        FuelTankLitres = stored.FuelTankLitres;
        AdapterSerialPort = stored.AdapterSerialPort;
        _loaded = true;
    }

    /// <summary>The tank in US gallons, for the hint beside the litres field — Jason thinks in gallons.</summary>
    public string FuelTankGallons => $"≈ {FuelTankLitres / 3.785411784:0.0} US gal";

    /// <summary>The current scale as a percentage, for the screen.</summary>
    public string WebScaleText => $"{WebScale * 100:0}%";

    /// <summary>
    /// Bring a stored or supplied scale into a range WebView2 will accept.
    /// </summary>
    /// <remarks>
    /// A zero here would be a divide-by-nothing inside the browser and throws on assignment,
    /// and a settings file is editable by hand — so a value that arrived from outside this
    /// class is never trusted straight.
    /// </remarks>
    public static double Clamp(double scale) =>
        double.IsFinite(scale) ? Math.Clamp(scale, MinimumScale, MaximumScale) : 1.0;

    partial void OnWebScaleChanged(double value)
    {
        OnPropertyChanged(nameof(WebScaleText));

        if (!_loaded)
        {
            return;
        }

        // Written the moment it changes, like every other choice: a dash is closed by having
        // its power pulled (ADR-0014).
        SettingsStore.Update(stored => stored with { WebScale = value });
    }

    partial void OnKeepStageAudioChanged(bool value)
    {
        if (!_loaded)
        {
            return;
        }

        SettingsStore.Update(stored => stored with { KeepStageAudio = value });
    }

    partial void OnGpsEnabledChanged(bool value)
    {
        if (!_loaded)
        {
            return;
        }

        SettingsStore.Update(stored => stored with { GpsEnabled = value });
    }

    partial void OnGpsEndpointChanged(string value)
    {
        if (!_loaded)
        {
            return;
        }

        SettingsStore.Update(stored => stored with { GpsEndpoint = value });
    }

    partial void OnGpsTransportChanged(string value)
    {
        if (!_loaded)
        {
            return;
        }

        SettingsStore.Update(stored => stored with { GpsTransport = value });
    }

    partial void OnGpsSerialPortChanged(string value)
    {
        if (!_loaded)
        {
            return;
        }

        SettingsStore.Update(stored => stored with { GpsSerialPort = value });
    }

    partial void OnFuelTankLitresChanged(double value)
    {
        if (!_loaded)
        {
            return;
        }

        SettingsStore.Update(stored => stored with { FuelTankLitres = value });
    }

    partial void OnAdapterSerialPortChanged(string value)
    {
        if (!_loaded)
        {
            return;
        }

        SettingsStore.Update(stored => stored with { AdapterSerialPort = value });
    }

    /// <summary>Serial ports the OS can see, for the adapter picker.</summary>
    public static IReadOnlyList<string> AvailableSerialPorts()
    {
        try
        {
            return DashDeck.Vehicle.SerialPortTransport.AvailablePorts();
        }
        catch (Exception)
        {
            // Enumerating ports is not worth failing a settings screen over.
            return [];
        }
    }
}

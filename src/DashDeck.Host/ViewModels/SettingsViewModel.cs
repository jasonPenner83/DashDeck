using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Host.Converters;
using DashDeck.Host.Settings;
using DashDeck.Host.Theme;

namespace DashDeck.Host.ViewModels;

/// <summary>One of Day, Night or Auto.</summary>
public sealed partial class ThemeModeOption(string label, string detail, ThemeMode mode) : ObservableObject
{
    [ObservableProperty]
    private bool _isCurrent;

    public string Label { get; } = label;

    public string Detail { get; } = detail;

    public ThemeMode Mode { get; } = mode;
}

/// <summary>One choosable accent, with a swatch to show it.</summary>
public sealed partial class AccentSwatch(AccentOption option) : ObservableObject
{
    [ObservableProperty]
    private bool _isCurrent;

    public AccentOption Option { get; } = option;

    public string Label { get; } = option.Name;

    public Brush Swatch { get; } = new SolidColorBrush(option.Colour);
}

/// <summary>One tab in the settings rail.</summary>
public sealed partial class SettingsSection(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Settings, in sections: appearance, mount, display and diagnostics.
/// </summary>
/// <remarks>
/// One long scroll became a rail and a pane once there was more than one thing to adjust —
/// the sections answer different questions and are reached for at different times.
/// <para>
/// What is adjustable is still deliberately narrow (B1). The palette is a token set, but
/// exposing every token as a colour picker would let someone quietly break the one rule the
/// dash is strictest about: that a value's quality is always legible. So appearance offers a
/// day/night mode and an accent — presets, or any colour that passes
/// <see cref="AccentValidation"/> — and the four quality colours are not on offer at all.
/// </para>
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeService _theme;
    private readonly DashDeck.Host.Sensors.SensorService _sensors;
    private readonly DashDeck.Host.Stage.UserAppStore _userApps;
    private readonly DashDeck.Host.Stage.Launcher.StageLauncherStore _launcher;

    /// <summary>
    /// The settings tabs. One long scroll became a rail and a pane once the sections stopped
    /// fitting a glance � appearance, mount, display and diagnostics answer different questions
    /// and are reached for at different times.
    /// </summary>
    public IReadOnlyList<SettingsSection> Sections { get; } =
    [
        new SettingsSection("APPEARANCE") { IsSelected = true },
        new SettingsSection("THEMES"),
        new SettingsSection("MOUNT"),
        new SettingsSection("DISPLAY"),
        new SettingsSection("VEHICLE"),
        new SettingsSection("SENSORS"),
        new SettingsSection("APPS"),
        new SettingsSection("DIAGNOSTICS"),
    ];

    /// <summary>Which section is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppearance))]
    [NotifyPropertyChangedFor(nameof(IsThemes))]
    [NotifyPropertyChangedFor(nameof(IsMount))]
    [NotifyPropertyChangedFor(nameof(IsDisplay))]
    [NotifyPropertyChangedFor(nameof(IsVehicle))]
    [NotifyPropertyChangedFor(nameof(IsSensors))]
    [NotifyPropertyChangedFor(nameof(IsApps))]
    [NotifyPropertyChangedFor(nameof(IsDiagnostics))]
    private string _section = "APPEARANCE";

    public bool IsAppearance => Section == "APPEARANCE";
    public bool IsThemes => Section == "THEMES";
    public bool IsMount => Section == "MOUNT";
    public bool IsDisplay => Section == "DISPLAY";
    public bool IsVehicle => Section == "VEHICLE";
    public bool IsSensors => Section == "SENSORS";
    public bool IsApps => Section == "APPS";
    public bool IsDiagnostics => Section == "DIAGNOSTICS";

    /// <summary>Switch sections. Bound to the rail.</summary>
    [RelayCommand]
    private void SelectSection(SettingsSection? section)
    {
        if (section is null)
        {
            return;
        }

        Section = section.Name;

        // Opening Vehicle tests the ports, so the list is current when it is looked at (ADR-0034).
        if (IsVehicle)
        {
            _ = Adapter.TestIfStaleAsync();
        }

        foreach (var candidate in Sections)
        {
            candidate.IsSelected = ReferenceEquals(candidate, section);
        }
    }

    public SettingsViewModel(
        ThemeService theme,
        DashDeck.Host.Settings.DisplaySettings display,
        DashDeck.Host.Sensors.SensorService sensors,
        DashDeck.Host.Stage.UserAppStore userApps,
        DashDeck.Host.Stage.Launcher.StageLauncherStore launcher,
        SensorInventoryViewModel inventory,
        VehicleIdentityViewModel vehicle,
        AdapterPortsViewModel adapter)
    {
        Adapter = adapter;
        _theme = theme;
        var dialogs = new ThemeDialogs();
        Themes = new ThemesViewModel(theme, dialogs);
        StageLayouts = new StageLayoutsViewModel(theme.Layouts, dialogs);
        ClimateLayouts = new StageLayoutsViewModel(theme.ClimateLayouts, dialogs);
        Inventory = inventory;
        Vehicle = vehicle;
        Display = display;
        _sensors = sensors;
        _userApps = userApps;
        _launcher = launcher;
        Launcher = new LauncherSettingsViewModel(launcher, dialogs);

        WebScales = [.. DashDeck.Host.Settings.DisplaySettings.Choices.Select(s => new WebScaleOption(s))];
        display.PropertyChanged += (_, _) => SyncScales();
        SyncScales();

        Modes =
        [
            new ThemeModeOption("DAY", "Full brightness", ThemeMode.Day),
            new ThemeModeOption("NIGHT", "Dimmed", ThemeMode.Night),
            new ThemeModeOption("AUTO", "Decide for me", ThemeMode.Auto),
        ];

        Accents = [.. AccentOption.All.Select(a => new AccentSwatch(a))];

        // Start the field on whatever is in use, so editing an accent begins from it rather
        // than from an empty box.
        _customAccentHex = ThemeService.ToHex(theme.Accent.Colour);

        theme.PropertyChanged += (_, _) => Sync();
        Sync();

        // Populate the COM-port list once so the Bluetooth picker has something to show.
        RefreshSerialPorts();
    }

    /// <summary>Choosing, importing and exporting themes (ADR-0036).</summary>
    public ThemesViewModel Themes { get; }

    /// <summary>Which layout the stage shows, and writing your own (ADR-0037).</summary>
    public StageLayoutsViewModel StageLayouts { get; }

    /// <summary>Settings ▸ Themes ▸ CLIMATE LAYOUT (ADR-0040).</summary>
    public StageLayoutsViewModel ClimateLayouts { get; }

    /// <summary>Every signal and sensor, the scan that finds missing ones, and the editor (ADR-0032).</summary>
    public SensorInventoryViewModel Inventory { get; }

    /// <summary>The OBD-II adapter and the tested ports to choose it from (ADR-0034).</summary>
    public AdapterPortsViewModel Adapter { get; }

    /// <summary>Which vehicle this is: its VIN, the decode, the signal pack (ADR-0033).</summary>
    public VehicleIdentityViewModel Vehicle { get; }

    /// <summary>Settings ▸ Vehicle ▸ FUEL (ADR-0041): fill-ups and the calibration they teach.</summary>
    public FuelCalibrationViewModel Fuel { get; init; } = new(null);

    /// <summary>True once the tablet''s mount has been levelled.</summary>
    public bool IsLevelled => _sensors.IsLevelled;

    /// <summary>What the level control says, and what it means.</summary>
    public string LevelCaption => IsLevelled ? "RE-LEVEL THE MOUNT" : "LEVEL THE MOUNT";

    /// <summary>When the current reference was taken, or why there is none.</summary>
    public string LevelDetail => _sensors.Reference.CapturedUtc is { } at
        ? $"Levelled {at.ToLocalTime():d MMM, HH:mm}. Re-level if the mount has moved."
        : "Not levelled. Pitch, roll and G will not render until it is.";

    /// <summary>
    /// Capture the tablet''s orientation as level and forward.
    /// </summary>
    /// <remarks>
    /// Lives here rather than on the compass because it is a calibration done once, parked, on
    /// flat ground � not something reached for while driving. It was on the stage''s action bar
    /// until that row cost more picture than it was worth (ADR-0022).
    /// </remarks>
    [RelayCommand]
    private void Level()
    {
        _sensors.Level();
        OnPropertyChanged(nameof(IsLevelled));
        OnPropertyChanged(nameof(LevelCaption));
        OnPropertyChanged(nameof(LevelDetail));
    }

    /// <summary>How large web occupants render. Shared with them, so a change applies live.</summary>
    public DashDeck.Host.Settings.DisplaySettings Display { get; }

    /// <summary>The scales offered, as chips.</summary>
    public IReadOnlyList<WebScaleOption> WebScales { get; } = [];

    /// <summary>Pick a scale for the web occupants.</summary>
    [RelayCommand]
    private void SetWebScale(WebScaleOption? option)
    {
        if (option is not null)
        {
            Display.WebScale = option.Scale;
        }
    }

    /// <summary>ON/OFF for the keep-stage-audio toggle.</summary>
    public string KeepStageAudioLabel => Display.KeepStageAudio ? "ON" : "OFF";

    /// <summary>Flip whether a source keeps playing when you switch the stage (ADR-0026).</summary>
    [RelayCommand]
    private void ToggleKeepStageAudio()
    {
        Display.KeepStageAudio = !Display.KeepStageAudio;
        OnPropertyChanged(nameof(KeepStageAudioLabel));
    }

    /// <summary>ON/OFF for the phone-GPS toggle (ADR-0027).</summary>
    public string GpsEnabledLabel => Display.GpsEnabled ? "ON" : "OFF";

    /// <summary>Flip whether GPS is taken from the phone. Applied at the next launch.</summary>
    [RelayCommand]
    private void ToggleGps()
    {
        Display.GpsEnabled = !Display.GpsEnabled;
        OnPropertyChanged(nameof(GpsEnabledLabel));
    }

    /// <summary>Which transport carries the GPS — the label shown on the toggle.</summary>
    public string GpsTransportLabel => Display.GpsTransport;

    /// <summary>True when the Bluetooth (COM port) transport is selected.</summary>
    public bool IsBluetoothGps =>
        string.Equals(Display.GpsTransport, "Bluetooth", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the network (host:port) transport is selected.</summary>
    public bool IsNetworkGps => !IsBluetoothGps;

    /// <summary>Switch between Bluetooth and the network for the phone's GPS.</summary>
    [RelayCommand]
    private void ToggleGpsTransport()
    {
        Display.GpsTransport = IsBluetoothGps ? "Network" : "Bluetooth";
        OnPropertyChanged(nameof(GpsTransportLabel));
        OnPropertyChanged(nameof(IsBluetoothGps));
        OnPropertyChanged(nameof(IsNetworkGps));
    }

    /// <summary>The COM ports Windows currently knows — a paired phone shows up here.</summary>
    public System.Collections.ObjectModel.ObservableCollection<SerialPortOption> SerialPorts { get; } = [];

    /// <summary>Re-enumerate the COM ports, so a phone paired after opening Settings appears.</summary>
    [RelayCommand]
    private void RefreshSerialPorts()
    {
        SerialPorts.Clear();

        foreach (var name in System.IO.Ports.SerialPort.GetPortNames()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            SerialPorts.Add(new SerialPortOption(name)
            {
                IsSelected = string.Equals(name, Display.GpsSerialPort, StringComparison.OrdinalIgnoreCase),
            });
        }
    }

    /// <summary>Pick the paired phone's COM port.</summary>
    [RelayCommand]
    private void SelectSerialPort(SerialPortOption? option)
    {
        if (option is null)
        {
            return;
        }

        Display.GpsSerialPort = option.Name;

        foreach (var port in SerialPorts)
        {
            port.IsSelected = ReferenceEquals(port, option);
        }
    }

    private void SyncScales()
    {
        foreach (var option in WebScales)
        {
            // Compared with a tolerance, because these round-trip through JSON as doubles
            // and 0.7 does not always come back as 0.7.
            option.IsSelected = Math.Abs(option.Scale - Display.WebScale) < 0.001;
        }

        OnPropertyChanged(nameof(Display));
    }

    /// <summary>Day, Night, Auto.</summary>
    public IReadOnlyList<ThemeModeOption> Modes { get; }

    /// <summary>The presets. Not the only option — see <see cref="CustomAccentHex"/>.</summary>
    public IReadOnlyList<AccentSwatch> Accents { get; }

    /// <summary>
    /// A hand-typed accent, as <c>#RRGGBB</c>.
    /// </summary>
    /// <remarks>
    /// Checked on every keystroke rather than on apply, so the answer arrives while the
    /// colour is still being chosen instead of after committing to it.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomPreview))]
    [NotifyPropertyChangedFor(nameof(CustomMessage))]
    [NotifyPropertyChangedFor(nameof(CustomIsRejected))]
    [NotifyPropertyChangedFor(nameof(CustomMessageBrush))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCustomAccentCommand))]
    private string _customAccentHex;

    /// <summary>What the typed colour looks like — the point of checking as you type.</summary>
    public Brush CustomPreview => AccentValidation.TryParse(CustomAccentHex, out var colour)
        ? new SolidColorBrush(colour)
        : Brushes.Transparent;

    /// <summary>Why the typed colour cannot be used, or how to type one.</summary>
    public string CustomMessage
    {
        get
        {
            if (!AccentValidation.TryParse(CustomAccentHex, out var colour))
            {
                return "Six hex digits, like #FF7A1A.";
            }

            var check = AccentValidation.Check(colour);
            return check.IsUsable ? "Looks good." : check.Message;
        }
    }

    /// <summary>True when the message is a refusal, so it can be shown in the fault colour.</summary>
    public bool CustomIsRejected =>
        AccentValidation.TryParse(CustomAccentHex, out var colour) && !AccentValidation.Check(colour).IsUsable;

    /// <summary>Fault red while the typed colour is refused, faint otherwise.</summary>
    /// <remarks>
    /// Read from <see cref="QualityPalette"/> rather than declared again in XAML, so the red
    /// that says no here is the same red the dash uses for a fault, permanently.
    /// </remarks>
    public Brush CustomMessageBrush => CustomIsRejected
        ? QualityPalette.Fault
        : (Brush)System.Windows.Application.Current.Resources["TextFaintBrush"];

    /// <summary>Where the choice is written. Shown because a setting you cannot find is a setting you cannot back up.</summary>
    public string SettingsPath => SettingsStore.Path;

    /// <summary>What Auto is currently deciding from, shown only while Auto is selected.</summary>
    public string AutoSource => _theme.AutoSource;

    /// <summary>True while Auto is selected, so the explanation can be hidden otherwise.</summary>
    public bool IsAuto => _theme.Mode is ThemeMode.Auto;

    /// <summary>Which palette is actually in use right now, Auto included.</summary>
    public string ResolvedTheme => _theme.IsNight ? "Night" : "Day";

    [RelayCommand]
    private void SetMode(ThemeModeOption? option)
    {
        if (option is not null)
        {
            _theme.Mode = option.Mode;
        }
    }

    [RelayCommand]
    private void SetAccent(AccentSwatch? swatch)
    {
        if (swatch is not null)
        {
            _theme.Accent = swatch.Option;
            CustomAccentHex = ThemeService.ToHex(swatch.Option.Colour);
        }
    }

    /// <summary>Whether the typed colour is currently applicable. Drives the button's enabled state.</summary>
    private bool CanApplyCustomAccent() =>
        AccentValidation.TryParse(CustomAccentHex, out var colour) && AccentValidation.Check(colour).IsUsable;

    [RelayCommand(CanExecute = nameof(CanApplyCustomAccent))]
    private void ApplyCustomAccent()
    {
        if (AccentValidation.TryParse(CustomAccentHex, out var colour))
        {
            _theme.Accent = new AccentOption("CUSTOM", colour);
        }
    }

    // ---- Stage apps (ADR-0024) ----

    /// <summary>The apps the user has added, bound directly by the list.</summary>
    public System.Collections.ObjectModel.ObservableCollection<DashDeck.Host.Stage.UserAppEntry> Apps => _userApps.Apps;

    /// <summary>Where the app list is written. Shown, so it can be found and backed up.</summary>
    public string AppsPath => _userApps.Path;

    /// <summary>
    /// The launcher file (ADR-0038): every stage option in order, the quick bar, and where the
    /// apps added here sit among them. It replaced the read-only built-in list — NUVIO, STREMIO
    /// and PROBE are entries in it now, with their install paths.
    /// </summary>
    public LauncherSettingsViewModel Launcher { get; }

    /// <summary>The name for the app being added.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppMessage))]
    [NotifyPropertyChangedFor(nameof(AppMessageIsRejected))]
    [NotifyPropertyChangedFor(nameof(AppMessageBrush))]
    [NotifyCanExecuteChangedFor(nameof(AddAppCommand))]
    private string _newAppName = "";

    /// <summary>The executable chosen with Browse.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppMessage))]
    [NotifyPropertyChangedFor(nameof(AppMessageIsRejected))]
    [NotifyPropertyChangedFor(nameof(AppMessageBrush))]
    [NotifyCanExecuteChangedFor(nameof(AddAppCommand))]
    private string _newAppPath = "";

    /// <summary>Optional command-line arguments — how a browser is aimed at a page.</summary>
    [ObservableProperty]
    private string _newAppArguments = "";

    /// <summary>Whether the app being added plays audio/video and should persist in the
    /// background (ADR-0026).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewAppKeepPlayingLabel))]
    private bool _newAppKeepPlaying;

    /// <summary>The toggle's caption.</summary>
    public string NewAppKeepPlayingLabel => NewAppKeepPlaying ? "PLAYS AUDIO — KEEP PLAYING" : "SILENT APP";

    /// <summary>Flip whether the app being added keeps playing in the background.</summary>
    [RelayCommand]
    private void ToggleNewAppKeepPlaying() => NewAppKeepPlaying = !NewAppKeepPlaying;

    /// <summary>Whether the typed name, upper-cased, already names a built-in or an added app.</summary>
    private bool NameCollides
    {
        get
        {
            var name = NewAppName.Trim();
            return _launcher.Current.Entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                || _userApps.Apps.Any(a => string.Equals(a.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>What is wrong with the pending app, or how to add one — checked as you type.</summary>
    public string AppMessage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(NewAppName))
            {
                return "Name it and choose its .exe.";
            }

            if (NameCollides)
            {
                return $"{NewAppName.Trim().ToUpperInvariant()} is already a stage name.";
            }

            if (string.IsNullOrWhiteSpace(NewAppPath))
            {
                return "Choose the .exe with Browse.";
            }

            return System.IO.File.Exists(NewAppPath)
                ? "Ready to add."
                : "That file is not there — it will read as not installed until it is.";
        }
    }

    /// <summary>True when the message is a refusal, so it shows in the fault colour.</summary>
    public bool AppMessageIsRejected =>
        !string.IsNullOrWhiteSpace(NewAppName) && (NameCollides || string.IsNullOrWhiteSpace(NewAppPath));

    /// <summary>Fault red while the pending app is refused, faint otherwise — the same red the
    /// dash uses for a fault, read from <see cref="QualityPalette"/> rather than redeclared.</summary>
    public Brush AppMessageBrush => AppMessageIsRejected
        ? QualityPalette.Fault
        : (Brush)System.Windows.Application.Current.Resources["TextFaintBrush"];

    /// <summary>A missing file is a warning, not a refusal — a built-in can be "not installed" too.</summary>
    private bool CanAddApp() =>
        !string.IsNullOrWhiteSpace(NewAppName) && !NameCollides && !string.IsNullOrWhiteSpace(NewAppPath);

    /// <summary>Pick the executable with a file dialog — the same API the video occupant uses.</summary>
    [RelayCommand]
    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an application",
            Filter = "Programs|*.exe|Every file|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            NewAppPath = dialog.FileName;

            // A blank name gets a sensible default from the file, so the common case is
            // Browse then Add.
            if (string.IsNullOrWhiteSpace(NewAppName))
            {
                NewAppName = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName).ToUpperInvariant();
            }
        }
    }

    /// <summary>Add the pending app to the store, which persists it and tells the shell to rebuild.</summary>
    [RelayCommand(CanExecute = nameof(CanAddApp))]
    private void AddApp()
    {
        _userApps.Add(new DashDeck.Host.Stage.UserAppEntry
        {
            Name = NewAppName.Trim().ToUpperInvariant(),
            Path = NewAppPath.Trim(),
            Arguments = NewAppArguments.Trim(),
            KeepPlaying = NewAppKeepPlaying,
        });

        NewAppName = string.Empty;
        NewAppPath = string.Empty;
        NewAppArguments = string.Empty;
        NewAppKeepPlaying = false;
    }

    /// <summary>Remove an app. Bound to the ✕ on each row.</summary>
    [RelayCommand]
    private void RemoveApp(DashDeck.Host.Stage.UserAppEntry? entry)
    {
        if (entry is not null)
        {
            _userApps.Remove(entry);
        }
    }

    private void Sync()
    {
        foreach (var mode in Modes)
        {
            mode.IsCurrent = mode.Mode == _theme.Mode;
        }

        foreach (var accent in Accents)
        {
            accent.IsCurrent = accent.Option == _theme.Accent;
        }

        OnPropertyChanged(nameof(AutoSource));
        OnPropertyChanged(nameof(IsAuto));
        OnPropertyChanged(nameof(ResolvedTheme));
        OnPropertyChanged(nameof(CustomMessageBrush));
    }
}

/// <summary>One COM port, for the Bluetooth GPS picker.</summary>
public sealed partial class SerialPortOption(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>One web-scale chip.</summary>
/// <remarks>
/// Labelled as a percentage because that is how every browser has ever expressed zoom, and a
/// dash is not the place to teach somebody a new unit.
/// </remarks>
public sealed partial class WebScaleOption : ObservableObject
{
    public WebScaleOption(double scale) => Scale = scale;

    /// <summary>The multiplier handed to WebView2.</summary>
    public double Scale { get; }

    /// <summary>What the chip says.</summary>
    public string Label => $"{Scale * 100:0}%";

    [ObservableProperty]
    private bool _isSelected;
}


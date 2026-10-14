using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Core.Audio;
using DashDeck.Host.Settings;
using DashDeck.WinAudio;

namespace DashDeck.Host.ViewModels;

/// <summary>One audio device, for the pickers.</summary>
public sealed partial class AudioDeviceRow(WaveDevice device) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public int Id { get; } = device.Id;

    public string Name { get; } = device.Name;

    public string Caption => $"{Id}  {Name}";
}

/// <summary>One probe's result, for the list the measurement leaves behind.</summary>
/// <param name="Line">Which output and what it read.</param>
public readonly record struct ProbeRow(string Line);

/// <summary>
/// Settings ▸ AUDIO: where the music comes from, what the output's delay is, and the probe that
/// measures it (ADR-0059).
/// </summary>
/// <remarks>
/// <b>Typing the number in is the primary path.</b> It always works, needs no quiet moment and no
/// microphone, and a person can tune it against a video in three tries. The probe is a convenience
/// on top — if it refuses, or is never run, the typed number is what the dash uses.
/// </remarks>
public sealed partial class AudioSettingsViewModel : ObservableObject
{
    private readonly DisplaySettings _settings;
    private CancellationTokenSource? _running;

    public AudioSettingsViewModel(DisplaySettings settings)
    {
        _settings = settings;
        Refresh();
    }

    /// <summary>Outputs Windows admits to.</summary>
    public ObservableCollection<AudioDeviceRow> Outputs { get; } = [];

    /// <summary>Inputs Windows admits to.</summary>
    public ObservableCollection<AudioDeviceRow> Inputs { get; } = [];

    /// <summary>The reference speaker — built into the tablet.</summary>
    [ObservableProperty]
    private AudioDeviceRow? _reference;

    /// <summary>The output being measured — the Bluetooth link to the stereo.</summary>
    [ObservableProperty]
    private AudioDeviceRow? _measured;

    /// <summary>Which microphone to listen on.</summary>
    [ObservableProperty]
    private AudioDeviceRow? _microphone;

    /// <summary>The delay in milliseconds, typed or measured.</summary>
    public string DelayText
    {
        get => _settings.AudioDelayMilliseconds > 0
            ? _settings.AudioDelayMilliseconds.ToString("0", CultureInfo.InvariantCulture)
            : "";
        set
        {
            // A settings field is editable by hand, so what arrives is never trusted straight.
            var parsed = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
                && double.IsFinite(ms)
                    ? Math.Clamp(ms, 0, 5000)
                    : 0;

            _settings.AudioDelayMilliseconds = parsed;
            _settings.AudioDelaySource = parsed > 0 ? "Manual" : "";
            _settings.AudioDelayMeasuredAt = "";
            _settings.AudioDelayEndpoint = Measured?.Name ?? _settings.AudioDelayEndpoint;

            OnPropertyChanged();
            OnPropertyChanged(nameof(DelayNote));
        }
    }

    /// <summary>Where the stored delay came from, and when, for the line under the field.</summary>
    public string DelayNote
    {
        get
        {
            if (_settings.AudioDelayMilliseconds <= 0)
            {
                return "Not set. Nothing is compensated until it is — which for music is no loss "
                    + "at all, but a video's picture will run ahead of its sound.";
            }

            var where = string.IsNullOrWhiteSpace(_settings.AudioDelayEndpoint)
                ? "an output that was not recorded"
                : _settings.AudioDelayEndpoint;

            var when = string.IsNullOrWhiteSpace(_settings.AudioDelayMeasuredAt)
                ? ""
                : $", measured {_settings.AudioDelayMeasuredAt}";

            return $"{_settings.AudioDelaySource.ToLowerInvariant()} for {where}{when}. "
                + "It moves after a reconnect and after the stream has been idle, so re-measure "
                + "rather than assuming.";
        }
    }

    /// <summary>Where the Stereo card reads music from.</summary>
    public string MusicFolder
    {
        get => _settings.MusicFolder;
        set
        {
            _settings.MusicFolder = value ?? "";
            OnPropertyChanged();
        }
    }

    /// <summary>Whether playback gets out of the way of spoken instructions (Q14).</summary>
    public bool DuckUnderInstructions
    {
        get => _settings.DuckUnderInstructions;
        set
        {
            _settings.DuckUnderInstructions = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DuckLabel));
        }
    }

    /// <inheritdoc cref="DuckUnderInstructions"/>
    public string DuckLabel => DuckUnderInstructions ? "ON" : "OFF";

    /// <summary>What the probe is doing, or what it concluded.</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>Each probe's reading, after a run.</summary>
    public ObservableCollection<ProbeRow> Probes { get; } = [];

    /// <summary>Whether a run is in progress, so the button can say so and not re-enter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeasureLabel))]
    private bool _isMeasuring;

    /// <inheritdoc cref="IsMeasuring"/>
    public string MeasureLabel => IsMeasuring ? "STOP" : "MEASURE";

    /// <summary>Re-read the device lists.</summary>
    [RelayCommand]
    private void Refresh()
    {
        Outputs.Clear();
        Inputs.Clear();

        try
        {
            foreach (var device in WaveDevices.Outputs())
            {
                Outputs.Add(new AudioDeviceRow(device));
            }

            foreach (var device in WaveDevices.Inputs())
            {
                Inputs.Add(new AudioDeviceRow(device));
            }
        }
        catch (DllNotFoundException)
        {
            Status = "No Windows audio on this machine.";
            return;
        }

        // A sensible first guess, which the person can override: the reference wants to be
        // something built in, and the measured output wants to be anything else.
        Reference ??= Outputs.FirstOrDefault(o => LooksBuiltIn(o.Name)) ?? Outputs.FirstOrDefault();
        Measured ??= Outputs.FirstOrDefault(o => o.Id != Reference?.Id);
        Microphone ??= Inputs.FirstOrDefault();
    }

    /// <summary>Pick the reference speaker.</summary>
    [RelayCommand]
    private void SelectReference(AudioDeviceRow? row) => Reference = row ?? Reference;

    /// <summary>Pick the output being measured.</summary>
    [RelayCommand]
    private void SelectMeasured(AudioDeviceRow? row) => Measured = row ?? Measured;

    /// <summary>Pick the microphone.</summary>
    [RelayCommand]
    private void SelectMicrophone(AudioDeviceRow? row) => Microphone = row ?? Microphone;

    /// <summary>Turn ducking on or off.</summary>
    [RelayCommand]
    private void ToggleDuck() => DuckUnderInstructions = !DuckUnderInstructions;

    /// <summary>Run the probe, or stop one that is running.</summary>
    [RelayCommand]
    private async Task MeasureAsync()
    {
        if (IsMeasuring)
        {
            await _running!.CancelAsync();
            return;
        }

        if (Reference is null || Measured is null)
        {
            Status = "Choose the tablet's own speaker and the output to measure.";
            return;
        }

        if (Reference.Id == Measured.Id)
        {
            Status = "Those are the same device. The difference between them would be only noise.";
            return;
        }

        Probes.Clear();
        IsMeasuring = true;
        _running = new CancellationTokenSource();

        var request = new MeasurementRequest(
            Reference.Id,
            Measured.Id,
            Microphone?.Id ?? WaveDevices.Mapper);

        Status = $"Recording {request.Duration.TotalSeconds:F0} s. Engine running, and don't talk over it.";

        try
        {
            // Blocking by design — it sleeps for the length of the recording — so it runs off the
            // interface thread and reports back through the dispatcher.
            var outcome = await Task.Run(
                () => DelayMeasurement.Run(
                    request,
                    line => App.Current?.Dispatcher.BeginInvoke(() => Status = line),
                    _running.Token),
                _running.Token);

            Report(outcome, request);
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (Exception ex)
        {
            Status = $"The measurement failed: {ex.Message}";
        }
        finally
        {
            IsMeasuring = false;
            _running?.Dispose();
            _running = null;
        }
    }

    private void Report(MeasurementOutcome outcome, MeasurementRequest request)
    {
        foreach (var (isRemote, match) in outcome.Session.Probes)
        {
            Probes.Add(new ProbeRow(match.Found
                ? $"{(isRemote ? "stereo" : "tablet"),-7} {match.Lag.TotalMilliseconds,7:F1} ms   confidence {match.Confidence,6:F1}"
                : $"{(isRemote ? "stereo" : "tablet"),-7}       -        not found"));
        }

        if (outcome.Gaps > 0)
        {
            Probes.Add(new ProbeRow(
                $"WARNING  {outcome.Gaps} capture buffer(s) short, {outcome.GapTime.TotalMilliseconds:F0} ms padded — this run is suspect"));
        }

        if (!outcome.Ok)
        {
            Status = $"Refused rather than guessing. {outcome.Problem}";
            return;
        }

        var delay = outcome.Delay!.Value;

        _settings.AudioDelayMilliseconds = Math.Round(delay.TotalMilliseconds);
        _settings.AudioDelayEndpoint = Measured?.Name ?? "";
        _settings.AudioDelaySource = "Measured";
        _settings.AudioDelayMeasuredAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        OnPropertyChanged(nameof(DelayText));
        OnPropertyChanged(nameof(DelayNote));

        var spread = outcome.Session.Remote.Spread.TotalMilliseconds;
        Status = spread > 5
            ? $"{delay.TotalMilliseconds:F0} ms, but the readings spread {spread:F0} ms. With a "
                + "continuous track every probe shares one playback start, so more than a few "
                + "milliseconds means something is wrong rather than merely jittery — measure again."
            : $"{delay.TotalMilliseconds:F0} ms, with the readings inside {spread:F1} ms. Saved.";

        _ = request;
    }

    /// <summary>
    /// Whether a device name reads like something built into the machine.
    /// </summary>
    /// <remarks>
    /// Only a first guess for the picker, never a decision: the wave API truncates names to 31
    /// characters and carries no stable identifier (Q34), so this would be a poor thing to trust.
    /// On the owner's truck it is the difference between "Speakers (Realtek High Definiti" and
    /// "Headphones (Ford F-150)".
    /// </remarks>
    private static bool LooksBuiltIn(string name) =>
        name.Contains("Speaker", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Realtek", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Internal", StringComparison.OrdinalIgnoreCase);
}

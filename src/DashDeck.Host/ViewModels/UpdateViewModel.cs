using System.Diagnostics;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Core.Updates;
using DashDeck.Host.Updates;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// Settings ▸ ABOUT ▸ UPDATE: check when asked, download when asked, install when asked.
/// </summary>
/// <remarks>
/// <b>Three deliberate buttons, and nothing automatic between them</b> (ADR-0061). The constructor
/// does no work: no check at launch, no badge, no toast, nothing stored to drive a reminder later.
/// A dash that interrupts a drive to mention a release is worse than one that is a version behind,
/// and a 334 MB download that starts itself on a phone hotspot is worse again.
/// <para>
/// Each step says what it found and stops. The person decides whether the next one happens, which
/// matters most at the last one: installing replaces the folder the dash is running from.
/// </para>
/// </remarks>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly UpdateService _service;
    private readonly Func<DateTimeOffset> _now;

    private UpdateStatus _status = new(UpdateVerdict.NotChecked);
    private string? _archivePath;
    private string? _stagedFolder;
    private CancellationTokenSource? _running;

    public UpdateViewModel(UpdateService? service = null, Func<DateTimeOffset>? now = null)
    {
        _service = service ?? new UpdateService();
        _now = now ?? (() => DateTimeOffset.UtcNow);

        // Deliberately not checking here. See the remarks.
        Headline = $"You are on {_service.Version}";
    }

    /// <summary>The one line that says where things stand.</summary>
    [ObservableProperty]
    private string _headline = "";

    /// <summary>The sentence under it — the age, a refusal, a problem, or what to do next.</summary>
    [ObservableProperty]
    private string _detail = "Nothing is checked until you ask. DashDeck never looks on its own.";

    /// <summary>Whether a check, a download or an install is in progress.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    /// <summary>Progress of a download or an unpack, 0–100, or empty when nothing is running.</summary>
    [ObservableProperty]
    private string _progress = "";

    /// <summary>Whether there is a newer release with a file to fetch.</summary>
    [ObservableProperty]
    private bool _canDownload;

    /// <summary>Whether a downloaded archive is staged and may replace this folder.</summary>
    [ObservableProperty]
    private bool _canInstall;

    /// <summary>Whether there are release notes to open.</summary>
    [ObservableProperty]
    private bool _hasNotes;

    /// <summary>The inverse of <see cref="IsBusy"/>, for enabling the buttons.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>Ask GitHub what the newest release is. The only thing that ever asks.</summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsBusy)
        {
            return;
        }

        Begin("Checking…");

        try
        {
            _status = await _service.CheckAsync(Token()).ConfigureAwait(true);
            Show(_status);
        }
        catch (OperationCanceledException)
        {
            Headline = $"You are on {_service.Version}";
            Detail = "The check was stopped.";
        }
        finally
        {
            End();
        }
    }

    /// <summary>Fetch the archive. Minutes over a hotspot, so it says how far along it is.</summary>
    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsBusy || _status.Asset is not { } asset)
        {
            return;
        }

        Begin($"Downloading {UpdateCheck.Size(asset.Bytes)}…");
        CanDownload = false;

        try
        {
            var progress = new Progress<double>(fraction =>
                Progress = string.Create(CultureInfo.InvariantCulture, $"{fraction * 100:0}%"));

            _archivePath = await _service.DownloadAsync(asset, progress, Token()).ConfigureAwait(true);

            if (_service.MayInstall(_archivePath, asset) is var refusal && refusal is not InstallRefusal.None)
            {
                Headline = "This cannot be installed";
                Detail = InstallGuards.Explain(refusal, new System.IO.FileInfo(_archivePath).Length);
                return;
            }

            Progress = "unpacking…";
            _stagedFolder = await Task.Run(() => _service.Stage(_archivePath!), Token()).ConfigureAwait(true);

            Headline = $"{_status.Latest} is ready to install";
            Detail = "Installing closes DashDeck, replaces its folder and opens it again. The folder "
                + "you are running now is kept beside it as DashDeck.previous.";
            CanInstall = true;
        }
        catch (OperationCanceledException)
        {
            Detail = "The download was stopped. Nothing has changed.";
            CanDownload = true;
        }
        catch (Exception ex)
        {
            Headline = "The download failed";
            Detail = $"{ex.Message} Nothing has changed.";
            CanDownload = true;
        }
        finally
        {
            End();
        }
    }

    /// <summary>
    /// Hand the swap to cmd and close the dash, which is what lets the folder be replaced.
    /// </summary>
    [RelayCommand]
    private void Install()
    {
        if (IsBusy || !CanInstall || _stagedFolder is null)
        {
            return;
        }

        try
        {
            _service.InstallAndRestart(_stagedFolder);
        }
        catch (Exception ex)
        {
            Headline = "The install could not start";
            Detail = $"{ex.Message} Nothing has changed.";
            return;
        }

        // The script waits for this process to exit before it touches a thing, so the shutdown is
        // the handover rather than a race.
        Application.Current?.Shutdown();
    }

    /// <summary>Stop a check or a download that is taking too long.</summary>
    [RelayCommand]
    private void Stop() => _running?.Cancel();

    /// <summary>Open the release notes in the browser.</summary>
    [RelayCommand]
    private void OpenNotes()
    {
        if (_status.NotesUrl is not { } url)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Detail = $"Could not open the notes: {ex.Message}";
        }
    }

    private void Show(UpdateStatus status)
    {
        Headline = UpdateCheck.Describe(status);
        CanDownload = status.Verdict is UpdateVerdict.Available;
        HasNotes = !string.IsNullOrWhiteSpace(status.NotesUrl);

        Detail = status.Verdict switch
        {
            UpdateVerdict.Available => Joined(
                UpdateCheck.Age(status.PublishedUtc, _now()),
                "Downloading does not install it — there is a second button for that."),
            UpdateVerdict.UpToDate => Joined(
                UpdateCheck.Age(status.PublishedUtc, _now()),
                "Nothing to do."),
            UpdateVerdict.UnknownRunningVersion =>
                $"{status.Problem}, so there is nothing to compare. A deploy built by publish.ps1 "
                    + "carries its version; a build run from the repository may not.",
            _ => status.Problem ?? "Nothing more is known.",
        };
    }

    private static string Joined(string first, string second) =>
        string.IsNullOrWhiteSpace(first) ? second : $"{first}. {second}";

    private void Begin(string what)
    {
        IsBusy = true;
        CanInstall = false;
        Progress = "";
        Detail = what;
        _running?.Dispose();
        _running = new CancellationTokenSource();
    }

    private void End()
    {
        IsBusy = false;
        Progress = "";
    }

    private CancellationToken Token() => _running?.Token ?? CancellationToken.None;
}

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DashDeck.Core.Brightness;

namespace DashDeck.Host.Brightness;

/// <summary>A screen's backlight, as a percentage.</summary>
public interface IBacklight
{
    /// <summary>The level now, 0 to 100, or null when this screen's backlight cannot be read.</summary>
    int? Read();

    /// <summary>Set the level. False when it could not be set.</summary>
    bool Write(int percent);
}

/// <summary>Where an unrestored backlight change is written down, so a crash can still be undone.</summary>
public interface IBacklightMemory
{
    /// <summary>What an earlier run left, or null.</summary>
    BacklightLeft? Load();

    /// <summary>Write down what this run has borrowed, or null when there is nothing to give back.</summary>
    void Save(BacklightLeft? left);
}

/// <summary>
/// Sets the tablet's backlight to the day or night level, and gives it back on exit (ADR-0068).
/// </summary>
/// <remarks>
/// <b>The backlight rather than a dark layer over the window</b>, because a layer cannot reach the
/// map, video, Android Auto or a hosted app: each draws in a window of its own that WPF cannot
/// compose over (ADR-0021). The one control that dims all of them is the panel's.
/// <para>
/// Driven by the shell's one-second beat with whether it is night. Nothing is written unless the
/// level wanted has changed, so a person who moves the Windows slider mid-drive is not fought
/// every second — DashDeck only speaks again at the next day/night change or settings tap.
/// </para>
/// <para>
/// All backlight calls run on one worker, in order, because WMI is slow and a dash that stalls at
/// dusk is worse than one that dims a moment late. Everything about the session is touched only
/// there.
/// </para>
/// </remarks>
public sealed partial class ScreenBrightness : ObservableObject, IDisposable
{
    /// <summary>How long closing waits for the backlight to be given back.</summary>
    private static readonly TimeSpan RestoreBudget = TimeSpan.FromSeconds(3);

    private readonly IBacklight _backlight;
    private readonly IBacklightMemory _memory;
    private readonly Func<BrightnessChoice> _choice;
    private readonly Action<Action>? _run;
    private readonly Lock _gate = new();
    private Task _queue = Task.CompletedTask;

    // Worker only.
    private BacklightSession? _session;
    private int? _applied;

    // Dispatcher only.
    private bool _isNight;
    private (bool Posted, int? Target, bool Night) _lastPosted;
    private bool _disposed;

    /// <summary>What the backlight is doing, one line, for Settings ▸ DISPLAY.</summary>
    [ObservableProperty]
    private string _status = "Reading the backlight…";

    /// <param name="backlight">The panel.</param>
    /// <param name="memory">Where a borrowed level is written down.</param>
    /// <param name="choice">The person's choice, read fresh each time it is needed.</param>
    /// <param name="run">
    /// How work reaches the worker. Null is a serial queue on the thread pool; tests pass
    /// <c>work =&gt; work()</c> to run it where they can see it.
    /// </param>
    public ScreenBrightness(IBacklight backlight, IBacklightMemory memory, Func<BrightnessChoice> choice, Action<Action>? run = null)
    {
        _backlight = backlight;
        _memory = memory;
        _choice = choice;
        _run = run;
    }

    /// <summary>
    /// Read the panel and recover anything an earlier run left dimmed. Called once, before the
    /// first <see cref="Apply"/>.
    /// </summary>
    public void Start() => Post(() =>
    {
        _session = new BacklightSession(_backlight.Read(), _memory.Load());

        if (_session.Found is null)
        {
            // Nothing borrowed can be given back through a backlight that cannot be read, and a
            // record that cannot be acted on only misleads the next launch.
            _memory.Save(null);
            Status = "This screen's backlight can't be set from Windows, so DashDeck leaves it alone.";
        }
    });

    /// <summary>Set the level for day or night, if it has changed. Called on the shell's beat.</summary>
    public void Apply(bool isNight)
    {
        _isNight = isNight;

        if (_disposed)
        {
            return;
        }

        var target = BrightnessPolicy.Target(_choice(), isNight);

        if (_lastPosted == (true, target, isNight))
        {
            return;
        }

        _lastPosted = (true, target, isNight);
        Post(() => Settle(target, isNight));
    }

    /// <summary>Apply a changed choice now rather than at the next beat.</summary>
    public void Reapply() => Apply(_isNight);

    /// <summary>Give the backlight back, waiting a little for it — the shell is closing.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Post(Restore);

        Task pending;

        lock (_gate)
        {
            pending = _queue;
        }

        // Bounded: a WMI service that has stopped answering must not hold the dash open. The
        // record stays written in that case, so the next launch still knows the original.
        pending.Wait(RestoreBudget);
    }

    private void Settle(int? target, bool isNight)
    {
        if (_session is not { Found: not null } session)
        {
            return;
        }

        if (target is not { } level)
        {
            // Turned off: what was borrowed goes back now rather than at exit, so OFF means the
            // tablet's own level from the moment it is tapped.
            GiveBack(session);
            _applied = null;
            Status = "Off. DashDeck leaves the backlight where you set it.";
            return;
        }

        if (_applied != level)
        {
            if (!_backlight.Write(level))
            {
                Status = "Windows refused to set the backlight. It is wherever it was.";
                return;
            }

            // The panel's own reading, when it gives one, because that is what the restore will
            // be comparing against — a panel with coarse steps rounds what it is given.
            session.Wrote(_backlight.Read() ?? level);
            _memory.Save(session.Record);
            _applied = level;
        }

        Status = Describe(session, isNight);
    }

    private void Restore()
    {
        if (_session is { Found: not null } session)
        {
            GiveBack(session);
        }
    }

    private void GiveBack(BacklightSession session)
    {
        if (session.RestoreTo(_backlight.Read()) is { } original)
        {
            if (!_backlight.Write(original))
            {
                // Leave the record, so the next launch can still put it back.
                return;
            }

            session.Wrote(original);
        }

        // Given back, or moved by someone since — either way nothing remembered is still owed.
        _memory.Save(null);
    }

    private static string Describe(BacklightSession session, bool isNight)
    {
        var level = session.LastSet ?? session.Found ?? 0;
        var which = isNight ? "night" : "day";
        var back = session.HasChanged
            ? string.Create(CultureInfo.CurrentCulture, $" {session.Original} % goes back when DashDeck closes.")
            : "";

        return string.Create(CultureInfo.CurrentCulture, $"Backlight {level} %, the {which} level.{back}");
    }

    private void Post(Action work)
    {
        if (_run is not null)
        {
            _run(() => Guard(work));
            return;
        }

        lock (_gate)
        {
            _queue = _queue.ContinueWith(_ => Guard(work), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void Guard(Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            // A backlight is never worth the dash.
            Status = $"The backlight could not be set: {ex.Message}";
        }
    }
}

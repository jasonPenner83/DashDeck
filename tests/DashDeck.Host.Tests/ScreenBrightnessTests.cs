using DashDeck.Core.Brightness;
using DashDeck.Host.Brightness;

namespace DashDeck.Host.Tests;

/// <summary>
/// The backlight controller (ADR-0068): it sets day and night, stays quiet when nothing changed,
/// and gives the tablet's own level back.
/// </summary>
/// <remarks>
/// Run inline rather than on the worker, against a fake panel, so what was written and when is
/// visible. The WMI half needs a real screen and is the walkthrough's.
/// </remarks>
public sealed class ScreenBrightnessTests
{
    [Fact]
    public void Night_dims_to_the_night_level_and_day_brings_it_back_up()
    {
        var rig = new Rig(found: 70);

        rig.Brightness.Apply(isNight: true);
        Assert.Equal(30, rig.Panel.Level);

        rig.Brightness.Apply(isNight: false);
        Assert.Equal(100, rig.Panel.Level);
    }

    [Fact]
    public void Nothing_is_written_while_nothing_changes()
    {
        var rig = new Rig(found: 70);

        rig.Brightness.Apply(isNight: true);
        var writes = rig.Panel.Writes.Count;

        for (var beat = 0; beat < 10; beat++)
        {
            rig.Brightness.Apply(isNight: true);
        }

        Assert.Equal(writes, rig.Panel.Writes.Count);
    }

    [Fact]
    public void Moving_the_windows_slider_mid_drive_is_not_fought()
    {
        var rig = new Rig(found: 70);
        rig.Brightness.Apply(isNight: true);

        rig.Panel.Level = 55;

        for (var beat = 0; beat < 10; beat++)
        {
            rig.Brightness.Apply(isNight: true);
        }

        Assert.Equal(55, rig.Panel.Level);
    }

    [Fact]
    public void Closing_gives_back_the_level_it_found()
    {
        var rig = new Rig(found: 70);
        rig.Brightness.Apply(isNight: true);

        rig.Brightness.Dispose();

        Assert.Equal(70, rig.Panel.Level);
        Assert.Null(rig.Memory.Left);
    }

    [Fact]
    public void Closing_after_somebody_moved_it_leaves_their_level()
    {
        var rig = new Rig(found: 70);
        rig.Brightness.Apply(isNight: true);
        rig.Panel.Level = 55;

        rig.Brightness.Dispose();

        Assert.Equal(55, rig.Panel.Level);
        Assert.Null(rig.Memory.Left);
    }

    [Fact]
    public void What_is_borrowed_is_written_down_before_anything_can_crash()
    {
        var rig = new Rig(found: 70);

        rig.Brightness.Apply(isNight: true);

        Assert.Equal(new BacklightLeft(70, 30), rig.Memory.Left);
    }

    [Fact]
    public void A_launch_after_a_crash_gives_back_the_real_original()
    {
        // The last run dimmed 80 to 30 and never closed.
        var rig = new Rig(found: 30, leftOver: new BacklightLeft(80, 30));

        rig.Brightness.Apply(isNight: false);
        rig.Brightness.Dispose();

        Assert.Equal(80, rig.Panel.Level);
    }

    [Fact]
    public void Turning_it_off_gives_the_level_back_at_once()
    {
        var rig = new Rig(found: 70);
        rig.Brightness.Apply(isNight: true);

        rig.Choice = rig.Choice with { Enabled = false };
        rig.Brightness.Reapply();

        Assert.Equal(70, rig.Panel.Level);
        Assert.Null(rig.Memory.Left);
        Assert.StartsWith("Off", rig.Brightness.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Off_from_the_start_never_touches_the_panel()
    {
        var rig = new Rig(found: 70, choice: new BrightnessChoice(false, 100, 30));

        rig.Brightness.Apply(isNight: true);
        rig.Brightness.Dispose();

        Assert.Empty(rig.Panel.Writes);
        Assert.Equal(70, rig.Panel.Level);
    }

    [Fact]
    public void Off_after_a_crash_still_gives_back_what_the_crash_left()
    {
        var rig = new Rig(found: 30, leftOver: new BacklightLeft(80, 30), choice: new BrightnessChoice(false, 100, 30));

        rig.Brightness.Apply(isNight: true);

        Assert.Equal(80, rig.Panel.Level);
        Assert.Null(rig.Memory.Left);
    }

    [Fact]
    public void A_new_level_chosen_in_settings_applies_without_waiting_for_dusk()
    {
        var rig = new Rig(found: 70);
        rig.Brightness.Apply(isNight: true);

        rig.Choice = rig.Choice with { NightPercent = 10 };
        rig.Brightness.Reapply();

        Assert.Equal(10, rig.Panel.Level);
        Assert.Equal(new BacklightLeft(70, 10), rig.Memory.Left);
    }

    [Fact]
    public void A_screen_that_cannot_be_read_is_left_alone_and_says_so()
    {
        var rig = new Rig(found: null);

        rig.Brightness.Apply(isNight: true);
        rig.Brightness.Dispose();

        Assert.Empty(rig.Panel.Writes);
        Assert.Contains("can't be set", rig.Brightness.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_write_says_so_and_borrows_nothing()
    {
        var rig = new Rig(found: 70);
        rig.Panel.Refuse = true;

        rig.Brightness.Apply(isNight: true);

        Assert.Equal(70, rig.Panel.Level);
        Assert.Null(rig.Memory.Left);
        Assert.Contains("refused", rig.Brightness.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_panel_that_throws_costs_the_backlight_not_the_dash()
    {
        var rig = new Rig(found: 70);
        rig.Panel.Throw = true;

        rig.Brightness.Apply(isNight: true);
        rig.Brightness.Dispose();

        Assert.Contains("could not be set", rig.Brightness.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void The_status_names_the_level_and_what_goes_back()
    {
        var rig = new Rig(found: 70);

        rig.Brightness.Apply(isNight: true);

        Assert.Contains("30 %", rig.Brightness.Status, StringComparison.Ordinal);
        Assert.Contains("night", rig.Brightness.Status, StringComparison.Ordinal);
        Assert.Contains("70 % goes back", rig.Brightness.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_panel_that_rounds_is_compared_by_what_it_reports()
    {
        // Asked for 30, the panel settles on 31: the restore must still recognise it as ours.
        var rig = new Rig(found: 70);
        rig.Panel.Round = level => level == 30 ? 31 : level;

        rig.Brightness.Apply(isNight: true);
        rig.Brightness.Dispose();

        Assert.Equal(70, rig.Panel.Level);
    }

    [Fact]
    public void Closing_twice_restores_once()
    {
        var rig = new Rig(found: 70);
        rig.Brightness.Apply(isNight: true);

        rig.Brightness.Dispose();
        var writes = rig.Panel.Writes.Count;
        rig.Brightness.Dispose();

        Assert.Equal(writes, rig.Panel.Writes.Count);
    }

    [Fact]
    public void The_real_worker_runs_in_order_and_closing_waits_for_it()
    {
        // No inline runner: the thread-pool queue the dash uses.
        var panel = new FakePanel(70);
        var memory = new FakeMemory(null);
        var brightness = new ScreenBrightness(panel, memory, () => BrightnessChoice.Default);

        brightness.Start();
        brightness.Apply(isNight: true);
        brightness.Dispose();

        Assert.Equal(70, panel.Level);
        Assert.Equal(new[] { 30, 70 }, panel.Writes);
    }

    private sealed class Rig
    {
        public Rig(int? found, BacklightLeft? leftOver = null, BrightnessChoice? choice = null)
        {
            Panel = new FakePanel(found);
            Memory = new FakeMemory(leftOver);
            Choice = choice ?? BrightnessChoice.Default;
            Brightness = new ScreenBrightness(Panel, Memory, () => Choice, work => work());
            Brightness.Start();
        }

        public FakePanel Panel { get; }

        public FakeMemory Memory { get; }

        public BrightnessChoice Choice { get; set; }

        public ScreenBrightness Brightness { get; }
    }

    private sealed class FakePanel(int? level) : IBacklight
    {
        private readonly Lock _gate = new();

        public int? Level { get; set; } = level;

        public List<int> Writes { get; } = [];

        public bool Refuse { get; set; }

        public bool Throw { get; set; }

        public Func<int, int> Round { get; set; } = level => level;

        public int? Read() => Throw ? throw new InvalidOperationException("WMI fell over") : Level;

        public bool Write(int percent)
        {
            if (Throw)
            {
                throw new InvalidOperationException("WMI fell over");
            }

            if (Refuse || Level is null)
            {
                return false;
            }

            lock (_gate)
            {
                Writes.Add(percent);
            }

            Level = Round(percent);
            return true;
        }
    }

    private sealed class FakeMemory(BacklightLeft? left) : IBacklightMemory
    {
        public BacklightLeft? Left { get; private set; } = left;

        public BacklightLeft? Load() => Left;

        public void Save(BacklightLeft? left) => Left = left;
    }
}

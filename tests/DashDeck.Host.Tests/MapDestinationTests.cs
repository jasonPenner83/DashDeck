using DashDeck.Abstractions;
using DashDeck.Core.Location;
using DashDeck.Core.Maps;
using DashDeck.Host.Stage;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Tests;

/// <summary>
/// The MAPS destination (ADR-0062) — its placements as the shell sees them, and the one real
/// thing on the placeholder, which is the position.
/// </summary>
/// <remarks>
/// The placement rules themselves are pinned in the engine's own suite
/// (<c>DashDeck.Tests.MapPlacementTests</c>), where they run without WPF at all. What is tested
/// here is the part that only exists in the shell: that a move is announced once so the shell can
/// follow it, and that the stage occupant's one action is the way back.
/// <para>
/// No view is built. <c>CreateView</c> needs a UI thread, and what it returns is a
/// <c>UserControl</c> with the view-model attached — nothing worth asserting that the compiler
/// has not already checked.
/// </para>
/// </remarks>
public sealed class MapDestinationTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class Source(LocationFix? fix, string name = "COM4") : ILocationSource
    {
        public string Name => name;

        public LocationFix? Latest => fix;

        public void Start()
        {
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void The_map_opens_in_the_bottom_third()
    {
        var map = new MapViewModel();

        Assert.True(map.IsPanel);
        Assert.False(map.IsFull);
        Assert.False(map.IsOnStage);
    }

    [Fact]
    public void Every_move_is_announced_once_so_the_shell_can_follow_it()
    {
        // The shell collapses the stage, makes an occupant or drops one off the back of these,
        // so a move that is not announced is a map drawn in the wrong region.
        var map = new MapViewModel();
        var moves = new List<MapPlacement>();
        map.PlacementChanged += view => moves.Add(view.Placement);

        map.ResizeCommand.Execute(null);
        map.SendToStageCommand.Execute(null);
        map.BringBackCommand.Execute(null);

        Assert.Equal([MapPlacement.Full, MapPlacement.Stage, MapPlacement.Full], moves);
    }

    [Fact]
    public void A_move_that_changes_nothing_is_not_announced()
    {
        // Load-bearing: the shell reconciles the placement after every stage change, so bringing
        // back a map that is already back arrives here — and re-entering the shell's stage code
        // while it is still unwinding the last switch is how that becomes a loop.
        var map = new MapViewModel();
        var moves = 0;
        map.PlacementChanged += _ => moves++;

        map.BringBackCommand.Execute(null);
        map.SendToStageCommand.Execute(null);
        map.SendToStageCommand.Execute(null);

        Assert.Equal(1, moves);
    }

    [Fact]
    public void The_panel_says_how_the_size_is_changed_and_what_it_would_become()
    {
        // The size control is a second press of the nav cell (ADR-0062's addendum), and a gesture
        // with nothing on screen to announce it is a control nobody finds. So the hint is part of
        // the feature, not decoration — and it names the size the press gives, not the current one.
        var map = new MapViewModel();
        Assert.Equal("TAP MAPS AGAIN FOR FULL SCREEN", map.ResizeHint);

        map.ResizeCommand.Execute(null);
        Assert.Equal("TAP MAPS AGAIN FOR BOTTOM THIRD", map.ResizeHint);
    }

    [Fact]
    public void The_hint_follows_the_size_without_being_asked_twice()
    {
        // It is bound, so it has to be announced: a hint that still reads FULL SCREEN after the
        // map went full screen is worse than none.
        var map = new MapViewModel();
        var told = new List<string>();
        map.PropertyChanged += (_, e) => told.Add(e.PropertyName ?? "");

        map.ResizeCommand.Execute(null);

        Assert.Contains(nameof(MapViewModel.ResizeHint), told);
        Assert.Contains(nameof(MapViewModel.ResizeCaption), told);
    }

    [Fact]
    public void The_occupants_first_action_is_the_way_back()
    {
        // The panel's own buttons are hidden on the stage, so the menu is the only way out short
        // of choosing something else — and an occupant with no way back is a trap. It is first
        // because it is the one you reach for; the full list is pinned in MapRenderTests, which
        // is where RECENTRE joined it.
        var map = new MapViewModel();
        map.SendToStageCommand.Execute(null);

        var occupant = new MapStageOccupant(map, () => map.BringBackCommand.Execute(null));

        Assert.Equal("BRING THE MAP BACK", occupant.Actions[0].Caption);

        occupant.Actions[0].Invoke();
        Assert.False(map.IsOnStage);
    }

    [Fact]
    public void The_occupant_names_itself_maps_and_says_what_it_would_centre_on()
    {
        var map = new MapViewModel(new Source(new LocationFix(49.9, -97.1, 60, 90, SignalQuality.Live, Noon)), new Clock(Noon));
        var occupant = new MapStageOccupant(map, () => { });

        Assert.Equal("MAPS", occupant.Name);
        Assert.Contains("on the stage", occupant.Describe(), StringComparison.Ordinal);
        Assert.Contains("49.90000", occupant.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_gps_the_panel_says_where_to_turn_it_on()
    {
        // Not "no position" — that reads as a map waiting for a satellite when the dash is
        // simply not reading the phone at all.
        var map = new MapViewModel();

        Assert.Equal("NO GPS", map.PositionText);
        Assert.Contains("Settings", map.PositionDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_that_is_open_with_nothing_on_it_says_so_by_name()
    {
        var map = new MapViewModel(new Source(null), new Clock(Noon));

        Assert.Equal("NO DATA", map.PositionText);
        Assert.Contains("COM4", map.PositionDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fix_reads_in_the_same_words_as_settings_and_the_probe()
    {
        var fix = new LocationFix(49.8951, -97.1384, 63.2, 84, SignalQuality.Live, Noon.AddSeconds(-4));
        var map = new MapViewModel(new Source(fix), new Clock(Noon));

        Assert.Equal(LocationStatus.Describe(fix), map.PositionText);
        Assert.Contains("4 s ago", map.PositionDetail, StringComparison.Ordinal);
        Assert.Contains("COM4", map.PositionDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stale_fix_is_not_dressed_up_as_a_live_one()
    {
        // The whole point of the readout: a map centred on a position that stopped arriving must
        // say so, because the picture looks identical either way.
        var map = new MapViewModel(
            new Source(new LocationFix(49.8951, -97.1384, double.NaN, double.NaN, SignalQuality.Stale, Noon.AddMinutes(-2))),
            new Clock(Noon));

        Assert.StartsWith("STALE", map.PositionText, StringComparison.Ordinal);
        Assert.Contains("2 min ago", map.PositionDetail, StringComparison.Ordinal);
    }
}

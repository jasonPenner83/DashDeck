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
    public void The_size_button_names_the_size_it_would_give_you()
    {
        var map = new MapViewModel();
        Assert.Equal("FULL SCREEN", map.ResizeCaption);

        map.ResizeCommand.Execute(null);
        Assert.Equal("BOTTOM THIRD", map.ResizeCaption);
    }

    [Fact]
    public void The_occupant_offers_one_action_and_it_is_the_way_back()
    {
        // The panel's own buttons are hidden on the stage, so this is the only way out of it
        // short of choosing something else — and an occupant with no way back is a trap.
        var map = new MapViewModel();
        map.SendToStageCommand.Execute(null);

        var occupant = new MapStageOccupant(map, () => map.BringBackCommand.Execute(null));

        var action = Assert.Single(occupant.Actions);
        Assert.Equal("BRING THE MAP BACK", action.Caption);

        action.Invoke();
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

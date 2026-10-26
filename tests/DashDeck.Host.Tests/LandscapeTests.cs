using System.IO;
using DashDeck.Host.Stage.Gauges;

namespace DashDeck.Host.Tests;

/// <summary>
/// The tablet on its side (ADR-0077): the numbers, and the console and climate columns.
/// </summary>
public sealed class LandscapeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"dashdeck-landscape-{Guid.NewGuid():N}");

    public LandscapeTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void On_its_side_the_stage_keeps_its_width_and_the_column_takes_the_rest()
    {
        // 90 + 714 + 108 = 912 tall; 912 + 456 = 1368 wide.
        Assert.Equal(714, BandGrid.LandscapeBodyHeight);
        Assert.Equal(BandGrid.DesignHeight, BandGrid.LandscapeStageWidth + BandGrid.LandscapeColumnWidth);
        Assert.Equal(456, BandGrid.LandscapeColumnWidth);
    }

    [Fact]
    public void The_stage_loses_66_of_height_and_the_cards_a_row()
    {
        Assert.Equal(636, BandGrid.StageOccupantHeightFor(landscape: false));
        Assert.Equal(570, BandGrid.StageOccupantHeightFor(landscape: true));
        Assert.Equal(5, BandGrid.RowsInHeight(BandGrid.StageOccupantHeightFor(landscape: false)));
        Assert.Equal(4, BandGrid.RowsInHeight(BandGrid.StageOccupantHeightFor(landscape: true)));
    }

    [Fact]
    public void The_columns_are_the_column_beside_the_stage()
    {
        Assert.Equal(BandGrid.LandscapeColumnWidth, LayoutCanvas.ConsoleColumn.Width);
        Assert.Equal(BandGrid.LandscapeBodyHeight, LayoutCanvas.ConsoleColumn.Height);
        Assert.Equal(LayoutCanvas.ConsoleColumn.Width, LayoutCanvas.ClimateColumn.Width);
        Assert.Equal(LayoutCanvas.ConsoleColumn.Height, LayoutCanvas.ClimateColumn.Height);
    }

    [Theory]
    [InlineData("console")]
    [InlineData("climate")]
    public void Each_Modern_column_loads_clean_and_fits(string which)
    {
        var column = which == "console" ? StageLayout.BuiltInConsoleColumn : StageLayout.BuiltInClimateColumn;

        Assert.Empty(column.Problems);
        Assert.Equal("builtin/modern", column.Id);
        Assert.All(column.Elements, e => Assert.True(
            e.X >= 0 && e.Y >= 0 && e.X + e.Width <= column.Canvas.Width && e.Y + e.Height <= column.Canvas.Height, e.Id));
    }

    [Theory]
    [InlineData("console")]
    [InlineData("climate")]
    public void Each_Modern_column_shows_everything_its_panel_does(string which)
    {
        var (panel, column) = which == "console"
            ? (StageLayout.BuiltInConsole, StageLayout.BuiltInConsoleColumn)
            : (StageLayout.BuiltInClimate, StageLayout.BuiltInClimateColumn);

        // Turning the tablet re-places the panel; it never drops a reading.
        Assert.Equal(panel.Elements.Select(e => (e.Id, e.Source.Signal)), column.Elements.Select(e => (e.Id, e.Source.Signal)));
    }

    [Fact]
    public void A_column_follows_its_panel_by_name_and_falls_back_to_Modern()
    {
        var yours = Path.Combine(_folder, "yours");
        Directory.CreateDirectory(yours);
        File.WriteAllText(Path.Combine(yours, "glass.json"), """{ "name": "Glass column", "elements": [] }""");

        var panelName = "glass";
        var column = new StageLayoutService(
            new StageLayoutLibrary(null, yours, LayoutCanvas.ConsoleColumn, StageLayout.ConsoleColumnBuiltIns),
            () => panelName,
            StageLayoutService.FollowTheme);

        Assert.Equal("Glass column", column.Current.Name);

        panelName = "lcars";
        column.Reload();
        Assert.Same(StageLayout.BuiltInConsoleColumn, column.Current);
    }
}

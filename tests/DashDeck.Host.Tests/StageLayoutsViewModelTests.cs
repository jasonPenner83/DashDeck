using System.IO;
using DashDeck.Host.Stage.Gauges;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Tests;

/// <summary>Settings ▸ Themes ▸ STAGE LAYOUT (ADR-0037): choosing, and starting your own.</summary>
public sealed class StageLayoutsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dashdeck-stage-vm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class NoDialogs : IThemeDialogs
    {
        public string? Opened { get; private set; }

        public string? PickThemeFile() => null;

        public string? PickExportFolder() => null;

        public void OpenFolder(string folder) => Opened = folder;
    }

    private (StageLayoutsViewModel Vm, StageLayoutService Service) Make(string themeLayout)
    {
        var shipped = Path.Combine(_dir, "shipped");
        Directory.CreateDirectory(shipped);
        File.WriteAllText(Path.Combine(shipped, "lcars.json"), """{ "name": "LCARS stage", "elements": [ { "type": "text", "x": 0, "y": 0, "width": 100, "height": 40, "content": "HI" } ] }""");

        var service = new StageLayoutService(new StageLayoutLibrary(shipped, Path.Combine(_dir, "yours")), () => themeLayout, null);
        return (new StageLayoutsViewModel(service, new NoDialogs()), service);
    }

    [Fact]
    public void Lists_follow_the_theme_first_and_marks_the_choice()
    {
        var (vm, _) = Make("lcars");

        Assert.Equal(["FOLLOW THE THEME", "F-150 CLUSTER", "LCARS STAGE"], vm.Rows.Select(r => r.Caption));
        Assert.True(vm.Rows[0].IsCurrent);
        Assert.Equal("LCARS STAGE", vm.CurrentName);
    }

    [Fact]
    public void Choosing_a_layout_shows_it_and_following_the_theme_comes_back()
    {
        var (vm, service) = Make("lcars");

        vm.ChooseCommand.Execute(vm.Rows.Single(r => r.Choice == "builtin/default"));
        Assert.Equal("builtin/default", service.Current.Id);
        Assert.True(vm.Rows.Single(r => r.Choice == "builtin/default").IsCurrent);

        vm.ChooseCommand.Execute(vm.Rows[0]);
        Assert.Equal("shipped/lcars", service.Current.Id);
    }

    [Fact]
    public void Saving_under_the_themes_name_replaces_its_stage_and_any_other_name_is_chosen()
    {
        var (vm, service) = Make("lcars");

        vm.SaveAsName = "lcars";
        vm.SaveAsCommand.Execute(null);
        Assert.Equal("yours/lcars", service.Current.Id);
        Assert.Equal(StageLayoutService.FollowTheme, service.Choice);

        vm.SaveAsName = "Towing";
        vm.SaveAsCommand.Execute(null);
        Assert.Equal("yours/towing", service.Current.Id);
        Assert.Equal("yours/towing", service.Choice);

        var towing = vm.Rows.Single(r => r.Choice == "yours/towing");
        Assert.True(towing.CanDelete);
        vm.DeleteCommand.Execute(towing);
        Assert.Equal(StageLayoutService.FollowTheme, service.Choice);
        Assert.Equal("yours/lcars", service.Current.Id);
    }
}

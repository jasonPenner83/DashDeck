using System.IO;
using DashDeck.Abstractions;
using DashDeck.Host.Components;

namespace DashDeck.Host.Tests.Components;

/// <summary>
/// The host's job on a folder of untrusted input: load what will load, and turn everything
/// else into a stated reason rather than a failure to start (ADR-0002). These cover the checks
/// that happen <em>before</em> any assembly is mapped — the ones that keep an incompatible or
/// malformed component from ever running.
/// </summary>
public class ComponentHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dashdeck-plugins-" + Guid.NewGuid().ToString("N"));

    public ComponentHostTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Plugin(string id, string? manifest)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);

        if (manifest is not null)
        {
            File.WriteAllText(Path.Combine(dir, "component.json"), manifest);
        }
    }

    private async Task<LoadedComponent> LoadOne(params string[] knownSignals)
    {
        var host = new ComponentHost(new FakeSignals(knownSignals), new FixedClock(DateTimeOffset.UnixEpoch), _root);
        await host.LoadAllAsync(CancellationToken.None);
        return Assert.Single(host.Components);
    }

    [Fact]
    public async Task A_folder_with_no_manifest_is_rejected()
    {
        Plugin("com.example.empty", manifest: null);

        var component = await LoadOne();

        Assert.Equal(ComponentState.Rejected, component.State);
        Assert.Contains("No component.json", component.LastError);
    }

    [Fact]
    public async Task An_unserved_apiVersion_is_incompatible_and_never_loads()
    {
        // A future major is not served; the component is marked incompatible without its
        // assembly — which is absent here — ever being needed.
        Plugin("com.example.future", """
            { "id": "com.example.future", "apiVersion": "2.0",
              "entry": { "assembly": "Future.dll", "type": "Future.C" } }
            """);

        var component = await LoadOne();

        Assert.Equal(ComponentState.Incompatible, component.State);
        Assert.Contains("apiVersion 2.0", component.LastError);
    }

    [Fact]
    public async Task A_declared_signal_the_catalog_does_not_define_is_rejected()
    {
        Plugin("com.example.badsignal", """
            { "id": "com.example.badsignal", "apiVersion": "1.0",
              "entry": { "assembly": "X.dll", "type": "X.C" },
              "signals": [ { "id": "vehicle.nonesuch" } ] }
            """);

        var component = await LoadOne("vehicle.speed", "engine.rpm");

        Assert.Equal(ComponentState.Rejected, component.State);
        Assert.Contains("unknown signal 'vehicle.nonesuch'", component.LastError);
    }

    [Fact]
    public async Task A_missing_entry_assembly_is_rejected_with_the_name()
    {
        Plugin("com.example.noasm", """
            { "id": "com.example.noasm", "apiVersion": "1.0",
              "entry": { "assembly": "NotThere.dll", "type": "X.C" } }
            """);

        var component = await LoadOne();

        Assert.Equal(ComponentState.Rejected, component.State);
        Assert.Contains("NotThere.dll", component.LastError);
    }

    [Fact]
    public async Task An_empty_plugins_folder_loads_nothing()
    {
        var host = new ComponentHost(new FakeSignals(), new FixedClock(DateTimeOffset.UnixEpoch), _root);
        await host.LoadAllAsync(CancellationToken.None);

        Assert.Empty(host.Components);
    }
}

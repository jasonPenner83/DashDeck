using System.IO;
using DashDeck.Host.Components;

namespace DashDeck.Host.Tests.Components;

/// <summary>
/// The manifest is the first gate: a malformed component is rejected with a message before a
/// line of its code runs (ADR-0002). These pin the shape checks it makes.
/// </summary>
public class ComponentManifestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dashdeck-manifest-" + Guid.NewGuid().ToString("N"));

    public ComponentManifestTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string json)
    {
        var path = Path.Combine(_dir, "component.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void A_complete_manifest_is_accepted()
    {
        var result = ComponentManifest.Read(Write("""
            {
              "id": "com.example.ok",
              "apiVersion": "1.0",
              "entry": { "assembly": "Ok.dll", "type": "Ok.Component" },
              "surfaces": ["widget"],
              "signals": [ { "id": "vehicle.speed", "rateHz": 2 } ]
            }
            """));

        Assert.True(result.IsAccepted);
        Assert.Equal("com.example.ok", result.Manifest!.Id);
        Assert.True(result.Manifest.HasWidget);
        Assert.Equal("vehicle.speed", result.Manifest.Signals[0].Id);
    }

    [Fact]
    public void A_manifest_with_no_id_is_rejected()
    {
        var result = ComponentManifest.Read(Write("""
            { "apiVersion": "1.0", "entry": { "assembly": "X.dll", "type": "X.C" } }
            """));

        Assert.False(result.IsAccepted);
        Assert.Contains("no id", result.Reason);
    }

    [Fact]
    public void A_manifest_with_no_apiVersion_is_rejected()
    {
        var result = ComponentManifest.Read(Write("""
            { "id": "com.example.x", "entry": { "assembly": "X.dll", "type": "X.C" } }
            """));

        Assert.False(result.IsAccepted);
        Assert.Contains("apiVersion", result.Reason);
    }

    [Fact]
    public void A_manifest_with_no_entry_is_rejected()
    {
        var result = ComponentManifest.Read(Write("""
            { "id": "com.example.x", "apiVersion": "1.0" }
            """));

        Assert.False(result.IsAccepted);
        Assert.Contains("entry", result.Reason);
    }

    [Fact]
    public void Malformed_json_is_rejected_rather_than_thrown()
    {
        var result = ComponentManifest.Read(Write("{ this is not json"));

        Assert.False(result.IsAccepted);
        Assert.Contains("not valid JSON", result.Reason);
    }

    [Fact]
    public void Unknown_fields_are_tolerated()
    {
        // A manifest written against a newer SDK degrades rather than failing to parse.
        var result = ComponentManifest.Read(Write("""
            {
              "id": "com.example.future",
              "apiVersion": "1.0",
              "entry": { "assembly": "F.dll", "type": "F.C" },
              "somethingFromTheFuture": { "nested": true }
            }
            """));

        Assert.True(result.IsAccepted);
    }
}

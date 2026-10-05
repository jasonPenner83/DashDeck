using DashDeck.Core.Catalog;

namespace DashDeck.Tests;

/// <summary>Loads the real shipped catalog, so tests exercise what actually runs.</summary>
internal static class TestCatalog
{
    private static SignalCatalog? _cached;

    public static SignalCatalog Load() => _cached ??= SignalCatalog.FromFile(Path());

    private static DashDeck.Core.Discovery.ObdReference? _reference;

    /// <summary>The shipped reference tables, <c>catalog/reference/</c> (ADR-0052).</summary>
    public static DashDeck.Core.Discovery.ObdReference Reference() =>
        _reference ??= DashDeck.Core.Discovery.ObdReference.Load(System.IO.Path.GetDirectoryName(Path())).Reference;

    public static string Path()
    {
        var dir = AppContext.BaseDirectory;

        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = System.IO.Path.Combine(dir, "catalog", "signals.obd2-standard.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        }

        throw new FileNotFoundException("Could not locate the signal catalog from the test binary.");
    }
}

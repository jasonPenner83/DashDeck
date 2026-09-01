using DashDeck.Core.Catalog;

namespace DashDeck.Tests;

/// <summary>Loads the real shipped catalog, so tests exercise what actually runs.</summary>
internal static class TestCatalog
{
    private static SignalCatalog? _cached;

    public static SignalCatalog Load() => _cached ??= SignalCatalog.FromFile(Path());

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

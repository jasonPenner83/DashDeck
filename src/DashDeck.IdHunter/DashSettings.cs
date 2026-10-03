using System.Text.Json;

namespace DashDeck.IdHunter;

/// <summary>
/// What the dash knows about the adapter, read from its own <c>settings.json</c>: the port it
/// chose, the rate and identity it last answered with, and the phone-GPS port never to open
/// (ADR-0034). The hunter finds the adapter the way the dash does, starting from these.
/// </summary>
internal sealed record DashSettings(string? Port, int? BaudRate, string? Identity, string? GpsPort)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DashDeck", "settings.json");

    /// <summary>Read the dash's settings; anything missing or unreadable is simply null.</summary>
    public static DashSettings Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new DashSettings(null, null, null, null);
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var root = doc.RootElement;

            string? Text(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                    ? v.GetString()!.Trim()
                    : null;

            int? Number(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n > 0 ? n : null;

            bool Flag(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

            var gps = Flag("gpsEnabled") && string.Equals(Text("gpsTransport"), "Bluetooth", StringComparison.OrdinalIgnoreCase)
                ? Text("gpsSerialPort")
                : null;

            return new DashSettings(Text("adapterSerialPort"), Number("adapterBaudRate"), Text("adapterIdentity"), gps);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new DashSettings(null, null, null, null);
        }
    }
}

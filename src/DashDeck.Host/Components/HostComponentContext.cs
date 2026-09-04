using System.IO;
using System.Text.Json;
using DashDeck.Abstractions;

namespace DashDeck.Host.Components;

/// <summary>
/// The host's side of <see cref="IComponentContext"/> — the services one component is lent.
/// </summary>
/// <remarks>
/// Built per component, so each gets its own storage scope, its own settings and a logger
/// tagged with its id. The signals and the clock are the host's own, shared: a component
/// observing signals is exactly the point, and one clock keeps replay coherent across the
/// whole app (ADR-0005).
/// </remarks>
internal sealed class HostComponentContext(
    IVehicleSignals signals,
    IComponentStorage storage,
    IComponentSettings settings,
    IComponentLogger logger,
    IClock clock) : IComponentContext
{
    public IVehicleSignals Signals => signals;

    public IComponentStorage Storage => storage;

    public IComponentSettings Settings => settings;

    public IComponentLogger Logger => logger;

    public IClock Clock => clock;

    // Actions stays null (the interface default): read-only until Phase 3 (ADR-0006).
}

/// <summary>
/// A component's private store, one small file per key under its own folder.
/// </summary>
/// <remarks>
/// The folder is the isolation: <c>%LOCALAPPDATA%\DashDeck\components\&lt;id&gt;\storage\</c>,
/// named by the component's id, which no other component is handed. A file per key rather
/// than one shared document, so a component writing one value cannot corrupt another's, and a
/// half-written file loses one key rather than the lot. Keys are mapped to safe filenames, so
/// a component cannot escape its folder with a key like <c>../../settings</c>.
/// </remarks>
internal sealed class FileComponentStorage(string root) : IComponentStorage
{
    public async Task<string?> ReadAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception) when (ct.IsCancellationRequested is false)
        {
            // A store is best-effort: an unreadable value is a missing value, never a crash.
            return null;
        }
    }

    public async Task WriteAsync(string key, string value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(root);
        var path = PathFor(key);

        // Write-and-rename, so a power cut mid-write leaves the old value rather than a
        // truncated one — the dash loses power without notice, and this is that promise.
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, value, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>Map a key to a file inside the scope. Never outside it.</summary>
    private string PathFor(string key)
    {
        var safe = string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "_";
        }

        return Path.Combine(root, safe + ".txt");
    }
}

/// <summary>
/// A component's settings, read from one JSON object of string values.
/// </summary>
/// <remarks>
/// Read-only from the component's side (the editing UI is the host's job, and unbuilt yet).
/// Loaded once and tolerant: a missing file, bad JSON or a non-string value all read as "no
/// value", so a component always gets its default rather than an exception. The
/// <see cref="Changed"/> event exists for when the editor lands; for now it is never raised,
/// which is honest — nothing changes settings yet.
/// </remarks>
internal sealed class FileComponentSettings : IComponentSettings
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public FileComponentSettings(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path));

                if (parsed is not null)
                {
                    foreach (var (key, element) in parsed)
                    {
                        if (element.ValueKind == JsonValueKind.String && element.GetString() is { } value)
                        {
                            _values[key] = value;
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // A component with unreadable settings runs on its defaults, rather than not at all.
        }
    }

    public event Action? Changed;

    public string? Get(string key) => _values.GetValueOrDefault(key);

    // Suppresses the "event never used" warning while keeping the contract's shape. The event
    // is real and will fire once settings are editable; until then there is nothing to raise it.
    private void NeverCalled() => Changed?.Invoke();
}

/// <summary>
/// Forwards a component's log lines to the host's sink, tagged with the component's id.
/// </summary>
/// <remarks>
/// The tag is the point: in a shared log a misbehaving component has to be identifiable, and
/// a component cannot be trusted to identify itself, so the host stamps its id on every line.
/// </remarks>
internal sealed class TaggedComponentLogger(string componentId, Action<string> sink) : IComponentLogger
{
    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        var line = $"[{componentId}] {level.ToString().ToUpperInvariant()}: {message}";

        if (exception is not null)
        {
            line += $"  ({exception.GetType().Name}: {exception.Message})";
        }

        sink(line);
    }
}

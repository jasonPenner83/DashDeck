using System.Diagnostics;
using System.IO;
using System.Windows;
using DashDeck.Abstractions;
using DashDeck.Abstractions.Wpf;

namespace DashDeck.Host.Components;

/// <summary>
/// The range of contract versions this host serves (ADR-0008).
/// </summary>
/// <remarks>
/// A component declares the <c>apiVersion</c> it was built against; the host serves a range,
/// so an old component keeps loading as the host advances. The range is <c>1.0</c> up to
/// whatever <see cref="ComponentApi.Version"/> currently is: same major (a major bump breaks
/// every component and needs its own ADR), minor no higher than the host understands.
/// </remarks>
internal static class ApiRange
{
    /// <summary>True when the host can serve a component that asked for this contract version.</summary>
    public static bool Serves(string apiVersion)
    {
        if (!TryParse(apiVersion, out var major, out var minor) ||
            !TryParse(ComponentApi.Version, out var hostMajor, out var hostMinor))
        {
            return false;
        }

        // Same major, and not from the future: a component built against a newer minor than
        // the host may use a member the host's contract does not have.
        return major == hostMajor && minor <= hostMinor;
    }

    private static bool TryParse(string version, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        var parts = version.Split('.');

        return parts.Length >= 2
            && int.TryParse(parts[0], out major)
            && int.TryParse(parts[1], out minor);
    }
}

/// <summary>
/// One component's whole presence in the host: what its manifest said, how loading went, and
/// — when it loaded — the wall around it and the context it was lent.
/// </summary>
/// <remarks>
/// A single type covers both the loaded and the rejected, because the host wants one list to
/// show and one list to test: a component whose <c>apiVersion</c> is not served is as much a
/// fact worth rendering as one that is running. When <see cref="State"/> is
/// <see cref="ComponentState.Incompatible"/> or <see cref="ComponentState.Rejected"/> there is
/// no instance behind it and <see cref="Reason"/> says why.
/// </remarks>
public sealed class LoadedComponent
{
    private readonly GuardedComponent? _guard;
    private readonly ComponentLoadContext? _context;
    private readonly IDashComponentView? _view;
    private readonly ComponentState _loadState;

    internal LoadedComponent(
        string id,
        ComponentManifest? manifest,
        GuardedComponent? guard,
        ComponentLoadContext? context,
        IDashComponentView? view,
        ComponentState loadState,
        string? reason)
    {
        Id = id;
        Manifest = manifest;
        _guard = guard;
        _context = context;
        _view = view;
        _loadState = loadState;
        Reason = reason;
    }

    /// <summary>The component id, from the manifest (or the folder, if that was all there was).</summary>
    public string Id { get; }

    /// <summary>Its manifest, when one parsed.</summary>
    public ComponentManifest? Manifest { get; }

    /// <summary>Why it is not running, when it is not.</summary>
    public string? Reason { get; }

    /// <summary>Where it is now — the guard's live state when loaded, the load outcome otherwise.</summary>
    public ComponentState State => _guard?.State ?? _loadState;

    /// <summary>The last runtime error the guard saw, if any.</summary>
    public string? LastError => _guard?.LastError ?? Reason;

    /// <summary>True when there is a live, non-disabled component behind this.</summary>
    public bool IsLive => _guard is not null && State is not (ComponentState.Disabled or ComponentState.Rejected or ComponentState.Incompatible);

    /// <summary>True when it offers a widget the dash can host.</summary>
    public bool HasWidget => _view is not null && Manifest?.HasWidget == true;

    /// <summary>True when tapping its widget should open a full-screen detail.</summary>
    public bool HasFullScreen =>
        _view is not null && Manifest?.ParsedSurfaces.Contains(ComponentSurface.FullScreen) == true;

    internal GuardedComponent? Guard => _guard;

    /// <summary>Became visible on a page. Start it, behind the guard.</summary>
    internal Task ActivateAsync(CancellationToken ct) =>
        _guard?.ActivateAsync(ct) ?? Task.FromResult(false);

    /// <summary>Left the visible page. Stop it, behind the guard.</summary>
    internal Task DeactivateAsync(CancellationToken ct) =>
        _guard?.DeactivateAsync(ct) ?? Task.FromResult(false);

    /// <summary>
    /// Build the component's widget, or null if it has none or throws building it.
    /// </summary>
    /// <remarks>
    /// A build that throws is contained like any other call into component code (ADR-0002):
    /// the caller renders a "component stopped" placeholder rather than letting the exception
    /// reach the dash. Called on the UI thread, once, when the card is first shown.
    /// </remarks>
    internal FrameworkElement? CreateWidget()
    {
        if (_view is null)
        {
            return null;
        }

        try
        {
            return _view.CreateWidget();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Build the component's full-screen detail, or null if it has none or throws building it.
    /// </summary>
    /// <remarks>Contained like every other call into component code (ADR-0002).</remarks>
    internal FrameworkElement? CreateFullScreen()
    {
        if (!HasFullScreen)
        {
            return null;
        }

        try
        {
            return _view!.CreateFullScreen();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Drop the component and unload its context, so it can be collected.</summary>
    internal void Unload() => _context?.Unload();
}

/// <summary>
/// Discovers, loads and owns the in-process components in <c>plugins/</c> (ADR-0002).
/// </summary>
/// <remarks>
/// The whole tier-1 story of ADR-0012: first-party code, one folder each, loaded into its own
/// collectible context sharing only <c>DashDeck.Abstractions</c>. Everything a component can
/// do wrong is expected here and turns into a <see cref="LoadedComponent"/> with a reason, not
/// an exception that stops the dash coming up — a folder in <c>plugins/</c> is untrusted input
/// the same way a manifest is.
/// <para>
/// Loading is deliberately shallow-then-deep: the manifest is read and its <c>apiVersion</c>
/// and signals checked <em>before</em> any assembly is mapped, so an incompatible or malformed
/// component costs nothing and never runs a line of its code.
/// </para>
/// </remarks>
public sealed class ComponentHost(IVehicleSignals signals, IClock clock, string pluginsRoot)
{
    private const int MaxLogLines = 200;

    private readonly List<LoadedComponent> _components = [];
    private readonly List<string> _log = [];

    /// <summary>Everything found in <c>plugins/</c>, loaded or not.</summary>
    public IReadOnlyList<LoadedComponent> Components => _components;

    /// <summary>The recent component log, tagged by id — for diagnostics.</summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>The loaded component with this id, or null if none was found or it was rejected.</summary>
    public LoadedComponent? ById(string id) =>
        _components.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Scan <c>plugins/</c>, load every component that will load, and initialise each behind
    /// its guard. Safe to call once at startup; never throws for bad component content.
    /// </summary>
    public async Task LoadAllAsync(CancellationToken ct)
    {
        if (!Directory.Exists(pluginsRoot))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(pluginsRoot))
        {
            ct.ThrowIfCancellationRequested();
            _components.Add(await LoadOneAsync(folder, ct).ConfigureAwait(false));
        }
    }

    private async Task<LoadedComponent> LoadOneAsync(string folder, CancellationToken ct)
    {
        var manifestPath = Path.Combine(folder, "component.json");
        var folderId = Path.GetFileName(folder);

        if (!File.Exists(manifestPath))
        {
            return Reject(folderId, null, $"No component.json in {folderId}.");
        }

        var read = ComponentManifest.Read(manifestPath);

        if (!read.IsAccepted)
        {
            return Reject(folderId, null, read.Reason!);
        }

        var manifest = read.Manifest!;

        // apiVersion first: an incompatible component must never load a line of its code.
        if (!ApiRange.Serves(manifest.ApiVersion))
        {
            return new LoadedComponent(
                manifest.Id, manifest, null, null, null, ComponentState.Incompatible,
                $"apiVersion {manifest.ApiVersion} is not served (host serves 1.0–{ComponentApi.Version}).");
        }

        // Signals next: a component asking for a signal the catalog does not define is broken
        // in a way that would only surface as an arbiter throw much later. Catch it here.
        foreach (var declared in manifest.Signals)
        {
            if (!signals.KnownSignals.Contains(declared.Id))
            {
                return Reject(manifest.Id, manifest, $"'{manifest.Id}' declares unknown signal '{declared.Id}'.");
            }
        }

        var assemblyPath = Path.Combine(folder, manifest.Entry.Assembly);

        if (!File.Exists(assemblyPath))
        {
            return Reject(manifest.Id, manifest, $"'{manifest.Id}' entry assembly '{manifest.Entry.Assembly}' is missing.");
        }

        ComponentLoadContext? context = null;

        try
        {
            context = new ComponentLoadContext(assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType(manifest.Entry.Type);

            if (type is null)
            {
                context.Unload();
                return Reject(manifest.Id, manifest, $"'{manifest.Id}' entry type '{manifest.Entry.Type}' was not found.");
            }

            if (Activator.CreateInstance(type) is not IDashComponent instance)
            {
                context.Unload();
                return Reject(manifest.Id, manifest, $"'{manifest.Id}' type '{manifest.Entry.Type}' is not an IDashComponent.");
            }

            // Identity must agree: the manifest is authoritative, and a component whose code
            // disagrees about its own id cannot be trusted to scope its storage.
            if (!string.Equals(instance.Id, manifest.Id, StringComparison.Ordinal))
            {
                context.Unload();
                return Reject(manifest.Id, manifest,
                    $"'{manifest.Id}' code reports a different id ('{instance.Id}').");
            }

            var logger = new TaggedComponentLogger(manifest.Id, Record);
            var guard = new GuardedComponent(instance, logger);
            var componentContext = BuildContext(manifest.Id, logger);

            await guard.InitializeAsync(componentContext, ct).ConfigureAwait(false);

            // The view half is optional (ADR-0010): only a component that draws something
            // implements it. Captured here so the dash can build a widget for it later.
            var view = instance as IDashComponentView;

            // A component with a widget is started and stopped by the page it sits on (the
            // ADR-0015 rule). One with no widget has no page to gate it, so it runs whenever
            // the vehicle is present — start it now, behind the same guard.
            if (!manifest.HasWidget && guard.State is not (ComponentState.Faulted or ComponentState.Disabled))
            {
                await guard.ActivateAsync(ct).ConfigureAwait(false);
            }

            return new LoadedComponent(manifest.Id, manifest, guard, context, view, guard.State, null);
        }
        catch (Exception ex)
        {
            context?.Unload();
            return Reject(manifest.Id, manifest, $"'{manifest.Id}' failed to load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private IComponentContext BuildContext(string id, IComponentLogger logger)
    {
        var scope = Settings.JsonFile.InLocalAppData(Path.Combine("components", id));
        var storage = new FileComponentStorage(Path.Combine(scope, "storage"));
        var settings = new FileComponentSettings(Path.Combine(scope, "settings.json"));

        return new HostComponentContext(signals, storage, settings, logger, clock);
    }

    private LoadedComponent Reject(string id, ComponentManifest? manifest, string reason)
    {
        Record($"[host] rejected {id}: {reason}");
        return new LoadedComponent(id, manifest, null, null, null, ComponentState.Rejected, reason);
    }

    private void Record(string line)
    {
        Debug.WriteLine(line);
        _log.Add(line);

        if (_log.Count > MaxLogLines)
        {
            _log.RemoveAt(0);
        }
    }

    /// <summary>Unload every component's context. Best-effort; for shutdown and hot-reload.</summary>
    public void UnloadAll()
    {
        foreach (var component in _components)
        {
            component.Unload();
        }

        _components.Clear();
    }
}

using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace DashDeck.Host.Components;

/// <summary>
/// The isolated, collectible context one component's assemblies load into (ADR-0002).
/// </summary>
/// <remarks>
/// <b>The one subtlety that makes or breaks in-process components: the shared contract must
/// not be loaded twice.</b> A type is identified by its assembly <em>and the context that
/// loaded it</em>, so if a component brought its own copy of <c>DashDeck.Abstractions</c>,
/// the <c>IDashComponent</c> it implements would be a different type from the one the host
/// knows, and the cast that turns the loaded object into a component would fail with a
/// baffling "cannot convert IDashComponent to IDashComponent". So the contract assemblies —
/// and the framework itself — are deferred to the host's default context, where exactly one
/// copy lives; everything genuinely private to the component loads from its own folder.
/// <para>
/// Collectible so a component can be unloaded and, in development, reloaded — the property
/// ADR-0002 called out as compounding the low-friction argument. Unload succeeds only once
/// nothing holds a reference into the context, which is why the host is careful to drop the
/// instance, its view and its context together.
/// </para>
/// </remarks>
internal sealed class ComponentLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// Assemblies that must come from the host, not the plugin folder.
    /// </summary>
    /// <remarks>
    /// Only the contract. Everything else the component legitimately carries its own copy of;
    /// framework assemblies (WPF included) resolve to the host anyway, because they are not
    /// beside the component's DLL for the resolver to find and so fall through to the default
    /// context. The contract is the one thing a component <em>could</em> ship alongside its
    /// DLL and must not be allowed to.
    /// </remarks>
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        "DashDeck.Abstractions",
        "DashDeck.Abstractions.Wpf",
    };

    private readonly AssemblyDependencyResolver _resolver;

    public ComponentLoadContext(string mainAssemblyPath)
        : base(isCollectible: true, name: Path.GetFileNameWithoutExtension(mainAssemblyPath))
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Returning null hands resolution to the default context, where the host's single
        // copy lives — which is exactly what the shared contract needs.
        if (assemblyName.Name is { } name && Shared.Contains(name))
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    /// <inheritdoc />
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}

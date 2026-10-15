using System.Reflection;
using DashDeck.Abstractions;

namespace DashDeck.Tests;

/// <summary>
/// Assembly identity must not move with the product version.
/// </summary>
/// <remarks>
/// Written after it did. Adding <c>&lt;Version&gt;0.9.0&lt;/Version&gt;</c> so the in-app updater
/// could tell which build it was (ADR-0061) also stamped 0.9.0.0 onto every assembly — and the
/// five components in <c>plugins\</c> are built separately, each holding a reference to
/// <c>DashDeck.Abstractions, Version=1.0.0.0</c>. .NET will load an assembly older than the
/// reference asks for in some cases and not others, and the failure arrives as a component that
/// quietly does not load rather than as a build error.
/// <para>
/// So the product version lives in <c>Version</c>/<c>InformationalVersion</c>, which the updater
/// reads, and <c>AssemblyVersion</c> stays pinned. This test is the guard, because the next person
/// to want a version number will reach for the same property.
/// </para>
/// </remarks>
public sealed class AssemblyIdentityTests
{
    [Fact]
    public void The_abstractions_assembly_version_never_moves()
    {
        var version = typeof(ComponentApi).Assembly.GetName().Version;

        Assert.Equal(new Version(1, 0, 0, 0), version);
    }

    [Fact]
    public void The_product_version_is_carried_separately_so_it_can_move_freely()
    {
        // What the updater compares against the newest release tag. It may be anything; what
        // matters is that it is not the assembly version above.
        var informational = typeof(ComponentApi).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.False(string.IsNullOrWhiteSpace(informational));
        Assert.True(
            DashDeck.Core.Updates.ReleaseVersion.TryParse(informational, out _),
            $"the updater must be able to read \"{informational}\"");
    }

    [Fact]
    public void Every_shipped_assembly_keeps_the_same_identity()
    {
        // One mismatched assembly is enough: a component references Abstractions, and the host
        // loads Core and Vehicle beside it.
        Version?[] versions =
        [
            typeof(ComponentApi).Assembly.GetName().Version,
            typeof(DashDeck.Core.Updates.ReleaseVersion).Assembly.GetName().Version,
            typeof(DashDeck.Vehicle.AdapterLinkOptions).Assembly.GetName().Version,
        ];

        Assert.All(versions, v => Assert.Equal(new Version(1, 0, 0, 0), v));
    }
}

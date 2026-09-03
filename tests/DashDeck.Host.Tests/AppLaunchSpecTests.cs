using DashDeck.Host.Stage;

namespace DashDeck.Host.Tests;

/// <summary>
/// Locating a native application to host.
/// </summary>
/// <remarks>
/// The adoption itself is Win32 and a foreign process, and is not unit-testable in any honest
/// way — it is verified by running the shell against a known-simple window and reading the
/// state back out of <c>Describe()</c>. What <em>is</em> testable is the part that decides
/// whether there is anything to launch at all, and that is where the user-visible "not
/// installed" answer comes from.
/// </remarks>
public sealed class AppLaunchSpecTests
{
    [Fact]
    public void The_first_candidate_that_exists_wins()
    {
        var spec = new AppLaunchSpec(
            "TEST",
            "",
            [@"C:\nowhere\missing.exe", @"%WINDIR%\System32\charmap.exe"]);

        Assert.True(spec.IsInstalled);
        Assert.EndsWith("charmap.exe", spec.Resolve(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Candidates carry environment variables so a spec stays a plain description. An MSI
    /// lands in different places depending on machine and version, and a spec that knew only
    /// one of them would tell somebody who installed the app that they had not.
    /// </summary>
    [Fact]
    public void Environment_variables_in_a_candidate_are_expanded() =>
        Assert.True(new AppLaunchSpec("T", "", [@"%WINDIR%\System32\charmap.exe"]).IsInstalled);

    [Fact]
    public void Nothing_installed_resolves_to_nothing_rather_than_throwing()
    {
        var spec = new AppLaunchSpec("TEST", "", [@"C:\nowhere\a.exe", @"%WINDIR%\nowhere\b.exe"]);

        Assert.False(spec.IsInstalled);
        Assert.Null(spec.Resolve());
    }

    [Fact]
    public void A_spec_with_no_candidates_is_not_installed() =>
        Assert.False(new AppLaunchSpec("TEST", "", []).IsInstalled);

    /// <summary>
    /// Nuvio is listed whether or not it is present, so this asserts the shape rather than
    /// the outcome — the answer differs between this machine and the tablet.
    /// </summary>
    [Fact]
    public void The_nuvio_spec_names_somewhere_plausible_to_look()
    {
        Assert.NotEmpty(AppLaunchSpec.Nuvio.Candidates);
        Assert.All(AppLaunchSpec.Nuvio.Candidates, c => Assert.EndsWith(".exe", c, StringComparison.OrdinalIgnoreCase));
    }
}

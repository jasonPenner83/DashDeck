using DashDeck.Host.Theme;

namespace DashDeck.Host.Tests;

/// <summary>
/// The guard that lets ADR-0014 allow custom accents at all.
/// </summary>
/// <remarks>
/// ADR-0013 refused a colour picker because a badly chosen accent breaks signal quality
/// <em>silently</em>. Custom colours are only defensible while this refuses the bad ones, so
/// these tests are the load-bearing part of that argument rather than coverage for its own sake.
/// </remarks>
public sealed class AccentValidationTests
{
    /// <summary>
    /// The load-bearing test. The hue floor is calibrated so Ember — the tightest preset —
    /// sits exactly on it, so this fails if the floor is raised, if a quality colour moves, or
    /// if someone adds a preset that would have quietly lowered the bar. The first version of
    /// the rule picked 25° by feel and rejected Ember itself.
    /// </summary>
    [Fact]
    public void Every_preset_passes_its_own_rule()
    {
        foreach (var preset in AccentOption.All)
        {
            var check = AccentValidation.Check(preset.Colour);
            Assert.True(check.IsUsable, $"{preset.Name} rejected: {check.Message}");
        }
    }

    [Theory]
    [InlineData("#5BC77E", "live")]       // the Live green itself
    [InlineData("#63CC85", "live")]       // two shades off it — the exact case ADR-0013 feared
    [InlineData("#E0B23C", "stale")]      // the Stale amber
    [InlineData("#8DA6FF", "simulated")]  // the Simulated blue
    public void A_colour_that_collides_with_a_quality_colour_is_refused(string hex, string expected)
    {
        Assert.True(AccentValidation.TryParse(hex, out var colour));

        var check = AccentValidation.Check(colour);

        Assert.False(check.IsUsable);
        Assert.Contains(expected, check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_colour_too_dark_for_the_canvas_is_refused()
    {
        Assert.True(AccentValidation.TryParse("#101018", out var colour));
        Assert.False(AccentValidation.Check(colour).IsUsable);
    }

    [Theory]
    [InlineData("#FF7A1A")]
    [InlineData("FF7A1A")]
    [InlineData("  #ff7a1a  ")]
    public void Hex_is_read_with_or_without_the_hash_case_and_padding(string text)
    {
        Assert.True(AccentValidation.TryParse(text, out var colour));
        Assert.Equal(AccentOption.Ember.Colour, colour);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    [InlineData("orange")]
    public void Anything_that_is_not_six_hex_digits_is_rejected_rather_than_guessed(string? text)
    {
        Assert.False(AccentValidation.TryParse(text, out _));
    }
}

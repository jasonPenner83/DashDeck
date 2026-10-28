using DashDeck.Core.Screen;

namespace DashDeck.Tests;

/// <summary>
/// When the dash asks for the touch keyboard (ADR-0079): a text field selected, and no keyboard
/// attached.
/// </summary>
public sealed class TouchKeyboardRuleTests
{
    // What a Surface lists with nothing attached: its buttons and its cover connector are keyboards
    // that are part of the tablet.
    private static readonly KeyboardDevice[] Tablet =
    [
        new("HID Keyboard Device", TouchKeyboardRule.LocalMachine),
        new("HID Keyboard Device", TouchKeyboardRule.LocalMachine),
        new("Surface Type Cover Filter Device", TouchKeyboardRule.LocalMachine),
    ];

    private static readonly KeyboardDevice Bluetooth =
        new("Logitech K380", new Guid("6f0e8f43-2b3a-4f0c-9d55-0d1e2f3a4b5c"));

    [Fact]
    public void A_text_field_on_the_bare_tablet_brings_it_up()
    {
        var decision = TouchKeyboardRule.Decide(textFieldFocused: true, slate: true, Tablet);

        Assert.True(decision.Show);
    }

    [Fact]
    public void Nothing_selected_asks_for_nothing()
    {
        var decision = TouchKeyboardRule.Decide(textFieldFocused: false, slate: true, Tablet);

        Assert.False(decision.Show);
        Assert.Contains("no keyboard attached", decision.Why);
    }

    [Fact]
    public void A_bluetooth_keyboard_keeps_it_away_and_is_named()
    {
        var decision = TouchKeyboardRule.Decide(textFieldFocused: true, slate: true, [.. Tablet, Bluetooth]);

        Assert.False(decision.Show);
        Assert.Contains("Logitech K380", decision.Why);
    }

    [Fact]
    public void The_type_cover_open_keeps_it_away()
    {
        var decision = TouchKeyboardRule.Decide(textFieldFocused: true, slate: false, Tablet);

        Assert.False(decision.Show);
        Assert.Contains("laptop mode", decision.Why);
    }

    [Fact]
    public void A_keyboard_windows_gave_no_container_counts_as_part_of_the_tablet()
    {
        Assert.False(TouchKeyboardRule.IsAttached(new("Mystery", null)));
        Assert.False(TouchKeyboardRule.IsAttached(new("Mystery", Guid.Empty)));
        Assert.True(TouchKeyboardRule.IsAttached(Bluetooth));
    }

    [Fact]
    public void One_keyboard_seen_twice_is_named_once()
    {
        var decision = TouchKeyboardRule.Decide(true, true, [Bluetooth, Bluetooth with { Name = "logitech k380 " }]);

        Assert.Equal("a keyboard is attached: Logitech K380", decision.Why);
    }
}

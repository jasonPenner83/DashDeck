namespace DashDeck.Core.Screen;

/// <summary>A keyboard Windows reports, and the device container it belongs to.</summary>
/// <param name="Name">What Windows calls it, for the About page.</param>
/// <param name="Container">
/// Its container id: every part of one physical device shares it. Null when Windows did not say.
/// </param>
public readonly record struct KeyboardDevice(string Name, Guid? Container);

/// <summary>Whether the touch keyboard should come up, and why, in words for the About page.</summary>
/// <param name="Show">True to ask for it.</param>
/// <param name="Why">The reason, either way.</param>
public readonly record struct TouchKeyboardDecision(bool Show, string Why);

/// <summary>
/// When the dash asks for the tablet's touch keyboard (ADR-0079): a text field is focused, and no
/// keyboard is attached, by Bluetooth, USB or the Type Cover.
/// </summary>
/// <remarks>
/// <para>
/// <b>The Type Cover</b> is told by the tablet's posture. With it attached and open Windows is in
/// laptop mode; detached, or folded back, it is a slate.
/// </para>
/// <para>
/// <b>Bluetooth and USB keyboards</b> are told by their container. Windows puts every part of the
/// computer itself in one container with a fixed id, and a keyboard that is a device of its own in
/// another. A Surface lists several keyboards that are part of the tablet — its buttons are a HID
/// keyboard — so counting keyboards would always find one. Counting the ones from outside does not.
/// </para>
/// <para>Pure: the dash asks Windows, and this decides.</para>
/// </remarks>
public static class TouchKeyboardRule
{
    /// <summary>The container id Windows gives every device that is part of the computer itself.</summary>
    public static readonly Guid LocalMachine = new("00000000-0000-0000-ffff-ffffffffffff");

    /// <summary>True for a keyboard that is a device of its own rather than part of the tablet.</summary>
    public static bool IsAttached(KeyboardDevice keyboard) =>
        keyboard.Container is { } container && container != LocalMachine && container != Guid.Empty;

    /// <summary>Decide.</summary>
    /// <param name="textFieldFocused">A text field has the keyboard focus.</param>
    /// <param name="slate">The tablet is in slate posture: no Type Cover, or folded back.</param>
    /// <param name="keyboards">Every keyboard Windows has enabled.</param>
    public static TouchKeyboardDecision Decide(bool textFieldFocused, bool slate, IEnumerable<KeyboardDevice> keyboards)
    {
        ArgumentNullException.ThrowIfNull(keyboards);

        if (!slate)
        {
            return new(false, "a keyboard is attached (the tablet is in laptop mode)");
        }

        var attached = keyboards.Where(IsAttached)
            .Select(k => string.IsNullOrWhiteSpace(k.Name) ? "a keyboard" : k.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (attached.Count > 0)
        {
            return new(false, $"a keyboard is attached: {string.Join(", ", attached)}");
        }

        return textFieldFocused
            ? new(true, "no keyboard attached, and a text field is selected")
            : new(false, "no keyboard attached; it comes up when a text field is selected");
    }
}

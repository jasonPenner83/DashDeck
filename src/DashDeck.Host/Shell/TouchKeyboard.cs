using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DashDeck.Core.Screen;
using KeyboardDevice = DashDeck.Core.Screen.KeyboardDevice;
using Windows.Devices.Enumeration;
using Windows.UI.ViewManagement;

namespace DashDeck.Host.Shell;

/// <summary>
/// The tablet's own on-screen keyboard (ADR-0065's addendum), asked for only when it is wanted
/// (ADR-0079): a text field is selected, and no keyboard is attached.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every text field, one place.</b> A class handler sees every <c>TextBox</c> take and lose the
/// keyboard focus — the map's search, Settings, the card editor — so none of them has to remember.
/// Selecting a field asks for the keyboard when <see cref="TouchKeyboardRule"/> says so; leaving
/// it for anything that is not a field puts the keyboard away.
/// </para>
/// <para>
/// <b>Not at launch.</b> Once the dash is first drawn, a keyboard showing with no field selected is
/// put away. Before this the search's nudge started <c>TabTip</c> whether or not a keyboard was
/// attached, and the keyboard came up with the app.
/// </para>
/// <para>
/// <b>Asked, then nudged.</b> Windows' input pane is asked first; <c>TabTip</c>, which ships with
/// Windows (C1), is started only when that does not answer. Every failure is silent: a field still
/// types from a keyboard, and a dash that falls over because a Windows component was not where it
/// was expected is worse than a field you have to tap.
/// </para>
/// </remarks>
internal static class TouchKeyboard
{
    /// <summary>The keyboard device interface every keyboard registers, by Bluetooth, USB or built in.</summary>
    private const string KeyboardInterface = "{884B96C3-56EF-11D1-BC8C-00A0C91405DD}";

    private const string ContainerId = "System.Devices.ContainerId";

    private const string EnabledKeyboards =
        "System.Devices.InterfaceClassGuid:=\"" + KeyboardInterface + "\" AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True";

    private const int SmConvertibleSlateMode = 0x2003;

    private static bool _watching;

    /// <summary>Watch every text field, and put away a keyboard nobody asked for once the window is drawn.</summary>
    public static void Watch(Window window)
    {
        if (!_watching)
        {
            _watching = true;
            EventManager.RegisterClassHandler(typeof(TextBoxBase), UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnSelected), handledEventsToo: true);
            EventManager.RegisterClassHandler(typeof(TextBoxBase), UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnLeft), handledEventsToo: true);
        }

        window.ContentRendered += (_, _) =>
        {
            if (!IsField(Keyboard.FocusedElement))
            {
                Hide(window);
            }
        };
    }

    /// <summary>What the rule says now, for the About page.</summary>
    public static async Task<TouchKeyboardDecision> DecideAsync()
    {
        var keyboards = await KeyboardsAsync().ConfigureAwait(true);
        return TouchKeyboardRule.Decide(IsField(Keyboard.FocusedElement), IsSlate(), keyboards);
    }

    private static async void OnSelected(object sender, KeyboardFocusChangedEventArgs e)
    {
        // The event bubbles, so a field inside another reaches the outer one's handler too.
        if (!ReferenceEquals(sender, e.NewFocus) || sender is not TextBoxBase field || field.IsReadOnly)
        {
            return;
        }

        try
        {
            var keyboards = await KeyboardsAsync().ConfigureAwait(true);

            // Still selected after the look: a tap straight through a field asks for nothing.
            if (field.IsKeyboardFocused && TouchKeyboardRule.Decide(true, IsSlate(), keyboards).Show)
            {
                Show(field);
            }
        }
        catch (Exception)
        {
            // Windows' own business; see the remarks.
        }
    }

    private static void OnLeft(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OldFocus) || IsField(e.NewFocus) || sender is not DependencyObject field)
        {
            return;
        }

        if (Window.GetWindow(field) is { } window)
        {
            Hide(window);
        }
    }

    private static bool IsField(IInputElement? element) => element is TextBoxBase { IsReadOnly: false };

    /// <summary>True when the tablet is a slate: no Type Cover, or one folded back.</summary>
    private static bool IsSlate()
    {
        try
        {
            return GetSystemMetrics(SmConvertibleSlateMode) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Every keyboard Windows has enabled, with its container.</summary>
    private static async Task<IReadOnlyList<KeyboardDevice>> KeyboardsAsync()
    {
        try
        {
            var found = await DeviceInformation.FindAllAsync(EnabledKeyboards, [ContainerId]);

            return [.. found.Select(d => new KeyboardDevice(
                d.Name ?? "",
                d.Properties.TryGetValue(ContainerId, out var container) && container is Guid id ? id : null))];
        }
        catch (Exception)
        {
            // None found reads as none attached, which on a slate brings the keyboard up: the safe
            // side on a tablet with no other way to type.
            return [];
        }
    }

    private static void Show(Visual field)
    {
        if (PresentationSource.FromVisual(field) is HwndSource { Handle: var handle } && handle != IntPtr.Zero)
        {
            try
            {
                if (InputPaneInterop.GetForWindow(handle).TryShow())
                {
                    return;
                }
            }
            catch (Exception)
            {
                // Older Windows, or a window the pane will not answer for: nudge instead.
            }
        }

        try
        {
            var path = Environment.ExpandEnvironmentVariables(@"%CommonProgramFiles%\microsoft shared\ink\TabTip.exe");

            if (System.IO.File.Exists(path))
            {
                using var started = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception)
        {
            // See the remarks.
        }
    }

    private static void Hide(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;

            if (handle != IntPtr.Zero)
            {
                InputPaneInterop.GetForWindow(handle).TryHide();
            }
        }
        catch (Exception)
        {
            // See the remarks.
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

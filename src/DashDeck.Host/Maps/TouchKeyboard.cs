using System.Diagnostics;

namespace DashDeck.Host.Maps;

/// <summary>
/// The tablet's own on-screen keyboard (ADR-0065's addendum).
/// </summary>
/// <remarks>
/// DashDeck drew its own first — five rows of themed keys — and it was replaced after a drive by
/// the Surface's, which is the keyboard the tablet's owner already knows and which brings holding
/// a key, the clipboard, prediction and other languages with it.
/// <para>
/// <b>It is asked for, not commanded.</b> Windows decides whether the touch keyboard appears, and
/// on a tablet with no keyboard attached it usually does so by itself when a text field takes
/// focus. Starting <c>TabTip</c> is a nudge for the cases where it does not, and a failure here is
/// silent on purpose: a USB keyboard still types into the field, and the dash must not throw
/// because a Windows component it does not own was not where it was expected.
/// </para>
/// <para>
/// Nothing is installed (C1): <c>TabTip</c> ships with Windows.
/// </para>
/// </remarks>
internal static class TouchKeyboard
{
    /// <summary>Ask Windows for the touch keyboard. Does nothing where there is none.</summary>
    public static void Show()
    {
        try
        {
            var path = Environment.ExpandEnvironmentVariables(
                @"%CommonProgramFiles%\microsoft shared\ink\TabTip.exe");

            if (!System.IO.File.Exists(path))
            {
                return;
            }

            using var started = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Windows' own business. The field still types from a keyboard, and the alternative —
            // a dash that falls over because a touch keyboard would not come up — is worse than
            // a search you cannot type into.
        }
    }
}

# ADR-0079 — The touch keyboard only when wanted, and the tools from Settings ▸ ABOUT

**Status:** Proposed · 2026-10-27
**Builds on:** [ADR-0065](ADR-0065-address-search.md) and its addendum (the Surface's keyboard),
[ADR-0061](ADR-0061-manual-update.md) (a batch file that runs after the dash exits),
[ADR-0044](ADR-0044-id-hunter.md), [ADR-0048](ADR-0048-serial-tap.md), [ADR-0050](ADR-0050-id-matcher.md) (the tools).
**Keeps:** C1 (nothing installed); read-only towards the vehicle.

## Context

Two requests from the owner.

1. **"The keyboard always opens with the app. It should only show if there is no keyboard attached
   (bluetooth or other) and a text field is selected."** Since ADR-0065's addendum the map's search
   focused its field and started `TabTip` whenever it opened. `TabTip` brings the keyboard up whether
   or not a keyboard is attached. Nothing else in the dash asked for it. Why it was up at launch was
   not reproduced away from the tablet. So the dash now never asks without a selected field, and it
   puts away a keyboard it finds showing at launch.
2. **"In Settings ▸ ABOUT lets have buttons to open things like IDmatcher/IDhunter."** The tools ship
   in the release beside `DashDeck.Host.exe`. Reaching them meant Explorer, which is hard to use on
   a mounted tablet.

## Decision

1. **One rule, in Core (`TouchKeyboardRule`).** Ask for the touch keyboard only when a text field
   has the keyboard focus and no keyboard is attached:
   - **The Type Cover** is told by the tablet's posture. `SM_CONVERTIBLESLATEMODE` says laptop with
     the cover attached and open, and slate with it detached or folded back.
   - **Bluetooth and USB keyboards** are told by their device container. Windows gives every part of
     the computer one fixed container id, `{00000000-0000-0000-ffff-ffffffffffff}`, and a device of
     its own another. A Surface lists several keyboards that are part of the tablet: its buttons are
     a HID keyboard. So *any keyboard at all* would always be true, but *a keyboard from outside* is
     not.
   - A keyboard Windows gives no container counts as part of the tablet.
2. **Every text field, one place (`Shell.TouchKeyboard`).** A class handler on `TextBoxBase` sees
   every field take and lose the focus: the map's search, Settings, the card editor.
   - Selecting an editable field lists the enabled keyboards (`DeviceInformation`, the keyboard
     interface class). If the rule says so and the field is still selected, the keyboard is asked for.
   - Focus leaving a field for anything that is not a field puts the keyboard away.
   - Once the window is first drawn, a keyboard showing with no field selected is put away.
3. **Asked, then nudged.** Windows' input pane (`InputPaneInterop.GetForWindow(...).TryShow()`) is
   asked first. `TabTip` is started only when that does not answer. Failures are silent, as before.
   The search no longer asks for anything itself: it focuses its field, and the rule does the rest.
4. **Settings ▸ ABOUT says what the rule sees.** A *Touch keyboard* line names the keyboard it
   counts as attached, or says none is. If the keyboard does not behave, this line is the first thing
   to read.
5. **Settings ▸ ABOUT ▸ TOOLS.** One row per tool in `DashTools.All` (ID hunter, ID matcher, serial
   tap, as `publish.ps1` places them, which a test checks). Each row has what it is for and an
   **OPEN** button. A tool missing from the install (a build from source) says *not in this install*
   and its button is off.
6. **OPEN closes the dash.** Every tool wants the adapter, which serves one program at a time, and a
   tool started beside a running dash finds its port in use.
   - The first tap arms the button (*TAP AGAIN: DASHDECK CLOSES*), as the menu's CLOSE does. A second
     tap within four seconds opens. Arming one tool disarms the others.
   - The dash writes a batch file to the temp folder and starts it, then closes. The file waits for
     the dash's process to exit (at most 30 s, then starts the tool anyway), starts the tool in its
     own folder, and deletes itself. This is the update swap's approach, for the same reason: a
     program that has exited cannot start anything.

## Consequences

- On the bare tablet, tapping any text field brings the keyboard up, and leaving it puts the
  keyboard away. With the Type Cover open, or a Bluetooth or USB keyboard connected, the dash never
  asks.
- Windows' own setting still applies. *Settings ▸ Time & language ▸ Typing ▸ Touch keyboard* set to
  **Always** shows it on a tap whatever the dash does. **When no keyboard attached** agrees with
  this rule.
- A receiver for a wireless mouse can register a keyboard and count as one. So can the Type Cover
  folded back while it is still connected. The About line names whatever was counted.
- The tools can be opened from the cab. DashDeck has to be started again afterwards: the tools do
  not start it.

## Not done

- Starting DashDeck again when a tool closes.
- Tools opened without closing the dash, for those that could run without the adapter (the hunter's
  `--simulate`).

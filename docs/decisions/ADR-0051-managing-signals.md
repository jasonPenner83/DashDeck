# ADR-0051 — Adding, editing, hiding and removing signals from the ID matcher

**Status:** Accepted · 2026-10-04
**Builds on:** [ADR-0050](ADR-0050-id-matcher.md) (the matcher manages DashDeck's identifiers),
[ADR-0032](ADR-0032-signal-discovery-and-user-catalog.md) (the overlay; TEST). **Keeps:** rule 3 (never
guess a PID: a typed definition is not offered until the truck has answered it), shipped files never
written.

## Context

The owner wants to add DashDeck signals from the matcher, and to remove them. The matcher could
already add one: accepting a match without a DashDeck signal selected made a new signal on save. It
could not name it properly, add one by hand, or take anything away.

Two things make removing harder than it sounds:
- **Built-in signals carry weight.** Screens, cards and components rely on them: Fuel Economy needs
  `engine.fuelRate`. Deleting one blanks whatever uses it.
- **The built-in catalog is never written on a device.** Only the user's overlay is
  (`signals.user.json`), and an overlay entry can replace a built-in but cannot delete one.

## Decision

Asked, the owner chose to **hide built-ins** and to allow **typed signals, held as unconfirmed**.

- **Two new catalog fields, both written only when true:**
  - **`hidden`:** the signal leaves the card editor's picker and the matcher's list, but stays in the
    catalog. A card, screen or component already using it keeps working.
  - **`unconfirmed`:** a definition typed by hand. The card editor does not offer it until **TEST in
    Settings ▸ Sensors** has answered exactly the request being saved, and it is saved from there.
- **Hide / unhide.** Hiding writes the overlay's own entry, marked hidden, or a hidden copy of the
  built-in. Unhiding drops that copy when nothing else about it was changed, or keeps the correction,
  unhidden.
- **Remove only removes your entry.** One of yours is removed, after a confirmation, since cards using
  it then show NO DATA. A correction of a built-in **reverts** to the built-in. A plain built-in cannot
  be removed; the button says to hide it instead.
- **New.** From the selected accepted match: measured, and stays confirmed if its request and scaling
  are kept. Or blank, typed by hand: unconfirmed.
- **Edit.** Changing the source (bus, module, mode, PID, bytes, scale or offset) makes a definition
  unconfirmed. Changing its name, category, range, rate or unit label does not: the truck's answer is
  the same. Editing a built-in saves a correction, and editing it back to exactly the built-in removes
  the correction.
- **On the dash:**
  - the picker filters on `ValueChoice.Offered` (not hidden, not unconfirmed), keeping a card's
    current choice;
  - Settings ▸ Sensors labels rows **UNCONFIRMED — TEST IT** and **HIDDEN**;
  - the tablet's editor saves a typed signal confirmed only when TEST answered the request as it now
    stands;
  - a placeholder given a real request stops being a placeholder.
- The operations live in `SignalWorkbench` (Core), pure functions on the overlay, tested.

## Consequences

- Removing a built-in for good, from what ships, is a change to the catalog in the repository, by PR.
  The matcher's hide is the device-side answer.
- An unconfirmed signal is still polled if something already asks for it, such as a layout naming it,
  and is shown with its quality as usual. It is only kept out of the picker.
- The overlay carries two more flags. Older builds ignore them, so an older build would offer a
  hidden or unconfirmed signal again.

## Rejected

- **Deleting built-ins from the overlay** (a "removed" marker): it blanks whatever uses the signal, by
  design. Hiding does what the owner wants without that.
- **Typed signals offered immediately:** that is the guessed-PID failure rule 3 exists to prevent.
- **From a match only:** the owner wanted to type in identifiers found elsewhere (FORScan's PID list,
  the ID hunter).

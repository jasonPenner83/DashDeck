# ADR-0056 — Multi-state signals: named states, a selector, and matching by typed names

**Status:** Accepted · 2026-10-06
**Extends:** [ADR-0050](ADR-0050-id-matcher.md) (the ID matcher's switch matching, generalised),
[ADR-0041](ADR-0041-console-dash.md) (the console), [ADR-0053](ADR-0053-catalog-driven-simulator.md)
(placeholders). **Answers:** Q24.

## Context

Some things on the truck are neither a number nor on/off: 4WD mode is 2H, 4A, 4H or 4L. The owner
asked how to manage them, and answered how:
- **Shown** as a row of all the states with the current one lit — *2H 4A 4H 4L*.
- **Found** in the ID matcher, the way switches are: put the truck in each state and type it as
  FORScan shows it.
- **And the others like it**: the gear selector, drive mode, wipers and headlights.

A switch was matched to one bit (`ScalingFitter.FromStates`). A state is usually a small field of bits
in one byte, and it is a *name*, not a number: no scale and offset turns `0x20` into "4H".

## Decision

### The catalog names states

A signal may carry **`states`**: a list of `{ "value": …, "name": … }`, where the value is what the
decode gives (raw bytes, masked, scaled — a masked field is not shifted). `SignalDefinition.StateName`
names a value; **a value with no name is shown as itself** (`?32`), never as the nearest state — a
wrong gear on a dash is worse than a question mark. `SignalCatalog.Check` refuses a state without a
name, and two states sharing a value or a name. The value on the bus is still a number with the usual
quality; only its presentation changes.

### Five placeholders

`drivetrain.4wdMode` (2H 4A 4H 4L), `transmission.gearSelector` (P R N D M), `vehicle.driveMode`
(Normal Eco Sport Tow/Haul Snow/Wet Mud/Rut Sand), `body.wipers` (Off Interval Low High) and
`body.headlights` (Off Parking On Auto) — `"placeholder": true`, no identifier (ADR-0053), values that
are only the order of the states. Nothing vehicle-specific is shipped (ADR-0052): the names are the
everyday ones, and pairing replaces them with the truck's values and FORScan's words. The synthetic
truck answers them by name (park while idling at the kerb, drive otherwise; Tow/Haul while towing;
the rest set on `SimulatedCabin`).

### Showing them

- **Cards** show the state's name (`WidgetCardViewModel.Text`, via `ValueChoice.States`), and never a
  bar.
- **Layouts** gain a **`selector`** element: every state the catalog names, in a row, the current one
  lit in the accent with a glow, the others quiet; no reading dims the row with a dash. The names come
  from the running catalog (`SignalStates.Of`, set at launch), not the layout, so a pairing changes
  the row without touching the layout.
- The built-in **Modern console** shows the gear selector and 4WD in the free space at the bottom
  centre. On the real truck both read as a dimmed dash until paired.
- **Settings ▸ Sensors** shows the state's name, and TEST in its editor says which state a reply
  decodes to. The editor now carries a definition's **mask** and states through a save — it used to
  drop the mask, which would have broken a paired switch on its confirming save.

### Matching them

Any word typed in the ID matcher that is not a number or on/off is a **named state**.
`ScalingFitter.FromNamedStates` tries, in each byte, every run of adjacent bits wide enough for the
number of states, and keeps those that read **the same every time a state was typed and differently
between states** — the narrowest in each byte only, because a wider field holding a working one adds
bits that never moved. Narrowest first, then lowest byte. Names group regardless of case and spaces;
the first spelling is kept. The hint asks for a second round of every state, which is what drops a
counter or checksum that happened to fit once.

A candidate is a `ScalingCandidate` with a `FieldMask` and `States`; pairing (`SignalPairing.Pair`)
gives the definition the mask, the states, and a range of 0 to the mask so an unseen state is still a
reading. Pack export writes `"states"` too.

## Consequences

- 4WD, gear, drive mode, wipers and headlights are on the matcher's NEEDS ID list, and the first two
  are on the console, dark until paired.
- A state the person never typed reads as `?value` until it is added to the signal's `states` by hand
  or matched again with it included.
- Matching states from FORScan's PID log (a text column) is not built; the live way is.
- A signal could in principle be shown as a number on one card and a name on another; it is not — a
  signal with states is always shown by name.

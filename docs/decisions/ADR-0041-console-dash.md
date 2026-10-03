# ADR-0041 — DASH is a console drawn from a layout file; the cards move to the stage

**Status:** Accepted · 2026-10-03
**Builds on:** [ADR-0037](ADR-0037-stage-layouts.md) and [ADR-0040](ADR-0040-climate-panel.md) (layouts
on a canvas) · [ADR-0030](ADR-0030-fuel-flow-without-maf.md) (no standard fuel rate on this truck) ·
[ADR-0038](ADR-0038-stage-launcher-file.md) (the launcher file). **Amends:** ADR-0015 — the arranged
cards are kept whole, but they are a stage occupant now, not the region below the stage.
**Keeps:** ADR-0006 (read only) · rule 7 (quality rendered, never hidden) · rule 3 (never guess a PID).

## Context

The two bands below the stage have held the arranged cards since ADR-0015. After driving a new
RAV4, the owner asked for that space to be a **modern console** — speed, odometer, instant fuel
economy, distance to empty, warning lights — and for the cards to become a stage page instead.
Asked, the owner chose: the console as **a layout file like CLIMATE**; **economy and range as
placeholders** for now — speed-density (ADR-0030) was built and parked, because the truck almost
certainly reports its own fuel flow and that is worth finding first; **real warning lights plus placeholders** for
the ones not yet found; and **a CARDS button on the launcher**.

## Decision

**DASH shows the console**, a layout on a **912 × 390** canvas (`LayoutCanvas.Console`) drawn with
the stage's engine, made when DASH is chosen and disposed when it is left — its signals are declared
only while it is on screen. The built-in is **Modern**: speed large on a glowing arc, rpm beside it,
fuel, engine temperature and range on the right, a row of nine warning lights, and a strip of
odometer, economy, stored codes and outside air. **LCARS (inspired)** ships `catalog/console/lcars.json` and names it
(`"consoleLayout": "lcars"`). Yours live in `%LOCALAPPDATA%\DashDeck\console\`, chosen in
**Settings ▸ Themes ▸ CONSOLE LAYOUT** — the stage's block, one template for all three canvases.

**A `warning` element**: an icon lit in `litColour` (with a glow) when its signal says so — by
`bit`, `below`, `equals` or `onAt` — faint when off, fainter with a grey quality dot when the truck
has not said. Nine icons are built in (`checkEngine`, `oil`, `battery`, `coolant`, `fuel`, `seatbelt`,
`door`, `brake`, `tpms`), drawn for DashDeck as path data on a 24 × 24 grid so the repository can
publish them; a layout may give its own path data instead. `below` works on indicators too.

**Which lights are real:**

| Light | Source | |
|---|---|---|
| Check engine | `diagnostics.checkEngine` — PID 01 byte A bit 7 | real, standard |
| Low fuel | `fuel.levelPercent` below 12 % | real, worked out |
| Engine hot | `engine.coolantTemp` at 112 °C | real, worked out |
| Battery | `vehicle.controlModuleVoltage` below 11.8 V | real, worked out |
| Oil pressure, seatbelt, door, brake, tyres | `warning.*`, MS-CAN `D1`–`D5` | **placeholders** |

The placeholders follow TPMS and the climate signals: the synthetic truck answers them (off,
Simulated); a real truck reads Unavailable and the light stays dark with a grey dot — never lit by a
guess, never claimed off. **A decode can now mask** (`"mask"`), so one PID 01 byte gives both the
light and **`diagnostics.dtcCount`**. **`vehicle.odometer`** is PID `A6` (SAE J1979, 2019 onward) —
the F-150 may not answer it, in which case it reads a dash and a Ford identifier is next.

**Economy and range are placeholders** — `fuel.economy` (L/100 km) and `fuel.range` (km), MS-CAN
`D6` and `D7`, like the warning lights: the synthetic truck answers them (Simulated); the real one
reads Unavailable and the console shows a dash. The truck's cluster shows both, so the real ones are
likely its identifiers, or fuel flow on 7E0 to work them out from; when found, they are a definition
in the vehicle pack and the console is unchanged. Speed-density with fill-up calibration was built
and is parked on `feature/speed-density-fuel`, the fallback if no identifier turns up.

**The cards are a stage occupant, `CARDS`** (`"type": "cards"` in the launcher file), second in
the built-in list and on the quick bar. One dashboard for the life of the shell: its cards, order
and page survive CARDS coming and going. The stage's 636 holds **five rows** where the old region
held three, so more of them fit a page. **While CARDS is not the occupant, no card asks for
anything** (`IsShown`) — a page nobody can see is not visible, so ADR-0015's rule holds. MODIFY
WIDGETS puts CARDS on the stage and enters edit mode; leaving CARDS leaves edit mode. **A launcher
file that never mentions `cards` gets CARDS at the end of the grid** — files written before this
would otherwise lose the cards; one that lists it `hidden` has chosen to.

## Consequences

- The dash in front of the driver no longer depends on what is on the stage. The cards gain room
  and lose their permanent place; a driver who wants them always visible puts CARDS on the stage.
- Economy and range read a dash on the truck until found; the shipped fuel components still read
  `engine.fuelRate`, which the truck does not answer.
- Five of nine warning lights are placeholders until the cluster or body module's identifiers are
  found — the same sweep-and-WATCH work as the HVAC module and TPMS.
- Speed and rpm, at 4 and 3 Hz, are the console's largest share of the ~19 requests a second.
- The card editor and component details can now open over CLIMATE as well as DASH; both panels
  hide beneath them.

## Alternatives considered

- **A fixed, hand-built console.** Rejected by the owner: not editable, not themeable.
- **Replace GAUGES with the cards.** Rejected by the owner: a launcher button keeps both.
- **Show only the real lights.** Rejected by the owner: the placeholders mark where each light goes
  and say plainly that it is not known yet, which an absent icon would not.
- **Licensed warning-light icon sets.** Rejected: the repository is public (MIT), so the icons are
  drawn for it.

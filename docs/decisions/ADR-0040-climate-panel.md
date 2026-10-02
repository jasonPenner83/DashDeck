# ADR-0040 — A read-only climate panel, drawn from a layout file

**Status:** Accepted · 2026-10-02
**Builds on:** [ADR-0037](ADR-0037-stage-layouts.md) (the stage as a file) ·
[ADR-0036](ADR-0036-token-themes.md) (a theme names its layouts) · ADR-0015 (only what is on screen
declares signals). **Keeps:** ADR-0006 and C3 (read only; no takeover of the factory climate) ·
ADR-0013 and rule 7 (quality rendered, never hidden). **Answers part of:** Q14.

## Context

CLIMATE has been in the nav since P0.5 with nothing behind it ("not built yet"), parked by Q14:
a climate card that *controls* the factory HVAC needs writes to an undocumented Ford network and an
ADR superseding ADR-0006, and fails the five gates as written. The owner asked for the climate
controls in the bottom half of the screen, looking modern and glass-like, "like it belongs in a high
end vehicle", and editable — default and by theme — as a JSON file, like the stage. Asked how far
to go, the owner chose:

- **Design and state feedback only, read only.** Control waits for Phase 3, as planned.
- **CLIMATE replaces the cards.** It is a nav destination; it draws where the cards are.
- **The same engine as the stage.** Not a second file format.

The truck's HVAC module has not been found yet (R2, ADR-0035's tools are how it will be).

## Decision

**A layout is drawn on a canvas** (`LayoutCanvas`): the stage's 912 × 636, or the **climate
panel's 912 × 390** — the two bands where the cards are. The format, parser, library, service and
view are the stage's (ADR-0037), parameterised by the canvas; a position outside it is a warning
naming the canvas.

**Four new element types**, usable on either canvas:

- **`setpoint`** — a set temperature drawn large, with a thin arc over `min`–`max`, an optional glow
  and a bright point at the value. No reading: `– –` and no arc fill.
- **`levels`** — a row of steps (rising `bars` or `dots`) lit up to the value. Below zero lights in
  `negativeColour`, so one element shows a seat that heats and cools (−3…3).
- **`indicator`** — a pill lit when its signal is on: by a `bit` of the value (airflow is one
  signal of bits), when it `equals` a value, or at least `onAt` (1). Off is outlined glass; **no
  reading is dimmed with a dash, never shown as off.**
- **`glass`** — a frosted panel: a translucent tint brighter at the top, a sheen, a hairline edge and
  a soft shadow. Painted, not a backdrop blur (which WPF cannot do cheaply); over a near-black
  background it reads as glass.

Every reading element carries the fixed quality dot and dims when Stale.

**The built-in climate layout is *Glass*** (`glass`): three frosted zones — driver set temperature
and seat, the fan, airflow and cabin temperature, passenger set temperature and seat — over a bar of
AUTO, A/C, RECIRC, DEFROST, REAR DEF and the outside air temperature. **LCARS (inspired)** ships
`catalog/climate/lcars.json` and names it with **`"climateLayout": "lcars"`**, as it names its stage.
The user's live in `%LOCALAPPDATA%\DashDeck\climate\`, with `examples\` beside them (Glass as JSON,
every shipped one), never loaded. **Settings ▸ Themes ▸ CLIMATE LAYOUT** chooses one or follows the
theme, with SAVE AS, RELOAD, DELETE and OPEN FOLDER — the stage's block, one template for both.

**The panel exists only while CLIMATE is chosen.** It is made when CLIMATE is chosen and disposed
when it is left, so its signals are declared only while it is on screen — the ADR-0015 rule for a
card page. The cards' page is not declared meanwhile, for the same reason.

**The climate signals are placeholders, like TPMS's.** Twelve `hvac.*` and `seat.*.climate` signals
in the standard catalog (category *Climate*, MS-CAN, mode 01 PIDs `C4`–`CF`) are **not Ford's** —
they are documented as simulation placeholders so the synthetic truck can answer them (flagged
Simulated) and the panel can be designed and tested. **On the real truck they read Unavailable and
every element shows a dash.** When the HVAC module and its identifiers are found (SCAN FOR MODULES, a
sweep, WATCH while pressing the buttons), the real definitions go in the F-150 vehicle pack, which
lays over the standard set, and the panel lights up with no change to any layout.

**Nothing is sent.** No element has a command; a tap on the panel does nothing. Control is Phase 3
and passes ADR-0006's five gates, or it does not ship.

## Consequences

- The climate panel is as editable as the stage: move, resize, recolour, drop the seats, add a gauge
  — any element works on either canvas, so a climate layout can carry a coolant gauge, and a stage a
  setpoint.
- `StageLayoutView` draws any canvas; `IReadingFace` is the seam every reading element is fed through.
- A placeholder that is never replaced would leave CLIMATE a page of dashes on the truck. That is the
  honest outcome (rule 7) and the push to find the module.
- `IsDestinationUnbuilt` is now STEREO alone.

## Alternatives considered

- **A climate card on the dash.** Rejected by the owner: a card is one cell of a page; the panel
  wants the whole region and its own layout.
- **A separate climate file format** (zones, buttons). Rejected by the owner: a second format to
  learn, and nothing in it could be placed on the stage.
- **Taking the climate panel over the stage instead of the cards.** Rejected: the stage is where
  phone projection and video play, and replacing it to glance at the fan would stop them.
- **Guessing Ford's HVAC identifiers.** Rejected by rule 3: a wrong set temperature is in range, so
  nothing would catch it. Placeholders that read Unavailable are honest; guesses are not.

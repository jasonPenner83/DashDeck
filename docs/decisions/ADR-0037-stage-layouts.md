# ADR-0037 — The stage as a file: layouts of gauges, text, clock and panels

**Status:** Accepted · 2026-10-02
**Builds on:** [ADR-0036](ADR-0036-token-themes.md) (token themes) · ADR-0018 (the stage is four
bands) · ADR-0004 (only what is on screen declares signals). **Keeps:** ADR-0013 (quality colours
fixed) and CLAUDE.md rule 7 (quality rendered, never hidden).

## Context

The GAUGES stage — the stage's idle default — was six round dials written into a XAML file: boost
and oil temperature large, volts, intake, throttle and load small. Their signals, ranges, ticks,
redlines, sizes and positions could not change without a build, and their look followed the theme
only as far as fonts. Asked for more control, the owner wanted all of it: sizes, data sources,
limits, positions, the style of the dial — and then the whole stage, tied to themes so the LCARS
theme brings an LCARS stage. JSON now, a GUI later.

Two things were also wrong with the old cluster:

- **It drew a confident zero.** A gauge whose signal never answered showed its minimum: on the
  F-150, which does not report oil temperature (Q4, bring-up), the OIL TEMP needle sat on 40 °C as
  if the oil were cold. That breaks rule 7.
- **It showed no signal quality.** Live, Stale and Simulated looked the same.

## Decision

**A stage layout is a JSON file of elements** — modelled on a Home Assistant dashboard, with
comments allowed, in the same family as the theme and catalog files:

```jsonc
{
  "name": "Towing",
  "background": "#000000",          // or "@canvas", or left out
  "elements": [
    { "type": "panel", "x": 0, "y": 0, "width": 220, "height": 86, "colour": "#CC99CC", "radius": "56,0,0,0" },
    { "type": "text",  "x": 240, "y": 30, "width": 400, "height": 50, "content": "ENGINE", "fontSize": 36 },
    { "type": "clock", "x": 652, "y": 30, "width": 250, "height": 50, "content": "HH:mm", "align": "right" },
    { "type": "gauge", "style": "lcarsBar", "x": 140, "y": 96, "width": 760, "height": 60,
      "label": "BOOST", "unit": "psi", "format": "0.0",
      "source": { "signal": "engine.intakeManifoldPressure", "minus": "engine.barometricPressure",
                  "minusFallback": 101.325, "scale": 0.1450377, "rateHz": 3 },
      "min": -15, "max": 25,
      "zones": [ { "from": 18, "to": 25, "colour": "#CC6666" } ],
      "parts": { "segments": 24, "capColour": "#FF9966" } }
  ]
}
```

- **Positions are stage pixels on a fixed 912 × 636 canvas**, scaled to the real stage. Elements
  draw in order, later on top.
- **Four element types**: `gauge`, `text`, `clock` (a .NET time format) and `panel` (a filled
  shape with its own four corner radii — enough to draw LCARS elbows from two panels and a black
  cut-out).
- **Five gauge styles**: `dial` (the F-150 cluster's needle, ticks and chrome bezel), `arc`, `bar`,
  `lcarsBar` (segmented pills with a caption cap) and `digital`. Each takes **parts** that tune it —
  needle width, arc thickness, segment count, colours — every one with a default, so a gauge sets
  only what it changes (`GaugeParts` lists them).
- **A gauge's source** is a catalog signal, optionally minus a second one, scaled and offset — enough
  for boost (manifold minus barometric, in psi) without a formula language. Each declares its own
  rate (at most 10 Hz); all of them share the adapter's ~19 a second, and a gauge spends budget only
  while the stage shows it (ADR-0004).
- **Colours are `#RRGGBB` or `@token`.** `@accent`, `@caption` and the rest follow the theme live
  and dim at night with it; a literal colour is exactly that, day and night.
- **Zones** colour a range of the scale, and the fill or lit segments take a zone's colour while
  the value is in it — so the redline lights red.

**Honest by construction.** A gauge with no reading draws **no needle and no fill**, and its readout
says `NO DATA` — never a zero. A Stale reading is drawn dimmed. A small dot in the corner of every
gauge carries the quality colour, the same fixed four as the cards (ADR-0013). A source that is not
in the catalog says `UNKNOWN SIGNAL` instead of drawing a scale for nothing.

**Nothing in a layout file can blank the stage.** An element that cannot be drawn honestly — no
source, `max` not above `min`, a rate over 10 Hz, text with no words — is left out and named; an
element off the edge, an unknown part or a bad colour is a warning. A file that is not a layout is
left out of the list and named. The built-in F-150 cluster is compiled in and always there.

**Where layouts live, and which one shows.** Shipped layouts in `catalog/stage/`; the user's in
`%LOCALAPPDATA%\DashDeck\stage\`. **A theme can name its stage** — `"stageLayout": "lcars"` — and the
stage follows the theme by default: wearing LCARS brings the LCARS stage, the DashDeck look brings
the F-150 cluster. A user's file with the same name as a shipped one wins, so saving your own copy as
`lcars` replaces the LCARS stage for that theme. **Settings ▸ Themes ▸ STAGE LAYOUT** lists FOLLOW
THE THEME and every layout, with SAVE AS (your editable copy of the one showing), DELETE (yours),
OPEN FOLDER and RELOAD; the three-dot menu on the stage has **RELOAD STAGE LAYOUT** for the
edit–look loop without leaving it.

**The shipped layouts are written out as examples.** Every launch writes each shipped layout,
exactly as shipped, and the built-in cluster as JSON, to `stage\examples\` beside the user's
layouts, with a README. They are references to read and copy from, not layouts: only files directly
in `stage\` are loaded, and the examples are put back whenever they differ from the running build.

**The first shipped layout is the LCARS (inspired) stage**: a frame of elbows and bars, a title and
the time, and the same six readings as segmented LCARS bars. The LCARS theme names it.

## Consequences

- The built-in cluster is the old one, data instead of XAML, and now honest: on the truck its OIL
  TEMP dial reads `NO DATA` with no needle until a Ford oil temperature PID is confirmed.
- `RoundGauge`, `GaugesView` and `GaugesViewModel` are gone; `GaugeFace` draws every style and
  `StageLayoutView` places elements.
- A GUI editor later writes the same file — the format is the contract, as the token file is for
  themes.
- The dash cards are unchanged. A gauge as a card style is a natural next step, as is the stage
  holding other occupants' widgets (weather, compass) as elements.

## Alternatives considered

- **Only gauges, not the stage.** Rejected once the owner asked for the whole stage: panels and text
  cost little, and without them an LCARS stage is LCARS bars floating on black.
- **A layer system** — gauges built from stacked arcs, ticks and needles. Rejected for now for the
  owner's choice of *pick a style, tune its parts*; it covers most looks with a far shorter file.
- **YAML.** Rejected: a second file format and a dependency, where JSON with comments already serves
  the themes and the catalogs.
- **A formula language for sources.** Rejected: one subtraction and a scale covers the cluster's
  derived reading; anything cleverer is a component (ADR-0023).
- **Layout positions in a grid** rather than pixels. Rejected: the owner asked for position and size
  control, and a fixed 912 × 636 canvas scaled to the stage is as simple as a grid and more exact.

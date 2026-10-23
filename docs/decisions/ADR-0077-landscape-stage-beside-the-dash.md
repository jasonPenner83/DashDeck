# ADR-0077 — Landscape: the stage beside the dash

**Status:** Proposed · 2026-10-23. Built and tested off the tablet; not yet seen on the Surface.
**Builds on:** [ADR-0018](ADR-0018-fixed-stage-and-overflow-menu.md) (the four-band stage),
[ADR-0037](ADR-0037-stage-layouts.md), [ADR-0040](ADR-0040-climate-panel.md) and
[ADR-0041](ADR-0041-console-dash.md) (layouts on a fixed canvas, scaled to their region).
**Keeps:** B2's portrait grid, exactly as it was; read-only towards the vehicle.

## Context

The dash was designed for one shape: the Surface Pro 7 upright, 912 × 1368. The window already
followed a rotation (`FillWorkArea` on `DisplaySettingsChanged`), but on its side it drew the upright
dash shrunk to two thirds between black bars. The owner sometimes uses the tablet on its side and
asked for both shapes to work.

The owner chose the arrangement and how it switches:

- **The stage beside the dash.** The stage at near-full size on the left, a column on the right, the
  nav along the bottom across the full width, the status strip on top.
- **Automatically**, following the tablet, with no setting.

## Decision (proposed)

1. **On its side the design surface is 1368 × 912, drawn 1:1**, as upright is. The status strip
   (90) and the nav (108) keep their heights and stretch across. The body between them is **714**.

2. **The stage keeps its upright width, 912**, and the full body height. Cards, the stage picker,
   the launcher bar and every occupant lay out as they do upright. The only loss is **66 of
   height**: the occupant's picture goes from 912 × 636 to 912 × 570. The cards re-pack to fit,
   four rows instead of five (`BandGrid.StageOccupantHeightFor`).

3. **The region is a column beside it, 456 × 714.** The console, climate panel and map go there.
   A full-screen view (Settings, the card editor, a component's detail, the map or Android at full
   size) takes the whole body, 1368 × 714, as it takes all six bands upright.

4. **Where each goes is pure and tested.** `DashDeck.Core.Screen.ScreenShape.Split` gives the two
   boxes for either shape, with or without the stage. `StageSplitPanel` arranges the stage and the
   region in them. It replaces the two-row grid, and upright it gives the same 780 and 390.

5. **The shape follows the work area**, on launch and on every display change. Clearly wider is
   landscape and clearly taller is portrait (`ScreenShape.IsLandscape`). Within 10 % of square it
   keeps what it was, so a window caught half way through a rotation does not flip back and forth.
   `--portrait` and `--landscape` force either shape, so both can be seen and `--shot` on a desk.

6. **The console and climate panel have column layouts of their own**, on new canvases of
   456 × 714: `console column` and `climate column`. A 912-wide panel squeezed into 456 would be
   half-size type. Their built-in Modern columns are the Modern panels re-placed: every element and
   signal the same, only positioned down the column instead of across. A test holds them to that.

7. **A column follows its panel by name; there is nothing to choose.** It shows the column with the
   same file name as the panel layout showing: `glass.json` beside Glass, `modern.json` beside
   Modern. With no column of that name it falls back to Modern's. Yours go in
   `%LOCALAPPDATA%\DashDeck\console-column\` and `climate-column\`, and the examples are written out
   beside them. Changing, reloading or re-theming the panel reloads its column.

8. **Turning the tablet makes the console and climate panels again** from the right layouts, and
   re-packs the cards. The stage occupant is not touched: a video, the map or the phone keeps
   running, and the phone's mirror re-fits to its new region as it does for any resize.

9. **The card editor scrolls** on its side: it is taller than 714. Upright it still fits.

## Consequences

- Upright is unchanged: the same numbers, the same arrangement. The first change a portrait user
  sees is none at all.
- On its side nothing is shrunk. The stage is 912 wide in both shapes, so anything written against
  that width keeps working.
- The shipped Glass and LCARS panels have no columns yet. On their side they fall back to Modern's
  column, which is plain but readable. Each is one JSON file to add later.
- A layout of yours for the upright console has no column until you write one. The README in
  `console-column\examples\` says so.
- The stereo panel is parked (ADR-0060). When it returns, it will need to work at 456 × 714 as well.

## Not done

- No setting to lock a shape. The owner asked for automatic, and Windows' own rotation lock already
  holds the tablet one way.
- No landscape arrangement of the stage itself. A stage layout is still drawn on 912 × 636 and
  scaled to 912 × 570, so it is slightly smaller with narrow bars at the sides.

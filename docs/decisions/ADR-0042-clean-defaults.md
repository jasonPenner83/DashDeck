# ADR-0042 — Clean defaults: type over shapes, and layouts that bring their own fonts

**Status:** Accepted · 2026-10-03
**Builds on:** [ADR-0040](ADR-0040-climate-panel.md) (the climate panel) ·
[ADR-0041](ADR-0041-console-dash.md) (the console). **Keeps:** rule 7 (quality rendered) ·
ADR-0036 (themes; a layout's fonts change only that layout).

## Context

Seen in the truck, the LCARS climate panel and console were a success; the defaults — Glass's
frosted panels and Modern's arcs, pills and glass strip — looked amateur. The owner asked for the
climate panel and the console to look like a BMW's: **less is more, simple fonts, and typography
rather than shapes to separate things.**

## Decision

**Clean is the new built-in for both** (`builtin/clean`). On black, with no panels, arcs, boxes or
pills:

- **Hierarchy is type.** A small caption above a large, light number, aligned to its edge; units
  smaller and quieter than their numbers; groups separated by space. The console's speed is the one
  large thing on the screen; the climate panel's two set temperatures sit at their edges.
- **Switches are words** that light when on (blue; orange for heaters) and stay grey when off — with a
  dash when not known, never claimed off.
- **Warning lights are nearly invisible until lit**, then they are the only colour on the console.
- **One family, two weights:** Segoe UI Variable (Windows 11) or Segoe UI — light numbers, regular
  captions.
- White and grey come from the theme's text tokens, so the defaults dim at night with the dash; only
  what is lit has a colour of its own.

**Glass and Modern are not lost**: they ship as `catalog/climate/glass.json` and
`catalog/console/modern.json`, chosen in Settings ▸ Themes as before.

**To draw this, the layout format gains** (all optional, all usable on any canvas):

- **`fonts`** on a layout — `{ "ui": …, "mono": … }` — shadowing the theme's `UiFont`/`MonoFont` for
  that layout's canvas only (a resource set on the canvas, nearest wins). A comma list falls back.
- **`valueWeight`/`labelWeight`** on reading elements, and `weight` on text.
- On a **digital** gauge: `align` (left, center, right) and `labelPosition: "above"` — caption over
  number, aligned; `unitSize`/`unitColour` to set the unit smaller; `noData` (`"–"`) instead of
  NO DATA.
- On an **indicator**: `style: "text"` — the word alone — and `align`.

## Consequences

- The defaults change for anyone on the DashDeck theme; LCARS is untouched, and a theme or a choice in
  Settings still wins.
- A layout can look unlike its theme on purpose. The theme still decides everything outside it.
- Segoe UI Variable is Windows 11's; on Windows 10 the fallback is Segoe UI, which looks close.

## Alternatives considered

- **Restyle Glass and Modern in place.** Rejected: their shapes are the problem, and someone may like
  them — keeping them as shipped files costs nothing.
- **A theme for the look** (fonts and colours as tokens). Rejected for this: the change is about the
  climate panel and console, not the shell's buttons and settings, and a layout-level font says so.
- **Bundling a font.** Rejected for now: Segoe UI is already on the tablet and reads well; a shipped
  font needs a licence check and a reason.

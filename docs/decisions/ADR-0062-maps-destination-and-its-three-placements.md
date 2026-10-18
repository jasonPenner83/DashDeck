# ADR-0062 — MAPS is a destination with three placements

**Status:** Accepted · 2026-10-16
**Builds on:** [ADR-0058](ADR-0058-offline-map-and-routing.md) (what the map will be),
[ADR-0018](ADR-0018-fixed-stage-and-overflow-menu.md) (the fixed four-band stage),
[ADR-0026](ADR-0026-persistent-source.md) (the stage's two slots),
[ADR-0041](ADR-0041-console-dash.md) (the region below the stage as a destination's own).
**Answers:** the layout half of Q18. **Opens:** Q43.
**Keeps:** read-only (ADR-0006) — a map asks the truck for nothing; C1 — nothing installed.

## Context

[ADR-0058](ADR-0058-offline-map-and-routing.md) decided what the map *is*: MapLibre GL JS in
WebView2 over a PMTiles archive, with GraphHopper beside it for routes. It did not decide where
the map goes, and that turns out to be the part the rest of the shell has to agree with.

Every destination so far owns exactly one region. The cards, the console and the climate panel
draw in the two bands below the stage; Settings and the card editor take all six and collapse the
stage behind them (Q17). A map does not fit either pattern, because **how much of the screen it
should have depends on what you are doing with it** — a strip beside the gauges when you know
where you are, the whole screen when you do not, and the stage itself when the map is the thing
you are watching and the cards are what you want underneath.

Retrofitting a second size onto a destination later is not a small change: it touches the band
arithmetic, the full-screen rule, the stage handoff and the nav at once. So the geometry is
settled now, before there is a map, and what is drawn in it today is honestly a placeholder.

## Decision

1. **MAPS is a nav destination** — the fourth cell, with a pin glyph. The nav is the destination
   list (B3) and a map is something you drive to.
2. **Three placements, named and modelled from the start:** `Panel` (the bottom third), `Full`
   (all six bands, stage collapsed), `Stage` (an occupant). The enum carries all three even
   though the handoff is the least finished of them, so it is a case in one type rather than a
   retrofit through the shell's geometry later.
3. **The placement is a value in `DashDeck.Core.Maps`** — `MapView`, a readonly record struct
   with `Resized()`, `SentToTheStage()` and `BroughtBack()`. The shell turns a placement into
   bands and regions and owns nothing of the rules.
4. **It opens in the bottom third**, and the size button names the size it would give you rather
   than the one you are in.
5. **Full screen reuses Q17's existing rule** rather than inventing a geometry: the map at full
   size reports as a full-screen view, the stage collapses to nought bands, and the one region
   below it fills all six. One binding, not two.
6. **The stage handoff goes through `SetStage`** — the same path the launcher uses, so ADR-0026's
   two slots and the switch rules apply to the map exactly as to anything else.
7. **Coming off the stage returns the map to the size it had before it went**, and that is the
   reason `MapView` is a value rather than two booleans.
8. **The controls sit at the bottom of the region** in the nav placements, and **in the overflow
   menu** on the stage — as actions (ADR-0022), not drawn inside the occupant's own picture.
9. **The built-in launcher's `MAPS` web entry is renamed `OSM`.** It is the online OpenStreetMap
   page, which is a different thing from the offline map, and two of them answering to one name
   would be a trap the first time someone tapped the wrong one in a dead zone.
10. **What is drawn is a placeholder — except the position, which is real.** The phone's GPS is
    proven on the truck (Q38), and whether there is a fix to centre on is the one question worth
    answering before a map is worth drawing.

## Reasoning

### Why three states and not two

The two visual sizes are what the feature needs. The stage is the third because the map is going
to be a WebView2 occupant anyway (ADR-0058), and the stage is where occupants live — so the
handoff is not a new mechanism, it is the one that already exists. Leaving it out of the model
would have meant the shell's map geometry knowing about two placements and the stage knowing
about a third, which is how a feature ends up with two sources of truth about where it is.

### Why the rules are in Core

`DashDeck.Tests` runs anywhere; the shell's suite runs only on Windows. Putting the placement
rules in the engine means the one rule that is actually easy to get wrong — that a map which went
to the stage from full screen comes back to full screen — is pinned in a test that runs on every
push, on any machine, rather than one that needs a Windows runner.

### Why the map is not in the stage picker yet

It would be the natural place for it, and that is exactly why it waits: the right home for a map
on the stage is a `map` entry in the launcher file (ADR-0038), beside every other option, not a
hard-coded thirteenth item. Offering a placeholder in three places would be three things to undo
when the real map lands. The handoff is reachable from the map's own panel, which is enough to
prove the geometry and costs nothing to replace.

### Why the controls are at the bottom

The bottom of this region is immediately above the nav strip at both sizes, and the nav strip is
the only band comfortably reachable from the driver's seat. At the top of the region they would
be mid-screen in the small size and at arm's stretch in the large one.

### Why the placement is reconciled after a stage change rather than reported by the occupant

Choosing anything else on the stage disposes the map occupant, and the map's placement must not
outlive it. The obvious implementation — tell the map from the occupant's `Dispose` — runs in the
middle of `SetStage` and re-enters it. So the shell reconciles once, after every stage change,
and `BroughtBack()` is idempotent for the case where the button got there first.

## Consequences

- **The nav is one cell closer to B3's unsolved overflow.** Four of about seven are spent.
- **Full screen is now something you can drive to.** Settings and the card editor are places you
  go deliberately and leave; a full-screen map is a place you sit for an hour. The nav stays on
  top of it and the stage keeps running behind it, both already true, but the case is new.
- **`--shot` reports the placement**, because a map on the stage and a map in the bottom third
  render as different regions and one picture cannot say which.
- **A `launcher.json` that names `MAPS` as a web page still works** — a user's file replaces the
  built-in list outright — but it will collide with the real map when it lands, and the entry
  should be renamed then.
- **The placeholder is a placeholder.** It says so on itself, which is the posture Q13 settled:
  an honest empty state beats a region that looks broken.

## Opens

- **Q43** — a WebView2 cannot be moved between the stage and the nav region without reloading it,
  so the real map's three placements need either one host that both regions can show or state
  that survives being rebuilt. The placeholder is stateless and hides this entirely.
- **Q30, Q31, Q32** — unchanged from ADR-0058: range requests, cold start, extract scope.

## Addendum — 2026-10-16: the nav cell is the size control

Decision 8 above put the size control at the bottom of the map's own region, as a chip beside
**TO THE STAGE**. That chip is gone. **A second press of MAPS in the nav swaps the two sizes**,
and the panel carries a hint — *TAP MAPS AGAIN FOR FULL SCREEN* — naming the size the press would
give.

The reasoning that put the chip at the bottom of the region was right about *where* and wrong
about *what*. The nav strip is the only band comfortably reachable from the driver's seat, and the
MAPS cell is in it — so the control was being drawn one band above a button the thumb had just
pressed to get there. Two targets for the same thing, one of them permanently better placed.

It also makes MAPS the only cell that does something when you are already on it, which is a new
behaviour in this nav and worth stating plainly:

- **Not on MAPS** → go to MAPS, at the size it was left in.
- **On MAPS, map in the nav** → swap the size.
- **On MAPS, map on the stage** → bring it back, at the size it had. There is no second size on
  the stage, and a nav cell that ignores a press reads as broken, so the press means the one move
  the map has there. **BRING IT BACK** stays in the region as the explicit affordance.

What this costs: a gesture rather than a visible control, which is why the hint is part of the
feature and is tested rather than being decoration. The alternative — leaving both — was worse
than either, because a chip and a nav press that do the same thing teach that neither is the way.

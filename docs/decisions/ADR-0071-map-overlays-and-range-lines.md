# ADR-0071 — Buttons on the map, three bases, track up, and range lines

**Status:** Accepted · 2026-10-21
**Builds on:** [ADR-0063](ADR-0063-tiles-as-you-drive.md) (the map and its bridge),
[ADR-0064](ADR-0064-the-map-wears-the-theme.md) (the theme's own cartography),
[ADR-0065](ADR-0065-address-search.md) (search and directions),
[ADR-0066](ADR-0066-navigation-as-sensors.md) (`RouteProgress`),
[ADR-0041](ADR-0041-console-dash.md) (`fuel.range`, the distance to empty).
**Amends:** [ADR-0062](ADR-0062-maps-destination-and-its-three-placements.md)'s decision 8 — the
map's *own* functions move off the chips below it and onto the map. **Keeps:** ADR-0021's airspace
rule, which is why they are drawn by the page; ADR-0013's fixed quality colours, which the range
colours follow in spirit.

## Context

Jason asked for overlays on the map: functions laid over it — search, **track up / north up**,
**satellite / road / terrain** — and **range lines**: the truck knows its distance to empty, so draw
an orange line where it would be 80 km from empty and red for everything past empty.

Three facts shaped the answer before any of it was a choice:

- **Nothing WPF draws can sit over a WebView2.** A browser control is a child window (ADR-0021).
  The place popup and the loading word are in the page for exactly this reason, and buttons *over*
  the map can only be drawn the same way.
- **The page is rebuilt on every placement change** (Q43). Whatever the buttons choose has to live
  outside it, like the camera, or the map would forget it was on satellite every time it was resized.
- **The map is drawn from a style the dash generates** (ADR-0064). A new base has to be a style
  the dash builds, not a different hosted one, or the theme's roads and names would stop applying.

## Decision

1. **The buttons are drawn by the page and decide nothing.** A column at the top right — SEARCH,
   the base, NORTH UP / TRACK UP, and the range — plus RECENTRE low on the left after a pan. Each
   is a 68 px target in the theme's colours. A press goes over the bridge as `map:press:<name>`,
   parsed by `MapMessage` against `MapControl` **by name**, and the view-model decides what it does
   and sends the buttons' new state back with `window.dash.buttons(…)`. The page holds no choice.
2. **The choices are a value in Core**, `MapChoices` (base, orientation, range on or off), held by
   the view-model so they survive a rebuild, and **saved to `settings.json`** (`mapBase`,
   `mapOrientation`, `mapRange`) so the map opens the way it was left. Road, north up and the range
   lines on by default.
3. **SEARCH and RECENTRE leave the chips below the map**; TO THE STAGE and TRY AGAIN stay, because
   they are about where the map is on the screen, or why it is not there. The stage's overflow
   menu keeps its own RECENTRE. On the stage the buttons are on the map too.
4. **Three bases, all built by `MapStyle.Build(palette, base)`:**
   - **Road** is the theme's map, unchanged.
   - **Satellite** puts imagery straight over the background and drops the fills it makes redundant
     (water, greenery, buildings), keeping the theme's roads and names over it. The imagery is
     **EOX's Sentinel-2 cloudless 2020** mosaic: no key, no account, the template MapLibre's own
     satellite example uses, licensed for non-commercial use with attribution — which a personal
     dash is. It is ten metres a pixel.
   - **Terrain** is the road map with the ground **hill-shaded** from **Terrain Tiles on AWS**
     (Mapzen's Terrarium encoding, an AWS open dataset, no key), under the water and the roads,
     in the theme's own colours so it darkens with the rest at dusk. Exaggerated, because most of
     Manitoba is flat enough that true relief draws nothing.
   - A `--map-style` override still wins over all three.
5. **Track up turns the map to the truck's course while it follows**, and puts the truck in the
   lower third so more of the road ahead is on screen. It turns only on a course the dash already
   trusts (above 5 km/h, the compass' rule); below that the map holds the last direction travelled
   rather than spinning at a red light. North up turns back at once. Panning still frees the map,
   and **rotation by two-finger twist is off**: which way is up is the button's to decide.
6. **The range lines are drawn from `fuel.range`**, the distance to empty, asked for at 0.2 Hz
   **only while a map is on screen with the lines on** — one shared link (ADR-0004). Miles are
   converted. Nothing usable means nothing drawn and the button says **NO RANGE**.
7. **Two rings around the truck: orange at 80 km from empty, red at empty.** Labelled on the line —
   `80 KM LEFT`, `EMPTY` — and dashed. Inside the reserve only the red one is left.
8. **And along a route, when there is one.** The route stays solid in the accent as far as the
   orange point, then **orange dashes to empty and red dashes past it**, over a dark bed so the
   dashes read on an orange accent. Measured from where the truck is *on the route*, and refused
   more than 150 m off it, as the countdown is (ADR-0066).
9. **The range colours are fixed, not themed** — orange `#FF8F1F`, red `#E5544B` (the dash's fault
   red) — for the reason the quality colours are: "running low" and "will not get there" must read
   the same on every theme. **A stale range drains them to grey**, as it does the vehicle marker.
10. **The arithmetic is in Core and tested:** `Geo.Destination` for the rings, `RangeLines` for the
    rings and the route cut, sharing `RouteProgress`'s nearest-point search rather than copying it.
    The view-model redraws only when the truck has moved 250 m or the range a kilometre.
11. **A rebuilt map is dressed when its page says it is ready**, not only when it is built. A
    surface passes scripts on only once its page exists, so the pin, the route, the buttons and the
    lines are given again on `map:ready`. Before this, a pin restored at build time was dropped.
12. **The page's own lines survive a restyle.** A new base or a theme change at dusk replaces the
    whole style; the route and the range lines are carried into the new one with MapLibre's
    `transformStyle`, from what the page is holding.

## Reasoning

### Why a ring and a route, not one of them

**A ring is a straight line and roads are not.** It is honest about one thing — nothing outside the
red ring can be driven to on this tank — and flatters everything inside it, because the road there
is longer than the crow's flight. The route says exactly where on the drive the fuel runs low and
out, and is only there once a destination is chosen. Each answers a question the other cannot: the
ring before you have decided where to go, the route once you have. Both are cheap, so both are drawn.

### Why the buttons decide nothing

The page could hold the choices — it is the thing being drawn. But it is rebuilt on every
placement change, it cannot save anything, and JavaScript in a WebView2 is the one part of this
feature that is not tested. So it does the two things only it can do — draw a button and notice a
press — and everything else is a value in Core and a view-model that is.

### Why EOX and not Esri

Esri's World Imagery is far sharper and is what most "satellite" buttons show. Its tiles are
reachable without a key, but the terms read as permission to use them inside Esri's own software
or through an ArcGIS Location Platform account with a key — 2 million tiles a month free, then
metered. That is a key in the deploy and an account to look after for a button. Sentinel-2 cloudless
needs neither, and ten metres a pixel is enough to read fields, rivers, yards, shelterbelts and
towns — which, on a prairie highway, is most of what a satellite view is for. If it is not enough on
glass, an Esri key is a setting and one more template, and the base already has a name.

### Why 80 km is a constant

The orange line is "where you would want to have found fuel by". It could be a setting; it is a
constant (`RangeLines.ReserveKm`) until there is a reason it should differ, because a setting nobody
changes is a decision nobody owns.

## What is not verified

- **Neither imagery nor elevation has been seen on the Surface.** The session that wrote this could
  reach the AWS elevation tiles (a 200 and a PNG) and could not reach EOX — its proxy refuses the
  host — so the imagery template is the one MapLibre's own example uses, unprobed from here.
- **The page was driven in headless Chromium**, with MapLibre 6.12 as vendored: the buttons draw and
  send their presses, track up turns the map and the compass, the rings and their labels draw, the
  route is cut solid–orange–red, and all of it survives a restyle to satellite. No real tiles were
  loaded and no touch was used, so how it reads on glass in a cab is for the walkthrough.
- **`fuel.range` is a placeholder on the truck** until its identifier is found (ADR-0041). With
  the synthetic truck it is the simulator's range, so the whole thing can be seen at a desk.

## Consequences

- The map now spends the truck's data on a second kind of tile when satellite or terrain is on.
  Imagery is JPEG and larger than a vector tile; the browser caches both like the rest.
- Two more data sources to credit, and they are: EOX and AWS Terrain Tiles are in the map's own
  attribution control, from the style, and in `THIRD-PARTY-NOTICES.md`.
- **Satellite and terrain are online-only**, like the road map until Q44. An offline cut of either
  is a bigger archive than the vector tiles and is not planned.
- The range lines are only as good as the cluster's own distance to empty, which is the truck's
  estimate from recent economy. A tow, a headwind or a full bed will move them, and they move with
  the number on the button.

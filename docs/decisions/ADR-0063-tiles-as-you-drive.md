# ADR-0063 — Tiles as you drive, from OpenFreeMap

**Status:** Accepted · 2026-10-16
**Amends:** [ADR-0058](ADR-0058-offline-map-and-routing.md) — its *offline-first* ordering and its
"no network tile server" rule. Everything else in it stands: MapLibre in WebView2, PMTiles for the
offline half, GraphHopper for routing.
**Builds on:** [ADR-0062](ADR-0062-maps-destination-and-its-three-placements.md) (where the map is
drawn), [ADR-0027](ADR-0027-phone-location.md) (where the position comes from),
[ADR-0021](ADR-0021-owned-not-reparented.md) (a browser control is a child window).
**Answers:** the display half of Q18. **Opens:** Q44. **Changes:** Q30, Q43.

## Context

[ADR-0058](ADR-0058-offline-map-and-routing.md) decided the map would be **offline-first**: a
PMTiles archive on the tablet, no network tile server, on the grounds that "guidance that stops
when signal does is worth little on a rural Manitoba highway", and that "the truck's Wi-Fi does
not change that".

Two things about that have since been pinned down, and they move the ordering.

**The connection is the truck's own**, on its own data plan — not a phone hotspot shared with the
GPS tether. It is still cellular and still gone on a rural highway, so the conclusion about
guidance holds. What changes is the cost of *starting* online: it is not competing with the phone
that is already feeding the dash its position.

**Offline-first was blocking.** The offline half needs a tile archive cut to a region, which needs
Q32's scope answered, which needs gigabytes on the tablet and a refresh chore — before a single
map has been seen on the screen. Meanwhile the thing most likely to be wrong is none of that: it
is whether MapLibre renders usefully in that region at that size, whether the marker reads at a
glance, and whether the three placements survive a live browser control. All of which a live map
answers in an afternoon.

## Decision

1. **Tiles come from [OpenFreeMap](https://openfreemap.org), fetched as the map is panned** —
   no API key, no registration, no stated request limit, commercial use allowed.
2. **The style is their `dark`**, `https://tiles.openfreemap.org/styles/dark`. `--map-style <url>`
   swaps it without a rebuild; the override must be HTTPS with no credentials or it is ignored.
3. **Never OpenStreetMap's own tile servers.** Their usage policy prohibits exactly this, and
   names archives and offline use specifically.
4. **MapLibre GL JS is vendored**, not loaded from a CDN — 6.12.0, BSD-3-Clause, in
   `src/DashDeck.Host/Map/web/lib/`, deployed as `map/lib/`.
5. **The page is served from a virtual host**, `https://dashdeck.map/index.html`, mapped to the
   deploy's `map` folder. Not `file://`: the page is an ES module and MapLibre starts a web
   worker, and a file origin refuses both.
6. **The dash drives the page over a string bridge** — `window.dash.position/camera/follow` out,
   `map:ready` / `map:error:` / `map:camera:` / `map:free` back — parsed by a tested type in Core.
7. **The camera lives outside the page**, so it survives the rebuild a placement change causes.
8. **The map follows the vehicle until it is panned**, and offers RECENTRE once it has been.
9. **Nothing invents a centre.** With no fix there is no map and the region says which of the
   three reasons it is: GPS off, no fix yet, or the tiles did not load.
10. **The offline half is still coming**, and is now Q44: a caching proxy on localhost, seeded
    from a PMTiles cut of OpenFreeMap's own planet.

## Reasoning

### Why OpenFreeMap over the paid tiers

MapTiler and Stadia both have free tiers that are described as non-commercial and metered in
"sessions" and "credits" — workable, and both would need an API key in the deploy and a watch on
the meter. OpenFreeMap needs neither, and the licence question does not arise.

The real argument is the one that is not about price. **It publishes the same planet as a PMTiles
archive** — weekly, at `btrfs.openfreemap.com`, in the format ADR-0058 already chose. So the
online map and the offline map end up the *same data rendered by the same style*, and a province
is cut out of their planet rather than built from an OSM extract by us. Two maps that disagree
about where a road is would be worse than either alone.

Its weakness is real and worth writing down: **donation-funded, one tile server behind round-robin
DNS, no SLA.** That is survivable precisely because of the archive — if it goes dark or adds
limits, the fallback is a file already on the tablet. An offline half that was merely nice to have
is load-bearing for this choice.

### Why vendored rather than a CDN

The dash is launched by hand in a truck (C5). Nothing between the process starting and a drawn map
should need the network to resolve, and a CDN would make the renderer itself a thing that can fail
to arrive. 1.2 MB in the deploy is cheaper than that. Taken a fortnight after release rather than
at latest, so a fresh regression has had time to surface before it reaches a vehicle.

### Why the page and the placeholder never overlap

A WebView2 is a child window, so WPF cannot draw over it — the same airspace rule that shaped the
launcher bar and the overflow menu (ADR-0021, ADR-0022). A "loading" panel drawn by the dash over
the map would be *underneath* it and invisible. So the states are split by who can draw them: the
three with no page at all — GPS off, no fix, failed — are the dash's, and **the loading state
lives inside the page**, over its own canvas. The browser's background is set to the dash's canvas
colour so the join does not flash white in a dark cab.

### Why the camera is a value in Core

Because the map is rebuilt whenever it moves between the three placements, and a reloaded page
starts wherever it is told. Without the camera outside it, every resize would throw you back to
where the map first opened — and the first thing anyone does with a map is resize it. It is also
where the clamping and the invariant-culture formatting live, both of which are failures that only
appear on the tablet: a `NaN` across the bridge draws a blank canvas with no error, and a locale
with a decimal comma turns one coordinate into two numbers.

### Why "no signal" is said plainly

Every failure in a browser control looks the same on screen — a dark rectangle — and on this
connection the cause is nearly always signal. A dash that says "no map" without saying that sends
you into the settings to fix something that is not broken.

## What is not verified

**None of the network side has been run.** The session that wrote this could not reach
`openfreemap.org`, `tiles.openfreemap.org` or `btrfs.openfreemap.com` — its proxy refuses them —
so the endpoint URLs are read from the project's own README on GitHub and have never been probed.
The same was true of ADR-0058 and Geofabrik. **One `curl` on the Surface settles it**, and the
in-vehicle walkthrough starts with exactly that.

Nor has the map been seen to render. There is no Windows, no WebView2 and no signal here, so what
is tested is the camera, the bridge protocol, the source rules and every state the region can be
in — and what is unproven is MapLibre drawing, the virtual-host mapping, the worker starting under
it, and touch behaving on glass.

## Consequences

- **The dash now fetches data while driving**, on a metered connection. Vector tiles are small and
  the browser caches them, but this is the first feature that spends the truck's data by moving.
- **A map nobody is looking at must not fetch.** The surface is built when the MAPS region is on
  screen and disposed when it leaves, which the existing panel lifecycle already does.
- **The browser profile is shared** with the web stage occupant, deliberately: one cache, so tiles
  fetched today are warm tomorrow. It also means clearing it affects both.
- **Attribution is now an obligation in two places** — the map's own control, and the loading
  state, because the obligation survives a map that has not drawn.
- **Q30 is no longer on the critical path.** PMTiles range requests in WebView2 were the first
  spike; with tiles arriving over HTTPS there is nothing to range-request yet, and when the offline
  half lands the proxy reads the archive with file seeks instead (Q44).
- **A second WebView2 exists.** Two browser controls in one app, sharing a profile. If the dash
  ever grows a third, this is the point at which a shared environment becomes worth building.

## Opens

- **Q44** — the offline half: a caching tile proxy on localhost, seeded from a PMTiles cut of
  OpenFreeMap's planet. The architecture is sketched and nothing is built.
- **Q43** — partly answered here: the camera survives a rebuild, so the cost of a placement change
  is a redraw rather than losing your place. Whether that redraw is acceptable in the cab is a
  question for glass.
- **Q32** — extract scope, still unanswered and now needed for Q44 rather than for the first map.

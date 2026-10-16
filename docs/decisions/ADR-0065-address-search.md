# ADR-0065 — Address search, and the dash's own keyboard

**Status:** Accepted · 2026-10-16
**Implements:** [ADR-0058](ADR-0058-offline-map-and-routing.md)'s fifth decision — "destinations:
map tap, favourites and recents first; address search is online-only" — and its distraction rule,
"no destination entry above a speed threshold".
**Builds on:** [ADR-0062](ADR-0062-maps-destination-and-its-three-placements.md) (the placements),
[ADR-0063](ADR-0063-tiles-as-you-drive.md) (the map), [ADR-0021](ADR-0021-owned-not-reparented.md)
(airspace). **Opens:** Q46.

## Context

The map can be panned and pinched, and that is the whole of it. Saying *where you want to go*
needed three things that did not exist: somewhere to type, something to type on, and a geocoder.

The second of those turned out to be the real decision. **Nothing in the dash has ever needed
typing in the cab** — every Settings field reachable there is a picker, and text entry has only
ever happened at a desk or in the ID matcher.

## Decision

1. **The geocoder is Nominatim**, OpenStreetMap's own, used under its acceptable-use policy.
2. **No auto-complete, ever.** The policy says plainly that auto-complete "is not yet supported by
   Nominatim and you must not implement such a service on the client side using the API". Typing
   never searches; a SEARCH button is the only thing that asks.
3. **One request a second at most**, checked in code rather than left to the interface.
4. **Results are cached** by normalised query, as the policy asks — and because searching the same
   address twice is what anyone does when the first answer was not the one they meant.
5. **A User-Agent that names DashDeck, its version and the public repository.** A generic agent is
   grounds for being blocked, and the repository is where a complaint reaches somebody.
6. **DashDeck draws its own on-screen keyboard.** One page, no shift: a digits row, three letter
   rows, and a row of the marks an address uses.
7. **The search covers the map and takes it full screen.** It cannot overlay: a WebView2 is a child
   window and WPF cannot draw a keyboard over one.
8. **A destination cannot be typed above 5 km/h** — the same threshold the compass already uses for
   a trustworthy heading, rather than a second number that nearly agrees.
9. **Choosing an address moves the map, pins it, and stops following.** RECENTRE is the way back.
10. **Recents are offered and held for the session only.** Persisting them is worth doing and is
    not this change.

## Reasoning

### Why our own keyboard rather than Windows'

Windows' touch keyboard is in-box, costs almost nothing and would have kept C1 intact. It was
rejected on one property: over a borderless full-screen window it decides for itself whether to
appear and what to cover, and the failure mode is *nothing happens when you tap the field*. That is
not a thing to discover in a truck, in the dark, trying to get somewhere.

Ours is five rows of 78-pixel keys that follow the theme, sit where we put them, and cannot fail to
appear. The layout is **data in Core**, so what a press does to a query is tested without a UI
thread and the view does nothing but paint.

### Why one page and no shift

The first layout had a letters half and a numbers half. A test asserting that rows are proportioned
alike failed it — one row came to 4.6 key-widths against another's 10, which would have drawn some
keys half the size of others, and the small ones are the keys a thumb misses on a rough road.

Fixing the proportions showed the mode was not worth having. A street number is the most likely
thing typed first, so the digits get their own row; addresses match case-insensitively, so there is
no case to shift. Five rows, nothing hidden, fewer taps. **The test did not just catch a bad
layout, it rejected a bad design** — which is the argument for the layout being data.

### Why the search takes the whole region

Airspace, and then arithmetic. A keyboard cannot be drawn over a browser control (ADR-0021), so the
search covers the map rather than floating on it. And five rows of keys, a field and a list of
results do not fit the bottom third — so opening the search moves the map to its full-screen
placement first.

That is the first time a placement change has happened for a reason other than taste, and it cost
nothing because ADR-0062 had already built the three placements and the camera that survives moving
between them.

### Why the speed gate is a gate and not a warning

ADR-0058 decided this before any of it existed, and it is right: typing an address takes both
hands' worth of attention. The same posture as the warning popups (ADR-0055) — the dash does not
let you, rather than telling you not to.

It is read on the beat rather than latched, so stopping unlocks it and pulling away locks it again
with nothing else having to happen.

**The hole, stated plainly:** an unknown speed counts as stopped. No position means no map, so the
search is unreachable anyway; what is left is a fix that went stale while moving, which carries no
speed. Refusing on unknown would lock out every desk and every parked truck with the phone off,
which is where this will mostly be used.

### Why the HTTP call is injected

The same reason as `UpdateService`: the URL, the bias box, the parsing, the rate limit and the
cache are all testable without a network, and they are the parts that matter. What is left to the
real client is a `GetStringAsync` and the User-Agent.

### Why the search is biased, not fenced

A search for "Main Street" from a cab in Manitoba should find the one up the road, so the request
carries a `viewbox` around the truck — but not `bounded`, so a genuinely distant address is still
found. A preference, not a fence.

## Consequences

- **The dash now depends on donated infrastructure for a feature.** Nominatim is run by the OSM
  Foundation and the policy can change. The politeness here is the rent; if the usage ever grows
  beyond "one truck, a few searches a day", the answer is Q46 rather than asking for more.
- **Search works only with signal**, as ADR-0058 decided. With none, recents are what is left.
- **No routing.** This finds a place and pins it. Getting there is ADR-0058's second stage.
- **Nothing is persisted.** Recents go when the dash closes, which is wrong and is a small change
  (`JsonFile.InLocalAppData` already exists) deliberately left out of a first cut.
- **A keyboard now exists** that other parts of the dash could use — Settings' text fields have
  never been reachable in the cab for exactly this reason. It is in Core as data, so pointing
  something else at it is a view, not a rewrite.
- **Unseen, like the rest of the map.** No Windows, no browser, no network here: the query rules,
  the keyboard, the URL, the parsing, the gate and all nineteen view-model behaviours are tested,
  and whether a 78-pixel key is big enough with gloves on is for glass.

## Opens

- **Q46** — offline search. Photon is the answer and the shape is already decided elsewhere:
  Apache-2.0, built on Nominatim's own import, and GraphHopper — already chosen for routing
  (ADR-0058) — publishes its dumps. Same move as the tiles: somebody's public instance now, our
  own process later.
- **Q44** — unchanged. Tiles, glyphs and now search all stop at the edge of signal.

# ADR-0064 — The map wears the theme

**Status:** Accepted · 2026-10-16
**Amends:** [ADR-0063](ADR-0063-tiles-as-you-drive.md) — "the style is OpenFreeMap's `dark`". The
tiles, the host, the bridge and the camera are unchanged; only who decides the colours.
**Extends:** [ADR-0036](ADR-0036-token-themes.md) — ten more tokens in the same vocabulary.
**Opens:** Q45. **Keeps:** ADR-0013 — the four quality colours are still not themeable.

## Context

The map arrived wearing somebody else's clothes. OpenFreeMap's hosted `dark` style is good and it
is *theirs*: a dark grey map under an LCARS console, and no way to make a Star Trek map at all.

Two shapes were available, and the codebase already has both.

**A style file per theme**, the way a theme names its `stageLayout`, `climateLayout` and
`consoleLayout`. Full control, and nobody would ever write the second one — a complete
OpenMapTiles style is a thousand lines of JSON, and the user's own themes would get nothing.

**Tokens**, the way everything else about a theme works: a flat list of named values, each with a
default that may follow another token (`@textLow`), each with a night rule. That is the one that
scales, because of a property the token system already has and a style file cannot: **a default
that follows an existing token means a theme which says nothing about maps still gets a map that
matches it.**

## Decision

1. **Ten colour tokens** — `mapLand`, `mapWater`, `mapGreen`, `mapBuilding`, `mapRoad`,
   `mapRoadMajor`, `mapRoadCasing`, `mapLabel`, `mapLabelHalo`, `mapMarker` — in
   `ThemeTokens.All` beside every other token, with night rules.
2. **Every default follows a token that already exists** where one fits: the ground follows
   `canvas`, the roads the text ramp, the casing `canvas`, the halo `mapLand`, the vehicle
   `accent`. Only water and greenery have literals, because nothing on the dash is water.
3. **They carry no resource key.** They paint no WPF, so `ThemeService.Apply` skips them — a
   guarded case that had to be added, since without it all ten would be written to the
   empty-string resource and overwrite one another.
4. **DashDeck draws its own cartography**, generated from the palette: `MapStyle.Build` in Core,
   pure and tested. Eleven layers.
5. **Deliberately simpler than a browsing map.** OpenFreeMap's own styles are around two hundred
   layers; this is eleven. Fewer colours, bolder lines, bigger labels.
6. **The style is rebuilt and pushed on every applied palette** — a theme worn, an accent picked,
   dusk falling — so a map already on screen turns with the rest of the dash.
7. **`--map-style <https url>` still wins**, and under it the theme supplies only the marker. The
   vehicle is ours whoever draws the ground beneath it.
8. **A style that will not parse is never handed to the renderer.** It falls back to the hosted
   dark style and records why.
9. **Alpha is dropped** on the way to the map.
10. **Labels are Noto Sans**, from OpenFreeMap's glyph server, whatever the theme's fonts say.

## Reasoning

### Why authoring our own cartography is the cheaper path, not the dearer one

It looks like the expensive choice and it is not, for two reasons.

A style a theme recolours has to be one we can account for. Somebody else's two hundred layers are
not a contract — they change weekly, and a heuristic recolour produces mush: a road casing that
lands on the same value as its fill, a label halo that disappears.

And **a dash map wants to be simpler anyway.** OSM styles are tuned for a desktop, a mouse and
time to look. At a glance through a windscreen, past a speed readout, what reads is few colours and
bold lines. Eleven layers from ten colours is not a reduced version of a proper map; it is the
right map for the job, and it happens to be reviewable.

### Why this makes an LCARS map two lines

The LCARS theme already sets `textLow` to lavender, `textMid` to peach and `accent` to LCARS
orange. So its roads come out lavender, its motorways peach and its vehicle orange **without the
theme mentioning maps**. The only things it has to say are the two with no equivalent on the dash:

```jsonc
"mapWater": "#1F1F3D",
"mapGreen": "#140F1A"
```

That is the whole argument for tokens over files, and it was not obvious until the tokens were
chosen to follow the right things. Picking `@textLow` for roads rather than a literal grey is what
made the Star Trek map fall out of a theme written months earlier.

### Why night comes free, and why that matters more than it sounds

Night is derived by dimming: surfaces to 0.55, ink to 0.74, accent to 0.82. Because the map tokens
go through the same rules, a night map keeps its labels legible over a darker ground rather than
fading evenly into illegibility — and ten night values that would otherwise be hand-maintained in
every theme are maintained nowhere.

### Why the style is built in Core

It is the only part of this feature that can be held to account without Windows, a browser or a
signal: it is data. So the cartography is tested — the JSON parses, every layer names a source
layer the tiles actually carry, the theme's colours reach the properties they name, the layer order
puts casings under roads and names over them, and nothing in it fixes a camera (which would undo
the camera the dash keeps across a rebuild, Q43).

That last one is a real trap: a style with its own `center` and `zoom` would throw the map back to
where it was authored on every placement change.

### Why `$ROAD` is substituted after `$ROADMAJOR`

Because it is a prefix of it, and of `$ROADCASING` — as `$LABEL` is of `$LABELHALO`. Getting that
order wrong leaves `#CC6666MAJOR` in the style, which fails to parse. It is a one-line ordering
with a test of its own, named for what it is.

## Consequences

- **Every theme now has a map**, including the user's own and ones written before this existed.
- **Two things a theme cannot reach:** the label font, and anything structural. A theme can recolour
  the map and not change what it draws.
- **The style goes over the bridge as JSON**, which is a JavaScript expression — so it is handed to
  `setStyle` as one rather than as a string the page has to parse. Around 6 KB a restyle.
- **`Legibility(ResolvedTheme)` does not yet check the map.** A theme can produce a technically
  themed, practically unreadable map — water against ground, label against ground. Worth adding.
- **One fewer hard-coded colour:** the vehicle marker was `#FF7A1A` in the page's CSS and is now
  the theme's accent.
- **Nothing here has been seen to render**, same as ADR-0063. The style is tested as data; whether
  it reads well in a cab is for glass, and `--map-style` is one argument away as the comparison.

## Opens

- **Q45** — can the map's labels use the theme's own font? It needs glyph PBFs built from a TTF,
  which is a build-time tool; until then an LCARS map is Star Trek colours in Noto Sans.
- **Q44** — unchanged, and now also owns the glyph server: labels are the first thing to fail when
  signal drops, because the tiles cache and the fonts do not.

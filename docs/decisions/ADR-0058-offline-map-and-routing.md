# ADR-0058 — Offline map and routing: MapLibre in WebView2, GraphHopper beside it

**Status:** Accepted · 2026-10-13
**Decides:** Q18's stack. **Builds on:** [ADR-0019](ADR-0019-phone-projection.md) and
[ADR-0021](ADR-0021-owned-not-reparented.md) (the stage and its occupants),
[ADR-0027](ADR-0027-phone-location.md) / [ADR-0028](ADR-0028-bluetooth-gps.md) (where position
comes from). **Keeps:** read-only — nothing here touches the vehicle; and constraint C1 — nothing
is installed on the tablet.

## Context

Q18 settled two things on 2026-10-13: guidance is built in-app on a non-Google map, and it is
**offline-first**, because guidance that stops when signal does is worth little on a rural
Manitoba highway. The overlap with Android Auto's licensed Maps guidance was put squarely and
accepted — a map that needs no phone is wanted anyway.

MapLibre renders. It does not route. So three things need choosing, and two of them are not
obvious: where the renderer lives, where tiles come from, and what computes a route. Turn
instructions and voice follow from the third.

## Decision

1. **Renderer — MapLibre GL JS inside the existing WebView2**, as a stage occupant. The stage
   already hosts web applets, so the map arrives through a door that exists.
2. **Tiles — a single PMTiles file** built from a Prairie OSM extract, stored on the tablet and
   served to WebView2 **without a network tile server**.
3. **Routing — GraphHopper, bundled, as a child process on localhost**, with the routing graph
   built at publish time rather than on the tablet.
4. **Instructions and voice — GraphHopper's maneuver instructions**, spoken through Windows
   speech, ducking DashDeck's own audio per Q14.
5. **Destinations — map tap, favourites and recents first. Address search is online-only** and
   degrades to those three when there is no signal.

## Reasoning

### Why not the obvious .NET answer

**Itinero** is the routing library a .NET app reaches for, and it is not usable here. Its latest
NuGet package is **`1.6.0-pre037`, published 2023-09-20** — three years stale, and still a
prerelease after 353 of them, with no stable release ever cut. Navigation in a moving vehicle
cannot rest on that. Checked directly against the NuGet flat-container index rather than taken on
reputation, because reputation is exactly what would have led us in.

### Why not Valhalla

Valhalla is the best open routing engine for this job and the worst fit for this platform. Its own
README says Windows support is incomplete, and **`prime_server` — required to build the HTTP
service — is not available for Windows**; the project's recommended way to run the service on
Windows is Docker. Docker on the truck tablet fails C1 outright: an install, a virtualisation
dependency, and a service.

Its core library and CLI tools *do* build on Windows via vcpkg, so a one-shot
route-per-invocation design is not impossible. That is a community-territory build for a worse
result than the option below, and it is recorded here only so the door is visibly shut rather than
unexamined.

### Why not OSRM

No official Windows binaries, and the preprocessing chain (extract → partition → customize) is as
Linux-shaped as the runtime. Same class of problem as Valhalla without Valhalla's instruction
quality.

### Why GraphHopper

Java, **officially supports Windows**, runs as a library or a standalone server, and emits
**turn-by-turn instructions** — which is precisely the part the alternatives make hard. Routing
geometry is the easy half; "turn left onto Highway 59 in 400 metres" is the half that makes this a
navigation feature rather than a moving map.

The JVM is the cost, and it is payable. `jlink` trims a runtime into the deploy folder, so nothing
is installed and C1 holds. The repo already ships a side executable beside the shell —
`IdHunter\IdHunter.exe` is in the publish summary — so a bundled process is an established pattern
here, not a new kind of thing.

### Why a child process rather than in-process

Running Java inside the WPF process (IKVM and friends) buys nothing and costs a great deal. A
localhost child process also keeps the engine **replaceable**: the seam is an HTTP call, so if
Valhalla's Windows story improves or a maintained .NET router appears, the swap touches one
adapter. That is the same reasoning as ADR-0003's transport split, and it has paid out once
already.

### Why PMTiles

One file, no tile server, designed for this. MBTiles would need a process answering per-tile
requests; PMTiles is read with range requests against a single archive.

## The two things to prove before the feature is written

This ADR picks the architecture. Two mechanisms underneath it are **unverified**, and both are
cheap to settle and expensive to get wrong late.

**Spike 1 — PMTiles in WebView2 with no server.** PMTiles reads via HTTP **range requests**.
Browsers will not hotlink a local file, so the documented answers are a local HTTP server or an
OPFS-backed plugin. WebView2's virtual-host-to-folder mapping is the intended route and would
avoid both — but whether it honours range requests is **not confirmed**: Microsoft's docs were
unreachable from the session that wrote this ADR, so nothing here should be read as verified.
*Fallback if it does not hold:* serve the archive from the same localhost process that already
fronts GraphHopper, which costs nothing extra because that process exists.

**Spike 2 — footprint and cold start.** Measure the `jlink`ed runtime size, the graph size for the
chosen extract, and the time from launch to first route. The dash is launched by hand every time
(C5), and a routing engine that needs thirty seconds to warm must warm in the background and say
so in the UI rather than appear broken.

## Consequences

- **The deploy stops being small.** A JVM, a tile archive and a routing graph are multi-GB
  together. `publish.ps1` gains steps, and a Release zip may exceed what a GitHub Release asset
  allows — so map data likely ships separately from the app, or is fetched once on first run.
  Either way the deploy notes change and rollback stops being "unzip the previous one" for the
  data half.
- **A refresh chore exists now.** An extract ages. The regeneration path has to be documented, or
  the map silently rots.
- **Attribution is a licence obligation.** OSM data is ODbL and must be credited on the map;
  MapLibre, PMTiles and GraphHopper licences all need confirming and adding to
  `THIRD-PARTY-NOTICES.md` before anything ships.
- **Guidance while moving is a distraction surface.** Voice-first, minimal touch, and no
  destination entry above a speed threshold — the same posture the warning popups took in
  ADR-0055.
- **Audio contends with projection.** Spoken directions share the Bluetooth sink with Android Auto
  and the Stereo card (Q14). Whoever lands first owes the ducking contract.
- **Build it in three stages, not one:** the map as a display-only occupant; then routing without
  voice; then voice and guidance. Each stage is useful alone, and the first one proves Spike 1
  before any of the routing work is committed.

## Opens

- **Q30** — does WebView2's virtual host mapping serve PMTiles range requests? (Spike 1)
- **Q31** — `jlink`ed runtime size, graph size and cold-start time. (Spike 2)
- **Q32** — extract scope: Manitoba alone, or the Prairies plus the border states you actually
  drive to? This decides gigabytes and the refresh cost.

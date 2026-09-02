# CLAUDE.md — DashDeck

Working memory for this repo. Read this first; it is kept current deliberately.

## What this project is

A Windows infotainment app for Jason's **2019 Ford F-150**, running on a **Surface Pro 7
(Intel, 912 x 1368 portrait)** mounted in **portrait**, carried in and out of the truck. It reads vehicle data
over an OBD-II adapter and presents it through **pluggable components**.

The point of the project is the **ecosystem**, not any one feature. Adding a capability
should mean one small project against a stable SDK plus a folder in `plugins/`. When a
design choice trades ecosystem quality against a single feature's convenience, the
ecosystem wins.

Start with [`docs/00-project-outline.md`](docs/00-project-outline.md).

## Current state

**P0 engine complete. P0.5 shell underway and running on Windows.**

Engine (`Abstractions`, `Vehicle`, `Core`, `Simulator`, `DebugConsole`) targets plain
`net10.0` and builds anywhere. Shell (`Abstractions.Wpf`, `Host`) targets `net10.0-windows`
(ADR-0010). **132 tests green** — 47 engine, 85 shell.

```bash
dotnet run --project src/DashDeck.Host              # the shell, on the synthetic truck
dotnet run --project src/DashDeck.DebugConsole -- cold-start-city --seconds 60
```

The shell renders the six-band layout, the status strip and the nav. The **stage** takes
occupants chosen from the launcher bar below it: a clock-and-weather face, a **compass** with
a G meter and vehicle pitch and roll, local video (LibVLC), and web applets in WebView2. The
compass reads truck-first and falls back to the tablet's own sensors, saying which (ADR-0016,
ADR-0017) — and anything measured against the mount refuses to render until it is levelled.
Below it, the **dash is a user-arranged list of cards**
(ADR-0015) loaded from `dashboard.json` — add, remove, reorder, resize, and pick each card's
signal, style, unit, rate and format. Cards flow into rows and rows into pages that snap
sideways; **only the visible page declares signals.** Settings and the card editor are
full-screen views that take all six bands and hide the stage, which keeps running (Q17).

Five traps already hit and worth not re-learning:

- **`InvariantGlobalization` breaks WPF.** `Directory.Build.props` sets it for the whole
  solution, which is right for the headless engine. WPF's font stack builds a
  `CultureInfo("en")` while measuring the first `TextBlock` and throws. Overridden to
  `false` in `Host` and `Host.Tests` only.
- **Don't rely on `WindowState="Maximized"`** for the borderless window. With no explicit
  bounds it takes its restore size from the fixed 912 × 1368 design surface and lands partly
  off-screen. `MainWindow` sets the work-area rectangle outright.
- **Theme brushes must be *replaced*, not mutated, and referenced with `DynamicResource`.**
  WPF freezes the `SolidColorBrush` instances in a compiled resource dictionary, so setting
  `.Color` on one is a silent no-op — and a `StaticResource` reference has captured the old
  instance anyway. Day and night rendered pixel-identical until this was measured.
  WPF's Fluent theme also brings its own `TextBox`/control chrome, so a control needs a
  template, not just a `Background`.
- **WPF applies a *layout clip* to anything wider than the space it is arranged in.** The
  paged card strip is 912 × *n* wide inside a 912 region; in any normal panel the pages past
  the first were laid out and then clipped away *before* the slide transform could move them,
  so sliding revealed blank canvas. It sits in a `Canvas`, which gives children their full
  desired size. Clip once, deliberately, at the outer edge.
- **`<Trigger Property="Tag" Value="True"/>` never fires.** `Tag` is typed `object`, so XAML
  has no target type to convert against and leaves the literal as the *string* `"True"`,
  which never equals a boxed `bool`. Use `<Trigger.Value><sys:Boolean>True</sys:Boolean>`.
  Cost: the current page's dot silently never lit.

Also worth knowing: `MeasuredRequestsPerSecond` — the `req/s` on the status strip — is the
adapter's measured **capability**, not the achieved load. It is not a way to check whether
something is consuming budget, and reading it as one is an easy mistake to make twice.

**Next: the component host** — discovery, manifest, lifecycle — so cards stop being built by
the shell and start arriving from `plugins/`. ADR-0015 settled the instance format and the
activation rule it will have to honour; what is missing is a *source* other than a catalog
signal.

Adapter chosen but not bought: **OBDLink EX**, wired USB (ADR-0007).

## Things that are easy to get wrong here

1. **No hardware exists yet.** The adapter is *chosen* (OBDLink EX over wired USB,
   ADR-0007) but not bought. Everything runs on the synthetic vehicle (ADR-0005). Do not
   write code that assumes a truck is attached, and do not defer work waiting for hardware.
2. **The Surface is a personal device.** No kiosk mode, no shell replacement, no services,
   no registry writes. Self-contained folder deploy, settings in `%LOCALAPPDATA%`. One
   Microsoft-signed FTDI USB serial driver is the single accepted exception (ADR-0007).
   This rules out solutions that would otherwise be obvious.
3. **Components never touch CAN.** They subscribe to named signals. If a component needs
   a PID, the signal catalog is missing a definition — fix it there, in config.
   **And ask the truck before the tablet** (ADR-0016). Where the vehicle knows something,
   that is the source; a device sensor is a *declared* fallback whose name is on screen, never
   a silent stand-in. Never guess a PID to fill the gap — a wrong heading is in range, so the
   catalog's own `min`/`max` guard cannot catch it.
4. **Nothing polls the adapter directly.** Components *declare* signals; the Request
   Arbiter builds one plan (ADR-0004). The old "~10–20 requests/second" figure was a
   *Bluetooth* limit and no longer applies now the link is USB — but **the real ceiling is
   unmeasured**, so do not assume headroom. The simulator keeps the conservative number
   until P1.5 measures a real one (Q12).
   Signals also declare a bus (`hs` / `ms`) and the arbiter interleaves across both.
5. **Read-only until Phase 3.** No writes to the vehicle. When they arrive they pass the
   five gates in ADR-0006 — all five, or it does not ship.
6. **The truck must work without us** (ADR-0006). SYNC 3 stays. Nothing here may degrade
   the vehicle when DashDeck is closed or absent.
7. **Signal quality is rendered, never hidden.** Every value carries `Live` / `Stale` /
   `Unavailable` / `Simulated`. A confidently wrong number on a dash is worse than a blank.
8. **`IClock` is injected.** Never `DateTime.Now` — it breaks replay and scripted-drive
   tests.

## Branching and versions

`main` is **the build on the truck tablet** — never commit directly to it, only tagged
merges from `develop`. `develop` is day-to-day work and may be broken. See
[`docs/05-releases-and-branching.md`](docs/05-releases-and-branching.md).

Three things version independently, and **must not be welded together** (ADR-0008): the
app (SemVer tags), `DashDeck.Abstractions` (`apiVersion`, bumped only when the component
contract changes), and each component. Host `v1.4.0` serving `apiVersion 1.0` is normal.

## Conventions

- **Stack:** .NET 10, C#, WPF (ADR-0001). Custom design system over WPF's Fluent theme.
- **Portrait-first.** Primary navigation lives in the bottom third — the only band
  reachable from the driver's seat.
- **Layering is strict.** Transport → adapter → catalog → arbiter → state bus → services →
  component host → shell. No layer reaches past its neighbour.
- **`DashDeck.Abstractions` stays small, stable and dependency-free.** It is the only
  assembly components reference, and it is shared across every load context.
- **The catalogs are data.** New signals are JSON, never a recompile — and so are the tablet
  sensors, in a second catalog whose `prefer` field names the vehicle signal that supersedes
  each one (ADR-0017).
- Never block the UI thread. All vehicle I/O is serialised on its own worker.

## Decisions

ADRs live in [`docs/decisions/`](docs/decisions/) and are immutable once accepted — a
changed decision gets a new ADR that supersedes the old one. Seventeen exist so far, covering
the UI stack, plugin model, transport split, request arbiter, mock-first development, the
additive/read-only posture, the widget/applet split, theming, the arranged dashboard and the
vehicle-first rule and sensor catalog for anything the tablet could also guess at.
**Read them before proposing an architectural change**;
several rejected alternatives were rejected for reasons that are not obvious from the
code.

## Working agreement

- Interview rather than assume. The scoping for this project was done by asking; keep
  doing that when a choice would change the shape of the work.
- Open questions go in [`docs/04-open-questions.md`](docs/04-open-questions.md) rather
  than being silently resolved.
- When a decision gets made, write the ADR in the same change that implements it.

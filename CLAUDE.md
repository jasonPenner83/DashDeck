# CLAUDE.md — DashDeck

Working memory for this repo. Read this first; it is kept current deliberately.

## What this project is

A Windows infotainment app for Jason's **2019 Ford F-150 (2.7 L EcoBoost)**, running on a **Surface Pro 7
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
(ADR-0010). **354 tests green** — 130 engine, 224 shell.

```bash
dotnet run --project src/DashDeck.Host              # the shell, on the synthetic truck
dotnet run --project src/DashDeck.DebugConsole -- cold-start-city --seconds 60
```

The shell renders the six-band layout, the status strip and the nav. The **stage** takes
occupants chosen from the launcher bar below it: a clock-and-weather face, a **compass** with
a G meter and vehicle pitch and roll, **phone projection** (ADR-0019), local video (LibVLC),
web applets in WebView2, and **native Windows apps** — left as real top-level windows, *owned*
by the shell and placed over the stage (ADR-0021, superseding ADR-0020's re-parenting), so they
keep their own focus, DPI and input. They report `Hosted` or `Outside` rather than pretending
it always works. The
compass reads truck-first and falls back to the tablet's own sensors, saying which (ADR-0016,
ADR-0017) — and anything measured against the mount refuses to render until it is levelled.
Below it, the **dash is a user-arranged list of cards**
(ADR-0015) loaded from `dashboard.json` — add, remove, reorder, resize, and pick each card's
value, style, unit, rate and format. A card names a **`source`** (B4): a vehicle `Signal`
from the catalog, or a tablet `Sensor` — heading, pitch, roll, G. Sensor cards cost no request
budget, and their footer says where they answered from rather than an allocated rate. The
**signal catalog now defines the standard OBD-II Mode 01 set** (~35, each `category`-tagged),
so the card editor's picker is **searchable and grouped by function**; the synthetic truck
answers the new PIDs so the extra options are live. Cards flow into rows and rows into pages
that snap sideways; **only the visible page declares signals.** Settings is **split into a rail
of sections** (Appearance, Mount, Display, Vehicle, Sensors, Apps, Diagnostics); it and the card
editor are full-screen views that take all six bands and hide the stage, which keeps running (Q17).
**Settings ▸ Sensors** (ADR-0032) lists every vehicle signal and tablet sensor with what the
truck has said about each — without ever declaring demand — **scans** the supported-PID bitmaps
to find what the truck supports and the catalog lacks (and the reverse), and **edits**
definitions with a **TEST** that asks the truck before saving. Edits go to a user overlay,
`%LOCALAPPDATA%\DashDeck\signals.user.json`, laid over the shipped catalog at the next launch
(RESTART NOW is in the section); the shipped files are never written on the tablet.
**Settings ▸ Vehicle** (ADR-0033) says *which* vehicle this is, so nothing hard-codes it: the
VIN is **read from the truck** (mode 09) or typed, **decoded once by NHTSA vPIC** and cached in
`%LOCALAPPDATA%\DashDeck\vehicle.json`, and every decoded field is correctable by hand. It fills
`VehicleProfile` (year, make, model, engine — `apiVersion 1.2`, **never the VIN**) and picks a
**vehicle signal pack** from `catalog/vehicles/` to lay over the standard set
(standard → pack → your overlay). The F-150 2.7 pack is empty until TEST confirms Ford PIDs.

**The stage is always four bands** (ADR-0018) — it used to vary and the cards below moved with
it, which on the road read as the dash rearranging itself. An occupant that wants less picture
gets all 708 of it, minus the 72 launcher bar. **Occupants hand back verbs, not chrome**
(ADR-0022): `IStageOccupant.Actions` returns captions and callbacks, and the three-dot menu
shows them above MODIFY WIDGETS and SETTINGS. The action bar that used to carry them cost a
quarter of the stage for two buttons. **Levelling lives in Settings** now — it is a
calibration, not a driving control. The status strip is a quick-info bar (weather, SIM badge,
clock); diagnostics moved to Settings. It **watches the adapter link** and shows an
`ADAPTER LOST — RECONNECTING` banner in the Stale amber when the transport is not connected —
sampled on the clock beat, not a worker-thread event — so the strip stops claiming a live
truck while the cards go Stale around it. **The link heals itself** (ADR-0034):
`AdapterLinkTransport` finds the baud rate again if the adapter resets, follows the adapter to a
new COM number (matched by its `ATI` identity; the phone-GPS port is never opened), and paces its
retries; `ElmAdapter` **re-configures the adapter after every reconnect**. A dash that started
simulated because the adapter wasn't there **goes live by itself when it answers**: a
`SwitchableTransport` swaps under the running pipeline, readings turn Live, and polling forgets
what the simulator taught it. One way only — once live, a lost cable is Stale, never simulated.
**Settings ▸ Vehicle lists tested ports** (identity, baud, voltage at the OBD port, or why not)
to choose the adapter from.

Six traps already hit and worth not re-learning:

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

- **Touch never reaches the mouse events on a manipulation-enabled element.** The card strip
  sets `IsManipulationEnabled` for swiping, which consumes a touch before WPF promotes it to a
  mouse click — so a `Button` on a card, and the old 600 ms hold before it, fire from a mouse
  and never from a finger. Both worked only in mouse-driven screenshots and were found on the
  truck. **The remedy now lives in the code:** `DashboardView.HandleTap` treats a manipulation
  that ends with almost no translation as a tap, hit-tests its origin, and runs the command of
  whatever `Button` is under it — the click the touch never became. So a new tap target on a
  card just needs to be a `Button` with a `Command`; do not add a `TouchDown` handler of its
  own. The `--tap-detail` flag drives that path without a touch screen. Still: if a gesture
  must work on glass, confirm it on glass — this trap was found there twice.

Also worth knowing: `MeasuredRequestsPerSecond` — the `req/s` on the status strip — is the
adapter's measured **capability**, not the achieved load. It is not a way to check whether
something is consuming budget, and reading it as one is an easy mistake to make twice.

Worth knowing before touching the stage: **an occupant lives as long as it is the stage
occupant** (ADR-0025, resolving F22). Leaving it — navigating below the stage, or opening a
full-screen view over it — keeps it running (the phone model, Q17/B2); *replacing* it with
another occupant ends it. The one gap that left — a full-screen view hiding a running occupant
with nothing on screen to say so — is closed by a **▶ NAME** pill in the always-visible status
strip that appears only then and taps back to the stage, not by changing the lifecycle.
**One exception to "replacing ends it"** (ADR-0026): some occupants are audio/video *sources*
(VIDEO, SPOTIFY, MUSIC, NUVIO, STREMIO, phone, and any user app marked so), and with the global
"keep stage audio" setting on, switching from a source to a silent occupant keeps the source
**alive and playing, hidden** behind it — the same ▶ pill brings it back. The stage holds up to
two occupants for this, each in its own content host so a live player is **never reparented**
(that would reload it and drop audio); the switch rule is the pure `StageSwitch.Decide`. Still
open one layer down: **where projected audio comes out at all** (F21).

**The component host works, widget and all** (ADR-0023). The in-process loader discovers
`plugins/`, validates the manifest and `apiVersion` before mapping any code, loads each
component into its own collectible `AssemblyLoadContext` sharing only `DashDeck.Abstractions`,
runs a guarded time-boxed lifecycle, and contains a faulting component. A component's
`IDashComponentView` **widget is hosted on the dash** as a `ComponentCardViewModel` — a third
`CardSpec.Source` (`Component`) beside Signal and Sensor, placed in the one ordered arrangement
and **activated by the visible page** (the ADR-0015 rule reaches a component through the new
`IDashCard` seam, so a component off-page spends no request budget). Tapping a component card
opens its **`IDashComponentView.CreateFullScreen` detail** as a full-screen view with a back bar
(Q17 — the stage keeps running underneath); the host draws that chrome, the component supplies
the content — and the card whose detail is open stays active so it keeps feeding it (the same
exemption the edited card gets). **Five components ship in `components/`**, all built through the
public SDK: `TripComputer` (integrates and draws distance), `FuelEconomy` (instantaneous L/100km
from two signals — the value a card can't derive), `TripEconomy` (trip-average economy,
persisted, with a full-screen detail and a reset), `Tpms` (per-wheel tyre pressure with an
overhead white F-150 that lights the low corner), and `RangeEstimator` (distance to empty — fuel
level over a recent economy it derives from fuel rate and speed, with a full-screen breakdown;
the assumed tank size is a documented constant until per-component settings land). **TPMS is the
first MS-CAN signal set** — its
four `tire.*.pressure` ids carry **placeholder mode/PID** (documented in the catalog) because
Ford's real body-module message is undiscovered (R2); the synthetic answers them on MS-CAN
flagged Simulated, and on a real truck without the PID they read Unavailable and every corner
shows a dash. Verify the loader alone with `--components <outfile>`
(`--components-dwell <seconds>` to let it run); `--detail <n>` opens a card's detail for a
screenshot. `publish.ps1` builds each component and ships `plugins/` beside the executable, the
same way it ships the catalog. **How to write one:** [`docs/writing-a-component.md`](docs/writing-a-component.md).
**Next (small):** reorder/remove/settings-edit of a component card via the editor UI,
`statusItem`, hot-reload, and permission enforcement.

One more trap, from building the loader: **the shared contract must not load twice.** A type is
identified by its assembly *and* its load context, so a component that carried its own copy of
`DashDeck.Abstractions` would implement a *different* `IDashComponent` and the cast that adopts
it would fail with an impossible "cannot convert IDashComponent to IDashComponent". The load
context defers the contract assemblies to the host's default context; the component's
`ProjectReference` sets `Private=false` so no copy is ever emitted beside its DLL.

The **OBDLink EX** adapter, wired USB (ADR-0007), was **ordered 2026-09-29, due 2026-10-05**
(genuine, sold by OBD Solutions). The **Carlinkit CPC200** for Android Auto and CarPlay
(ADR-0019) is chosen and not bought. Both are built against synthetic transports behind a
seam, so neither is blocking. First bring-up with the EX: a real `UsbSerialTransport`, then
measure the request ceiling (Q12) and what the Gateway Module passes on MS-CAN (Q5). Then
**Settings ▸ Sensors ▸ SCAN THE TRUCK** asks which standard PIDs it supports from the tablet,
and **TEST** is the loop for trying Ford mode 22 PIDs (R2, Q13) from the driver's seat.

## Things that are easy to get wrong here

1. **No hardware exists yet.** The adapter (OBDLink EX over wired USB, ADR-0007) is
   ordered, not in hand. Until bring-up, everything runs on the synthetic vehicle (ADR-0005). Do not
   write code that assumes a truck is attached, and do not defer work waiting for hardware.
2. **The Surface is a personal device.** No kiosk mode, no shell replacement, no services,
   no registry writes. Self-contained folder deploy, settings in `%LOCALAPPDATA%`.
   **Two** driver exceptions, each named by an ADR: the Microsoft-signed FTDI USB serial
   driver (ADR-0007) and a WinUSB binding for the Carlinkit dongle (ADR-0019). Two is not
   "a few" — a third needs its own ADR, or the constraint erodes by accident.
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
merges from `develop`. `develop` is day-to-day work and testing, and may be broken. Both are
protected: work goes on a `feature/*` or `fix/*` branch and reaches `develop` by PR with CI
green (`.github/workflows/ci.yml`, Windows). See
[`docs/05-releases-and-branching.md`](docs/05-releases-and-branching.md) and
[`CONTRIBUTING.md`](CONTRIBUTING.md).

**The repo is public (MIT).** Commit as the GitHub noreply address, never a personal email.
Never commit an asset without the right to publish it — the TPMS overhead photo was an
AI-generated image and was purged from history; `components/Tpms/truck.png` is git-ignored
and the component draws its vector truck without it. No secrets, VINs or home coordinates.

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
changed decision gets a new ADR that supersedes the old one. Thirty-four exist so far, covering
the UI stack, plugin model, transport split, request arbiter, mock-first development, the
additive/read-only posture, the widget/applet split, theming, the arranged dashboard and the
vehicle-first rule and sensor catalog for anything the tablet could also guess at, the
fixed-height stage that came out of the first drive, phone projection through a dongle, native
apps owned and placed over the stage rather than re-parented into it, user-added app launchers
from the UI, the rule that an occupant lives as long as it is the stage occupant, a
persistent audio/video source that keeps playing behind a silent occupant, and the phone's GPS
as a third source (`PHONE`) behind a transport seam — over Bluetooth by default (an in-box
virtual COM port, no driver) or the network, and a `VehicleProfile` on the component context
(fuel tank size, set in Settings ▸ Vehicle) that made the first additive `apiVersion` bump to 1.1,
and finding and defining signals from Settings ▸ Sensors — a supported-PID scan, a TEST, and a
user overlay catalog that the shipped one never absorbs by accident, and which vehicle this is —
a VIN read from the truck or typed, decoded once and cached, filling `VehicleProfile`
(`apiVersion 1.2`) and choosing a vehicle signal pack, and an adapter link that heals itself —
re-configured after reconnects, rate and port found again — with a simulated start that goes
live when the adapter answers, chosen from a list of tested ports.
**Read them before proposing an architectural change**;
several rejected alternatives were rejected for reasons that are not obvious from the
code.

## Working agreement

- Interview rather than assume. The scoping for this project was done by asking; keep
  doing that when a choice would change the shape of the work.
- Open questions go in [`docs/04-open-questions.md`](docs/04-open-questions.md) rather
  than being silently resolved.
- When a decision gets made, write the ADR in the same change that implements it.






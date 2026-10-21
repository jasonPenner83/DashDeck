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

**P0 engine complete. P0.5 shell running on Windows — and on the truck.** `v0.3.0` (2026-10-02) is
the first release that reads the real F-150; `main` is what is on the tablet.

Engine (`Abstractions`, `Vehicle`, `Core`, `Simulator`, `DebugConsole`) targets plain
`net10.0` and builds anywhere. Shell (`Abstractions.Wpf`, `Host`) targets `net10.0-windows`
(ADR-0010). **533 engine tests green** — `DashDeck.Tests` builds and runs anywhere, which is why
pure logic belongs in Core; `DashDeck.Host.Tests` needs the WindowsDesktop runtime and is CI's
(`windows-latest`).

```bash
dotnet run --project src/DashDeck.Host              # the shell, on the synthetic truck
dotnet run --project src/DashDeck.DebugConsole -- cold-start-city --seconds 60
```

The shell renders the six-band layout, the status strip and the nav. The **stage** takes
occupants chosen from the launcher bar below it: a clock-and-weather face, a **compass** with
a G meter and vehicle pitch and roll, **phone projection** (ADR-0019, ADR-0057), local video (LibVLC),
web applets in WebView2, and **native Windows apps** — left as real top-level windows, *owned*
by the shell and placed over the stage (ADR-0021, superseding ADR-0020's re-parenting), so they
keep their own focus, DPI and input. They report `Hosted` or `Outside` rather than pretending
it always works. The
compass reads truck-first and falls back to the tablet's own sensors, saying which (ADR-0016,
ADR-0017) — and anything measured against the mount refuses to render until it is levelled.
Below it, **DASH shows a console** (ADR-0041) — a layout file
like CLIMATE: speed, rpm, fuel, temperature, range, nine warning lights, odometer and economy — and
**the cards are a stage occupant, `CARDS`**, five rows a page, asking for nothing while another
occupant is on the stage. The **cards are a user-arranged list**
(ADR-0015) loaded from `dashboard.json` — add, remove, reorder, resize, and pick each card's
value, style, unit, rate and format. A card names a **`source`** (B4): a vehicle `Signal`
from the catalog, a tablet `Sensor` — heading, pitch, roll, G, the phone's GPS, and the navigation
numbers (ADR-0066) — or a `Readout`, which is words rather than a number (ADR-0067). Sensor cards
cost no request
budget, and their footer says where they answered from rather than an allocated rate. The
**signal catalog now defines the standard OBD-II Mode 01 set** (~35, each `category`-tagged),
so the card editor's picker is **searchable and grouped by function**; the synthetic truck
answers the new PIDs so the extra options are live. Cards flow into rows and rows into pages
that snap sideways; **only the visible page declares signals.** Settings is **split into a rail
of sections** (Appearance, Themes, Mount, Display, Vehicle, Sensors, Apps, Diagnostics); it and the card
editor are full-screen views that take all six bands and hide the stage, which keeps running (Q17).
**Settings ▸ Sensors** (ADR-0032) lists every vehicle signal and tablet sensor with what the
truck has said about each — without ever declaring demand — **scans** the supported-PID bitmaps
to find what the truck supports and the catalog lacks (and the reverse), and **edits**
definitions with a **TEST** that asks the truck before saving. Edits go to a user overlay,
`%LOCALAPPDATA%\DashDeck\signals.user.json`, laid over the shipped catalog at the next launch
(RESTART NOW is in the section); the shipped files are never written on the tablet.
Its **MODULES** block (ADR-0035) finds the rest of the truck the way FORScan does: the broadcast
(`7DF`) only reaches the engine computer, so it asks **every module address** (`700`–`7F7`, 8s bit
clear) on both buses for its part number (`22 F113`), then sweeps a chosen module's
**identifiers** a range at a time — reads only, refused while moving. A request and a catalog
signal can now **name a module** (`"module": "726"`); `ElmAdapter` sets `ATSH`/`ATCRA`/flow control
only when the module changes and restores the broadcast after. The parser reads negative
responses as `Rejected` with a code, and takes the first of several broadcast answers rather than
gluing them into garbage. Module names come from the vehicle pack and are shown as *likely*.
Scans and sweeps of the **real** truck are **kept** in `%LOCALAPPDATA%\DashDeck\discovery.json` — the
last module scan and the latest sweep of each module and range — and shown again at launch marked
**SAVED**; the synthetic truck's are never saved. The file can hold the VIN (7E0's F190), so it stays
on the tablet.
**WATCH** re-asks the identifiers a sweep found, round and round until STOP, and ranks them by how
often they changed **since the watch's own first pass** — never against the sweep, which may be old
(the first version did, and every row read MOVED ×1) (`IdentifierWatch`): leave it 30 s, then do one
thing to the truck — blip the throttle, let it warm — and what moves with it rises to the top. Beside
each, the likely temperature: one byte less 40, or two bytes over 16 (Ford's finer ones); a two-byte
value with its top bit set is shown signed. Every watch is **recorded as it runs** to
`%LOCALAPPDATA%\DashDeck\watch\watch-<module>-<range>-<time>.csv` — a line per pass with engine rpm,
each identifier as one unsigned number — to lay beside a FORScan log; OPEN FOLDER after STOP. It is
a sweep that does not end, so it is refused while moving and stops by itself if the truck moves.
**Settings ▸ Vehicle** (ADR-0033) says *which* vehicle this is, so nothing hard-codes it: the
VIN is **read from the truck** (mode 09) or typed, **decoded once by NHTSA vPIC** and cached in
`%LOCALAPPDATA%\DashDeck\vehicle.json`, and every decoded field is correctable by hand. It fills
`VehicleProfile` (year, make, model, engine — `apiVersion 1.2`, **never the VIN**) and picks a
**vehicle signal pack** to lay over the standard set (standard → pack → your overlay). **Packs are the
user's own files** in `%LOCALAPPDATA%\DashDeck\vehicles\` (ADR-0052); `catalog/vehicles/` ships empty.

**Nothing vehicle-specific is shipped** (ADR-0052, [`DISCLAIMER.md`](DISCLAIMER.md)): to manage
liability, code and the shipped catalog hold only what public standards define, and placeholders. The
SAE J1979 mode 01 table and the ISO module names, identity question (`F187`) and identification range
(`F100`–`F1FF`) are files in `catalog/reference/`, read by `ObdReference`; a vehicle file adds
`identityDid` (Jason's: Ford's `F113`), `identifierRanges` and module names over them (`With`).
**Placeholders have no identifier at all** — no bus, mode or PID; `HasRequest` is false, the
arbiter plans them at no cost, and `VehicleService` never asks: Unavailable on a real truck, the
synthetic truck's value by id (`SimulatedValues`) while simulated. **The synthetic truck answers from
the catalog**: whatever request a signal lives at, encoded by its own decode from the truck's
quantity of the same name (`SimulatedF150.Value`/`Reading`), plus the SAE reference for standard
PIDs the catalog lacks; its invented modules (7E0, 7E1, 710, 740, 750; 7A0, 7A4, 7B0 on pins 3/11),
their identifiers, broadcast frames and VIN are `catalog/simulator/synthetic-truck.json`
(`SyntheticTruckData`). Change a signal's request in a file and the simulator follows. The
tools read speed, rpm and coolant **by signal name** through the catalog (`SignalProbe`), never by PID
number. The ID hunter's shipped `targets.json` names only `7E0`/`7E1`; the user's own copy in
`%LOCALAPPDATA%\DashDeck\idhunter\` wins. Only the protocol's own fixed numbers stay in code (`7DF`,
`7E8`, `700`–`7F7`, service `22`, the bitmap PIDs, mode 09). **Never put a manufacturer's identifier in a
PR** — Jason's F-150 file lives on his tablet and laptop.

**Themes are files of named tokens** (ADR-0036), modelled on Home Assistant's: 26 tokens —
surface and text ramps, accent and how solidly a selection is filled, captions, strip, nav and
button colours, button/card/panel radii, border width, the two fonts — each defaulting to the
DashDeck look (the built-in theme is pixel-identical to before). Shipped themes sit in
`catalog/themes/` with any font files they carry; the user's in `%LOCALAPPDATA%\DashDeck\themes\`.
**Settings ▸ Themes** wears one on a tap, IMPORTs, EXPORTs, SAVEs AS, DELETEs yours, and RELOADs
after a hand edit; [`docs/writing-a-theme.md`](docs/writing-a-theme.md) is the reference. Night is
derived by dimming; a bad value costs one token and a warning, never the dash. **The quality
colours are not tokens**, and a theme's accent passes the same check a hand-picked one does.
*LCARS (inspired)*, lettered in Antonio (OFL), came first; it is now an extra the user owns (ADR-0043). **Fonts, radii and border width are
`DynamicResource` now, like colours** — a view that reaches `UiFont` by `StaticResource` will not
follow the theme.

**Night dims the backlight too** (ADR-0068). DashDeck sets the Surface's own backlight through
Windows' in-box WMI classes (`System.Management`), because a dark layer cannot reach the map,
video, Android Auto or hosted apps (ADR-0021). A day and a night level live in Settings ▸ DISPLAY ▸
BRIGHTNESS and follow `ThemeService.IsNight`, so when the headlights decide night (F10) both
follow, and the console dimmer will become the night level in `BrightnessPolicy.Target`. **The
backlight is borrowed:** the launch level goes back on exit unless somebody moved it since, the
loan is in `settings.json` so a crash is undone next launch, and nothing is written unless the
level wanted changes. The rules are in `DashDeck.Core.Brightness`; only `WmiBacklight` needs glass.

**The GAUGES stage is a file** (ADR-0037): a *stage layout* is JSON of elements — gauges (`dial`,
`arc`, `bar`, `lcarsBar`, `digital`, each tuned by `parts`), `text`, `clock` and `panel` — placed
in pixels on a fixed 912 × 636 canvas scaled to the stage. A gauge's source is a signal, optionally
minus another, scaled (boost = manifold − barometric, in psi). **A gauge with no reading draws no
needle and says NO DATA**, Stale draws dimmed, and a corner dot carries the fixed quality colour.
The old six-dial cluster is the compiled-in built-in layout; `catalog/stage/` ships more and the
user's live in `%LOCALAPPDATA%\DashDeck\stage\`. **A theme names its stage**
(`"stageLayout": "lcars"`) and the stage follows the theme unless one is chosen in **Settings ▸
Themes ▸ STAGE LAYOUT**; a user file with a shipped one's name wins. Every launch writes the shipped
themes and layouts — and the built-in ones, as JSON — to `themes\examples\` and `stage\examples\`
beside the user's, as references to copy from; they are never loaded. Reference:
[`docs/writing-a-stage-layout.md`](docs/writing-a-stage-layout.md).

**The launcher is a file too** (ADR-0038, superseding ADR-0024's "built-ins stay in code"):
`%LOCALAPPDATA%\DashDeck\launcher.json` lists every stage option in order — `gauges` (optionally
pinned to a `layout`, so TOWING can have its own button), `clock`, `compass`, `phone`, `video`, `web`
(`url`, per-page `zoom`) and `app` (`paths`, most likely first, `arguments`) — each with `detail`,
`group`, `hidden` and `keepPlaying`; plus `quickBar` (up to five names for the bar below the stage;
whatever is showing still always gets a button) and `startOn`. A `userApps` marker places the apps
from Settings ▸ Apps. **Your file replaces the built-in list outright** — order is the point; the
built-in list is compiled in and written to `launcher.example.json`. An entry's name is the stage's
name (`NamedOccupant` wraps an occupant whose own name differs, because the button is matched to
the stage by name). A bad entry is left out and named; a bad file leaves the built-in list.
**Settings ▸ Apps ▸ STAGE LAUNCHER** shows it, with RELOAD, MAKE IT MINE and OPEN FOLDER.
Reference: [`docs/writing-a-launcher.md`](docs/writing-a-launcher.md).

**COMPASS is a stage layout too** (ADR-0039). A gauge's `source` can be a **`sensor`** from the
sensor catalog (`attitude.pitch`, `motion.lateralG`, `location.latitude`…) instead of a signal —
read through `SensorService`, truck first, with the source (`TRUCK`, `TABLET`, `NOT LEVELLED`) drawn
along the gauge's bottom edge. Two new elements: **`compass`** (a rose, `mode` `rose` or `needle`) and
**`gMeter`** (no levelled mount, no ball; RESET PEAK G appears in the three-dot menu on any stage with
one). The old screen is the compiled-in **`compass`** layout; the launcher's `compass` entry shows it,
and a `compass.json` of the user's in `stage\` replaces it. `CompassView`/`CompassViewModel`/
`CompassStageOccupant` are gone; the arithmetic lives in `SensorMath`.

**CLIMATE is a layout too** (ADR-0040) — read only, in place of the cards. A layout is drawn on a
`LayoutCanvas`: the stage's 912 × 636 or the **climate panel's 912 × 390** (the two card bands), with
the stage's engine. Four new elements work on either: **`setpoint`** (a set temperature on a thin
glowing arc), **`levels`** (steps lit to the value; below zero in `negativeColour`, for a seat that
heats and cools — `positiveText`/`negativeText` make it read HEAT 2 / COOL 1), **`indicator`** (a pill lit by a `bit`, `equals` or `onAt`; no reading is dimmed
with a dash, never "off") and **`glass`** (painted frost). The built-in is **Modern** (ADR-0042/0043; the Glass
theme's frosted panel ships as `catalog/climate/glass.json`); a theme names its own
(`"climateLayout"`); the user's live in
`%LOCALAPPDATA%\DashDeck\climate\`, chosen in **Settings ▸ Themes ▸ CLIMATE LAYOUT** (the stage's
block, one template). The panel is **made when CLIMATE is chosen and disposed when left**, so it
declares signals only while visible. Its thirteen `hvac.*` / `seat.*.climate` / `steeringWheel.heat`
signals are
**placeholders** (like TPMS — no request at all, ADR-0052): Simulated on the synthetic truck,
**Unavailable on the real one** until the HVAC module is found. **Nothing is sent** — control is
Phase 3 (ADR-0006).

**DASH is a console, and the cards are on the stage** (ADR-0041). The console is a layout on a
**912 × 390** canvas (`LayoutCanvas.Console`), made when DASH is chosen; the built-in is **Modern**
(ADR-0042/0043; the Glass theme's arcs console ships as `catalog/console/glass.json`), a theme names
its own (`"consoleLayout"`), yours live in
`%LOCALAPPDATA%\DashDeck\console\` (Settings ▸ Themes ▸ CONSOLE LAYOUT). A **`warning`** element
draws one of nine original icons (path data, `WarningIcons`) lit by `bit`/`below`/`equals`/`onAt`.
Real lights: check engine (`diagnostics.checkEngine`, PID 01 bit 7 — **decodes can `mask`**, so
`diagnostics.dtcCount` comes from the same byte), low fuel, hot coolant, low voltage; oil, seatbelt,
door, brake and tyres are `warning.*` **placeholders**, and so are the economy and range figures,
`fuel.economy` and `fuel.range` — the cluster shows both, so they are likely its identifiers to find. `vehicle.odometer` is PID `A6`. **CARDS** (`"type": "cards"`) shows the one `DashboardViewModel`; `IsShown` is false
while it is off the stage, so no card declares; MODIFY WIDGETS puts CARDS on the stage first; a
launcher file that never mentions `cards` gets it at the end of the grid. The card editor and
component details can open over CLIMATE now, so both panels hide beneath them.

**MAPS is a destination with three sizes** (ADR-0062) — and, for now, a placeholder in them. It is
the first destination that does not own exactly one region: `Panel` is the bottom third beside the
stage, `Full` is everything between the status strip and the nav (it reports as a full-screen view,
so Q17's existing rule collapses the stage and the one region fills all six bands), and `Stage`
hands the map to the stage as an occupant through the same `SetStage` path the launcher uses.
The placement is a value — `DashDeck.Core.Maps.MapView`, with `Resized()`, `SentToTheStage()` and
`BroughtBack()` — so the rule that matters (a map sent up from full screen comes back to full
screen) is tested off Windows, and the shell only turns a placement into bands. **The size control
is a second press of the nav's MAPS cell**, not a chip in the panel (ADR-0062's addendum): the nav
strip is the only band reachable from the driver's seat and the cell was already under the thumb.
MAPS is therefore the one cell that does something when you are already on it — swap the size, or,
with the map on the stage, bring it back. The panel carries the hint that says so, and the hint is
tested, because a gesture nothing announces is a control nobody finds. Coming off the
stage is **reconciled after every stage change** rather than reported from the occupant's
`Dispose`, which would re-enter `SetStage`. The map is deliberately **not** in the stage picker
until it can be a `map` launcher entry (ADR-0038), and the built-in launcher's `MAPS` web page is
now **`OSM`**, since the online OpenStreetMap page is a different thing from the offline map.
**And there is a map in it now** (ADR-0063): **MapLibre GL JS**, vendored into the deploy
(`src/DashDeck.Host/Map/web/`, 6.12.0, BSD-3-Clause), rendering **OpenFreeMap** vector tiles
fetched as you pan — no API key, no request limit, their `dark` style, `--map-style <https url>` to
swap it. **Never OSM's own tile servers**, whose usage policy forbids precisely this. The page runs
in a **second WebView2** served from a virtual host (`https://dashdeck.map/index.html`, mapped to
the deploy's `map/` folder), because an ES module and a web worker both refuse a `file://` origin,
and it is driven over a string bridge — `window.dash.position/camera/follow` out, `map:ready` /
`map:error:` / `map:camera:` / `map:free` back, parsed by `MapMessage` in Core. `MapCamera` lives
in Core too, so where the map is looking **survives the rebuild a placement change causes** (Q43),
along with invariant-culture formatting and clamping — a `NaN` across that bridge draws a blank
canvas with no error. The map follows the vehicle until it is panned and then offers **RECENTRE**;
on the stage both that and BRING THE MAP BACK are menu actions, since the panel's chips are hidden
there. **Nothing invents a centre** — no fix, no map, and the region says which of the three
reasons it is (GPS off, no fix yet, tiles did not load). The map and the placeholder never overlap,
because a WebView2 is a child window and WPF cannot draw over it (ADR-0021): the dash draws the
three no-page states and **the loading state lives inside the page**. What is still ahead is
**Q44** — the offline half, a caching tile proxy on localhost seeded from a PMTiles cut of
OpenFreeMap's own planet, so both halves end up the same data and the same look.

**The map wears the theme** (ADR-0064). Ten `map*` colour tokens sit in the same vocabulary as
every other, and **each defaults to one that already exists** — the ground follows `canvas`, the
roads and names follow the text ramp, the vehicle follows `accent` — so a theme that says nothing
about maps still gets a map that matches it, and night comes free through the same dimming factors
(surfaces ×0.55, ink ×0.74). They carry no resource key, since they paint no WPF; `ThemeService`
skips those and the map reads them off the resolved theme instead. DashDeck draws **its own
cartography** from them: `MapStyle.Build` in Core, **eleven layers**, deliberately far simpler than
OpenFreeMap's two hundred because a dash is read at a glance and not at a desk — and pure, so the
JSON, the source-layer names, the colour routing and the paint order are all tested without
Windows. The style is rebuilt and pushed on every applied palette, so a map on screen turns with
the dash. **An LCARS map is two tokens** (`mapWater`, `mapGreen`): lavender roads and peach
motorways already fall out of its text ramp. `--map-style` still wins, and then the theme supplies
only the marker. A style that will not parse is never handed to the renderer. Labels are Noto Sans
whatever the theme's fonts say — **Q45**.

**Buttons on the map, three bases, track up and range lines** (ADR-0071). The map's own
functions are **drawn by the page** (nothing WPF draws can sit over a WebView2) and **decide
nothing**: SEARCH, ROAD/SATELLITE/TERRAIN, NORTH UP/TRACK UP and the range button at the top right,
RECENTRE low left after a pan, each press sent as `map:press:<name>` and parsed by name against
`MapControl`; the view-model decides and answers with `window.dash.buttons(…)`. The choices are
`MapChoices` in Core, survive a rebuild, and are saved (`mapBase`, `mapOrientation`, `mapRange`).
All three bases come from `MapStyle.Build(palette, base)` so the theme's roads and names stay on
each — satellite is **EOX Sentinel-2 cloudless 2020** (no key, non-commercial, 10 m), terrain is
hill-shading from **AWS Terrain Tiles**. Track up turns to a trusted course (above 5 km/h) with the
truck in the lower third. **Range lines** read `fuel.range` (asked for only while drawn): dashed
rings at 80 km from empty (orange) and at empty (red), and on a route the stretch past those points
dashed orange then red, measured along the route and refused off it — `RangeLines` and
`Geo.Destination` in Core. Fixed colours, grey when stale. A rebuilt map is dressed again on
`map:ready`, and the page carries its own lines across a restyle with `transformStyle`.

**Address search, and the dash's own keyboard** (ADR-0065). SEARCH on the map region opens a
full-screen search — it **covers** the map rather than overlaying it, because a WebView2 is a child
window and WPF cannot draw a keyboard over one (ADR-0021), and five rows of keys do not fit the
bottom third anyway, so it takes the map to its larger placement first. The geocoder is
**Nominatim** under its acceptable-use policy, and all five of the policy's requirements are code
rather than good intentions: **no auto-complete** (it is forbidden outright, so typing never
searches — a SEARCH button is the only thing that asks), at most one request a second, results
cached by normalised query, a User-Agent naming DashDeck and its repository, and a `viewbox` bias
towards the truck that is a preference rather than a fence. **DashDeck draws its own keyboard** —
one page, no shift, a digits row first — because nothing in the dash had ever needed typing in the
cab and Windows' touch keyboard over a borderless window may simply not appear; `KeyboardLayout` is
data in Core, and a test about row proportions rejected the first two-page design outright. **A
destination cannot be typed above 5 km/h** (ADR-0058's distraction rule, reusing the compass'
threshold), read on the beat so stopping unlocks it. Choosing an address moves the map, pins it and
stops following. Recents are session-only and search needs signal — both are **Q46**.

**The keyboard is the Surface's own**, after a drive (ADR-0065's addendum). DashDeck drew one first
and it is deleted rather than parked: the in-box keyboard is the one the tablet's owner knows and
it brings holding a key, the clipboard and prediction with it. The field is focused and `TabTip`
nudged when the search opens, failing silently — a USB keyboard still types into it. **One
guarantee moved with it:** the gate used to refuse keystrokes, and now the field's `IsEnabled`
follows it while the view-model guarantees only that **no request leaves while moving**, which is
the safety-relevant half.

**A place says what it is, and you can get directions to it.** A result carries what OSM knows —
kind, hours, telephone, website — at **no extra request**, because `extratags` is an output option
on the search already being made. It shows in a **MapLibre popup inside the page**, themed, because
WPF cannot draw over a child window (ADR-0021) — and built with `textContent` from
`JsonSerializer`'d values, since a place name comes from anybody. Its button draws a route from
**FOSSGIS's public OSRM**: distance, time, a line on the map and a turn list behind a chip, with
the instruction wording ours (OSRM sends a manoeuvre type and a street name, no sentence) and
tested across ten manoeuvres. **Routes are never cached** where searches are — a route depends on
where the truck is now. **This is the display half, not navigation:** no voice, no re-routing,
nothing that follows the truck. That is ADR-0058's bundled GraphHopper, still ahead, and following
a vehicle over a public router would breach its terms as well.

**The map's numbers are sensors** (ADR-0066). Six rows in the device catalog under a new
`Navigation` source, behind a third `IDeviceSensors` provider (`NavigationSensors`) composed by
`CompositeDeviceSensors`: a straight-line distance and bearing to the chosen place, the route's
total distance and time, and the distance and time still to drive. So a card or a stage layout
shows *Distance Left* the way it shows pitch, with nothing changed in either. The arithmetic is in
Core — `Geo` (haversine and initial bearing) and `RouteProgress` (nearest point on the route's
geometry, then everything after it) — and tested off Windows. **Every value carries the fix's own
quality**, so a synthetic track produces blue navigation numbers, and the route *totals* are the
router's answer about the road rather than a measurement, so they are Live and need no position.
**More than 150 m off the line the countdown refuses** (`NAV · OFF ROUTE`) instead of shrinking
towards a place being driven away from; `prefer` is named only on the two rows the truck's own
navigation could really supersede. The remaining time is the router's duration pro-rated, never a
fresh estimate. **Q47 is the known limit:** nearest-point snapping can pick the wrong end of a
route that comes back near itself.

**Words on a card** (ADR-0067). The third source `CardValue.cs` has predicted since B4:
`CardSource.Readout`, with seven entries for what a searched place knows that is not a number — its
name, address, kind, opening hours, telephone, website, and the route's summary. It arrives as a
**second interface, `ITextValue`**, so `ICardValue.Value` stays a `double` and every face, unit
conversion, range and threshold is untouched; exactly one place asks which kind it has, the card's
own `Text`, ahead of the NaN guard that would otherwise draw every readout as a dash. The
`ReadoutCatalog` is **declared in code** rather than JSON — alone among the three catalogs —
because a readout is a fact the dash already holds rather than something a user can add, and each
definition is a **function**, so a destination chosen after the cards were built reaches them with
no wiring. Words get their own text block (18px, wrapped to two lines, then ellipsis); no unit, no
range, never a bar, never Stale. **Cards only** — a stage layout's gauge reads through
`SensorService`, which a readout is not in.

**No strings in a sensor reading, and that is a decision rather than a type problem** (ADR-0066).
Adding `string? Text` to `SensorReading` would break nothing — but the numeric contract is
`ICardValue.Value`, and every face, `DisplayUnits`, `ValueChoice.HasRange` and the thresholds are
built on that `double`; and **NaN is the dash's "nothing"**, which a string has no equivalent of.
ADR-0056 already settled how the dash says words — a number plus a `states` table — and that works
because the set is closed when the catalog loads, which a destination name never is. So text gets a
**third `CardSource`**, which is ADR-0067 above rather than a wider sensor reading.

**The synthetic track drives a real loop now.** `SyntheticLocationSource` used to weave inside a
sixty-metre box while reporting 60 km/h and an unrelated course; it circles at a true 60 km/h on an
800 m radius (five kilometres a lap), so the speed it reports is the speed it travels and anything
doing arithmetic on the position gets numbers that move. `--gps synthetic` is how the navigation
readouts are tested at a desk.

**Two looks, Modern and Glass; LCARS is yours** (ADR-0043). **Modern** is the built-in theme
(`builtin/modern`: cool greys, accent `#5AC8FA`, Segoe UI Variable) and names the Modern stage
(`catalog/stage/modern.json`), console and climate panel; **Glass** ships (`catalog/themes/glass.json`)
and names the Glass console and climate panel and the F-150 cluster. Every screen follows the theme
unless a layout is picked by hand. **LCARS is an extra** in `catalog/extras/lcars/`, copied **once**
into the user's folders by `ExtrasInstaller` (recorded as `installedExtras` in settings), so it is
YOURS — editable, deletable, and deleted stays deleted; a stored `shipped/x` choice finds `yours/x`.
The build and `publish.ps1` clear the output `catalog` first, so a moved file never lingers twice.

**The defaults are type over shapes** (ADR-0042, named *Clean* then, *Modern* now): on black, no panels, arcs or pills; a small
caption over a large light number aligned to its edge, units smaller than numbers, switches as words
that light, warning icons near-invisible until lit; text colours are theme tokens so they dim at
night. Layouts can now name their own **`fonts`** (`{ "ui", "mono" }`, set as canvas resources that
shadow the theme's `UiFont`/`MonoFont` for that layout only) and use `valueWeight`/`labelWeight`,
digital `align`/`labelPosition: "above"`/`unitSize`/`noData`, and indicator `style: "text"`.

**The ID hunter is a separate program** (ADR-0044): `src/DashDeck.IdHunter`, `IdHunter.exe` in
`IdHunter\` beside the dash (DashDeck must be closed — one program holds the adapter). A terminal guide
through a checklist (`targets.json`) of things to find, three ways: **listen** (hold the truck in
alternating states — door shut/open — while the bus is heard; `BroadcastRanker` keeps fields steady
within each step and different between states), **follow** (sweep a module, watch its identifiers
beside coolant or rpm while the person makes it change; `FollowRanker` by correlation, with a scaling
hint) and **match** (numbers read off the cluster; `MatchRanker`/`MatchTally`). **M** on the menu
matches a number named on the spot (ADR-0069) with open-ended readings. **The hunter no longer checks
whether the truck is moving** — any step, any speed; each M reading's speed is recorded instead. The
dash's own Settings ▸ Sensors sweeps still refuse while moving (ADR-0035), to keep the gauges live. Each ends in a live
check and a row in `findings.csv` with the verdict, plus captures (which can hold the VIN — never in
the repo). **Listening is new to the vehicle layer**: `IStreamingTransport.StreamAsync` carries the
adapter's monitor (`STMA`/`ATMA`), and `CanMonitor` sets it up **silent (`ATCSM1`)**, headers on, CAN
formatting off; whoever owns the `ElmAdapter` must `InitializeAsync` again after. `--simulate` runs it
against the synthetic truck, whose `SimulatedCabin` switches and broadcast frames use **invented**
identifiers. A value found by listening cannot be shown on the dash yet — signals are requested, not
heard. **D** checks a **CAN database** (`.dbc`, `CanDatabase`) the person keeps on the tablet: which
of its messages the truck sends, then picked signals decoded live to confirm (ADR-0045). **A database
is a lead, never committed** — the one found so far is for the 2021+ F-150 (P702) and unlicensed;
only our own confirmed measurements reach the vehicle pack. Reference: [`docs/id-hunter.md`](docs/id-hunter.md).

**The serial tap records FORScan** (ADR-0048): `src/DashDeck.SerialTap`, `SerialTap.exe` in
`SerialTap\` beside the dash. Serial port monitors need filter drivers and kept failing, so the tap
**holds the adapter's COM port itself and offers it on TCP** (`127.0.0.1:35000`); FORScan connects as
to a Wi-Fi adapter, and `TapBridge` relays every byte both ways while `TapRecorder` writes
timestamped `>>` command and `<<` answer lines to `%LOCALAPPDATA%\DashDeck\tap\tap-<time>.log`. It
sends nothing of its own. The logs can hold the VIN — never in the repo. Reference:
[`docs/serial-tap.md`](docs/serial-tap.md).

**The ID matcher is where DashDeck's identifiers are managed** (ADR-0050): `src/DashDeck.IdMatcher`,
`IdMatcher.exe` in `IdMatcher\` — a **desktop** WPF app for the laptop, keyboard and mouse (the owner's
rule: find IDs the traditional way, keep touch for the dash). The tap is built in (FORScan on
`127.0.0.1:35000`, every session saved as a tap log; saved logs reopen). **Left:** DashDeck's signals,
standard → pack → overlay as the dash reads them, with **NEEDS ID** for `"placeholder": true` (new
catalog field, on the 30 stand-ins (the five multi-state ones the latest) — **a new stand-in must carry it**) and **NOT ON TRUCK** for a
standard PID the engine computer's own supported-PID answers (heard in the traffic) say it lacks.
**Middle:** every identifier FORScan asks (`TrafficReader` follows `ATSH`, `ATTP6`/`STP53`, `STPX`, the
count digit). **Right:** match live — type what FORScan shows, `ScalingFitter` tries Ford's usual steps
and offsets in metric (°F/psi/mph converted) and fits a line once the raw value moves — or import the
PID log CSV (`ForscanCsv`, `LogMatcher`, R²). **Switches** (On/Off, Open/Closed — no unit) are matched
to a **bit**, not a line: `ScalingFitter.FromStates` keeps the bits that agree with every typed state,
`FromBinarySeries` scores a two-valued log column (≥ 97% agreement), and the decode carries a `mask`. Pairing keeps the DashDeck signal's id, name, range and
rate, takes the truck's module/mode/PID/decode in the signal's unit (`SignalPairing.Pair`), and saves
to **`signals.user.json`** or exports **pack entries**; TEST still confirms before a pack commit. The
tap code lives in `DashDeck.Vehicle.Tap` now, shared with SerialTap. **Signals are added, edited,
hidden and removed there too** (ADR-0051, `SignalWorkbench`): built-ins are **hidden** (`"hidden": true`
in the overlay — out of the card picker via `ValueChoice.Offered`, still working for anything using
them), never deleted; REMOVE takes away only your entry (yours removed, a correction reverted); a
definition **typed by hand** is `"unconfirmed": true` — not offered in the picker until the tablet's
TEST answers exactly that request and it is saved from Settings ▸ Sensors. Changing a signal's source
or decode makes it unconfirmed; renaming does not. **LISTEN TO THE BUS** (ADR-0054, `BusWatch`) hears
the bus silently, exclusive with the tap: LEARN NOISE marks bytes that move on their own, MARK (F5)
shows what changed since in [brackets], STATE A/B (F6/F7) and RANK (F8) narrow it with the hunter's
`BroadcastRanker`. Reference:
[`docs/id-matcher.md`](docs/id-matcher.md).

**Warning lights pop up, and codes can be cleared** (ADR-0055) — **the first write, and the only
one.** `catalog/warnings.json` lists each warning (signal and console-style condition, severity
`stop`/`caution`, `popup` default, `holdSeconds`, `whenMoving`, `countSignal`, `codes`);
`WarningMonitor` debounces on the shell's beat, counts only Live/Simulated readings (no answer is
never "off"), and keeps a dismissal — across launches, in `settings.json` — until the light is seen
off and comes back or the stored-code count rises. Default popups: check engine, oil, hot coolant, low
voltage, brake (while moving). **Moving: a strip over the status strip** (one word, DISMISS — in the
strip's row because occupants' child windows draw over the stage); **stopped: a full-screen window**
with advice and the codes, the occupant collapsed beneath it and still running. Codes are modes 03/07
(`PidRequest.Service` — no PID), asked of each emissions module **by address**; descriptions from
`catalog/reference/dtc-descriptions.json` (groups only, plus a few standard codes). **Clearing (mode
04) goes only through `VehicleActions`**, all five ADR-0006 gates: shell-only (components' `Actions`
stays null, `apiVersion` unchanged), **ALLOW CLEARING CODES off by default**, an interlock read fresh
(speed 0, rpm 0, engine computer answering), a 2-second `HoldButton` used once within 10 s, and
`%LOCALAPPDATA%\DashDeck\actions.log` — **no log line, no send**. At a desk: `--fault P0420`,
`P0171/pending`, `warning.oilPressure@20-60`, `engine.coolantTemp=118`, `engine.rpm=0` (engine off).
**Multi-state signals** (ADR-0056): a catalog signal may carry **`states`** (`{ value, name }`, the
decoded value — a masked field is not shifted); a value with no name shows as `?value`, never the
nearest state. Cards show the name; layouts have a **`selector`** element (every state in a row, the
current lit — names from the running catalog via `SignalStates.Of`); the Modern console shows the
gear selector and 4WD at the bottom centre. Placeholders: `drivetrain.4wdMode`,
`transmission.gearSelector`, `vehicle.driveMode`, `body.wipers`, `body.headlights`. In the **ID
matcher** any typed word that is not a number or on/off is a state; `ScalingFitter.FromNamedStates`
keeps the narrowest bit fields that read the same every time a state was typed and differently between
states — go round every state twice. The tablet's signal editor now **carries mask and states**
through TEST and save (it used to drop the mask).

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

Nine traps already hit and worth not re-learning:

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

- **Never wait on async work from the UI thread without `ConfigureAwait(false)` all the way
  down.** `App.OnExit` used to block on `VehicleStack.DisposeAsync()`, whose awaits came back to
  the dispatcher — which was the thread blocked waiting. The window closed, the process never did,
  and it kept the serial port. Disposal now runs on the pool with a time limit, and a background
  backstop ends the process 10 s after exit begins whatever is stuck. On the truck, **CLOSE
  DASHDECK** in the three-dot menu (two taps) is the only way out: there is no Escape key.

- **A second tap on the icon made a second dash.** DashDeck takes a few seconds to find the
  adapter before its window appears, so on a touch screen the first tap looks like it did nothing.
  The second copy could not open the serial port and ran on no truck. `SingleInstance` now holds a
  `Local\` named mutex from launch until the vehicle stack has closed the port; a newcomer brings the
  running dash forward, or waits (up to 20 s, `InstanceGate`) for one that is starting or closing.
  RESTART NOW releases it before starting its successor.

- **A `ScrollViewer` ignores a finger unless `PanningMode` is set.** Every page of Settings and the
  card editor shipped without it: they scrolled with a mouse wheel at a desk and not at all on the
  tablet, where the sensor list could not be reached past the first screen. Each now says
  `PanningMode="VerticalOnly"`, the window swallows `ManipulationBoundaryFeedback` (so a list dragged
  past its end does not drag the whole dash after it), and `TouchScrollingTests` fails any new
  `ScrollViewer` without it.

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
the tank size comes from `VehicleProfile`, set in Settings ▸ Vehicle, with 136 L as the fallback). **TPMS is the
first MS-CAN signal set** — its
four `tire.*.pressure` ids are **placeholders** because Ford's real body-module message is
undiscovered (R2); the synthetic truck answers them by name, flagged Simulated, and on a real truck
they read Unavailable and every corner shows a dash. Verify the loader alone with `--components <outfile>`
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

**The hardware is in hand and on the truck.** The **OBDLink EX** (STN2232 v5.12.4), wired USB
through its FTDI virtual COM port (ADR-0007), was brought up on 2026-10-01 and runs through
`SerialPortTransport` wrapped in `AdapterLinkTransport` (ADR-0031, ADR-0034). What bring-up
measured:

- **~19 requests/second** for the whole app — mean 52.5 ms round trip, p95 71.4 ms (Q12). The
  vehicle's response time dominates, so USB bought far less than hoped; risk R1 stands, and
  smooth high-rate gauges need request batching, not a faster link. The simulator now runs at
  the same 52 ms (`SyntheticFaults.Realistic`).
- **Fast requests** (ADR-0049) are the first answer to R1: the serial tap timed FORScan at ~20 ms a
  request on the same truck, asking mode 01 **filtered to the engine computer (`ATCRA7E8`) with a
  count of one (`010C1`)** so the adapter stops waiting for other modules. `ElmAdapter.FastRequests`
  does the same for mode 01 to the broadcast on HS, bitmaps excepted (`IsFastEligible`); NO DATA or a
  lone busy reply is asked again the old way, and a value that answers only the old way three times
  running goes slow for good (one dropped answer once sent rpm slow and made the filter thrash).
  The claimed ceiling rises to 45; the arbiter still plans on measured time. Settings ▸ Vehicle ▸
  FAST REQUESTS, on by default, at launch; the simulator measures 47 req/s against 19. **Unmeasured
  in the cab (Q22).**
- **MS-CAN is reachable from the OBD port** — the adapter accepts the switch and the Gateway
  Module does not get in the way (Q5). **But "MS" is a misnomer on this truck (Q21):** a silent listen on
  pins 3/11 heard nothing at 125 kbit/s and traffic at **500 kbit/s** — a second high-speed bus, not
  MS-CAN. DashDeck's requests there went out at 125 kbit/s and made error frames on it until the F-150
  pack gained `"pins311BitRate": 500000` (now in Jason's own vehicle file); `VehicleStack` sets
  `ElmAdapter.Pins311BitRate` from the user's vehicle file, and the adapter sends `STPBR` after `STP53`.
  **Never send on pins 3/11 at a rate nobody measured** — and now the code enforces it: the rate is
  nullable, **null sends nothing there** (NO DATA), and a real vehicle with no file giving the rate gets
  null, including on a simulated start that goes live (`AdapterFailover.GoingLive`). `CanBus.Ms` still
  means "pins 3/11", whatever the rate.
- **The truck answers 48 standard PIDs but neither fuel rate (`5E`) nor MAF (`10`)** (Q4). So
  **Fuel Economy, Avg Economy and Range Estimator read blank on the real truck** — they need
  `engine.fuelRate`. The fix is decided, not built: speed-density from MAP, IAT, RPM and lambda,
  with tank calibration made mandatory (ADR-0030) — built once and **parked** (branch
  `feature/speed-density-fuel`) in favour of finding Ford's own fuel-flow identifier. The console's
  economy and range are **placeholders** (`fuel.economy`, `fuel.range`) until then. Oil temperature
  (`5C`) is absent too.
- **TPMS reads a dash at every corner** — its PIDs are still placeholders.

Ford's own values (transmission and oil temperature, fuel flow, TPMS — R2, Q27) are the next
hardware-side work, and the tools are on the tablet: **SCAN FOR MODULES** finds the modules,
a module's **identifier sweep** finds candidates, and **TEST** in the editor confirms one while
the thing it measures changes (ADR-0032, ADR-0035). Confirmed values go in the vehicle pack.

**Android Auto is on the stage through a Carlinkit CPC200-CCPA** (ADR-0019, ADR-0057) — being bought,
**not yet seen working** (Q25). `UsbDongleTransport` reaches it by WinUSB P/Invoke (SetupAPI finds
`1314:1520`/`1521`, the device key is *read* for its interface GUID, bulk pipes from its own
descriptor), bound once by hand with Zadig — the second driver exception. `DongleSetup` sends the
community driver's whole setup in its order, ending with `/etc/android_work_mode` = 1 (without it a
CarPlay-first dongle never offers Android Auto), then asks for the last phone a second later.
`DongleFrameReader` reassembles reads and resyncs. H.264 goes through an `H264Pipe` into LibVLC with
**memory callbacks into a `WriteableBitmap`** — the picture is in the visual tree, never a child
window — and touch is mapped to the picture *as drawn*. PCM goes to **Windows' default output**
(`ProjectionAudio`, `waveOut`, one player per format; F21 resolved) — DashDeck never picks the
truck's audio. No dongle: the screen says why and looks again every 5 s; `--synthetic-dongle` at a
desk. **Android Auto always needs the phone**; Android Automotive is a separate experiment (Q26), and
the Pi Zero 2 W stays giving SYNC 3 wireless Android Auto. Setup: [`docs/phone-projection.md`](docs/phone-projection.md).

## Things that are easy to get wrong here

1. **The truck is not always attached.** The adapter is real now, but the tablet is carried in
   and out, and development happens at a desk. The synthetic vehicle (ADR-0005) is still the
   default whenever the adapter is absent, and the dash goes live by itself when it answers
   (ADR-0034). Do not write code that assumes a truck is attached, and do not defer work waiting
   for one — but anything that touches the vehicle, the adapter or the cab screen is not done
   until it has been walked through in the truck (see *Working agreement*).
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
   Arbiter builds one plan (ADR-0004). **The measured ceiling is ~19 requests/second for the
   whole app** (Q12), shared by every card and component — there is no headroom to assume.
   Settings sweeps (ADR-0032, ADR-0035) go through the same serialised adapter and take most of
   it while they run, which is why they are refused while moving.
   Signals also declare a bus (`hs` / `ms`) and the arbiter interleaves across both.
5. **Read-only, with one gated exception.** The only write is clearing trouble codes (ADR-0055),
   and it goes through `VehicleActions` and nowhere else. Any further write passes the five gates
   in ADR-0006 — all five, or it does not ship — and gets its own ADR.
6. **The truck must work without us** (ADR-0006). SYNC 3 stays. Nothing here may degrade
   the vehicle when DashDeck is closed or absent.
7. **Signal quality is rendered, never hidden.** Every value carries `Live` / `Stale` /
   `Unavailable` / `Simulated`. A confidently wrong number on a dash is worse than a blank.
8. **`IClock` is injected.** Never `DateTime.Now` — it breaks replay and scripted-drive
   tests.
9. **Ship nothing vehicle-specific** (ADR-0052). No manufacturer's identifier, module address, bus
   rate or scaling in code or the shipped catalog — the user's vehicle file holds those. Standards'
   tables are files in `catalog/reference/`; tools ask signals by name.

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
changed decision gets a new ADR that supersedes the old one. Sixty-five exist so far, covering
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
live when the adapter answers, chosen from a list of tested ports, and asking modules by
address — a module sweep, an identifier sweep, and signals that name their module, and
themes as files of named tokens chosen in Settings, with the quality colours still out of reach,
and the stage as a file of gauges, text, clock and panels that a theme can bring with it, and
the launcher as a file — every stage option, web page and program, in order, and the quick bar, and
the compass as layout elements — sensor-sourced gauges, a rose and a G meter, and a read-only
climate panel drawn from a layout file in place of the cards, and DASH as a console layout with
warning lights while the cards move onto the stage, and clean, typographic defaults for both, and two default themes, Modern and Glass, with LCARS made
the user's own, and a separate guided ID hunter that listens silently, follows and matches, and CAN databases as leads checked on the truck, never shipped, and a scroll strip for programs
that ignore touch — tried and withdrawn (ADR-0046, superseded by ADR-0047): a hosted program gets the
whole stage, and a serial tap that records FORScan by standing between it and the adapter, and fast
requests asked FORScan's way — filtered to the engine computer, with a count of one, and the
ID matcher, a desktop program where DashDeck's identifiers are managed against FORScan — signals
added, edited and hidden there, typed ones held unconfirmed until TEST, and nothing vehicle-specific
shipped — standards' tables as files, vehicle files the user's own, pins 3/11 silent until measured —
and placeholders with no identifier at all, the synthetic truck answering from the catalog, and free
listening there — noise learned, a MARK, states A and B ranked — and warning lights that pop up
(a strip while moving, a window when stopped, dismissed until they relight), with clearing the codes
as the first write through the five gates, and signals with named states — 4WD, gear, drive mode —
shown as a row with the current one lit and matched by typing each state, and Android Auto through
the dongle — WinUSB by P/Invoke, the picture decoded into the visual tree, the sound on Windows'
default output, and updating from the dash with nothing automatic between the three buttons, and
MAPS as a destination with three placements — the bottom third, all six bands, or handed to the
stage — and the map itself: MapLibre vendored into the deploy over OpenFreeMap tiles fetched as you
drive, in a second WebView2 on a virtual host, with the camera held outside the page so a resize
does not lose your place.
**Read them before proposing an architectural change**;
several rejected alternatives were rejected for reasons that are not obvious from the
code.

## Working agreement

- Interview rather than assume. The scoping for this project was done by asking; keep
  doing that when a choice would change the shape of the work.
- Open questions go in [`docs/04-open-questions.md`](docs/04-open-questions.md) rather
  than being silently resolved.
- When a decision gets made, write the ADR in the same change that implements it.
- **Every new feature comes with an in-vehicle test walkthrough, and Jason gets walked through
  it.** Anything that touches the vehicle, the adapter, or the screen in the cab is not done
  when the tests pass on the synthetic truck. Add a section to
  [`docs/08-in-vehicle-testing.md`](docs/08-in-vehicle-testing.md) in the same change, and copy
  it into the PR under *How to test in the truck*. It gives what is needed (ignition off, on or
  running; internet; parked), numbered steps with what Jason should **expect to see**, and what a
  failure looks like. When reporting the work, walk him through it step by step — not "untested
  on hardware", but how to test it.






# Architecture Decision Records

One file per decision. Numbered, immutable once accepted — a decision that changes gets a
new ADR that supersedes the old one, rather than an edit. The point is to preserve *why*,
including the reasoning that later turns out to be wrong.

| ADR | Decision | Status |
|-----|----------|--------|
| [0001](ADR-0001-ui-stack.md) | WPF for the shell (runtime version superseded by 0009) | Accepted |
| [0002](ADR-0002-plugin-model.md) | In-process plugins from a `plugins/` folder | Accepted |
| [0003](ADR-0003-transport-abstraction.md) | Transport and adapter are separate layers | Accepted |
| [0004](ADR-0004-request-arbiter.md) | Components declare signals; an arbiter schedules them | Accepted |
| [0005](ADR-0005-mock-first.md) | Synthetic vehicle is the primary data source until hardware exists | Accepted |
| [0006](ADR-0006-additive-not-replacement.md) | DashDeck is additive to SYNC 3, and read-only before Phase 3 | Accepted |
| [0007](ADR-0007-usb-link-and-adapter.md) | Wired USB link, OBDLink EX adapter | Accepted |
| [0008](ADR-0008-branching-and-versioning.md) | `main`/`develop` branching, three independent version streams | Accepted |
| [0009](ADR-0009-target-net10.md) | Target .NET 10 LTS (supersedes the version in 0001) | Accepted |
| [0010](ADR-0010-abstractions-has-no-ui-dependency.md) | `DashDeck.Abstractions` carries no UI dependency | Accepted |
| [0011](ADR-0011-view-contract-and-mvvm.md) | Component = widget + optional full-screen; MVVM in the shell only | Accepted |
| [0012](ADR-0012-widgets-and-applets.md) | Declarative widgets and sandboxed web applets for third parties (supersedes 0002 in part) | Accepted |
| [0013](ADR-0013-theming.md) | Bounded theming: day/night, one curated accent, quality colours fixed | Accepted; "three things" superseded by 0036 |
| [0014](ADR-0014-custom-accents.md) | Custom accents, allowed by validation rather than curation (supersedes one clause of 0013); settings persist in `%LOCALAPPDATA%` | Accepted |
| [0015](ADR-0015-arranged-dashboard.md) | The dash is a user-arranged list of cards, flowed into pages; only the visible page declares signals | Accepted |
| [0016](ADR-0016-vehicle-first-heading.md) | Vehicle data first, device sensors as a declared and visible fallback | Accepted |
| [0017](ADR-0017-sensor-catalog.md) | A second catalog for tablet sensors, with a required mount reference | Accepted |
| [0018](ADR-0018-fixed-stage-and-overflow-menu.md) | Stage fixed at four bands with an optional action bar; overflow menu replaces a hold gesture (supersedes 0015 in part) | Accepted |
| [0019](ADR-0019-phone-projection.md) | Android Auto and CarPlay through a Carlinkit dongle, not a head-unit implementation (amends 0007 with a second driver exception) | Accepted |
| [0020](ADR-0020-native-app-occupants.md) | Native applications on the stage, adopted into it where that works, launched alongside where it does not | Accepted |
| [0021](ADR-0021-owned-not-reparented.md) | Native app windows are **owned and placed**, not re-parented (supersedes 0020's mechanism) | Accepted |
| [0022](ADR-0022-actions-not-a-bar.md) | Occupant controls are verbs in the overflow menu, not a band of the stage (supersedes 0018's action bar) | Accepted |
| [0023](ADR-0023-component-host.md) | In-process component host: manifest + `apiVersion` validation, isolated load context, guarded lifecycle; a component's widget lives on the dash | Accepted |
| [0024](ADR-0024-user-app-launchers.md) | User-added stage apps from the UI, persisted to `apps.json`; built-ins stay in code; still a launcher, not an extension mechanism | Accepted (built-ins in code superseded by 0038) |
| [0025](ADR-0025-occupant-lifetime.md) | An occupant lives as long as it is the stage occupant — leaving keeps it, replacing ends it; a now-playing return in the status strip (resolves F22) | Accepted |
| [0026](ADR-0026-persistent-source.md) | A persistent audio/video source: with a global toggle on, switching to a silent occupant keeps the source playing hidden behind it (amends 0025) | Accepted |
| [0027](ADR-0027-phone-location.md) | The phone's GPS as a third source (`PHONE`): heading/speed/position over NMEA behind a transport seam, truck-first, no driver (extends 0016/0017) | Accepted |
| [0028](ADR-0028-bluetooth-gps.md) | Bluetooth as the default GPS transport — NMEA over the paired phone's in-box virtual COM port; no driver, so C1's limit is untouched (extends 0027) | Accepted |
| [0030](ADR-0030-fuel-flow-without-maf.md) | Fuel flow by speed-density; tank calibration becomes mandatory | Accepted |
| [0029](ADR-0029-vehicle-profile.md) | A `VehicleProfile` on the component context (fuel tank size, more later), set in a Settings ▸ Vehicle section — the first additive `apiVersion` bump, 1.0 → 1.1 | Accepted |
| [0031](ADR-0031-live-adapter-in-the-host.md) | The Host can run on the real adapter, falling back to the simulator | Accepted |
| [0032](ADR-0032-signal-discovery-and-user-catalog.md) | A Settings ▸ Sensors section: an inventory of every signal and sensor, a supported-PID scan that finds missing ones, and an editor with TEST that writes a user overlay catalog in `%LOCALAPPDATA%` (applied at next launch) | Accepted |
| [0033](ADR-0033-vin-lookup-and-vehicle-packs.md) | Which vehicle this is: the VIN read from the truck or typed, decoded once by NHTSA vPIC and cached; year/make/model/engine on `VehicleProfile` (`apiVersion` 1.2, never the VIN); vehicle signal packs in `catalog/vehicles/` matched to it | Accepted |
| [0034](ADR-0034-resilient-adapter-link.md) | A self-healing adapter link (rate and port found again, adapter re-configured after a reconnect, paced retries); a simulated start goes live when the adapter answers, without a restart; Settings ▸ Vehicle lists tested ports to choose from (amends 0031) | Accepted |
| [0035](ADR-0035-module-discovery.md) | Asking modules by address: requests and catalog signals can name a module (`"module": "726"`); Settings ▸ Sensors sweeps every address on both buses for modules and a module's identifiers a range at a time, reads only, parked; module names come from vehicle packs | Accepted |
| [0036](ADR-0036-token-themes.md) | Themes as files of named tokens (colours, fonts, radii) like Home Assistant's, chosen, imported, exported and reloaded in Settings ▸ Themes; night derived by dimming; quality colours still fixed and accents still checked; *LCARS (inspired)* ships first (supersedes ADR-0013's "three things") | Accepted |
| [0037](ADR-0037-stage-layouts.md) | The stage as a JSON file of elements — gauges (dial, arc, bar, LCARS bar, digital, each tunable), text, clock and panels — positioned on a 912 × 636 canvas; a theme names its stage; honest gauges (no reading, no needle); chosen and edited from Settings ▸ Themes ▸ STAGE LAYOUT | Accepted |
| [0038](ADR-0038-stage-launcher-file.md) | The stage launcher as a JSON file: every option (gauges with a pinned layout, clock, compass, phone, video, web pages with zoom, programs with candidate paths), its order and headings, the five-button quick bar and the opening stage; replaces the built-in list outright, never empties it; Settings ▸ Apps ▸ STAGE LAUNCHER (supersedes 0024's "built-ins stay in code") | Accepted |
| [0039](ADR-0039-compass-as-layout-elements.md) | The compass as stage layout elements: any gauge can read a sensor (truck first, source named under it); new `compass` (rose or needle) and `gMeter` elements; COMPASS is the built-in `compass` layout, replaced by a `compass.json` of yours | Accepted |
| [0040](ADR-0040-climate-panel.md) | A read-only climate panel in place of the cards when CLIMATE is chosen, drawn from a layout file on a 912 × 390 canvas with the stage's engine; new `setpoint`, `levels`, `indicator` and `glass` elements; built-in *Glass*, a theme names its own (`climateLayout`); `hvac.*` signals are placeholders until the HVAC module is found; nothing is sent | Accepted |
| [0041](ADR-0041-console-dash.md) | DASH is a console drawn from a layout file (912 × 390; built-in *Modern*, LCARS ships one, Settings ▸ Themes ▸ CONSOLE LAYOUT); a `warning` element with nine original icons; check engine, code count and odometer from standard PIDs, other lights as placeholders; decodes can mask; the cards become the CARDS stage occupant, five rows, asking nothing while off the stage (amends 0015) | Accepted |
| [0042](ADR-0042-clean-defaults.md) | *Clean* replaces Glass and Modern as the built-in climate panel and console — type over shapes, BMW-like; Glass and Modern ship as files; layouts can name their own `fonts`, weights, aligned captions over numbers, smaller units and text-only indicators | Accepted |
| [0043](ADR-0043-modern-and-glass-themes.md) | Two default themes — *Modern* (built-in) and *Glass* (shipped) — each naming its stage, console and climate layouts, so every screen follows the theme unless overridden; LCARS becomes an extra copied once into the user's folders; the output catalog is cleared each build | Accepted |
| [0044](ADR-0044-id-hunter.md) | The ID hunter: a separate terminal guide that finds identifiers by listening (silent monitor, ranked against held states), following (correlated with coolant or rpm) and matching (numbers read off the cluster), with a live check and a findings CSV; read-only, simulated at a desk | Accepted |
| [0045](ADR-0045-can-databases-as-leads.md) | A CAN database (`.dbc`) is a lead: the ID hunter reads one kept on the tablet, shows which of its messages the truck sends, and decodes picked signals live for the person to confirm; no database is ever committed — only our own confirmed measurements reach the vehicle pack | Accepted |

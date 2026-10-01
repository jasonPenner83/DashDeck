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
| [0013](ADR-0013-theming.md) | Bounded theming: day/night, one curated accent, quality colours fixed | Accepted |
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
| [0024](ADR-0024-user-app-launchers.md) | User-added stage apps from the UI, persisted to `apps.json`; built-ins stay in code; still a launcher, not an extension mechanism | Accepted |
| [0025](ADR-0025-occupant-lifetime.md) | An occupant lives as long as it is the stage occupant — leaving keeps it, replacing ends it; a now-playing return in the status strip (resolves F22) | Accepted |
| [0026](ADR-0026-persistent-source.md) | A persistent audio/video source: with a global toggle on, switching to a silent occupant keeps the source playing hidden behind it (amends 0025) | Accepted |
| [0027](ADR-0027-phone-location.md) | The phone's GPS as a third source (`PHONE`): heading/speed/position over NMEA behind a transport seam, truck-first, no driver (extends 0016/0017) | Accepted |
| [0028](ADR-0028-bluetooth-gps.md) | Bluetooth as the default GPS transport — NMEA over the paired phone's in-box virtual COM port; no driver, so C1's limit is untouched (extends 0027) | Accepted |
| [0030](ADR-0030-fuel-flow-without-maf.md) | Fuel flow by speed-density; tank calibration becomes mandatory | Accepted |
| [0029](ADR-0029-vehicle-profile.md) | A `VehicleProfile` on the component context (fuel tank size, more later), set in a Settings ▸ Vehicle section — the first additive `apiVersion` bump, 1.0 → 1.1 | Accepted |
| [0031](ADR-0031-live-adapter-in-the-host.md) | The Host can run on the real adapter, falling back to the simulator | Accepted |
| [0032](ADR-0032-signal-discovery-and-user-catalog.md) | A Settings ▸ Sensors section: an inventory of every signal and sensor, a supported-PID scan that finds missing ones, and an editor with TEST that writes a user overlay catalog in `%LOCALAPPDATA%` (applied at next launch) | Accepted |

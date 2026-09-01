# DashDeck — Project Outline

> Status: **outline / pre-code**. Nothing here is built yet. This document is the
> agreed shape of the project, derived from the scoping interview on 2026-09-01.

## 1. What this is

DashDeck is a Windows infotainment application for a **2019 Ford F-150**, running on a
**Surface Pro 9 (Intel)** that is carried into the truck and mounted in portrait
orientation. It reads live data from the truck's CAN networks over an OBD-II adapter and
presents it through a set of **pluggable components**.

The defining goal is not any single feature. It is the **ecosystem**: adding a new
capability to the dash should mean writing one small project against a stable SDK and
dropping a folder into `plugins/`. Everything else in the architecture exists to make
that true.

## 2. Hard constraints

These came out of the interview and are not up for renegotiation without an ADR.

| # | Constraint | Consequence |
|---|---|---|
| C1 | The Surface Pro 9 is a **personal device**, swapped in and out of the truck | No kiosk mode, no shell replacement, no services, no registry work. Self-contained folder deploy; settings in `%LOCALAPPDATA%`; uninstall = delete the folder. *Amended 2026-09-01:* one Microsoft-signed FTDI USB serial driver is accepted, in exchange for a wired link (ADR-0007). Nothing more invasive than that. |
| C2 | **No OBD-II adapter has been purchased yet**, and the app will spend most of its life not connected to a truck anyway | The synthetic vehicle is not test scaffolding — for now it *is* the data source. Every layer above the transport must be buildable, demoable and shippable against mock data. Real hardware is a later swap of the bottom layer, nothing more. |
| C3 | **SYNC 3 stays.** DashDeck is an additional screen | Phase 1 is purely additive. The truck remains 100% usable if DashDeck is closed, crashed, or left at home. No takeover of audio, factory camera, or climate. |
| C4 | **Portrait** orientation | Layout system is portrait-first. Primary navigation lives in the bottom third — the only zone reachable from the driver's seat without leaning. |
| C5 | Launched **manually**, no ignition integration | No power-state machine in Phase 1. But: connect/disconnect, sleep/resume, and adapter loss must all be non-events that recover on their own. |
| C6 | Read-only now; **safe actions later** (locks, lights, windows) | The write path is designed for from day one but physically absent from the binary until Phase 3, and then gated (see §7). |

## 3. Non-goals

Naming these now prevents scope drift later.

- Replacing or emulating SYNC 3.
- Anything safety-critical, ADAS-adjacent, or that the driver could come to rely on.
- Module reflashing, tuning, or FORScan-style configuration writes.
- Public distribution or a component marketplace. The SDK is built as if for others,
  because that discipline produces a better architecture — but the audience is one truck.
- Cross-platform. Windows-only (see ADR-0001).

## 4. Architecture in one picture

```
┌──────────────────────────────────────────────────────────────────┐
│  Shell (WPF)   portrait nav · tile grid · status strip · settings │
├──────────────────────────────────────────────────────────────────┤
│  Component Host   discovery · manifest · lifecycle · permissions  │
│     ┌────────────┐ ┌────────────┐ ┌────────────┐                  │
│     │ TripComputer│ │  Gauges    │ │  <yours>   │  ← plugins/     │
│     └────────────┘ └────────────┘ └────────────┘                  │
├──────────────────────────────────────────────────────────────────┤
│  Services   storage · trips · settings · logging · navigation     │
├──────────────────────────────────────────────────────────────────┤
│  Vehicle State Bus    named typed signals · pub/sub · coalescing  │
├──────────────────────────────────────────────────────────────────┤
│  Request Arbiter      one polling plan for all subscribers        │
├──────────────────────────────────────────────────────────────────┤
│  Signal Catalog       declarative PID/frame → signal decoding     │
├──────────────────────────────────────────────────────────────────┤
│  Vehicle Adapter      ELM327/STN command protocol, HS/MS-CAN      │
├──────────────────────────────────────────────────────────────────┤
│  Transport   Bluetooth SPP │ USB serial │ Replay │ Synthetic      │
└──────────────────────────────────────────────────────────────────┘
```

Two rules give the whole design its shape:

1. **No component ever touches CAN.** Components subscribe to *named signals*
   (`vehicle.speed`, `engine.rpm`) and are completely unaware of PIDs, adapters, or
   transports. Swapping a Bluetooth ELM adapter for a raw CAN interface later changes
   two layers and zero components.
2. **The adapter is a single serialized resource, so nobody polls it directly.**
   Components *declare* the signals they need and the rate they want. The Request
   Arbiter merges every declaration into one deduplicated polling plan. Without this,
   five components polling independently would starve each other — see §8, R1.

Full detail: [`01-architecture.md`](01-architecture.md).

## 5. The component model

A component is a folder under `plugins/<id>/` containing a `component.json` manifest and
one or more assemblies. It implements `IDashComponent` from `DashDeck.Abstractions` —
the single contract assembly, and the only thing a component author references.

A component can contribute any combination of four surfaces:

- **Tile** — a live card on the portrait home grid.
- **Screen** — a full-screen view opened from its tile.
- **Status item** — a small indicator in the top strip.
- **Background worker** — no UI at all (a logger, an exporter, a Home Assistant bridge).

Loading is in-process via a collectible `AssemblyLoadContext` per component (ADR-0002),
which buys hot-reload during development. The honest cost is that a badly behaved
component *can* take down the dash; §7 covers the containment measures that make that
unlikely rather than impossible.

Full contract: [`03-component-sdk.md`](03-component-sdk.md).

## 5a. Mock-first development

Until an adapter is bought, DashDeck runs entirely on a **synthetic vehicle**: a
simulated 2019 F-150 that produces the same named signals, at the same rates, with the
same jitter, dropouts and warm-up behaviour as the real thing. It is selected exactly
like any other transport, so nothing above it knows the difference.

This is a design constraint, not a convenience:

- The synthetic vehicle must model *realistic* behaviour — a cold start that warms up,
  fuel economy that responds to throttle and speed, occasional dropped responses, and a
  polling ceiling that mimics R1's Bluetooth throughput limit. A simulator that is too
  well-behaved produces components that break on contact with the truck.
- It ships **scripted drives** (cold-start city loop, highway cruise, towing pull, idle)
  so a component can be exercised against repeatable scenarios.
- Once an adapter exists, the same infrastructure replays **recorded** drives from the
  truck. Synthetic drives are then progressively replaced by real captures, and the
  scripted ones stay on as regression fixtures.

The practical effect: P0 and P1 can be built and finished start to finish with no
hardware. Buying an adapter becomes a bring-up milestone, not a prerequisite.

## 6. First vertical slice — Trip Computer

The proving-ground component is a **fuel economy and trip computer with history** —
the thing the factory cluster computes and then forgets.

It was chosen over live gauges deliberately: it exercises CAN read, the signal catalog,
the state bus, *and* the persistence and charting layers. Gauges would have proven only
the top half of the stack, leaving storage to be bolted on later.

Scope:
- Instant and rolling average fuel economy; trip and lifetime aggregates.
- Automatic trip detection without an ignition signal — a trip opens on first movement
  and closes after a configurable idle timeout.
- Per-trip history that persists across sessions, charted over weeks and months.
- **Tank calibration**: you enter actual fill-up litres, and DashDeck learns a correction
  factor against its computed consumption. This is the feature that makes a DIY trip
  computer beat the factory one, and it is only possible because we keep history.

Critically, the trip computer is built **as a normal plugin through the public SDK**,
with no host special-casing. If the SDK is awkward, we find out in week one.

It is developed and validated entirely against synthetic drives. A scripted highway run
with a known fuel consumption gives us an exact expected answer to test against —
something a real drive can never provide.

## 7. Safety, containment, and the write path

**Component containment.** Every call into a component is wrapped and time-boxed. A
component that throws or hangs is replaced in the UI by a "component stopped" tile rather
than taking the dash with it; repeated faults auto-disable it until re-enabled in
settings. Components get an isolated storage scope and cannot see each other's data.

**The write path** (Phase 3) passes through exactly one choke point, `IVehicleActions`,
and every action requires all of:
1. A `vehicle.actions` permission declared in the component manifest and granted by you.
2. A global master switch, default off.
3. A per-action interlock (e.g. stationary-only for window actions).
4. An explicit confirm gesture — no action fires from a single stray tap.
5. An append-only audit log entry.

**Driving posture.** Glanceable typography and touch targets sized for a moving truck;
no interaction that requires reading a paragraph; the UI thread never blocks on I/O, so
the dash cannot freeze mid-drive.

**Disclaimer.** This is a personal, non-commercial project that talks to a vehicle
network. It is not a safety device and must never be relied on as one.

## 8. Principal risks

| # | Risk | Assessment | Mitigation |
|---|---|---|---|
| R1 | **Adapter throughput ceiling.** An ELM327/STN-class adapter is a text-protocol device, not a raw CAN interface, and everything in the app shares one serialised link. | **Downgraded 2026-09-01.** The 10–20 requests/second figure was a *Bluetooth SPP* limit; the switch to wired USB (ADR-0007) removes that bottleneck, leaving the STN chip and the bus as the constraint. No replacement number is claimed until it is measured on the truck in P1.5. Live gauges move from "probably not viable" to "measure and decide". | The Request Arbiter, unchanged and still necessary — it is what lets components share whatever the real ceiling turns out to be. The simulator deliberately keeps the old conservative ceiling until a real one is measured. |
| R2 | **Ford-specific PIDs are undocumented.** Trans temp, real coolant, per-wheel TPMS and odometer are not in the legislated OBD-II set. | High likelihood of trial and error. | A discovery mode that sweeps and logs candidate PIDs, cross-referenced against the FORScan community's published Ford PIDs. The signal catalog is data-driven precisely so this is config, not code. |
| R3 | ~~**Windows Bluetooth SPP flakiness** across sleep/resume.~~ | **Largely retired 2026-09-01** by the move to wired USB (ADR-0007). What remains is a COM port handle to re-acquire after sleep — a much smaller problem than re-establishing a Bluetooth link. | Transport-level reconnect with backoff, treating disconnection as normal. The app must never require a restart to recover. |
| R4 | **Gateway Module filtering.** The 2019 F-150's GWM bridges four CAN networks and mediates what the OBD-II port can see. | Unknown until measured on the truck. | Pick an adapter that can reach MS-CAN as well as HS-CAN (see hardware doc); measure early; behind-dash tap remains the fallback. |
| R5 | Fuel-rate PID (0x5E) may not be supported. | Moderate. | Fall back to deriving consumption from MAF; the tank-calibration feature corrects the resulting drift, so this degrades gracefully rather than failing. |

## 9. Phases

| Phase | Goal | Exit criteria |
|-------|------|---------------|
| **P0 — Foundations** *(no hardware)* | Solution skeleton, abstractions, signal catalog, state bus, arbiter, **synthetic vehicle + scripted drives**, shell with one hardcoded tile. | Run a scripted cold-start drive and watch live speed and RPM move in a debug view. Record and replay it. |
| **P1 — First component** *(no hardware)* | Trip computer, storage, charts, tank calibration — loaded through the real plugin path. | A scripted drive is captured, persisted and charted, and its computed fuel use matches the scenario's known value. Nothing about the trip computer is special-cased in the host. |
| **P1.5 — Hardware bring-up** | Buy the OBDLink EX, implement the USB serial transport and STN adapter, **measure the real throughput ceiling**, discover Ford PIDs against the real truck. | The trip computer — unmodified — runs on real CAN data. Every layer above the transport is untouched by this phase. That is the proof the layering was right. |
| **P2 — Ecosystem hardening** | Manifest validation, permissions, `dotnet new` component template, authoring docs, plus a second component (gauges) written using only the public SDK. | Someone who has never seen the host can add a component from the docs alone. |
| **P3 — Actions** | `IVehicleActions`, interlocks, audit log; lock/unlock as the first action. | Doors lock from the dash, with every gate in §7 enforced. |
| **P4 — Comfort** | Media, aux camera, Home Assistant bridge, navigation — as ordinary components. | Each ships without a single host change. |

P4 landing without host changes is the real test of whether the ecosystem thesis held.

## 10. Open questions

Tracked in [`04-open-questions.md`](04-open-questions.md).

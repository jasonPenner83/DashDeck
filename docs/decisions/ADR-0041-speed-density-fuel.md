# ADR-0041 — Fuel flow by speed-density, calibrated by fill-ups, as derived signals

**Status:** Accepted · 2026-10-03
**Builds:** [ADR-0030](ADR-0030-fuel-flow-without-maf.md) (decided, not built until now) ·
**Keeps:** ADR-0016 (the truck first) · rule 7 (quality rendered, never hidden) · ADR-0004 (one plan).
**Amends:** ADR-0015's "only what is on screen declares signals" — with one named, Low-priority exception.

## Context

ADR-0030 found the 2019 F-150 answers neither fuel rate (`5E`) nor mass air flow (`10`), so Fuel
Economy, Avg Economy and Range Estimator have read a dash on the truck since bring-up. It decided
speed-density with mandatory tank calibration, and left it unbuilt. The owner then asked for a
console dash — speed, odometer, instant economy, distance to empty — and, asked, chose to build
speed-density now rather than ship economy and range as dashes.

## Decision

**A derived signal is a kind of catalog signal** (`SignalSourceKind.Derived`): it has an id, a
name, a unit, a range and a category like any other, so a card can show it, a layout can read it
and a component can subscribe to it — but it is **never asked of the truck**. The arbiter grants a
declaration of one in full and leaves it out of the polling plan; the code that derives it holds
the declarations for its inputs and publishes the result to the bus. They are compiled in
(`FuelModel.Definitions`), because the derivation is code, and added to the standard set at load.
They are hidden from Settings ▸ Sensors, which scans, tests and edits things the truck is asked.

**`FuelModel` (Core) derives six:**

| Signal | What |
|---|---|
| `fuel.flowRate` | L/h — **the truck's `engine.fuelRate` when it answers**, else speed-density × the calibration factor |
| `fuel.flowSource` | 0 the truck, 1 calibrated estimate, 2 uncalibrated estimate — so a screen can say which |
| `fuel.economy` | instant L/100 km, lightly smoothed; none below 5 km/h |
| `fuel.economyAverage` | L/100 km since the last fill-up (from 10 km), else the long-run average of past fill-ups |
| `fuel.range` | distance to empty: level × tank (from `VehicleProfile`, 136 L if unset) ÷ the average |
| `fuel.usedSinceFill` | litres since the last fill-up |

**Speed-density:** air density from manifold pressure and intake temperature; air volume from
displacement (`VehicleProfile`, from the VIN decode) × rpm ÷ 120 × a base volumetric efficiency of
0.85; fuel from that ÷ (14.7 × commanded λ, PID `44` — added to the catalog); litres at 0.745 kg/L.
A warm idle of the 2.7 comes out near 1.5 L/h.

**Calibration is a fill-up ledger** (`FuelLedger`, `%LOCALAPPDATA%\DashDeck\fuel.json`). Fill to
full and enter the litres in **Settings ▸ Vehicle ▸ FUEL**: the first starts the count; each later
one compares the pump's litres with the raw estimate since the last and folds the ratio into the
factor, weighted by litres. A ratio outside 0.5–2 is a partial or missed fill, recorded but kept
out of the factor. RESET CALIBRATION takes two taps.

**It runs all the time, at Low priority** — the exception to ADR-0015. A fill-up can only be
compared with fuel counted since the last one; fuel not counted because nothing was on screen is
fuel the calibration never sees. Its inputs (rpm, manifold pressure, speed, fuel rate until the
truck refuses it at 1 Hz; λ 0.5; intake air 0.2; level 0.1) cost about 4–5 of the ~19 requests a
second, and Low gives way to anything on screen.

**Only the real truck counts.** Simulated readings go to a session count that is never saved,
seeded so range reads something at a desk — marked Simulated like everything else there. A gap of
more than 5 s between readings is a lost link, not driving.

**The shipped components read `fuel.flowRate`** instead of `engine.fuelRate`, so Fuel Economy, Avg
Economy and Range Estimator work on the truck. `engine.fuelRate` stays defined and is still asked —
it is what `fuel.flowRate` prefers.

## Consequences

- Economy and range show numbers on the F-150, marked by `fuel.flowSource` as estimates until a
  fill-up calibrates them. They are only as good as the calibration, which is the point of it.
- The arbiter has a kind of signal it never plans; a future derived signal (boost, a towing load)
  is a definition and a class, not a catalog edit.
- Ford's own fuel flow (a mode 22 identifier, ADR-0030's preferred answer) would make the estimate
  unnecessary: define it as `engine.fuelRate` in the vehicle pack and the truck-first rule uses it.
- Displacement must be known: with no VIN decode the estimate reads a dash.

## Alternatives considered

- **Only while on screen**, like everything else. Rejected: calibration needs every litre since the
  last fill-up counted, and a model that counted only while the console showed would teach a wrong
  factor and blame the engine for it.
- **Derived values inside each component.** Rejected: three components and the console would each
  carry the arithmetic and their own calibration, and a layout cannot host a component's logic.
- **A formula language in the catalog** (`"derive": "a * b / c"`). Rejected for now, as ADR-0037
  rejected one for gauges: speed-density needs state (the ledger) and judgement (truck first,
  gaps), which a formula does not have.

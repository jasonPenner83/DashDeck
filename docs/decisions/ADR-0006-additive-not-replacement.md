# ADR-0006 — Additive to SYNC 3, and read-only before Phase 3

**Status:** Accepted · 2026-09-01

## Context

The Surface mounts as an additional screen; the factory SYNC 3 unit stays. The write
ambition is "safe actions" — locks, lights, windows — not module reprogramming.

## Decision

DashDeck is **purely additive**. It does not take over audio, the factory camera, or
climate. It ships **read-only** until Phase 3, at which point writes go through a single
gated `IVehicleActions` choke point.

## Reasoning

Additive means the truck is never degraded by this project. If DashDeck is closed,
crashed, mid-refactor, or left on the kitchen table, the F-150 is a completely normal
F-150. That property is worth more than any feature, and it is only preserved by refusing
to depend on DashDeck for anything the truck already does.

Read-only-by-default follows the same logic. A read bug shows a wrong number; a write bug
does something to a vehicle. Those are not the same category of mistake, and the codebase
should not be able to make the second one until it has earned the right to.

## Consequences

- No audio routing, no reverse-camera takeover, no HVAC control in Phases 0–2.
- The write path is *designed for* from day one but physically absent from the binary
  until Phase 3.
- When it lands, every action requires: a manifest permission, a global master switch
  (default off), a per-action interlock, an explicit confirm gesture, and an audit log
  entry. All five. Any action that cannot satisfy all five does not ship.
- Explicitly out of scope, permanently: reflashing, tuning, and FORScan-style
  configuration writes.

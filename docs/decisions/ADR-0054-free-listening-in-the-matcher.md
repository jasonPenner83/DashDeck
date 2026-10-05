# ADR-0054 — Free listening in the ID matcher

**Status:** Accepted · 2026-10-04
**Builds on:** [ADR-0044](ADR-0044-id-hunter.md) (silent listening, the broadcast ranker),
[ADR-0050](ADR-0050-id-matcher.md) (the matcher is where identifiers are managed).

## Context

The owner wanted to do something in the truck — open the tailgate, press the wheel-heat button —
and see what changed on the bus, then narrow it down. Pieces existed but not that: the ID hunter
listens only through a checklist of targets with fixed steps; Settings ▸ Sensors' WATCH re-asks one
module's identifiers; the matcher only reads FORScan's traffic.

## Decision

A **LISTEN TO THE BUS** tab in the matcher's middle panel, beside FORScan's traffic.

- **Silent and exclusive.** It opens the adapter itself (refused while the tap runs, and the tap is
  refused while it listens), sets it up with AT commands only, and listens with `ATCSM1` through the
  existing `CanMonitor` — nothing reaches the bus. Pins 3/11 use the vehicle file's rate, or one
  found by listening (`CanMonitor.DetectPins311Async`, now shared with the ID hunter).
- **`BusWatch`** (Core) keeps every identifier's latest bytes and three ways to narrow:
  - **noise** — bytes that move while the person learns noise are marked and ignored after;
  - **MARK** — a baseline: bytes differing from it, changes since, identifiers new since;
  - **states A and B** — held phases, ranked by the ID hunter's `BroadcastRanker`, noise left out.
- **F5–F8** for MARK, A, B and RANK; captures save as CSV to `%LOCALAPPDATA%\DashDeck\listen\`.

## Consequences

- A find is a broadcast field, which the dash cannot display: signals are requested, not heard. It
  says which module and byte carry a thing, to look for as an identifier FORScan reads.
- The capture can hold the VIN and stays on the machine, like tap logs.

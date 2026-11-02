# ADR-0082 — Simulated data is a switch, off by default

**Status:** Proposed · 2026-11-02
**Changes:** [ADR-0005](ADR-0005-mock-first.md) (the synthetic truck whenever there is no adapter).
**Builds on:** [ADR-0034](ADR-0034-resilient-adapter-link.md) (going live by itself when the adapter answers).

## Context

ADR-0005 made the synthetic truck the default whenever no adapter answered. A dash with no truck
nearby still had something to show, and it marked every number SIM. That served the long stretch
of building the dash at a desk. The adapter is now the normal case. The owner asked for a switch,
saying they don't think they need simulated data any more.

## Decision

1. **Settings ▸ Vehicle ▸ SIMULATED DATA — ON / OFF**, stored as `simulateWithoutAdapter`. It is
   **off by default**. Like FAST REQUESTS, it applies at the next launch, with RESTART NOW offered.
   Switching it while running would leave the simulator's last numbers on screen, dimmed but still
   numbers, and those are the numbers this switch is meant to keep off the screen.
2. **Off, with no adapter answering, the dash starts on `NoAdapterTransport`:**
   - The adapter's own setup commands (`AT…`, `ST…`) are accepted, so the pipeline starts as it does
     on hardware, and `ATI` answers `NO ADAPTER`.
   - Every question to the vehicle fails as a pulled cable does. The adapter turns that into a
     timeout, and a timeout never writes a signal off as unsupported: Settings ▸ Sensors reads
     WAITING, never NO ANSWER.
   - Nothing is published. Placeholders read Unavailable, not the synthetic truck's values, and so
     do calculated signals.
   - The strip shows an amber **NO ADAPTER — NO DATA UNTIL IT ANSWERS** banner with the reason (the
     chosen port is not there, or none is chosen), and no SIM badge. Settings ▸ Vehicle says the
     same.
   - It still watches for the chosen adapter, and goes live when it answers (ADR-0034), exactly as
     it does from the simulator. What polling learned meanwhile is forgotten then.
3. **On:** ADR-0005's synthetic truck, unchanged, marked SIM.
4. **The command line can still ask for it:** `--simulate`, a scripted drive by name, `--fault` and
   `--unplug` force it on whatever Settings says. The last three mean nothing without it.
5. **No adapter, nothing to ask.** A module scan, a sweep or a watch is refused with a reason, so
   that an empty result from a dash with no adapter never replaces a saved one from the truck.
6. The stack knows three states: **simulated** (`IsSimulated`), **waiting** (`IsWaitingForAdapter`)
   and **live**. Everything that took "not simulated" to mean live now asks for live: the adapter
   status, `LiveAdapter`, `WatchedPort`, `FallbackReason`, choosing a port, and pausing the search.

## Consequences

- With the truck off and the tablet on a desk, the dash shows dashes, not numbers, unless
  simulated data is switched on. Desk walkthroughs that expect the synthetic truck say to switch it
  on (docs/08, *Before any test in the truck*).
- Nothing synthetic can be mistaken for the truck with it off: there is no synthetic source at all.
- Turning it on is one tap and a restart, for trying a layout or a theme at a desk.

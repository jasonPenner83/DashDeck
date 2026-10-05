# ADR-0053 — Placeholders carry no identifier; the synthetic truck answers from the catalog

**Status:** Accepted · 2026-10-04
**Completes:** [ADR-0052](ADR-0052-nothing-vehicle-specific-shipped.md) (nothing vehicle-specific
shipped). **Changes:** [ADR-0005](ADR-0005-mock-first.md) (the synthetic truck),
[ADR-0050](ADR-0050-id-matcher.md) (what a placeholder is).

## Context

ADR-0052 moved the identifiers out of the code paths that talk to a real vehicle. Two places still
held their own:

- **Placeholders** — the 24 signals the screens need whose identifiers are not known (TPMS, the
  climate panel, the warning lights, economy and range) — carried **invented** mode 01 PIDs on
  pins 3/11 (`C0`–`D7`), so the synthetic truck had something to answer. On a real vehicle with a
  measured pins 3/11 rate, DashDeck **sent those invented requests to the truck**, every poll, to be
  told NO DATA.
- **The synthetic truck** answered about sixty PIDs from its own table in `SyntheticTransport`, and
  its modules, identifiers and broadcast frames were code laid out after a Ford.

Asked, the owner chose: **drive the simulator from the catalog**, and keep **placeholders** for IDs
still to be found, in the UI for now.

## Decision

- **A placeholder has no request.** `"placeholder": true` with no `bus`, `mode` or `pid`;
  `SignalDefinition.HasRequest` is false and `Pid` is no longer required (a non-placeholder still
  needs one). The decode carries only the unit.
  - The **arbiter** plans placeholders at the rate wanted and charges none of the budget, so a
    screen full of them never slows a real signal; `AllocatedHz` leaves them out.
  - **`VehicleService` never asks.** A placeholder's turn publishes Unavailable on a real vehicle,
    or, while simulated, the synthetic truck's value of the same name (`SimulatedValues`), flagged
    Simulated.
  - A vehicle file or overlay entry with the same id and a real request replaces it (the ID
    matcher's pairing already clears the flag).
- **The synthetic truck answers from the catalog.** `SyntheticTransport` takes the running
  catalog and the reference tables. A request is answered by whichever signal the catalog says
  lives there, encoded by that signal's own decode (masks and shared bytes included) from the
  truck's quantity of the same name — `SimulatedF150.Value`/`Reading`, keyed by signal id — plus
  the SAE reference table for standard PIDs the catalog lacks (what a supported-PID scan reports as
  missing). The supported-PID bitmaps are built from the same list, so they never claim a PID that
  does not answer. Change where a signal lives in a file, and the simulator follows.
- **The simulator's invented world is a file**, `catalog/simulator/synthetic-truck.json`
  (`SyntheticTruckData`): its VIN, its modules (renumbered `7E0`, `7E1`, `710`, `740`, `750`, and
  `7A0`, `7A4`, `7B0` on pins 3/11, with neutral `SYNTH-…` identities), their identifiers, a locked
  one, and the broadcast frames the ID hunter listens to. A module answers its identity at whatever
  the scan asks (`IdentityDid`, from the reference and the user's vehicle file), so the desk
  behaves like the cab.

## Consequences

- **No invented request ever reaches a real vehicle.** Before, a measured pins 3/11 bus received
  the placeholders' made-up PIDs every few seconds.
- The synthetic truck's values are by name; only the encoding moved. Readings agree with before to
  the decode's resolution; the order sensor noise is drawn in changed, so seeded runs produce
  different, equally plausible, numbers.
- PID 01 is now answered one byte long (the only byte the catalog describes) rather than four.
- The Simulator project references Core, for the catalog. The simulator is a test double at the
  bottom of the stack reading the catalog above it on purpose: that is what makes it follow the
  files.
- Tests that named the simulator's old Ford-shaped modules use the new invented ones.

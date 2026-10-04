# ADR-0050 — The ID matcher: DashDeck's identifiers managed on a desktop, matched against FORScan

**Status:** Accepted · 2026-10-04
**Builds on:** [ADR-0048](ADR-0048-serial-tap.md) (the tap),
[ADR-0032](ADR-0032-signal-discovery-and-user-catalog.md) (the overlay and TEST),
[ADR-0033](ADR-0033-vin-lookup-and-vehicle-packs.md) (vehicle packs). **Keeps:** rule 3 (never guess a
PID: every match is measured against FORScan), read-only.

## Context

The tap showed FORScan's traffic: which module, which identifier, which raw bytes. Pairing that
traffic with the value FORScan shows was done by hand, reading logs. Meanwhile DashDeck carries 24
signals whose mode and PID are stand-ins, and a few standard ones the truck doesn't answer. Nothing
listed which they were except comments in the catalog.

The owner's view, stated: *identify the IDs in a traditional way*, with a keyboard and mouse, and
keep the touch screen for the dash.

## Decision

**A desktop program, `IdMatcher`, is where DashDeck's identifiers are managed.**

- **The tap is built in.** It holds the COM port and FORScan connects on `127.0.0.1:35000`, exactly
  as with SerialTap. Every session is also saved as a tap log, and saved logs open again.
- **It reads FORScan's traffic the way the adapter does** (`TrafficReader`). It follows `ATSH`, the
  bus (`ATTP6`, `STP53`), `STPX` and the response count, and names every answer's identifier.
- **It knows DashDeck's signals.** The same layers the dash reads: standard, vehicle pack, overlay
  (`SignalPairing`). A signal **needs an ID** when:
  - it is marked `"placeholder": true` (new, on the 24 stand-ins, so the catalog says so as data);
    or
  - it is a standard PID the engine computer's own supported-PID answers, heard in the traffic,
    say it lacks.
- **Matching, two ways:**
  - **Live.** Type what FORScan shows. `ScalingFitter` tries the usual Ford steps and offsets in
    the catalog's units (°F, psi, mph converted), and fits a line once the raw value moves.
    Coincidences drop out as samples arrive.
  - **PID log.** `ForscanCsv` reads FORScan's CSV tolerantly. `LogMatcher` lines it up by clock,
    or by the moment its identifiers were first asked when it counts from its own start, and fits
    every column by regression (R²).
- **Pairing** keeps the DashDeck signal's id, name, range and rate. It takes the truck's module,
  mode, PID and decode, converted into the signal's own unit.
- **Saving** goes to the overlay (`signals.user.json`), never a shipped file. **Export** writes
  vehicle pack entries with the evidence in a comment. Both still pass TEST on the truck before a
  pack commit.
- **Keyboard first:** Ctrl+N for the newest identifier, F3 and Enter for a sample, Ctrl+Enter to
  accept, and more.

## Consequences

- The ID hunter (ADR-0044) and the tablet's Settings ▸ Sensors sweeps stay. They find identifiers
  FORScan doesn't know. The matcher pairs the ones FORScan does, which on this truck is most of
  them.
- A placeholder flag in the catalog is now load-bearing. A new stand-in signal must carry it, or the
  matcher won't list it.
- FORScan's CSV format is not pinned down: no sample is in hand. The reader accepts several shapes.
  A real one may need a tweak.
- Logs and exports can hold the VIN. They stay on the owner's machines.

## Rejected

- **Matching on the tablet's touch screen,** in Settings: the owner's call. Typing values and
  cycling through FORScan is keyboard work.
- **Reading FORScan's screen:** brittle, and the PID log carries the same numbers.
- **Separate tap and matcher programs:** two programs to start for every session. SerialTap stays,
  for plain recording.

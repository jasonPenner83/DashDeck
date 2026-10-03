# ADR-0044 — The ID hunter: a separate, guided, read-only tool for finding identifiers

**Status:** Accepted · 2026-10-03
**Builds on:** [ADR-0035](ADR-0035-module-discovery.md) (module and identifier sweeps, WATCH) ·
ADR-0032 (TEST) · ADR-0033 (vehicle packs). **Keeps:** ADR-0006 (read-only), ADR-0004 (one adapter,
serialised), rule 3 (never guess a PID).

## Context

Finding Ford's own values has been the next hardware job since bring-up (Q13). Settings ▸ Sensors has
the parts — SCAN FOR MODULES, an identifier sweep, WATCH, TEST — but using them means knowing what to
do in what order, reading hundreds of rows, and then comparing a CSV against a FORScan log at a desk.
Some of what the dash wants (doors, seatbelt, seat heat, the HVAC buttons) is probably not an
identifier anyone can ask for at all: body modules broadcast it, and only listening finds it.

The owner asked for a CAN ID program that guides them through the process, and said it need not live
in the GUI. Asked how, they chose: **both asking and listening**, a **terminal wizard**, a **checklist
of targets**, and **a CSV** to send back.

## Decision

**A separate console program, `IdHunter`** (`src/DashDeck.IdHunter`), shipped in `IdHunter\` beside
the dash. It does not run alongside DashDeck — one program holds the adapter — and it is never part of
the dash's request plan.

It walks a **checklist** (`targets.json`, editable) of things to find. Each target uses one of three
methods:

- **listen** — for switches and doors. The guide asks the person to put the truck in a state (door
  shut), press Enter and hold still, then the next (door open), five steps alternating, while it
  listens to the bus. `BroadcastRanker` then keeps every byte, nibble or bit of every frame that
  **held one value within each step, read the same for the same state and differently for different
  ones** — which rejects counters and checksums by construction — narrowest field first, carrying a
  value forward for frames sent only on change. MS-CAN first, then HS-CAN if nothing followed.
- **follow** — for values that move: oil and transmission temperature, fuel flow, oil pressure. It
  sweeps a suggested module's identifiers (the existing `DidScanner`), then watches them pass after
  pass (`IdentifierWatch`) beside a standard reading that the person makes change — coolant while the
  engine warms from cold, rpm while they blip the throttle on cue. `FollowRanker` ranks every reading
  of every identifier (each byte, each pair, signed or not) by correlation, and fits a line to name a
  likely scaling (`value − 40`, `÷ 16 − 40`).
- **match** — for numbers the cluster shows: distance to empty, average economy, tyre pressures. It
  sweeps, the person types what the cluster says, and `MatchRanker` finds identifiers that give that
  number under a set of common scalings and unit changes. One reading matches much by chance; two
  readings at different values, or four tyres sharing one scaling (`MatchTally`), thin it out.

Every ranking ends in a **live check**: the guide shows the chosen candidate changing while the
person does the thing again, and asks *did it follow?* Everything ranked goes to `findings.csv` with
that verdict; every frame heard and every pass watched goes to a capture beside it.

**Listening is new to the vehicle layer.** `IStreamingTransport.StreamAsync` carries the adapter's
monitor (`STMA`, or `ATMA` on a plain ELM), whose answer never ends with a prompt;
`SerialPortTransport` polls the port for it so it can stop cleanly. `CanMonitor` sets the adapter up
for it — **`ATCSM1`, silent monitoring, so the adapter acknowledges nothing and puts nothing on the
bus**, headers on, CAN formatting off, and a receive filter when one identifier is wanted. The adapter
is re-initialised after every listen.

**Still read-only.** Requests are mode 22 and standard mode 01 reads, as before; listening sends
nothing to the vehicle. Every step checks the truck is parked first, and a watch stops if it moves.

**Desk first.** `--simulate` runs the guide against the synthetic truck, which gains a cabin of
switches the guide sets for itself and broadcast frames whose **identifiers are invented** (and say
so), so the whole flow is tested at a desk and in CI without being a claim about a Ford.

Nothing it finds goes into the dash by itself. Confirmed rows come back as `findings.csv`; they reach
the F-150 vehicle pack by a PR, with the evidence, as ADR-0033 asks.

## Consequences

- The dash's Settings ▸ Sensors keeps its scan, sweep, WATCH and TEST; the hunter is the guided way to
  use the same engine, plus listening.
- A broadcast value found by listening cannot yet be shown on the dash: signals are requested, not
  heard. Using one needs a listening path in the vehicle layer — its own decision, when there is a
  confirmed frame worth it.
- How much of HS-CAN the monitor can carry at 115200 baud is unknown (Q20); the guide reports lost
  frames rather than hiding them.
- Captures can hold the VIN. The output folder's README says so, and they stay off the repository.

## Alternatives considered

- **Inside Settings ▸ Sensors.** Rejected for now: the owner did not need it in the GUI, a terminal
  guide is far quicker to change, and listening takes the adapter away from the dash entirely.
- **Free-form tools only (sniff and diff).** Rejected as the default: the point is to be guided. Free
  listening is still there, as L on the menu.
- **Writing confirmed values straight into the user overlay.** Rejected by the owner in favour of a
  CSV to review; and a value found by listening has no request to put there.

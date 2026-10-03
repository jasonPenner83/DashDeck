# ADR-0045 — CAN databases are leads, checked on the truck and never shipped

**Status:** Accepted · 2026-10-03
**Builds on:** [ADR-0044](ADR-0044-id-hunter.md) (the ID hunter) · ADR-0033 (vehicle packs hold only
what was confirmed). **Keeps:** rule 3 (never guess a PID), the repository's rule on assets (never
commit what we have no right to publish).

## Context

The owner found a CAN database on GitHub: `VehicleCAN.dbcx`, Ford's naming throughout, 329 messages
and 2,141 signals. It names the frames that carry much of what DashDeck has been hunting:
- engine oil and gearbox oil temperature;
- fuel flow and distance to empty;
- tyre pressures;
- door status and the odometer.

Two things stop it being used as it stands:
- **It is for another truck.** It was built for P702, the 2021+ F-150, on its CAN FD network. The
  owner's 2019 is the generation before. Ford reuses many identifiers across generations, but not all
  of them, and a value can move within a frame. Taken on trust, a wrong row decodes into a plausible
  wrong number — the failure this project refuses.
- **It is not ours to publish.** The repository holding it has no licence, and the content looks
  like Ford's own. DashDeck's repository is public.

## Decision

**A CAN database is a lead, and the ID hunter checks it against the truck.** **D** on the hunter's
menu (`CanDatabase`):

1. Reads a `.dbc`/`.dbcx` file the person keeps on the tablet. It suggests
   `%LOCALAPPDATA%\DashDeck\dbc\` and takes `--dbc <file>`.
2. Listens to each bus for a few seconds and reports which of the file's messages this truck actually
   sends, and on which bus.
3. Searches the signals by name. A blank search uses what DashDeck is hunting.
4. Decodes the picked signals live, Intel or Motorola order as the file says, with its value text.
   The person makes the value change and compares it with FORScan or the cluster.
5. Records each verdict in `findings.csv` as method `dbc`, with the bit layout and scaling. A message
   not heard is recorded as `absent`.

**No database is ever committed.** Not in `catalog/`, not in tests (they use invented messages), not
as an example. What reaches the F-150 vehicle pack is our own measurement: the confirmed signal,
written in DashDeck's catalog format, with the check that confirmed it in the commit message, as
ADR-0033 asks.

## Consequences

- One session in the cab can settle many leads at once, instead of hunting each value from scratch.
- A broadcast value confirmed this way still cannot be shown on the dash: signals are requested, not
  heard (ADR-0044). Listening for broadcast frames in the dash is the next decision, now with
  concrete frames to justify it.
- CAN FD messages (longer than 8 bytes) are listed but marked: the OBDLink EX hears classic CAN only.

## Alternatives considered

- **Import the file into the catalog.** Rejected: unverified for this truck, and not ours to publish.
- **Ship a curated subset.** Rejected for the same reason. A subset of someone else's database is
  still their database.

# ADR-0052 — Nothing vehicle-specific is shipped; identifiers live in files

**Status:** Accepted · 2026-10-04
**Changes:** [ADR-0033](ADR-0033-vin-lookup-and-vehicle-packs.md) (vehicle packs move out of the repository, into the
user's own folder), [ADR-0035](ADR-0035-module-discovery.md) (the identity question and the sweep
ranges come from files, not code). **Keeps:** read-only (ADR-0006), never guess a PID, pins 3/11 never
sent on at an unmeasured rate (Q21).

## Context

The owner asked for two things: every CAN identifier kept in a file, nothing hard-coded, and a
disclaimer that the project is used at your own risk. Asked where to draw the line, the owner
answered with the reason: **to manage liability, the repository should provide nothing specific to a
vehicle.**

Before this, identifiers lived in four places in code and one shipped file:
- the **SAE J1979 table** (~160 mode 01 PIDs, names and formulas) compiled into `StandardPids`;
- **module names** for the two ISO addresses, compiled into `ModuleNames`;
- **Ford's part-number identifier** `F113`, compiled into `ModuleScanner` and the ID hunter;
- the **identifier ranges** offered for a sweep (`DD00`, `F400`, `1000`…), compiled into Settings;
- the tools reading **speed, rpm and coolant** by PID number (`0D`, `0C`, `05`) to refuse a sweep
  while moving or to follow a value;
- and a shipped **F-150 vehicle pack** (`catalog/vehicles/`) with Ford module names and the measured
  500 kbit/s bus on pins 3/11, plus Ford module and range hints in the ID hunter's `targets.json`.

## Decision

**The repository ships only what public standards define, and placeholders.** Anything that is one
manufacturer's or one vehicle's lives in files the user creates on their own machines.

- **Standard tables are files** in `catalog/reference/`:
  - `sae-j1979-mode01.json`, the mode 01 table (a reference, never polled);
  - `iso-diagnostics.json`, the ISO 15765-4 module names (`7E0`, `7E1`), the ISO 14229 identity
    question (`F187`, spare part number) and the ISO identification range (`F100`–`F1FF`).

  `ObdReference` reads them; a missing or bad file costs what it held and a reported problem.
- **Vehicle files are the user's.** Packs load from `catalog/vehicles/` (now empty, with a README)
  **and `%LOCALAPPDATA%\DashDeck\vehicles\`**; a user file with a shipped one's name replaces it. The
  dash, the ID hunter and the ID matcher all read both. A pack gains `identityDid` (what a module scan
  asks) and `identifierRanges` (added to the sweep's choices), laid over the reference by
  `ObdReference.With`.
- **Pins 3/11 are never sent on at an unmeasured rate.** `ElmAdapter.Pins311BitRate` is now nullable;
  null answers NO DATA without sending. The dash sets null for a real vehicle unless a vehicle file
  gives a rate, including on a simulated start that goes live (`AdapterFailover.GoingLive`, before
  the switch). Settings' module scan skips pins 3/11 and says why. The synthetic truck keeps its own
  rate.
- **Tools read signals by name.** `SignalProbe` asks the catalog's `vehicle.speed`, `engine.rpm` and
  `engine.coolantTemp`, decoded by the catalog, so a vehicle file or overlay that moves one moves it
  for the sweeps, the watch and the hunter too.
- **The ID hunter's checklist** ships without module or range hints beyond `7E0`/`7E1`; the user's
  own `%LOCALAPPDATA%\DashDeck\idhunter\targets.json` is read instead when it exists. Without a range,
  the hunter suggests the reference's and the vehicle files' ranges, or asks.
- **`DISCLAIMER.md`** at the root, linked from the README: as-is, no warranty, your vehicle your
  responsibility, read-only by design and not by guarantee, never while driving, not affiliated.

**What stays in code** are the protocol's own fixed numbers, which describe no vehicle: the OBD
broadcast `7DF` and the engine's reply `7E8`, the module id range `700`–`7F7`, UDS service `22`,
negative-response codes, the supported-PID bitmaps (`00`, `20`, …) and mode 09's VIN. Moving those to
a file would add a way to misconfigure the adapter and buy no separation.

**What comes next** (a second change): the synthetic truck answers from the catalog instead of its
own table of PIDs, and **placeholders carry no identifier at all** — never sent to a real vehicle,
answered by the synthetic truck by name.

## Consequences

- A fresh clone, or a tablet without a vehicle file, reads the standard set; module scans ask `F187`
  (modules are still found — a refusal counts — but show no part number); and nothing is sent on
  pins 3/11 of a real vehicle.
- **The owner's F-150 settings are now a file on his machines**, not in the repository: the pack
  (with `"identityDid": "F113"` and the old sweep ranges added) and his checklist were handed to him
  to put in `%LOCALAPPDATA%\DashDeck\vehicles\` and `…\idhunter\`, on the tablet and the laptop.
- History is not rewritten. The removed pack held module labels and a bus rate, nothing secret; the
  ADRs and in-vehicle notes keep their record of what was found on the owner's truck, as history.
- Tests keep using identifiers as test data; they are not shipped and are not sent to anything.

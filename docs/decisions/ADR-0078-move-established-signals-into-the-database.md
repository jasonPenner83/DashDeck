# ADR-0078 — Moving the signals you have established into your own database

**Status:** Proposed · 2026-10-27
**Builds on:** [ADR-0073](ADR-0073-our-own-can-database.md) (our own CAN database),
[ADR-0074](ADR-0074-catalog-entries-point-into-a-database.md) (a catalog entry points into one),
[ADR-0032](ADR-0032-signal-discovery-and-user-catalog.md) (the overlay),
[ADR-0051](ADR-0051-managing-signals.md) (unconfirmed signals).
**Keeps:** [ADR-0052](ADR-0052-nothing-vehicle-specific-shipped.md) (vehicle files are the person's,
never committed); read-only towards the vehicle.

## Context

ADR-0074 let a catalog entry point into a database. The signals the owner has established on the truck
are still in `signals.user.json`, each with its own request and decode. The owner asked for them to be
moved into the database, where other CAN tools can read them too. This is the first part of ADR-0073's
stage 3 (the editors write to the database): a move of what is there now, not yet the editors.

## Decision

1. **A button, Settings ▸ Sensors ▸ YOUR CAN DATABASE ▸ MOVE TO DATABASE.** It works out the move
   and shows it: how many signals go into which file, and which stay and why. Nothing is written
   until **MOVE**; **CANCEL** drops it, and so does any edit to the signal file in between.

2. **Into `truck-hs.dbc` and `truck-ms.dbc`**, in `%LOCALAPPDATA%\DashDeck\dbc\`. One file per bus,
   as ADR-0073 has it. A file already there is added to, keeping what it holds. A file there that says
   it is the other bus, or does not say, is not written into; its signals stay.

3. **What moves:** an entry of the person's with a request in mode 01 or 22 that is **confirmed**.
   - An **unconfirmed** entry stays until TEST has answered it, so the database holds measurements only.
   - An entry that only renames, hides or re-rates a **shipped** signal stays, and is not mentioned:
     the standard set stays JSON (ADR-0073, stage 4). A correction to one, or an identifier found for a
     shipped placeholder, moves.
   - A placeholder has nothing to move. An entry that already points into a database has moved.
   - Mode 22 asked of every module at once stays: a database names the module that answers.

4. **Each value is written as ADR-0073 writes answers.** Its module's message (`Module_726`, on its
   answer id), multiplexed by service and identifier. The signal is named after the id, with anything
   a DBC name cannot hold made `_` (`ford.oilTemp` → `ford_oilTemp`), and a number added if that name
   is already used in the message for another value. The signal's comment is its name, the catalog's
   min/max is its range, and its states are its value table.

5. **Nothing the dash asks changes.** Every moved entry is read back through the database it went into,
   as the next launch will read it. It moves only if the bus, request, module, decode, unit and states
   come back exactly as they were. Anything else stays, with the reason. Every request in the shipped
   catalog passes this, which a test keeps true.

6. **The catalog entry keeps the use** (id, name, category, rate, staleness, min/max, flags) and refers
   for the rest, as ADR-0074 writes it.

7. **Copies first.** `signals.user.json`, and each database about to change, are copied to
   `<file>.before-move-<yyyyMMdd-HHmmss>.bak` beside them. Then the databases are written, then the
   signal file. A failure before the signal file leaves the dash reading what it read before.

8. **The pure part is in Core** (`DbcMove.Plan`), with no files. `DatabaseMove` in the dash reads and
   writes them.

## Consequences

- The owner's established signals are in a standard `.dbc`. SavvyCAN, cantools, CANExplorer or Vector's
  tools can open it and name those values in a capture. They will not ask for them; the dash or FORScan
  has to be asking.
- The database is the owner's and stays on the tablet. Nothing about the truck is committed.
- Once moved, an edit in Settings ▸ Sensors that changes where or how a signal is read detaches it from
  the database (ADR-0074), and the database keeps the old layout. Writing the edit into the database
  instead is the rest of stage 3.
- A restart applies it, as every change to the signal file does. Until then the dash runs as it was.

## Not done

- The editors writing to the database (Settings ▸ Sensors, the ID matcher's pairing): the rest of stage 3.
- Moving the signals of a vehicle file in `%LOCALAPPDATA%\DashDeck\vehicles\`. The owner's file holds
  none today.
- A way back other than the `.bak`. Copying it over `signals.user.json` undoes the move.

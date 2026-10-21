# ADR-0074 — A catalog entry can point into our own CAN database

**Status:** Accepted · 2026-10-21
**Builds on:** [ADR-0073](ADR-0073-our-own-can-database.md) (stage 2 of its four),
[ADR-0032](ADR-0032-signal-discovery-and-user-catalog.md) (the overlay),
[ADR-0052](ADR-0052-nothing-vehicle-specific-shipped.md) (vehicle files are the user's),
[ADR-0053](ADR-0053-catalog-driven-simulator.md) (placeholders ask nothing). **Keeps:** rule 3 (never
guess a PID), read-only.

## Context

ADR-0073 put a lossless DBC reader and writer in Core, with a convention for values the dash asks for
(each written as the frame that answers it). Its stage 2 was to let a catalog entry say "this value
is in that database" and leave where and how to the database, while the rest of the dash goes on
seeing an ordinary `SignalDefinition`.

## Decision

1. **A `dbc` reference in any catalog file** — the overlay, a vehicle file, even the standard set:

   ```jsonc
   {
     "id": "ford.oilTemp", "name": "Oil temperature", "category": "Engine",
     "defaultRateHz": 1, "min": -40, "max": 160,
     "dbc": { "file": "truck-hs.dbc", "message": "Module_7E0", "signal": "EngOilTemp" }
   }
   ```

   The entry gives **use**: id, name, category, rate, staleness, min/max, hidden, unconfirmed. The
   database gives **where and how**: bus, mode, PID, module, decode, unit and states.

2. **Followed at load, into one folder.** `%LOCALAPPDATA%\DashDeck\dbc\`, by file name alone — a
   name with a folder in it, or one that climbs out (`..`), is refused. `DbcLibrary` opens each file
   once. `SignalCatalog.ParseList`/`FromJson` and `VehiclePacks.Parse`/`LoadFolders` take the library
   and resolve as they read (`DbcReferences.Resolve`), so the dash, the ID matcher and the ID hunter
   all see the same definitions, and nothing after the loader can tell a resolved reference from a
   definition written by hand.

3. **What the database decides**, from the answer DiagnosticAnswers reads (ADR-0073):
   - **bus** from the file's `DashDeckBus` attribute. A file that does not say is not guessed at.
   - **mode, PID and module** from the answer message. Mode 01 to `7E0` is asked of the broadcast,
     as every standard PID is (ADR-0049's fast path still applies); everything else of its own module.
   - **decode and unit** from `ToDecodeSpec`.
   - **states** from the signal's value table, each raw value decoded so it names the same number
     ADR-0056's states do.

   **min/max stay the catalog's**, the guard that decides what is shown. A DBC's range is
   informational in most tools, and copying it in would make two places decide one thing.

4. **Written wrong is refused; unanswerable is a placeholder.**
   - An entry with a `dbc` reference *and* its own `decode`, `pid`, `module`, `states` or
     `placeholder` is refused, as is one with neither a reference nor a decode — mistakes in the
     file in front of the person, treated like any other invalid definition (the overlay or vehicle
     file is refused and says why, as before).
   - A reference the database cannot answer — the file missing, no such message or signal, not an
     answer to mode 01 or 22, no bus named, or a layout the catalog cannot decode yet — loads as a
     **placeholder carrying the reason** (`SignalDefinition.DbcProblem`). Nothing is asked for it, a
     real vehicle reads Unavailable, every other signal keeps working, and the reason is listed with
     the vehicle files' problems in Settings ▸ Vehicle.

5. **Written back as the reference alone.** `SignalCatalog.ToJson` leaves the database's fields out of
   any entry that has a reference — even one that could not be followed — so saving never copies a
   layout into the catalog where it could drift from the database.

6. **An edit keeps the reference only while it leaves the database's part alone.** Renaming,
   re-rating, re-ranging, hiding: the reference stays. Changing the request, decode, unit or states —
   in Settings ▸ Sensors or the ID matcher — drops it, and the edited definition is saved whole in the
   catalog, so the change is kept rather than quietly undone by the database at the next launch
   (`DbcReferences.Keep`, used by both editors). Writing such changes into the database instead is
   stage 3.

7. **`DecodeSpec` does not grow bit fields yet.** A database can describe an Intel field across
   bytes, or three bytes; the catalog's byte-wise decode cannot. Such a reference loads as a
   placeholder saying so. The decode will grow when a real signal on the truck needs it, not before.

8. **An example to try it with, from the standard.** `docs/examples/obd2-standard.dbc` is the
   standard catalog's mode 01 PIDs written as the engine's answers on `7E8` — a public standard, so
   ADR-0052 allows it — and `docs/examples/dbc-test-vehicle.json` a vehicle file reading speed, rpm and
   coolant through it under new ids. Tests keep both in step with the shipped catalog.

## Consequences

- A value can be described once, in a file other tools read, and shown on the dash by adding a few
  lines of use to the catalog.
- The same database loaded into CANExplorer, SavvyCAN or cantools names the answers the dash's own
  requests get, so a value can be checked outside DashDeck with the same definition.
- A renamed signal in the database turns its catalog entry into a placeholder with a reason, rather
  than a wrong number or a dash that will not start.
- Until stage 3, a database is made outside the dash — in another tool, by hand, or from the example —
  and the editors can only detach a signal from it, not write to it.

## Alternatives considered

- **Resolve after loading, in each program.** Rejected: three programs load catalogs, and each would
  have had to remember to; resolving inside the readers means none can forget.
- **Drop an unanswerable reference.** Rejected: an overlay entry with a shipped id would silently
  fall back to the shipped definition, and a new id would vanish from the cards using it. A placeholder
  with a reason keeps the card and says what is wrong.
- **Default the bus to HS when a file does not say.** Rejected: guessing is what rule 3 refuses, and a
  file without the attribute was not written by DashDeck, so it is the person's to say.
- **Let the entry override the database's decode.** Rejected: two places deciding one layout is the
  problem ADR-0073 set out to remove.

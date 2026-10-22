# ADR-0073 — Our own CAN database says where a value is; the catalog says how the dash uses it

**Status:** Accepted · 2026-10-21 (stage 1 built; stages 2–4 to come)
**Builds on:** [ADR-0045](ADR-0045-can-databases-as-leads.md) (other people's databases are leads,
checked on the truck), [ADR-0052](ADR-0052-nothing-vehicle-specific-shipped.md) (nothing
vehicle-specific is shipped), [ADR-0032](ADR-0032-signal-discovery-and-user-catalog.md) (the user's
overlay), [ADR-0056](ADR-0056-multi-state-signals.md) (named states). **Keeps:** rule 3 (never guess a
PID), read-only.

## Context

The owner asked whether DashDeck's signal catalog should become a CAN database — a `.dbc` file, the
format the rest of the CAN world uses — with the app referring to it and doing its own conversions and
refresh rates. The reasons given were that sharing would be easier, and that other tools could be used
instead of built.

The catalog (`catalog/README.md`) is JSON, written for one thing: values DashDeck **asks** a module
for — mode 01 to the broadcast, mode 22 to a module's address — decoded byte-wise (`DecodeSpec`). It
also holds what only the dash needs: the stable `id` cards subscribe to, the poll rate, staleness,
min/max as a guard that drops bad readings, and the placeholder, hidden and unconfirmed flags. It is
layered (standard → vehicle file → the person's overlay).

A DBC describes **frames**: which identifier, which bits, the byte order, scale, offset, unit, range
and value names. It has no way to say "ask this module this question", and nowhere for the dash's own
fields. What it does have is every tool: SavvyCAN, cantools, Cabana, Vector's and Kvaser's editors
all read it, and anyone sharing CAN findings shares them as one.

So neither replaces the other, but they do split cleanly along one line: **what the vehicle does**
(where a value sits and how its bits become a number) and **what the dash does with it** (what it is
called, how often it is asked, when it goes stale, what is believable).

## Decision

1. **Our own database holds where and how; the catalog holds use.** A DashDeck database — ours,
   built from our own measurements — holds each value's identifier, bit layout, scaling, unit, range
   and value names (ADR-0056's states are a DBC value table). The catalog keeps `id`, name, category,
   rate, staleness, min/max, and the flags, and — from stage 2 — points into the database for the rest.

2. **A value DashDeck asks for is written as the frame that answers it.** One message per module, on
   its answer identifier (the request id plus 8: `7E0` → `7E8`), laid out as ISO 15765-2's single-frame
   answer is:

   | byte | holds |
   |---|---|
   | 0 | ISO-TP length (`IsoTpLength`, written so tools show it, never read) |
   | 1 | the service echoed, `41` or `62` — the multiplexor `Service` |
   | 2 | mode 01: the PID — `Pid01`, multiplexed by `Service` at 65 |
   | 2–3 | mode 22: the identifier — `Did22`, multiplexed by `Service` at 98 |
   | 3 or 4 on | the value, multiplexed by `Pid01` or `Did22` at its PID or identifier |

   A module that answers both modes does so on one identifier, and a DBC allows one message per
   identifier, so the message is multiplexed twice, with an `SG_MUL_VAL_` line for each signal
   saying which multiplexor it hangs from — the format's extended multiplexing. The question needs no
   field of its own: the module is the answer id less 8, the service the echo less `0x40`, the
   identifier the multiplexor's value.

3. **What is read as well as written.** The reader also takes the two other shapes these files come
   in: a second multiplexor written as a bare `M` and placed only by its `SG_MUL_VAL_` line (how
   cantools writes our own file back), and a single multiplexor across bytes 2–3, or byte 2 for mode 01
   (how many hand-made databases are written). DashDeck writes only the two-level form, and adds to a
   file in either two-level shape; a one-level message is the person's own and is refused rather than
   rewritten. An answer it cannot place — no `SG_MUL_VAL_` line in a two-level message, a range of
   identifiers, bits outside the value's bytes — is left out and said, never guessed.

4. **One file is one bus.** A module address can be used on both buses, and DBC files are per bus
   everywhere else, so a database says which in a network attribute: `BA_DEF_ "DashDeckBus" ENUM
   "Hs","Ms";` and `BA_ "DashDeckBus" 1;` — an enum's value is its **index**, as every tool expects (a
   quoted name is read too).

5. **The reader and writer lose nothing** (`DbcReader`, `DbcWriter`, `DbcDocument` in
   `DashDeck.Core.Dbc`). A tokenizer and one parser per statement, not a regular expression per line:
   comments run over lines, node lists wrap, statements share lines. Everything the dash does not read
   — transmitter lists, environment variables, signal value types, relations — is kept word for word
   and written back. A statement that cannot be read is reported with its line and skipped; the rest of
   the file still loads. Reading what was written gives the same database again (tested), and the
   files were checked in both directions against cantools 45.1: it loads ours and decodes every answer
   to the same numbers, and ours loads what it writes back.

6. **The bridge to the catalog refuses what it cannot say.** `DiagnosticAnswers.Layout` turns a
   catalog `DecodeSpec` into a DBC signal, and `ToDecodeSpec` the other way, each giving the same
   number from the same frame (tested over random payloads). A catalog mask keeps bits in place (PID
   01's check-engine bit reads 128) where a DBC field is shifted down (it reads 1), so the scale carries
   the difference. What one side cannot express — a gappy mask, a signed masked value, an Intel field
   across bytes, three bytes — is refused, not bent. Whether `DecodeSpec` grows bit fields is stage 2's
   decision.

7. **Ours is the truth only where the catalog points into it.** Files live in
   `%LOCALAPPDATA%\DashDeck\dbc\` — never in the repository (ADR-0052), never shipped. Any other file
   there is still what ADR-0045 calls it: a lead, checked on the truck before anything reaches the
   catalog.

8. **The ID hunter's D reads through the same reader.** `CanDatabase` is now a view over
   `DbcReader`, so the hunter gets the proper parser and there is one bit decoder, `DbcBits`.

### Stages

1. **Read and write DBC in Core, with the answer convention** — this ADR's change.
2. **A catalog entry points into a database** — `"dbc": { "file": …, "message": …, "signal": … }` or
   similar, with definitions written wholly in the catalog still working. The loader builds the same
   `SignalDefinition` from both, so the poller, arbiter, cards, warnings and simulator do not change. A
   reference that does not resolve is reported in Settings ▸ Sensors like an invalid overlay.
3. **The editors write to the database.** Settings ▸ Sensors, the ID matcher's pairing and its export
   write the layout to the person's database and the use to `signals.user.json`.
4. **The standard OBD-II set** probably stays JSON: it is legislated, small, and shipped.

## Consequences

- A FORScan session recorded by the tap, or a listen capture, can be opened in a DBC tool and read as
  named values, because FORScan's answers on `7E8` match our multiplexed messages. Graphing, replay
  and log analysis come from those tools rather than being built here — once captures are in a format
  they open, which is its own small piece of work.
- A database of our own measurements is something other owners can use, shared by hand. The
  repository still ships none.
- An answer longer than one frame cannot be described: a DBC message is one frame. That leaves out
  the VIN and part numbers, and no value the dash shows.
- A tool that does not read `SG_MUL_VAL_` will see every value of a module's message at once,
  decoded from every answer; cantools reads it, and others should be checked before relying on them.
- Two files describe one signal from stage 2, joined by names. A renamed signal breaks the join; that
  is why stage 2 reports a dangling reference rather than dropping the signal quietly.
- A DBC's range is informational to most tools. The catalog's min/max stays the guard that decides
  what is shown.

## Alternatives considered

- **Replace the catalog with a DBC outright.** Rejected: a DBC cannot say what to ask, has no layers,
  and has nowhere for the dash's fields except custom attributes only DashDeck would understand —
  which would throw away the reason for using the format.
- **Keep the catalog and only export a DBC.** Rejected as the end state: the export would be a copy,
  and copies of a bit layout drift. One file holding each layout is the point.
- **One message per service.** Impossible: a DBC allows one message per identifier, and a module
  that answers both services answers both on one.
- **One multiplexor across the service and identifier together.** Rejected: its values (`6484493`
  for `62 F40D`) are unreadable in every tool.

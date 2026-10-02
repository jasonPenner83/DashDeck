# ADR-0035 — Asking modules by address: a module sweep, an identifier sweep, and signals that name a module

**Status:** Accepted · 2026-10-02
**Builds on:** [ADR-0032](ADR-0032-signal-discovery-and-user-catalog.md) (the Sensors section, TEST and
the user overlay), [ADR-0033](ADR-0033-vin-lookup-and-vehicle-packs.md) (vehicle packs).
**Keeps:** [ADR-0006](ADR-0006-additive-not-replacement.md) — read-only.

## Context

Settings ▸ Sensors could scan the truck, and found one module's worth of signals. FORScan, on the
same truck and the same adapter, lists a dozen and more modules — PCM, ABS, BCM, IPC, APIM, the
doors — and reads values from each. The difference is addressing, not hardware:

- **Every request went to the broadcast.** The adapter sends to `7DF`, which only *emissions*
  ECUs are required to listen to: the engine computer, sometimes the transmission. Mode 01 lives
  there because the law put it there. Everything else on a Ford — door states, wheel speeds, the
  odometer, transmission temperature — is a mode 22 identifier on a particular module, which
  answers only when asked **by its own address**: an 11-bit request id from `700` to `7F7`,
  answered on that id plus eight.
- **A signal could not say which module.** `SignalDefinition` and `PidRequest` had a mode, a PID
  and a bus. So the TEST loop that ADR-0032 built for Ford PIDs (R2, Q13) could never have reached
  most of them, whatever number was typed.
- **Two answers read as garbage.** With headers off, when the engine and transmission computers
  both answer the broadcast, the adapter prints two lines and the parser glued them into one long
  malformed reply.
- **A refusal read as garbage too.** A module that is there but declines a request answers
  `7F 22 31` — a negative response. The parser expected the positive echo and called it malformed,
  which hides the most useful thing a sweep can learn: *something is at this address*.

## Decision

**A request can name a module.** `PidRequest` gains an optional `Header` — the module's request id,
or null for the broadcast. `ElmAdapter` points the adapter at it only when it changes: `ATSH` for the
request id, `ATCRA` to listen only for that module's reply (id + 8), and an explicit flow-control
header (`ATFCSH`, `ATFCSD300000`, `ATFCSM1`) so a reply longer than one frame — a part number — is
carried on by the right module. A broadcast request puts it back (`ATSH7DF`, `ATAR`, `ATFCSM0`).
These are **adapter** settings; nothing extra is sent to the vehicle. A reply address (`7E8`) or
the broadcast is refused as a module before anything is sent.

**A signal can name a module.** The catalog gains `"module": "726"` — hex text, not a number, so a
file can be checked against FORScan by eye. Left out, the signal goes to the broadcast, as every
standard PID does; nothing already in a catalog changes. `SignalCatalog.Check` refuses an address
that is not a module's, as the editor is typed.

**The parser understands two more shapes.** A negative response is `PidFailure.Rejected` with its
code (`NegativeCode`); "response pending" (`7F xx 78`) is skipped for the answer that follows;
and several unframed single-frame replies — several modules answering the broadcast — read as the
**first**, not as one malformed reply. Multi-frame replies are joined as before. Polling treats a
refusal like `NO DATA`, with the same patience before retiring a signal.

**Settings ▸ Sensors ▸ MODULES** has two sweeps, both reads only:

1. **SCAN FOR MODULES** asks every module address on HS-CAN and then MS-CAN — 128 per bus, the
   ids with the 8s bit clear — one question each: UDS `22 F113`, ReadDataByIdentifier, the
   module's part number. **Any answer** — the part number, or a refusal — means a module lives
   there; only silence means none does. No diagnostic session change, no tester-present, no
   writes: being asked its part number changes nothing in a module. About a minute on the truck,
   with a progress bar and STOP.
2. **IDENTIFIERS** on a chosen module asks every identifier in a range — presets for the
   identity block (`F100–F1FF`) and other common ranges, or a typed range up to **4,096** (about
   four minutes). A module that is there answers every identifier with a value or a no, so an
   absent one costs one exchange, not a timeout; the sweep **stops itself** after twelve silences
   in a row, because the module is then asleep or gone. Refusals that mean "no such identifier"
   are not listed; ones that mean *it exists but* — locked behind security access, or not in
   these conditions — are.

An identifier that answers with one to four bytes has **+ DEFINE**, which opens the ADR-0032
editor with the mode (`22`), identifier, bus and module filled in and a placeholder formula. The
editor gained a **MODULE** field, and TEST goes to it. Nothing about how a definition is saved,
overlaid, or promoted into a pack changes.

**Both sweeps refuse to start while the truck is moving.** They ask the speed once, directly, and
decline above zero: a sweep takes most of the adapter's time, and the dash goes stale while it
runs. On the synthetic truck there is no speed worth refusing on, and results say SIMULATED.

**Module names come from the vehicle pack.** A pack gains `"modules": { "726": "BCM — body
control" }`. The screen shows the name as **likely**: the sweep finds modules by asking, and a wrong
name mislabels a row and nothing more. Without a pack, only the two addresses ISO 15765-4 itself
gives meaning to (`7E0`, `7E1`) are named. The F-150 2.7 pack carries the usual Ford addresses.

**The synthetic truck has modules.** Five on HS-CAN (one of them declines the part-number question),
three on MS-CAN, part numbers that say `SYNTH`, and a body module with two invented identifiers and
one locked one — so the sweeps, the parser's new shapes and the module field are exercised on every
run, and none of it is a claim about a real Ford.

## Consequences

- The path from FORScan's module list to a dash card is now all on the tablet: find the module,
  sweep its identifiers, DEFINE one, TEST it while the thing it measures changes, save. Open
  question Q13 (fuel flow, oil temperature, transmission temperature) becomes a session in the
  driver's seat rather than a code change.
- Switching between a module and the broadcast costs adapter commands — five one way, three back
  — each a few milliseconds on the real adapter. A dash with module-addressed signals interleaved
  with broadcast ones pays that per switch. Unmeasured; if it matters, the arbiter can learn to
  group requests by module.
- The synthetic adapter charges its full simulated latency for adapter commands too, so a module
  sweep on the simulator is slower than on the truck. Not worth a second latency figure.
- 29-bit addressing is not covered. The 2019 F-150 uses 11-bit on both buses; a vehicle that does
  not will find nothing and say so.
- Names in packs are labels. Identifiers in packs remain held to ADR-0033's rule: confirmed on a
  real vehicle with TEST, or not there.

## Alternatives considered

- **Tester-present or an extended diagnostic session** to reach more identifiers. Rejected: a
  session change is a state change in a module, and the project's posture before Phase 3 is reads
  in the default session only. What the default session will not give stays ungiven.
- **Probe with `3E 00` (tester present)** to find modules. Rejected for the same reason, and
  because `22 F113` both finds the module and identifies it.
- **A numeric `header` field** in the catalog. Rejected: `1830` cannot be checked against FORScan's
  `726` by eye.
- **Combining the answers when several modules answer the broadcast** (OR-ing support bitmaps).
  Rejected for now: addressing a module is the honest way to hear a particular one, and the first
  answer keeps today's behaviour for everything that worked.
- **Hard-coding Ford module names.** Rejected for the ecosystem's sake: names belong to a vehicle,
  so they live in its pack, and somebody else's truck brings its own.

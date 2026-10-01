# ADR-0032 — Finding and defining signals from Settings: a scan, a TEST, and a user overlay catalog

**Status:** Accepted · 2026-10-01
**Extends:** [ADR-0004](ADR-0004-request-arbiter.md) (the catalogs are data) and
[ADR-0017](ADR-0017-sensor-catalog.md). Prepares for P1.5's PID discovery (R2) with the OBDLink EX.

## Context

Every value on the dash comes from one of two catalogs — the vehicle signals
(`signals.obd2-standard.json`) and the tablet sensors (`sensors.device.json`) — and both were
only visible as JSON files in the repository. Three things were missing:

- **No way to see them.** Nothing on the tablet listed what DashDeck can show, where each value
  comes from, or what the truck has said about it. A signal the truck refused simply read
  `UNAVAIL` on whatever card happened to show it.
- **No way to find missing ones.** OBD-II has a question built in for this — PID `00` answers a
  bitmap of the supported PIDs `01`–`20`, PID `20` the next thirty-two, and so on — and nothing
  asked it. A PID the truck supports that the catalog lacks never reveals itself at all.
- **No way to add or correct one without a laptop.** "A new signal is a JSON edit" was true, but
  the JSON sits beside the executable, which `publish.ps1` deletes and rewrites on every deploy.
  Discovery is trial and error done in the truck (R2), and it needs a loop on the tablet:
  ask, look at the bytes, adjust the formula, ask again.

## Decision

**A Sensors section in Settings** with three parts.

1. **An inventory.** Every vehicle signal, grouped by `category` as the card picker groups them,
   tagged `YOURS` or `CORRECTED` where the user's file is involved, with a status read from the
   pipeline: a value, `WAITING`, `NOT ASKED` (nothing has wanted it since launch), `NO ANSWER` (the
   polling loop retired it after repeated refusals) or `NEXT LAUNCH`. Below them, the tablet and
   phone sensors with the source each is reading from and the truck signal that would supersede
   it. **Listing a signal never declares demand for it** — the inventory reads
   `IVehicleSignals.Current` and a new `VehicleService.StatusOf`, and never calls `Require`, so
   opening the page cannot put forty signals into the plan.
2. **SCAN THE TRUCK.** `PidScanner` walks the mode 01 supported-PID bitmaps on HS-CAN and MS-CAN
   and the section sets the answer against the catalog both ways: PIDs the truck supports that
   nothing defines, and defined signals the truck says it does not support. A missing PID opens
   the editor pre-filled from a SAE J1979 table (`StandardPids`) — the standard's formula where it
   is a single linear value, a raw-byte placeholder and an explicit "work the formula out" where
   it is a bitfield or a record.
3. **An editor with TEST.** Every field of a definition (id, name, group, bus, mode, PID, byte
   offset and length, signedness, scale, offset, unit, rate, min, max), checked as it is typed by
   the catalog's own `SignalCatalog.Check`. **TEST** sends the request once and shows the raw
   bytes beside what the formula makes of them and whether min/max would let it through; editing
   the formula re-decodes the same bytes without asking again.

**Edits go to a user overlay, never the shipped file.** `%LOCALAPPDATA%\DashDeck\signals.user.json`
holds the user's definitions, in the catalog's own format. At launch `SignalCatalog.Overlay` lays
it over the shipped catalog: a user definition with a shipped id **replaces** it (a correction,
revertible), a new id **adds** one. The merged catalog is validated as one; if it fails, the whole
overlay is dropped, the shipped catalog runs alone, and the Sensors section says so in red.

**Applied at the next launch, with RESTART NOW one tap away.** The arbiter, the state bus and the
card picker are built from the catalog once; changing it live would mean rebuilding them under
every subscription. The restart relaunches with the same arguments *after* the vehicle stack is
disposed, so on the truck the serial port is free before the successor opens it.

**Probes go through the pipeline, outside the plan.** `VehicleService.ProbeAsync` hands one
request to the same adapter, whose gate serialises it between polls. A scan is at most a dozen
requests and a TEST is one, both asked for by a person; neither repeats, so neither takes a share
of the arbiter's budget.

## Reasoning

**Ask the truck, don't guess (ADR-0016).** The supported-PID bitmaps are the vehicle's own answer.
They replace "poll it and see if it goes quiet" for the standard set, and they make the catalog's
mistakes visible in both directions. MS-CAN's body modules are not emissions ECUs and usually do
not answer the bitmaps; the scan reports that as an answer rather than hiding it, because it is
exactly why Ford's own values (mode 22, two-byte PIDs) are found with TEST and not with SCAN.

**The shipped file stays known-good.** `catalog/README.md` already said the standard set "should
not be churned by the trial and error of PID discovery". An overlay keeps that true on the tablet
too, survives deploys (it lives with the other settings, C1-clean), and makes every user change
revertible. A definition that proves itself is promoted by copying it into a shipped file — same
format, no translation.

**Refuse early, never half-apply.** A wrong decode spec puts a confidently wrong number on a dash,
the failure this project ranks worst. So the editor checks with the load-time validator as you
type, TEST shows the decoded value *and* the range verdict before saving, min/max still drops
anything outside them at runtime, and a bad overlay is dropped whole rather than partly applied.

**Where this sits against "components never see PIDs".** That line is about *consumers*:
components and the card editor subscribe to named signals and still never see a mode, a PID or a
decode spec (`ValueChoice` is unchanged). The Sensors editor is the other side of the line — it
edits the catalog, which is data, the way a text editor on the JSON would.

## Alternatives

- **Write to the shipped catalog.** Rejected: deleted by the next deploy, and it mixes trial and
  error into the known-good set.
- **Hot-reload the catalog.** Rejected for now: rebuilding the arbiter and bus under live
  subscriptions is a large change for something done parked, a few times, during bring-up. The
  restart button covers it. Revisit if editing on the move turns out to matter.
- **Poll every catalog signal once to find unsupported ones.** Rejected: dozens of requests on an
  unmeasured budget (Q12) to learn less than four bitmap queries tell.
- **Editable tablet sensors.** Rejected: a sensor is a hardware channel with a mount reference,
  not a formula. They are listed with their sources; their supersession is the `prefer` field,
  which needs nothing new — defining the preferred vehicle signal in the overlay hands over.

## Consequences

- **A seventh Settings section, SENSORS**, between Vehicle and Apps.
- **`signals.user.json` joins `%LOCALAPPDATA%\DashDeck\`.** A corrupt file is an empty overlay and
  a reason, never a dash that will not start.
- **The synthetic truck answers five standard PIDs the shipped catalog lacks** — bank 2's fuel
  trims and catalyst temperature (the 3.5 EcoBoost is a V6), commanded λ, and distance with the
  MIL on — and lists them in the bitmaps it already answered for the bring-up tool, so a scan
  has something to find on the desk.
- **The bitmap decoding is shared with the bring-up tool** (`PidSupportScanner.DecodeBitmap`).
  `PidScanner` exists beside it because the app asks through the running pipeline
  (`VehicleService.ProbeAsync`) and must re-ask a dropped bitmap rather than end the walk.
- **`SignalCatalog` gains `Overlay`, `FromDefinitions`, `Check`, `ParseList` and `ToJson`**;
  `VehicleService` gains `StatusOf` and `ProbeAsync`. Validation gained two rules: a name is
  required, and scale must be a non-zero number.
- **Bring-up with the EX gets a tool on the tablet**: SCAN answers what the truck supports, and
  TEST is how Ford mode 22 PIDs are tried (R2, Q13) — from the driver's seat, without a laptop.

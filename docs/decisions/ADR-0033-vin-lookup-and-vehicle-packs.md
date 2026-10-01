# ADR-0033 — Which vehicle this is: VIN lookup, a richer vehicle profile, and vehicle signal packs

**Status:** Accepted · 2026-10-01
**Extends:** [ADR-0029](ADR-0029-vehicle-profile.md) (the vehicle profile; `apiVersion` 1.1 → 1.2),
[ADR-0032](ADR-0032-signal-discovery-and-user-catalog.md) (the catalog overlay) and
[ADR-0016](ADR-0016-vehicle-first-heading.md) (ask the truck first).

## Context

The project assumed one truck in its comments and its defaults — and assumed it wrongly: the
code and two ADRs said "3.5 EcoBoost" about a truck that has the **2.7**. Nothing broke, because
nothing read the engine yet, but ADR-0030's speed-density fuel flow will (Q15 asks for the
displacement), and Ford's own mode 22 PIDs differ by engine and year. And the repository is
public: anybody fitting DashDeck to their own vehicle would have to edit code to say what it is.

The fix is to stop *saying* what the vehicle is and *ask*: every vehicle carries a VIN, the
vehicle will report it, and a public decoder turns it into year, make, model and engine.

## Decision

**Settings ▸ Vehicle starts with the VIN.**

- **READ FROM TRUCK** asks the vehicle for its own VIN — mode 09 PID 02, a read — through
  `VehicleService.ProbeAsync`, the same serialised path as the PID scan (ADR-0032). The box also
  takes a VIN typed or pasted, for when no adapter is in reach. It is tidied (case, spaces,
  dashes) and checked as typed: seventeen characters from the VIN alphabet, and the North
  American check digit — a failed check digit is a *warning*, never a refusal, because VINs from
  elsewhere need not use it.
- **LOOK UP** sends it once to **NHTSA's vPIC decoder** (free, public, no key) and caches the
  answer in `%LOCALAPPDATA%\DashDeck\vehicle.json`, so the truck needs no signal afterwards. A
  partial decode is kept with the decoder's explanation; an unrecognised VIN changes nothing.
- **Every decoded field is editable**, and an edit is labelled as one ("NHTSA vPIC + your
  corrections", or "Entered by hand" with no VIN at all). The tank stays a hand-set value: no
  VIN encodes it.
- **FORGET** deletes the file.

**The component profile gains what the vehicle is** — `ModelYear`, `Make`, `Model`, `Trim`,
`EngineDisplacementLitres`, `EngineCylinders`, `Turbocharged`, `FuelType` — at **`apiVersion 1.2`**,
the second additive bump. Zero, empty and null mean "not known". **The VIN is not on it.**

**Vehicle signal packs.** `catalog/vehicles/*.json` each name a vehicle by `match` (make, and
optionally model, year range and displacement) and carry its manufacturer signals. At launch the
packs that match the cached vehicle are laid over the standard catalog, and the user's overlay
(ADR-0032) over that: **standard → vehicle pack → yours.** The first pack is the 2018–2020 F-150
2.7 EcoBoost, and it is **empty** — it is where Ford PIDs go once TEST has confirmed them on a
real truck (Q13), not before.

**Applied at the next launch**, like the rest of the Vehicle section, with RESTART NOW beside it.

**The ELM parser now reads multi-frame replies with spaces off.** With `ATS0` an ISO-TP frame
index is glued to its data (`0:490201…`); every reply until the VIN fitted one frame, so the
whole line read as unparseable and nobody had noticed.

## Reasoning

**Ask, don't assume — the vehicle-first rule applied to identity.** The same reasoning that puts
the truck before the tablet for heading (ADR-0016) puts the truck before the keyboard for the
VIN: it cannot be mistyped. The typed box is the declared fallback, not a peer.

**The VIN is personal; the facts are not.** A VIN identifies one vehicle and, through
registration, its owner. So it goes to exactly one place a person chose (the decoder, on a
button press), is stored only on the tablet, can be forgotten, and never reaches components —
none of which need the number to use the engine size. CLAUDE.md's "no VINs in the repository"
covers the synthetic truck too: its VIN has serial `000000`, which is never issued.

**One online call, cached, is the right trade.** vPIC is authoritative for North America and good
elsewhere, and an offline decode would get the year and the manufacturer and little else —
not the engine, which is the point. The weather fetch already set the posture: online when
asked, cached, and the dash runs without it.

**Packs, not branches in code.** A manufacturer's PIDs differ by vehicle, and the ecosystem goal
is that another owner's truck gets its own signals by adding a file, not by editing one meant for
Jason's. A pack with no make is refused: it would match every vehicle on the road. A pack holds
only what TEST has confirmed, because a guessed PID that answers is the plausible wrong number
the project refuses.

## Alternatives

- **Hard-code the vehicle.** What was done, and why the 3.5 / 2.7 mistake happened. Rejected.
- **Offline decode only** (WMI and model-year tables). No network, but no engine or model for most
  makes. Rejected as the primary path; the check digit and alphabet are still checked offline.
- **The VIN on the component profile.** Rejected: no component needs it, and the contract would
  hand personal data to every plugin.
- **Packs selected by the user from a list.** Rejected as the default: the VIN already says which
  applies. Matching is data (`match`), so a mismatch is fixed by correcting a decoded field.

## Consequences

- `apiVersion` is **1.2**; the host serves 1.0–1.2 and every existing component still loads.
- `vehicle.json` joins `%LOCALAPPDATA%\DashDeck\`. It holds the VIN; FORGET deletes it.
- `catalog/vehicles/` ships with the binary like the rest of the catalog.
- **Q15's displacement is answered by the VIN** (2.7 L for this truck); the VE correction is still
  open. A speed-density component can read `EngineDisplacementLitres` instead of a constant.
- The synthetic truck answers mode 09 PID 02 with a multi-frame reply, which keeps the parser fix
  tested end to end.
- The "3.5 EcoBoost" comments in the simulator and ADR-0032 are corrected. **ADR-0030** keeps its
  text (accepted ADRs are not edited); its VE argument holds for any turbocharged engine, and this
  ADR records that the truck is the 2.7.

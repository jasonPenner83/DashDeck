# ADR-0030 — Fuel flow on a truck with neither a fuel-rate nor a MAF PID

**Status:** Accepted · 2026-10-01
**Closes:** open question Q4 · supersedes the mitigation stated for risk R5

## Context

Bring-up on the 2019 F-150 (OBDLink EX, STN2232 v5.12.4) asked the truck which mode 01
PIDs it supports. It answered `4100BFBEA893` and onward — 48 data PIDs across five
ranges. Two of them are missing:

- **`0x5E` engine fuel rate — not supported.** Expected; risk R5 called it.
- **`0x10` mass air flow — not supported.** Not expected, and it was the entire stated
  fallback for R5.

Both inputs to fuel consumption are therefore absent, and the P1 trip computer with fuel
history depends on knowing fuel flow. The project doc's mitigation for R5 — "derive from
MAF" — is void.

Also absent and worth recording: `0x5C` oil temperature, `0x0A` fuel pressure, `0x52`
ethanol, `0x2C` commanded EGR, `0x61`/`0x62`/`0x63` torque, `0x47` throttle B.

## Decision

Compute fuel flow by **speed-density**, from PIDs the truck does support, and treat
**tank calibration as mandatory** rather than as a refinement.

Supported and sufficient for this:

| PID | Signal | Role |
|---|---|---|
| `0x0B` | Intake manifold absolute pressure | air mass |
| `0x0F` | Intake air temperature | air density |
| `0x0C` | Engine RPM | pumping rate |
| `0x44` | Commanded equivalence ratio (lambda) | air-to-fuel ratio |
| `0x33` | Barometric pressure | reference |
| `0x04` / `0x43` | Calculated / absolute load | sanity cross-check |

Air mass flow is estimated from RPM, MAP and intake temperature with a volumetric
efficiency term; fuel mass flow follows from that and the commanded equivalence ratio.

## Reasoning

Volumetric efficiency is the weak link, and on a turbocharged 3.5 EcoBoost it is not a
constant — under boost it exceeds 100%, and it varies with RPM and load. So the computed
flow carries real error.

**This is exactly what the tank-calibration feature absorbs.** It was designed into P1 as
the thing that would make a DIY trip computer beat the factory one: you enter actual
fill-up litres and DashDeck learns a correction factor against its computed consumption.
That feature has now gone from a nicety to load-bearing — it is the only thing standing
between a speed-density estimate and a number worth showing a driver.

Which means the P1 design holds, but a dependency has inverted: **fuel economy is not
trustworthy until it has been calibrated against at least one real fill-up.** The UI must
say so rather than present an uncalibrated estimate as fact, consistent with the rule that
signal quality is rendered and never hidden.

Engine displacement and the learned VE correction belong on the **vehicle profile**
(ADR-0029), not hard-coded — they are properties of this truck, and the profile is already
the place vehicle-specific facts live.

## Alternatives

- **Ford enhanced PIDs (mode 22).** Almost certainly the better answer: FORScan reads Ford
  fuel flow, oil temperature and transmission temperature from manufacturer-specific mode
  22 PIDs that the standard mode 01 support bitmaps do not advertise. `PidRequest` already
  carries a 16-bit PID and formats mode 22 commands correctly, so the plumbing exists —
  what is missing is knowing the PID numbers, which needs experimentation against the
  truck. **Preferred long-term; pursued next.** Speed-density is what ships in the
  meantime, and remains the fallback for anything mode 22 does not yield.
- **Injector-duty estimation.** Needs injector flow rates and short/long-term trims, and
  compounds more unknowns than speed-density for no better accuracy.
- **Drop fuel economy from P1.** Rejected. It is the component chosen precisely because it
  exercises persistence and history, and it is the feature with the clearest advantage over
  SYNC 3.

## Consequences

- The catalog gains speed-density inputs; `engine.fuelRate` and `engine.mafRate` stay
  defined but are unreachable on this vehicle. No code change is needed for that — the
  polling loop already detects repeated `NO DATA`, writes the signal off and stops spending
  request budget on it.
- Fuel economy carries a calibration state, and the UI must distinguish *uncalibrated
  estimate* from *calibrated*.
- Displacement and the VE correction factor go on the vehicle profile (ADR-0029).
- A Ford mode 22 discovery pass is the next hardware-side task, and would also recover oil
  temperature and transmission temperature — the latter being the single most requested
  gauge for a truck that tows.

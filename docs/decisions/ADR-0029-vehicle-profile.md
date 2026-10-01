# ADR-0029 — A vehicle profile on the component context (apiVersion 1.1)

**Status:** Accepted · 2026-09-29
**Extends:** [ADR-0023](ADR-0023-component-host.md) (the component contract) and
[ADR-0008](ADR-0008-branching-and-versioning.md) (this is the first additive `apiVersion` bump,
1.0 → 1.1). Prompted by the Range Estimator, which needs the fuel tank's size.

## Context

A range estimate needs the fuel tank's usable size, and no PID reports it — it is a fact of the
*build*, not a reading off the bus (a 2019 F-150 ships 23, 26 or 36 US-gallon tanks). The Range
Estimator shipped with the size baked in as a constant. That is fine for one truck and wrong for
the ecosystem: the value belongs to the vehicle, the user should set it, and other components
will want it and its cousins — weight, wheel circumference — soon after.

## Decision

**The component contract gains a `VehicleProfile`, read from `IComponentContext.Vehicle`.** It
carries facts about the truck the app is fitted to — `FuelTankLitres` today, more fields as they
are needed. The user sets them in a new **Vehicle** section in Settings; the shell reads them at
launch, builds one `VehicleProfile`, and lends it to every component through the context, the
same way signals and the clock are lent.

**This is `apiVersion 1.1`** — the first additive bump (ADR-0008). It is a new member with a
default (`VehicleProfile.Empty`), so a component built against 1.0 still loads and runs unchanged;
it simply does not read the profile. A component that *does* read it declares 1.1, and the host —
which now serves 1.0 through 1.1 — accepts both.

**A snapshot, applied at the next launch.** A tank does not change size while you drive, so the
profile is read once at startup rather than threaded as live settings, and a change in the Vehicle
section takes effect on the next start — the same contract the GPS transport already sets.

**The Range Estimator reads the profile, with the constant kept as a fallback.** On a host that
provides no profile (an older one, or the `--components` dev path) it falls back to the baked-in
136 L, so it degrades rather than breaks.

## Reasoning

**It is a vehicle fact, not a component setting.** The contract already has `IComponentSettings`
for a component's *own* configuration, and the tank size is not that: the Range Estimator does not
own it, the truck does, and the next component that wants it should read the same number rather
than each carrying its own copy the user has to set twice. So it belongs on the context beside the
signals, not in a per-component store.

**Additive, because the ecosystem promise is the whole point (ADR-0008).** A new fact is a new
field on `VehicleProfile` and a new member is a minor bump — every existing component keeps
loading. The first bump is worth doing carefully precisely because it sets the pattern for every
one after: read at launch, default when absent, fall back in the component.

**Snapshot over live.** Threading live settings into the component host would mean rebuilding or
observing across the load-context boundary for a value that changes about once in the life of a
truck. The launch-time snapshot is simpler and honest — the Settings note says so.

## Alternatives

- **Keep the constant in the component.** Rejected: right for one truck, wrong for the ecosystem,
  and it makes the user edit code to change a number about their own vehicle.
- **Model it as the Range Estimator's own `IComponentSettings`.** Rejected: the tank is the
  truck's, not the component's, and a second consumer would have to set it again. It is also the
  surface whose editing UI is not built yet (the component-host "Next"), whereas a shell settings
  section is a pattern already in hand.
- **A file the component reads directly** (`vehicle.json` in `%LOCALAPPDATA%`). Rejected: it
  couples a component to the shell's storage layout and breaks the rule that a component reaches
  the outside world only through the context.
- **Live settings threaded to the host.** Rejected as over-built for a value that changes once;
  the snapshot with a next-launch note is enough.

## Consequences

- **`apiVersion` is now 1.1**, and the host serves 1.0–1.1. The Range Estimator declares 1.1; the
  other four components stay 1.0 and are unaffected. This is the first live exercise of the
  additive-bump promise — the reason the contract was version-ranged from the start.
- **A `Vehicle` section joins Settings**, with the fuel tank in litres (and a US-gallon hint,
  since the spec is usually quoted in gallons). It is built to grow — "more factors will land
  here" is on the screen.
- **`fuelTankLitres` joins `settings.json`** in `%LOCALAPPDATA%`. Still C1-clean.
- **Verification is a screenshot and the maths**: the detail shows the tank it used and the range
  it computed from it; `VehicleProfile` and the fallback are simple enough to read.
- **When per-component settings and their editor land** (the component-host "Next"), this does not
  move — a vehicle fact stays a vehicle fact. The two surfaces are different on purpose.

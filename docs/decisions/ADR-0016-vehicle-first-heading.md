# ADR-0016 — Ask the truck first, the tablet second, and say which answered

**Status:** Accepted · 2026-09-02
**Builds on:** [ADR-0004](ADR-0004-request-arbiter.md) (the catalog is data; components
subscribe to named signals) and [ADR-0005](ADR-0005-mock-first.md) (no code assumes a truck
is attached).
**Relates to:** B4 (widgets beyond vehicle signals) — this is the same question arriving from
the other direction, on the stage rather than in a card.

## Context

The compass needs a heading, and there are two places one could come from.

**The truck knows.** The F-150 has a compass; SYNC 3 displays it. It is not in the legislated
OBD-II set, so it lives on a Ford module and reaching it depends on PID discovery against the
real vehicle (R2) and on what the Gateway Module passes through (R4, Q5). None of that has
happened, and no truck has been connected to anything yet.

**The tablet knows too, badly.** The Surface Pro 7 has a magnetometer. It reports through
`Windows.Devices.Sensors.Compass`, it works, and it is a magnetometer in a steel cab, near
speakers, a metal mount and the vehicle's own wiring — everything a compass is meant to be
kept away from. It also measures the *tablet's* orientation, which is only the truck's while
the Surface is in its mount.

The stated preference is broader than the compass: **where the truck knows something, get it
from the truck.** That needs to be a rule with a shape, not a case-by-case judgement, because
the interesting values — heading, location, altitude, fuel range, tyre pressures — will each
arrive at this fork.

## Decision

**Vehicle data is a named signal, and the vehicle is asked first.** `vehicle.heading` is the
id. A component asks the catalog for it exactly the way it asks for road speed, and knows
nothing about where it comes from.

**`vehicle.heading` is deliberately not in the catalog.** Not "not yet wired" — *absent*,
because the PID is unknown. Guessing one would produce a decoded number that is confidently
wrong, which is the single failure this project refuses. The day it is discovered, adding a
JSON definition switches the compass over **with no code change**.

**The device sensor is a declared fallback, never a stand-in.** `PreferredHeadingSource` tries
the vehicle, falls back to the tablet, and re-evaluates on every read — so a truck heading
that goes stale hands over and takes back over when it recovers.

**Which source answered is rendered.** The stage says `TRUCK` or `TABLET · MAGNETIC` or
`TABLET · TRUE`, beside the same quality badge every other value on the dash carries.

**Sensor confidence maps onto `SignalQuality`.** Windows' `MagnetometerAccuracy` becomes
Live / Stale / Unavailable, and `Approximate` is demoted to **Stale** rather than promoted to
Live. Unknown and unreliable render no number at all.

**The Host's platform floor rises to `net10.0-windows10.0.19041.0`** for the WinRT sensor
projection. The Host alone — the engine and its tests still target plain `net10.0` and still
build anywhere (ADR-0010).

## Reasoning

**A substitute that cannot be told apart from the real thing is a lie.** "The truck says 341°"
and "the thing suction-cupped to your windscreen says 341°" are different claims with
different reliability, and a dash that renders them identically is misleading precisely when
it matters — on a back road, at night, when the heading is the only thing you have. The
project already refuses this for vehicle values: every reading carries Live / Stale /
Unavailable / Simulated, and `SIMULATED` is on screen right now so synthetic numbers can never
be mistaken for real ones. Naming the heading source is the same principle applied one layer
out.

**Preferring the vehicle is not sentimentality about CAN.** The truck's heading is measured by
the vehicle, about the vehicle, and unaffected by whether the tablet is in its cradle or on
the passenger seat. The device sensor is better than nothing and worse than the real answer,
which is exactly what "fallback" should mean.

**Keeping the preference in a source rather than in the compass** is what makes it a rule
instead of a special case. The next value to arrive at this fork gets the same shape, and the
compass view-model does not know which source it is drawing.

**Not guessing a PID is the whole discipline of ADR-0005 restated.** It would have been easy
to put a plausible Ford PID in the catalog and let it decode into garbage until corrected. The
catalog's `min`/`max` guard exists because a wrong decode usually produces a wildly
out-of-range number — but a heading is a number between 0 and 360 whatever it decodes to, so
the guard would not catch it. This is one of the values where a wrong PID is *invisible*.

**Demoting Approximate to Stale** is deliberate. Approximate is exactly the state a
magnetometer sits in after being carried past a truck door, and a bearing that is roughly
right looks identical on screen to one that is right.

## Alternatives

- **Ship a device compass and say nothing.** Simplest, and what most apps do. Rejected: it
  makes the tablet's guess indistinguishable from the truck's answer, which is the failure
  above.
- **Guess a Ford PID now.** Rejected — see above; a wrong heading is in range and therefore
  invisible to the catalog's own correctness guard.
- **Ship no compass until the PID is found.** Consistent, and would leave the stage empty for
  months on a dependency (R2) that has no date. Rejected: the fallback is honest as long as it
  is labelled, and building the preference now is what makes the PID a config change later.
- **Read the magnetometer through the Sensor API by P/Invoke** and avoid raising the platform
  floor. Rejected: considerably more code and more failure modes, to avoid a floor the tablet
  already exceeds by two major versions.
- **Correct magnetic declination ourselves.** Rejected as premature. Windows supplies true
  north when it has a location fix and the label says which north it means otherwise; carrying
  a declination model to improve on that is work for a problem nobody has measured yet.

## Consequences

- **The compass will read badly in the cab until the PID is found**, and the honest label is
  the mitigation rather than a fix. How badly is unmeasured — first real data comes when the
  Surface is mounted in the truck.
- **A precedent has been set for every device-versus-vehicle value.** Location, altitude and
  G-force will each arrive at this fork, and each should get a named signal, a declared
  fallback and a visible source rather than a fresh judgement call.
- **The Host now depends on the Windows SDK projection**, which is one more thing that must
  keep working in a self-contained publish. It does, and it is checked by running the
  published executable rather than the development build.
- **The stage consumes vehicle data for the first time.** The compass declares three signals
  through the arbiter like any component. Nothing new was needed for that, which is a small
  piece of evidence that the layering is right — but it does mean a stage occupant can now
  spend request budget, and occupants have no equivalent of the card pages' suspension rule.
- **`vehicle.heading` is a promise in the code with nothing behind it.** If PID discovery
  fails or the Gateway Module blocks it, the constant stays and the fallback is permanent.
  That is a visible dead end rather than a hidden one, which is the point.

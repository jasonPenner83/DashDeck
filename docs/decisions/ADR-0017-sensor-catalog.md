# ADR-0017 — A second catalog, for what the tablet can measure

**Status:** Accepted · 2026-09-02
**Builds on:** [ADR-0016](ADR-0016-vehicle-first-heading.md) (vehicle first, device fallback,
visible source) and [ADR-0004](ADR-0004-request-arbiter.md) (catalogs are data; one budget,
shared).
**Generalises:** the hand-written heading preference ADR-0016 shipped. That ADR predicted the
fork would recur and said each value should get "a named signal, a declared fallback and a
visible source rather than a fresh judgement call". It recurred immediately.

## Context

A G meter and vehicle pitch and roll were wanted on the compass stage. All three are values
the truck plausibly knows — stability control cannot work without lateral and longitudinal
acceleration, and rollover mitigation implies attitude — and all three are also things the
Surface can measure with its own accelerometer and inclinometer.

That is exactly the fork ADR-0016 settled for heading, and heading was implemented as three
purpose-built classes with the preference written into them. Repeating that shape four more
times would be four more places for the rule to drift.

Two things made a plain copy impossible anyway.

**The signal catalog cannot describe a sensor.** A signal definition is a mode, a PID, a bus
and a decode spec. A tablet sensor has none of those, and needs something a PID never does:
a mount reference.

**Raw device attitude is meaningless in a vehicle.** Measured on the development Surface, on a
kickstand, sitting perfectly still: pitch 69°, and 0.91 g on one axis. Rendered raw that is
most of a g of cornering force in a parked truck — a confidently wrong number, which is the
one failure this project refuses everywhere else.

## Decision

**A second catalog, `catalog/sensors.device.json`**, data like the first. Each entry names a
value the tablet can measure about the vehicle: an id, a unit, which piece of hardware, which
channel, a range, and — the field that matters — **`prefer`**, the vehicle signal that
supersedes it.

**`prefer` is ADR-0016's rule as data rather than as an if-statement.** The moment that id
appears in the signal catalog, the value comes from the truck and the tablet stands down, with
no code change on either side. `SensorService` resolves it on every read, so a truck value
that goes stale hands over and takes back over when it recovers.

**None of those vehicle signals exist**, deliberately, for the reason ADR-0016 gave: guessing
a Ford PID produces a decoded number that sits inside its own plausible range, so the
catalog's `min`/`max` guard cannot catch it.

**A mount reference is required, captured, and persisted.** `MountReference` records what
"level, pointing forward" means for this tablet in this mount. Attitude is expressed relative
to it. Acceleration has gravity removed as a *vector* and what remains resolved into axes
built from the mount — down from the reference gravity, across from the tablet's X axis with
the down component projected out, forward from the cross product. A sensor that needs a
reference and has not got one **renders nothing and says why**.

**Device values never enter the arbiter's plan.** A truck-supplied value is declared and costs
budget; a tablet-supplied one costs nothing, because reading a magnetometer is not traffic on
the OBD-II link.

**Readings carry `SignalQuality` and a visible source**, exactly as ADR-0016 established, and
are guarded by their declared range the way signals are.

## Reasoning

**Two catalogs, because they describe different things.** Folding sensors into the signal
catalog would give both a shape that fits neither — every signal carrying a nullable mount
flag, every sensor carrying a nullable PID — and it would put device values into the request
arbiter's plan, where they would consume a budget they cost nothing against. Keeping them
apart also keeps the signal catalog exactly what it is: the description of an OBD-II
conversation.

**The reference is the feature, not a setup step.** Without it there is no G meter, only a
tilt indicator wearing a G meter's clothes. It is also why the frame is built from the mount
rather than from the tablet: assuming the tablet's axes are the truck's works only for a mount
that happens to be plumb, and produces lateral force under braking for every other one. The
arithmetic that separates a tilted mount's forward from its sideways is the single piece of
this worth testing hardest, and it is.

**Refusing to render beats rendering approximately.** An unlevelled dash shows the compass —
which is measured against the earth's field and needs no reference — and says plainly what the
other three are waiting for. That is the same rule as a stale signal rendering as a placeholder
rather than a stale digit.

**The ball moves the way the driver is pushed**, so braking throws it forward and a right-hand
bend throws it left. The opposite convention is equally defensible on paper and reads as broken
in a moving vehicle, where the body already knows which way it is being pulled.

**Peak is reset by levelling**, because a peak measured against axes that no longer exist is
not a number about this mount.

## Alternatives

- **Extend the signal catalog with a sensor kind.** Fewer files. Rejected above: it fits
  neither shape, and it puts device values in front of the arbiter.
- **Hard-code the five sensors** the way heading was hard-coded. Simplest, and it is what
  ADR-0016 shipped. Rejected because the fork recurred within a day of that ADR predicting it
  would, and a rule expressed five times is a rule that will eventually be expressed four ways.
- **Skip levelling; zero against whatever the tablet reads at startup.** Tempting — no button,
  no file. Rejected: it silently calibrates against whatever angle the truck happened to be
  parked at, including a hill, and quietly re-zeroes mid-drive if the app restarts.
- **Show raw device pitch and roll**, labelled as the tablet's. Honest, and useless: nobody
  wants to know the angle of their tablet.
- **Make sensors bindable as dash cards** so a G reading could sit in the band grid. Genuinely
  wanted, and it is B4 — the card system takes catalog *signals*, and widening it to a second
  source is the component host's job (ADR-0015). Deliberately not done here; the sensor catalog
  existing as data is most of what that work will need.
- **Capture yaw as well**, supporting a tablet mounted at an angle across the cab. Rejected as
  premature: the shell is portrait-first against a dash mount, and a yawed mount is a problem
  nobody has yet.

## Consequences

- **A third file in `%LOCALAPPDATA%\DashDeck\`** — `mount.json`, beside the settings and the
  dashboard, and outside the folder `publish.ps1` rewrites. Levelling survives an update.
- **A reference can go stale silently.** Bump the mount, or move the tablet to a different
  cradle, and every attitude and G reading is wrong by that much with nothing on screen to say
  so. The capture time is recorded but not yet shown. Raised as an open question rather than
  solved.
- **Sensor values are still not card sources.** The dash can hold as many cards as you like
  and the signal catalog has eleven things to put on them (F13); this adds five more values
  that a card cannot yet bind to. That gap is now the most obvious argument for B4.
- **The compass stage carries three clusters** — heading, attitude, motion — and is close to
  as much as it should. Anything further belongs on a card.
- **`Windows.Devices.Sensors` is now used for three sensors rather than one**, so the platform
  floor ADR-0016 raised earns more of its keep.
- **Five more promises in the code with nothing behind them.** `vehicle.lateralAccel`,
  `vehicle.longitudinalAccel`, `vehicle.pitch`, `vehicle.roll` and `vehicle.heading` are all
  visible dead ends until PID discovery says otherwise — which is the point, but it does mean
  the catalog now advertises more than the truck has ever delivered.

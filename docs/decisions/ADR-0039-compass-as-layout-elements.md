# ADR-0039 — The compass as stage layout elements: sensor sources, a rose and a G meter

**Status:** Accepted · 2026-10-02
**Builds on:** [ADR-0037](ADR-0037-stage-layouts.md) (the stage as a file) ·
[ADR-0038](ADR-0038-stage-launcher-file.md) (the launcher as a file) · ADR-0016/0017 (truck first, a
declared tablet fallback named on screen) · ADR-0027 (the phone's GPS). **Keeps:** ADR-0013 and rule 7
(quality rendered, never hidden) · ADR-0004 (only what is on screen declares signals).

## Context

After ADR-0038, COMPASS could be renamed, moved and hidden from `launcher.json`, and its colours
followed the theme. But the screen itself was still code: a XAML view and a view model holding the
heading rose, the G meter, pitch and roll, speed, outside air and the phone's position, at fixed sizes
in fixed places. Asked how to edit it, the owner chose to make the compass **part of stage layouts**
rather than give it a settings file of its own.

Stage layouts could only read **vehicle signals**. Everything on the compass except speed and outside
air comes from the **sensor catalog** — heading, attitude, G, position — through `SensorService`,
which asks the truck first and falls back to the tablet (or phone) with the source named on screen.

## Decision

**A gauge's source can be a sensor.** `"source": { "sensor": "attitude.pitch" }` reads that sensor
through `SensorService` instead of a signal; every style (dial, arc, bar, LCARS bar, digital) works.
`scale` and `offset` apply; `minus` does not; `rateHz` is ignored. A source is a signal **or** a
sensor, never both. A sensor-sourced gauge **draws its source along its bottom edge** — `TRUCK`,
`TABLET`, `PHONE · GPS`, `NOT LEVELLED` — in the quality colour (`showSource`, default on), because a
fallback is named on screen, never a silent stand-in (ADR-0016). A reading that is not usable is
`NO DATA`, never a zero; an id the sensor catalog lacks says `UNKNOWN SENSOR`.

**Two new element types**, each the old screen's drawing made into an element:

- **`compass`** — a rose of ticks and letters, the heading number and its compass point in the
  middle, a marker, and the source underneath. `mode` is `rose` (the card turns under a fixed marker,
  as before) or `needle` (north stays up, a needle turns). Every colour, the tick step, the letters
  and the readouts are parts. Reads `attitude.heading` unless it names another sensor; it cannot read
  a signal. Headings are eased as vectors, so crossing north never swings through south.
- **`gMeter`** — rings, a crosshair and a ball pushed the way the driver is (braking up, a right-hand
  bend left), with total and peak g beneath. `range` sets the outer ring (default 1 g), `rings` the
  count. **No levelled mount, no ball** — it says so instead. The peak resets on re-levelling and from
  **RESET PEAK G**, which the three-dot menu offers on any stage whose layout has a G meter.

Sensors are polled every 60 ms, only while the layout on screen has a sensor element. Reading one is a
lookup, not vehicle traffic.

**The COMPASS screen is the built-in `compass` layout**, compiled in beside the F-150 cluster and
written to `stage\examples\compass.json`: the rose, latitude and longitude, the G meter, pitch, roll,
speed and outside air, where they were. The launcher's `compass` entry shows the layout called
`compass` — **a `compass.json` of yours in `stage\` replaces it** (yours before shipped before
built-in, as for any layout name), and `"layout"` on the entry can show another. The built-in compass
also appears in Settings ▸ Themes ▸ STAGE LAYOUT, so GAUGES can show it too.

`CompassView`, `CompassViewModel` and `CompassStageOccupant` are gone; COMPASS is a `GaugesStageOccupant`
pinned to the compass layout. The arithmetic — the eight-point cardinal, vector smoothing, the ball's
position — moved, unchanged except that the ball now clamps to the outer **ring** rather than a
square, into `SensorMath`, which is tested without WPF.

## Consequences

- Any layout can carry a heading, a G meter, or pitch and roll as any gauge style — an LCARS stage
  with a rose, a towing stage with pitch beside trans temp.
- The compass's position readout is now two digital gauges (LATITUDE, LONGITUDE), each with its own
  source line, instead of one line of text.
- A theme does not yet bring its own compass layout; a theme names one stage (ADR-0037). Whether it
  should name more is Q19's question one level down.
- The old compass's colours were fixed theme tokens; they are now parts with those tokens as defaults,
  so the built-in compass looks as it did.

## Alternatives considered

- **A compass settings file** (ring sizes, which readouts, G meter on or off). Rejected by the owner
  for this: quicker, but a second format for one screen, and nothing on it could be placed in another
  layout.
- **Sensor values as signals in the vehicle catalog**, so layouts needed no change. Rejected: the
  truck-first fallback and its on-screen source are the point of `SensorService` (ADR-0016), and a
  tablet reading posing as a vehicle signal is the silent stand-in that ADR forbids.
- **An `attitude` element** (an artificial horizon). Deferred: pitch and roll as numbers is what the
  old screen showed, and any gauge style now draws them. A horizon is a natural next element.

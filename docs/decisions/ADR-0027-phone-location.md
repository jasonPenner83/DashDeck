# ADR-0027 — The phone's GPS as a location source

**Status:** Accepted · 2026-09-08
**Extends:** [ADR-0016](ADR-0016-vehicle-first-heading.md) and
[ADR-0017](ADR-0017-sensor-catalog.md) — location is the fork ADR-0016 predicted, arriving as a
third provider with a declared fallback and a visible source. Uses the transport seam of
[ADR-0003](ADR-0003-transport-abstraction.md)/[ADR-0005](ADR-0005-mock-first.md) and follows the
synthetic-then-real shape of [ADR-0019](ADR-0019-phone-projection.md)'s dongle.

## Context

The Surface Pro 7 has no GPS, OBD-II carries no position, and the Carlinkit/Android-Auto path
does not expose the phone's location (projection is video and audio only; in real Android Auto
location flows car → phone, not out). So the phone itself is the only usable GPS on hand.
ADR-0016 already named this: *"Location, altitude and G-force will each arrive at this fork, and
each should get a named signal, a declared fallback and a visible source."*

## Decision

**The phone's GPS is a third provider behind the sensor model — labelled `PHONE`.** It reads NMEA
from a GPS-share app on the phone and answers three catalog entries — `location.latitude`,
`location.longitude`, `location.groundSpeed` — plus a **heading** from the GPS course. Every
reading carries its source string (`PHONE · GPS`, `PHONE · NO FIX`) exactly like the tablet's
`TABLET · MAGNETIC`, and its quality on this dash's scale (a weak or aged fix reads Stale, no fix
Unavailable, the synthetic source Simulated).

**It defers to the truck, for free.** `location.groundSpeed` prefers `vehicle.speed`, so the
truck's own wheel speed wins when the adapter is plugged in and the GPS is the fallback —
`SensorService`'s existing truck-first rule, unchanged. Latitude and longitude prefer vehicle
signals that do not exist yet (the F16/F19 convention), so they stay `PHONE` until a Ford
position PID is ever found.

**Heading prefers the GPS course, but only while moving.** A stationary GPS reports a random
course, so below a small speed the phone declines and the tablet magnetometer answers — which in
a steel cab is usually the worse of the two, so this eases F16.

**Transport-agnostic, synthetic-first, no driver.** An `ILocationSource` seam has a
`SyntheticLocationSource` (a canned track, stamped Simulated, for the desk and `--shot`) and a
`TcpNmeaLocationSource` (connects to a `host:port`, reads on a worker, reconnects on drop).
Bluetooth would be another source behind the same seam. A TCP socket is not a driver, so C1's
two-driver-exception limit is untouched. The parser (`NmeaParser`, RMC + GGA, any talker id,
checksum-validated) is pure and is where the tests are (F7 keeps the shell out of them).

**Privacy: location never leaves the tablet.** DashDeck reads the phone's position over the local
network and does nothing else with it — no upload, no logging off-device. Read-only, like
everything before Phase 3.

**Off by default.** A `Use phone GPS` toggle and a `host:port` in Settings, applied at the next
launch; a `--gps <host:port|synthetic>` dev flag forces one for a screenshot or a test.

## Reasoning

**The sensor model already had the shape.** `SensorService` resolves truck-first then asks one
`IDeviceSensors` for the reading, and `SensorReading` already carries a free-text provenance
string. So the phone is a new `IDeviceSensors` provider, composed with the tablet's, and almost
nothing else changes — the compass shows `PHONE · GPS` with no compass code touched, and a card
binds a GPS value through the same `SensorCardValue` a magnetometer value uses. Building a
parallel system would have been more code and a second way to say the same thing.

**Never reparent the resolver, never guess a PID.** GPS speed and heading are real measurements,
so they do not risk the "confidently wrong number in range" that a guessed PID would — but they
are still the *fallback*, declared as such, so the day the truck can answer, it does, and the
label changes on screen rather than in a code review.

**Course, not compass, but only when it means something.** A GPS course is the truck's actual
direction of travel and needs no calibration; a magnetometer inside a steel cab is the thing
ADR-0016 was written around. Preferring the course while moving is the honest ranking; declining
it while stopped is the honest exception, and the provenance says which answered.

## Alternatives

- **A companion app we build for the phone.** Rejected for now: Android GPS-share apps already
  serve NMEA over TCP, so there is no phone code to write. DashDeck stays a Windows app.
- **Model GPS as vehicle signals with placeholder PIDs.** Rejected: GPS is not OBD-II traffic and
  has no PID; putting it in the signal catalog would misuse the arbiter's budget and invite a
  guessed PID. It is a sensor-shaped value, so it goes in the sensor catalog.
- **Windows' own location (Wi-Fi positioning).** Rejected: too coarse and connectivity-dependent
  for a moving truck, and the Pro 7 has no GNSS to fall back to.
- **Get it through the Carlinkit dongle.** Not possible — projection does not expose the phone's
  location to the host.
- **A moving map now.** Deferred to Q18 (MapLibre/OSM tiles, offline, licensing). This increment
  is the feed; the map is the next one, built on it.

## Consequences

- **A new source string, `PHONE`, joins `TRUCK` and `TABLET`** on the compass and on cards, and a
  `Location` group appears in the card editor. The four quality colours are unchanged.
- **`vehicle.speed` may now be answered by the phone** (as `location.groundSpeed`) when the truck
  is absent, which is new: a speed reading that used to blank without the adapter can now come
  from GPS, clearly labelled `PHONE`.
- **"Keeps a fix" is verified by eye, with a real phone.** The parser and the moving-heading rule
  are unit-tested; that a collapsed Wi-Fi link ages the fix and reconnects, and that a real
  Android app's NMEA parses, is a truck-seat check.
- **F16 is eased, not closed.** A GPS heading now exists and is usually better than the
  magnetometer, but `vehicle.heading` (and `vehicle.latitude`/`longitude`) are still undefined —
  the truck PIDs remain F19. Altitude and G from GPS are not done.
- **`apps.json`/`settings.json` gain two keys** (`gpsEnabled`, `gpsEndpoint`) in
  `%LOCALAPPDATA%`. Still C1-clean: a socket, no driver, no registry.

## Addendum — 2026-10-15: a fix is built from the sentences, not the last one

A real phone (GPS NMEA Tether over Bluetooth, COM4) reached the dash for the first time, and the
readings were intermittent. Both causes were in this ADR's own read loop, which kept **whichever
sentence parsed last** — a reasonable-looking line that is wrong twice over:

- **Not every sentence carries every field.** RMC has speed and course; GGA has neither. An app
  sending both — most do, and this one does — therefore alternated between a fix with a heading
  and a fix without one, once a second, so the compass handed back to the magnetometer on every
  other sentence. The *moving-heading* rule above was working perfectly; it was being fed NaN.
- **A receiver says "no fix" out loud.** A void RMC or a quality-zero GGA parses fine and carries
  no coordinates. Keeping the newest sentence therefore threw away a good position because of one
  blip, and the gauges blanked.

**Decision: a `LocationTracker` holds what is known, and sentences update it rather than replace
it.** Fields merge across sentences inside a 3-second window, so a GGA does not take away the
course the RMC beside it supplied. A position is held: a sentence without one means *nothing new*,
not *nowhere*. A dropped link marks the fix down to Stale rather than blanking it, because a
two-second Bluetooth hiccup and a phone left at home arrive at the same code path.

**Held is not frozen, and that line is the whole point.** The held fix keeps the timestamp it was
taken at, so its age is always on screen; it demotes to Stale after 5 seconds and stops being
offered as a position at all after 10. Quality only ever moves down — being young never promotes
a fix the link already marked down. A phone that has genuinely lost its fix still reports it
within ten seconds; it just no longer does so because of a single sentence. A dot that stopped
being true is the worst thing a map can draw, which is why the hold has an end rather than being
a smoothing filter.

The carry-forward fills what is *absent*, never what is inconvenient: a zero speed is a
measurement, so stopping reads as stopped rather than holding the last moving value.

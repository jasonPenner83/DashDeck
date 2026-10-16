# ADR-0066 — Navigation readouts as sensors, and why strings stay out

**Status:** Accepted · 2026-10-16
**Builds on:** [ADR-0027](ADR-0027-phone-location.md) (the phone's GPS as a device sensor),
[ADR-0016](ADR-0016-vehicle-first-heading.md) (the truck supersedes the tablet),
[ADR-0065](ADR-0065-address-search.md)'s addendum (a destination and a route exist at all),
[ADR-0056](ADR-0056-multi-state-signals.md) (how the dash already shows words).
**Does not implement:** [ADR-0058](ADR-0058-offline-map-and-routing.md)'s guidance. **Opens:** Q47.

## Context

A searched place and a route to it now exist, and both are held in C# — the geocoder and the router
are called from the host, parsed in Core, and the map page is handed finished values. So the
question was not how to get navigation data out of the WebView2. It was never in it.

The question was whether those values could be **sensors** — pointed at by a card, dropped on a
stage layout, read by anything that reads `attitude.pitch` today. And underneath it, a sharper one
that came up directly: *why wouldn't a sensor be allowed to carry a string?*

## Decision

1. **Navigation is a third `IDeviceSensors` provider**, `NavigationSensors`, composed by
   `CompositeDeviceSensors` beside the tablet's sensors and the phone's GPS. One more arm on two
   switch statements; nothing above it changes.
2. **Six rows in the device catalog**, under a new `Navigation` source: a straight-line distance
   and bearing to the chosen place, the route's total distance and time, and — once directions
   have been asked for — the distance and time still to drive.
3. **Numbers only. No strings in a sensor reading.** The destination's name, website, phone and
   opening hours stay where they are, in the route strip and the map's popup.
4. **Every value carries the fix's own quality**, not one of its own. A synthetic track produces
   navigation numbers stamped `Simulated`; a GPS that goes quiet takes them to `Stale` and then to
   nothing.
5. **It reads the phone's aged fix**, through a new `PhoneLocationSensors.Fix`, rather than the raw
   source — one staleness rule for the dash instead of two that will eventually disagree.
6. **A position more than 150 m off the route refuses to answer** the countdown, with
   `NAV · OFF ROUTE`, rather than reporting a shrinking distance.
7. **The time remaining is the router's own duration, pro-rated by the distance left.** It is not a
   fresh estimate and does not pretend to be.
8. **The totals need no position at all** and are reported `Live`, because they are the router's
   answer about the road rather than a measurement of the truck.
9. **`prefer` is named only where the truck could actually supersede the row.** The distance and
   time remaining name `vehicle.nav.distanceRemaining` and `vehicle.nav.timeRemaining`; the
   straight line and the bearing name nothing, because nothing in the truck has an opinion about
   them at any price.
10. **The arithmetic lives in Core** — `Geo` for distance and bearing, `RouteProgress` for what is
    left of a route — so all of it is tested on a machine that cannot run the dash.

## Reasoning

### Why not strings in a sensor reading

`SensorReading` is a record struct and adding `string? Text` to it would break nothing. That is the
cheap part, and it is not the reason.

The numeric contract is not the reading, it is `ICardValue.Value`, and everything above it is built
on that `double`: the dial, the bar, the number and graph faces, `DisplayUnits` and its °C/°F and
km/h/mph conversions, `ValueChoice.HasRange` — which is the thing that decides whether a card can
be drawn as a bar at all — and the warning thresholds. A string value cannot be dialled, barred,
graphed, converted or threshold-coloured, so it is not one field but a parallel face family, with
two thirds of the card editor's options dark for those rows.

Two smaller things bite as well. **NaN is the dash's "nothing" convention**: `SensorReading.None`
is NaN plus `Unavailable`, and NaN is how every face knows to draw a dash rather than a zero. A
string has no NaN, so a second convention would arrive — `null` against `""` against `"—"` — in the
one place this project is most careful. And **`prefer` would stop being true**: every sensor row
names a vehicle signal that may one day supersede it, and a vehicle signal is bytes decoded to a
number, because `DecodeSpec.Decode` returns `double?`. A text row could never hand over to the
truck, which makes it a row permanently lying about the property that makes it a sensor row.

### The dash already shows words, and already decided how

[ADR-0056](ADR-0056-multi-state-signals.md) hit exactly this and did not reach for strings. A gear
selector is a number plus a `states` table naming P R N D M, and `SelectorFace` lights the current
one. 4WD mode, drive mode and the wipers all work the same way.

That works because the set is **closed and known when the catalog loads**. A destination name is
the opposite: an open set, arrived at at runtime, cardinality one, different every trip. It cannot
be a `states` table — and that, rather than any purity about types, is why it does not ride the
existing rails.

So the criterion used here is the one that falls out of `prefer`: *can you name the vehicle signal
that would one day replace this row?* Distance and time remaining, yes. A straight line and a
bearing, no, but they are still numbers of the same kind and are admitted with the field left out.
A name, a website, a phone number and opening hours, not even close.

### Where text will go instead

`CardValue.cs` already anticipates it in its own comment — "when components start contributing
sources of their own, this is where a third kind arrives, and nothing above it changes". A text
readout is a **third `CardSource`** with its own small face, not a widened sensor. That is the next
piece of work and is deliberately not this one.

### Why derived values are allowed in a catalog of measurements

This is the honest cost of the decision. Every other row in `sensors.device.json` is something the
tablet measures; these six are sums over things the dash already knows. `SensorDefinition`'s own
documentation said a row with no `prefer` "would be a surprising thing to add", and four of these
are exactly that — so the documentation is corrected rather than quietly contradicted.

What keeps it honest is decision 4. A derived value is never better than the fix beneath it, so it
reports that fix's quality rather than inventing one, and a number worked out from a synthetic
track reads as synthetic on screen. The alternative — a parallel catalog of derived values — would
have doubled the picker, the card editor and the stage layout vocabulary to express a distinction
nobody looking at a dash cares about.

### Why the countdown refuses when off route

`RouteProgress` finds the nearest point of the route's geometry and adds up what comes after it.
That is a real measurement while the truck is on the line and a meaningless one the moment it is
not: a wrong turn would otherwise read as a cheerfully shrinking distance to a place being driven
away from, which is the single failure this project refuses everywhere else. A hundred and fifty
metres is wide enough for a divided highway drawn down its centre line and the GPS's own error on a
bad fix, and narrow enough to catch a wrong turn within a block or two.

It is also the honest limit of the technique, and Q47 is the part it does not solve.

### What this is not

Not guidance. There is no turn announced, no re-route, no opinion about which way the truck is
pointing, and no voice. ADR-0058's bundled engine is still the thing that does all of that, for
both reasons it always was: a public router may not be polled to follow a vehicle, and a dead zone
ends the conversation. What this adds is the countdown, and the refusal.

## Consequences

- Six new values in the card picker under **Navigation**, usable on any card and in any stage
  layout, with no change to either.
- The **off-route limit is a constant, not a setting.** It is picked for a highway drawn down its
  centre line; a service road running further from the line than that reads as off route.
- **Distance is in kilometres and time in minutes, with no unit choice.** `DisplayUnits` has no
  km/mi conversion, and adding one is a separate decision from this — `RouteSummary` is metric by
  deliberate choice.
- **A dash with no GPS has no navigation rows behind it.** `CompositeDeviceSensors` is only built
  when there is a location source, so the rows read `NO NAVIGATION`, which is the truth.
- **The route's geometry is walked on every poll**, four times a second, O(n) in its vertices. A
  town errand is a few hundred points and this is nothing; a cross-country route is not measured,
  and if it ever matters the segment lengths are the thing to cache.
- Nothing here has been seen on glass. The arithmetic and the provider are tested; the walkthrough
  in `docs/08-in-vehicle-testing.md` is how the rest gets confirmed.

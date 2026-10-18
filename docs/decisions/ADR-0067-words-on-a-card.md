# ADR-0067 — Words on a card

**Status:** Accepted · 2026-10-16
**Implements:** [ADR-0066](ADR-0066-navigation-as-sensors.md)'s "where text will go instead" — a
third `CardSource` rather than a widened `SensorReading`.
**Builds on:** [ADR-0012](ADR-0012-widgets-and-applets.md) (a card is an instance of a definition),
[ADR-0065](ADR-0065-address-search.md)'s addendum (a place knows things about itself),
[ADR-0056](ADR-0056-multi-state-signals.md) (the dash's existing way of saying words).

## Context

[ADR-0066](ADR-0066-navigation-as-sensors.md) put the map's *numbers* on cards and left its words
where they were, in the route strip and the map's popup. The reason given was specific: a sensor
reading is a `double`, NaN is how the dash says "nothing", and widening that contract would make
every face, unit conversion and threshold in the dash check which kind of value it had.

That argument was about the sensor layer, and it holds. It was never an argument that a card cannot
show a word — `CardValue.cs` has carried a comment since B4 saying a third source would arrive here
and that nothing above it would change. This is that source.

## Decision

1. **`CardSource.Readout`**, beside `Signal`, `Sensor` and `Component`. A card stores it the same
   way and reloads as one.
2. **A second interface, `ITextValue`, not a wider `ICardValue`.** `ICardValue.Value` stays a
   `double` and every consumer of it is untouched.
3. **Exactly one place asks which kind it has:** the card's own `Text`, before the NaN guard that
   is right for every other card and would otherwise render every readout as a dash.
4. **The readout catalog is declared in code, not JSON** — the only one of the three that is.
5. **Seven navigation readouts:** the destination's name, address, kind, opening hours, telephone
   and website, and the route's summary.
6. **A definition is a function, not a value.** The card asks; the closure reads whatever the map
   holds now.
7. **Words get their own typography** — a second text block in the card face at 18px, wrapped to
   two lines then ellipsised, rather than the value's 30px single line.
8. **A readout has no unit, no range, no rate and no number format**, and cannot be drawn as a
   bar. A card switched onto one loses a stored unit rather than printing °C beside a shop's name,
   and the editor greys those rows with a reason rather than hiding them — an option that silently
   vanishes looks like a bug, and one that is offered and does nothing is worse.
9. **Live when there are words, Unavailable when there are not** — never Stale, never Simulated.
10. **Blank counts as absent.** OSM leaves fields empty as often as it leaves them out.

## Reasoning

### Why a second interface rather than a wider one

The whole of ADR-0066's argument survives if the string arrives beside the number instead of
inside it. `ICardValue` keeps meaning "a quantity, its unit, and how far it can be trusted";
`ITextValue` means "words". `ReadoutCardValue` implements both, because a card still wants the
quality border and the footer line, and reports `double.NaN` and an empty unit — which is not a gap
but the dash's own vocabulary for "there is no number here".

The cost of the other design is concrete rather than theoretical. `WidgetCardViewModel.Text`,
`BarFraction`, `DisplayUnits.Convert`, `ValueChoice.HasRange`, every gauge face and the warning
thresholds all currently know that a value is a number. A nullable string on the shared interface
makes each of them a place where somebody can forget to check. One cast in one property does not.

### Why the catalog is code where the other two are files

Signals and sensors are files because [ADR-0004](ADR-0004-request-arbiter.md) and
[ADR-0052](ADR-0052-nothing-vehicle-specific-shipped.md) are about the user adding things: a PID
discovered in the truck, a channel a particular tablet happens to have. Adding one should be an
edit, not a build.

A readout is not that kind of thing. It is a particular fact the dash already holds, reachable only
through the object that holds it, so a JSON row naming one would need a closure compiled in to go
with it — two places to keep in step, for no gain. Declaring the pair together is the honest shape,
and it keeps the list short on purpose: every entry is a decision that a fact is worth a card, not
a row anyone can add.

### Why a function rather than a value

Cards outlive every search. A destination chosen a minute after the dash came up has to reach a
card that already exists, and a pushed value would need either the map to know about the dashboard
or the dashboard to subscribe to the map. A closure read on the card's own poll needs neither, and
is the same trick the search and the directions already use for the opposite direction.

### Why words are drawn separately

"25.6" is four characters and "Tim Hortons, Portage Avenue" is twenty-seven. The value's
typography — 30px, bold, one line, no wrapping — would clip every readout in the catalog. Giving
words their own block rather than resizing the value means nothing about the way every other card
draws its number changed, which was the condition for doing this at all.

Two lines then ellipsis, because a card is read at a glance and a third line is a paragraph.

### Why never Stale

A destination is what the person chose. It is not a measurement of a world that could have moved
on, so there is no age at which it becomes doubtful — where the *numbers* derived from it carry the
GPS fix's quality precisely because they are about a world that moves (ADR-0066's decision 4). The
name of the place does not go stale; only the distance to it does.

## Consequences

- Seven new entries in the card picker under **Navigation**, beside the six numeric ones.
- **Cards only. Stage layouts do not get readouts**, because a layout's gauge reads through
  `SensorService` and a readout is not in it. Worth doing and not done here.
- **The two Navigation groups in the picker are now mixed** — six numbers and seven words under one
  heading, distinguishable only by the second line of each row. Splitting them is a picker change
  rather than a model one.
- **A long value is clipped at two lines**, so a website with a path may not be readable in full. A
  card is not where a URL is read.
- **`ReadoutCatalog.Empty` is what a dash with no map gets**, and the three-argument
  `ValueChoice.All` and `CardValueFactory` both default to it, so nothing that called the old
  two-argument forms changed.
- Unproven on glass: the typography, the clipping, the greyed editor rows, and whether a
  destination name on a card is actually wanted once it is there. The walkthrough in
  `docs/08-in-vehicle-testing.md` asks.

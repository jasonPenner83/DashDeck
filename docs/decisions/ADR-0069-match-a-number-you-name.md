# ADR-0069 — The ID hunter matches a number you name, and a passenger may read it on the road

**Status:** Accepted · 2026-10-20
**Extends:** [ADR-0044](ADR-0044-id-hunter.md) — the hunter's match method, from the menu rather
than only from the checklist. **Amends:** ADR-0044's "every step checks the truck is parked first",
for one kind of step, under one condition.

## Context

The hunter finds where the truck keeps a number three ways, and **match** is the one for numbers a
person can read: type what the cluster shows, and `MatchRanker` finds the identifiers that give it
under common scalings and unit changes. But every match is a **target in `targets.json`**, written
in advance, with a fixed number of rounds. Distance to empty, average economy and tyre pressures are
there. Anything else means editing JSON first.

Jason wants to look for things FORScan does not list, starting with **cruise control's set speed**.
That one has a property no checklist target has: **it only exists on the road.** Cruise sets above
a minimum speed, and the hunter refuses everything while the truck is moving. He can make it take
known values — set 60 km/h, then 100 — and a passenger can type them while he drives.

The parked rule has two reasons, and they are different:

1. **A sweep takes the adapter for minutes** (ADR-0035): thousands of requests, a module asked
   identifier by identifier.
2. **The hunter is a keyboard program**, and nobody should be typing into one while driving.

A **reading** is neither. It is one pass over the identifiers a sweep already found — tens of
requests, a second or two — which is what FORScan's live data does on every drive. And the second
reason is about who is at the keyboard, not about the truck moving.

## Decision

1. **M on the main menu: match a number you name.** Name it, choose a module and ranges, sweep, then
   type the number each time it changes. Open-ended: up to 12 readings, **R** to rank whenever
   enough have been taken. The same `MatchRanker`, `MatchTally`, ranking table, live check and
   `findings.csv` row as a checklist match. The ranking and reporting are now one method both use.
2. **The sweep stays parked, always.** M goes through the same `PrepareAsync` and `SweepAsync`, with
   the same refusals.
3. **A reading may be taken while moving, but only by a passenger who has said so.** The first time
   a reading meets a moving truck, the hunter says that readings on the road are a passenger's, and
   asks for the word **PASSENGER** to be typed. Anything else refuses the reading and keeps the sweep,
   so it can be taken again once parked. Once typed, it holds for the rest of that match and is asked
   again at the next. The rule is one function, `ReadingRule.Decide(speed, passenger)`, tested as a
   table: parked reads; moving reads only with the word; an unanswered speed asks before reading, as
   `ParkedAsync` already did.
4. **A repeated number is not a reading.** The same value again proves nothing new, so it is refused
   with a reason. Only a changed number thins the list.
5. **Progress is counted in identifiers, not decodes.** One identifier read as unsigned and as signed
   is one place. *1 still fits all 2 readings* means one place.
6. **The synthetic truck has a cruise set speed** (`7E0` `4105`, hundredths of a km/h, invented like
   everything else there), set through a new cabin control. Under `--simulate`, M sets it to a new
   speed at each reading and says which, so the whole thing can be practised at a desk.

## Consequences

- Anything Jason can read, he can look for without editing a file: a set speed, a trip meter, a
  setting a screen shows.
- **A typed word is not proof of a passenger.** Nothing can be. The word makes the choice deliberate
  and on the record, which is as far as a terminal program can go. The driver's side of the rule is
  in the walkthrough as plainly as here: the driver drives, the passenger types.
- **Only M relaxes the rule.** Checklist targets, listens, follows and asks still refuse to move. A
  follow is many passes over minutes. Widening the exception to it would be its own decision, made
  when something needs it.
- The live check after ranking is not speed-gated, as it was not before. It reads one identifier and
  is reached by a person who has just ranked a match. On the road, that person is the passenger who
  typed the word.
- Findings carry the speed of each reading in the match capture (`km_h`), so a reading taken on the
  road is visibly one.

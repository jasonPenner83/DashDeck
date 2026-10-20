# ADR-0069 — The ID hunter matches a number you name, and runs whether or not the truck is moving

**Status:** Accepted · 2026-10-20
**Extends:** [ADR-0044](ADR-0044-id-hunter.md) — the hunter's match method, from the menu rather
than only from the checklist. **Supersedes:** ADR-0044's "every step checks the truck is parked
first, and a watch stops if it moves". **Leaves alone:** ADR-0035's refusal of the *dash's* own
sweeps while moving, which protects the gauges rather than the person.

## Context

The hunter finds where the truck keeps a number three ways, and **match** is the one for numbers a
person can read: type what the cluster shows, and `MatchRanker` finds the identifiers that give it
under common scalings and unit changes. But every match is a **target in `targets.json`**, written
in advance, with a fixed number of rounds. Distance to empty, average economy and tyre pressures are
there. Anything else means editing JSON first.

Jason wants to look for things FORScan does not list, starting with **cruise control's set speed**,
which only exists on the road: cruise sets above a minimum speed. The hunter refused every step while
the truck was moving.

A first version kept that refusal for sweeps and let readings through once the person typed
*PASSENGER*. Jason's answer was to take the refusal out altogether: it is his truck and his tool, he
will decide who works the laptop and when, and the program should not decide it for him. What the
refusal guarded was a choice, not a fault: the hunter writes nothing to the truck, and reads are what
FORScan makes on every drive.

## Decision

1. **The hunter no longer checks whether the truck is moving.** The module scan, sweeps, listens,
   follows, asks, the CAN database check and M all run at any speed. A follow no longer stops when
   the truck moves. `ParkedAsync` is gone. What a target *needs* (ignition on, a cold engine, a door
   to open) is still said up front, because that is about getting a good answer.
2. **M on the main menu: match a number you name.** Name it, choose a module and ranges, sweep, then
   type the number each time it changes. Up to 12 readings, **R** to rank whenever enough have been
   taken. The same `MatchRanker`, `MatchTally`, ranking table, live check and `findings.csv` row as a
   checklist match. The ranking and reporting are now one method both use.
3. **A repeated number is not a reading.** The same value again proves nothing new, so it is refused
   with a reason. Only a changed number thins the list.
4. **Progress is counted in identifiers, not decodes.** One identifier read as unsigned and as signed
   is one place. *1 still fits all 2 readings* means one place.
5. **Each reading's speed is recorded** in the match capture (`km_h`), so a reading taken on the road
   is visibly one when the findings are looked at later.
6. **The synthetic truck has a cruise set speed** (`7E0` `4105`, hundredths of a km/h, invented like
   everything else there), set through a new cabin control. Under `--simulate`, M sets it to a new
   speed at each reading and says which, so the whole thing can be practised at a desk.

## Consequences

- Anything Jason can read, he can look for without editing a file, wherever the truck has to be for
  the number to exist.
- **A sweep on the road takes the adapter for minutes.** That costs nothing here: the hunter holds
  the adapter alone and the dash is closed. It is why the dash's own sweeps (ADR-0035) still refuse
  to start while moving, since there the gauges would go stale. That rule is unchanged.
- Read-only is unchanged. Requests are still mode 22 and standard mode 01 reads, and listening still
  sends nothing.
- The walkthrough describes how to do cruise without saying who should type. That is Jason's call.

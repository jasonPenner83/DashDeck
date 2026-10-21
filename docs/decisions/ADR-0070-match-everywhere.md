# ADR-0070 — M looks in several modules and on the bus at once

**Status:** Accepted · 2026-10-20
**Extends:** [ADR-0069](ADR-0069-match-a-number-you-name.md) — M, match a number you name, which
searched one module.

## Context

M finds where the truck keeps a number Jason can read, starting with cruise control's set speed. As
built in ADR-0069 it asked for **one module**, swept it, and matched readings there. That works
when you know where to look. For cruise you mostly don't:
- the engine computer runs it on a truck without adaptive cruise;
- a radar module runs it on one with;
- the cluster shows it, so may hold a copy;
- whichever module owns it may simply **broadcast** it on the bus, never answering a request at all.

A set speed only exists on the road. So every module tried meant another drive with two set speeds,
and broadcasts were only reachable through L, a separate listen by states that knows nothing about
numbers.

## Decision

1. **M takes any number of modules:** one (`7E0`), several (`7E0, 720, 760`), **ALL**, or **NONE**.
   ALL means every module S found; if S has not been run, M runs it first. The ranges are asked once
   and swept in each module, and the time estimate covers them all. A module that gives nothing in
   those ranges is left out, with a line saying so.
2. **M can listen as well**, and does unless told not to. At every reading, before the modules, it
   listens to each bus for a few seconds (`ListenSeconds`, 3 by default). What it hears becomes a
   `BroadcastSnapshot`: every frame, its last bytes, and which bytes changed during the listen.
3. **Only steady bytes count in a broadcast.** A number someone reads off a display is steady while
   they read it; counters, checksums, rpm and road speed are not. Any field covering a byte that took
   two values in the window is dropped. That one rule does most of what L's separate noise-learning
   step does, with no extra step.
4. **One table across every place.** Each place (a module, or a bus's broadcasts) has its own tally,
   so places cannot vouch for each other the way the four tyres do. The table merges them by
   score, with **WHERE** saying which module or bus. It has **one row per place**: an identifier
   that fits read as unsigned, signed or one byte of it is one place, shown with its best decode.
   Progress counts places too: *2 places still fit all 2 readings*.
5. **The live check works for both.** A module candidate is asked over and over, as before. A
   broadcast candidate is heard alone, its field printed decoded each time it changes.
6. **Findings say which it was.** A broadcast row fills `frame_id` and leaves `module` and `did`
   empty, with the note `broadcast`. The capture records where each hit was.
7. **The synthetic truck broadcasts the cruise set speed too:** HS-CAN `3F2`, halves of a km/h in
   bytes 1–2, beside a counter in byte 0. It is invented. Practice at a desk finds both places, and
   the tests prove the counter is never offered.

## Consequences

- One drive with two set speeds covers every candidate place. The cost moves to before the drive:
  sweeping ALL in a wide range can take many minutes. Sweep while parked; the sweep is kept for the
  rest of the session.
- **Finding a broadcast is a step, not the end.** The dash still reads by asking (ADR-0044), so a
  value that is only broadcast cannot go on a card yet. What the find gives is the frame and the
  sending bus, and usually a strong hint which module to ask for the same value. Reading broadcasts
  on the dash is its own decision, when there is a confirmed frame worth it.
- A reading takes longer: a few seconds of listening per bus plus each module's pass. The hunter
  says how long when the places are chosen.
- The checklist's match targets are unchanged. They still name one module and use the per-label
  ranking that lets four tyres vouch for each other.

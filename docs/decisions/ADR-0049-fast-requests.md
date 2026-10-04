# ADR-0049 — Fast requests: the engine computer's standard values, filtered, with a count of one

**Status:** Accepted · 2026-10-04
**Builds on:** [ADR-0004](ADR-0004-request-arbiter.md) (one serialised adapter, one plan),
[ADR-0035](ADR-0035-module-discovery.md) (receive filters per module),
[ADR-0048](ADR-0048-serial-tap.md) (the tap that measured it). **Answers in part:** risk R1, Q12.

## Context

The dash measured **~19 requests a second** for everything on the screen (Q12): 52 ms per request.
The serial tap then recorded FORScan on the same truck, adapter and USB link. Reading coolant with
the same broadcast `0105`, FORScan averaged **19.6 ms**, about 50 a second.

The difference is in how FORScan asks:
- **`ATCRA7E8`:** it listens for the engine computer's answer alone;
- **`01051`:** a count of one after the request, so the adapter stops waiting the moment that answer
  arrives.

DashDeck sent `0105` unfiltered. The adapter then waited out its timeout in case another module also
answered. Most of the 52 ms was that wait.

The count has risks:
- **Only one answer.** A value that only another module gives would be hidden by the filter.
- **A busy reply** (`7F 01 78`) ends the wait before the real answer comes.
- **A multi-frame answer** might be cut short.
- **Old or clone adapters** may not understand the count.

## Decision

**Ask the narrow set FORScan's way, and everything else as before.**

- **Eligible** (`ElmAdapter.IsFastEligible`):
  - mode 01;
  - on the main bus, to the broadcast;
  - not a supported-PID bitmap (`00`, `20`, `40`…).

  Every mode 01 answer fits in one CAN frame, so a count of one cannot cut it short.
- **Stays as it was:**
  - mode 22 identifiers, the VIN and module requests;
  - pins 3/11;
  - the bitmaps, where several modules answer and a scan wants to hear them all.

  Ford identifiers join once their answer lengths are known.
- **Only on adapters that understand it:** an ELM327 v1.3 or later, or any STN chip (the OBDLink's).
  A `?` reply at run time turns it off until the next configuration.
- **Falling back:**
  - **NO DATA the fast way** is asked again the slow way.
  - **Hidden answers:** if that happens three times in a row for one value, and the slow way answers
    each time, that value goes the slow way for good: another module answers it, and the filter
    would hide it. One dropped answer is not evidence. The first build sent rpm slow for good after a
    single drop, and the filter then toggled around every request, which was slower than not
    filtering at all.
  - **A busy reply alone** is asked again the slow way, which waits for the real answer.
  - **A late answer** that lands on the next request cannot be read as that request's value: the
    parser checks the answer names the PID that was asked for.
- **The filter is set once and kept.** It is cleared (`ATAR`) only before a slow broadcast request.
  A module's own filter replaces it, and an adapter reset forgets it.
- **The ceiling the arbiter may plan to rises from 19 to 45** while fast requests are active.
  The arbiter still plans against the measured service time, never above the claim.
- **A switch, on by default:** Settings ▸ Vehicle ▸ FAST REQUESTS, applied at launch (RESTART NOW).
  The section shows how many requests went fast and how many were asked again. The ID hunter, Bring-up
  and the debug console leave it off.
- **The simulator models it:** a counted, filtered request comes back in 20 ms instead of 52. On the
  realistic synthetic truck, the whole pipeline measures 47 req/s against 19.

## Consequences

- **About twice the readings for the same screen**, if the truck agrees with the simulator. That
  means smoother rpm and speed, and more cards per page before each slows. The `req/s` on the status
  strip is how to see it.
- **A value another module answers costs extra.** Its first three requests are asked twice before it
  settles on the slow way. After that, each one needs an `ATAR` and an `ATCRA` around it, 15 ms or so
  on the real adapter.
- **Wrong numbers stay ruled out by the same checks as before:**
  - only single-frame answers are counted;
  - every answer must name its PID;
  - anything doubtful is asked again the old way.
- **Not done until walked through in the truck** (docs/08). The busy and multi-module behaviour only
  real modules show.

## Rejected

- **Counting everything,** mode 22 and the VIN included: a count of one on a multi-frame answer is not
  something to guess about.
- **Addressing `7E0` directly instead of filtering the broadcast:** FORScan's coolant capture shows the
  broadcast is just as fast once filtered and counted. Physical addressing would also need flow
  control for no gain.
- **Lowering the adapter's timeout (`ATST`) instead:** this shortens every wait, including for modules
  that are slow but present. Those would read as NO DATA, a blank that looks like "not supported".

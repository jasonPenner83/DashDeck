# ADR-0047 — No scroll strip: a hosted program gets the whole stage

**Status:** Accepted · 2026-10-04
**Supersedes:** [ADR-0046](ADR-0046-scroll-strip.md) (the scroll strip beside a hosted program).
**Keeps:** [ADR-0021](ADR-0021-owned-not-reparented.md) — programs are owned and placed over the
stage, with their own input.

## Context

ADR-0046 put a 64 px strip beside every hosted program. Dragging on it turned finger travel into
mouse-wheel scrolling, for programs such as NuvioDesktop that read a finger as a mouse. The owner
saw it on the tablet and did not want it.

## Decision

**The strip is removed.** A hosted program is placed over the whole stage again, as before
ADR-0046.

The following go with it:
- the `scrollStrip` and `scrollBy` launcher fields;
- `WheelSteps`, `ScrollStripView` and `WheelSender`;
- the `PostMessage`, `SendInput` and `WindowFromPoint` declarations;
- the strip's in-vehicle walkthrough.

A launcher file that still carries `scrollStrip` or `scrollBy` loads: unknown fields are ignored.

## Consequences

- NuvioDesktop's lists still do not scroll under a finger. That is the program's own behaviour, the
  same with DashDeck closed. Dragging its scroll bar still works. A better fix belongs in the program,
  or in a version of it that handles touch.
- If touch scrolling for a desktop program comes back, it needs a way that adds nothing to the screen.
  ADR-0046's rejected alternatives explain why the obvious ones — a touch hook or a transparent
  overlay — do not fit.

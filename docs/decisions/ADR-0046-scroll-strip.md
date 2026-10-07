# ADR-0046 — A scroll strip beside a hosted program that will not scroll for a finger

**Status:** Superseded by [ADR-0047](ADR-0047-no-scroll-strip.md) · accepted 2026-10-03, withdrawn 2026-10-04
**Builds on:** [ADR-0021](ADR-0021-owned-not-reparented.md) (programs are owned and placed over the
stage) · [ADR-0038](ADR-0038-stage-launcher-file.md) (the launcher file). **Keeps:** the touch trap in
CLAUDE.md (manipulation, not mouse events, on anything a finger drags).

## Context

On the truck, NuvioDesktop's lists do not scroll under a finger. This is not DashDeck: the program
behaves the same with DashDeck closed. It is a Java desktop program, and like many desktop programs it
reads a finger as a mouse — a drag is a press and a move, which selects or does nothing, never a
scroll. The web version scrolls, but the owner prefers the desktop program for its playback.

DashDeck cannot change how another program reads touch, and does not handle the input of a program
on the stage (ADR-0021 — it keeps its own focus and input, which is the point). But every desktop
program scrolls for a mouse wheel.

## Decision

**A strip beside the program turns finger travel into wheel notches sent to it.**

- **Where.** A 64 px strip down one side of the stage while a program is **hosted** there. The program
  is placed over the rest of the stage, so the strip is never under it. Not shown while the program is
  starting or running outside the stage — there would be nothing beside it to scroll.
- **How it reads.** WPF manipulation with inertia: drag down and the page comes down with the finger;
  a flick carries on and slows. One notch per 40 px of travel (`WheelSteps`). Only whole notches are
  sent, with the remainder carried, because some programs round a smaller delta to nothing. A mouse
  drag or a wheel over the strip work too, for a desk.
- **Where it lands.** At the finger's height, just inside the program's edge beside the strip. You
  pick which pane scrolls by where you drag, as a wheel scrolls the pane under the pointer — and a
  scroll bar sits at that edge.
- **How it is sent.** There are two ways, and the launcher entry says which:
  - **`message`, the default.** A `WM_MOUSEWHEEL` is posted to the program's window at that point. It
    moves nothing and needs no focus.
  - **`input`.** A real wheel turn through `SendInput`: the pointer moves over the program, turns and
    moves back, all in one call so nothing falls between. This is for a program that ignores a posted
    wheel because it checks where the pointer really is.

  Which kind a program is cannot be known from here.
- **Configured per program** in the launcher file:
  - `"scrollStrip"`: `right` (default), `left` or `off`;
  - `"scrollBy"`: `message` (default) or `input`.

  An unknown value is named in the launcher's problems and the default is used. A program added in
  Settings ▸ Apps gets the default.

## Consequences

- Every hosted program loses 64 px of width, on the right unless it says otherwise. A program that
  scrolls well by touch can turn the strip off with `"scrollStrip": "off"`.
- **A horizontal row scrolls too.** A row that scrolls sideways may take a vertical wheel when it lies
  beside the finger. Dragging at the height of a gap between rows reaches the page.
- Touching the strip makes DashDeck the active window, so the program loses keyboard focus. Wheel
  scrolling does not need focus, so it still works.
- Nothing here reaches the vehicle. No new driver and no hook; two user32 calls are added
  (`PostMessage`, `SendInput`).

## Rejected

- **Turning a drag on the program itself into a scroll.** This needs a low-level touch hook across
  processes, or a transparent window over the program that swallows its taps. The first is the
  machinery ADR-0021 rejected. The second breaks every tap.
- **Windows' own touch-to-wheel settings.** There is no such system setting for programs that do not
  ask for touch. A per-program compatibility switch would be a registry write, which CLAUDE.md rules
  out.
- **The web version of Nuvio.** It is the owner's choice not to use it. The strip helps every
  desktop program, not one.

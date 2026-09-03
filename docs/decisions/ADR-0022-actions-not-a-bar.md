# ADR-0022 — Occupant controls are verbs in the menu, not a band of the stage

**Status:** Accepted · 2026-09-02
**Supersedes in part:** [ADR-0018](ADR-0018-fixed-stage-and-overflow-menu.md) — the fixed
four-band stage and the overflow menu stand. The one-band action bar does not.

## Context

ADR-0018 gave each occupant an optional action bar: one band, between its content and the
launcher, carrying video transport, web back/reload/home, and compass levelling. The reasoning
was sound — a child window draws over all WPF content whatever the z-order says, so controls
overlaid on video are invisible and untappable, and a real row was the only arrangement that
survived.

Living with it made the price obvious. **A quarter of the stage, permanently, to carry two
buttons.** An occupant with a bar got 513 pixels of content where it could have had 708. For
Nuvio that is a visibly smaller picture; for the compass it was a rose squeezed above a row
holding a control you press once, ever.

The trade was picture you look at constantly, for a control you use rarely.

## Decision

**The action bar is removed.** An occupant now gets the whole stage minus the launcher — 708
rather than 513.

**Occupants hand back verbs, not a view.** `IStageOccupant.Actions` returns
`StageAction(Caption, Invoke)`, and the shell decides where they live. Today that is the
overflow menu, which already exists and is a separate full-screen layer, so the airspace
argument that forced a real row does not apply to it.

**Levelling moves to Settings.** It is a calibration done once, parked, on flat ground — not
something reached for while driving.

**Video's seek bar is gone**, replaced by skip verbs: play/pause, back thirty seconds, forward
thirty seconds, restart.

**Captions are read when the menu opens**, so the video item says PLAY or PAUSE according to
what the player is actually doing.

## Reasoning

**Handing back verbs rather than a `FrameworkElement` is the part worth keeping.** It means
the shell owns placement, so moving these controls again costs nothing in any occupant — and
this is the second time they have moved. It also removes an occupant's ability to draw
arbitrary chrome into the shell's layout, which was a wider door than anything needed.

**Two taps for play/pause is a real cost, honestly incurred.** It was one. The judgement is
that a permanently smaller picture is worse than an extra tap on a control used a few times a
drive — and unlike the bar, this is reversible without touching any occupant.

**A slider was the one thing that could not survive**, because it has to be visible while
dragged and a menu is not. Fixed skips are the closest equivalent and are arguably better in a
moving vehicle: a thirty-second jump can be hit without looking, and a slider cannot.

**Levelling was never a stage control.** Putting it there was convenience — it was where the
compass was. Settings is where things you set once and leave belong, and it has room to say
*why* the mount reference exists, which the bar's one-line caption did not.

## Alternatives

- **Shrink the bar** to the launcher's 72 rather than a full band. Rejected: it would still
  cost picture permanently, and the launcher row is already spoken for.
- **Merge controls into the launcher row**, replacing the quick-launch buttons while an
  occupant has controls. Tempting and cheap in pixels — rejected because switching apps would
  then always need the grid, trading one frequent action for another.
- **Overlay controls on the occupant**, fading them out. This is what every media player does
  and it is exactly what airspace forbids: a child window or an owned top-level window both
  draw over WPF content regardless of z-order.
- **Keep the bar for video only.** Rejected: an occupant-specific exception to the layout is
  how a stage stops being predictable, which is the whole point of ADR-0018 fixing its height.

## Consequences

- **Play/pause is two taps.** The most likely thing to want revisiting, and the one to watch
  for on a real drive.
- **Seeking is coarse.** Thirty-second steps, no scrubbing. Enough for skipping an advert; not
  enough for finding a scene.
- **`ActionBar` and its styles are deleted**, and `CreateActionBar` is gone from the occupant
  contract — which is a simplification of a contract that is still unpublished, so it costs
  nothing outside this repository.
- **The menu now has two halves**, contextual and global, separated by a rule. It will get
  crowded if occupants grow many actions, and there is no scrolling in it yet.
- **F8 is answered differently than ADR-0018 answered it.** Occupants still have no chrome of
  their own and still cannot draw over their own content; they now have somewhere to put verbs
  instead, which was the actual need.

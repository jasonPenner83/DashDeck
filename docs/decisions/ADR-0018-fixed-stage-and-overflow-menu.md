# ADR-0018 — A stage that does not move, and a menu you can actually tap

**Status:** Accepted · 2026-09-02
**Supersedes in part:** [ADR-0015](ADR-0015-arranged-dashboard.md) — the row-flowing mechanism
stands, but the stage no longer varies its height, so in practice the widget region is always
two rows. **Resolves:** F8 (stage occupants have no chrome of their own).

## Context

First use in the truck, on the road. Four things came back, and three of them were about the
shell being *unstable* or *unreachable* rather than about any feature.

**The dash moved.** Occupants asked for the bands they wanted — video three, a map four — so
the cards below flowed between two rows and three whenever the stage changed. ADR-0015 treated
that flexibility as a feature and even resolved F9 with it. On a desk it looks adaptive. At
speed it reads as the dash rearranging itself while you are trying to read it.

**Editing the dash was unreachable.** Edit mode was a 600 ms hold on a card. It was built on
`PreviewMouseLeftButtonDown`, and the card strip sets `IsManipulationEnabled` for swiping —
which consumes touch before WPF ever promotes it to a mouse event. **The gesture could never
have fired from a finger.** It worked in every screenshot because screenshots are driven by a
mouse.

**Settings held a permanent quarter of the nav strip**, which is the only band reachable from
the driver's seat, for something set once and then left.

**The status strip was diagnostics.** Source label, scripted drive name, request rate — three
things a developer asks and nobody asks while driving.

## Decision

**The stage is always four bands.** `BandGrid.StageBands`, a constant. An occupant that wants
less picture takes an action bar instead of asking for fewer bands.

**Occupants may contribute an action bar**, one band tall, between their content and the
launcher. `IStageOccupant.CreateActionBar()` returns null by default. Video gets play/pause, a
seek slider and elapsed/total; the web occupants get back, reload and home; the compass gets
levelling and peak reset. An occupant with a bar has **513** of content — exactly what a
three-band stage used to give it.

**A three-dot menu in the status strip** opens a modal sheet: MODIFY WIDGETS, SETTINGS, CLOSE.
The long-press is deleted rather than repaired.

**Settings leaves the nav strip** and lives in that menu. The nav is the destination list (B3),
and Settings is not a destination you drive to.

**The status strip becomes a quick-info bar**: outside temperature and conditions on the left,
a compact `SIM` badge and the clock on the right, the menu button at the end. The diagnostics
move to Settings.

**One weather fetch for the whole application.** `WeatherService`, owned by the shell.

## Reasoning

**Stillness beats adaptiveness on a moving vehicle.** This is a straight reversal of a
judgement in ADR-0015, made on a desk and corrected by a road test. The flowing-rows machinery
was not wasted — it is what makes an action bar cost exactly one band, and `RowsIn` still
governs — but the *variability* was the problem, not the mechanism. F9's spare band is now
answered by the stage never being three bands rather than by rows growing.

**The action bar is the answer to F8, and it had to be a real row.** Every occupant so far
renders into a child window — LibVLC's video surface, WebView2's browser — and a child window
draws over all WPF content whatever the z-order says. Controls floating on a video are
invisible and untappable the moment it loads. That is the same lesson the launcher bar taught,
applied a second time, and it is why the bar sits beside the content rather than on it.

**A gesture that cannot be discovered is barely better than one that does not work.** The
long-press failed for a specific, findable reason, and it would have been a two-line fix to
handle `TouchDown` — but a hold with no affordance is undiscoverable even when it works. A
visible button in a fixed corner is what a touch screen in a vehicle wants.

**The status strip should answer questions you have while driving.** Weather and time qualify.
The request rate does not — and it is worse than useless there, because it looks like a load
figure and is actually the adapter's measured *capability*. Settings has room to say so.

**One fetcher, one backoff.** There were two — the clock face and the theme's Auto mode, on
separate timers — and adding weather to the status strip would have made three. Two fetchers
with independent backoff is precisely the shape that produced this project's worst bug: one
request per second, indefinitely, at a free keyless API, because a backoff timed from a success
that never came is not a backoff. Consolidating was not tidiness; it was removing the second
copy of a known trap before adding a third.

## Alternatives

- **Fix the long-press by handling touch events.** Cheap, and leaves the feature hidden.
  Rejected in favour of something visible; the menu was wanted anyway.
- **Keep Settings in the nav and put the menu elsewhere.** Rejected: the strip is the scarcest
  space on the screen, and B3 says what it is for.
- **Let occupants keep asking for bands, but clamp the widget region to two rows.** Would have
  stopped the cards moving while keeping video its 3/6 shape. Rejected because it makes the
  stage's height silently meaningless — the number would be declared, honoured, and then
  ignored downstream, which is worse than not having it.
- **A floating overlay for video controls** instead of a band. Rejected: airspace, above.
- **Leave the status strip alone and add weather to it.** Rejected — it was already at the
  point where nothing on it was worth reading at a glance.

## Consequences

- **`IStageOccupant.PreferredBands` is gone.** Occupants no longer negotiate size, which
  simplifies the contract that will eventually be published — but it also means a future
  occupant that genuinely needs a different shape has no way to say so, and would need this
  decision revisited rather than a property added back quietly.
- **An action bar is a whole band whether or not it needs one**, so the compass and web bars
  have a lot of air in them. Accepted for consistency: a bar that varied in height would put
  the stage back to moving, which is the thing this ADR exists to stop.
- **The dash is now always two rows of cards**, which caps a page at six 1×1 cards. Paging
  still works, and F13 (eleven signals) bites before this does.
- **Weather has a single point of failure.** If `WeatherService` stops, the status strip, the
  clock face and Auto day/night all lose their source together. That is the trade for not
  having three of them, and the failure is visible in all three places rather than silent.
- **Open-Meteo's failure mode is now legible.** It answers HTTP 200 with a plain sentence in
  the body when it is struggling; the fetch used to report `'U' is an invalid start of a
  value`, which named neither the service nor the problem. It now says which.

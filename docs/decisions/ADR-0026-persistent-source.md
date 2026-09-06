# ADR-0026 — A persistent audio/video source on the stage

**Status:** Accepted · 2026-09-06
**Amends:** [ADR-0025](ADR-0025-occupant-lifetime.md) — "replacing ends it" now has one exception,
below. Builds on the two-occupant airspace facts from [ADR-0021](ADR-0021-owned-not-reparented.md)
and the ▶ pill from ADR-0025.

## Context

ADR-0025 settled that an occupant lives as long as it is the stage occupant, and that *replacing*
it ends it. Right for two silent screens — but it means putting the GAUGES cluster up while
Spotify plays **stops the music**. The wanted behaviour is the phone's: music keeps playing in
the background while you look at something else, and you tap back to it.

## Decision

**Some occupants are audio/video *sources*** — VIDEO, SPOTIFY, MUSIC, NUVIO, STREMIO, phone
projection, and any user app the owner marks. The rest (GAUGES, CLOCK, COMPASS, MAPS, the Probe)
are not.

**A global setting, "keep stage audio playing when you switch," off by default** (it changes
ADR-0025's behaviour, so opt-in). With it on:

- Switching from a source to a **non-source** keeps the source **alive and playing, hidden**
  behind the new screen — it does not end it.
- Switching to **another source** replaces it. One source at a time.
- The **▶ pill** (ADR-0025) shows the backgrounded source and taps back to it, bringing it
  forward without recreating it — so it keeps its place and its sound.

**The mechanism is two content slots, and the point is that neither is ever reparented.** The
source lives in its own host for its whole life, and switching to a screen only **collapses** it
(alive, still playing) rather than moving it. A live WebView2 or owned window that was reparented
would reload and drop its audio — so it never is. At most one slot is visible at a time, so two
child windows never draw at once and the airspace rule (F8) is not stressed.

**The switch rule is one pure function** — `StageSwitch.Decide(keepAudio, frontIsSource,
incomingIsSource, incomingIsBackgroundSource)` → `{ BringForwardSource,
BackgroundSourceShowIncoming, ReplaceWithSource, ReplaceFrontKeepBackground }` — because the view
model that runs it cannot be unit-tested without the whole pipeline (F7). The tricky part of the
feature is that table, so that is the part with tests.

**User apps declare it per app**, a "keep playing in background" toggle in the APPS editor, off
by default — a launched program is not assumed to make sound. Built-in sources are classified in
code (they are known).

## Reasoning

**Keep-playing is the phone model applied one layer deeper.** ADR-0025 already keeps an occupant
alive when you *leave* the stage (nav, a full-screen view). This extends the same instinct to
*switching*: a silent screen is another way of looking elsewhere, not a demand to stop the music.
Making it a setting, off by default, keeps ADR-0025's simpler rule as the out-of-the-box
behaviour for anyone who does not want background audio.

**Never reparenting is the whole ballgame.** The obvious implementation — one stage slot, move the
source view aside when a screen comes up — reloads the source, which for a browser or an owned
app means losing the song and the position. Giving the source its own permanent host and only
toggling visibility is what makes "keeps playing where it left off" true rather than aspirational.

**Replacing a source with a source still stops the old one.** Two players fighting over the
speakers is worse than a clean handover, and the stage has always held exactly one picture. So
the exception to ADR-0025 is narrow and deliberate: only a source-behind-a-screen survives.

**Classification lives on the option, not the occupant kind.** SPOTIFY and MAPS are both a
`WebStageOccupant`; one makes sound and one does not. So "is a source" is a property of the
configured option (and, for user apps, of the entry), not of the occupant class or its
`StageKind`.

## Alternatives

- **Always keep audio (no setting).** Rejected: it silently changes ADR-0025 for everyone, and
  someone who expects "pick gauges, music stops" should get that until they ask otherwise.
- **One slot, reparent the source when backgrounded.** Rejected: reloads the player and drops the
  audio — the exact thing the feature exists to prevent.
- **Pause the source instead of hiding it.** There is no uniform pause across WebView2, an owned
  native window and phone projection; "pause" would be a lie for the ones that cannot honour it.
  Hiding-but-alive is universal; pausing is left to the occupant's own controls, reached via the
  pill.
- **Classify by `StageKind`.** Rejected: MAPS (Web) is not a source and SPOTIFY (Web) is, so kind
  is the wrong axis.
- **A per-occupant toggle for the built-ins too.** More control, more setup; the built-in sources
  are known, so they are classified in code and only the unknowable user apps get a checkbox.

## Consequences

- **The stage now holds up to two occupants**, a source and a screen, each in its own host. The
  view model tracks both and disposes both on shutdown; a backgrounded source is still ours to
  close.
- **"Keeps playing" is verified by eye, with the real apps.** The synthetic can prove the occupant
  is not disposed and the unit tests prove the switch rule, but only Spotify or a real video can
  prove a collapsed WebView2 or a hidden owned window keeps *singing*. That confirmation is a
  truck-seat check, like every other airspace claim (ADR-0020).
- **The ▶ pill now has two jobs**: return to an occupant hidden behind a full-screen view
  (ADR-0025) and bring a backgrounded source forward (this ADR). It names whichever is hidden and
  taps to it.
- **Video backgrounded plays audio with no picture.** Deliberate — it is "audio/video", and a
  movie's sound continuing while you glance at the gauges is the same want as music. Tap back for
  the picture.
- **F21 is still open**: this decides how long stage audio lasts, not which speaker projected
  audio comes out of.

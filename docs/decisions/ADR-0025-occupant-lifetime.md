# ADR-0025 — An occupant lives as long as it is the stage occupant

**Status:** Accepted · 2026-09-06
**Relates to:** [ADR-0018](ADR-0018-fixed-stage-and-overflow-menu.md) and the "phone model" of
Q17 (a full-screen view covers the stage, which keeps running underneath). Resolves **F22**.

## Context

Audio started on a stage occupant — Spotify or Apple Music in WebView2, Stremio or a user's
app, phone projection — had no defined lifetime, and the three ways to leave the stage
disagreed:

- **Navigate** to STEREO/CLIMATE: the stage is a layer and nav switches only the region below
  it (B2), so the occupant keeps running **and stays visible** in the top four bands. Fine.
- **Open a full-screen view** (Settings, the card editor, a component detail): the occupant
  keeps running but its view is hidden (`IsOccupantVisible` goes false). It plays on with
  **nothing on screen to say so** — audio with no visible source.
- **Pick a different occupant**: `ShellViewModel.SetStage` disposes the old one, which for a
  native app kills the process **mid-song**.

Read as three lifecycles it looks incoherent. It is not: it is one rule that was never stated
or made visible.

## Decision

**An occupant lives exactly as long as it is the stage occupant.**

- **Leaving keeps it.** Navigating below the stage, or opening a full-screen view over it, does
  not end the occupant — the stage is a persistent layer (Q17/B2). This is unchanged; it is the
  right behaviour, and both of those paths already did it.
- **Replacing ends it.** Choosing another occupant disposes the previous one. Also unchanged,
  and also right: the stage holds exactly one thing, and asking for a new one is asking for the
  old one to stop. Keeping the old app's audio alive while a new occupant plays would be two
  players fighting over the speakers, which is worse than a clean stop.

**The one gap — audio with no visible source — is closed by showing it, not by changing the
lifecycle.** While a full-screen view hides a running occupant, the always-visible status strip
carries a small **▶ NAME** pill that names what is on the stage and taps back to it
(`StageRunningButHidden` + `ReturnToStageCommand`). Tapping closes the covering view and returns
to the DASH, where the stage shows again and the occupant's own controls take over. Navigating
to STEREO/CLIMATE needs no pill because the stage stays visible there.

## Reasoning

**The lifecycle was already correct; only its visibility was wrong.** The instinct on seeing F22
is to make all three paths agree by changing one of them — most obviously, to stop the audio when
a full-screen view opens. That is the wrong fix: it breaks the phone model that Q17 deliberately
chose, where opening Settings does not tear down what is on the stage. A person who opens Settings
to nudge a colour does not expect their music to cut out. So the answer is to keep playing and
**say so**, which is also the rule the rest of this dash follows — a state is rendered, never
hidden (C7).

**Replacing-stops-it is not a rough edge to file down.** It is the only coherent meaning of
"put something else on the stage." The abruptness is real but inherent; softening it with a
confirmation would put a modal between the driver and a one-tap action, which is its own cost.

**The status strip is the only place this can live.** It is the one region that stays visible
over every full-screen view (Q17), which is exactly the situation the pill exists for. The
launcher bar would do, but it is on the DASH and so gone precisely when the pill is needed.

## Alternatives

- **Stop the occupant when a full-screen view opens.** Rejected: breaks the phone model (Q17);
  a glance at Settings should not end what is on the stage.
- **Suspend (pause) the occupant while hidden, resume on return.** Tempting, but there is no
  generic pause across WebView2, a native app and phone projection — each has its own, and some
  have none — so "suspend" would be a lie for the cases that cannot honour it. Pausing is left to
  the occupant's own controls, reached by tapping back.
- **A full transport (play/pause/stop) in the status strip.** More than F22 asked for, and it
  presumes a control surface the occupants do not uniformly expose. The pill returns you to where
  those controls already are. A stop was offered and declined in favour of tap-to-return.
- **Also show the pill for silent screens (gauges, clock, compass).** It shows for any hidden
  occupant, because "there is a running thing back here, tap to return" is honest for all of them
  and cheaper than a per-occupant "does this make sound" test that WebView2 and a native window
  cannot answer anyway.

## Consequences

- **A new always-visible affordance appears only in a specific state** — a running occupant
  behind a full-screen view — and is hidden otherwise, so the ordinary bar is unchanged.
- **The pill reads "playing" (▶) even for a paused or silent occupant**, because play/pause state
  is not observable across the occupant kinds. It means "the active stage occupant, tap to
  return", not "audio is coming out right now". Accepted as the honest limit of what is knowable.
- **Verification is by eye** (ADR-0020): `ShellViewModel` is not unit-tested without the whole
  vehicle pipeline and a WPF element for `StageContent` (F7), and an owned occupant window does
  not appear in a `--shot`. The pill itself does render in a shot, and was confirmed there.
- **F22's harder cousin, F21** (where projected audio comes out at all) is untouched; this
  decides how long stage audio lasts, not which speaker it uses.
- **If MAP ever becomes an occupant rather than a nav destination**, navigating to it becomes a
  *replacement* and would stop the music by this same rule — which is consistent, and worth
  remembering when that change is made.

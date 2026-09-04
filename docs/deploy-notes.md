# Deploy notes — the build on the tablet

Operational notes for the self-contained build carried to the truck. Updated whenever the
deploy is refreshed. This is not release history (that is git tags and
[05-releases-and-branching](05-releases-and-branching.md)); it is "what is in the folder on the
Surface right now, and what to check."

## How to deploy

```bash
pwsh -File publish.ps1 -Shortcut
```

Produces `dist/DashDeck/` — a self-contained folder: its own .NET runtime, the signal catalog,
LibVLC, and every component under `plugins/`. Copy the whole folder to the Surface (or launch it
in place) and run `DashDeck.lnk` / `DashDeck.Host.exe`. Escape closes it. Uninstalling is
deleting the folder (constraint C1).

The publish summary should read: catalog `included`, libvlc `included`, components `4 included`.
If components says `none`, a component did not build — check `components/` before carrying it out.

## What this build contains

- **The shell**: six-band layout, status strip (weather · SIM badge · clock, plus an
  `ADAPTER LOST — RECONNECTING` banner when the link drops), stage, and the seven-across nav.
- **Stage occupants**: clock/weather, compass (with G, pitch, roll), phone-projection placeholder,
  local video, web applets, and native Windows apps (Nuvio, Stremio) owned and placed over the
  stage.
- **The dash**: user-arranged cards, paged, edited through MODIFY WIDGETS. The signal picker is
  **searchable and grouped by function**, over the **standard OBD-II Mode 01 set** (~35 signals)
  plus tablet sensors.
- **Four components** in `plugins/`, all shipping in the default layout: Trip Computer, Fuel
  Economy, Avg Economy (tap for a detail with a reset), and Tire Pressure (tap for the overhead
  F-150 that lights a low corner).
- **Settings** in a rail of sections: Appearance, Mount, Display, Diagnostics.

## Known limits — read before deciding something is broken

- **No adapter yet.** Everything runs on the **synthetic F-150** (ADR-0005). The **SIM** badge is
  on, and the adapter-lost banner will *not* appear — there is no real link to lose. Every value
  is simulated, including the new OBD-II signals and TPMS, which read plausible numbers, not real
  ones. TPMS uses placeholder PIDs; the rear-left tyre reads low on purpose.
- **Levelling is required** before pitch, roll, G or the compass attitude render — Settings →
  Mount → LEVEL THE MOUNT, done parked with the tablet in its cradle.
- **Stage occupant is not remembered** across launches (F12); the dash comes up on the clock.

## What to check in the truck

The things that only reveal themselves on glass and in the mount:

- **Touch gestures**: page-swipe on the cards; tap a component card to open its detail; MODIFY
  WIDGETS → the grouped/searchable picker; the Settings section rail. (Tap-to-detail and the
  edit buttons were mouse-only until they were fixed for touch — worth re-confirming.)
- **Reachability** of the bottom nav and launcher from the driver's seat.
- **Levelling**, then that the compass and attitude read sanely at the mount angle.
- **Legibility** at a glance in daylight and at night (Settings → Appearance → Day/Night/Auto).

When something turns up, it is fixed on `develop` and the deploy refreshed — the truck has been
the most valuable source of bugs so far.

# ADR-0038 — The stage launcher as a file: every option, its order, and the quick bar

**Status:** Accepted · 2026-10-02
**Supersedes:** ADR-0024's "built-ins stay in code" (the rest of ADR-0024 — apps added from
Settings ▸ Apps into `apps.json` — stands). **Builds on:** [ADR-0037](ADR-0037-stage-layouts.md)
(the stage as a file) · ADR-0026 (a source keeps playing behind a silent screen) · ADR-0020/0021
(native apps owned and placed over the stage) · ADR-0018 (the launcher bar is 72 high below a
fixed stage). **Keeps:** ADR-0012 (web applets are the third-party model; this launches only what
the user points it at).

## Context

After ADR-0037 the GAUGES stage was a file, but everything around it was still code:
`StageOption.All` listed GAUGES, CLOCK, COMPASS, PHONE, VIDEO, three web pages (MAPS, SPOTIFY,
MUSIC) and three programs (NUVIO, STREMIO, PROBE) in a fixed order, and the bar under the stage
showed whichever five came first. The owner asked for **all of it to be editable through JSON** —
what is offered, in what order, and the quick launch bar — including the web views and the
programs.

ADR-0024 had kept the built-ins in code, rejecting a seed file because the multi-candidate install
paths (Stremio 5 hiding in `stremio-shell-ng.exe`, Nuvio's MSI landing in several places) were
"intelligence" that a file `publish.ps1` rewrites would lose. Both halves of that reasoning stop
holding once the list itself is the thing being edited: a `paths` list in a file carries the same
intelligence, and the user's file lives in `%LOCALAPPDATA%`, which publishing never touches.

## Decision

**The launcher is one JSON file** — `%LOCALAPPDATA%\DashDeck\launcher.json`, comments allowed,
the same family as themes and stage layouts:

```jsonc
{
  "startOn": "GAUGES",
  "quickBar": [ "GAUGES", "TOWING", "MAPS", "PHONE", "SPOTIFY" ],
  "entries": [
    { "name": "GAUGES",  "type": "gauges" },
    { "name": "TOWING",  "type": "gauges", "layout": "towing" },
    { "name": "CLOCK",   "type": "clock" },
    { "name": "MAPS",    "type": "web", "url": "https://www.openstreetmap.org", "zoom": 0.8 },
    { "name": "SPOTIFY", "type": "web", "url": "https://open.spotify.com", "keepPlaying": true },
    { "name": "NUVIO",   "type": "app", "keepPlaying": true,
      "paths": [ "%ProgramFiles%\\Nuvio\\Nuvio.exe", "%LOCALAPPDATA%\\Programs\\Nuvio\\Nuvio.exe" ] },
    { "type": "userApps" }
  ]
}
```

- **Seven entry types, each one occupant**: `gauges` (the theme's stage layout, or one pinned by
  `layout`), `clock`, `compass`, `phone`, `video` (optional `path`), `web` (`url`, optional `zoom`)
  and `app` (`paths`, most likely first, environment variables expanded; `arguments`). A
  `userApps` marker says where the apps added in Settings ▸ Apps go; without it they go last.
- **Every entry** can set `detail` (the line under it in the grid), `group` (its heading — SCREENS,
  WEB and APPS by type otherwise, headings in the order they first appear), `hidden` (kept in the
  file, not offered) and `keepPlaying` (an audio/video source under ADR-0026; phone and video
  default on, everything else off).
- **The order of `entries` is the order of the grid.** **`quickBar`** names up to five entries for
  the buttons below the stage, in order; without it the first five available get them. **Whatever
  is on the stage always has a button**, taking the last place, as before. **`startOn`** is the
  stage at launch — a `--stage` argument still wins.
- **An entry's name is the stage's name.** The occupant answers to it — the clock entry named
  TIME shows TIME in the status strip and lights the TIME button — so renaming works for screens
  too.

**Your file replaces the built-in list outright; it is not merged.** Order is the point, so an
entry left out is not offered. The built-in list is the same format, compiled in, and written to
`launcher.example.json` beside yours at every launch as the reference to copy from — exactly what
the code used to offer, in the same order.

**Nothing in it can empty the launcher.** An entry with an unknown type, no name, a duplicate name,
a web entry without an http(s) `url`, or an app without `paths` is left out and named; a
`quickBar` or `startOn` name that is not offered is dropped and named; a zoom outside 0.25–5 falls
back to Settings ▸ Display. A file that is not JSON, or keeps nothing to offer, leaves the built-in
list in place with one line saying why.

**Settings ▸ Apps** gains a **STAGE LAUNCHER** block in place of the read-only built-in list: which
list is in use, every entry in order with whether it is on the **BAR**, in the **GRID** or
**HIDDEN**, the warnings, and **RELOAD**, **MAKE IT MINE** (writes `launcher.json` from the built-in
list) and **OPEN FOLDER**. Reload rebuilds the buttons without touching what is on the stage. The
Settings ▸ Apps add-an-app form is unchanged and still refuses a name the launcher already uses.

## Consequences

- `AppLaunchSpec.Nuvio`, `.Stremio` and `.Probe` are gone; their paths are entries in the built-in
  launcher. `StageOption.All` became `StageOption.FromLauncher`.
- A stage layout can have its own button — TOWING beside GAUGES — without changing the theme or the
  Settings choice. ADR-0037's FOLLOW THE THEME still governs the plain `gauges` entry.
- A web page can fix its own zoom, which the global web scale could not do per page.
- **Not tied to themes.** A theme brings its stage (ADR-0037) but not a launcher; whether it should
  is open (Q19).
- A GUI editor later writes the same file — the format is the contract, as for themes and layouts.

## Alternatives considered

- **Merge the user's file over the built-in list** (add, override by name). Rejected: it cannot
  express order or removal without inventing `before`/`after` and `remove`, and the owner asked to
  control order.
- **Fold the launcher into the stage layout or the theme file.** Rejected: a layout is what one
  occupant draws, and a theme is how everything looks; which programs are on this tablet is neither,
  and would have to be repeated in every theme.
- **Move `apps.json` into the launcher file.** Rejected for now: the add-an-app form writes a file a
  person never has to open, and a form editing a hand-commented file would lose its comments. The
  `userApps` marker places those apps instead.
- **A shipped `catalog/launcher.json`.** Rejected: the built-in list is compiled in like the built-in
  stage layout, so a missing or damaged catalog cannot cost the launcher, and the example file shows
  it anyway.

# ADR-0043 — Two looks, Modern and Glass; LCARS becomes the user's own

**Status:** Accepted · 2026-10-03
**Supersedes in part:** [ADR-0042](ADR-0042-clean-defaults.md) (the built-in layouts were named
*Clean*; they are now *Modern*, and the old arcs console *Modern* is now *Glass*).
**Builds on:** [ADR-0036](ADR-0036-token-themes.md) (themes) ·
[ADR-0037](ADR-0037-stage-layouts.md) (stage layouts) · ADR-0040 (climate) · ADR-0041 (console).

## Context

After ADR-0042 the climate panel and console were typographic, but the rest of the app — buttons,
settings, cards, the stage — still wore the original DashDeck theme, and the frosted Glass climate
panel and arcs console lived on as loose files with no theme of their own. The owner asked for **two
default themes, Glass and Modern**, for **every screen to follow the theme unless overridden**, and
for **LCARS to be a custom theme** — theirs, not shipped.

## Decision

- **Modern is the built-in theme** (`builtin/modern`, replacing `builtin/dashdeck`): cool greys, one
  light-blue accent (`#5AC8FA`, clear of the quality colours' hues), Segoe UI Variable, smaller radii,
  and a hand-tuned night. It names the built-in **Modern** stage (`catalog/stage/modern.json`: boost
  and engine temperature as thin arcs with large light numbers, four readouts below), and the
  built-in Modern console and climate panel (ADR-0042's *Clean*, renamed).
- **Glass ships beside it** (`catalog/themes/glass.json`): blue-black glass, an ice-blue accent,
  rounder corners. It names the frosted **Glass** climate panel and the **Glass** console (the
  arcs-and-frosted-strip one, `catalog/console/glass.json`, formerly *Modern*), and leaves the stage
  to the chrome F-150 cluster.
- **Every screen follows the theme**: stage, compass, console, climate panel, cards and the shell's
  own chrome. A layout chosen by hand in Settings ▸ Themes still wins; FOLLOW THE THEME undoes it.
  (The mechanism is ADR-0036/0037's; what changes is that both default themes now name their
  layouts.)
- **LCARS is an extra.** It moves to `catalog/extras/lcars/{themes,stage,climate,console}`. The
  first launch that sees an extra copies its files into the user's folders in
  `%LOCALAPPDATA%\DashDeck\` (`ExtrasInstaller`), once, recorded in `settings.json`
  (`installedExtras`). From then on LCARS is **YOURS** — editable, exportable, deletable — and
  deleting it keeps it deleted. A file of the same name the user already has is left alone.
- **A stored choice of a moved file follows it**: `shipped/lcars-inspired` resolves to
  `yours/lcars-inspired` (`ExtrasInstaller.Moved`), for the theme and for every layout choice, so
  whoever wore LCARS still does after the update.
- **The build and `publish.ps1` clear the output `catalog` folder first**, so a file moved or removed
  in the repo does not linger beside the executable and show up twice.

## Consequences

- Anyone on the old DashDeck theme lands on Modern: a new accent, greys and font. That is the point.
- The extras folder is a pattern: a future optional look is a folder, not code.
- Antonio (OFL) now travels with LCARS in the extra, and is copied with the theme so it still loads.

## Alternatives considered

- **Keep LCARS shipped and mark it optional.** Rejected: the owner wants it as their own theme, to
  change freely; a shipped file is replaced by every deploy.
- **Ship LCARS nowhere** (only in the user's folder on the tablet). Rejected: a fresh install or a
  new tablet would lose it; the extras folder keeps a spare copy.
- **Copy extras on every launch.** Rejected: it would bring back a theme the user deleted and
  overwrite their edits.

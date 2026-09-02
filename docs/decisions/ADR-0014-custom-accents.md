# ADR-0014 — Custom accents, allowed by validation rather than curation

**Status:** Accepted · 2026-09-02
**Supersedes:** the *"Accents are curated rather than picked"* clause of
[ADR-0013](ADR-0013-theming.md). Everything else in ADR-0013 stands, including — especially —
that the four signal-quality colours are never themeable.
**Resolves:** F11 (settings do not persist).

## Context

ADR-0013 offered four accents and refused a colour picker. Asked for custom colours a day
later, the useful question was not "was that decision wrong" but **what the decision was
actually protecting**, because the answer is in ADR-0013's own wording:

> A colour wheel lets someone choose a green two shades from `Live` … and **quietly** destroy
> the distinction the dash is most careful about.

The load-bearing word is *quietly*. The objection was never to custom colours; it was to a
control that accepts a bad one **without saying so**. Curation was one way to prevent that.
It is not the only way, and it is the more expensive one — it makes a four-item list the
permanent ceiling on personalisation, in a project whose stated point is the ecosystem.

## Decision

**Any colour may be the accent, provided it passes a check that is shown to the user.**

The presets stay. They are the fast path and they are the calibration reference — not a
fence.

`AccentValidation` applies two rules:

1. **Hue separation ≥ 18° from every quality colour** — Live, Stale, Simulated and Fault, read
   from `QualityPalette` so the rule and the rendering can never drift apart.
2. **Relative luminance ≥ 0.18**, so the accent is legible on the canvas.

**18° is calibrated, not chosen.** Ember — already shipping — sits 18.02° from `Stale`, so it
is the boundary case and the rule reads *"no closer than something already on the dash."* The
first attempt picked 25° by feel and **rejected Ember itself**; that mistake is now a test
(`AccentValidationTests.Every_preset_passes_its_own_rule`) that fails the build if the floor
is raised, if a quality colour moves, or if a preset is added that would have lowered the bar.

**The check runs on every keystroke, and its verdict is on screen.** A refused colour says
which quality colour it collides with, in the fault red, and the apply button is disabled.
This is the whole argument: the failure mode ADR-0013 rejected was silent, and this one talks.

**Stored accents are re-validated on load, not merely on entry.** A colour approved by an
older build was checked against that build's quality palette. If a later build moves one of
those four, the stored accent falls back to the default rather than inheriting a stale
approval.

## Persistence

Settings are written to `%LOCALAPPDATA%\DashDeck\settings.json`, **immediately on change** —
a dash is closed by having its power pulled, and a setting that survives only a graceful
shutdown is not a setting that survives.

Where the file lives is the decision, not an implementation detail. `publish.ps1` **deletes
and rewrites** `dist\DashDeck` on every build, so anything stored beside the executable would
be destroyed by every update. `%LOCALAPPDATA%` is outside that folder: preferences survive a
rebuild, an update, and deleting the app. It is also C1 working as intended — settings there,
never the registry, and uninstalling is still deleting a folder.

The file is **tolerant in both directions**: every property has a default and unknown
properties are ignored, so a file written by an older build loads into a newer one and the
other way round. Adding a setting is not a migration; removing one is not a crash. A corrupt
file yields defaults and a recorded reason rather than a failure to start.

`--theme` and `--accent` go through `ThemeService.Preview`, which does not persist. They are
for looking, and a look should not become the setting.

## Consequences

- The accent is genuinely personal, and the ceiling on personalisation is legibility rather
  than a list of four.
- The quality colours gain a second guarantee: not merely unthemeable, but actively defended
  from being *approached*.
- The validation rules are judgements with numbers attached. They will be wrong at the edges
  for someone. They are visible, tested and in one file, which is the best available answer.
- A future extra quality colour automatically tightens the rule — and may invalidate a stored
  accent, which is why loading re-validates.

## Alternatives considered

- **Keep curation and add more presets.** Rejected: it postpones the request without
  answering it, and every added preset is another judgement call with no rule behind it.
- **A free picker with no check.** Rejected for exactly ADR-0013's reason. The silence was
  always the problem.
- **Warn but allow.** Tempting, and rejected: a warning that can be clicked past is how the
  quality distinction erodes anyway, one dash at a time. There is no reason to need a colour
  within 18° of `Stale` — the constraint costs the user almost nothing.
- **Let the user re-map the quality colours too.** Rejected, permanently. See ADR-0013.

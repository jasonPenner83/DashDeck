# ADR-0013 — Bounded theming: day/night, one accent, fixed quality colours

**Status:** Accepted · 2026-09-02
**Implements:** B1, which resolved Q9 — themes are *tweakable tokens* rather than a second
hand-built light theme.

## Context

Q9 asked whether the dash should be dark-only or ship light and dark. Neither answer was
right: a dash used in a truck is dark in practice, and a light theme is a second design to
maintain for a case that may never occur — but "dark only, take it or leave it" ignores that
the same screen is used in bright afternoon sun and on an unlit highway.

B1 settled the direction: the palette is already a token set, so expose the tokens. This
records what that means concretely, because "expose the tokens" has an obvious reading that
would quietly break the project's central rule.

## Decision

**Three things are adjustable, and nothing else.**

1. **Day / Night / Auto.** Both palettes are dark. Night lowers luminance — surfaces further
   than text, so contrast survives while the screen stops being the brightest thing in the
   cab. This is the automotive convention: night mode exists so the display does not dazzle,
   not so it looks different.
2. **One accent, from a curated list.** Ember, Cyan, Violet, Magenta.
3. Nothing else. In particular:

**The four signal-quality colours are not themeable, ever.** Live, Stale, Unavailable and
Simulated are semantic constants. A reading that is stale must look stale on every dash,
whatever else has been adjusted — otherwise the one rule this project will not bend, that a
value's trustworthiness is always rendered honestly, starts depending on a setting.

**Accents are curated rather than picked.** A colour wheel lets someone choose a green two
shades from `Live`, or a yellow indistinguishable from `Stale`, and quietly destroy the
distinction the dash is most careful about. The offered accents are spaced away from green
(~145°), amber (~45°), blue (~230°) and red (~5°).

**Auto decides from sunrise and sunset, provisionally.** Headlights would be the right
signal — the driver has already made the judgement, and a tunnel or a prairie storm is night
as far as a screen is concerned. Lighting status is not in the legislated OBD-II set; it
lives on the Ford body module over MS-CAN, and reaching it depends on PID discovery against
the real truck (R2) and on what the Gateway Module passes (R4, Q5). Sunrise and sunset are
already being fetched for the clock face, are right most of the time, and work today. When a
headlight signal exists it becomes a named signal like any other and only
`ThemeService.ResolveAuto` changes.

## Reasoning

**Why bounded rather than free.** The tempting version of "tweakable tokens" is a colour
picker per token. It is also the version that lets a user unknowingly break signal-quality
legibility, which is the one property this project treats as non-negotiable. Constraining the
choice to an accent means every combination is safe by construction, with no validation to
write and no way to arrive at an unreadable dash.

**Why both palettes are dark.** Q9's reasoning has not changed. Designing, maintaining and
testing a genuinely light theme is real work for a case that may never occur, and this
delivers the benefit that was actually wanted — not being dazzled at night — for a fraction
of the cost.

**Why Auto explains itself.** The settings screen shows what Auto is deciding from
("Sunrise 06:45, sunset 20:09") and says plainly that headlights would be better. An
automatic setting that will not say why it did something is infuriating, and this one is
provisional enough to deserve the caveat.

## Alternatives

- **A full colour picker per token.** Rejected: see above. The failure mode is silent and
  lands on the safety-adjacent part of the design.
- **A real light theme for Day.** Rejected as Q9 rejected it — a second design to maintain
  for a case that may never occur. Worth revisiting only if the tablet turns out to be
  genuinely unreadable in direct sun, which is a measurement nobody has taken.
- **Auto from the tablet's ambient light sensor.** The Surface has one. Rejected for now: it
  reads the cabin, not the road, and a hand or a sun visor over the tablet would flip the
  theme. Headlights remain the better answer when reachable.
- **System dark mode.** Meaningless here — the app is dark either way, and Windows knows
  nothing about whether it is dark outside the truck.

## Consequences

- **Theme tokens must be referenced with `DynamicResource`, not `StaticResource`.** WPF
  freezes the `SolidColorBrush` instances in a compiled resource dictionary, so mutating one
  is a no-op, and a `StaticResource` reference has captured the old instance regardless.
  Theming works by *replacing* the resource value. This was found by measuring pixels after
  day and night rendered identically — worth knowing before adding a new token.
- **Every new colour must come from a token.** A literal hex in a view is invisible to the
  theme and will not dim at night. `Theme.xaml` says so already; this makes it load-bearing.
- The quality colours stay in `QualityConverters`, in code, deliberately outside the themed
  token set — a structural reminder that they are not part of it.
- Auto costs one extra Open-Meteo call per twelve hours, alongside the clock face's own.
  Both are cached; neither needs a key.
- Settings persistence is **not** part of this. The chosen theme resets on restart until
  there is a settings store, which belongs in `%LOCALAPPDATA%` under constraint C1.

# ADR-0036 — Themes as files of named tokens, chosen in Settings ▸ Themes

**Status:** Accepted · 2026-10-02
**Supersedes:** ADR-0013's *"Three things are adjustable, and nothing else"*. Everything else in
[ADR-0013](ADR-0013-theming.md) stands — above all, **the four signal-quality colours are never
themeable** — and so does [ADR-0014](ADR-0014-custom-accents.md)'s accent check, which now applies
to a theme's accent too.

## Context

The dash could be made day or night and given one accent. Asked for a theme layer — save themes,
open them, a settings section, and an LCARS look to start with — the question was how much a theme
should be able to change. The model offered was Home Assistant, where the customisation people
reach for is themes and the add-ons around them.

How HA does it:

- **A theme is a flat list of named variables** in a file (`primary-color`,
  `card-background-color`, `ha-card-border-radius`, fonts). A theme sets only what it changes;
  everything else falls back to the default. A variable can name another.
- **Themes live in a folder**, are picked per user, and `frontend.reload_themes` picks up an edit
  without a restart. **Core has no theme editor** — themes are written by hand or downloaded, and
  shared as files.
- **The deep end** — styling one card (card-mod), changing layout (custom cards) — comes from CSS,
  which a browser applies to anything at runtime.

DashDeck already worked like the first half for colours: every colour in the shell is a named
token reached by `DynamicResource` (ADR-0013's consequence), replaced at runtime. Fonts, corner
radii and border widths were literals. An LCARS look needs all of them: condensed capitals, pill
buttons, black panels.

## Decision

**A theme is a JSON file of named tokens** — colours, numbers and fonts — modelled on HA:

```jsonc
{
  "name": "LCARS (inspired)",
  "fontFiles": [ "Antonio-Regular.ttf", "Antonio-Bold.ttf" ],
  "tokens": { "canvas": "#000000", "buttonRadius": 28, "uiFont": "Antonio, Segoe UI" },
  "night":  { }
}
```

- **The vocabulary** (`ThemeTokens`) is 26 tokens: the surface and text ramps; the accent, how
  strongly a selected button is filled with it (`accentWash`, 0.08 to 1) and the text on that fill;
  captions; the status strip, the nav bar, and buttons' fill, outline and text; button, card and
  panel corner radii and outline width; the two fonts. Every token has a default, and the
  defaults are the DashDeck look — so the built-in theme is pixel-identical to what shipped before,
  day and night. A default can follow another token (`caption` follows `textLow`), as an HA
  variable can name another.
- **A theme's fonts travel with it.** `fontFiles` names `.ttf`/`.otf` files beside the theme file;
  they are used before any font of the same name on the tablet, and are copied on import and
  export. A theme cannot name a file anywhere else.
- **Night is derived by dimming**, per token: surfaces ×0.55, text ×0.74, the accent ×0.82 — unless
  the theme gives a `night` value. The DashDeck look keeps its hand-tuned night palette as its
  defaults.
- **Nothing in a theme file can stop the dash coming up.** A value that cannot be used costs that
  token — it falls back to the default — and a line in Settings. An unknown token is ignored and
  named. A file that is not a theme is left out of the list and named.
- **The accent check still applies** (ADR-0014). A theme whose accent is too close to Live, Stale,
  Simulated or Fault is worn with the DashDeck accent, and Settings says why. Choosing a theme sets
  the accent to the theme's; the Appearance picker still overrides it until the next theme.
- **Legibility is checked, not enforced**: text against what it sits on, by WCAG contrast. Low
  contrast is a warning beside the theme. The person choosing it can see the result.
- **The quality colours are not tokens.** Card borders, value dimming, the SIM badge and the
  `ADAPTER LOST` banner keep their fixed colours under every theme.

**Where themes live.** Shipped themes in `catalog/themes/`, beside the executable and never written
on the tablet; the user's in `%LOCALAPPDATA%\DashDeck\themes\`, which a deploy never touches. The
choice is stored as an id (`shipped/lcars-inspired`); one that has gone falls back to the DashDeck
look.

**Settings ▸ Themes** lists the built-in look, the shipped themes and yours, each with a swatch
strip; a tap wears one immediately. IMPORT copies a theme file (and its fonts) in and wears it;
EXPORT writes the current theme and its fonts to a folder; SAVE AS copies the current theme into
your folder under a new name; DELETE removes one of yours; OPEN FOLDER shows your folder; RELOAD
reads the folders again — HA's reload, for a theme edited in Notepad. Below the list, every token
with what the current theme sets it to: the reference for editing a file by hand.

**The first shipped theme is *LCARS (inspired)*** — black, pill buttons in periwinkle, peach
captions, a solid orange selection with black lettering, and the Antonio font (SIL OFL 1.1, shipped
with its licence). It is a fan homage, named and described as such: no Star Trek artwork, logos or
proprietary fonts. Its orange accent is `#FF7722`, not the classic `#FF9900`, which is 7° from the
Stale amber and is refused by the accent check — a test pins that.

## Consequences

- **Fonts are now `DynamicResource` everywhere** in the shell, as colours already were. A new view
  that uses `StaticResource UiFont` will not follow the theme. The same goes for the radius and
  border-width tokens.
- Code-drawn surfaces (the compass, the gauges) read fonts and brushes when they draw, so they pick
  up a new theme on their next redraw rather than instantly.
- A selected button can now be filled solid (`accentWash: 1`), which put the settings rail's hover
  colour over a selected tab's dark lettering. Hover now yields to selection there.
- Three things version independently (ADR-0008); theme files are data, like the catalogs, and are
  not versioned with the app. An unknown token from a newer build is ignored, not an error.

## Alternatives considered

- **An in-app editor now** — a colour picker per token with live preview. Deferred, not rejected:
  HA core manages without one, and SAVE AS + a text editor + RELOAD is a complete loop. The accent
  check and legibility warnings are what an editor would need anyway, and they exist.
- **A decorative frame** — LCARS elbows and side bars around the bands. Deferred: it changes layout,
  not tokens, costs pixels on a 912-wide screen, and is a separate decision.
- **Per-card styling** (HA's card-mod). Deferred: a card in `dashboard.json` carrying its own token
  overrides is the natural next step and fits this model.
- **Arbitrary styling from files** — loading XAML a theme supplies. Rejected: WPF has no runtime
  stylesheet, and loading markup from a downloaded file is both fragile and unsafe. Tokens are the
  ceiling, and a high one.
- **Theming the quality colours**, as HA themes can recolour states. Rejected, as ADR-0013 rejected
  it: a stale reading must look stale on every dash.
- **The name *LCARS* alone, or a fully neutral name.** The theme is called *LCARS (inspired)* with
  a non-affiliation note, so it can be found by the name people know without claiming to be the
  real thing.

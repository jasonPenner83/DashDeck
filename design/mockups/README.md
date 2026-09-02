# P0.5 shell mockups

Visual design for the WPF shell, drawn before any XAML exists. Nothing here is code that
ships — it is the design system and layout that [ADR-0001](../../docs/decisions/ADR-0001-ui-stack.md)
leaves to us, worked out in HTML because it is faster to iterate than XAML.

## The layout

Six horizontal bands sit between the status strip and the navigation. **One band is one
widget row** — 175 of widget plus a 20 gutter, so 195. Six of those is 1170, which with a
90 status strip and a 180 nav is exactly 1440.

The **stage is a layer, not a screen**: it claims the top *n* bands and keeps running while
navigation switches only the region below it. Tapping a widget expands it in place down
there; the stage yields bands rather than being replaced. Settings is the exception that
takes all six.

See `B2` and `B3` in [open questions](../../docs/04-open-questions.md).

## Files

| File | What it is |
|---|---|
| `*.dc.html` | One artboard each. Self-contained HTML fragments; `{{accent}}` is the one template hole. |
| `*.src.html` | Page shells with `<!--SCREEN:Name-->` placeholders. |
| `*.html` | Built output — **generated, do not edit.** |
| `canvas.json` | Layout manifest, used only if these are ever re-seeded into a design canvas. |
| `build.ps1` | Inlines the artboards into the shells. |
| `TripComputer.superseded.html` | The old full-screen component view, replaced by `Expanded.dc.html` when the stage became a layer. Kept for reference. |

## Rebuilding

```powershell
pwsh -File design/mockups/build.ps1
```

Edit an artboard, re-run, reopen the built page. There is no watcher and no toolchain —
deliberately, since these are throwaway once the XAML exists.

## The two built pages

- **`dashdeck-shell.html`** — the design review: every screen with captions, the layout
  model, the foundations sheet, and the open questions.
- **`dashdeck-device.html`** — full-screen 1:1 viewer for judging type and touch targets on
  real glass. Tap the edges to change screen, tap the middle for the readout. It reports
  the viewport against the 960 × 1440 design so you can see when it is *not* 1:1 — which
  matters, because the dev Surface Pro 7 gives 912 × 1368 in portrait, not the Pro 9's
  960 × 1440 (`Q16`).

## Known gaps

- Drawn at the **Pro 9** portrait size. If the truck tablet is the Pro 7, the band
  arithmetic needs redoing — 1368 − 90 − 180 = 1098, which is not divisible by six (`Q16`).
- The map on the stage is a **mockup of intent**. No map component exists.
- **Climate and Stereo are nav slots only.** Before either is built, note that C3 and
  ADR-0006 forbid taking over audio or climate (`Q14`).
- Nav overflow past ~5 destinations is undesigned (`B3`).

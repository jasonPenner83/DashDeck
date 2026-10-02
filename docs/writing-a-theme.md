# Writing a theme

A DashDeck theme is a JSON file of named **tokens**, the way a Home Assistant theme is a file of
named variables (ADR-0036). Set only what you want to change; everything else stays the DashDeck
look. Themes are chosen in **Settings ▸ Themes**.

## The quickest way to start

1. In **Settings ▸ Themes**, wear the theme closest to what you want.
2. Type a name under **START YOUR OWN FROM THIS ONE** and press **SAVE AS**. Your copy is saved in
   `%LOCALAPPDATA%\DashDeck\themes\` and worn straight away.
3. Press **OPEN FOLDER**, open your file in Notepad, change some values, save.
4. Press **RELOAD**. The dash redraws in your edit. Problems are listed above the theme list:
   a value that couldn't be used, text that will be hard to read, an accent that was refused.

**EXPORT…** writes the current theme (and its fonts) to a folder to share; **IMPORT…** brings one in.

## Examples to copy from

Every theme DashDeck ships is written out to `%LOCALAPPDATA%\DashDeck\themes\examples\` each time
DashDeck starts (and when you press **OPEN FOLDER**):
- `lcars-inspired.json`, with its Antonio font files and licence beside it;
- `dashdeck.json`: the built-in look with **every token written out** at its default value, day and
  night. It's the whole vocabulary in one file.

That folder is for reading. Files there are not loaded, and they're put back at the next launch.
To start from one, copy the `.json`, and any `.ttf` files it lists under `fontFiles`, up a folder
into `themes\`. Change its `name`, then press **RELOAD**.

## The file

```jsonc
{
  "name": "Night Owl",                       // required — what the list shows
  "description": "Deep blue, amber-free.",   // optional
  "author": "you",                           // optional
  "stageLayout": "night-owl",                // optional — the stage layout this theme brings (ADR-0037)
  "fontFiles": [ "MyFont-Regular.ttf" ],     // optional — font files beside this file
  "tokens": {                                // the day values you change
    "canvas": "#0A0F1E",
    "accent": "#B07BFF",
    "buttonRadius": 28,
    "uiFont": "MyFont, Segoe UI"
  },
  "night": {                                 // optional — night values not to derive
    "canvas": "#04060C"
  }
}
```

- **Colours** are `#RRGGBB`, `#AARRGGBB` (with transparency), `#RGB`, or `transparent`.
- **Numbers** are plain numbers. Radii are 0–60 (half a 56-high button, **28**, makes a pill);
  `borderWidth` 0–6; `accentWash` 0–1.
- **Fonts** are a comma-separated list, first that exists wins. A family whose file is listed in
  `fontFiles` is used from beside the theme file, so the theme carries it. Only `.ttf`/`.otf` files
  sitting next to the theme are allowed. Include the font's licence beside it if you share it.
- **Night** is derived by dimming each day value (surfaces more than text), unless you give one in
  `night`. Day/Night/Auto in **Settings ▸ Appearance** still applies to every theme.
- Comments (`//`) are allowed. A value that can't be used falls back to the default and is listed
  as a problem; it never stops the dash starting.

## Rules that a theme can't change

- **Live, Stale, Unavailable and Simulated keep their colours** under every theme: card borders,
  value dimming, the SIM badge, the ADAPTER LOST banner (ADR-0013). A stale reading must look stale
  on every dash.
- **The accent has to stay tellable apart from them** (ADR-0014): at least 18° of hue from each,
  and bright enough to read. A theme whose accent fails is worn with the DashDeck accent, and
  Settings says why. The classic LCARS orange `#FF9900` fails — it is 7° from Stale amber — which is
  why the shipped LCARS theme uses `#FF7722`.

## Every token

| Token | Kind | Default | Night | What it paints |
|---|---|---|---|---|
| `canvas` | colour | `#0D0C0B` | dim ×0.55 | The screen behind everything. |
| `surface` | colour | `#191816` | dim ×0.55 | Cards, panels and list rows. |
| `raised` | colour | `#211F1C` | dim ×0.55 | Things that sit above a surface: text fields, menus, badges. |
| `hairline` | colour | `#2C2925` | dim ×0.55 | Dividers and card outlines. |
| `hairlineStrong` | colour | `#3A3630` | dim ×0.55 | Stronger outlines. |
| `textHigh` | colour | `#F4F1EB` | dim ×0.74 | Headline text. |
| `textMid` | colour | `#A5A096` | dim ×0.74 | Body text and status. |
| `textLow` | colour | `#6B665E` | dim ×0.74 | Quiet text. |
| `textFaint` | colour | `#4A453E` | dim ×0.74 | Fine print. |
| `accent` | colour | `#FF7A1A` | dim ×0.82 | What is selected or current. Must stay tellable apart from the quality colours. |
| `accentWash` | number | `0.08` | same | How strongly a selected button is filled with the accent: 0.08 is a tint, 1 is solid. |
| `selectedText` | colour | follows `accent` | dim ×0.82 | Text on a selected button. Dark, when the wash is solid. |
| `onAccent` | colour | `#140A02` | same | Text drawn on solid accent. |
| `caption` | colour | follows `textLow` | dim ×0.74 | Small capital labels: card names, section headings. |
| `stripBackground` | colour | follows `canvas` | dim ×0.55 | The status strip across the top. |
| `navBackground` | colour | follows `canvas` | dim ×0.55 | The navigation bar across the bottom. |
| `navText` | colour | follows `textMid` | dim ×0.74 | Navigation labels and icons. |
| `buttonBackground` | colour | `transparent` | dim ×0.55 | Buttons and chips when not selected. |
| `buttonBorder` | colour | follows `hairlineStrong` | dim ×0.55 | Their outline. |
| `buttonText` | colour | follows `textMid` | dim ×0.74 | Their text. |
| `buttonRadius` | number | `14` | same | Corner radius of buttons and chips. Half their height (28) makes a pill. |
| `cardRadius` | number | `14` | same | Corner radius of dash cards. |
| `panelRadius` | number | `12` | same | Corner radius of settings rows and panels. |
| `borderWidth` | number | `1` | same | Outline width of buttons, chips and fields. |
| `uiFont` | font | `Archivo, Segoe UI` | same | Values and headlines. |
| `monoFont` | font | `IBM Plex Mono, Consolas` | same | Labels, captions and buttons. |

The **Appearance** accent picker still works on top of a theme: choosing a theme sets the accent
to the theme's, and picking another afterwards overrides it until the next theme is chosen.

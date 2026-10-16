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
- `glass.json`: the Glass theme — blue-black glass, an ice-blue accent, rounder corners;
- `modern.json`: the built-in **Modern** theme with **every token written out** at its default value,
  day and night. It's the whole vocabulary in one file.

**LCARS (inspired)** is not among them: since ADR-0043 it is *yours*. The first launch after the update
copies it, its Antonio fonts and its licence into `themes\` itself (and its stage, console and climate
layouts into theirs), so it shows under YOURS — change it, export it, delete it. A spare copy stays in
`catalog\extras\lcars\` beside `DashDeck.exe`; deleting it in Settings keeps it deleted.

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
  why the LCARS theme uses `#FF7722`.

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
| `mapLand` | colour | follows `canvas` | dim ×0.55 | The map's ground: everything that is not something else. |
| `mapWater` | colour | `#0F1A24` | dim ×0.55 | Lakes, rivers and the sea. |
| `mapGreen` | colour | `#141712` | dim ×0.55 | Woods, grass, farmland and wetland. |
| `mapBuilding` | colour | follows `raised` | dim ×0.55 | Building footprints, drawn close in. |
| `mapRoad` | colour | follows `textLow` | dim ×0.74 | Streets and minor roads. |
| `mapRoadMajor` | colour | follows `textMid` | dim ×0.74 | Motorways, trunk roads and primaries. |
| `mapRoadCasing` | colour | follows `canvas` | dim ×0.55 | The outline under a road, which separates it from the ground. |
| `mapLabel` | colour | follows `textHigh` | dim ×0.74 | Place and street names. |
| `mapLabelHalo` | colour | follows `mapLand` | dim ×0.55 | Drawn behind a name so it reads over anything. |
| `mapMarker` | colour | follows `accent` | dim ×0.82 | The vehicle on the map. |

## Theming the map

The map (ADR-0064) is drawn from the ten `map*` tokens above, and **you almost never need to set
them.** Every one follows a token you have probably already changed: the ground follows `canvas`,
the roads follow the text ramp, the vehicle follows `accent`. Change the dash and the map comes
with it — including at night, through the same dimming factors.

The LCARS theme is the whole argument in two lines. Its roads come out lavender and its motorways
peach *because* `textLow` and `textMid` are lavender and peach; its marker is LCARS orange because
`accent` is. The only things it has to say are the two that have no equivalent anywhere else on the
dash:

```jsonc
"mapWater": "#1F1F3D",
"mapGreen": "#140F1A"
```

Two caveats worth knowing before you spend an evening on it:

- **Labels are always Noto Sans**, whatever `uiFont` says. A map label needs glyphs built into PBF
  ranges, and a TTF in a theme folder has none. So an LCARS map is Star Trek colours in ordinary
  lettering until there is a tool that builds glyphs from a theme's own font.
- **Alpha is dropped.** A colour with transparency is taken as opaque, because translucent roads
  over translucent landcover is how a map turns to mud.

To see somebody else's cartography instead, `--map-style https://tiles.openfreemap.org/styles/dark`
(or `bright`, `liberty`, `positron`, `fiord`) overrides the theme's map entirely — which is also
the comparison to reach for if a themed map looks wrong.

The **Appearance** accent picker still works on top of a theme: choosing a theme sets the accent
to the theme's, and picking another afterwards overrides it until the next theme is chosen.

# Writing a stage layout

The GAUGES stage is drawn from a **stage layout**: a JSON file listing every element on the stage,
where it sits, what it shows and how it looks (ADR-0037). It works like a Home Assistant dashboard
written in YAML, but in JSON with `//` comments allowed. Change the file, press **RELOAD**, look.

## Examples to copy from

Every layout DashDeck ships is written out to `%LOCALAPPDATA%\DashDeck\stage\examples\` each
time DashDeck starts:
- `lcars.json`: the LCARS (inspired) stage, comments and all;
- `f150-cluster.json`: the built-in six-dial cluster, which otherwise only exists in code.

That folder is for reading. Files there are not loaded, and they're put back at the next launch.
Copy one up a folder into `stage\` to make it yours. Keeping the name `lcars.json` makes your copy
replace the LCARS stage whenever the LCARS theme is worn.

## The loop

1. **Settings ▸ Themes ▸ STAGE LAYOUT**: the layout showing is named at the top.
2. Type a name under **EDIT YOUR OWN COPY OF THE ONE SHOWING**, press **SAVE AS**. Your copy is
   written to `%LOCALAPPDATA%\DashDeck\stage\` and shown.
   - Save it as the name the theme uses (`lcars` for LCARS) and it replaces that theme's stage.
3. **OPEN FOLDER**, open the file in Notepad, change something, save.
4. Go to the stage and choose **RELOAD STAGE LAYOUT** from the three-dot menu, or press **RELOAD** in
   Settings. Problems are listed in Settings, under the layout's name.

## The file

```jsonc
{
  "name": "Towing",                          // required
  "description": "Trans temp and boost big.",
  "author": "you",
  "background": "#000000",                   // #RRGGBB, @token, or leave out for the theme's canvas
  "elements": [ /* drawn in order, later on top */ ]
}
```

The stage is **912 wide and 636 tall**. Every `x`, `y`, `width` and `height` is in those pixels,
and the whole canvas is scaled to the real stage.

### Colours

`#RRGGBB` (or `#AARRGGBB`, `#RGB`), or **`@token`** — any colour token from a theme
([writing-a-theme](writing-a-theme.md)): `@accent`, `@caption`, `@textHigh`, `@surface`…
A `@token` follows the theme and dims at night with it; a `#` colour stays exactly that.

## Elements

Every element has `type`, `x`, `y`, `width`, `height` and an optional `id` (used in problem
messages).

| `type` | What it is | Its own fields |
|---|---|---|
| `gauge` (the default) | A reading, drawn in a `style` | everything under *Gauges* below |
| `text` | Fixed words | `content`, `colour`, `fontSize`, `font` (`ui` or `mono`), `align` (`left`, `center`, `right`) |
| `clock` | The time | `content` is a [.NET time format](https://learn.microsoft.com/dotnet/standard/base-types/custom-date-and-time-format-strings) — `HH:mm`, `h:mm tt`, `ddd d MMM` — plus the `text` fields |
| `panel` | A filled shape | `colour`, `radius`: one number for all corners, or four for top-left, top-right, bottom-right, bottom-left |

**An LCARS elbow** is two things: a thick panel with one big corner (`"radius": "56,0,0,0"`), and a
black panel over its inside corner with a smaller radius (`"radius": "28,0,0,0"`). The shipped
`catalog/stage/lcars.json` does exactly this.

## Gauges

```jsonc
{
  "id": "boost",
  "style": "dial",                // dial | arc | bar | lcarsBar | digital
  "x": 134, "y": 28, "width": 300, "height": 300,
  "label": "BOOST", "unit": "psi", "format": "0.0",
  "source": {
    "signal": "engine.intakeManifoldPressure",   // a catalog id — Settings ▸ Sensors lists them
    "minus": "engine.barometricPressure",        // optional: subtract a second signal
    "minusFallback": 101.325,                    // used when that one has no reading
    "scale": 0.1450377,                          // then multiply (kPa → psi)
    "offset": 0,                                 // then add
    "rateHz": 3                                  // how often to ask, at most 10
  },
  "min": -15, "max": 25,
  "majorTick": 5, "minorTick": 2.5,              // 0 for none
  "zones": [ { "from": 18, "to": 25, "colour": "#E8531E" } ],
  "parts": { "needleWidth": 6 }                  // style-specific tuning, below
}
```

- **`format`** is a .NET number format: `0`, `0.0`, `0.00`.
- **Zones** colour part of the scale. A bar's fill and an LCARS bar's lit pills take the zone's
  colour while the value is in it.
- **Rate**: every gauge on screen shares the adapter's ~19 requests a second with the dash. Ask for
  what the needle needs: 3–4 for boost or throttle, 1 for temperatures, 0.5 for slow things.

### How a gauge says how much to trust it

- **No reading** (the truck does not answer, or has not yet): no needle, no fill, the readout says
  `NO DATA`. Never a zero.
- **Stale**: drawn dimmed.
- A **dot** in the top-right corner is the quality colour: green Live, blue Simulated, amber Stale,
  grey Unavailable — the same as the cards, under every theme.
- A `source.signal` that is not in the catalog: the gauge says `UNKNOWN SIGNAL`.

## Parts

Every part has a default; set only what you change. These work on every style:

| Part | What it does |
|---|---|
| `labelColour` | colour of the caption — default the theme's caption colour |
| `valueColour` | colour of the number — default the theme's headline text |
| `labelSize` | caption size in px |
| `valueSize` | number size in px |
| `showLabel` | true/false |
| `showValue` | true/false — the digital readout |

### `dial`

| Part | What it does |
|---|---|
| `startAngle` | degrees from 12 o'clock where the scale starts — default -135 |
| `sweep` | degrees the scale covers — default 270 |
| `face` | dial face colour, or "none" |
| `bezel` | "chrome", "ring" or "none" |
| `bezelColour` | ring colour when bezel is "ring" |
| `needleColour` | needle colour |
| `needleWidth` | needle width at the hub, px |
| `needleGlow` | glow colour around the needle, or "none" |
| `hubColour` | the pivot's centre colour |
| `tickColour` | long tick colour |
| `minorTickColour` | short tick colour |
| `tickLength` | long tick length, px |
| `numerals` | true/false — numbers on the scale |
| `numeralColour` | number colour |
| `numeralDivisor` | divide the scale numbers by this: 1000 shows rpm as 1–7 |

### `arc`

| Part | What it does |
|---|---|
| `startAngle` | degrees from 12 o'clock where the arc starts — default -135 |
| `sweep` | degrees the arc covers — default 270 |
| `thickness` | arc thickness, px |
| `trackColour` | the unfilled arc |
| `fillColour` | the filled part — default the theme accent |
| `tickColour` | tick colour |

### `bar`

| Part | What it does |
|---|---|
| `orientation` | "horizontal" or "vertical" |
| `thickness` | track thickness, px |
| `trackColour` | the unfilled track |
| `fillColour` | the filled part — default the theme accent |
| `tickColour` | tick colour |
| `radius` | corner radius of the track, px |

### `lcarsBar`

| Part | What it does |
|---|---|
| `orientation` | "horizontal" or "vertical" |
| `segments` | how many pills — default 20 |
| `segmentGap` | gap between pills, px |
| `thickness` | pill height (or width when vertical), px |
| `trackColour` | unlit pills |
| `fillColour` | lit pills — default the theme accent |
| `capColour` | the end cap that carries the caption |
| `capWidth` | end cap width, px |

### `digital`

| Part | What it does |
|---|---|
| `frameColour` | outline colour, or "none" |
| `frameRadius` | outline corner radius, px |

## What stops an element being drawn

Left out, and named in Settings:

- a gauge with no `source.signal`;
- `max` not above `min`;
- a `rateHz` above 10;
- ticks so fine the scale would be solid;
- text with no `content`;
- a clock `content` that is not a time format;
- a panel `radius` that is not one number or four;
- a `width` or `height` that is not positive.

Only warned about, and still drawn:

- an element that reaches off the 912 × 636 stage (it is cut off);
- a part that style does not have (ignored);
- a colour that is not a colour (the default is used).

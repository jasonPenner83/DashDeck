# Writing a stage layout

The GAUGES stage is drawn from a **stage layout**: a JSON file listing every element on the stage,
where it sits, what it shows and how it looks (ADR-0037). It works like a Home Assistant dashboard
written in YAML, but in JSON with `//` comments allowed. Change the file, press **RELOAD**, look.

## Examples to copy from

Every layout DashDeck ships is written out to `%LOCALAPPDATA%\DashDeck\stage\examples\` each
time DashDeck starts:
- `modern.json`: the Modern theme's stage — two thin arcs with large light numbers, four readouts;
- `f150-cluster.json`: the built-in six-dial cluster, which otherwise only exists in code.
- `compass.json`: the built-in COMPASS screen (ADR-0039). Copy it up keeping the name `compass.json`
  and it replaces the compass.

That folder is for reading. Files there are not loaded, and they're put back at the next launch.
Copy one up a folder into `stage\` to make it yours. Keeping the name `modern.json` makes your copy
replace the Modern stage whenever the Modern theme is worn. (LCARS's layouts are already yours — in
`stage\`, `console\` and `climate\` — since ADR-0043.)

## The loop

1. **Settings ▸ Themes ▸ STAGE LAYOUT**: the layout showing is named at the top.
2. Type a name under **EDIT YOUR OWN COPY OF THE ONE SHOWING**, press **SAVE AS**. Your copy is
   written to `%LOCALAPPDATA%\DashDeck\stage\` and shown.
   - Save it as the name the theme uses (`lcars` for LCARS) and it replaces that theme's stage.
3. **OPEN FOLDER**, open the file in Notepad, change something, save.
4. Go to the stage and choose **RELOAD STAGE LAYOUT** from the three-dot menu, or press **RELOAD** in
   Settings. Problems are listed in Settings, under the layout's name.

A layout can also have **its own button** below the stage — TOWING beside GAUGES — with a `gauges`
entry that names it in the launcher file: see [writing-a-launcher.md](writing-a-launcher.md).

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

### Fonts

A layout can bring its own type, for itself alone (ADR-0042):

```jsonc
"fonts": { "ui": "Segoe UI Variable Display, Segoe UI", "mono": "Segoe UI Variable Text, Segoe UI" }
```

`ui` is the numbers and headings, `mono` the captions; leave either out to keep the theme's. A comma
list falls back in order — Segoe UI Variable is Windows 11's, Segoe UI is everywhere. Weights are
per element: `valueWeight` and `labelWeight` (`thin`, `light`, `regular`, `medium`, `semibold`,
`bold`), and `weight` on text.

**Type over shapes.** The built-in Modern layouts use no panels or boxes: a small grey caption above a
large light number, aligned to its edge (`"align": "left"` on a digital gauge), with space between
groups. Switches are words (`"style": "text"` on an indicator) that light when on.

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
| `compass` | A compass rose with the heading in the middle | `source.sensor` (default `attitude.heading`) and `parts` — see *The compass and the G meter* |
| `gMeter` | A G meter: rings, a crosshair and a ball | `parts` — see *The compass and the G meter* |
| `setpoint` | A set temperature, large, on a thin arc | `source`, `min`, `max`, `label`, `unit`, `format`, `parts` — see *The climate panel* |
| `levels` | A row of steps lit up to the value | the same — see *The climate panel* |
| `indicator` | A pill that lights when its signal is on | `source`, `label`, `parts` — see *The climate panel* |
| `glass` | A frosted glass panel | `radius`, `parts` — see *The climate panel* |
| `warning` | A warning light: an icon lit when its signal says so | `source`, `parts` — see *The console* |

**An LCARS elbow** is two things: a thick panel with one big corner (`"radius": "56,0,0,0"`), and a
black panel over its inside corner with a smaller radius (`"radius": "28,0,0,0"`). LCARS's
`stage\lcars.json` (in your folder; a spare in `catalog/extras/lcars/`) does exactly this.

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

### A sensor instead of a signal

A gauge can read a **tablet sensor** from `catalog/sensors.device.json` instead of a vehicle signal:

```jsonc
{ "id": "pitch", "style": "arc", "x": 0, "y": 0, "width": 200, "height": 200,
  "label": "PITCH", "unit": "°", "format": "0.0", "min": -30, "max": 30,
  "source": { "sensor": "attitude.pitch" } }
```

| Sensor | What it is |
|---|---|
| `attitude.heading` | Which way the truck points, 0–360° |
| `attitude.pitch`, `attitude.roll` | Nose up/down and lean, ° — **needs the mount levelled** |
| `motion.lateralG`, `motion.longitudinalG` | Sideways and fore-aft g — **needs the mount levelled** |
| `location.latitude`, `location.longitude`, `location.groundSpeed` | From the phone's GPS (ADR-0027) |

- **Truck first.** Each sensor names the vehicle signal that would replace it; when the truck
  answers, that is what you see. Otherwise it is the tablet's (or the phone's).
- **It says which.** A small line along the bottom of the gauge reads `TRUCK`, `TABLET`,
  `PHONE · GPS`, or why there is nothing (`NOT LEVELLED`, `NO SENSOR`), in the quality colour. Turn it
  off with `"showSource": false`.
- `scale` and `offset` work; `minus` does not (a sensor source cannot subtract), and `rateHz` is
  ignored — sensors cost no request budget.
- Not both: a source is `signal` **or** `sensor`. A sensor id the catalog lacks says
  `UNKNOWN SENSOR`.

## The compass and the G meter

The COMPASS screen is a layout too — the built-in `compass` (ADR-0039). To change it, copy
`stage\examples\compass.json` up into `stage\` keeping the name, edit it, and use **RELOAD STAGE
LAYOUT** from the three-dot menu on COMPASS. Your `compass.json` replaces the built-in wherever
COMPASS shows it. The two elements below can go in **any** layout — a heading rose beside boost
and volts, say.

```jsonc
{ "id": "heading", "type": "compass", "x": 31, "y": 96, "width": 320, "height": 344,
  "parts": { "mode": "needle", "northColour": "#FF3B30" } },
{ "id": "g", "type": "gMeter", "x": 369, "y": 150, "width": 240, "height": 312,
  "parts": { "range": 0.5, "rings": 2 } }
```

**`compass`** — the rose fills the width (or the height, less a line for the source). With no
usable heading the number reads `———` and the source line says why — never a confident north.

| Part | What it does |
|---|---|
| `mode` | `rose` (default): the card turns under a fixed marker. `needle`: north stays up and a needle turns. |
| `ringColour` | The outer ring, or `none` |
| `cardinalTickColour`, `majorTickColour`, `minorTickColour` | N/E/S/W ticks (accent), the 45° ticks, the rest |
| `tickStep` | Degrees between ticks, default 5 |
| `northColour`, `letterColour`, `letterSize`, `showLetters` | The N, the other letters, their size, whether to draw them |
| `markerColour` | The marker (rose) or needle (needle) |
| `valueColour`, `valueSize`, `showValue` | The heading number |
| `cardinalColour`, `showCardinal` | The NE / SW under it |
| `showSource` | Where the heading came from, under the rose |

**`gMeter`** — reads `motion.lateralG` and `motion.longitudinalG`. **The ball moves the way you
are pushed**: braking throws it up, a right-hand bend throws it left. Until the mount is levelled
(Settings ▸ Mount) there is no ball, only a line saying so. **RESET PEAK G** appears in the
three-dot menu on any stage with a G meter; re-levelling resets the peak too.

| Part | What it does |
|---|---|
| `range` | g at the outer ring, default 1 (0–10) |
| `rings` | How many rings, default 3 (a quarter, a half and the whole range) |
| `ringColour`, `outerRingColour`, `crossColour` | The rings and the crosshair (`none` hides it) |
| `ballColour`, `ballSize` | The ball — by default it is the quality colour |
| `showValue`, `valueColour`, `valueSize`, `labelColour` | The G and PEAK line under the meter |
| `showSource` | Where the reading came from |

## The climate panel

**CLIMATE** in the bottom bar shows a **climate layout** in place of the cards (ADR-0040). It is the
same format as a stage layout, on a canvas **912 wide and 390 tall** — the two bands where the cards
are — and is chosen in **Settings ▸ Themes ▸ CLIMATE LAYOUT**, the same way: follow the theme, or
pick one; SAVE AS, OPEN FOLDER, edit, RELOAD. Yours live in `%LOCALAPPDATA%\DashDeck\climate\`;
`climate\examples\` has the built-in **Modern** panel (`modern.json`) and the Glass theme's frosted
**Glass** one to copy from. A theme names its own with `"climateLayout": "glass"`.

**It is read only.** It shows what the truck reports and changes nothing; a tap does nothing. The
`hvac.*`, `seat.*.climate` and `steeringWheel.heat` signals it reads are **placeholders** until the truck's HVAC module is
found — on the synthetic truck they answer (marked Simulated), on the real one every element shows a
dash. When the real ones are found they go in the vehicle pack and the panel lights up with no change
here.

Any element works on either canvas — a coolant gauge on the climate panel, a setpoint on the stage.
These four were made for it:

```jsonc
{ "type": "glass", "x": 24, "y": 16, "width": 276, "height": 262, "radius": "30" },
{ "id": "driver", "type": "setpoint", "x": 44, "y": 28, "width": 236, "height": 192,
  "label": "DRIVER", "unit": "°", "format": "0.0", "min": 15, "max": 30,
  "source": { "signal": "hvac.driverSetTemp", "rateHz": 0.5 } },
{ "id": "seat", "type": "levels", "x": 64, "y": 222, "width": 196, "height": 44,
  "label": "SEAT", "min": -3, "max": 3, "source": { "signal": "seat.driver.climate" },
  "parts": { "steps": 3, "litColour": "#FF9A4D", "negativeColour": "#6CC8FF" } },
{ "id": "feet", "type": "indicator", "x": 419, "y": 150, "width": 74, "height": 38, "label": "FEET",
  "source": { "signal": "hvac.airflow" }, "parts": { "bit": 1 } }
```

**`glass`** — painted frost: a tint brighter at the top, a sheen, a hairline edge, a soft shadow.
Put it first; what comes after sits on it. It reads best on a near-black `background`.

| Part | What it does |
|---|---|
| `tint` | the glass colour — default a cool white |
| `opacity` | how much of the tint shows, 0–1 — default 0.07 |
| `sheen` | the brighter band across the top, 0–1 — default 0.10; 0 for none |
| `edge` | the outline colour, or `none` — default a faint white |
| `shadow` | the shadow under it, 0–1 — default 0.45; 0 for none |

**`setpoint`** — the number in the middle of an arc over `min`–`max`, with a bright point at the
value. No reading: `– –` and no arc.

| Part | What it does |
|---|---|
| `arcColour`, `trackColour` | the arc up to the value, and the rest |
| `thickness`, `sweep` | arc thickness (default 4) and the degrees it covers (default 240) |
| `glow` | a glow round the arc and point: a colour, or `none` |
| `valueColour`, `valueSize`, `labelColour`, `labelSize` | the number and the caption |
| `showArc` | true/false |

**`levels`** — `steps` cells lit in proportion to the value between `min` and `max`. **Below zero
lights from the other end of the range in `negativeColour`**, so a seat on −3…3 with `steps: 3`
shows heat 2 as two warm bars and cooling 1 as one cool bar — and, with `positiveText` and
`negativeText`, reads `HEAT 2` or `COOL 1`. The caption is top-left; the value
top-right (`OFF` at zero).

| Part | What it does |
|---|---|
| `steps` | how many cells, 1–20 — default 7 |
| `shape` | `bars` (rising, default) or `dots` |
| `litColour`, `unlitColour`, `negativeColour` | lit, unlit, lit below zero |
| `gap` | space between cells, px |
| `labelColour`, `labelSize`, `showValue` | the caption and value line |
| `positiveText`, `negativeText` | a word before the value above and below zero — `HEAT 2`, `COOL 1` for a seat |

**`indicator`** — lit by, in this order: a **`bit`** of the value, under **`below`** (`hvac.airflow` is 1 face, 2 feet,
4 windshield, so `"bit": 1` is FEET; `steeringWheel.heat` is 0 or 1), exactly **`equals`** a value, or at least **`onAt`** (default 1).
Off is an outlined pill. **No reading is dimmed with a dash, never drawn as off.**

| Part | What it does |
|---|---|
| `onAt`, `below`, `equals`, `bit` | when it is lit |
| `litColour`, `litText` | the fill and text when lit |
| `unlitColour` | text and outline when off |
| `radius`, `fontSize` | corner radius (default a pill), text size |
| `style` | `pill` (default) or `text` — just the word, lit in its colour, grey when off |
| `align`, `labelWeight` | where the word sits, and its weight |

## The console

**DASH** in the bottom bar shows a **console layout** below the stage (ADR-0041) — the same format,
on a canvas **912 wide and 390 tall**, chosen in **Settings ▸ Themes ▸ CONSOLE LAYOUT** like the
stage and the climate panel. Yours live in `%LOCALAPPDATA%\DashDeck\console\`; `console\examples\`
has the built-in **Modern** console (`modern.json`) and the Glass theme's arcs-and-frost **Glass** one.
A theme names its own with `"consoleLayout": "glass"`. Your cards are on the stage now, behind **CARDS**.

Signals worth knowing for a console: `vehicle.speed`, `engine.rpm`, `vehicle.odometer`,
`fuel.levelPercent`, `engine.coolantTemp`, `fuel.economy` and `fuel.range` (placeholders until the
truck's own are found — a dash on the truck), and `diagnostics.checkEngine` and
`diagnostics.dtcCount`.

**`warning`** — a warning light. Lit in `litColour`, with a glow, when its signal says so; faint
when off; fainter still with a grey dot when the truck has not said. Lit by the same rules as an
indicator: a `bit`, `below`, `equals`, or at least `onAt` (1).

```jsonc
{ "id": "lowFuel", "type": "warning", "x": 438, "y": 244, "width": 34, "height": 34,
  "source": { "signal": "fuel.levelPercent" }, "parts": { "icon": "fuel", "below": 12 } },
{ "id": "mine", "type": "warning", "x": 478, "y": 244, "width": 34, "height": 34,
  "source": { "signal": "warning.doorAjar" }, "parts": { "icon": "M4 4h16v16H4z", "litColour": "#FF4D4D" } }
```

| Part | What it does |
|---|---|
| `icon` | `checkEngine`, `oil`, `battery`, `coolant`, `fuel`, `seatbelt`, `door`, `brake`, `tpms` — or your own SVG path data on a 24 × 24 grid |
| `onAt`, `below`, `equals`, `bit` | when it is lit |
| `litColour` | lit — default amber |
| `unlitColour` | off — default a faint white |

The `warning.oilPressure`, `warning.seatbelt`, `warning.doorAjar`, `warning.parkingBrake` and
`warning.tirePressure` signals are **placeholders** until the truck's own are found: on the truck
they stay dark with a grey dot.

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
| `valueWeight`, `labelWeight` | `thin`, `light`, `regular`, `medium`, `semibold` or `bold` |
| `unitSize`, `unitColour` | draw the unit smaller and quieter than the number |
| `noData` | what shows with no reading — default `NO DATA`; `–` is quieter |

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
| `align` | `left`, `center` or `right` — the caption above the number, aligned to that edge |
| `labelPosition` | `above` puts the caption over the number; `below` (default) under it, centred |
| `labelGap` | space between caption and number when above, px |

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

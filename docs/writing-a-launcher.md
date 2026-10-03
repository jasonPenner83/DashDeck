# Writing a launcher

The launcher is everything the stage offers — the buttons below it and the nine-dot grid — as one
JSON file (ADR-0038). This page is the reference for every field.

## Where it lives

| File | What it is |
|---|---|
| `%LOCALAPPDATA%\DashDeck\launcher.json` | **Yours.** DashDeck reads this one. It does not exist until you make it. |
| `%LOCALAPPDATA%\DashDeck\launcher.example.json` | The built-in list, written out at every launch as a reference. Editing it changes nothing. |
| `%LOCALAPPDATA%\DashDeck\apps.json` | The apps added in Settings ▸ Apps. Written by the form; you never need to open it. |

The quickest start: **Settings ▸ Apps ▸ STAGE LAUNCHER ▸ MAKE IT MINE** writes `launcher.json`
from the built-in list. **OPEN FOLDER** shows it. Edit it in Notepad, save, and tap **RELOAD** —
the buttons rebuild at once, and whatever is on the stage keeps running.

**Your file replaces the built-in list outright.** Anything you leave out is not offered, and the
order you write is the order shown. Delete `launcher.json` to go back to the built-in list.

## The shape

```jsonc
{
  "description": "Mine.",              // for you; shown nowhere
  "startOn": "GAUGES",                 // the stage at launch
  "quickBar": [ "GAUGES", "TOWING", "MAPS", "PHONE", "SPOTIFY" ],
  "entries": [ … ]
}
```

Comments (`//` and `/* */`) and trailing commas are fine.

| Field | Meaning |
|---|---|
| `startOn` | The entry on the stage when DashDeck starts. Left out, or not offered: GAUGES, then the first entry. A `--stage` argument still wins. |
| `quickBar` | Up to **five** entry names for the buttons below the stage, left to right. Left out: the first five available entries. Whatever is on the stage **always** has a button — it takes the last place if it is not one of these. A name that is not installed (an app whose program is missing) is skipped. |
| `entries` | Everything the grid offers, in order. |

## An entry

Every entry has a `name` and a `type`. The name is the button's caption **and** the stage's name
while it shows (the status strip says it; the button lights). Names are upper-cased and must be
unique.

These work on every entry:

| Field | Meaning |
|---|---|
| `detail` | The line under the name in the grid. A sensible one is made up when left out. |
| `group` | The heading it sits under in the grid. **SCREENS**, **WEB** or **APPS** by type when left out. Headings appear in the order they are first used — so `"group": "MEDIA"` on SPOTIFY, MUSIC and NUVIO makes a MEDIA section. |
| `hidden` | `true` keeps the entry in the file but does not offer it — a way to put MUSIC away without losing its URL. |
| `keepPlaying` | `true` makes it an audio/video source: switching from it to a silent screen keeps it playing behind (with **keep stage audio** on in Settings ▸ Display — ADR-0026). Phone and video default to `true`, everything else to `false`. |

### The types

| `type` | What it puts on the stage | Its own fields |
|---|---|---|
| `gauges` | A stage layout (`docs/writing-a-stage-layout.md`). | `layout` — a layout's file name (`lcars`, `towing`) or id. Left out: the theme's layout, or the one chosen in Settings ▸ Themes ▸ STAGE LAYOUT. With it, this button always shows that layout, whatever the theme. |
| `cards` | Your cards (ADR-0041) — the arranged dash, five rows to a page. Edit them with MODIFY WIDGETS in the three-dot menu. **Always offered:** a file that never mentions `cards` gets CARDS at the end of the grid; list it with `"hidden": true` to really hide it. | — |
| `clock` | Time and weather. | — |
| `compass` | Heading, attitude, G — the `compass` stage layout (ADR-0039): yours if you saved a `compass.json` in `stage\`, the built-in otherwise. | `layout` — show a different stage layout under this name. To change what the compass screen draws, see *The compass and the G meter* in [writing-a-stage-layout.md](writing-a-stage-layout.md). |
| `phone` | Android Auto and CarPlay through the dongle (ADR-0019). | — |
| `video` | A video file, played by VLC. | `path` — the file. Left out or missing: it asks when chosen. |
| `web` | A web page in WebView2. | `url` — `https://…` (or `http://`). `zoom` — 0.25 to 5 (1 is 100 %). Left out: Settings ▸ Display ▸ web scale decides. |
| `app` | A Windows program, placed over the stage where that works (ADR-0021). | `paths` — where it might be, most likely first; the first that exists is launched. `%LOCALAPPDATA%`, `%ProgramFiles%` and the like are expanded. Remember `\\` for each `\` in JSON. `arguments` — its command line. `scrollStrip` — `right` (default), `left` or `off`: the strip beside the program that scrolls it for a finger (ADR-0046). `scrollBy` — `message` (default) or `input`: try `input` if the strip does nothing. |
| `userApps` | Not an entry: **where the apps added in Settings ▸ Apps go**. No name. Left out, they go last. Give it a `group` to head them differently. |

## Examples

**A stage layout on its own button.** Make a layout called `towing.json` (Settings ▸ Themes ▸ STAGE
LAYOUT ▸ SAVE AS), then:

```jsonc
{ "name": "TOWING", "type": "gauges", "layout": "towing", "detail": "Trans temp, boost, volts" }
```

**A web page, zoomed out to show more.**

```jsonc
{ "name": "RADAR", "type": "web", "url": "https://weather.gc.ca/radar/", "zoom": 0.75, "group": "WEATHER" }
```

**Chrome on a page, as a program.**

```jsonc
{
  "name": "CHROME", "type": "app",
  "paths": [ "%ProgramFiles%\\Google\\Chrome\\Application\\chrome.exe" ],
  "arguments": "--new-window https://www.youtube.com",
  "keepPlaying": true
}
```

**Renaming.** `{ "name": "TIME", "type": "clock" }` — the button, the grid and the status strip all
say TIME.

**A short bar.** `"quickBar": [ "GAUGES", "MAPS" ]` gives two buttons; the rest stay in the grid.

## When something is wrong

Nothing in the file can empty the launcher.

- An entry with an unknown `type`, no `name`, a name used twice, a `web` entry without an
  http(s) `url`, or an `app` without `paths` is **left out**, and Settings ▸ Apps ▸ STAGE LAUNCHER
  names it in amber.
- A `quickBar` or `startOn` name that is not offered is dropped and named. More than five in
  `quickBar`: the extras are dropped and named.
- A `zoom` outside 0.25–5 is ignored (Settings ▸ Display decides) and named.
- A file that is not JSON, or offers nothing, is not used at all: the built-in list stays, and the
  first amber line says why.

The add-an-app form in Settings ▸ Apps refuses a name the launcher already uses.

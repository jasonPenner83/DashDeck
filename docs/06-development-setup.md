# DashDeck — Development Setup

## What you need

| | |
|---|---|
| **.NET 10 SDK** | https://dotnet.microsoft.com/download/dotnet/10.0 — the LTS release (ADR-0009). |
| **Visual Studio 2022 Community** | Free. Needed for the WPF shell: XAML hot reload and the designer matter when sizing touch targets for a moving vehicle. Install the *.NET desktop development* workload. |
| **VS Code** *(optional)* | Fine for everything except XAML. Install the **C# Dev Kit** extension. |
| **Git** | |

No OBD-II adapter is needed. None of P0 or P1 requires one.

## Get it running

```bash
git clone https://github.com/jasonPenner83/DashDeck.git
cd DashDeck
git checkout develop

dotnet test
dotnet run --project src/DashDeck.DebugConsole -- cold-start-city --seconds 60
```

If the tests pass and the console prints live signals, the whole engine works.

## Putting it on the tablet

```powershell
pwsh -File publish.ps1 -Shortcut
```

Produces `dist\DashDeck\` — a **self-contained folder deploy**. No .NET runtime to install,
no VLC to install, no registry, no services, no kiosk mode. That is constraint C1 taken
literally: uninstalling DashDeck is deleting the folder, and the tablet goes back to being
an ordinary personal machine.

Copy the folder anywhere and launch `DashDeck.Host.exe`. **Escape closes it.** Pin the
shortcut to the taskbar or Start if you want it a tap away — that is as close to "installed"
as this gets, deliberately.

Expect **around 420 MB across ~1700 files**. Most of that is not us: a self-contained WPF
runtime plus VLC's full plugin set, which is the price of playing whatever you actually own
rather than whatever Windows Media Player recognises. It can be trimmed later by pruning
unused VLC plugins, at the risk of discovering the missing one on a back road.

### Running it

```powershell
DashDeck.Host.exe                          # cold-start-city, empty stage
DashDeck.Host.exe highway-cruise           # a different scripted drive
DashDeck.Host.exe --video "D:\clip.mkv"    # video on the stage
DashDeck.Host.exe --stage COMPASS          # open on a named occupant
DashDeck.Host.exe --picker                 # open on the stage picker
DashDeck.Host.exe --nav SETTINGS           # open on a destination below the stage
DashDeck.Host.exe --theme NIGHT            # force a palette without waiting for sunset
DashDeck.Host.exe --accent CYAN            # force an accent — a preset name...
DashDeck.Host.exe --accent "#26C6DA"       # ...or any colour that passes validation
DashDeck.Host.exe --edit                   # open with the dash in edit mode
DashDeck.Host.exe --level                  # capture the mount reference at launch
DashDeck.Host.exe --menu                   # open the overflow menu
DashDeck.Host.exe --page 1                 # open on a later page of cards
DashDeck.Host.exe --edit-card 2            # open the card editor on the third card
```

`--theme` and `--accent` **preview without saving** (ADR-0014). Everything chosen in Settings
is written to `%LOCALAPPDATA%\DashDeck\settings.json` the moment it changes, and a development
flag that went through the same path would make looking at night mode permanent.

Note that the drive name is positional and everything else takes a value *except* `--picker`,
`--edit`, `--level` and `--menu`, which are switches. `--nav SETTINGS` once put the shell on the floor with
*Unknown drive 'SETTINGS'*, because "the first argument without a dash" is a flag's value as
often as it is the drive. `App.Switches` is the list that keeps that honest — a new
valueless flag has to be added to it.

These flags are for reviewing states that otherwise need a finger: edit mode is a
600 ms hold on a card, and the card editor is three gestures deep. Both are worth putting in
a `--shot`.

### Arranging the dash

The cards are yours to arrange (ADR-0015). Hold any card for a moment to enter edit mode: the
nav strip becomes a toolbar, each card gains a ✕ and ‹ › to move it, and tapping one opens a
full-screen editor for its signal, size, style, unit, rate, decimals and priority. Swipe the
dash sideways for more pages; the dots sit under the cards.

Two things worth knowing:

- **Only the visible page asks the truck for anything.** Cards on other pages withdraw their
  signal declarations and re-declare on return, because every card is demand on one shared
  link (ADR-0004). This is why paging is snapped rather than free-scrolling — a page that
  comes to rest half way leaves "which page am I on" without an answer, and that question is
  what decides which cards are live.
- **The `req/s` in the status strip is the adapter's measured *capability*, not the load.**
  It barely moves when cards are added or suspended, so it is not the way to check any of
  this. `DashboardViewModelTests` is.

The arrangement lives in `%LOCALAPPDATA%\DashDeck\dashboard.json`, beside `settings.json`, and
is plain enough to edit or hand to someone else:

```jsonc
{ "id": "trans", "signal": "engine.coolantTemp", "label": "COOLANT",
  "priority": "Normal", "rateHz": 0.5, "format": "0",
  "width": 2, "style": "Bar", "unit": "Fahrenheit" }
```

Every field has a default and unknown ones are ignored, so a file from an older or newer build
still loads. A card naming a signal the catalog does not define is **kept and rendered as
UNAVAIL** rather than dropped — deleting someone's card because a catalog moved is the worst
possible way to tell them. RESET in the edit toolbar puts the shipped six back.

The stage is chosen at runtime from the **stage chip**, top right of the stage — tap it for
a grid of app buttons. `--stage` and `--video` just skip the tap at launch.

**Occupant controls live in the three-dot menu** (ADR-0022), above MODIFY WIDGETS: video
transport, web back/reload/home, RESTART for a native app. They used to have a band of their
own, which cost a quarter of the stage to carry two buttons.

### The compass, and where its numbers come from

`--stage COMPASS` opens it: a bearing, a G meter, and vehicle pitch and roll, with road speed
and outside air temperature beside them from the signal catalog.

**Level it first.** Everything measured against the mount — pitch, roll and both axes of G —
refuses to render a number until you have. Park somewhere flat, put the tablet in its cradle,
and use **Settings → MOUNT → LEVEL THE MOUNT**. It sat on the compass until ADR-0022 took that
row back; it is a calibration, not a driving control. The reason it exists is not fussiness: a Surface on a kickstand reads 69° of pitch
and 0.91 g on one axis while sitting perfectly still, so raw device attitude would put a third
of a g of cornering force on screen in a stationary truck. `--level` does the same thing at
launch, for screenshots.

Levelling records what "flat and pointing forward" means for this tablet in this mount, in
`%LOCALAPPDATA%\DashDeck\mount.json`. Gravity is then removed as a vector and what remains is
resolved into axes built from the mount rather than from the tablet — which is what lets a
cradle at any angle still tell braking from cornering. **Re-level if the mount moves**; nothing
notices on its own yet (F18). The ball moves the way you are pushed, so braking throws it
towards the top of the screen.

**Every one of them asks the truck first** (ADR-0016, ADR-0017), and none of the truck signals
exist yet. They are named in `catalog/sensors.device.json`, in each sensor's `prefer` field:

| Sensor | Prefers | Needs levelling |
|---|---|---|
| `attitude.heading` | `vehicle.heading` | no — measured against the earth's field |
| `attitude.pitch` | `vehicle.pitch` | yes |
| `attitude.roll` | `vehicle.roll` | yes |
| `motion.lateralG` | `vehicle.lateralAccel` | yes |
| `motion.longitudinalG` | `vehicle.longitudinalAccel` | yes |

Those signals are deliberately **not in the signal catalog**. The F-150 knows all of them —
stability control cannot work without lateral acceleration — but they are Ford messages behind
PID discovery (R2) and the Gateway Module (R4, Q5). Guessing a PID would decode into a
confidently wrong number that sits inside its own plausible range, where the catalog's
`min`/`max` guard cannot catch it. **Add one to the signal catalog and the value switches to
the truck with no code change** — that is what `prefer` is for.

So today they fall back to the tablet, and the stage says which:

| It says | Meaning |
|---|---|
| `TRUCK` | The vehicle supplied it. Does not happen yet. |
| `TABLET` | The tablet's accelerometer or inclinometer, relative to the levelled mount. |
| `NOT LEVELLED` | Waiting for LEVEL. Shown rather than a raw device axis. |
| `TABLET · TRUE` | The tablet's magnetometer, corrected to true north by Windows using a location fix. |
| `TABLET · MAGNETIC` | The same sensor with no fix, so magnetic north — worth about ten degrees on the prairies. |
| `NO SENSOR` | No magnetometer on this machine. |

Treat the tablet reading with suspicion in the cab: it is a magnetometer surrounded by steel,
speakers and a metal mount, and it measures the *tablet's* orientation rather than the
truck's. Windows' own confidence is rendered as the usual quality badge, and `Approximate` is
deliberately shown as **Stale** rather than Live.

**When the PID is found, this becomes a JSON edit.** Add `vehicle.heading` to the catalog and
the compass switches over with no code change — that is the whole point of the arrangement.

Two development flags, both useful for looking at the thing without a camera:

```powershell
DashDeck.Host.exe --unplug 20              # pull the adapter after 20s, watch it go stale
DashDeck.Host.exe --shot out.png --shot-after 30
```

`--shot` renders the layout to a PNG at its true 912 × 1368 and exits, which is easier than
screen-grabbing a 200%-scaled display. It cannot capture video — `VideoView` draws into a
child window — so it writes the player's actual state to `out.png.txt` alongside.

### Orientation

The shell is authored at 912 × 1368, the tablet's **portrait** size, and a `Viewbox` fits
that to whatever window it gets. Rotate the Surface into portrait and it is 1:1. In
landscape it still runs, letterboxed into a strip down the middle — fine for development,
not the intended shape.

Crashes are written to `%LOCALAPPDATA%\DashDeck\crash.log` rather than vanishing, and settings
to `settings.json` beside it. Both are outside `dist\DashDeck`, which `publish.ps1` deletes
and rewrites on every build — that is deliberate, and it is why preferences survive an update
(ADR-0014). Deleting that folder resets DashDeck to factory.

## The debug console

The P0 harness. Not a stand-in for the shell — it exists so the entire stack below the UI
can be run, watched and recorded on any machine, with no truck, no adapter and no Windows.
Everything it drives is the real pipeline.

```bash
# Drives: cold-start-city, highway-cruise, towing-pull, idle
dotnet run --project src/DashDeck.DebugConsole -- highway-cruise --seconds 120

# Record a session to a replayable log
dotnet run --project src/DashDeck.DebugConsole -- cold-start-city --record captures/drive.jsonl

# Replay it — nothing above the transport knows the difference
dotnet run --project src/DashDeck.DebugConsole -- --replay captures/drive.jsonl --seconds 30
```

It prints the polling plan before it starts, including which signals the arbiter degraded
and why. When something looks wrong on a dash, that plan is the first place to look.

## Where things are

| Project | Target | Builds on |
|---|---|---|
| `DashDeck.Abstractions` | `net10.0` | any OS |
| `DashDeck.Vehicle` | `net10.0` | any OS |
| `DashDeck.Core` | `net10.0` | any OS |
| `DashDeck.Simulator` | `net10.0` | any OS |
| `DashDeck.DebugConsole` | `net10.0` | any OS |
| `DashDeck.Abstractions.Wpf` *(P0.5)* | `net10.0-windows` | **Windows only** |
| `DashDeck.Host` *(P0.5)* | `net10.0-windows10.0.19041.0` | **Windows only** |

The split is deliberate (ADR-0010): the engine and all its tests build anywhere, which is
what allowed P0 to be finished before any Windows machine was involved. Keep it that way —
a UI reference sneaking into `Abstractions` would be a silent and expensive regression.

## Adding a signal

Edit `catalog/signals.obd2-standard.json`. No rebuild, no code:

```jsonc
{
  "id": "engine.oilTemp",
  "name": "Engine Oil Temperature",
  "bus": "Hs",
  "pid": 92,
  "decode": { "byteOffset": 0, "byteLength": 1, "signed": false, "scale": 1, "offset": -40, "unit": "°C" },
  "defaultRateHz": 0.5,
  "min": -40, "max": 210
}
```

`min`/`max` are a correctness guard, not decoration: a wrong decode spec usually produces a
wildly out-of-range number, and out-of-range readings are dropped rather than displayed.
Ford-specific PIDs go in a separate file once discovered on the truck, so trial and error
never churns the known-good standard set.

## Adding a sensor

`catalog/sensors.device.json` is the second catalog: values the *tablet* can measure about the
vehicle it is riding in (ADR-0017). No rebuild, no code:

```jsonc
{
  "id": "motion.lateralG",
  "name": "Lateral G",
  "unit": "g",
  "source": "Accelerometer",         // Compass | Accelerometer | Inclinometer
  "channel": "Lateral",              // Heading | Pitch | Roll | Lateral | Longitudinal
  "prefer": "vehicle.lateralAccel",  // the vehicle signal that supersedes this one
  "defaultRateHz": 20,
  "needsMountReference": true,
  "min": -2, "max": 2
}
```

`prefer` is the field that matters, and it is why there are two catalogs rather than one: it
names the signal that takes over the moment the signal catalog defines it. `min`/`max` are the
same correctness guard the signals get — three g of lateral force in a pickup means the sensor
is confused, not the truck. A channel the source cannot produce fails validation at load,
because that is the mistake most likely to be made by hand.

**Device sensors never enter the arbiter's plan.** Reading a magnetometer is not traffic on the
OBD-II link, so it costs nothing against a budget the whole dash shares. Only the truck-supplied
half of a sensor is declared.

## Conventions worth knowing before you write code

- **Never `DateTimeOffset.Now`.** Take `IClock`. Wall-clock reads cannot be replayed or
  tested against a scripted drive.
- **Components declare signals, they never poll.** If you want a PID, the catalog is
  missing a definition.
- **Render quality.** Every value carries `Live` / `Stale` / `Unavailable` / `Simulated`.
  A confidently wrong number on a dash is worse than a blank one.
- **Nothing blocks the UI thread.** The dash must not freeze while someone is driving.

`TreatWarningsAsErrors` is on. That is intentional — a warning ignored in a vehicle app is
a warning ignored at 100 km/h.

## Branches

`develop` for day-to-day work; `main` is the build that goes on the truck tablet. Never
commit directly to `main`. See [`05-releases-and-branching.md`](05-releases-and-branching.md).



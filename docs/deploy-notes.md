# Deploy notes — the build on the tablet

Operational notes for the self-contained build carried to the truck. Updated whenever the
deploy is refreshed. This is not release history (that is git tags and
[05-releases-and-branching](05-releases-and-branching.md)); it is "what is in the folder on the
Surface right now, and what to check."

## How to deploy

Deploy from **`main`** — the tagged build (see [05-releases-and-branching](05-releases-and-branching.md)).
`develop` builds are for testing only.

```powershell
git switch main; git pull
powershell -ExecutionPolicy Bypass -File publish.ps1 -Shortcut   # or pwsh, if PowerShell 7 is installed
```

Produces `dist/DashDeck/` — a self-contained folder: its own .NET runtime, the signal catalog,
LibVLC, and every component under `plugins/`. Copy the whole folder to the Surface (or launch it
in place) and run `DashDeck.lnk` / `DashDeck.Host.exe`. Tap it **once** — it takes a few seconds
to find the adapter, and a second tap now just brings the same dash forward. **CLOSE DASHDECK** in
the three-dot menu (two taps) closes it. Uninstalling is
deleting the folder (constraint C1).

The publish summary should read: catalog `included`, libvlc `included`, components `5 included`.
If components says `none`, a component did not build — check `components/` before carrying it out.
To confirm the loader in the deployed folder without opening the shell:
`DashDeck.Host.exe --components <outfile>` should list all five.

To attach the build to its GitHub Release (rollback is unzipping the previous one):

```powershell
Compress-Archive -Path dist\DashDeck\* -DestinationPath dist\DashDeck-v0.6.0-win-x64.zip -Force
```

## What this build contains

**`v0.6.0`** — released from `main` (the tag goes on the release merge; see the release steps). It
adds the following to `v0.5.0`:

- **The launcher is a file** (ADR-0038). `%LOCALAPPDATA%\DashDeck\launcher.json` lists every stage
  option — screens, web pages (with their own zoom), Windows programs — in order, with the five
  buttons below the stage and the opening stage. **Settings ▸ Apps ▸ STAGE LAUNCHER** shows it, with
  MAKE IT MINE, OPEN FOLDER and RELOAD. Without a file, the launcher is exactly as before.
  Reference: [writing-a-launcher](writing-a-launcher.md).
- **COMPASS is a stage layout** (ADR-0039): a rose, a G meter, and gauges that read the tablet's
  sensors with their source written under them. Copy `stage\examples\compass.json` up into
  `stage\` to change it. RESET PEAK G is in the three-dot menu on COMPASS.
- **One DashDeck at a time.** A second launch brings the running one forward instead of starting a
  copy with no truck.
- **Module scans and sweeps are kept** in `%LOCALAPPDATA%\DashDeck\discovery.json` and shown again at
  launch, marked SAVED. The file can hold the VIN — keep it on the tablet.
- **The signal editor's TEST button is on screen** again (it was pushed off the right edge).

Check in the truck: the walkthroughs in [08-in-vehicle-testing](08-in-vehicle-testing.md) —
*The launcher as a file*, *COMPASS as a stage layout*, *One DashDeck at a time*, *Module scans and
sweeps are kept across a restart* and *The signal editor's TEST button is on screen*.

And from **`v0.5.0`** — released 2026-10-02 from `main` (tag on the #27 merge, `ee8911b`):

- **The stage as a file** (ADR-0037). The GAUGES stage is drawn from a JSON layout of gauges
  (dial, arc, bar, LCARS bar, digital), text, a clock and panels on a 912 × 636 canvas.
  - A theme brings its stage: LCARS brings the LCARS stage.
  - **Settings ▸ Themes ▸ STAGE LAYOUT** chooses a layout, or follows the theme, and has SAVE AS,
    DELETE, OPEN FOLDER and RELOAD. RELOAD STAGE LAYOUT is in the three-dot menu.
  - Your layouts are in `%LOCALAPPDATA%\DashDeck\stage\`.
  - Writing one: [writing-a-stage-layout](writing-a-stage-layout.md).
- **Honest gauges.** No reading means no needle and NO DATA; Stale is dimmed; every gauge has a
  quality dot. On the truck, **OIL TEMP reads NO DATA**, which is correct: the truck doesn't report
  oil temperature.
- **Examples** are written to `themes\examples\` and `stage\examples\` on every launch: the
  shipped files, plus the built-in theme and cluster as JSON. They're references only, never loaded.
- **CLOSE DASHDECK** in the three-dot menu (from v0.4.1): two taps to quit, and quitting now ends the
  process and frees the adapter's port.

And from **`v0.4.0`** (released 2026-10-02):

- **Themes** (Settings ▸ Themes, ADR-0036). A theme restyles the whole dash; tap one to wear it.
  IMPORT, EXPORT, SAVE AS, DELETE (yours only), OPEN FOLDER and RELOAD.
  - **LCARS (inspired)** ships in `catalog/themes/` with its Antonio font files and their licence.
  - Your own themes are in `%LOCALAPPDATA%\DashDeck\themes\`; the choice is remembered in
    `settings.json`, so a redeploy keeps it.
  - Writing one by hand: [writing-a-theme](writing-a-theme.md).
- **Settings ▸ Sensors ▸ MODULES** (ADR-0035). **SCAN FOR MODULES** finds every module on HS-CAN
  and MS-CAN by its part number; tap one to sweep its identifiers, and **+ DEFINE** opens the signal
  editor on one it found. Reads only; refused while moving. A signal can now name a module
  (`"module": "726"`).
- Two reply-parsing fixes: several modules answering the broadcast read as the first answer, not
  garbage; a refusal from a module (`7F …`) is recognised as an answer.

And from **`v0.3.0`**, the first build that reads the real truck:

- **The real adapter** (ADR-0031, ADR-0034). Choose it in **Settings ▸ Vehicle ▸ OBD-II
  adapter**, from a list of **tested ports**. Each row shows the adapter's identity, its baud rate
  and the voltage at the OBD port, or IN USE, NO ADAPTER or PHONE GPS.
  - The **SIM** badge goes when the dash is live.
  - A dash that came up simulated goes live by itself when the adapter answers.
  - A knocked cable shows **ADAPTER LOST — RECONNECTING** and recovers without a restart.
  - A renumbered COM port is followed.
  - What the link does is logged to `%LOCALAPPDATA%\DashDeck\adapter.log`.
- **Settings ▸ Vehicle ▸ VIN** (ADR-0033): read from the truck or typed, decoded once by NHTSA
  (needs internet the first time), cached in `vehicle.json`, every field correctable. It picks the
  F-150 2.7 vehicle pack.
- **Settings ▸ Sensors** (ADR-0032):
  - every vehicle signal and tablet sensor, with what the truck has said;
  - **SCAN THE TRUCK** for supported standard PIDs;
  - an editor with **TEST**, saving to `signals.user.json` (applied at the next launch,
    **RESTART NOW** in the section).
- Everything in `v0.1.0`:
  - the six-band shell;
  - stage occupants (clock and weather, compass, video, web applets, native and user-added
    apps), with the ▶ pill and "keep stage audio";
  - the phone's GPS over Bluetooth;
  - the arranged, paged dash with the grouped, searchable picker;
  - the five components in `plugins/`;
  - Settings sections for Appearance, Mount, Display, Vehicle and Diagnostics.

Check the deploy carried the theme: the publish copies `catalog/themes/` (JSON, fonts and the
font licence) beside the executable. If LCARS appears in Settings ▸ Themes but its lettering is wide
and ordinary, the `.ttf` files did not make it into the folder.

## Known limits — read before deciding something is broken

- **Fuel Economy, Avg Economy and Range Estimator read blank on the real truck.** They need the
  engine fuel-rate PID (`5E`), and this F-150 supports neither that nor mass air flow (`10`).
  Bring-up found this on 2026-10-01. The speed-density replacement is decided (ADR-0030) but not
  built. On the simulator they still read.
- **Tire Pressure shows a dash at every corner on the real truck.** Its PIDs are placeholders
  until Ford's body-module values are found (R2). On the simulator, the rear-left tyre reads low on
  purpose.
- **The F-150 2.7 vehicle pack has no signals yet.** Ford mode 22 values go there once TEST has
  confirmed them on the truck.
- **Close FORScan before launching DashDeck.** Only one app can hold the adapter's port. TEST
  PORTS shows IN USE when something else has it.
- **Signal edits apply at the next launch**, not immediately. So do the vehicle profile and the
  fuel tank size.
- **Levelling is required** before pitch, roll, G or the compass attitude render: Settings ▸
  Mount ▸ LEVEL THE MOUNT, parked, with the tablet in its cradle.
- **Stage occupant is not remembered** across launches (F12); the dash comes up on the clock.
- **The TPMS photo is local-only.** The overhead truck photo is not in the repo (no right to
  publish it). A build made on a machine that has `components/Tpms/truck.png` ships it; any
  other build draws the vector truck. Both are correct.

## What to check in the truck

Step-by-step walkthroughs, with what to expect and what a failure looks like, are in
[08-in-vehicle-testing](08-in-vehicle-testing.md). For this build:

- **The adapter link**, sections A–E: live on launch, a late adapter, a knocked cable, a moved
  USB socket, testing ports while it searches. A–C passed on 2026-10-02 after the reconnect fix.
- **Settings ▸ Sensors**: the scan should list Fuel Rate and Mass Air Flow as *not supported by
  the truck*. Check TEST rpm against the tachometer.
- **VIN lookup**: read from the truck, and decoded as a 2019 Ford F-150 with the 2.7 L.
- **Settings ▸ Themes**: passed on the tablet 2026-10-02 (LCARS approved).
- **Closing by touch**: passed on the Surface 2026-10-02. Nothing left in Task Manager.
- **The stage as a file**: ran on the PC on 2026-10-02. In the truck, with the engine running,
  OIL TEMP should read NO DATA and the other gauges should move.
- **Settings ▸ Sensors ▸ Modules**: **not yet walked through** — the next thing to do in the truck.
  Close FORScan first; send the module list it finds (cover the `22 F190` row, the VIN).

The `v0.1.0` checks still apply: touch gestures on glass, reach from the driver's seat, levelling,
and day/night legibility.

When something turns up, it is fixed on `develop`, merged to `main` and tagged, and the deploy
refreshed — the truck has been
the most valuable source of bugs so far.

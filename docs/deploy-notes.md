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
in place) and run `DashDeck.lnk` / `DashDeck.Host.exe`. Escape closes it. Uninstalling is
deleting the folder (constraint C1).

The publish summary should read: catalog `included`, libvlc `included`, components `5 included`.
If components says `none`, a component did not build — check `components/` before carrying it out.
To confirm the loader in the deployed folder without opening the shell:
`DashDeck.Host.exe --components <outfile>` should list all five.

To attach the build to its GitHub Release (rollback is unzipping the previous one):

```powershell
Compress-Archive -Path dist\DashDeck\* -DestinationPath dist\DashDeck-v0.3.0-win-x64.zip -Force
```

## What this build contains

**`v0.3.0`** — tagged 2026-10-02 on `main`. The first build that reads the real truck. It adds
the following to `v0.1.0`:

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

Not in this build: **Settings ▸ Sensors ▸ MODULES** (ADR-0035) is on `develop`, waiting for the
next release.

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

The `v0.1.0` checks still apply: touch gestures on glass, reach from the driver's seat, levelling,
and day/night legibility.

When something turns up, it is fixed on `develop`, merged to `main` and tagged, and the deploy
refreshed — the truck has been
the most valuable source of bugs so far.

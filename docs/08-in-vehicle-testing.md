# Testing in the truck

Every feature that touches the vehicle, the adapter, or what is on the screen in the cab
comes with a walkthrough here: **how to check it on the real hardware, in the truck**. The unit
tests and the synthetic truck prove the logic. They cannot prove that a real OBDLink EX, a real
2019 F-150 and a Surface on a mount behave the way the fakes do, and the bugs that matter most
in this project have been found there: touch that never reached a button, a reconnect that
forgot the adapter's settings.

This is a working agreement (see `CLAUDE.md`): a feature isn't done until it has a walkthrough
here, and the person who built it has walked the owner through it.

## Before any test in the truck

- **Parked, parking brake on.** Nothing here is tested while driving. Where a step says
  "engine running", that means idling in park, **outdoors** — never in a closed garage.
- **The adapter is seated** in the OBD-II port under the driver's side of the dash, left of
  the steering column, and its USB cable runs to the Surface.
- **The dash is on the real adapter.** Choose the adapter in **Settings ▸ Vehicle ▸ OBD-II
  adapter** and check that the **SIM badge has gone** from the status strip. While it shows, the
  numbers are from the simulator, whatever else is plugged in.
- **Note the ignition state** for each step: **off**, **on** (engine off, dash lit), or
  **running**.
- **When something doesn't match**, write down the step number and what you saw, and take a phone
  photo of the screen. "Step 4: said NO DATA instead of a VIN" is enough to start from. For
  anything about the adapter connection, also send `%LOCALAPPDATA%\DashDeck\adapter.log`: it
  records each time the adapter was found, each drop and the error behind it, and why each
  attempt to find it again failed.

Each walkthrough lists what you need, the steps with what you should see, and what a failure
looks like.

---

## Settings ▸ Sensors — scan, inventory and TEST (ADR-0032)

**You need:** the truck, the adapter, ignition **on** (running is better for step 5).

1. Open **Settings ▸ Sensors**. **Expect:** every vehicle signal grouped by function, and the
   tablet and phone sensors at the bottom. Rows read `NOT ASKED` until something on screen wants
   them. That's correct: opening this page never polls the truck.
2. Go back to the dash for ten seconds, then return. **Expect:** the signals on your visible
   dash page now show live values, with a green dot (Live), not the blue of Simulated.
3. Press **SCAN THE TRUCK**. It takes a few seconds. **Expect:** a summary like
   `HS-CAN: n PIDs supported · MS-CAN: no answer to PID 00 …`.
   - MS-CAN saying *no answer* is correct: Ford's body modules don't answer the standard
     question.
   - **"In the catalog, not supported by the truck"** should include **Engine Fuel Rate** and
     **Mass Air Flow** (`5E` and `10`). Bring-up on 2026-10-01 found this truck answers neither.
     If they're missing from that list, the scan is wrong.
4. Look at **"Supported by the truck, not in the catalog"**. Tap one. **Expect:** the editor
   opens filled in, with a note saying whether the formula is from the standard. Press **TEST**.
   **Expect:** raw bytes, then `Decodes to …` with a sensible number. Press **CANCEL**. Save only
   ones you want on the dash.
5. Check TEST against something you can see. Press **+ NEW SIGNAL** and set:
   - **mode** `01`, **PID** `0C`, **length** `2 BYTES`;
   - **scale** `0.25`, **offset** `0`, **unit** `rpm`.

   With the engine **running**, press **TEST**. **Expect:** about 600–800 rpm at idle, close to
   the tachometer. Press **CANCEL**.
6. Check the failure path. Still in the editor, set **mode** `22` and **PID** `1234`, then press
   **TEST**. **Expect:** `NO DATA — nothing on HS-CAN answered 22 1234.` The app keeps running
   and the dash keeps updating.
7. Optional, to check saving: save a signal from step 4, then press **RESTART NOW**. **Expect:**
   the app relaunches, the signal shows **YOURS** in the list, and it can be picked in the card
   editor.

**A failure looks like:** the scan hanging for more than about 30 seconds; Fuel Rate and Mass
Air Flow missing from the "not supported" list; TEST rpm far from the tachometer; or the dash
freezing while a scan or TEST runs.

---

## Settings ▸ Vehicle — VIN lookup (ADR-0033)

**You need:** the truck, the adapter, ignition **on**, and internet on the tablet (Wi-Fi or a
phone hotspot) for LOOK UP.

1. Open **Settings ▸ Vehicle**. Press **READ FROM TRUCK**. **Expect:** the VIN fills in and the
   line under it says `Looks right.`.
   - Compare it with the VIN at the bottom of the windscreen on the driver's side, or on the
     driver's door-jamb sticker. Every character should match.
   - If it says it came from the **synthetic truck**, the dash is still simulated; see "Before
     any test".
2. Press **LOOK UP**. **Expect:** `Decoded.` and the fields filled in: **2019**, **FORD**,
   **F-150**, engine **2.7**, **6** cylinders, **Gasoline**, turbo **YES**. Trim may be blank,
   since NHTSA often doesn't know it. Type it in if you like; the source line then says
   `+ your corrections`.
3. **Expect** the pack line: `Signal pack: Ford F-150 2.7 EcoBoost (2018–2020) (0 signals) —
   from the next launch.` Press **RESTART NOW**.
4. After the relaunch, open **Settings ▸ Vehicle**. **Expect:**
   - the same vehicle, with no internet needed this time (it's cached);
   - the pack line no longer says *from the next launch*.
5. Optional, to check the offline path: turn Wi-Fi off and press **LOOK UP** again. **Expect:**
   `Couldn't decode it: couldn't reach NHTSA's decoder … Nothing was changed.` The fields stay
   as they were.

**A failure looks like:** READ FROM TRUCK saying the truck didn't answer with the ignition on; a
VIN that differs from the windscreen by even one character; LOOK UP decoding a different engine
than the 2.7; or the vehicle being forgotten after a restart.

---

## Settings ▸ Vehicle — the adapter link and tested ports (ADR-0034)

**You need:** the truck, the adapter in the OBD-II port, and its USB cable to the Surface.

**A — The tested-ports list** (ignition **on**, engine off)

1. Open **Settings ▸ Vehicle**. The ports are tested as it opens, and rows fill in within a few
   seconds. **Expect:**
   - the adapter's row reads **ADAPTER**, with its identity and a baud rate (115200, or 2000000
     if FORScan raised it);
   - the same row shows **about 12 V at the OBD port (ignition off)**;
   - if phone GPS over Bluetooth is on, its port reads **PHONE GPS** and isn't opened.
2. Tap the adapter's row. **Expect:** the row highlights, and the status line turns **LIVE — … on
   COMn** within a few seconds. The SIM badge disappears from the status strip.
3. Optional: with FORScan open and connected, press **TEST PORTS**. **Expect:** the adapter's
   port reads **IN USE**. Close FORScan.

**B — A late adapter goes live** (the trip from the house to the truck)

4. Unplug the adapter's USB cable from the Surface. Quit DashDeck and launch it again.
   **Expect:**
   - the SIM badge;
   - in the status strip, **ADAPTER NOT CONNECTED — SHOWING SIMULATED DATA · COMn isn't there …
     switches to live when it answers**.
5. Plug the USB cable back in. Don't restart. **Expect:** within about 10 s the SIM badge goes,
   and the dash values turn from the blue of Simulated to the green of Live.

**C — A knock to the cable** (engine **running**, with RPM on the visible dash page)

6. Pull the adapter's USB cable for about five seconds. **Expect:** **ADAPTER LOST —
   RECONNECTING**, and the values turn the amber of Stale. They don't freeze and don't go blue.
7. Plug it back in. **Expect:** the banner clears within about 10 s, the values are green again,
   and RPM matches the tachometer. This proves the adapter is set up again after a reconnect.

**D — A renumbered port** (needs a second USB socket or a USB-C adapter)

8. Quit DashDeck, move the adapter's cable to the other USB socket, and launch. **Expect:** it
   comes up live, and **Settings ▸ Vehicle** says **found it on a new port**, with the new COM
   number highlighted. If Windows kept the same COM number, this step proves nothing — note that.

**E — Testing ports while DashDeck is looking for the adapter** (ignition **on**)

9. Unplug the adapter's USB from the Surface, then quit and relaunch DashDeck. It comes up
   simulated and starts looking. Plug the cable back in, and **straight away** open
   **Settings ▸ Vehicle** and press **TEST PORTS**. **Expect:** the summary briefly reads
   *Testing… (pausing DashDeck's own search first)*, then the adapter's row reads **ADAPTER** —
   **not IN USE**. Within a few seconds of the test finishing, the dash goes live.

**A failure looks like:**
- the SIM badge never going after plugging in (step 5);
- **ADAPTER LOST** not clearing within about 30 s of plugging back in (step 7);
- values green, but RPM far from the tachometer;
- the app freezing while ports are tested;
- any port other than the adapter's being chosen automatically;
- the adapter's own port reading **IN USE** in step 9, when nothing else is running.

If step 7 fails, **don't restart yet.** Open **Settings ▸ Vehicle**: the status line ends with
`Last: …`, saying what the link last ran into. Photograph it, then send `adapter.log`. The first
run of step 7, on 2026-10-02, found that a pulled USB device throws "access denied" rather than
an I/O error, and that stopped polling for good. Fixed in the change that added this paragraph.

---

## Settings ▸ Sensors ▸ Modules — finding modules and their identifiers (ADR-0035)

**You need:** the truck, the adapter, the dash on the real adapter (no SIM badge), **parked**,
ignition **on** for steps 1–6, **running** for step 7. FORScan on a laptop or phone is useful for
step 3 but not required. **Close FORScan before starting** — the adapter can only talk to one app.

1. Open **Settings ▸ Sensors** and scroll to **MODULES**. Press **SCAN FOR MODULES**. **Expect:** a
   progress bar and a line like `HS 7A3 · 52 of 256 · 6 found`. It takes about a minute. The dash
   behind it may go amber (Stale) while it runs — that's expected, the sweep is using the adapter.
2. When it finishes, **expect** a summary like `HS-CAN: n modules · MS-CAN: n modules`, and a list
   of rows such as `7E0 · PCM — POWERTRAIN CONTROL` with `part …` underneath.
   - **HS-CAN** should include at least **7E0** (the engine), and very likely **760** (ABS) and
     **730** (power steering).
   - **MS-CAN** should include **726** (the body module) and very likely **720** (the cluster) and
     **7D0** (SYNC).
   - A row whose second line says `part number: not supported here` is still a module: it's there,
     it just wouldn't give its part number.
3. Compare with FORScan's module list if you have it. Same addresses? Names are marked **likely**,
   so note any that are wrong — they're easy to correct in the vehicle pack.
4. Tap **7E0**. **Expect:** an **IDENTIFIERS IN 7E0 ON HS-CAN** block with range chips. Leave
   **F100–F1FF IDENTITY** selected and press **SWEEP**. **Expect:** about 15 seconds, then rows
   such as `22 F190 · 17 bytes · "1FT…"` — **the truck's VIN**, read from the engine computer —
   and `22 F113` with the part number from step 2. Don't photograph or share the VIN row.
5. Tap **726** (MS-CAN) and sweep **DD00–DDFF**. **Expect:** it finishes, and lists whatever the
   body module answers there (perhaps nothing — that's a valid result). Rows that say `there, but
   locked (security access)` are real identifiers it won't read without unlocking; we leave those.
6. Check STOP: start a sweep of **0000–0FFF** on any module, then press **STOP**. **Expect:** it
   stops within a second and says `stopped`. (The sweeps also refuse to start while the truck is
   moving. Don't test that by driving.)
7. Engine **running**: on **7E0**, pick an identifier from step 4 that came back with 1–2 bytes
   and press it. **Expect:** the editor opens with **MODE 22**, the identifier, **MODULE 7E0** and
   `Asks 7E0 and listens on 7E8 — likely the PCM …` under it. Press **TEST** a few times while
   blipping the throttle or watching the temperature climb. Whether the raw bytes move with
   something real is how a Ford PID is found. Press **CANCEL** unless it's one worth keeping.
8. Go back to the dash for ten seconds. **Expect:** the cards are green (Live) again and updating —
   the adapter went back to the broadcast after the sweeps.

**A failure looks like:**
- the module scan finding **nothing** on HS-CAN (7E0 must answer — it answers the dash);
- the scan running much past two minutes, or STOP not stopping it;
- the dash still amber a minute after leaving the Sensors section (the adapter didn't go back to
  the broadcast — send `adapter.log` and a photo);
- step 7's TEST saying `NO DATA` for an identifier the sweep just found on the same module;
- the app freezing at any point.

If MS-CAN finds nothing at all, check **Settings ▸ Vehicle**'s adapter line still lists MS-CAN as
reachable, and note it — the 2026-10-01 bring-up found the adapter accepts the MS-CAN switch, so
silence there would be news.

---

## Settings ▸ Themes — wearing a theme in the cab (ADR-0036)

**You need:** the tablet in its mount. Ignition **on** or **running**, parked. Do one pass in
**daylight** and, if you can, one after dark (or set **Settings ▸ Appearance ▸ NIGHT** to fake it).
No internet needed. The adapter is optional; with it, the card borders show real Live green.

0. (At a desk is fine.) **Settings ▸ Themes ▸ OPEN FOLDER**. **Expect:** an **examples** folder
   holding `modern.json` (every token written out), `glass.json` and `README.txt`; beside it, in
   `themes\` itself, `lcars-inspired.json` with its Antonio `.ttf` files and `OFL-Antonio.txt`
   (LCARS is yours since ADR-0043).
1. Open **Settings ▸ Themes**. **Expect:** a list of **MODERN** (built in, marked **WEARING**),
   **GLASS** (shipped) and **LCARS (INSPIRED)** (yours), each with five colour swatches, and a token
   list below.
2. Tap **LCARS (INSPIRED)**. **Expect**, within a second and without a restart:
   - a black screen;
   - **pill-shaped** buttons in periwinkle with black lettering;
   - **tall, narrow capital lettering** (the Antonio font);
   - the selected settings tab filled **orange** with black lettering.

   If the lettering looks like ordinary wide Windows text instead, the font didn't load — note it.
3. Go to the dash. **Expect:**
   - **The nav bar**: black, with lavender labels; the current destination is filled orange with
     black lettering.
   - **The cards**: black, with rounder corners and peach captions.
   - **The card borders keep their quality colours**: green for Live, blue for Simulated, amber
     for Stale.
   - **Values** are still the same off-white.
4. Glance test from the driver's seat, in daylight. Can you read a card value, a caption and the nav
   labels at a glance? Is the peach caption text too faint in sun? Note anything you had to squint at.
5. Touch test: switch dash pages, open the stage launcher, change the stage, open and close a card's
   detail. **Expect:** every pill button responds to a finger as before.
6. Night: **Settings ▸ Appearance ▸ NIGHT** (or wait for dark on AUTO). **Expect:** the LCARS
   colours dim — orange and lavender get darker, the screen stops being the brightest thing in the
   cab — and stay readable. Set it back to AUTO.
7. Pull the adapter's USB (if connected). **Expect:** the **ADAPTER LOST — RECONNECTING** banner is
   still the amber it always is, not an LCARS colour. Plug it back in.
8. Quit DashDeck and start it again. **Expect:** it comes up already in LCARS — the choice is
   remembered.
9. Back in **Settings ▸ Themes**: type `My LCARS` under **START YOUR OWN FROM THIS ONE**, press
   **SAVE AS**. **Expect:** a **MY LCARS** row marked **YOURS** and **WEARING**. Press **DELETE** on
   it. **Expect:** it's gone and the dash is back to the DashDeck look. Tap **DASHDECK** to make
   sure the default is back exactly as it was.

**A failure looks like:**
- the dash not changing when a theme is tapped, or only part of it changing (for example, buttons
  still square);
- wide, ordinary lettering under LCARS (the font did not load);
- a quality colour that changed with the theme (a card border, the SIM badge, the ADAPTER LOST
  banner);
- text you can't read in daylight or at night;
- a selected button or tab whose lettering disappears into its fill;
- the theme not remembered after a restart.

Take a photo of anything that looks wrong; for a theme, the screen *is* the bug report.

---

## Closing DashDeck by touch, and nothing left running

**You need:** the tablet in its mount, touch only (no keyboard), and the adapter plugged in. Ignition
**on**. For step 5 you'll need Task Manager. On the Surface, open it by long-pressing the Start
button.

1. Put a web app (Spotify or a map) or a native app (Nuvio/Stremio) on the stage, so there is
   something running to clean up.
2. Tap the **three dots** at the top right. **Expect:** the last item reads **CLOSE DASHDECK**.
3. Tap it once. **Expect:** it changes to **TAP AGAIN TO CLOSE DASHDECK** in the accent colour, and
   nothing closes.
4. Wait five seconds without tapping. **Expect:** it goes back to **CLOSE DASHDECK** by itself.
5. Tap it twice in a row. **Expect:** DashDeck closes within a couple of seconds. Then open **Task
   Manager ▸ Processes** (or **Details**) and look for these:
   - **no `DashDeck.Host`**;
   - no **Microsoft Edge WebView2** processes left from it;
   - none of the stage apps DashDeck started (Nuvio, Stremio).
6. Start DashDeck again. **Expect:** it comes up **live** on the adapter (no SIM badge) — proof the
   serial port was let go.
7. Tap outside the open menu. **Expect:** it still just dismisses the menu.

**A failure looks like:**
- one tap closing the app;
- `DashDeck.Host` still in Task Manager a minute after closing;
- the next launch coming up simulated with the port **IN USE**.

If `DashDeck.Host` lingers, wait 15 seconds: DashDeck ends itself if shutdown overruns, and writes
why to `%LOCALAPPDATA%\DashDeck\crash.log` — send that file.

---

## The stage as a file — gauges, LCARS stage, and editing your own (ADR-0037)

**You need:** the tablet in its mount, the adapter plugged in, parked. Ignition **running** for steps
2–4 (so boost, throttle and load move). For step 7, Notepad.

1. Put **GAUGES** on the stage with the **DashDeck** theme worn. **Expect:** the familiar cluster —
   boost and oil temperature large, volts, intake, throttle and load small, in the same places as
   before.
2. Look at **OIL TEMP**. **Expect:** **no needle**, and **NO DATA** under the caption. This truck
   doesn't report oil temperature; the old cluster wrongly parked the needle on 40 °C. Its corner
   dot is grey.
3. Look at the other five. **Expect:** needles moving with the engine (blip the throttle: BOOST,
   THROTTLE and LOAD respond), and a **green** dot in each gauge's top-right corner. Volts should
   read about 14 V running.
4. **Settings ▸ Themes** → tap **LCARS (INSPIRED)**, then back to the stage. **Expect:** the stage
   changes too:
   - a black screen framed by lavender and peach elbows and bars;
   - **ENGINE STATUS** and the time across the top;
   - six segmented bars, BOOST to LOAD, each with a coloured end cap showing its name and number.

   Blip the throttle and the THROTTLE and BOOST bars should light further. OIL °C says NO DATA with
   no lit segments.
5. **Settings ▸ Themes ▸ STAGE LAYOUT**. **Expect:** **SHOWING LCARS (INSPIRED) STAGE**, *Following
   the theme*, and the list with **FOLLOW THE THEME** marked **CHOSEN**. Tap **F-150 CLUSTER**,
   go back to the stage. **Expect:** the dials again, still under the LCARS theme's colours. Tap
   **FOLLOW THE THEME** to go back.
6. Press **OPEN FOLDER**. **Expect:** an **examples** folder containing `lcars.json`,
   `f150-cluster.json` and `README.txt`. Open `lcars.json`: it's the LCARS stage with its comments.
   Close it without saving.
   Then, under **EDIT YOUR OWN COPY OF THE ONE SHOWING**, type `lcars`, press **SAVE AS**. **Expect:** a
   row **LCARS (INSPIRED) STAGE (MINE)** marked **YOURS**, and the stage unchanged.
7. Press **OPEN FOLDER**, open `lcars.json` in Notepad. Find `"title"` and change
   `"ENGINE STATUS"` to `"MY TRUCK"`. Find the `"boost"` gauge and change `"segments": 24` to
   `"segments": 12`. Save. On the stage, open the three-dot menu and tap **RELOAD STAGE LAYOUT**.
   **Expect:** **MY TRUCK** across the top, and the BOOST bar with 12 fatter segments.
8. Break it on purpose: in Notepad change the boost gauge's `"max": 25` to `"max": -20`, save,
   **RELOAD STAGE LAYOUT**. **Expect:** the BOOST bar is gone, everything else still drawn, and in
   **Settings ▸ Themes ▸ STAGE LAYOUT** an amber line: `boost: max (-20) must be above min (-15) —
   left out`. Put it back to `25`.
9. Clean up: on the **LCARS (INSPIRED) STAGE (MINE)** row press **DELETE**. **Expect:** the shipped
   LCARS stage is back.

**A failure looks like:**
- a gauge needle or bar showing a value for OIL TEMP;
- a dot that isn't green while the engine runs and the adapter is live;
- the stage not changing with the theme;
- RELOAD not picking up an edit;
- a bad file blanking the whole stage instead of leaving out one element;
- the dash going stale while the stage shows gauges. Each gauge declares its own rate, so check
  the dash cards stay green.

Photograph anything that looks wrong. For a layout bug, also send the `.json` you were editing.

## The launcher as a file — order, the quick bar, web pages and programs (ADR-0038)

**You need:** the tablet in its mount, **parked**. Ignition on or off — nothing here reads the
truck. Internet for the web page in step 6. Notepad.

1. Look at the bar below the stage. **Expect:** exactly what was there before this build —
   **GAUGES, CLOCK, COMPASS, PHONE, VIDEO** and the nine-dot button. Open the grid. **Expect:**
   SCREENS, WEB and APPS in the same order as before, with your own apps from Settings ▸ Apps at
   the end of APPS.
2. **Settings ▸ Apps.** **Expect:** a **STAGE LAUNCHER** block where the built-in list used to be:
   **USING BUILT IN**, then every entry with **BAR** beside the first five, **GRID** beside the
   rest, and **YOUR APPS** last. No amber lines.
3. Tap **MAKE IT MINE**. **Expect:** **USING YOUR FILE — LAUNCHER.JSON**, the same list, a line
   saying it was created, and the MAKE IT MINE button gone. The bar and the stage don't change.
4. Tap **OPEN FOLDER**. **Expect:** Explorer on `…\AppData\Local\DashDeck` with `launcher.json`
   and `launcher.example.json` in it. Open `launcher.json` in Notepad.
5. **Reorder the bar.** Change the `quickBar` line to
   `"quickBar": [ "OSM", "GAUGES", "CLOCK" ],`. Save. Back in Settings ▸ Apps tap **RELOAD**.
   **Expect:** the line *Reloaded launcher.json: …* and **BAR** now beside OSM, GAUGES and CLOCK.
   Go back to the dash. **Expect:** three buttons — **OSM, GAUGES, CLOCK** — plus the nine-dot.
   Whatever was on the stage is still on it and still lit; if it was COMPASS, a fourth COMPASS
   button sits at the end.
6. **Add a web page with its own zoom.** In `entries`, after the OSM line, add:
   `{ "name": "RADAR", "type": "web", "url": "https://weather.gc.ca/radar/", "zoom": 0.75, "group": "WEATHER" },`
   Save, **RELOAD**. Open the grid. **Expect:** a **WEATHER** heading with **RADAR** under it,
   detail `weather.gc.ca`. Tap it. **Expect:** the radar page on the stage, zoomed out further than
   OSM, and **RADAR** in the status strip.
7. **Rename and hide.** Change `"name": "CLOCK"` to `"name": "TIME"` (and `CLOCK` in `quickBar` to
   `TIME`), and add `"hidden": true` to the MUSIC entry. Save, **RELOAD**. **Expect:** the bar says
   **TIME**; choosing it shows the clock with **TIME** in the status strip and the TIME button lit.
   MUSIC is gone from the grid; Settings lists it as **HIDDEN**.
8. **A layout on its own button** (needs a layout you saved, e.g. `lcars` from the stage walkthrough
   — or use the shipped `lcars`): add
   `{ "name": "LCARS", "type": "gauges", "layout": "lcars" },` and put `"LCARS"` in `quickBar`.
   Save, **RELOAD**. With the **DashDeck** theme worn, tap **GAUGES**, then **LCARS**. **Expect:**
   the F-150 dials, then the LCARS stage — without changing the theme or the Settings choice.
9. **Break it on purpose.** Add `{ "name": "OOPS", "type": "hologram" },` and put `"NOPE"` in
   `quickBar`. Save, **RELOAD**. **Expect:** two amber lines in Settings — *OOPS: type 'hologram'
   is not one of … — left out* and *quickBar: 'NOPE' is not an offered entry — left out* — and
   everything else still working. Then delete a `{` somewhere so the file is not JSON, save,
   **RELOAD**. **Expect:** **USING BUILT IN**, the first amber line saying `launcher.json was not
   used`, and the original five buttons back. Undo it in Notepad and **RELOAD**.
10. **Restart DashDeck** (three-dot ▸ CLOSE DASHDECK twice, then start it). **Expect:** your bar,
    your names, and the stage starting on **GAUGES** (`startOn`).
11. Clean up: delete `launcher.json` and tap **RELOAD**. **Expect:** **USING BUILT IN** and the
    original bar.

**A failure looks like:**
- the bar or grid order differing from the file after RELOAD;
- the stage restarting (a video jumping back, a program relaunching) when you RELOAD;
- a renamed entry whose button does not light while it is on the stage;
- a web page ignoring its `zoom`;
- a broken file leaving the bar empty instead of falling back;
- a program entry that is installed reading *not installed*.

Photograph anything that looks wrong and send the `launcher.json` you were editing.

## One DashDeck at a time

**You need:** the tablet, the adapter plugged in. Ignition on, so you can see the dash is live.
Task Manager (search the Start menu for it, or long-press the taskbar ▸ Task Manager).

1. Close every DashDeck: three-dot ▸ **CLOSE DASHDECK** twice. In Task Manager, end any
   **DashDeck.Host** still listed. **Expect:** none left.
2. Tap the DashDeck icon **three times quickly**. **Expect:** one window, after the usual few
   seconds, with live (green) cards. In Task Manager, **one** DashDeck.Host. The extra copies wait,
   see the first window appear, bring it forward and close themselves.
3. With DashDeck open, switch to another app (Notepad), then tap the DashDeck icon again.
   **Expect:** the same dash comes to the front, still live, nothing restarted — a video or a
   page on the stage carries on. Still one in Task Manager.
4. Settings ▸ Sensors (or Vehicle) ▸ **RESTART NOW**, if it's showing. **Expect:** the window
   closes and one new one opens within about ten seconds, live. One in Task Manager.
5. Three-dot ▸ **CLOSE DASHDECK** twice, then tap the icon straight away. **Expect:** a new dash
   within about ten seconds — it waits for the old one to let go of the adapter — and live cards.

**A failure looks like:** two DashDeck.Host in Task Manager; a second window; a dash that comes up
with the SIM badge or amber cards while another is running; tapping the icon doing nothing for
more than 20 seconds.

## Module scans and sweeps are kept across a restart

**You need:** the truck, the adapter, the dash on the real adapter (no SIM badge), **parked**,
ignition on (engine running for live values). About six minutes.

1. **Settings ▸ Sensors ▸ MODULES ▸ SCAN FOR MODULES.** **Expect:** the same list as before
   (7E0, 7D0, 726, 736, 723, 746 on HS-CAN).
2. Tap **7E0 › IDENTIFIERS**, choose **F400–F4FF**, press **SWEEP**. **Expect:** about 53 answered.
   The 7E0 row now ends **swept F400–F4FF**.
3. Close DashDeck (three-dot ▸ **CLOSE DASHDECK** twice) and start it again. Go back to Settings ▸
   Sensors. **Expect:** the module list is already there, its status starting
   **SAVED SCAN, 2 Oct 15:18** (your date and time), with 7E0 still saying **swept F400–F4FF**.
4. Tap **7E0 › IDENTIFIERS**. **Expect:** the F100–F1FF chip is chosen and the status says it
   **has not been swept on 7E0**. Tap **F400–F4FF**. **Expect:** the same rows as step 2, with the
   status starting **SAVED SWEEP, …**. Nothing was asked of the truck: the dash stays green.
5. Press **SWEEP** again on F400–F4FF. **Expect:** it runs, and the status loses **SAVED** — the
   saved sweep is replaced by the new one.
6. At a desk without the truck (SIM badge showing), scan and sweep. **Expect:** the status says
   **not saved**, and back in the truck your real results are still there.

**A failure looks like:** an empty module list after a restart; a saved sweep shown under the
wrong module or range; the synthetic truck's modules (SYNTH part numbers) showing in the truck.

The results are in `%LOCALAPPDATA%\DashDeck\discovery.json`. It can hold the VIN, if the
F100–F1FF range of 7E0 was swept, so don't post that file publicly.

## WATCH — finding which identifiers move

**You need:** the truck, the adapter, the dash on the real adapter, **parked**, engine **running**.
Best started within a few minutes of a cold start, so temperatures are still climbing. Ten minutes.

1. **Settings ▸ Sensors ▸ MODULES**, tap **7E0 › IDENTIFIERS**, choose **F400–F4FF** and **SWEEP**
   (or tap the chip to show a saved sweep). **Expect:** the list of about 53.
2. Press **WATCH** (beside SWEEP). **Expect:** an orange line *Watching N identifiers on 7E0 — one
   pass is about 3 s. Leave it 30 s first…*, a **STOP** button with `pass 1 · 22 F4… · x of N · 0
   moved` beside it, and after the first pass the list replaced by rows showing *first … (number) →
   now … (number)*, *low–high*, an *if a temperature* line, and **MOVED ×n** or **STILL** on the
   right. The dash goes amber while it runs — expected. **Touch nothing for 30 seconds.** Rows
   that move on their own (rpm wobble, fuel trims) show MOVED already; note them.
3. Then blip the throttle two or three times, and leave it another 30 seconds. **Expect:** after the next pass, rows that follow the
   throttle rise to the top as **MOVED** — **22 F44A** (pedal), **22 F411** (throttle), **22 F40C**
   (rpm), **22 F404** (load). Rows that don't care stay **STILL** at the bottom.
4. Leave it idling for a few minutes. **Expect:** **22 F405** (coolant) shows MOVED if the engine is
   still warming, with its *A−40* line reading the coolant temperature in °C — compare with the dash.
   Two-byte rows show *÷16* instead: Ford's finer temperatures (transmission fluid) are sent that way.
5. Press **STOP**. **Expect:** *Stopped by STOP after n passes · m of N moved*, the list stays, and
   the dash goes green again within a few seconds.
6. Tap the top **MOVED** row. **Expect:** the signal editor, with the module and identifier filled
   in and **TEST** visible. **CANCEL** back.
7. Now the real hunt: sweep **1000–1FFF** on 7E0 (about four minutes), then **WATCH** it right after
   a cold start and blip the throttle now and then. Photograph the top rows after ten minutes:
   slow risers with sensible *A−40* values are oil and transmission temperature candidates; rows
   that jump with the throttle are fuel-flow candidates.

**A failure looks like:** every row MOVED ×1 after a single pass (the baseline must be the watch's
own first pass, not the sweep); a two-byte value going negative shown as a huge number (FF F2 is
−14); WATCH greyed out after a sweep with results; a pass taking much longer
than one second per 19 identifiers; STOP not stopping within a second; the dash still amber a
minute after STOP; WATCH starting while the truck moves (it refuses, and stops by itself if the
truck starts moving).

## WATCH recordings — a CSV to share instead of a photo

**You need:** the truck, the adapter, the dash on the real adapter, **parked**, engine **running**.

1. **Settings ▸ Sensors ▸ MODULES ▸ 7E0 › IDENTIFIERS**, tap **1000–1FFF** (the saved sweep).
2. Press **WATCH**. Idle **30 s**, hold **2,000 rpm** steady **30 s** (in Park, foot on the brake),
   idle **30 s**, then **STOP**.
3. **Expect:** the status ends *Recorded to watch-7E0-1000-1FFF-<date>-<time>.csv — OPEN FOLDER to
   copy it*, and an **OPEN FOLDER** button appears.
4. Press **OPEN FOLDER**. **Expect:** Explorer on `…\AppData\Local\DashDeck\watch` with the CSV in
   it. Open it in Notepad: the first line is `time_ms,rpm,22 1004 (2B),…`, then one line per pass
   — about 30 lines for 90 seconds — with rpm near 670, then near 2,000, then back.
5. Copy the CSV to wherever you send files from (OneDrive, email, a USB stick) and attach it.

The file holds only numbers the engine computer answered — no VIN or part numbers (long text
answers are never watched). **A failure looks like:** no *Recorded to* line after STOP; an empty
or header-only file after several passes; the rpm column blank throughout with the engine
running.


## The climate panel (ADR-0040)

**You need:** the tablet at a desk first (no adapter — the synthetic truck), then in the truck with
the adapter, **parked**, ignition **on** or engine running. No internet.

**At the desk (synthetic truck):**

1. Start DashDeck with no adapter attached and tap **CLIMATE** in the bottom bar.
2. **Expect:** the cards go and the **Modern** panel takes their place — no boxes, just type on black:
   DRIVER at the left edge with **21.5°** large and thin, PASSENGER at the right with **22.0°**, a row
   of FAN dots in the middle with FACE / FEET / SCREEN beneath (the ones in use bright, the rest grey)
   and CABIN under them, SEAT dots and HEATED WHEEL under the driver, and AUTO · A/C · RECIRC ·
   DEFROST · REAR along the bottom as words, lit blue (or orange for the heaters) when on. OUTSIDE at
   the bottom right. Every element has a small **blue** dot (Simulated).
3. Tap anything on the panel. **Expect:** nothing happens. It is read only.
4. Tap **DASH**. **Expect:** the cards come back exactly as they were. Tap **CLIMATE** again: the
   panel is back.
5. **Settings ▸ Themes**, wear **LCARS (inspired)**, tap **CLIMATE**. **Expect:** the LCARS panel —
   lavender and peach elbows, ENVIRONMENTAL, orange arcs. Wear **Modern** again: its panel is back.
   Wear **Glass**: the frosted panels.
6. **Settings ▸ Themes ▸ CLIMATE LAYOUT**: **Expect:** SHOWING MODERN, the reason *following the
   theme*, rows FOLLOW THE THEME, MODERN (BUILT IN), GLASS (SHIPPED) and LCARS (INSPIRED) CLIMATE (YOURS). Type
   `mine`, press **SAVE AS**: *Saved as mine.json in your climate folder*. **OPEN FOLDER**, open
   `mine.json` in Notepad, change the first `"x": 40` to `"x": 100`, save, press **RELOAD**, tap
   **CLIMATE**: the DRIVER temperature has moved right. Back in Settings, **DELETE** it: the panel follows
   the theme again.

**In the truck (real adapter):**

7. Tap **CLIMATE**. **Expect:** the same panel, but **every climate element shows a dash** — the
   set temperatures read **– –** with no arc, FAN and both SEATs read **–** with every bar dark, and
   every pill — WHEEL included — is dimmed with a dash after its name. Their dots are **grey** (Unavailable). This is right:
   the HVAC signals are placeholders until the HVAC module is found.
8. **OUTSIDE** shows the real outside air temperature with a **green** dot — it is a standard signal.
9. Change the fan or temperature, and turn the heated seat, cooled seat and heated wheel on and
   off, on the truck's own controls. **Expect:** the truck's climate works
   exactly as always and the panel does not change (still dashes). DashDeck sends nothing.

**A failure looks like:** a climate element showing a number on the real truck (a placeholder must
never pass for a reading); the cards and the panel both showing; the panel staying after leaving
CLIMATE; anything about the truck's own climate behaving differently with DashDeck running.

## The console dash, and the cards on the stage (ADR-0041)

**You need:** the tablet at a desk first (no adapter — the synthetic truck), then the truck with the
adapter, **parked**, engine **running**, then a short drive.

**At the desk (synthetic truck):**

1. Start DashDeck. **Expect:** below the stage, where the cards were, the **Modern** console — no
   boxes or arcs: the speed large and thin in the middle with *km/h* under it; RPM, ENGINE and FUEL
   down the left (a small grey caption over a light number); RANGE, ECONOMY and OUTSIDE down the right,
   aligned right; nine warning icons under the speed, barely visible; ODOMETER at the bottom left and
   STORED CODES at the bottom right, small and grey. As the synthetic drive moves, the speed and
   rpm follow it. Dots are blue (Simulated). ODOMETER reads about 48,213 km and climbs.
2. Look at the warning icons. **Expect:** all dark — nothing is wrong with the synthetic truck.
3. Tap **CARDS** on the bar below the stage. **Expect:** your cards on the stage, up to five rows a
   page, swiping sideways as before. The console stays below.
4. Three-dot menu ▸ **MODIFY WIDGETS**. **Expect:** edit mode on the cards (the add/reset bar replaces
   the nav). Add a card, then **DONE**. Tap **GAUGES**: the cards go; **CARDS** brings them back on the
   same page.
5. Tap a card while editing to open its editor. **Expect:** the full-screen editor, as before; back
   out and you are on CARDS.
6. **Settings ▸ Themes**, wear **LCARS (inspired)**, tap **DASH**. **Expect:** the LCARS console —
   HELM STATUS, segmented RPM, FUEL and TEMP bars. Wear **Modern** again: its console is back.
   Wear **Glass**: the arcs and the frosted strip.
7. **Settings ▸ Themes ▸ CONSOLE LAYOUT**. **Expect:** SHOWING MODERN, FOLLOW THE THEME / MODERN
   (BUILT IN) / GLASS (SHIPPED) / LCARS (INSPIRED) CONSOLE (YOURS), SAVE AS, RELOAD and OPEN FOLDER as for the stage.

**In the truck (real adapter):**

8. Engine running, parked. Tap **DASH**. **Expect:** speed 0, rpm near 670 with a **green** dot,
   FUEL and ENGINE TEMP with green dots, OUTSIDE reading. **RANGE and ECONOMY read a dash with a
   grey dot** — placeholders until the truck's own figures are found.
9. The warning icons. **Expect:** **check engine, low fuel, engine hot and battery** dark with no dot
   (they are real and off). **Oil, seatbelt, door, brake and tyres** dark **with a small grey dot**
   — placeholders, not yet found on this truck. **CODES** shows the number of stored codes (FORScan
   will agree).
10. **ODOMETER**: a number that matches the truck's odometer, or a dash with a grey dot if the
    truck does not answer PID A6 — tell me which.
11. Drive a short way. **Expect:** the speed number and arc follow the truck's speedometer (within a
    km/h or two), and the console never rearranges.

**A failure looks like:** a placeholder light lit, or one of the real ones lit with nothing wrong; the
cards still showing below the stage; cards on a page you can't see still costing requests (the
`req/s` does not show this — say if the dash feels slower with CARDS off the stage); a launcher file
of yours with no CARDS anywhere in the grid.

## The Modern defaults — type over shapes (ADR-0042, named Clean then)

**You need:** the tablet at a desk (no adapter), then the truck at night and in daylight, parked.

1. With the Modern theme, tap **DASH**. **Expect:** the Modern console as in *The console dash*
   step 1 — no panels, arcs or boxes; groups separated by space and by the size and weight of the
   type. Captions are small and grey, numbers large and thin, units smaller than their numbers
   (*88* then a small *°C*). The console's background is the same as the stage's above it — no
   darker block. Each reading's small quality dot sits right beside its caption (after it, or before
   it on the right-hand column), not out in the middle of the screen.
2. Tap **CLIMATE**. **Expect:** the Modern panel as in *The climate panel* step 2 — the switches are
   words, not pills.
3. Look at the type itself. **Expect:** a clean sans-serif (Segoe UI) throughout both — not the
   theme's lettering. Wear **LCARS**: it brings its own consoles and keeps its Antonio lettering; the
   change is only to the defaults.
4. In the truck, in **daylight and at night**: read the speed, a temperature and the warning row from
   the driver's seat. **Expect:** speed and temperatures readable at a glance; the warning icons
   effectively invisible until one is lit; nothing glaring at night (it is white type on black — if it
   is too bright, the display's night dimming applies as for the rest of the dash).

**A failure looks like:** a box, glass panel or pill on either default; a visible edge where the
console or climate panel meets the stage; a quality dot that is not next to its caption; a number in
the wrong typeface; a unit as large as its number; any text running into another; a dark warning icon that
draws the eye.

## Two looks, Modern and Glass — and LCARS is yours (ADR-0043)

**You need:** the tablet at a desk first (no adapter), then the truck, parked, ignition **on**, once
in daylight and once after dark (or **Settings ▸ Appearance ▸ NIGHT**). No internet.

**At the desk:**

1. Deploy over the old build and start DashDeck. **If you were wearing LCARS, expect** to still be in
   LCARS — same stage, console and climate panel, Antonio lettering. **If you were on the old DashDeck
   theme, expect** the **Modern** look everywhere: cool greys, a light-blue accent on the selected nav
   button and settings tab, Segoe UI lettering, slightly squarer buttons and cards.
2. **Settings ▸ Themes**. **Expect:** **MODERN** (built in), **GLASS** (shipped) and
   **LCARS (INSPIRED)** marked **YOURS** — with **DELETE** available on it, which it never had before.
3. Wear **Modern** and tap **GAUGES**. **Expect:** the Modern stage — **BOOST** on the left and
   **ENGINE** on the right as thin light-blue arcs with large, thin numbers (psi and °), and VOLTS,
   INTAKE, THROTTLE and LOAD in a row below, each a small grey caption over a light number. No dials,
   no boxes. Tap **DASH**: the Modern console. **CLIMATE**: the Modern panel. **CARDS**, the nav and
   Settings: the same greys and accent.
4. Wear **Glass**. **Expect:** without a restart, everything turns blue-black with an ice-blue accent
   and rounder corners; **GAUGES** shows the chrome **F-150 cluster**; **DASH** the arcs and the frosted
   strip; **CLIMATE** the frosted glass zones.
5. Overrides still win: **Settings ▸ Themes ▸ CONSOLE LAYOUT**, pick **MODERN (BUILT IN)** while
   wearing Glass. **Expect:** DASH shows the Modern console while the rest stays Glass. Press
   **FOLLOW THE THEME**: the Glass console is back. The stage and climate blocks work the same way.
6. Wear **LCARS (INSPIRED)**. **Expect:** its stage, console and climate panel, as before the update.
   **OPEN FOLDER** on the stage, console and climate blocks: each holds `lcars.json` in your own folder.
7. LCARS stays deleted when you delete it (optional — skip if you want to keep it): wear Modern,
   **DELETE** LCARS, close DashDeck (two taps on **CLOSE DASHDECK**) and start it again. **Expect:**
   LCARS does not come back. To get it back, copy the files from `catalog\extras\lcars\` beside
   `DashDeck.exe` into the matching folders in `%LOCALAPPDATA%\DashDeck\` and press **RELOAD**.
8. Look for duplicates. **Expect:** exactly one GLASS and one MODERN in each list, and no
   LCARS marked SHIPPED (an old copy left beside the executable would show one — the build now clears it).

**In the truck:**

9. Wear **Modern**, engine running, parked. **Expect:** BOOST reads near **−10 psi** at idle with a
   **green** dot, ENGINE the coolant temperature, VOLTS about 14. Blip the throttle: boost and THROTTLE
   rise and fall.
10. Wear each of Modern and Glass in **daylight and at night**. **Expect:** every caption readable from
    the driver's seat; at night the white type dims with the dash and nothing glares.

**A failure looks like:** LCARS gone or reverted to Modern for someone who wore it; LCARS listed twice,
or as SHIPPED; a deleted LCARS coming back; Antonio missing from LCARS (ordinary wide lettering); a
screen that does not change when the theme does (other than one you picked by hand); a stage number with
no reading drawn as zero instead of a dash or NO DATA.

## The ID hunter — finding identifiers with a guide (ADR-0044)

**You need:**
- the tablet with the adapter, **DashDeck closed** (three-dot menu ▸ CLOSE DASHDECK) and FORScan
  closed;
- parked, parking brake set, outside rather than in a closed garage;
- for steps 3–6, the ignition **on**; for steps 7–8, a **cold** engine you will start;
- no internet.

The guide itself: [id-hunter](id-hunter.md).

**At a desk first (optional):**

1. Run `IdHunter\IdHunter.exe --simulate` from a terminal. Choose **1** (Driver door), press Enter at
   each step. **Expect:** after five steps, a table whose first row is `3B3  byte 0 bit 0  0 1 0 1 0`.
   Type **1**, watch the value flip by itself between 0 (CLOSED) and 1 (OPEN), press Enter, then **y**.
   Choose **Q**. **Expect:** *Send this file:* and a path to `findings.csv` holding that row,
   *confirmed*. Nothing here touched a truck: those identifiers are invented.

**In the truck:**

2. Double-click `IdHunter\IdHunter.exe`. **Expect:**
   - *Looking for the adapter…*, then your COM port marked **Adapter** with the OBDLink's name and
     about 12 V;
   - the checklist of 21 targets with **S**, **L** and **Q** below.

   It prints *DashDeck last used COMn…* and a line per port it tried. If it says no adapter answered,
   the window stays open: read the line for each port. *In use* means DashDeck (check Task Manager
   for a leftover `DashDeck.Host`) or FORScan still holds it. Send me that screen if it is anything
   else.
   Before the checklist it says *Checking the bus on OBD pins 3 and 11…* and then either *Pins 3/11
   carry a 125 kbit/s bus*, *…a 500 kbit/s bus*, or *Nothing heard on pins 3/11*. **Tell me which** —
   it decides how DashDeck itself must talk on those pins (open question Q21).
3. Choose **S**, press Enter. **Expect:**
   - about half a minute of progress;
   - a list of modules on HS-CAN and MS-CAN: `7E0 PCM — powertrain control (likely)` with a part
     number, and a dozen or more others.

   Tell me how many and which.
4. Choose **1** (Driver door), with every door shut and the ignition on. Press Enter, then follow each
   STEP: shut, open, shut, open, shut, pressing Enter after each and holding still for the countdown.
   **Expect:**
   - *Hearing MS-CAN*;
   - after the last step, *Heard N frames from M identifiers* — thousands from dozens — and, on
     HS-CAN, *heard in N bursts*;
   - a ranked table.

   If a row reads `0 1 0 1 0` (or `1 0 1 0 1`), type its number, open and shut the door a few times,
   and watch the value change with it. Then answer **y**, or **n** if it did not follow.
5. Do the same for **3** (Driver seatbelt) and **5** (Driver seat heat), engine running for the seat.
   **Expect:**
   - the seat's best row to step up with the levels: `0 1 2 3 0`;
   - if a row's values only differ without stepping up, it is weaker — check it live.
6. If any listen says *The adapter lost frames*, note which bus. That is open question Q20.
7. **Next morning, cold engine:** choose **15** (Engine oil temperature):
   - press Enter for module `7E0`, then for range `1000-1FFF`, then to sweep (about four minutes,
     ignition on);
   - start the engine when it says to;
   - press Enter to start watching, and let it idle.

   **Expect:**
   - a line per pass, with coolant rising and *closest so far* naming an identifier with *r* climbing
     towards 1;
   - after 12 minutes (or Enter to stop), a table of what moved with coolant, with a *LOOKS LIKE*
     scaling where one fits.

   If FORScan on a laptop shows oil temperature, pick the top row's number and compare the two live.
8. With the engine warm, choose **17** (Fuel flow). When it shows `>>> REV now`, press the accelerator
   gently to about 2,000 rpm for two seconds, then let it fall back. Never above 3,000. **Expect:**
   - rpm in the pass lines jumping with each rev;
   - the ranked table led by identifiers with *r* near 1.
9. Choose **19** (Distance to empty) and type the number the cluster shows. It asks for a second
   round. Press B to stop for now, or come back after a drive with the new number. **Expect:** a short
   list of identifiers that give your number, with how they decode.
10. Choose **Q**. **Expect:** the path to `findings.csv`. Send me that file, and the other CSVs beside it
    if you can.

**A failure looks like:**
- anything on the truck behaving differently while the hunter runs — a warning on the cluster, the
  radio resetting, a module going quiet. Listening is silent, so this should never happen; if it does,
  quit straight away and tell me;
- a listen that hears nothing with the ignition on;
- the program stuck and not answering Enter (Ctrl+C quits it).

## Pins 3/11 at the measured 500 kbit/s (Q21)

**You need:**
- the truck, engine **running**, parked;
- the adapter;
- DashDeck built from develop with this change.

1. Start DashDeck. **Expect:** the dash goes live as usual (the SIM badge goes).
2. Tap **DASH** and leave it for a minute, then tap **CLIMATE** and leave it for a minute. **Expect:**
   - the placeholder values still read a dash with a grey dot — they are still placeholders;
   - **nothing on the truck's cluster**: no warning, no message, no gauge dropping out. Before this
     change those requests went out at the wrong speed.
3. **Settings ▸ Sensors ▸ MODULES ▸ SCAN FOR MODULES.** **Expect:** modules on **MS-CAN** as well as
   HS-CAN. On this truck "MS" means pins 3/11, now asked at 500 kbit/s. The cluster at `720` may be
   among them. Tell me which ones answer.

**A failure looks like:**
- any warning on the cluster while DashDeck runs;
- the scan still finding nothing on MS-CAN with the engine running.

## The ID hunter — cruise control's set speed (M, ADR-0069, ADR-0070)

**You need:**
- a laptop with the hunter, and the adapter. DashDeck and FORScan must be closed, since one program
  holds the adapter;
- a road where cruise can be set at 60 and at 100 km/h.

**At a desk first:**

1. Run `IdHunter.exe --simulate` and choose **M**. Name it `Cruise set speed` and press Enter. Then:
   - modules `7E0, 7A0`;
   - Enter to listen as well;
   - ranges `4100-41FF,4300-43FF`;
   - Enter to sweep.
   - **Expect:** *1024 identifiers across 2 modules*, then a line starting *Looking in broadcast on
     HS-CAN…*
2. Type the speed the *Practice* line gives (60), then the next one (100).
   - **Expect:** *2 places still fit all 2 readings*.
3. Type **R**. **Expect:**
   - `3F2 u16@1 ÷ 2` (broadcast) and `4105 u16@0 ÷ 100` (7E0) at the top, both `2/2`;
   - nothing from byte 0 of `3F2`, which is a counter.

**In the truck:**

4. **Ignition on, before you drive:**
   - choose **M**, name it, type **ALL**, press Enter to listen;
   - take the suggested ranges, or a range you suspect;
   - sweep.
   - **Expect:** the scan, a sweep per module, then *Looking in …* with how long a reading takes.
5. **Set cruise at 60 km/h** and type `60`.
   - **Expect:** *Listening to HS-CAN… N frames heard*, a line per module, then *N places could hold
     60*.
6. **Set cruise at 100** and type `100`.
   - **Expect:** *N places still fit all 2 readings*.
   - If more than a few still fit, set **80** and type it.
7. Type **R**.
   - **Expect** one table with WHERE beside each row.
   - Pick the first one and change the set speed. The live readout should follow it.
   - Answer **y** if it did.
8. Send `findings.csv` and the `match-free.cruise-set-speed-…csv` capture beside it. They can hold the
   VIN, so they stay off the repository. The capture's last column is the speed of each reading.

**A failure looks like:**
- **"Nothing fits all 2 readings"** after two different speeds, with listening on: neither the swept
  ranges nor the broadcasts hold it. Try wider ranges.
- **"0 frames heard"** on a bus: the adapter heard nothing there. Is the ignition on? The modules are
  still read.
- **Only broadcast rows fit:** the value is broadcast and not asked-for in those ranges. Send it
  anyway. The frame tells us which module to search next.

## The ID hunter — checking a CAN database (ADR-0045)

**You need:**
- the truck, engine **running** and warm, parked;
- the adapter, with DashDeck closed;
- the `.dbcx` file copied to `%LOCALAPPDATA%\DashDeck\dbc\`;
- FORScan on a laptop if you can, to compare against.

1. Start the hunter and choose **D**. Press Enter to take the file. **Expect:** *VehicleCAN.dbcx: 329
   messages, 2141 signals.*
2. Press Enter to check presence. **Expect:** a line per bus, then *N of the file's 329 messages are on
   this truck*. **Tell me N.** It says how close this file is to your 2019.
3. Press Enter to search DashDeck's list. **Expect:** rows with **HEARD ON**. Write down which of these
   are heard:
   - `156` EngOil_Te_Actl
   - `230` GboxOil_Te_Actl
   - `179` FuelFlw_Vl_Dsply
   - `3B3` DrStatDrv_B_Actl
   - `3B5` Tire_Press_*
   - `430` OdometerMasterValue
4. For each one heard, type its number. **Expect:**
   - **oil and gearbox temperature:** a value within a few degrees of FORScan's;
   - **tyres:** kPa, so 240 kPa is 35 psi; compare with the cluster's tyre screen;
   - **driver door:** Closed/Ajar following the door;
   - **odometer:** the cluster's kilometres.

   Press Enter, then answer **y** or **n** for each.
5. Quit. Send me `findings.csv`.

**A failure looks like:**
- the file not loading;
- every message *not heard* with the engine running;
- any warning on the cluster. Listening is silent, so there should be none.


## The ID hunter's D reads files properly (ADR-0073)

D now reads a database through DashDeck's own DBC reader instead of a line-by-line match. Nothing
about using it changes; this checks that the same file still loads the same, or better.

**You need:** the laptop with the hunter, the same `.dbcx` you used for ADR-0045's walkthrough in
`%LOCALAPPDATA%\DashDeck\dbc\`. The truck is not needed for steps 1–2; `--simulate` is enough.

1. Start the hunter (`IdHunter --simulate` at a desk) and choose **D**, then Enter to take the file.
   **Expect:** *VehicleCAN.dbcx: 329 messages, 2141 signals*, the same counts as before. If it now
   says *N statements it could not read*, tell me N — it used to count only signal lines.
2. Search for `door`. **Expect:** `3B3 … DrStatDrv_B_Actl` and its neighbours, as before.
3. In the truck, engine running, repeat ADR-0045's step 4 for the driver's door. **Expect:**
   *Closed* / *Ajar* following the door, exactly as before.

**A failure looks like:** fewer messages or signals than before, a door that no longer shows its
*Ajar* text, or the hunter stopping on the file.

## The serial tap — recording FORScan (ADR-0048)

**You need:**
- the laptop or tablet with FORScan;
- the adapter, plugged into the truck and the computer;
- the ignition **on**, or the engine running for values that need it; parked;
- DashDeck and the ID hunter closed.

1. Double-click `SerialTap\SerialTap.exe` and pick the adapter's port. **Expect:** *Holding COM7 at
   115200 baud*, the FORScan settings to use, and the log file's path.
2. In FORScan, set **Settings ▸ Connection** to type **WiFi**, IP **127.0.0.1**, port **35000**, and
   connect. **Expect:**
   - FORScan connects and finds the truck as it normally does;
   - the tap window fills with `>>` and `<<` lines, starting with `--  connected`.
3. Open **one** value in FORScan's live data, such as transmission fluid temperature. Start FORScan's
   own PID log. **Expect:** the same `>>  22…` request repeating in the tap window, each with a
   `<<  62…` answer.
4. Make the value move: let it warm, or rev. Then stop FORScan's log and disconnect. **Expect:**
   `--  disconnected` in the tap.
5. Press **Ctrl+C** in the tap. **Expect:** the command and answer totals, and *Saved:* with the
   file's path.
6. Send me the `.log` file and FORScan's CSV, and say which value it was. Repeat for each value.

**A failure looks like:**
- *Could not open COMn*: something else holds the port;
- FORScan refusing to connect to 127.0.0.1;
- FORScan connected but finding no adapter;
- the log ending in garbage after an `STBR` or `ATBRD` line.

Send the log in any of these cases.

## Fast requests (ADR-0049)

**You need:**
- the truck, engine **running**, parked;
- the tablet with DashDeck on the adapter (live, not simulated);
- the CARDS stage with a few engine cards on it — rpm, speed, coolant.

1. Before updating, note the **`req/s`** on the status strip with DASH and CARDS showing. **Expect:**
   about 19.
2. Update and start DashDeck. Open **Settings ▸ Vehicle**. Under OBD-II ADAPTER, **expect**:
   - **FAST REQUESTS — ON**;
   - a line *Fast requests in use: N fast, M asked again the slow way*, with N climbing quickly and M
     small.
3. Go back to the dash. **Expect:**
   - **`req/s` around 35–50**, about twice before;
   - the rpm card following a throttle blip more smoothly.
4. Check the numbers against the cluster. **Expect:**
   - rpm, speed and coolant agree with the cluster as before;
   - nothing reads a value that belongs to another card;
   - no card stuck on NO DATA that worked before.
5. Turn it off: **FAST REQUESTS — OFF**, then RESTART NOW. **Expect:**
   - `req/s` back to about 19;
   - the line *Fast requests are off*.

   Turn it back on and restart.
6. Tell me the `req/s` you saw each way, and the N and M from step 2 after a few minutes.

**A failure looks like:**
- `req/s` no higher with it on;
- M climbing as fast as N, so everything is being asked twice;
- a card that worked now NO DATA;
- any number that is wrong, jumps between two values, or shows another card's value. In that case,
  turn it off and tell me which card.

## The ID matcher — pairing DashDeck's IDs with FORScan (ADR-0050)

**You need:**
- the laptop with FORScan, and the DashDeck folder copied to it;
- the adapter, plugged into the truck and the laptop;
- ignition **on**, engine running for values that need it; parked;
- DashDeck, the ID hunter and SerialTap closed.

1. Start `IdMatcher\IdMatcher.exe`. **Expect:**
   - the left list shows **NEEDS ID** rows: tyre pressures, warning lights, economy, range, climate;
   - the status line under the list says how many of DashDeck's signals need an ID.
2. Pick the adapter's port and click **START TAP**. **Expect:** *Holding COM7 at 115200 baud* in the
   status line.
3. Connect FORScan to WiFi **127.0.0.1**, port **35000**. **Expect:**
   - FORScan finds the truck as usual;
   - the middle list fills with identifiers;
   - the left list adds **NOT ON TRUCK** for fuel rate, MAF and oil temperature, read from the
     truck's own answers.
4. **Coolant, which we already know.** Tick off *Only those that need the truck's ID* and click
   *Coolant Temperature* on the left. In FORScan, open the engine's coolant temperature in live data.
   Press **Ctrl+N**. **Expect:** a yellow row, `7DF 01 05` or a Ford identifier on `7E0`.
5. Press **F3**, type FORScan's number, press **Enter**. As it warms, type it again a few times.
   **Expect:**
   - the top scaling to be **A − 40** (or a Ford one that agrees);
   - *Reads now* tracking FORScan's value.
6. **Transmission temperature.** Click any NEEDS ID or other signal to pair, or press Esc to make a
   new one. Open FORScan's transmission fluid temperature, press **Ctrl+N**, and type three values as
   it moves. **Expect:**
   - `7E0 22 1E1C`;
   - top scaling **(A·256+B) ÷ 16** in °C;
   - *Reads now* agreeing with FORScan.

   Press **Ctrl+Enter**.
7. **A PID log.** Open three or four values in FORScan and start its PID log. Rev and let it idle
   for a minute, then stop the log. In **PID LOG**, import the CSV. **Expect:**
   - each column matched to an identifier;
   - R² near 1 for anything that moved;
   - *hardly moved* for anything that did not.
8. Click **Export for the vehicle pack** (Ctrl+E) and send me the file it names. Also send the CSV
   and the tap log from `%LOCALAPPDATA%\DashDeck\tap\`.
9. Optional, on the tablet with a keyboard: **SAVE TO DASHDECK**, restart DashDeck, and check the
   paired signal in Settings ▸ Sensors with TEST.

**A failure looks like:**
- an empty left list (the catalog was not found beside the program);
- no rows in the middle while FORScan reads live data;
- no scaling fits values you typed (check the unit beside the box);
- a PID log where nothing matches though values moved. Send me that CSV: its format is the
  unknown.

## Managing signals from the ID matcher (ADR-0051)

**You need:**
- the laptop with the ID matcher;
- for steps 6–8, the tablet with DashDeck and the adapter in the truck, ignition on;
- the overlay saved where the tablet reads it: run the matcher on the tablet with a keyboard, or copy
  `signals.user.json` over.

1. In the matcher, untick *Only those that need the truck's ID*. Select **Mass Air Flow** (the truck
   has no MAF) and click **HIDE**. **Expect:**
   - it disappears from the list;
   - the message says the card editor stops offering it.
2. Tick **Show hidden**. **Expect:** it is back, greyed, marked **· HIDDEN**. Click **UNHIDE**: the
   button reads UNHIDE while it is selected.
3. Click **NEW…** with nothing accepted. Type:
   - id `ford.test`;
   - module `7E0`, mode `22`, PID `1E1C`;
   - bytes from `0`, length `2`;
   - scale `0.0625`, offset `0`, unit `°C`.

   Save. **Expect:**
   - it appears marked **UNCONFIRMED**;
   - the message says to TEST it on the tablet.
4. Select it and click **EDIT…**. Change only the name. **Expect:** still UNCONFIRMED, and nothing else
   changes.
5. Click **REMOVE** on `ford.test`, then OK. **Expect:** it is gone. On a built-in like *Engine RPM*,
   REMOVE says to hide it instead.
6. On the tablet, with the overlay copied and DashDeck restarted: open **MODIFY WIDGETS** and look for
   the hidden MAF and an unconfirmed signal. **Expect:** neither is offered. A card that already showed
   one still does.
7. **Settings ▸ Sensors.** **Expect:**
   - the unconfirmed one labelled **UNCONFIRMED — TEST IT**;
   - the hidden one labelled **HIDDEN**.
8. Open the unconfirmed one, tap **TEST** with the engine running, then **SAVE**. Restart. **Expect:**
   - the label is gone;
   - the card editor now offers it.

**A failure looks like:**
- a built-in actually removed;
- a hidden signal breaking a card that used it;
- an unconfirmed signal offered in the picker before TEST;
- a renamed signal turning unconfirmed.

## Matching a switch — door ajar (ID matcher, ADR-0050)

**You need:**
- the laptop with FORScan and the ID matcher, the tap running, FORScan connected through it;
- the truck parked, ignition on (engine off is fine), a door you can open and shut.

1. In FORScan, open live data for the body module and pick **one** door-ajar value (it shows
   *Open*/*Closed* or *On*/*Off*). In the matcher, select **Warning — Door Ajar** on the left, then
   press **Ctrl+N**. **Expect:** the newest identifier is selected.
2. With the door shut, press **F3**, type `Closed` (or whatever FORScan shows), **Enter**. **Expect:**
   - the unit beside the box switches to **on / off** by itself;
   - many candidates, each *bit n of X*, marked *seen one way only*;
   - the hint says to switch it on in the truck and add that too.
3. Open the door. When FORScan shows *Open*, type `Open`, **Enter**. Shut it, type `Closed`; open it,
   type `Open`. **Expect:**
   - the list shrinks to one bit (sometimes two that move together), *seen on and off*;
   - *Reads now* follows the door as you open and shut it.
4. **Ctrl+Enter.** Save to DashDeck, copy the overlay to the tablet, restart. In Settings ▸ Sensors,
   TEST the door signal with the door open, then shut. **Expect:** 1 open, 0 shut. On DASH, the door
   light lights when it is open.
5. From the PID log instead: clear FORScan's traffic, start its log on the door value, open and shut
   the door three times, stop it, import it (Ctrl+I). **Expect:** the column matched to the same bit,
   ticked, with a score of 0.97 or more.

**A failure looks like:**
- typing *Open* or *Off* refused;
- one state alone presented as a settled match;
- a match that reads anything other than 0 or 1;
- the PID log's door column reported as "never changed" when the door was opened during it.


## Your vehicle file, and nothing vehicle-specific shipped (ADR-0052)

**You need:**
- the new build on the tablet (and the laptop, for the ID hunter and matcher);
- your two files from this change: `ford-f150-2018-2020-2.7-ecoboost.json` and `targets.json`;
- the truck, ignition on, parked.

1. **Before copying anything**, open DashDeck on the tablet in the truck. Settings ▸ Vehicle.
   **Expect:** the pack line says no signal pack (none ships any more).
2. Settings ▸ Sensors ▸ **SCAN FOR MODULES**. **Expect:**
   - the status says pins 3/11 are skipped because no vehicle file gives their measured rate;
   - HS-CAN modules are still found, but with **no part numbers** (they are asked ISO's `F187`, which
     Ford declines — a refusal still counts as found);
   - the result line reads *pins 3/11: not asked*;
   - the range chips offer only **F100–F1FF IDENTITY**.
3. Close DashDeck. Copy `ford-f150-2018-2020-2.7-ecoboost.json` into
   `%LOCALAPPDATA%\DashDeck\vehicles\` (make the folder). Copy `targets.json` into
   `%LOCALAPPDATA%\DashDeck\idhunter\`. Do the same on the laptop.
4. Open DashDeck. Settings ▸ Vehicle. **Expect:** the pack line names *Ford F-150 2.7 EcoBoost
   (2018–2020)* again.
5. **SCAN FOR MODULES** again. **Expect:**
   - both buses asked, and the MS-CAN modules (720, 726, 733…) found at 500 kbit/s;
   - **part numbers back** on each row (asked `F113`, from your file);
   - module names *likely* from your file;
   - range chips: F100–F1FF, then DD00, F400, 0000, 1000, 4000.
6. Select the engine computer (7E0) and **SWEEP** F100–F1FF. While it runs, nothing else changes.
   Then start the engine and try a sweep while rolling slowly in the yard. **Expect:** refused — *the
   truck is moving*. (Speed now comes from the catalog's `vehicle.speed`, not a PID number.)
7. On the laptop, run the ID hunter, pick a *match* target (tyre pressure). **Expect:** it suggests
   your file's modules (726, 720) and ranges — your `targets.json` was read.

**A failure looks like:**
- anything sent on pins 3/11 before your file is in place (FORScan's CAN error counters rising, or
  module rows on MS-CAN in step 2);
- part numbers still missing after step 3;
- the pack line not finding your file;
- a sweep that runs while the truck is moving.

## Placeholders ask nothing; the synthetic truck reads the catalog (ADR-0053)

**You need:**
- the new build on the tablet; your vehicle file in place (from the ADR-0052 walkthrough);
- FORScan with the serial tap on the laptop, for step 4;
- the truck, ignition on, parked.

1. **At a desk first** (no adapter): open DashDeck. **Expect:**
   - SIM badge; CLIMATE shows set temperatures, fan and seats; DASH shows economy and range; TPMS
     shows four pressures with the rear left low — all flagged simulated, exactly as before;
   - Settings ▸ Sensors lists the climate, tyre and warning signals with *placeholder: no ID yet,
     nothing is asked* under each.
2. Settings ▸ Sensors ▸ **SCAN FOR MODULES** at the desk. **Expect:** the synthetic modules with new
   invented ids — 7E0, 7E1, 710, 740, 750 on HS and 7A0, 7A4, 7B0 on pins 3/11 — each with a
   `SYNTH-…` identity (710 declines to give one).
3. **In the truck**, open CLIMATE and DASH. **Expect:** every placeholder shows a dash with a grey
   dot (**Unavailable**, not Stale amber); real values (speed, rpm, coolant, fuel) are live.
4. With the tap recording, let DashDeck run on CLIMATE for a minute, then open the tap log.
   **Expect:** no requests on pins 3/11 for `01 C0`–`01 D7` — nothing goes out for a placeholder.
   (Before this change those went out every few seconds.)
5. Pair one placeholder in the ID matcher (say the door ajar, from the switch walkthrough) and copy
   the overlay. **Expect:** after a restart it reads live, and Sensors no longer calls it a
   placeholder.

**A failure looks like:**
- a placeholder lit or showing a number on the real truck;
- any `01 C…`/`01 D…` request in the tap log;
- a placeholder showing amber Stale instead of a grey Unavailable;
- the climate panel or TPMS blank at the desk.

## Pairing the tailgate (placeholder `body.tailgate`)

**You need:** the laptop with FORScan, the tap and the ID matcher; the truck parked, ignition on.

1. In the matcher, select **Tailgate — Open** (marked NEEDS ID). In FORScan, open the body module's
   live data and pick the tailgate switch (an `SW` item). Press **Ctrl+N**.
2. With the tailgate shut, press **F3**, type what FORScan shows (`Closed`/`Off`), **Enter**. Open it,
   type `Open`/`On`. Shut it and open it again, adding each. **Expect:** one bit, *seen on and off*.
3. **Ctrl+Enter**, save, copy the overlay to the tablet, restart. In Settings ▸ Sensors, TEST with the
   tailgate open and shut. **Expect:** 1 open, 0 shut; the row stops saying *placeholder*.

**A failure looks like:** the tailgate reading anything before pairing; a value other than 0 or 1;
DashDeck sending anything to the tailgate — it only reads.
## Listening for what changed (ID matcher, ADR-0054)

**You need:** the laptop with the ID matcher and the adapter, FORScan closed, the tap stopped; the
truck parked, ignition on.

1. Middle panel ▸ **LISTEN TO THE BUS**. Tick **Pins 3/11**, click **LISTEN**. **Expect:** *Listening
   to pins 3/11 at 500 kbit/s, silently* and a list of identifiers filling in, each with its rate.
2. **LEARN NOISE**, touch nothing for 15 seconds, click again. **Expect:** many bytes now marked `~`.
3. **MARK** (F5). Open the tailgate. **Expect:** the list narrows to a few identifiers, the changed
   bytes in `[brackets]`.
4. Shut it, **STATE A** (F6), wait; open it, **STATE B** (F7), wait; repeat twice; **RANK** (F8).
   **Expect:** at the top, one field marked *3/3* and *100%*.
5. Do the same with the heated steering wheel button.
6. **STOP LISTENING**, then START TAP. **Expect:** the tap starts normally and FORScan connects.

**A failure looks like:** a warning light or message on the cluster while listening (nothing should
be sent); the list empty with the ignition on; the tap failing to start after listening.

## Warning popups and clearing codes (ADR-0055)

**First at the desk** (no truck): `DashDeck.exe --fault P0420 --fault P0171/pending`.

1. **Expect**, within about five seconds: the whole screen dims and a window reads **CHECK ENGINE**
   with the amber engine icon, the advice, and under TROUBLE CODES **P0420** *Catalytic converter below
   efficiency, bank 1* (STORED) and **P0171** *Running lean, bank 1* (PENDING). The DASH console's
   check-engine light is lit.
2. Tap **DISMISS**. **Expect:** the window goes; the console light stays lit. Settings ▸ Diagnostics
   shows CHECK ENGINE *LIT · DISMISSED*. Close and reopen DashDeck with the same flags: **no window**.
3. Run `DashDeck.exe highway-cruise --fault warning.oilPressure@20`. **Expect** after 20 s, while
   "driving": a red strip across the top reading **OIL** with a big **DISMISS**, not the window.
4. Settings ▸ Diagnostics ▸ **ALLOW CLEARING CODES — ON**. Run `DashDeck.exe --fault P0420 --fault
   engine.rpm=0`. In the window, **HOLD TO CLEAR CODES** — let go after one second: **nothing**. Hold
   until it fills: **Expect** *Codes cleared…*, the list empty, *No codes*, and the window gone a few
   seconds later. `%LOCALAPPDATA%\DashDeck\actions.log` has a SENDING line naming P0420 and a DONE line.
5. Same, without `engine.rpm=0`. **Expect** the hold refused: *Only with the engine off and the
   ignition on…*, and a REFUSED line in the log.

**In the truck.** You need: the tablet on the adapter. **Clearing resets the readiness monitors** —
only do steps 9–10 when you want the codes cleared anyway (an emissions test would fail until a few
days of driving).

6. **Engine running, parked.** **Expect:** no window and no strip (unless the check-engine light is on
   in the cluster — then the window, with the codes FORScan also shows). Settings ▸ Diagnostics: CHECK
   ENGINE, ENGINE HOT and LOW VOLTAGE read **OFF**; OIL PRESSURE, BRAKE, DOOR OPEN, SEATBELT and TYRE
   PRESSURE read **NO READING** (their identifiers are not found yet).
7. **READ CODES.** **Expect:** *No codes, stored or pending (7E0 …, 7E1 … answered)* — or the codes
   FORScan shows. Both modules should answer; if only 7E0 does, note it.
8. Drive. **Expect:** nothing pops; the dash runs as before. No new light on the cluster.
9. *(Only if clearing is wanted.)* Park, switch the engine **off**, push START **without** the brake
   (ignition on). ALLOW CLEARING CODES — ON; READ CODES; hold **HOLD TO CLEAR CODES**. **Expect:**
   *Codes cleared*, a fresh read showing none, and the cluster's check-engine light out.
10. Try the hold with the engine running. **Expect:** refused, *Only with the engine off…*, and nothing
    sent (the log says REFUSED).
11. Turn ALLOW CLEARING CODES **OFF** again.

**A failure looks like:** a window or strip with no light on the cluster (a false alarm — note which
and the reading in Settings); a light on the cluster with no window when its popup is on; anything
sent with the switch off or the engine running (a SENDING line in the log); a cluster message or
new light after clearing other than the check-engine light going out; the stage not coming back after
DISMISS.

## Multi-state signals: 4WD, gear, drive mode, wipers, headlights (ADR-0056)

**At the desk first** (no truck): run DashDeck on the synthetic truck.

1. DASH. **Expect** at the bottom centre of the console a row **P R N D M** with **P** lit while the
   truck idles at the start, then **D** lit once it pulls away; under it **4WD 2H 4A 4H 4L** with
   **2H** lit.
2. MODIFY WIDGETS, add a card, pick **4WD Mode** (under Drivetrain). **Expect** the card to read
   **2H**, not a number, and no bar style offered.

**On the laptop with the ID matcher and the truck** (engine running, parked, FORScan through the tap):

3. In FORScan, find the 4x4 mode among the transfer case or body module's values and add it to its
   live data. In the matcher, select the identifier FORScan is now asking (Ctrl+N).
4. With the truck in 2H, type `2H`, **Enter**. Turn the 4WD knob to 4A; when FORScan shows 4A, type
   `4A`, **Enter**. Then 4H, then 4L (4L needs neutral and the brake — follow the truck's own
   instructions). **Expect** the hint to ask for a second round.
5. Go round again: 2H, 4A, 4H, 4L. **Expect:** *One field tells all 4 states apart, every time*, the
   formula showing which bits and each state's value, and *Reads now* following the knob.
6. Select **4WD Mode** (NEEDS ID) on the left, **Ctrl+Enter**, pair and save to `signals.user.json`.
   Copy it to the tablet; on the tablet, Settings ▸ Sensors ▸ 4WD Mode ▸ EDIT ▸ **TEST**: **Expect**
   *Decodes to 2H* (or whichever it is in). SAVE, RESTART NOW.
7. DASH. **Expect** the 4WD row to light the mode the truck is in, following the knob within a couple
   of seconds.
8. The same for the gear selector (P R N D M from the shifter), drive mode, wipers and headlights,
   each with every state typed twice.

**A failure looks like:** a state lit that the truck is not in; a row that stays on one state while
the knob moves; `?` with a number when the truck is in a state that was typed (the field is wrong —
match again); TEST saying a number instead of a name.

## Scrolling with a finger (touch fix)

**You need:** the tablet, in tablet mode, no keyboard or mouse attached. No truck needed.

1. Settings ▸ **Sensors**. Put a finger on the list and drag up. **Expect** the list to follow the
   finger and carry on a little when you let go, down to the last signal.
2. Drag past the bottom. **Expect** the list to stop; the whole dash must **not** slide up with it.
3. Tap a signal in the middle of the list. **Expect** its editor to open — a tap is still a tap, not
   the start of a scroll. BACK.
4. The same in Settings ▸ **Appearance**, **Themes**, **Vehicle**, **Apps** and **Diagnostics**, and
   in the card editor (MODIFY WIDGETS, tap a card): each scrolls with a finger, and its buttons still
   answer a tap.

**A failure looks like:** a list that will not move under a finger; the whole window shifting when a
list reaches its end; a button inside a list that no longer answers a tap (it would need the same
fix the card strip has, `DashboardView.HandleTap`).

## Android Auto on the stage (ADR-0057)

**At the desk first** (no dongle): `dotnet run --project src/DashDeck.Host -- --synthetic-dongle`.

1. Choose **PHONE** on the launcher bar. **Expect** *ANDROID AUTO CONNECTED — The session is open and
   no picture has arrived yet*, and `SYNTHETIC DONGLE` under it. Without `--synthetic-dongle`
   **expect** *NO DONGLE — No Carlinkit dongle is plugged in… Looking again every few seconds.*

**You need:** the Carlinkit CPC200-CCPA, bound to WinUSB with Zadig
([phone-projection.md](phone-projection.md)); the phone; the truck parked, engine running (the
dongle and the Surface draw power); **the Pi unplugged from SYNC 3** for this test.

2. Plug the dongle into the Surface. Open DashDeck, choose **PHONE**. **Expect** *WAITING FOR A PHONE*
   within five seconds (it looks again by itself if it said NO DONGLE first).
3. Pair the phone with **DashDeck** over Bluetooth and accept Android Auto on the phone. **Expect**
   within about 30 s the headline to read **ANDROID AUTO**, then the Android Auto screen filling the
   stage — no black band at the sides beyond a thin one.
4. Tap the Maps icon. **Expect** Maps to open; the tap lands where your finger was. Drag the map.
   **Expect** it to follow your finger.
5. Play music on the phone through Android Auto. **Expect** it from the Surface's speakers (or
   whatever Windows' output is set to). Start navigation to somewhere; **expect** directions to speak
   over the music.
6. Open the three-dot menu. **Expect** it drawn over the picture, not under it. Choose DASH below the
   stage and come back to PHONE. **Expect** the picture still live.
7. Close DashDeck and open it again, choose PHONE. **Expect** the phone to reconnect by itself, without
   touching it.
8. Pull the dongle out. **Expect** *LINK LOST*, then *NO DONGLE*; plug it back in and **expect** it to
   find the dongle again within a few seconds and the phone to return.

**A failure looks like:** *NO DONGLE — not bound to WinUSB* after Zadig (bind the right device);
stuck on *WAITING FOR A PHONE* with the phone paired (the Pi took it, or the dongle wants Android Auto
switched on — tell me the dongle's firmware from its own web page); *CONNECTED* with no picture for
more than 30 s; a picture that is stretched or tapped in the wrong place (send a photo); sound
stuttering or seconds late; the picture freezing (RECONNECT from the three-dot menu, and tell me
when it happened).

## Android on the stage — measuring it first (ADR-0075)

Before any of this is built into the dash, find out whether the Surface can run Android as its own
device fast enough. This is done at a desk, plugged in. The truck is not needed.

**You need:**
- the Surface, on mains power;
- a dash build with `AndroidProbe\` in its folder (any release after this branch merges);
- about 15 GB free for Android Studio and two system images.

**Set up, once:**
1. Turn on Windows' hypervisor platform: **Settings ▸ System ▸ Optional features ▸ More Windows
   features**, tick **Windows Hypervisor Platform**, OK, restart. This is a Windows feature, not a
   driver.
2. Install [Android Studio](https://developer.android.com/studio). In **SDK Manager ▸ SDK Tools**,
   make sure **Android Emulator** is installed. Do not install the *Android Emulator hypervisor
   driver*: it is a driver, and the Windows feature does the same job.
3. In **Device Manager**, make two virtual devices, both **x86_64**:
   - **Automotive:** category *Automotive*, e.g. *Automotive (1408p landscape)*, with an image whose
     name says **Automotive with Google Play**;
   - **Regular:** category *Tablet*, e.g. *Pixel Tablet*, with a **Google Play** image.

   - **Stage-shaped:** **New Hardware Profile** in the device list, category *Tablet*,
     **1824 × 1272** at **320 dpi** (so Android's units match the dash's), with the same Google Play
     image. Name it `DashDeck_Stage`. This is the one the tablet should be judged on: the stage is
     1824 × 1272, and rendering 2560 × 1600 for it is work thrown away.

   Start each once from Android Studio, let it finish booting, and close it. That makes the
   quick-boot snapshot the probe will start from.
4. Close Android Studio.

**Measure:**
1. Start DashDeck as usual and leave it running, so the numbers include the dash.
2. Open a terminal in the dash's folder and run `AndroidProbe\AndroidProbe.exe`. **Expect:**
   - the SDK found;
   - the virtualisation check saying the hypervisor is usable (*WHPX*);
   - both devices listed, one marked **Automotive** and one **regular**.
3. Run `AndroidProbe\AndroidProbe.exe --stop-running` first. It shuts down any emulator Android
   Studio left running, which would otherwise hold the AVD and the port.
4. Run `AndroidProbe\AndroidProbe.exe --avd <the Automotive name>`. Nothing appears on screen; it
   runs hidden, which is the point. **Expect, within a couple of minutes:**
   - *Discovery file* with `grpc.port` and a hidden `grpc.token`;
   - *Booted: N s*;
   - *Frames … dragging: N fps*;
   - *Touch to next frame: N ms*;
   - *Emulator processes: N MB*;
   - *Audio: …*.
5. Run the same line again. The second run starts from the snapshot, so **boot time is the one that
   counts**.
6. Repeat steps 4 and 5 with `Pixel_Tablet` and `DashDeck_Stage`.
7. Send me the reports from `%LOCALAPPDATA%\DashDeck\androidprobe\`. Each has a `.png` beside
   it, the screen as the stage would show it. Look at it first: if it shows anything personal, leave
   it out.

**What decides it** (ADR-0075's table): boot under 15 s from the snapshot, 20 fps or more while
dragging, touch under 150 ms, and memory that leaves the dash comfortable.

**A failure looks like:**
- *virtualisation* saying no hypervisor: step 1 did not take, or virtualisation is off in the
  Surface's UEFI;
- *gRPC REFUSED*: send the report, which shows the discovery file the emulator wrote;
- *NOT BOOTED* or *THE EMULATOR EXITED*: send the report, which includes the emulator's own warnings;
- the dash itself stuttering while the probe runs. Note when it did.

## Android on the dash — the ANDROID cell (ADR-0075)

**You need:**
- the Surface, with this build;
- Android Studio's emulator, and both AVDs from the probe's setup above (*Automotive with Google
  Play* and *Pixel Tablet*);
- Windows Hypervisor Platform on;
- **scrcpy closed**, so its `adb` does not fight the SDK's;
- parked, for the first try.

1. Start DashDeck. **Expect:** a fifth cell on the nav, **ANDROID**, after MAPS.
2. **Hold** ANDROID for about half a second. **Expect:** a menu over the nav, with:
   - **ANDROID**, naming `Pixel_Tablet · 2560×1600`;
   - **ANDROID AUTOMOTIVE**, naming the Automotive AVD;
   - **STOP ANDROID**;
   - a dot beside the one the cell opens now.
3. Choose **ANDROID AUTOMOTIVE**. **Expect:**
   - Android full screen, with the stage collapsed;
   - *STARTING ANDROID AUTOMOTIVE* for a few seconds;
   - then Automotive's home screen, about 10–20 s on a first start and a couple of seconds after
     that.
4. **Touch** it: open the app grid, scroll, open Settings. **Expect:** it follows your finger
   straight away.
5. **Tap DASH**, then **tap ANDROID** again. **Expect:** Android at once, with no restart. It kept
   running.
6. **Double-tap ANDROID.** **Expect:** Android on the stage, with the dash's console below it. It
   still answers touch on the stage.
7. **Tap ANDROID** once. **Expect:** back to full screen.
8. With GPS on, open **Maps** inside Automotive. **Expect:** the truck's own position.
9. **Hold** ANDROID and choose **ANDROID**. **Expect:**
   - *STOPPING*, then *STARTING ANDROID*, then the tablet's home screen;
   - letterboxed in the full-screen region, because a landscape tablet does not fill a portrait
     region.
10. **Hold** ANDROID, then **STOP ANDROID**. **Expect:** *ANDROID STOPPED*. Task Manager shows no
    `qemu-system-x86_64`.
11. Close DashDeck with Android running, then start it again and tap ANDROID. **Expect:** a quick
    start, because the snapshot was saved on the way out.

**A failure looks like:**
- *NO ANDROID EMULATOR* when Android Studio is installed. Note where its SDK is.
- *… DID NOT START*, with a reason. Send the reason.
- Touches landing in the wrong place.
- The dash stuttering while Android runs.
- `qemu-system-x86_64` still running a minute after DashDeck closed.

## The audio delay and the video sync offset — parked 2026-10-15

Both walkthroughs that were here are gone with the feature. The delay measurement, the
`AudioProbe` tool and the video sync offset are on the **`parked/audio-delay`** branch, and
nothing in a current build measures or compensates for the Bluetooth link.

**Why, in one paragraph, so the next person does not rebuild it by accident.** The offset was
built for *"the offset for video, like in Nuvio"* and wired to `SetAudioDelay` on DashDeck's own
LibVLC player. Nuvio is a separate process drawing its own picture and sending its own audio to
the mixer, so the offset never reached the only video that gets watched, and Nuvio exposes no
delay control of its own. The link's delay is also inaudible for music, which is what the Stereo
panel plays. That left a measurement with nothing to feed and three sections of Settings to
maintain. **The cheap answer, if a film is ever out of sync again:** send that one application's
audio to the tablet's speakers in Windows' volume mixer, which removes the Bluetooth link instead
of compensating for it. Q36, Q37 and Q39 hold the detail.

## The Stereo panel, and getting out of the way of directions (ADR-0060, Q14)

DashDeck's own playback, on the bottom third where the climate panel and the console live. It is
**additive**: the truck's own radio is untouched and unreachable, and with no AUX jack in this truck
(Q33) the sound goes out over Bluetooth to SYNC 3 like everything else DashDeck plays.

**You need:**
- the Surface, paired to SYNC 3, with the truck as the Windows **default** output;
- a folder of music on the tablet;
- DashDeck built from develop with this change.

### First, at the desk

1. **Settings ▸ AUDIO ▸ MUSIC FOLDER.** Type the path to your music. **Expect:** nothing visible
   here; the Stereo panel reads it when you open it.
2. Tap **STEREO** in the navigation. **Expect:** the panel, with the queue filled on the right. It
   used to say "not built yet" — that entry is gone, and it was the last one.
3. **PLAY. Expect:** sound from the truck, the title and album filling in, the position line
   advancing once a second, and the playing track marked with a dot in the queue.
4. **Tap partway along the position line. Expect:** it jumps there. It is a tap rather than a
   dragged thumb on purpose — a thumb is a small target in a moving truck.
5. **PREV twice.** **Expect:** the first tap restarts the current track, the second goes back one.
   That is deliberate: past the first three seconds, "previous" means "start this again", which is
   what every player does and none of them explain.
6. **Tap a track in the queue. Expect:** it plays.
7. **Leave STEREO for DASH, then come back. Expect: the music never stopped.** This is the one
   behaviour worth checking carefully — the panel is thrown away when you leave and the player is
   not, so if the music stops when you navigate away, that is the bug.

### Settings ▸ AUDIO, as it now stands

8. **Expect two things in that section and no more:** the **MUSIC FOLDER** and the ducking switch.
   The output delay, the video sync offset and the MEASURE IT probe were parked on 2026-10-15 —
   see the note further up. A build that still shows them is older than that.

### What is NOT testable yet

**The ducking contract has nothing to trigger it.** The policy is built and tested and the switch
is in Settings ▸ AUDIO, but nothing yet reports that projection is speaking — that needs the
Carlinkit dongle (Q25). So the switch is real and currently inert, and you will not see music duck
under a turn instruction until that is wired. Said plainly because a switch that does nothing is
worse than no switch if nobody wrote down which.

### A failure looks like

- **"No music folder chosen"** with a folder typed in — check the path, and that it is reachable
  from the tablet.
- **"Nothing playable under …"** — the folder has no audio files the player recognises. The
  extensions it looks for are listed in the message.
- **"No audio playback: LibVLC would not load"** — the native player is missing from the build.
  Everything else in the dash should still work; if the dash did not start at all, that is a
  different and worse bug, because this is meant to cost only the music.
- **The music stopping when you leave STEREO.** The player is supposed to outlive the panel.

## The phone's GPS over Bluetooth (ADR-0027, ADR-0028, Q38)

The Surface has no GPS, OBD-II carries no position, and the dongle's projection does not expose
the phone's location — so the phone itself is the only GPS on hand, and everything with a map in
front of it waits on this. The code has been in since September and **has never seen a real
phone**: the parser and the moving-heading rule are tested, the serial read loop is I/O and could
only ever be checked on glass. This is that check.

**You need:**
- the phone, running a GPS-share app that emits NMEA — **GPS NMEA Tether** is what the owner uses;
- the phone paired to the Surface in Windows Bluetooth settings, and the app set to share over
  **Bluetooth** (the same app can usually serve over USB tether or Wi-Fi instead, which is the
  `--tcp` path below);
- DashDeck built from develop with this change — the first step is `GpsProbe`, not the dash.

Nothing here touches the vehicle, and the position never leaves the tablet.

### First, find the port the phone answers on

This is the step that exists because of one awkward fact: **Windows turns a single pairing into
several COM ports, and only one of them carries the phone's data.** Windows names them by number
only, so the picker in Settings cannot tell you which. Worse, the GPS setting is applied at the
next launch, so guessing wrong used to mean a relaunch to find out.

```
dotnet run --project src\DashDeck.GpsProbe -- --list
```

**Expect** a short list — `COM3`, `COM5`, `COM7` and so on. The OBD adapter is in this list too.
Then try them one at a time, with the phone's GPS app open and sharing:

```
dotnet run --project src\DashDeck.GpsProbe -- --port COM5 --seconds 15
```

**Expect one of five answers, each pointing somewhere different:**

| What it says | What it means | What to do |
| --- | --- | --- |
| `FIX — 49.89510, -97.13840 · 63.2 km/h · course 182°` | A position arrived. | **This is the port.** Put it in Settings. |
| `NO FIX — n sentences, the receiver has no position yet` | The link is right, the phone has no sky. | Go outside; the port is still the answer. |
| `CONNECTED — n lines, no RMC or GGA (GSV, GSA)` | The link is right, the app is sending the wrong sentences. | Turn **RMC** on in the app. |
| `CONNECTED — n lines, none of it NMEA` | Something else is on this port — usually the OBD adapter. | Try the next port. |
| `NO DATA — nothing has arrived` | Wrong port, or the app is not actually sharing. | Try the next port; check the app says connected. |

`--raw` prints every sentence as it arrives, if you want to see the feed rather than a verdict.
If the app serves over the network instead, `--tcp 192.168.1.7:11123` takes the same path — use
whatever host and port the app itself reports, because the number differs between apps.

### Then turn it on in the dash

1. **Settings ▸ DISPLAY ▸ LOCATION (PHONE GPS).** Set **USE PHONE GPS — ON** and **VIA —
   BLUETOOTH**, press **REFRESH**, and pick the port the probe found.
2. **Close DashDeck and open it again.** The GPS setting is read at launch, like the adapter's.
3. **Go back to Settings ▸ DISPLAY.** **Expect the readout at the top of that section to be
   live** — `FIX — 49.89510, -97.13840 · …` with `bluetooth COM5 · just now` under it. This is
   new, and it is the point of this round: the section used to give no feedback at all.
4. **Expect `NOT RUNNING` if you have not relaunched yet** — that is the readout telling you the
   setting is saved and the source is not up, rather than pretending it is.

### Then drive it

5. **Open the COMPASS stage.** **Expect LATITUDE and LONGITUDE** to carry real numbers — those
   gauges have been reading `location.latitude`/`location.longitude` all along with nothing behind
   them.
6. **Drive.** **Expect the compass to switch to `PHONE · GPS`** once you are over about 5 km/h,
   and to hand back to `TABLET · MAGNETIC` when you stop. A stationary GPS reports a random
   course, which is why it declines rather than spinning the rose.
7. **With the adapter plugged in, expect speed to stay the truck's own.** Ground speed prefers
   `vehicle.speed`; the phone is the fallback, not the winner. Settings ▸ SENSORS shows which
   answered.
8. **Unpair the phone, or walk away from the truck with it.** **Expect the readings to age to
   `STALE` and then `NO FIX`** — not to freeze on the last position. A frozen dot that looks live
   is the worst failure a map can have, so this one is worth doing deliberately.
9. **Pair it again. Expect it to come back on its own** within a few seconds, without restarting
   the dash.

### What steadiness should look like (changed 2026-10-15)

The first real phone came in **intermittently**, and it was the read loop rather than the link:
it kept whichever sentence parsed last, so a GGA took away the course the RMC beside it supplied,
and a single void sentence threw away a good position. A fix is now **built** from the sentences
(ADR-0027's addendum), so:

- **The readout should sit still** at `FIX — …` with the age under it ticking between `just now`
  and however often the phone sends. **That age is the feed rate** — if it reads `3 s ago` over
  and over, the app is sending every three seconds, which is worth knowing.
- **A Bluetooth hiccup should read `STALE`, with the position still there**, not blank.
- **A phone switched off or left behind should reach `NO FIX` within about ten seconds.** This is
  the one to check deliberately: holding a position forever is worse than losing it.
- **The compass should not flick between `PHONE · GPS` and `TABLET · MAGNETIC`** second by second
  while driving. One hand-back when you slow below about 5 km/h is the rule working.

**The things I need you to tell me:**
- which port it turned out to be, and how many were in the list (if a pairing really does make
  several, the picker should probably sort or mark them);
- whether the phone's NMEA parses at all — the parser has only ever been fed sentences I wrote;
- whether `PHONE · GPS` actually appears on the compass while moving, and at what sort of speed;
- whether a dropped link ages or freezes.

**A failure looks like:**
- **`OFF` in the readout after relaunching** — the toggle did not save; check Settings again.
- **`NO DATA` with a port the probe liked** — something else grabbed the port, often the adapter
  search. The GPS port is meant to be reserved from it; if this happens, say so, because that
  reservation is exactly what ADR-0028 claims works.
- **Numbers on the gauges and nothing on the compass** — the heading threshold or the moving rule.
- **A position that never changes while driving** — the fix is being held rather than refreshed.

## Updating from the dash (ADR-0061, Q40)

**Confirmed 2026-10-21:** v.0.10.1 → v.0.11.0 went all the way through on the Surface — it closed,
swapped and came back by itself on the new version (ADR-0061's second addendum). The steps below
stay as the check for any later change to the updater.

**Do this once at a desk, on purpose, before you ever need it in a truck.** The check and the
download are ordinary network code; the install replaces the folder the dash is running from, and
until 2026-10-21 that half had never completed on Windows (Q40). Finding out whether it works while parked somewhere in
the dark is the wrong way round.

**You need:** a deploy folder (not a build folder — it will refuse), the internet, and a release
newer than the build you are running. To create that second thing deliberately: publish a deploy
with an older version stamp, `pwsh -File publish.ps1 -Version 0.8.0`, and the newest release will
be ahead of it.

### The three buttons

1. **Settings ▸ ABOUT ▸ UPDATE.** **Expect** `You are on 0.9.0` and *"Nothing is checked until you
   ask"* — and **expect no other buttons yet**. If anything here says an update is available
   before you press a thing, that is a bug in the one rule this screen has.
2. **CHECK FOR AN UPDATE.** **Expect** either `You are on 0.9.0 — nothing newer`, or
   `0.10.0 is available — 334 MB` with when it was published, plus **DOWNLOAD** and **WHAT
   CHANGED** appearing.
3. **WHAT CHANGED** opens the release notes in the browser. The dash keeps running behind it.
4. **DOWNLOAD.** **Expect** a percentage that climbs, then `0.10.0 is ready to install` and an
   **INSTALL AND RESTART** button. Minutes on a hotspot — **STOP** is there and is meant to leave
   everything as it was.
5. **INSTALL AND RESTART.** **Expect** the dash to close, and to come back within about ten
   seconds on the new version. Check Settings ▸ ABOUT: the version should have changed.
6. **Look beside your deploy folder. Expect `DashDeck.previous`** — the folder you were running,
   kept. That is the recovery, and it is the thing to confirm exists *before* you trust this in a
   vehicle.

### What a failure looks like, and the way back

- **The dash closes and does not come back.** Since 2026-10-18 a failed swap opens the old dash
  again, so this should not happen; if it does, launch `DashDeck\DashDeck.Host.exe`, or
  `DashDeck.previous\DashDeck.Host.exe` if that folder is there — either is the build you were
  on. Then send `%LOCALAPPDATA%\DashDeck\updates\swap.log`, which says which move failed.
- **The dash comes back on the old version.** The swap did not happen and the old folder was
  reopened. `swap.log` says why. Before 2026-10-18 this was every update: the script stood in the
  folder it was trying to move (ADR-0061's addendum).
- **"DashDeck did not exit within 30s — nothing was changed"** in that log: the dash was still
  holding its files. Nothing was moved; try again from a clean launch.
- **"could not move the current folder aside after 15 tries"**: something else has the folder
  open for longer than 15 s — Explorer sitting in it, a terminal `cd`'d into it, or antivirus
  reading it. Nothing was changed and the old dash was reopened. Close whatever it is and try again.
- **"could not clear the previous copy"**: `DashDeck.previous` from the last update would not
  delete, usually because something has a file in it open. Nothing was changed. Delete that folder
  by hand and try again.
- **"putting the old one back"**: the new folder could not be moved in and the old one was
  restored. The dash should still start from its original path.
- **"This cannot be installed"** with a reason, straight after the download: that is a guard
  refusing, not a crash. A build folder, an unfinished download, an archive that is not a
  DashDeck, or not enough free space for three copies.
- **`%LOCALAPPDATA%\DashDeck\updates` is about 700 MB** after an update — the archive plus the
  staged copy. Deleting its contents is safe; nothing automatic cleans it up yet.

## The map (ADR-0063) and its three sizes (ADR-0062)

**Added 2026-10-16, and nothing in it has ever been run.** There is no Windows, no WebView2 and no
signal in the session that built it, so the map has never been seen to draw. Everything testable
without those is tested; what follows is the part that needs glass.

**Start here, before anything else.** The tile endpoints are read from OpenFreeMap's own README and
have never been probed — that session's proxy refuses the hosts. One command settles it, on the
Surface, on the truck's connection:

```powershell
curl.exe -s -o $null -w "style %{http_code}`n" https://tiles.openfreemap.org/styles/dark
curl.exe -s -o $null -w "tile  %{http_code} %{size_download} bytes`n" https://tiles.openfreemap.org/planet/latest/10/246/355.pbf
```

**Expect:** `style 200` and `tile 200` with a few kilobytes. Anything else and the map cannot work
yet, and the rest of this is pointless — send me the output instead.

**You need:** the dash running, the truck's connection up, and the phone sharing GPS over Bluetooth
as in *The phone's GPS over Bluetooth* above. **At a desk instead**, `--gps synthetic` gives a
moving fix near downtown Winnipeg and the whole walkthrough works without the truck:

```
dotnet run --project src\DashDeck.Host -- --gps synthetic --nav MAPS
```

### Does it draw

1. **Tap MAPS in the nav.** **Expect:** the bottom third fills with a dark map, an orange cone and
   dot where you are, and a small attribution line bottom right reading *OpenStreetMap contributors
   · OpenMapTiles · OpenFreeMap*. Before it draws you get *Loading the map · fetching tiles* on a
   dark panel, which should last a second or two on a good connection.
   **A failure looks like:** *No map* with a reason under it — that is the honest state and the
   reason is the thing to send me. A **white** flash or a white map means the style loaded but not
   the dark one. A dark rectangle with no words at all is the one outcome that should be impossible;
   if you get it, say so, because it means the page failed without reporting.
2. **Check the position states.** With GPS off (Settings ▸ Display): **expect** *No GPS* and a line
   saying where to turn it on — **not** *No map*, because the missing thing is the position, not the
   tiles. With GPS on and the phone not sharing: **expect** *Waiting for a position* naming the
   port. Those two being told apart is the point.
3. **Watch the marker while moving.** **Expect:** the cone points the way the truck is going and the
   map slides to keep you centred. Below about 5 km/h the cone **stops turning** rather than
   spinning on the spot — that is deliberate, since a GPS course at a standstill is noise.
4. **Turn the phone's GPS off while driving.** **Expect:** within about ten seconds the marker
   **goes grey where it is** and the readout ages. It must not sit there in orange looking current,
   and it must not jump to nowhere.

### Does it behave

5. **Drag the map with a finger.** **Expect:** it pans, it stops following, and a **RECENTRE** chip
   appears beside TO THE STAGE. Tap it: the map comes back to the truck and stays with it.
   **A failure looks like:** the map fighting your drag (following was never switched off), or no
   RECENTRE appearing — which leaves you with a map that has lost you.
6. **Pinch to zoom, then tap MAPS again** to go full screen. **Expect:** the map fills everything
   between the status strip and the nav, **at the zoom you left it at**. It will redraw — the
   browser control is rebuilt by the move — so a second of tiles arriving is expected. What is not
   expected is coming back at a different zoom or over a different place. **This is the step most
   likely to be wrong.**
7. **Tap MAPS again** for the bottom third, then again for full screen. **Expect:** the stage
   reappears and disappears with the same occupant still running, and the map keeps its place each
   time.
8. **TO THE STAGE.** **Expect:** the map moves up onto the stage, the chips and the hint go, the
   status strip reads **MAPS**, and the region below says *The map is on the stage* with **BRING IT
   BACK**. Open the three-dot menu: **expect** **BRING THE MAP BACK** and **RECENTRE** in it — the
   chips are not available on the stage, so the menu is the only way to either.
9. **Press MAPS in the nav while it is on the stage.** **Expect:** the map comes back, at the size
   it had when it went up.

### Does it wear the theme (ADR-0064)

10. **Settings ▸ Appearance ▸ Themes, and choose LCARS.** **Expect** the map to change with the
    rest of the dash, without a relaunch: **lavender streets, peach motorways, an orange vehicle,
    near-black ground.** Those come from the theme's existing text ramp and accent, not from
    anything map-specific — the LCARS file names only water and greenery.
    **A failure looks like:** a grey map under an LCARS console (the restyle did not reach the
    page), or the map going blank (the style would not parse — the `--shot` text file carries the
    reason as `map: … style: …`).
11. **Switch back to Modern, then to Glass.** **Expect** each one's map to match its dash. Then
    **Settings ▸ Appearance ▸ NIGHT**: the ground should darken further than the names on it, so
    labels stay legible rather than everything fading together.
12. **Compare against somebody else's cartography.** Relaunch with
    `--map-style https://tiles.openfreemap.org/styles/dark`. **Expect** OpenFreeMap's own map,
    ignoring the theme, with the vehicle marker still in the theme's accent. This is the
    comparison to reach for if the themed map looks wrong — and if theirs reads better in the cab,
    say so, because that is a real answer and the token defaults can move.
    Their other looks: `bright`, `liberty`, `positron`, `fiord`.
13. **Labels are Noto Sans and will stay that way** whatever `uiFont` says — a map label needs
    glyphs built into PBF ranges and a theme's TTF has none (Q45). If the LCARS map looks right in
    every way except the lettering, that is why, and it is written down rather than a bug.

### Does the search work (ADR-0065)

**Parked, with the connection up.** The dash will not let you type a destination above 5 km/h, so
every step here is stationary on purpose.

14. **Tap SEARCH on the map region.** **Expect:** the map goes full screen, the search covers it,
    and **the Surface's own keyboard comes up** with the field already focused — the caret is in it
    and you can type straight away.
    **If no keyboard appears:** that is Windows, not the dash. Settings ▸ Devices ▸ Typing ▸ *Show
    the touch keyboard when there's no keyboard attached*. Tell me either way, because it decides
    whether this is usable with the tablet mounted.
15. **Type an address you know** — a street and a town. **Expect:** it appears in the field, and
    **nothing searches while you type.** That is deliberate and required: Nominatim's policy
    forbids auto-complete outright.
16. **Tap SEARCH.** **Expect:** up to five results, each a bold line with the address under it,
    nearby ones first. **Tap SEARCH again straight away** and expect *Just a moment — one search a
    second*, which is the usage policy enforced rather than trusted.
17. **Tap a result.** **Expect:** the search closes, the map flies to it, drops a **pale pin** —
    deliberately not the vehicle's colour — and **a popup opens on the map** with the name, the
    address, and a **DIRECTIONS TO HERE** button. RECENTRE appears, because choosing a destination
    stops the map following you.
18. **Search a business** — a coffee shop, a garage, a hardware store. **Expect** the popup to
    carry what OpenStreetMap knows: a kind in an accent-coloured pill, opening hours, a telephone
    number, a website. **Most places will have none of it** and that is normal — OSM knows what a
    house is and rarely anything else. A business in town is where this shows.
    **A failure looks like:** raw HTML or quotes in the popup (the contents are meant to be plain
    text, never markup), or the popup in white rather than the theme's colours.
19. **Press DIRECTIONS TO HERE.** **Expect:** the button says *Working it out…*, then a route is
    drawn from where the truck is to the pin, the map zooms out to fit the whole way, and a chip
    appears at the bottom reading something like **12.4 km · 18 min  STEPS ›**.
    **A failure looks like:** *No answer — directions need a connection*, or *No way there that the
    router could find* — both honest, and both worth telling me which.
20. **Tap the STEPS chip.** **Expect:** the turn list, each row an instruction and a distance —
    *Turn right onto Portage Avenue · 570 m* — with the destination and the summary at the top,
    the router credited at the bottom, and **‹ MAP** to go back. The route is still drawn on the
    map behind it.
    **This is directions, not guidance.** Nothing here follows the truck, speaks, or re-routes, and
    the row you are on is not highlighted because the dash does not know it. That is
    [ADR-0058](decisions/ADR-0058-offline-map-and-routing.md)'s bundled engine and it is still
    ahead — read these while stopped.
21. **Search somewhere else without clearing anything.** **Expect** the old route and pin to
    disappear as the new pin lands. One destination at a time; two on a map is a question rather
    than an answer.
22. **Tap SEARCH again.** **Expect** the address you chose under **RECENT** — tap it and go
    straight there with no request at all. **Search the same address twice** and expect the results
    instantly, with no network: it is cached, which the policy asks for and which also means it
    works once the signal has gone.
23. **Turn the connection off and search.** **Expect** *No answer — the search needs a connection*,
    with the map keeping whatever tiles it had. Recents still work.
24. **The gate. Roll slowly, or get someone to drive, and tap SEARCH.** **Expect** the search to
    open and say *Stop first — a destination cannot be typed at nn km/h*, with the field shut, **no
    keyboard asked for**, and **‹ MAP** still working. **Stop again without touching anything** and
    expect the field to come back within a second or two.
    **This is the step worth being fussy about** — it is the one safety rule in the feature.

### In the cab

25. **At night, parked.** **Expect** the map readable without being a torch. If the theme's map is
    too bright or too dim at night, the fix is a token (`mapLand`, `mapRoad`) or the night rule
    behind it — tell me which way it is wrong.
26. **Watch the data.** This is the first feature that spends the truck's data by moving. Vector
    tiles are small and the browser caches them, so a repeated drive should be nearly free — a
    drive somewhere new will not be. If it costs more than you expect, say so: that is Q44's
    argument, and it is the next thing to build.

**What this does not test**, and cannot yet: anything offline. Drive out of signal and the map
stops filling in — tiles already fetched stay, new ground does not arrive. That is
[Q44](04-open-questions.md), it is sketched and not built, and it is the half that makes this
useful on a highway. No routing and no guidance either — those are still
[ADR-0058](decisions/ADR-0058-offline-map-and-routing.md)'s stages two and three.

### Do the navigation readouts work (ADR-0066)

The map's numbers are now **sensors**: a card or a stage layout can show the distance to where you
are going the way it shows pitch or lateral g. Six of them, under **Navigation** in the picker.

**Most of this is testable at the desk, and should be done there first** — launch with
`DashDeck.Host.exe --gps synthetic`. The synthetic track now drives a real loop (800 m radius, a
true 60 km/h, five kilometres a lap, about five minutes round), so a distance worked out from it
actually changes. It used to weave inside a sixty-metre box while claiming 60 km/h, which was fine
for showing a marker and useless for this.

27. **MODIFY WIDGETS, add a card, and open the value picker.** **Expect** a **Navigation** group
    below Location with six entries: *To Destination*, *Destination Bearing*, *Route Distance*,
    *Route Time*, *Distance Left*, *Time Left*.
    **A failure looks like:** no Navigation group at all, which means the catalog did not load —
    check `catalog/sensors.device.json` travelled with the build.
28. **Add *To Destination* and *Destination Bearing* and leave the map with no destination.**
    **Expect** both cards to read as having nothing — a dash, not a zero — with **NAV · NO
    DESTINATION** in the footer. **Expect *Distance Left* to say NAV · NO ROUTE**, which is a
    different sentence on purpose.
29. **Search an address and pick it, then come back to the cards.** **Expect** *To Destination* in
    kilometres and *Destination Bearing* in degrees, both **blue** on the synthetic track, because
    a number worked out from a simulated position is simulated. Watch them for a minute: the
    distance should change by about a kilometre a minute and the bearing should swing round.
    **A failure looks like:** white or amber instead of blue (the quality is not being carried
    through), a distance that does not move (the fix is not being re-read), or a bearing outside
    0–360.
30. **Press DIRECTIONS TO HERE, then look at the four route cards.** **Expect** *Route Distance*
    and *Route Time* to fill in and then sit still — they are the router's answer about the road,
    so they are **white, not blue**, and need no position at all. **Expect** *Distance Left* and
    *Time Left* to start near the totals and count down.
31. **Keep watching *Distance Left* on the synthetic track.** **Expect it to go to NAV · OFF ROUTE
    within a minute or so**, and that is the test passing, not failing: the route follows real
    roads and the synthetic truck drives a circle through whatever is there, so it leaves the line
    almost at once. **The straight-line card keeps working while that happens** — the dash refuses
    the number it cannot stand behind and keeps the one it can.
32. **Unplug the phone, or turn its GPS off, and wait five seconds.** **Expect** every navigation
    card to go to nothing — the same staleness rule the location cards use, so the two cannot
    disagree about when the GPS went quiet. **Expect *Route Distance* to carry on regardless**,
    because it never needed a position.
33. **Put one on the stage.** Add *Distance Left* to a stage layout the way any sensor goes on one.
    **Expect** it to behave identically, including the dash when there is no route. Nothing in the
    layout engine was changed for this.

**In the truck, and this is the part the desk cannot do:**

34. **Parked, with a route to somewhere a few kilometres off, drive it.** **Expect *Distance Left*
    to count down smoothly and *Time Left* with it**, and expect both to hold up until you deviate.
    Three things worth telling me about:
    - **Does it jump backwards anywhere?** That is [Q47](04-open-questions.md) — the countdown
      snaps to the nearest point of the route, so a road that doubles back near itself, a
      cloverleaf, or a there-and-back errand can pick the wrong end. Where it happened matters
      more than that it did.
    - **Does OFF ROUTE come up while you are still on the road?** The limit is 150 m, which should
      cover a divided highway drawn down its centre line. If a service road or a frontage road
      reads as off route, the number is wrong.
    - **Is *Time Left* believable?** It is the router's own estimate pro-rated by the distance
      left, not a fresh one — so it will not notice traffic, and it will not notice that the
      remaining half is town rather than highway. If it is badly out, that is worth knowing before
      anyone trusts it.

**What this is not.** No guidance: nothing speaks, nothing re-routes, and nothing highlights the
turn you are on. Those are still
[ADR-0058](decisions/ADR-0058-offline-map-and-routing.md)'s bundled engine.

### Do the words work (ADR-0067)

The things a place knows that are *not* numbers — its name, its opening hours, its telephone
number — are now cards too, as a third kind of source. Also testable at the desk, and the search
needs a connection whether or not the GPS is real.

35. **Add a card and look in the picker under Navigation again.** **Expect** seven more entries
    beside the six numeric ones: *Destination*, *Destination Address*, *Destination Kind*, *Opening
    Hours*, *Telephone*, *Website*, *Route*. Each one's second line ends in **words** rather than a
    unit, which is how you tell the two kinds apart in the list.
36. **Add *Destination* with nothing chosen.** **Expect** a dash and **nothing chosen** in the
    footer. **Then search a place and pick it.** **Expect** the name to appear within a second,
    with **FROM THE MAP** in the footer and a green border — a destination is what you chose, so it
    is never stale and never simulated, even on a synthetic track where the distance to it is blue.
37. **Search a business and add *Opening Hours*, *Telephone* and *Website*.** **Expect** OSM's own
    wording — `Mo-Su 06:00-22:00` is normal and is not a bug — and **expect most places to leave
    them blank**, which draws as a dash rather than as an empty card.
38. **Put *Destination Address* on a one-column card.** **Expect** it to wrap to two lines at a
    smaller size than a number, and to be cut off with an ellipsis if it is longer than that. **A
    long website will not fit** and is not meant to; tell me if a name you actually use gets
    clipped, because the size is a judgement rather than a measurement.
39. **Open the editor on a readout card and look at the rows.** **Expect** BAR greyed out saying
    *no range in the catalog*, the only unit **AUTO**, and the rate and format rows greyed saying
    *words, not a number* — visible and refused rather than silently missing, the way an option
    this dash cannot offer always is. **Switch a temperature card onto a readout** and expect the
    °C to go with it rather than sitting beside a shop's name.
40. **Add *Route* and press DIRECTIONS TO HERE.** **Expect** one line — `12 km · 18 min` — the same
    wording the chip under the map uses, because it is the same sentence rather than a second
    opinion about it.

**Cards only, deliberately.** A stage layout's gauge reads through the sensor service and readouts
are not in it, so *Destination* cannot go on a COMPASS layout. That is worth doing and is not done.

## The map's buttons and range lines (ADR-0071)

**Added 2026-10-21.** The buttons were driven in a headless browser and draw, press and survive a
restyle; nothing has been seen on the Surface's glass, and **neither imagery nor elevation has been
fetched from the tablet**. Start with the two probes.

**On the Surface, on the truck's connection:**

```powershell
curl.exe -s -o $null -w "imagery %{http_code} %{size_download} bytes`n" https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless-2020_3857/default/g/10/337/235.jpg
curl.exe -s -o $null -w "terrain %{http_code} %{size_download} bytes`n" https://s3.amazonaws.com/elevation-tiles-prod/terrarium/10/235/337.png
```

**Expect:** `imagery 200` with tens of kilobytes and `terrain 200` with a few. A failure on one
means only that base will be blank; the other two still work.

**At a desk first.** `DashDeck.Host.exe --gps synthetic`, with the adapter unplugged, gives a
moving fix near Winnipeg *and* the synthetic truck's distance to empty (the dash falls back to it
when the adapter does not answer), so every step below works without the truck.

1. **Open MAPS.** **Expect** four buttons down the right of the map — **SEARCH**, **ROAD**,
   **NORTH UP**, and the range with a number in kilometres — and **no SEARCH or RECENTRE chips**
   under the map any more. TO THE STAGE is still there.
   **A failure looks like:** no buttons at all once the map has drawn (the page did not get its
   `map:ready` dressing — send `--shot`), or the old chips still there (an old build).
2. **Range lines.** Zoom out (pinch) until you see two dashed rings around the truck: **orange,
   labelled 80 KM LEFT**, and **red, labelled EMPTY**, further out. The number on the range button
   is the red ring's radius. **Expect** them to be roughly circles centred on the truck.
   **A failure looks like:** the button says **NO RANGE** (no distance to empty is arriving — on the
   truck that means its identifier is not in your catalog yet, see below), or rings that stay put
   while the truck moves more than a few hundred metres.
3. **Press the range button.** **Expect** the rings to disappear and the button to say **OFF**.
   Press again and they come back. Close and reopen the dash: **expect** it to remember.
4. **Press ROAD.** **Expect** three choices to open beside it: ROAD, SATELLITE, TERRAIN.
   - **SATELLITE:** **expect** imagery under the theme's roads and names in a second or two. It is
     ten-metre imagery: fields, rivers, yards and towns read; houses do not, and close in it goes soft.
   - **TERRAIN:** **expect** the road map with shading where the ground rises — river valleys and
     the escarpment show; the flat Red River valley mostly does not, and that is right.
   - Back to **ROAD.** **Expect** the theme's map exactly as before.
   **Expect** the rings (and a route, if one is drawn) to **stay on the map through every switch**.
   **A failure looks like:** a blank map on one base (the probe above for that one), or the range
   lines vanishing after a switch.
5. **Press NORTH UP.** **Expect** it to say **TRACK UP** and light up, and once the truck is moving
   above walking pace the map to turn so the way you are going is up, with the truck in the lower
   third. The compass needle on the button keeps pointing north. Stopped at a light, **expect** the
   map to hold still rather than spin. Press again: **expect** north back at the top at once.
6. **Pan the map.** **Expect RECENTRE** low on the left, clear of the small attribution text.
   Press it: the map goes back to the truck and the button goes.
7. **SEARCH on the map** **expect** to open the search exactly as the old chip did.
8. **A route past the range.** Search somewhere further than the range button says (on the
   simulator, a few hundred kilometres away) and press **DIRECTIONS TO HERE**. Zoom out along it.
   **Expect** the route solid in your accent until the orange point, then **orange dashes to the
   red ring, and red dashes beyond it**. A route shorter than the range minus 80 km stays solid.
9. **Change the size and send it to the stage and back** with satellite and track up on.
   **Expect** it to come back on satellite, track up, with its lines.

**In the truck.** The rings need the truck's own **distance to empty** (`fuel.range`), which is a
placeholder until its identifier is found. If the button says **NO RANGE** with the adapter
connected, run the ID matcher's **19 — Distance to empty**, or the hunter's **M** with the number the
cluster shows; once it is in your catalog the lines draw on the next launch. **Expect** the number
on the button to match the cluster's DTE, and **expect** the rings to be drawn grey if the adapter
stops answering — a stale range is never drawn as a live one.

### Typing an address while moving (ADR-0072)

1. **With the switch OFF (the default), drive above walking pace and tap SEARCH on the map.**
   **Expect** the field greyed out and *Stop first — a destination cannot be typed at N km/h.
   Settings ▸ Display ▸ TYPE WHILE MOVING lifts this.*
2. **Settings ▸ Display ▸ MAP SEARCH ▸ TYPE WHILE MOVING — ON.** Back to the map and SEARCH.
   **Expect** the field to take typing and SEARCH to find the address, moving. No restart.
3. **Turn it OFF again while the search is open and moving.** **Expect** the field to lock within a
   second.
4. **Close and reopen the dash.** **Expect** the switch where you left it.

At a desk, `--gps synthetic` moves at 60 km/h, so all four work without the truck.

## Screen brightness (ADR-0068)

**Added 2026-10-18, and never run on the Surface.** The rules are tested; whether Windows lets
DashDeck set this panel's backlight is what only the tablet can say. Steps 1–7 work at a desk.

**You need:** the Surface, not another PC — a desktop on an external monitor cannot be dimmed
this way and will say so. Note the brightness you have now (Action Center slider) before starting.

**First, turn off Windows' automatic brightness** for this test: Settings ▸ System ▸ Display ▸
Brightness ▸ untick *Change brightness automatically when lighting changes*. Step 8 turns it back
on to see whether the two fight.

1. **Launch DashDeck by day** with Appearance on DAY: `dotnet run --project src\DashDeck.Host`.
   **Expect:** the screen goes to full brightness within a second or two of the window appearing.
   **A failure looks like:** no change at all — open Settings ▸ DISPLAY and read the BRIGHTNESS box.
2. **Settings ▸ DISPLAY ▸ BRIGHTNESS.** **Expect:** *Backlight 100 %, the day level. N % goes back
   when DashDeck closes*, where N is the level you noted. If it says *can't be set from Windows*,
   stop and send me that line — the rest cannot work.
3. **Tap 60% under BY DAY.** **Expect:** the screen dims as you tap it, and the box says 60 %.
4. **Settings ▸ APPEARANCE ▸ NIGHT.** **Expect:** the palette goes to night *and* the backlight
   drops to 30 %, together. Tap 10% under BY NIGHT: darker at once, still readable.
5. **Move the Action Center slider to somewhere else and wait ten seconds.** **Expect:** DashDeck
   leaves it there. It should not snap back.
6. **Close DashDeck (menu ▸ CLOSE DASHDECK).** **Expect:** the slider you moved in step 5 stays
   where you moved it, because that was newer. Launch again, change nothing, close again:
   **expect** the level you had before launching to come back.
7. **Tap SET THE BACKLIGHT — ON so it reads OFF.** **Expect:** the level you had before DashDeck
   came back immediately, the day and night rows grey out, and the box says *Off*.
8. **Turn Windows' automatic brightness back on, ON in DashDeck, and cover the light sensor** (top
   bezel, beside the camera) with a thumb. **Expect** Windows to move it after a few seconds.
   **This is the question:** does the dash's level stay put once Windows moves it, or do they
   take turns? Tell me which — it decides whether the walkthrough's first step stays "turn it off".
9. **The crash case.** With it dimmed at night, end `DashDeck.Host` in Task Manager. **Expect:**
   the screen stays dim — nothing could run to put it back. Launch DashDeck, set Appearance to DAY,
   and close it normally. **Expect:** the level from *before the crash* comes back, not the dimmed
   one.

**In the cab, at night, parked:**

10. **Appearance on AUTO after sunset.** **Expect:** dim on launch, without touching anything.
11. **Put the map on, then Android Auto.** **Expect:** both as dim as the cards. This is the reason
    for the backlight over a dark layer: neither of them would have dimmed otherwise.
12. **Is 30 % right?** Step through BY NIGHT until the dash is readable and not a torch. Tell me the
    number, and I'll make it the default.

**A failure looks like:** the box saying *Windows refused to set the backlight*, or the level
changing and then jumping back on its own with automatic brightness off. Send the box's line and
whether automatic brightness was on.

## When the dash will not open

Added 2026-10-15, after a launch hung with no window and no log, and then — because it was still
running and holding the single-instance lock — made every launch after it fail silently too.

**First, the one command:**

```
powershell -ExecutionPolicy Bypass -File diagnose.ps1
```

It reads and changes nothing, and prints the four things that answer this: any DashDeck still
running, the startup trail, the end of the crash log, and the ports and settings that decide what
startup touches.

**What to look for:**

1. **A DashDeck process with `MainWindowHandle` of 0.** That is a stuck launch, and it is why
   nothing else will start. `Stop-Process -Name DashDeck.Host -Force`, then launch again.
2. **The last line of the startup trail** (`%LOCALAPPDATA%\DashDeck\startup.log`, rewritten every
   launch). It names the phase that never finished — claiming the lock, reading settings, preparing
   the adapter on a port, loading components, building the window. **That line is the diagnosis;
   everything before it finished.**
3. **`startup gave up after 45 s, still in: …`** in the trail or the crash log means the watchdog
   ended it rather than letting it sit there. The phase it names is where to look.
4. **A message saying another DashDeck is running and did not let go.** The gate used to exit
   silently after twenty seconds; it now says this, and the fix is step 1.

**To narrow it by hand**, these three launches separate the usual suspects:

```
dotnet run --project src\DashDeck.Host -- --port ""        the synthetic truck, no adapter search
dotnet run --project src\DashDeck.Host -- --port "" --gps ""   neither adapter nor phone GPS
dotnet run --project src\DashDeck.Host                     as it normally starts
```

If the first two start and the third does not, it is the serial side — the adapter search, or the
phone's Bluetooth port. **Components are a separate case:** they are built by `publish.ps1`, not by
`dotnet run`, so a repository checkout can hold component DLLs built against an older DashDeck.
Those fail to load and are logged; they do not stop the dash.

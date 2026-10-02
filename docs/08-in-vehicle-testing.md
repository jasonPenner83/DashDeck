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
   holding `dashdeck.json` (every token written out), `lcars-inspired.json`, the Antonio `.ttf`
   files, `OFL-Antonio.txt` and `README.txt`.
1. Open **Settings ▸ Themes**. **Expect:** a list of **DASHDECK** (built in, marked **WEARING**)
   and **LCARS (INSPIRED)** (shipped), each with five colour swatches, and a token list below.
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
   `"quickBar": [ "MAPS", "GAUGES", "CLOCK" ],`. Save. Back in Settings ▸ Apps tap **RELOAD**.
   **Expect:** the line *Reloaded launcher.json: …* and **BAR** now beside MAPS, GAUGES and CLOCK.
   Go back to the dash. **Expect:** three buttons — **MAPS, GAUGES, CLOCK** — plus the nine-dot.
   Whatever was on the stage is still on it and still lit; if it was COMPASS, a fourth COMPASS
   button sits at the end.
6. **Add a web page with its own zoom.** In `entries`, after the MAPS line, add:
   `{ "name": "RADAR", "type": "web", "url": "https://weather.gc.ca/radar/", "zoom": 0.75, "group": "WEATHER" },`
   Save, **RELOAD**. Open the grid. **Expect:** a **WEATHER** heading with **RADAR** under it,
   detail `weather.gc.ca`. Tap it. **Expect:** the radar page on the stage, zoomed out further than
   MAPS, and **RADAR** in the status strip.
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
2. **Expect:** the cards go and the **Glass** panel takes their place — three frosted panels (DRIVER,
   CLIMATE, PASSENGER) over a long glass bar of switches. The stage above is untouched and keeps
   running. DRIVER reads **21.5°** and PASSENGER **22.0°** on thin blue arcs with a bright point;
   FAN shows some bars lit; FACE or FEET is lit; the switches show A/C lit when the synthetic
   outside air is warm; OUTSIDE shows a temperature. Every element has a small **blue** dot
   (Simulated). Under DRIVER: **SEAT** with its bars and a **WHEEL** pill. When the synthetic
   outside air is below 10 °C the driver's seat reads **HEAT 2** with two warm orange bars and
   WHEEL is lit orange; above 25 °C the seat reads **COOL 1** with one ice-blue bar and WHEEL is
   off; in between the seat reads **OFF**. The passenger seat reads **OFF**.
3. Tap anything on the panel. **Expect:** nothing happens. It is read only.
4. Tap **DASH**. **Expect:** the cards come back exactly as they were. Tap **CLIMATE** again: the
   panel is back.
5. **Settings ▸ Themes**, wear **LCARS (inspired)**, tap **CLIMATE**. **Expect:** the LCARS panel —
   lavender and peach elbows, ENVIRONMENTAL, orange arcs. Wear the DashDeck theme again: Glass is back.
6. **Settings ▸ Themes ▸ CLIMATE LAYOUT**: **Expect:** SHOWING GLASS, the reason *following the
   theme*, rows FOLLOW THE THEME, GLASS (BUILT IN) and LCARS (INSPIRED) CLIMATE (SHIPPED). Type
   `mine`, press **SAVE AS**: *Saved as mine.json in your climate folder*. **OPEN FOLDER**, open
   `mine.json` in Notepad, change the first `"x": 24` to `"x": 60`, save, press **RELOAD**, tap
   **CLIMATE**: the driver glass has moved right. Back in Settings, **DELETE** it: the panel follows
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

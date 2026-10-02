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

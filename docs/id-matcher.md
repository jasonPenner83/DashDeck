# The ID matcher

**Where DashDeck's identifiers are managed.** It's a desktop program for the laptop, used with a
keyboard and mouse. It shows:
- which DashDeck signals don't have the truck's identifier yet;
- everything FORScan asks the truck, through a tap built in;
- which of those answers matches the value FORScan shows, and how it scales.

Pair a match with a DashDeck signal and save it into the overlay the dash reads.

Why it exists and how it matches: [ADR-0050](decisions/ADR-0050-id-matcher.md). The touch screen
stays for driving. Identifiers are found here, the traditional way.

## Starting it

It's in `IdMatcher\` beside the dash. Copy the whole DashDeck folder to the laptop: the matcher
reads DashDeck's catalog from the folder above it.

1. **Close FORScan, DashDeck, the ID hunter and SerialTap.** Only one program can hold the adapter.
2. Double-click `IdMatcher\IdMatcher.exe`.
3. Top left:
   - pick the adapter's port;
   - leave 115200 baud and port 35000;
   - click **START TAP**.
4. In FORScan, go to **Settings ▸ Connection** and set:
   - type **WiFi**;
   - IP **127.0.0.1**;
   - port **35000**.

   Then connect. The middle list fills as FORScan talks to the truck.

Every session is also saved as a tap log in `%LOCALAPPDATA%\DashDeck\tap\`. **Open tap log**
(Ctrl+O) matches a saved session later, without the truck. SerialTap's logs open the same way.

## The window

| | |
|---|---|
| **Left: DashDeck signals** | Every signal the dash knows. **NEEDS ID** marks a placeholder: TPMS, the warning lights, economy and range, climate. **NOT ON TRUCK** marks a standard PID the truck says it doesn't have, read from its own answers when FORScan connects: fuel rate, MAF, oil temperature. Tick off *Only those that need the truck's ID* to see them all. **PACK** and **YOURS** are already paired. |
| **Middle: what FORScan asks** | One row per identifier: module, bus (HS or 3/11), mode, PID, the latest answer in hex and as numbers, how often heard and how often it changed. **Yellow**: FORScan just started asking it. **Blue**: its answer is changing. |
| **Right: LIVE** | Match one identifier by typing what FORScan shows. |
| **Right: PID LOG** | Match many at once from FORScan's PID log. |
| **Bottom: accepted** | What you accepted, and the DashDeck signal each fills. **SAVE TO DASHDECK** writes them to the overlay. **Export for the vehicle pack** copies them as pack entries. |

## Matching one value, live

1. On the left, click the DashDeck signal to pair, for example *Fuel Economy — Instant*. The right
   panel says **Pairing with fuel.economy**.
2. In FORScan, open **one** value in live data, the one that corresponds.
3. In the matcher, press **Ctrl+N**. It selects the newest identifier, the yellow one FORScan just
   started asking.
4. Press **F3**, type the number FORScan shows, and press **Enter**. Check the unit beside the box
   matches FORScan's (°F, psi, mph…).
5. Make the value change (drive, warm up, open a door), and type it again each time it moves. The
   **scalings that fit** list shrinks as coincidences drop out. Three different raw values usually
   settle it.
6. Press **Ctrl+Enter** to accept the top one. It appears at the bottom as *→ fuel.economy*.

Without a DashDeck signal selected, a match becomes a new signal named after FORScan's value.
**Esc** stops pairing.

## Matching many, from FORScan's PID log

1. With the tap running, open several values in FORScan and start its **PID log**.
2. Make them change, then stop the log.
3. In **PID LOG**, click **Import FORScan PID log** (Ctrl+I) and pick the CSV. The matcher:
   - lines the log up with the traffic;
   - fits every column against every identifier;
   - ticks the matches that fit almost perfectly (R² ≥ 0.98).
4. Check the ticks, then click **ACCEPT TICKED**.
5. To pair one with a DashDeck signal, select it at the bottom, select the signal on the left, and
   press **Ctrl+P**.

A column that never moved can't be matched, because a constant fits anything. Make each value
change while logging.

## Adding, editing, hiding and removing signals

Under the left list (ADR-0051):

| | |
|---|---|
| **NEW…** | A new DashDeck signal. With an accepted match selected at the bottom, it starts from that match. It is measured, so it stays confirmed if you keep its request and scaling: name it, give it a category and range, save. With nothing selected, you type it by hand: module, mode, PID, bytes and scaling. A typed signal is saved **UNCONFIRMED**: the dash won't offer it in the card editor until **TEST** in Settings ▸ Sensors on the tablet answers it, and you save it from there. |
| **EDIT…** | Change any signal. Changing its name, category, range, rate or unit label keeps it confirmed. Changing where it comes from or how it decodes makes it unconfirmed until TEST. Editing a built-in saves your correction over it. |
| **HIDE / UNHIDE** | Takes a signal out of the card editor's picker and this list, without breaking anything that already uses it. Tick **Show hidden** to see hidden ones and bring them back. |
| **REMOVE** | For signals you added: gone, after a confirmation (cards using one show NO DATA). For a built-in you corrected, it says **REVERT TO BUILT-IN** and puts the original back. A plain built-in can't be removed, only hidden: screens and components may rely on it. |

Every change is written straight to `signals.user.json` (the path at the bottom). DashDeck reads it at
its next launch.

## Saving

- **SAVE TO DASHDECK** (Ctrl+S) writes the accepted matches into `signals.user.json`. The path is
  at the bottom: `%LOCALAPPDATA%\DashDeck\` on this machine. A paired signal keeps its DashDeck id,
  name, range and rate, and takes the truck's module, mode, PID and decode. It stays in the
  signal's own unit: a match in kPa fills a signal kept in psi. Run the matcher on the tablet (with
  a keyboard), or copy the file there. The dash reads it at its next launch. **Confirm each with
  TEST** in Settings ▸ Sensors.
- **Export for the vehicle pack** (Ctrl+E) puts the same as pack entries on the clipboard, and saves
  them in `%LOCALAPPDATA%\DashDeck\matches\`. Send that file. Confirmed entries go into the F-150
  pack by PR, so every tablet gets them.

## Keys

| | |
|---|---|
| Ctrl+N | Select the newest identifier |
| F2 / F3 | Jump to FORScan's name / the value it shows |
| Enter (in the value box) | Add the value as a sample |
| Ctrl+L | Clear the samples |
| Ctrl+Enter | Accept the selected scaling |
| Esc | Stop pairing |
| Ctrl+P | Pair the selected accepted match with the selected DashDeck signal |
| Ctrl+I | Import a PID log |
| Ctrl+O | Open a saved tap log |
| Ctrl+S | Save to DashDeck |
| Ctrl+E | Export for the vehicle pack |

## Notes

- Tap logs and exports can hold the **VIN**: FORScan reads it as it connects. They stay on your
  machines and never go in the repo.
- The matcher sends nothing of its own to the truck. Everything the truck sees is FORScan's.
- A FORScan value worked out from several identifiers can't be matched to one. It shows as
  unmatched.

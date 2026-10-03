# The ID hunter

A separate program that **guides you through finding where the truck keeps a value**: which frame
carries the door switch, which identifier is the transmission temperature, which one the cluster's
distance to empty comes from. It reads only. Why it exists and how it ranks:
[ADR-0044](decisions/ADR-0044-id-hunter.md).

It writes a `findings.csv` for you to send back. Confirmed values go into the F-150 vehicle pack by a
PR, with the evidence.

## Starting it

It lives beside the dash, in `IdHunter\` in the DashDeck folder.

1. **Close DashDeck** (three-dot menu ▸ CLOSE DASHDECK, two taps) and FORScan. Only one program can
   hold the adapter.
2. Double-click `IdHunter\IdHunter.exe`. A terminal window opens and finds the adapter **the way the
   dash does**:
   - it starts from the port, rate and identity in DashDeck's own `settings.json`;
   - then it tries every other port at every rate the dash would;
   - it never opens the phone-GPS port.

   It prints a line per port as it goes, then shows the checklist. If nothing answers, it says what to
   check and waits for Enter, so you can read why. Send that screen if you need help.

**First, it checks the bus on OBD pins 3 and 11.** Older Fords have a 125 kbit/s MS-CAN there; newer
trucks have a 500 kbit/s bus. Sending at the wrong speed puts error frames on that bus. So the hunter
listens silently at each speed first and uses whichever one it hears:

> Pins 3/11 carry a 500 kbit/s bus. The guide will use it at that speed.

If it hears nothing at either speed, it uses HS-CAN only and sends nothing on pins 3/11.

**A busy bus is heard in bursts.** HS-CAN carries far more than the adapter's USB link can pass on.
The adapter fills its buffer, stops, and is started again straight away. You will see *heard in N
bursts*. That is enough for anything a module sends every second or faster.

To learn it at a desk without the truck: open a terminal in that folder and run
`IdHunter.exe --simulate`. Everything works against the synthetic truck, whose identifiers are
invented. Other options: `--port COM5`, `--out <folder>`, `--targets <file>`.

Output goes to `%LOCALAPPDATA%\DashDeck\hunt\hunt-<date>-<time>\`. The program prints the path when you
quit.

## The checklist

```
  #   WHAT TO FIND                    HOW      FOUND
  1   Driver door                     listen   —
  …
  15  Engine oil temperature          follow   —
  …
  19  Distance to empty               match    —
  S   Scan for modules
  L   Listen freely — name your own action
  A   Ask a module while you do something
  D   Check a CAN database against the truck
  Q   Quit
```

Type a number and press Enter. Each target says first what it **needs**: ignition on or engine
running, parked, a cold engine. **Everything is refused while the truck is moving.**

### listen — doors, belts, switches, buttons

The guide asks for five steps, for example door shut, open, shut, open, shut. For each one:

1. Do what it says.
2. Press Enter.
3. Keep still while it counts down.

It listens to MS-CAN, ranks what changed with you and only with you, and shows a table:

```
  #  FRAME     FIELD                 EACH STEP              SCORE
  1  3B3       byte 0 bit 0          0 1 0 1 0               1.00
```

**EACH STEP** is what that field read in each of your five steps. **TELLS APART** says whether it
told every step apart, or only some — for example "3 of 6 pairs" for a field that knows the seat is
on but not at which level. Both OFF steps must always read the same, which is what keeps chance out. Type its number to **check it
live**: do the thing again a few times and watch the value change, then answer **y** if it followed
you. If nothing follows on MS-CAN, it offers HS-CAN.

### follow — temperatures, flow, pressure

1. **Pick a module.** It suggests one (`7E0`, the engine computer); press Enter to take it.
2. **Pick identifier ranges.** Press Enter for the suggested range.
3. **Wait for the sweep** — about four minutes for `1000-1FFF`.
4. **Make the reference change.** It now reads every identifier it found, over and over, beside a
   standard reading you make change:
   - **Temperatures:** start from a **cold** engine (overnight) and let it idle. The guide compares
     against coolant as it warms. Up to 12 minutes; press Enter to stop early.
   - **Fuel flow and oil pressure:** on the cue **REV**, press the accelerator gently to about
     2,000 rpm for two seconds and let it fall back. Never above 3,000.

It then ranks what moved with the reference:

```
  #  ID     READ     r       COOLANT ≈ a × value + b      LOOKS LIKE
  1  1310   u8@0      0.99   a=1        b=-40.1          value − 40
```

**r** near 1 rose with it; near −1 fell as it rose. **READ** says which bytes: `u8@0` is the first
byte, `u16@0` the first two, `s16` signed. Pick a number to read it live. If FORScan is on a laptop
beside you showing the same value, compare the two, then answer y, n or ?.

### match — numbers on the cluster

1. Sweep as for follow. The suggested module is the cluster, `720`, or the body module, `726`, for
   tyres.
2. Type the number exactly as the cluster shows it: `412`, `13.4`, `35.5`.
3. If it asks for a second round, come back after the number has changed — after a drive, for distance
   to empty. It keeps the sweep, so a second round takes seconds.

A single reading matches many identifiers by chance. Two readings, or four tyres at once, make the
right one rise. **DECODE** says how the raw number becomes what you typed (`÷ 4`, `km→mi`).

### When a listen finds nothing: ask the module

Not everything is broadcast. Some values stay inside the module that runs them. Seat heating and
cooling, for example, belong to the seat climate module, which FORScan calls **SCME**. A value like
that has to be asked for.

When a listen confirms nothing, the guide offers to **ask a module instead**:

1. Type the module's address. Match FORScan's module by its part number against the list from **S**.
2. Pick the identifier ranges to sweep. The default is `0000-0FFF`.
3. Do the same steps again.

It asks every identifier it found during each step, and ranks them the same way a listen does. The
table shows the **ID** in place of the frame.

**A** on the main menu does the same for anything you name.

### D — check a CAN database

A CAN database (a `.dbc` or `.dbcx` file) says which frame carries which value: for example,
*message 156, bits 15–8, minus 60, is engine oil temperature*. You may find one written for a truck
like yours. Treat it as a list of leads, because it may be for another model year
([ADR-0045](decisions/ADR-0045-can-databases-as-leads.md)). **D** checks it:

1. **Give it the file.** Drag it into the window, or put it in `%LOCALAPPDATA%\DashDeck\dbc\` and press
   Enter. You can also start with `IdHunter --dbc <file>`.
2. **Press Enter** to hear which of its messages your truck sends: about five seconds per bus. You get
   a count, such as *61 of the file's 329 messages are on this truck*.
3. **Search** for signals, or press Enter for DashDeck's list (oil, gearbox, fuel, tyre, door, odometer,
   heated wheel, seat, HVAC, range). Each row says where it was heard: **HS**, **pins 3/11**, **not
   heard**, or **CAN FD only**. This adapter cannot hear CAN FD.
4. **Type the numbers to check** (`3` or `3,5,9`). The guide listens for that message alone and prints
   the decoded values whenever they change: `EngOil_Te_Actl = 87 degC`. Make them change, then compare
   with FORScan or the cluster. Press Enter, then answer y, n or ? for each signal.

Each answer is a row in `findings.csv` with method `dbc`, the bit layout, the scaling, and the range
it read. A message not heard on either bus is recorded as `absent`.

**Keep the file on the tablet.** Do not add a database to the repository. Only what the truck confirms
is kept.

### S — scan for modules

Asks every module address on both buses for its part number, and lists what answered with a likely
name. Do it once; follow and match then know which bus each module is on.

### L — listen freely

For anything not on the checklist: name what you will do (`hazard lights on`). The guide runs the same
five steps.

## What to send

`findings.csv`, one row per candidate:

| column | means |
|---|---|
| `method` | listen, follow or match |
| `bus`, `module`, `frame_id`, `did` | where it is |
| `field`, `reading` | which bits or bytes |
| `hint`, `evidence` | the scaling it looked like, and the numbers behind the ranking |
| `verdict` | `confirmed`, `rejected` or `unsure` from your live check; `unchecked` if you did not check it |

The `listen-*`, `follow-*`, `match-*` and `sweep-*` files beside it are the raw captures. Send them
too if you can. **They may contain the VIN**, so send them to a person and never commit them to the
repository.

## Changing the checklist

`IdHunter\targets.json` is plain JSON:

- **Add a listen target:** a list of `steps`, each with a `label`, a `state` number (steps that should
  read alike share one) and the instruction in `do`.
- **follow and match targets:** name `modules` and `ranges` to search. These are where to *look*,
  not claims about where anything is.

A target with a mistake is skipped and named when the program starts.

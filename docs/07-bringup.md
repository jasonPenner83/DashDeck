# DashDeck — Hardware Bring-Up (P1.5)

Getting the OBDLink EX from "in a box" to "feeding the real signal pipeline."

It runs in **two stages**, because they fail for completely different reasons and mixing
them makes a failure hard to read:

| Stage | What it clears | Needs |
|---|---|---|
| **1 — adapter** | FTDI driver, COM port, baud rate, command protocol, adapter identity, ST command set, MS-CAN capability | Adapter on USB. **No vehicle.** |
| **2 — vehicle** | Which PIDs this truck answers, the real throughput ceiling, live data, a replayable capture | The truck, ignition on |

Stage 2 reporting "skipped, no vehicle bus found" is a **normal result**, not a failure.

## Stage 1 — on a desk

Plug the EX into the laptop. It powers from USB, so it comes up and talks without a
vehicle attached.

```bash
dotnet run --project src/DashDeck.BringUp
```

It finds the adapter itself by trying each serial port, or pass `--port COM4` if you
already know. It prints every command and reply, then a summary.

**What good looks like:**

```
  identity        STN2230 v5.6.6
  device          OBDLink EX r1.0
  STN firmware    STN2230 v5.6.6
  ST commands     yes
  MS-CAN switch   accepted
  OBD voltage     0.2V
  vehicle         NotDetected
```

**The line that matters most is `ST commands`.** A genuine OBDLink answers `STI`; an
ELM327 clone replies `?`. A clone cannot switch to MS-CAN, which would make TPMS, door
state and drivetrain mode permanently unreachable (ADR-0007). If this says `NO` on
hardware sold as an OBDLink EX, that is worth a return rather than a workaround.

**`OBD voltage` reads near zero here, and that is correct.** It measures OBD-II pin 16 —
*vehicle* power, not USB power. It usefully separates three situations that otherwise look
identical:

| Reading | Means |
|---|---|
| ~0 V | Not plugged into a vehicle at all |
| ~12 V | In the vehicle, ignition off |
| ~13.5–14.5 V | Engine running, alternator charging |

### If stage 1 fails

| Symptom | Cause |
|---|---|
| No serial ports listed | Adapter not plugged in, or the FTDI driver never installed. A **red LED on the adapter means the driver did not take.** |
| Port opens, nothing answers | Wrong port, or wrong baud. The EX ships at 115200; try `--baud 9600`. |
| "Access denied" / port busy | FORScan or OBDwiz is holding it. Close them. |
| `ST commands: NO` | Not a genuine STN adapter. See above. |

## Stage 2 — in the truck

**Park it first.** Engine running, transmission in park, parking brake on. Do not drive
while running this — it wants a laptop open and your attention on the output, and that is
not compatible with operating a vehicle. A passenger can run it while you drive later, once
you want data under load.

Plug the EX into the OBD-II port under the driver's side dash, then:

```bash
dotnet run --project src/DashDeck.BringUp
```

It will now continue past stage 1 and do four things.

**1. Ask the truck which PIDs it supports.** Mode 01 PID `0x00` returns a bitmap of
supported PIDs, and each range's last bit says whether the next range exists, so the scan
walks the chain. This replaces guesswork with the vehicle's own answer — and settles
**open question Q4**: whether this truck supports the fuel-rate PID `0x5E`, or whether
economy has to be derived from MAF.

**2. Check catalog coverage.** Every signal definition is cross-referenced against what the
truck actually answers, so we learn up front which signals will never produce data here
rather than discovering it as a blank tile while driving.

**3. Measure the real throughput ceiling** — **open question Q12**. Sixty back-to-back
requests, reported as mean, min and p95 round-trip, with the sustainable rate derived from
mean service time.

This is the number that decides whether live gauges are viable. Service time is measured
rather than achieved requests-per-second, deliberately: achieved rate cannot distinguish
"the adapter could not go faster" from "nothing needed polling," and using it as a
capability measure creates a feedback death spiral. That mistake has already been made
once in this codebase and fixed; the comment in `VehicleService.RecordServiceTime` explains
it.

**4. Record a replayable capture.** Every exchange is written to
`captures/bringup-<timestamp>.jsonl`, alongside a markdown report with the full command
transcript.

## Running the app itself on the adapter

Bring-up proves the pipeline; the shell is wired separately (ADR-0031). Put the adapter's
COM port in **Settings → Vehicle → OBD-II adapter**, then relaunch. Leave it empty to run
the synthetic truck.

The **SIM badge in the status strip is the thing to watch**: it is bound to whether the
data is simulated, so its absence is what tells you the dash is live. If a configured
adapter does not come up, the dash comes up simulated rather than dead and records why —
so a missing badge is the only confirmation that the numbers are real.

For a one-off without changing settings: `DashDeck.Host.exe --port COM7`.

## After the run

Commit both files:

```bash
git add captures/bringup-*.jsonl captures/bringup-*.md
git commit -m "captures: first bring-up on the 2019 F-150"
```

The capture is the valuable artefact. It means every later change can be developed and
tested against **real truck data** with no truck present:

```bash
dotnet run --project src/DashDeck.DebugConsole -- --replay captures/bringup-<timestamp>.jsonl
```

That is the point at which mock-first development graduates: the synthetic drives stay on
as regression fixtures, and real captures become the reference.

## Then update these from measurement, not assumption

| Where | Change |
|---|---|
| `ElmAdapter.AssumedRequestsPerSecond` | Set from the measured ceiling. It currently holds a deliberately pessimistic Bluetooth-era figure. |
| `SyntheticFaults.Realistic` | Set `LatencyMs` from measured mean service time, so the simulator keeps matching the truck. |
| `docs/04-open-questions.md` | Close Q4 and Q12 with the measured answers. |
| `catalog/` | Record which PIDs this truck does not support, and start a Ford-specific file for ones discovered beyond the standard set. |

**Keep the simulator pessimistic until the measurement says otherwise.** A simulator with
more headroom than the truck produces components that only fail on the road.

## Testing the tool without hardware

Both stages can be exercised with no adapter at all, which is how the tool itself was
developed and verified:

```bash
# A healthy adapter on a desk with no vehicle — stage 1 only
dotnet run --project src/DashDeck.BringUp -- --simulate-bench

# A full run against the synthetic truck — both stages
dotnet run --project src/DashDeck.BringUp -- --simulate
```

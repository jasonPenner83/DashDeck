# DashDeck — Open Questions

Live list. Resolved items move to an ADR in [`decisions/`](decisions/).

## Blocking nothing right now (mock-data phase)

| # | Question | Notes |
|---|----------|-------|
| Q1 | Buy the OBDLink MX+, or the wired EX? | Recommendation and reasoning in [`02-hardware.md`](02-hardware.md). MX+ unless you commit to the dock. Not urgent — P0 and P1 need no hardware. |
| Q2 | Is the Surface dock scenario real enough to build `UsbSerialTransport` early? | If it is a maybe, we build Bluetooth first and leave the interface in place. |
| Q3 | Mount hardware and power in the cab. | Affects nothing in software, but decides portrait geometry and whether the tablet charges while docked. |
| Q4 | Does this truck support the fuel-rate PID (0x5E)? | Unanswerable until an adapter exists. MAF fallback plus tank calibration is designed to cover either answer (risk R5). |
| Q5 | How much does the Gateway Module filter at the OBD-II port? | Measured on first bring-up. Determines whether MS-CAN signals are reachable without a behind-dash tap (risk R4). |

## Product questions for you

| # | Question | Notes |
|---|----------|-------|
| Q6 | After the trip computer, which component do you actually want next? | The outline assumes gauges as the SDK-proving second component, but that is a placeholder, not a decision. |
| Q7 | Should trip history stay strictly local, or sync somewhere? | Local-only is the default and the simplest. Worth deciding before the storage schema sets. |
| Q8 | Home Assistant integration — is that a real want? | You have a Home Assistant setup. "Truck is home / truck is warming up / fuel is low" as HA entities would be a natural background-worker component. Cheap to build, easy to skip. |
| Q9 | Dark-only, or light and dark themes? | A dash used at night is dark-only in practice. Light mode is real work for a case that may never occur. |
| Q10 | Does DashDeck ever need to run without the truck present — reviewing trips at your desk? | If yes, the simulator becomes a permanent product feature rather than temporary scaffolding, and "desk mode" is a real UI state. |

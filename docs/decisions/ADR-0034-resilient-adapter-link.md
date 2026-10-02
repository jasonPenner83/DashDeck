# ADR-0034 — A self-healing adapter link, a late adapter goes live, and a list of tested ports

**Status:** Accepted · 2026-10-02
**Amends:** [ADR-0031](ADR-0031-live-adapter-in-the-host.md) (falling back to the simulator is no
longer final). **Builds on:** ADR-0003 (transport/adapter split), ADR-0007 (USB adapter).

## Context

ADR-0031 put the real adapter in the shell, and the first days on the truck found where it was
brittle:

- **A reconnect was not a resume.** Unplugging the USB cable power-cycles the OBDLink, which comes
  back with echo on, spaces on, the protocol on automatic and the CAN transceiver on HS-CAN.
  `ElmAdapter` configured it once at launch and then believed its own settings — including which
  bus it had switched to — so after a knock the first MS-CAN request went out on the wrong bus.
- **The baud rate was found once.** FORScan runs this adapter at 2 Mbps; if a power cycle drops
  it back to 115200 while the port is open at 2 Mbps, every reply after is mojibake, forever.
- **Every launch tried every rate**, two seconds each, before the dash came up.
- **Falling back was final.** The tablet starts in the house and is carried to the truck. A dash
  that came up simulated because the adapter was not there stayed simulated until a restart.
- **COM numbers move.** Windows can give the same adapter a different number on a different USB
  socket, and the dash then fell back with "port not found" for an adapter that was plugged in.
- **The port was a text box** beside a list of COM names. "I set the port and it didn't work"
  could not be told apart from a wrong port, a busy port (FORScan) or a missing driver.

## Decision

**`AdapterLinkTransport`** wraps `SerialPortTransport` with what a pipe cannot do:

- It tries the **rate that worked last time first** (persisted), then the others, and keeps a
  rate only when the adapter answers `ATI` with an identity. A reply that turns to mojibake
  drops the link and finds the rate again.
- When the chosen port is missing or silent it **tries the other ports**, adopting one only if it
  is an adapter — and, once one has been seen, only **the same one** (by its `ATI` identity). The
  phone's Bluetooth GPS port is **reserved** and never opened.
- A missing adapter is looked for again after a **growing pause** (1, 2, 5, 10 s), never in a
  tight loop; requests in between fail fast, as a pulled cable should.
- While looking it sends **adapter commands only** (`ATZ`, `ATE0`, `ATI`) — nothing to the vehicle.

**`ElmAdapter` configures the adapter again after any reconnect** — any return to Connected after
the first initialisation — before its next request, including the bus probe.

**Simulated is no longer final.** The bottom of the pipeline is a **`SwitchableTransport`**. Launch
gives the chosen port a bounded try (12 s, that port only). If it does not answer, the dash comes
up simulated, as ADR-0031 decided, and an **`AdapterFailover`** keeps looking. When the adapter
answers it:

1. switches the transport, which makes `ElmAdapter` configure the new adapter;
2. stamps readings **Live** from then on;
3. has `VehicleService` **forget what the simulator taught it**: which signals answered, which
   were retired, and the measured service time.

**One way only.** Once live, a pulled cable is `ADAPTER LOST` and Stale values, never a quiet
return to made-up numbers. Each poll takes its quality *before* it asks, and the switch happens
before the flip to Live, so a simulated reply can never be stamped Live.

**Settings ▸ Vehicle lists tested ports.** Opening the section, or pressing TEST PORTS, opens each
port briefly in parallel and asks what is on it. Each row shows the adapter's identity, its baud
rate and the voltage at the OBD-II port, or else IN USE, NO ADAPTER, WON'T OPEN or PHONE GPS. The
port being read is shown as CONNECTED and not opened. Tapping a row chooses it. While simulated
that **applies at once**: the port is watched and the dash goes live when it answers. While live
on another port it applies at the next launch (RESTART NOW). The rate and identity the adapter
answers with are saved, and so is its new port if it moved.

## Reasoning

**The truck is the hostile environment, so recovery belongs in the link.** Every failure above
happens without anyone touching DashDeck: a knee on the cable, a cold start, a different USB
socket. Constraint C5 says recovery is a non-event; it can only be one if the link heals itself
and the layer above re-establishes its assumptions instead of trusting them.

**Switching the transport, not rebuilding the stack.** Everything above the transport — arbiter,
bus, cards, components — subscribes once at launch. ADR-0003 split the transport out so the
physical link could change underneath; switching it at runtime is the same property used live.
Rebuilding the pipeline would mean re-subscribing the whole shell.

**Identity before adoption.** A second ELM device on another port would otherwise be taken for
the truck's adapter. The `ATI` identity is the only thing that ties a COM number to a device
without registry reads or a driver API (constraint C1).

**Testing ports is a read of the adapter, not the vehicle.** `ATRV` reads the voltage at the
OBD-II port: near zero on a desk, about 12 V with the ignition off, 14 V running. That answers
"is it actually seated?" from the driver's seat, which is the question a list of COM names
never could.

## Alternatives

- **Restart to pick up a late adapter.** What ADR-0031 did; rejected — the tablet's normal day
  starts away from the truck.
- **Go live and wait instead of simulating.** A dash with no data on a desk is worse than a
  labelled simulated one (ADR-0005, ADR-0031); kept the fallback, removed its finality.
- **Identify the adapter by USB VID/PID/serial.** Needs WMI or the registry; the `ATI` identity
  is enough to tell one adapter from another for this purpose and needs neither.
- **Auto-test ports continuously.** Opening every COM port every few seconds would fight other
  programs for them; testing happens when the Vehicle section is open or asked.

## Consequences

- `settings.json` gains `adapterBaudRate` and `adapterIdentity`.
- The SIM badge and the fallback notice can change mid-session; the strip re-reads them on the
  clock beat.
- `VehicleStack.Synthetic` is null once live, so the unplug/replug diagnostics follow the
  current transport.
- **Not verified on the truck.** The engine tests drive every path against fake ports — wrong
  rate, renumbered port, a different adapter, a busy port, a pulled cable, configuring after a
  reconnect, simulator to truck. Serial ports, and the shell, still need the tablet in the cab.

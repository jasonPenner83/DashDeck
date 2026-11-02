# The serial tap

A small program that **records everything FORScan says to the adapter, and every answer**, to a text
file you can send back. It reads nothing from the truck itself and sends nothing of its own: it
passes FORScan's bytes through and writes them down. Why it works this way:
[ADR-0048](decisions/ADR-0048-serial-tap.md).

> **To match identifiers, use the [ID matcher](id-matcher.md)**: it has this tap built in, reads the
> traffic as it passes and pairs it with DashDeck's signals. SerialTap stays for plain recording.

## Why not a serial port monitor

Windows lets **one** program open a COM port. A monitor that watches another program's port has to
install a filter driver underneath it — that is what HHD, Portmon and the rest do, and why they fail
on current Windows or with FTDI's driver.

The tap avoids the problem. **It** opens the adapter's port, and offers it on a network port on the
same PC. FORScan connects to it **as if it were a Wi-Fi adapter**. Every byte goes through the tap,
so there is nothing to intercept and nothing to install.

## Using it

It lives beside the dash, in `SerialTap\` in the DashDeck folder. Copy that folder to the laptop if
FORScan is there; it needs nothing installed.

1. **Close FORScan, DashDeck and the ID hunter.** Plug in the adapter.
2. Double-click `SerialTap\SerialTap.exe`. It lists the serial ports; type the adapter's number (on the
   tablet it is the one Settings ▸ Vehicle shows, COM7 so far). Or start it with `--port COM7`.
   **Expect:**

   ```
   Holding COM7 at 115200 baud.
   In FORScan: Settings > Connection > type WiFi, IP 127.0.0.1, port 35000. Then connect.
   Recording to C:\Users\…\AppData\Local\DashDeck\tap\tap-20261004-091500.log
   ```

3. In FORScan: **Settings ▸ Connection**. Set the **connection type to WiFi**, the **IP address to
   `127.0.0.1`** and the **port to `35000`**. Save, then **Connect**.
4. Work in FORScan as usual. The tap window shows each line as it passes:

   ```
   09:15:02.114  --  connected: 127.0.0.1:52344
   09:15:02.120  >>  ATZ
   09:15:03.131  <<  ELM327 v1.4b
   09:15:03.132  <<  >
   09:15:04.410  >>  22F40C
   09:15:04.462  <<  62 F4 0C 1A F8
   ```

   `>>` is FORScan to the adapter, `<<` the adapter's answer, `--` a note from the tap.
5. When finished, disconnect in FORScan, then press **Ctrl+C** in the tap window. It prints how many
   lines it recorded and where the file is.

Each run makes a new `tap-<date>-<time>.log` in `%LOCALAPPDATA%\DashDeck\tap\`. Send that file.

**One capture per value.** For each value you want, such as transmission temperature:
- start the tap;
- open only that PID in FORScan's live data;
- make the value change: warm up, rev;
- stop the tap.

Keep FORScan's own PID log CSV of the same run. Send both and say which value it was. Matching the
timestamps shows which request carries the value and how it scales.

## Options

| | |
|---|---|
| `--port COMn` | The adapter's port. Without it, the tap asks. |
| `--baud n` | Its speed. 115200 (the OBDLink EX's) when left out. |
| `--listen n` | The network port FORScan connects to. 35000 when left out. |
| `--any` | Accept connections from other computers, not only this one. Then FORScan's IP is this PC's address. |
| `--bluetooth COMn` | Serve a phone app over Bluetooth instead of TCP, on Windows' **incoming** Bluetooth port COMn. See below. |
| `--out folder` | Where the logs go. |
| `--quiet` | Do not show every line in the window; just record them. |

## A phone app that talks to a Bluetooth adapter (Infocar, Torque, Car Scanner)

The phone pairs with this computer and the app opens it as it would a Bluetooth ELM327. The tap
stands between that and the real adapter on USB, and records everything as it does for FORScan.

1. **Pair the phone with this computer.** On the phone, open Bluetooth and pair with this computer
   by its name. Windows has to be discoverable: *Settings ▸ Bluetooth & devices*, with Bluetooth on.
2. **Give the phone a port to open.** *Settings ▸ Bluetooth & devices ▸ Devices ▸ More Bluetooth
   settings ▸ COM Ports ▸ Add… ▸ Incoming (device initiates the connection) ▸ OK.* Note the port it
   lists as **Incoming**, for example COM8. Remove it there when you are done.
3. Start the tap: `SerialTap\SerialTap.exe --port COM7 --bluetooth COM8` (COM7 the adapter, COM8 the
   incoming port).
4. In the app, choose a **Bluetooth** adapter and pick this computer from the list. Connect.

The phone connecting is not shown, because a serial port has no connections. The first `>>` lines
show the app has arrived. An answer the phone side cannot take (nobody is connected) is noted as
*did not take* and dropped, and the tap carries on.


- *Could not open COM7*: another program holds it — FORScan set to COM, DashDeck, the hunter. Close
  them.
- FORScan says it cannot connect: check the IP is `127.0.0.1` and the port matches the tap's.
- FORScan connects but finds no adapter, or the log shows `STBR` or `ATBRD` and then garbage: FORScan
  tried to change the adapter's speed. Send the log.
- The log can hold the **VIN** (FORScan reads it as it connects). Send it to me directly; never commit
  it to the repo.

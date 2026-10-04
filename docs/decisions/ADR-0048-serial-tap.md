# ADR-0048 — A serial tap that stands between FORScan and the adapter

**Status:** Accepted · 2026-10-04
**Serves:** R2, Q13 — finding Ford's own identifiers. **Keeps:** read-only (the tap only passes on what
another program sends); no new driver (CLAUDE.md, rule 2).

## Context

FORScan already reads the values DashDeck is hunting: transmission and oil temperature, oil pressure,
fuel flow. Recording what FORScan asks the adapter for would show the module, the identifier and,
next to FORScan's own log, the scaling. The plan was to capture that with a serial port monitor. The
owner could not find one that works.

That is no accident. Windows lets one program open a COM port. So a monitor that watches another
program's port has to install a kernel filter driver beneath it. Those drivers are what break: on
current Windows, with FTDI's driver, or because they are unsigned.

## Decision

**A separate console program, `SerialTap`, opens the adapter's port itself and offers it on a TCP
port on the same PC. FORScan connects to it as a Wi-Fi adapter.**

- **Passes bytes, never makes them.** Every byte from the program goes to the adapter unchanged, and
  every byte back goes to the program. Both are recorded. The tap sends nothing of its own, so
  everything on the truck is exactly what FORScan sent.
- **Logs lines, not chunks** (`TapRecorder`):
  - a command is a line at its carriage return;
  - an answer is its lines up to the `>` prompt;
  - each line is stamped with local time, to the millisecond, of its first byte, to line it up
    against FORScan's own log;
  - unprintable bytes are written as `\xNN`.
- **The port stays open between connections**, so FORScan can reconnect without the adapter resetting.
  One program at a time; a second is refused and noted.
- **Loopback only by default.** `--any` opens it to the local network, for FORScan on another
  machine.
- **Ends cleanly:**
  - an unplugged adapter stops the tap with a note;
  - Ctrl+C writes any partial lines and the totals.
- **Ships beside the dash** in `SerialTap\`, self-contained like the ID hunter, so it can be copied to
  a laptop.

## Consequences

- FORScan talks to the adapter through its ELM/STN text protocol over TCP, not its FTDI direct mode.
  The OBDLink accepts the same commands either way. Speed is lower, which a capture can afford.
- If FORScan sends a baud-rate change (`STBR`, `ATBRD`), the link breaks: the tap's side of the port
  stays at the old rate. FORScan does not do this on a Wi-Fi connection, which has no baud rate to
  change. If it ever does, the log shows it.
- A capture can hold the VIN. Logs go to `%LOCALAPPDATA%\DashDeck\tap\` and are never committed.
- Nothing reaches the dash. Whatever a capture shows still goes through TEST and the vehicle pack
  (ADR-0032, ADR-0033).

## Rejected

- **Writing a filter driver.** A kernel driver would be a third driver exception on a personal
  device, and the same thing that broke the monitors.
- **A virtual COM-port pair (com0com) with a relay.** It needs its own signed driver, and the signed
  builds are old.
- **A sniffer in the ID hunter.** The hunter talks to the adapter itself. The tap's whole point is to
  stay silent and let another program talk.

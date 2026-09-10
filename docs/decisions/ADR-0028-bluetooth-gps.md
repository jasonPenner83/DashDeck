# ADR-0028 — Bluetooth as the default GPS transport

**Status:** Accepted · 2026-09-09
**Extends:** [ADR-0027](ADR-0027-phone-location.md), which put the phone's GPS behind a
transport-agnostic seam and named Bluetooth as the next transport. This adds it, makes it the
default, and records why it does not count against C1's two-driver limit.

## Context

ADR-0027 shipped the phone GPS over **TCP** first because it was quickest to prove. In practice
the network path is the fiddly one: the phone and the Surface must share a Wi-Fi network, and the
phone's IP address has to be found and typed in. Bluetooth avoids all of that — pair once, and it
works whenever both are on — which is what a phone-in-a-truck actually wants day to day.

## Decision

**Bluetooth is added as a second transport behind the existing `ILocationSource` seam, and is the
default.** A `SerialNmeaLocationSource` reads NMEA from the paired phone's virtual COM port with a
plain `SerialPort`; the same `NmeaParser` and `PhoneLocationSensors` sit on top, unchanged. The
`GpsTransport` setting chooses `Bluetooth` (default) or `Network`; Settings shows a COM-port
picker for the former and the `host:port` field for the latter. Both transports stay — the
network path is kept for anyone who prefers Wi-Fi or a non-Bluetooth phone.

**This does not count as a third driver exception (C1).** Windows turns a paired Bluetooth SPP
device into a virtual COM port using its own **in-box, Microsoft-signed** serial-over-Bluetooth
stack. Nothing is installed. C1's two named exceptions (ADR-0007's FTDI, ADR-0019's WinUSB) are
about drivers *we require the user to install*; this requires none, so the count stays at two.

## Reasoning

**The seam is the reason this is small.** ADR-0027 built the location feed so the transport could
change without touching the parser, the provenance, or the compass. Bluetooth is a new
`ILocationSource` and a settings choice; the risky, shared code is untouched and already tested.

**Default to the one with less setup.** The network path needs a shared network and an IP; the
Bluetooth path needs a one-time pairing that Windows and Android both make routine. For a device
carried in and out of a truck, the pairing is done once and forgotten, while the IP can change
every time the phone rejoins a network. So Bluetooth is the better default, and the network path
remains for the cases it suits.

**No driver means no ADR-level erosion of C1.** The whole point of C1's "two, and a third needs
its own ADR" is to stop driver installs creeping in by accident. In-box serial-over-Bluetooth
installs nothing, so it is not the kind of thing that rule guards against — but because C1 is the
strictest constraint here, it is worth saying so on the record rather than leaving it implied.

## Alternatives

- **Bluetooth only, drop the network path.** Rejected: TCP is already built and proven, some
  phones or setups suit it better, and keeping both costs one settings choice.
- **A raw Bluetooth (RFCOMM/`Windows.Devices.Bluetooth`) implementation instead of a COM port.**
  Rejected: more code and more Windows-version surface for no benefit — the in-box virtual COM
  port is exactly what `SerialPort` is for, and it is the same thing every NMEA tool expects.
- **Auto-detect the phone's COM port.** Deferred: several COM ports can exist and guessing wrong
  is worse than a one-tap pick. The picker lists what Windows has and refreshes on demand.

## Consequences

- **A `System.IO.Ports` dependency** joins the Host's packages — used only by this source.
- **The user pairs the phone once in Windows Bluetooth settings**, then picks the COM port it
  appears as (Settings ▸ Display ▸ Location ▸ Refresh). Applied at the next launch, like the rest
  of the GPS setting.
- **Verification is on glass.** The serial read loop and reconnect are I/O and cannot be unit
  tested without a virtual port; the parser and the moving-heading rule already are (ADR-0027).
  That a real phone's SPP stream parses, and that unpairing ages the fix rather than freezing it,
  is a truck-seat check.
- **Range is a cab, which is fine.** Bluetooth SPP reaches well past the length of a truck; out of
  range degrades to Unavailable and reconnects, the same as a dropped TCP link.

# ADR-0057 — Android Auto on the stage: the dongle's USB, its picture and its sound

**Status:** Accepted · 2026-10-08
**Completes:** [ADR-0019](ADR-0019-phone-projection.md) — the USB transport, the decode and the audio
it left as gaps.
**Resolves:** F21 (where projected audio comes out).
**Relates to:** [ADR-0007](ADR-0007-usb-link-and-adapter.md) (driver exceptions),
[ADR-0021](ADR-0021-owned-not-reparented.md) (child windows over the stage),
[ADR-0026](ADR-0026-persistent-source.md) (a source that keeps playing).

## Context

ADR-0019 chose a Carlinkit CPC200 to terminate Android Auto and CarPlay and built everything above
the transport against a synthetic dongle. Three things were left: reading the device over USB,
decoding its H.264 into the stage, and deciding where its sound goes.

The owner asked for Android Auto **without the phone** if possible, and was interested in Android
Automotive. Neither changes this decision:

- **Android Auto needs a phone.** It is projection: the apps run on the phone and the head unit draws
  what it is sent. There is no phone-less Android Auto.
- **Android Automotive with Google's apps is licensed only to car makers.** What can run on a PC is
  the Android Emulator's Automotive image (heavy, a developer tool) or an AOSP build with no Play
  Store. That is a separate experiment and not this one.
- **The owner's Raspberry Pi Zero 2 W cannot be the head unit**: 512 MB and no hardware H.264
  encoder worth the name, and it is giving SYNC 3 wireless Android Auto today. It stays there
  (ADR-0019's reasoning, unchanged).

So the dongle is bought, and this ADR is about speaking to it.

## Decision

**USB through WinUSB, called directly.** `UsbDongleTransport` finds the dongle by vendor and product
(`1314:1520` or `1521`) with SetupAPI, reads the interface GUID WinUSB was given from the device's key
(read-only), opens it with `WinUsb_Initialize`, and takes the first bulk IN and OUT pipes from the
device's own interface descriptor — no endpoint numbers guessed. The binding is made **once, by
hand, with Zadig**: the second driver exception ADR-0019 admitted, Microsoft's own WinUSB, one
device. No USB library, no service, no registry write. A read thread asks for exactly what the next
message needs (`DongleFrameReader.Needed`); `DongleFrameReader` reassembles reads that cut messages
anywhere and resynchronises — counting what it skips — if the stream ever stops making sense.

**The whole setup is sent, in the community driver's order** (`DongleSetup`, numbers from the MIT
`node-carplay`): screen density, the Open with the stage's size (912 × 636), night mode, driving side,
charging, the box's name, the settings JSON with Android Auto's picture size, Wi-Fi on and its band,
the dongle's own microphone, audio sent to the host — and **`/etc/android_work_mode` = 1**, without
which a CarPlay-first dongle never offers Android Auto. A second later the dongle is asked to
reconnect the last phone it knew, so the dash connects without anyone touching the phone. Then the
heartbeat, every two seconds.

**The picture is decoded into the visual tree.** LibVLC — already shipped for VIDEO — reads the raw
H.264 from an `H264Pipe` with no caching and hands each frame to memory callbacks, which write a
`WriteableBitmap` on the dispatcher. Nothing native draws over the stage, so the three-dot menu, the
warning strip and the stage clip all behave. The decoder starts on the first frame (LibVLC is not
loaded for a dongle with no phone), and a decoder that falls 4 MB behind loses the backlog rather
than showing the past. The picture is scaled uniformly and centred; **touch is mapped to the picture
as drawn**, so a phone that renders its own size still lines up under a finger.

**Sound goes to Windows' default output** (F21). The dongle sends PCM (audio type and one of seven
formats); `ProjectionAudio` opens a `waveOut` player per format on the default device and lets Windows
mix them, so directions speak over music. **DashDeck never chooses the truck's audio** (C3): whatever
Windows outputs to — the tablet's speakers, a Bluetooth link to the truck's stereo, an AUX cable — is
the person's choice in Windows. A player more than 32 buffers behind drops them: directions spoken
after the turn are wrong.

**The phone kind is shown**: ANDROID AUTO, CARPLAY. With no dongle the screen says why — not
plugged in, or not bound to WinUSB — and looks again every five seconds, so plugging it in is enough.
`--synthetic-dongle` keeps the desk path.

## Reasoning

- **WinUSB by P/Invoke** is two dozen declarations against a library (LibUsbDotNet, a libusb build)
  that would bring its own driver story. The driver is the in-box one ADR-0019 already admitted.
- **Memory callbacks cost a copy per frame** (912 × 636 × 4 at 30 fps, about 70 MB/s) and lose
  hardware decode. That is the price of the picture living in WPF; a child window is what ADR-0021
  and the launcher bar's history say not to do again.
- **Windows' default output** is the only answer that respects C3 and lets the person decide. A
  dash that picked a Bluetooth device would be taking over the truck's audio by another name.

## Alternatives

- **libusb / LibUsbDotNet** — rejected above.
- **LibVLC drawing into its own window (`VideoView`)** — cheaper per frame, and a native child window
  over the stage, the thing this project has removed twice.
- **Leaving sound on the phone's Bluetooth to SYNC 3** (`AudioTransferOn`) — possible, and then SYNC 3
  has to be on the phone's Bluetooth source while DashDeck shows the picture. Kept as a fallback, not
  the default, because it splits one session across two head units.
- **Android Automotive now** — a separate experiment (an emulator or AOSP image hosted as an app
  occupant), noted in the open questions.

## Consequences

- **Unverified until the dongle arrives.** Every number in the setup is community reverse engineering;
  the protocol tests pin the bytes, not the dongle's opinion of them. The walkthrough is the test.
- **The Pi and the dongle both offer the phone a head unit.** The phone connects to one. Which one
  wins on a given drive depends on the phone; the guide says how to choose.
- **Microphone** is the dongle's own. A call works if the dongle has one; if not, that is the next
  change (the tablet's microphone, sent as `Mic`).
- **No hardware decode.** If the Surface struggles, the first fix is a lower frame rate in
  `DongleSetup`, not a child window.

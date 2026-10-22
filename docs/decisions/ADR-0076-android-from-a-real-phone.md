# ADR-0076 — Android on the stage from a real phone, mirrored with scrcpy

**Status:** Proposed · 2026-10-22. Built into the dash and tried with the owner's Pixel XL by cable,
"way better" than the emulator. The emulator was then removed (ADR-0075's last addendum). Wireless
added for the owner's Pixel 8, which will live in the truck when it is replaced.
**Builds on:** [ADR-0075](ADR-0075-android-on-the-stage.md) (the ANDROID cell, its hold menu, the key
bar, Android fitted to its region), [ADR-0057](ADR-0057-android-auto-through-the-dongle.md) (H.264
decoded into the visual tree), [ADR-0021](ADR-0021-owned-not-reparented.md) (nothing native on the
stage).
**Takes up:** ADR-0075's named fallback, *a real second Android device mirrored over ADB*.
**Supersedes:** ADR-0075's emulator.
**Keeps:** C1 (no drivers), read-only towards the vehicle.

## Context

The emulator works, but on the Surface Pro 7 it was measured at its limit: while scrolling, 16 frames
a second from the emulator and 9 drawn by the dash, the processor over 85 % and the GPU under 10 %.
That held even after the GPU was asked for, host composition turned off, Windows' throttling opted
out of, the cores tuned and the screen made smaller (ADR-0075's addenda). The emulator translates
Android's drawing, reads every frame back, packs it and sends it; the dash unpacks and copies it. All
of it lands on a four-core processor that also runs the dash.

The owner tried scrcpy with an old phone beside the dash and found it "way better already": the phone
does all of Android's drawing on its own GPU, and the tablet only plays video.

## Decision (proposed)

1. **The ANDROID cell mirrors a phone.** Tap for full screen, double tap for the stage, hold for its
   menu, as with the emulator (ADR-0075):
   - **ANDROID PHONE**, saying how it is reached (*by USB*, *over Wi-Fi*) and how the mirror is doing.
     A tap connects again;
   - **CONNECT WIRELESSLY** (decision 8);
   - **PICTURE**: SHARP, BALANCED or FAST, 1920 on the long side, three quarters or half;
   - **STOP ANDROID**.

   The key bar gives Back, Home and Recent apps. The mirror starts on first sight and keeps running.
   While nothing shows it, its video is dropped rather than decoded.

2. **The owner's scrcpy, used as its own client uses it.** DashDeck ships no part of scrcpy. It
   finds the install (`ScrcpyInstall`):
   - the settings file's `androidScrcpy` folder;
   - `SCRCPY_SERVER_PATH` and `PATH`;
   - winget, scoop and Chocolatey;
   - a folder unzipped into Downloads, the Desktop, the home folder or `C:\`.

   It then does what scrcpy's client does: pushes its `scrcpy-server` to the phone, makes a
   **forward** tunnel (the phone listens, so no firewall rule on the tablet), and starts the server
   with **the installed version**, which the server insists on. Video is H.264, at most 1920 on the
   long side, 60 frames a second, 8 Mb/s. Control is on. There is no sound yet: the phone plays
   through its own speaker or Bluetooth.

3. **Both protocols.** scrcpy's protocol is internal and changes between versions with no
   compatibility (`doc/develop.md`), so the dash reads the installed version and speaks its family
   (`ScrcpyVideo`):
   - **before 4.0:** the codec, width and height once, then packets with the config flag in the top
     bit. A new size shows only in the next SPS, which `H264Sps` reads, cropping included;
   - **from 4.0:** a session packet carrying the size each time it changes, and media packets with
     the flags one bit lower.

   Touch, keys, the phone's own display power and the key-frame request are the same bytes in every
   version so far (`ScrcpyControl`), checked against scrcpy's own serialization tests.

4. **Decoded by Android Auto's decoder.** `ProjectionVideo` (ADR-0057, LibVLC from raw Annex-B into a
   `WriteableBitmap`) takes the phone's packets as they arrive. The picture stays in WPF's tree, as
   every other picture on the stage does. Hidden, packets are dropped rather than decoded; shown
   again, a fresh key frame is asked for.

5. **The phone is told the region's shape, and put back.** No turning: the phone's screen is told the
   region's size and a density that makes one dp one of the dash's units (`AndroidFit`,
   `wm size`, `wm density`). scrcpy mirrors the screen Android was told it has, so the picture is that
   shape edge to edge, with no crop. At 636 dp or more on its short side, Android lays itself out as a
   tablet, so the launcher turns freely. On Android 12 and later, apps that insist on one way round are
   letterboxed rather than turning the screen (`set-ignore-orientation-request`).

   When the session stops, or the dash closes, the size, density and that setting are reset and the
   tunnel removed. The phone's own display is off while mirrored, which saves heat and battery, and
   comes back on.

   If the mirror ends any other way — the cable pulled, the Wi-Fi lost, the dash gone — a
   **watchdog** left on the phone puts them back (`PhoneShell.Watchdog`). It is a few lines of shell
   started beside the mirror and detached (`setsid`), so it outlives the connection. It waits while
   scrcpy's server runs, then resets. Its pattern is bracketed so it cannot find itself. This matters
   most because the phone is the owner's daily Pixel 8 until it is replaced.

6. **What a phone does for itself.** Its own GPS, microphone, clock, network and Bluetooth: none of
   the emulator's feeds are needed. It is only told whether it is night, as its dark theme
   (`cmd uimode night`). It is not Automotive, so there is no vehicle HAL to feed.

7. **One ADB: scrcpy's own**, else one on `PATH`. The dash and scrcpy then share one ADB server rather
   than two fighting (ADR-0075's *device offline*).

8. **Wireless is ADB over the network, switched on once by cable** (`PhoneFinder`). CONNECT
   WIRELESSLY, with the phone plugged in, does four things:
   - reads its Wi-Fi addresses (its connection to a network first, then its own hotspot);
   - tells ADB on the phone to listen on port 5555 (`adb tcpip`);
   - connects to it there;
   - remembers the address (`androidPhoneAddress`) and restarts the mirror.

   From then on, before looking for the phone, the dash connects to the remembered address and to each
   of the tablet's gateways. When the tablet is on the phone's own hotspot, as in the truck, the
   gateway is the phone. A phone by cable is still preferred to the same phone over Wi-Fi. A mirror
   that drops is tried again every ten seconds while ANDROID is showing.

   Android 11's **Wireless debugging** was not used: it needs the phone joined to a Wi-Fi network as a
   client, so it is off while the phone is the hotspot, which is the truck's case. The price of
   `adb tcpip` is that the phone forgets it when it restarts, and one cable connection switches it
   back on. Security: ADB's own key check applies over the network too, so a computer the phone has
   not allowed over USB is refused. On a network shared with strangers, the phone still listens on
   5555 until it restarts.

## Consequences

- The tablet decodes video, which its GPU does in hardware, instead of running Android. The phone
  does the drawing.
- A phone in the truck: mounted out of sight, on charge, and warm. The owner's original Pixel XL is
  for trial only: Android 10, so no sound forwarding (scrcpy needs Android 11), a nine-year-old
  battery on charge in a hot cab, and no security updates. The Pixel 8 that will live there has
  current Android, forwards sound, and can limit charging to 80 %.
- A real device is truly its own: its own Bluetooth, SIM, GPS and microphone.
- **No driving lock.** A phone has none of Android Automotive's restrictions while moving, and the
  emulated Automotive that would have had them is gone (on real hardware it is licensed only to car
  makers, ADR-0057). What is safe to do on the dash while driving is the driver's judgement, as with
  any phone.
- Video goes over the network when wireless: 8 Mb/s, fine on Wi-Fi or a phone's hotspot, a little
  more delay than the cable.
- Sound through the dash — scrcpy forwards it from Android 11 — is the next step. It also needs
  ducking under directions (ADR-0060).

## Alternatives considered

- **Frames from the emulator through shared memory** (`ImageTransport` `MMAP`): it would remove the
  gRPC copy, but not Android's own drawing and its translation, which is the other half of the
  processor. It is kept for the emulated flavours if they need it.
- **The scrcpy window over the stage**: a native window on the stage is what ADR-0021 refused.
- **scrcpy's virtual display** (`new_display`): it would leave the phone's own screen untouched, but
  needs Android 10's still-limited secondary-display launcher, and on 3.x it cannot be resized
  without restarting. It is worth revisiting on the Pixel 8.

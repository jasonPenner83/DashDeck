# ADR-0076 — Android on the stage from a real phone, mirrored with scrcpy

**Status:** Proposed · 2026-10-22. Built into the dash for trial with the owner's Pixel XL; the
phone that lives in the truck will be a Pixel 8.
**Builds on:** [ADR-0075](ADR-0075-android-on-the-stage.md) (the ANDROID cell, its hold menu, the key
bar, Android fitted to its region), [ADR-0057](ADR-0057-android-auto-through-the-dongle.md) (H.264
decoded into the visual tree), [ADR-0021](ADR-0021-owned-not-reparented.md) (nothing native on the
stage).
**Takes up:** ADR-0075's named fallback, *a real second Android device mirrored over ADB*.
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

1. **A third flavour, ANDROID PHONE,** in the ANDROID cell's hold menu, beside ANDROID and ANDROID
   AUTOMOTIVE. Tap, double tap, the key bar and the picture-size setting behave as before.
   `AndroidFlavour.Phone`.

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
   region's size and a density that makes one dp one of the dash's units (`AndroidFit.ForMirror`,
   `wm size`, `wm density`). scrcpy mirrors the screen Android was told it has, so the picture is that
   shape edge to edge, with no crop. At 636 dp or more on its short side, Android lays itself out as a
   tablet, so the launcher turns freely. On Android 12 and later, apps that insist on one way round are
   letterboxed rather than turning the screen (`set-ignore-orientation-request`).

   When the session stops, or the dash closes, the size, density and that setting are reset and the
   tunnel removed. The phone's own display is off while mirrored, which saves heat and battery, and
   comes back on. Unplugged mid-session, the size and density stay until the next start, or
   `adb shell wm size reset` and `adb shell wm density reset`.

6. **What a phone does for itself.** Its own GPS, microphone, clock, network and Bluetooth: none of
   the emulator's feeds are needed. It is only told whether it is night, as its dark theme
   (`cmd uimode night`). It is not Automotive, so there is no vehicle HAL to feed.

7. **One ADB.** The SDK's `adb` is used when Android Studio is installed, else scrcpy's. The emulators
   and the phone then share one ADB server rather than two fighting (ADR-0075's *device offline*).

## Consequences

- The tablet decodes video, which its GPU does in hardware, instead of running Android. The phone
  does the drawing.
- A phone in the truck: mounted out of sight, on charge, and warm. The owner's original Pixel XL is
  for trial only: Android 10, so no sound forwarding (scrcpy needs Android 11), a nine-year-old
  battery on charge in a hot cab, and no security updates. The Pixel 8 that will live there has
  current Android, forwards sound, and can limit charging to 80 %.
- A real device is truly its own: its own Bluetooth, SIM, GPS and microphone.
- Android Automotive with Google's apps stays the emulator's: on real hardware it is licensed only to
  car makers (ADR-0057).
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

# ADR-0075 — Android on the stage, as its own device

**Status:** Proposed · 2026-10-22. To be accepted, or not, on the AndroidProbe's numbers from the
Surface (below).
**Answers:** Q26 (Android Automotive on the stage, with no phone?), which
[ADR-0057](ADR-0057-android-auto-through-the-dongle.md) left as a separate experiment.
**Builds on:** [ADR-0057](ADR-0057-android-auto-through-the-dongle.md) (a picture decoded into the
visual tree, touch mapped to it as drawn), [ADR-0021](ADR-0021-owned-not-reparented.md) (nothing
re-parented onto the stage), [ADR-0038](ADR-0038-stage-launcher-file.md) (launcher entries).
**Keeps:** C1 (no drivers), read-only towards the vehicle.

## Context

The owner wants Android on the stage that is **its own device**: not the phone projected, as
Android Auto is, but an Android with its own apps, accounts and storage, running on the tablet. And
**both kinds**: Android Automotive, the car OS with a driving UI, and regular Android with every app.

What can run Android on a Surface Pro 7 (Intel, Windows 11):

- **Windows Subsystem for Android** — ended by Microsoft in March 2025. Gone.
- **The Android Emulator** — Google's, from the Android SDK, on Windows' own hypervisor platform
  (WHPX, an optional Windows feature rather than a driver). It runs any system image the SDK offers:
  Automotive with Google Play, and phone or tablet with Google Play, in x86_64. It has a **gRPC
  interface** for exactly this use — screen frames out, touch and keys in, GPS, audio — which is how
  Android Studio draws the emulator inside its own window.
- **An Android build in a virtual machine** (Android-x86, Bliss OS): Hyper-V gives such a guest no
  GPU acceleration, and VMware or VirtualBox install kernel drivers, which C1 refuses.
- **A real second Android device mirrored over ADB** (scrcpy's protocol): truly separate and light
  on the Surface, but another device to buy, mount and power. The owner chose the virtual route; this
  stays the fallback.
- **Waydroid and Cuttlefish** — Linux only.

## Decision (proposed)

1. **The Android Emulator is the device.** DashDeck starts an AVD hidden (`-qt-hide-window`, as
   Android Studio does), with gRPC on a localhost port and **a token** (`-grpc-use-token`), and
   finds the port and token in the emulator's discovery file (`pid_<pid>.ini`). Nothing native sits
   on the stage: frames from `streamScreenshot` are drawn into the visual tree, and touch on them is
   mapped back to the device's pixels and sent with `sendTouch` — ADR-0057's arrangement, so ADR-0021
   holds.

2. **Both flavours, as launcher entries.** A new entry type names an AVD:

   ```jsonc
   { "name": "AUTOMOTIVE", "type": "android", "avd": "Automotive_1408p" },
   { "name": "ANDROID",    "type": "android", "avd": "Pixel_Tablet" }
   ```

   Each AVD is its own device with its own data. Which one it is comes from its image (`tag.id`):
   Automotive or regular (`AvdInfo.Flavour`). **One runs at a time**, because memory is the limit;
   switching shuts one down to a quick-boot snapshot and starts the other from its own.

3. **What "its own device" means here, honestly:**
   - its own apps, Google account, settings and storage;
   - its network goes through the Surface's connection (the emulator's NAT), not an address of its
     own;
   - **no real Bluetooth**: the emulator's Bluetooth is virtual, so a phone cannot pair with it;
   - sound through Windows' default output (`streamAudio`), under the same ducking rules as the rest
     of the dash; the microphone later (`injectAudio`).

4. **The truck feeds it; it never feeds the truck.**
   - **GPS** from DashDeck's own fix, `setGps`, for both flavours, so maps inside Android know where
     the truck is.
   - **Vehicle data for Automotive**: speed, gear, fuel level and the like set in Android
     Automotive's vehicle HAL from DashDeck's signals, through ADB
     (`cmd car_service inject-vhal-event`). This writes to the emulator, never to the vehicle;
     DashDeck stays read-only towards the truck.

5. **It installs nothing.** The owner installs Android Studio (or the SDK's command-line tools) and
   makes the AVDs; DashDeck only finds them (`AndroidSdk`). Virtualisation is Windows' own *Windows
   Hypervisor Platform* feature, turned on in Windows' settings. The emulator's own hypervisor
   driver (AEHD) is not used: it is a driver, and C1 says no.

6. **The images' licence is the owner's to read.** The SDK's system images come under Google's
   Android SDK License Agreement, written for developing and testing apps. Using one as the tablet's
   everyday Android is the owner's call; DashDeck neither ships nor downloads an image.

7. **Measured before it is built.** `AndroidProbe` (in `AndroidProbe\` beside the dash) does what
   the occupant would — starts an AVD hidden, reads the discovery file, waits for boot, streams frames
   at the stage's size idle and while dragging, times touch to frame, and reads memory, processor,
   GPS and audio — and writes a report. The occupant is built if the Surface manages roughly:

   | | good enough |
   |---|---|
   | boot from a quick-boot snapshot | under 15 s |
   | frames while dragging, at the stage's size | 20 a second or more |
   | touch to the next frame | under 150 ms |
   | emulator memory | leaves the dash and Windows comfortable (on an 8 GB Surface, about 3 GB at most) |

   If it does not, the fallback is the real second device.

## First measurements on the Surface (2026-10-22)

Surface Pro 7 (Intel Iris Plus), emulator 37.2.12 on WHPX, with the dash running. The emulator wants
Vulkan 1.3.240 and the Intel driver offers 1.3.215, so it renders through OpenGL.

| | Automotive 1408×792 | Pixel Tablet 2560×1600 |
|---|---|---|
| boot from the quick-boot snapshot | **2.2–2.3 s** | 2.3 s (66 s the time the snapshot failed to load) |
| frames while dragging | **19 fps** the one time the screen moved | 2–4 fps |
| touch to the next frame | **22 ms** | 170–200 ms |
| emulator memory | **1.4 GB** | 2.6–3.5 GB |
| processor while measuring | 61 % | 72–74 % |

**Automotive passes.** The tablet's numbers are not yet a fair test. The screenshots showed it
measured while Android was still starting (*Pixel is starting…*, then *Pixel Launcher isn't
responding*), and it renders 2560×1600 for a stage of 1824×1272. A second Automotive run read
0 fps, but only because its home screen is still and a drag on it moves nothing; the emulator sends
frames only when the picture changes.

What the first runs taught the probe, now fixed:
- the discovery file is named after the virtual machine's process, not `emulator.exe`;
- emulators left running hold the AVD and the port, so the probe lists them and shuts them down
  through gRPC (which saves the snapshot) rather than starting over them;
- after "booted" it lets Android settle, opens Settings so a drag scrolls a list, and says when a
  screen that did not move made a number meaningless;
- frames are asked for at the stage's own size, 1824×1272.

Next: the tablet again, and a tablet AVD shaped like the stage (1824×1272 at 320 dpi, which makes
Android's units the dash's own). Then the occupant is built for whichever passes.

## Consequences

- No hardware to buy, and the stage gets Android in two flavours chosen per launcher entry.
- The heaviest thing DashDeck has ever run beside itself: gigabytes of memory, a hypervisor, and a
  processor working while Android draws. The probe exists so that is a number, not a hope.
- The gRPC interface is the emulator's own and changes between releases; the service definition is
  vendored and pinned, and the probe is the first thing to run after the emulator updates.
- Phone pairing, a SIM, and Android having its own address on the network are out of reach for a
  virtual device. Those are reasons to use the real-device fallback.

## Alternatives considered

- **A real Android device mirrored onto the stage** — kept as the fallback (above).
- **Show the emulator's own window on the stage**, owned and placed as ADR-0021 does for programs.
  Simpler, and wrong for a dash: the emulator's window brings its own toolbar and frame, and its
  scaling to the stage would be the emulator's, not ours. Drawing frames ourselves gives the same
  stage behaviour as Android Auto.
- **WSA, Hyper-V Android images, Waydroid** — see the context.

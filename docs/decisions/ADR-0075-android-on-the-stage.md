# ADR-0075 — Android on the stage, as its own device

**Status:** Proposed · 2026-10-22. Built into the dash for trial (the ANDROID cell, below); to be
accepted after it has been used in the truck.
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

**Second round, settled:** the tablet's touch fell to **60 ms** and it booted in 2.2 s, but dragged at
**4.6 fps** with 3.8 GB. Automotive moved nothing again: ADB answered *device offline* — the emulator
had run two ADB programs, the SDK's and scrcpy's, which restart each other's server — and the probe
had read that as Settings opening. Now success is Android's own *Starting: Intent*, an offline device
is reconnected first, screenshots are kept after opening Settings and after dragging, and the same
drag is measured again at half the frame size to tell copying frames out from Android drawing them.

**Third round, the tablet at the stage's size** (`-skin 1824x1272`, warm): boot **1.3 s**, touch
**22 ms**, **10.4 fps** dragging (69 MB/s of frames), **14.7 fps** with frames at half size, 2.7 GB,
70 % of the processor. Halving the frame bought 40 %, so copying frames out costs something but
Android drawing them is the limit — through OpenGL, because the Intel Iris Plus driver offers Vulkan
1.3.215 and the emulator wants 1.3.240, and this chip's driver is Intel's legacy branch. Automotive's
ADB stayed *offline* for the whole run, reconnected or not: the emulator ran two ADB programs (the
SDK's and scrcpy's). That matters past the probe — ADB is how Automotive will be fed the truck's data.

**Where it stands:** Automotive meets the bar — instant boot and touch, about 19 fps, about 1 GB.
Regular Android works at 10–15 fps: fine for something that mostly sits still, poor for scrolling.
Shared-memory frames (`ImageTransport` `MMAP`) and rendering below the stage's size are what is left
to try for it.

Earlier note: the tablet again, and a tablet AVD shaped like the stage (1824×1272 at 320 dpi, which makes
Android's units the dash's own). Then the occupant is built for whichever passes.

## In the dash: the ANDROID cell (2026-10-22)

The measurements were enough to try it for real, and the owner asked for it in the dash rather than
in another probe, shaped differently from decision 2: **not launcher entries but a nav cell**.

- **ANDROID**, a fifth cell on the nav strip. **Tap**: Android full screen, all six bands, the stage
  collapsed behind it as the map's full size does — and started if it is not running. **Double tap**:
  onto the stage, with the nav going back to where it was so the dash shows below. **Hold**: a menu
  of the two flavours — **ANDROID** and **ANDROID AUTOMOTIVE** — each naming the AVD it would run, or
  saying there is none, plus **STOP ANDROID**. The choice is saved (`androidFlavour`).
- The rules are Core's and tested: `AndroidView` (full or stage; a tap brings it back off the stage),
  `PressGesture` (a tap acted on at once — the first tap of a double tap opens it and the second sends
  it on, so a single tap is never held back — and a hold acted on while still held, at 550 ms),
  `AndroidChoice` (AVDs read from the emulator's own files, no process started; one per flavour,
  x86_64 first; touch to device pixels). `NavPress` feeds the gesture times from touch and mouse.
- `DashDeck.Emulator` holds the session: start an AVD hidden with a token, or join one of the same
  AVD already running; shut down any other first (one at a time); wait for boot; read the device's
  size from a full-size frame (its status does not carry it); stream frames at the stage's size;
  touch; position; and stop. The gRPC client is tested against a fake emulator served in-process from
  the same generated definition.
- **Started on first sight, kept after.** Leaving ANDROID stops its frames, not Android: coming back
  is instant. STOP gives the memory back. When the dash closes, Android is asked to shut down and
  left to finish, so the snapshot is saved without holding the dash past its ten seconds.
- Frames are dropped, never queued, when the UI is still drawing the last one.
- **Not yet:** Automotive's vehicle data through ADB (the two-ADB clash has to be settled first),
  audio, and rotating a phone-shaped Android to fill the portrait full-screen region.

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

## Addendum — 2026-10-22: first use in the dash; faster touch, a turning tablet, filling the region

**What the owner found with the ANDROID cell on the Surface:** both flavours load. Leaving and
coming back is instant, double tap to the stage works, and the hold menu works. **Touch works but
is slow**, and the regular tablet is hard to get around, letterboxed sideways in the tall
full-screen region. Not yet tried: Maps inside Android, STOP ANDROID, and how the rest of the dash
responds while Android runs. The Surface has **16 GB**, not the 8 GB this ADR assumed.

What changed, and why:

1. **Touch goes on one ordered stream** (`streamInputEvent`) instead of a `sendTouch` call for each
   move. Separate calls can overtake one another, so a drag arrived with its moves out of order and
   felt slow and jumpy. Now each move is queued and written to the one stream in order. If the
   stream breaks, the touch in hand is sent by itself and the stream is reopened for the next one.
2. **Frames are asked for at the size they are drawn.** Before, every frame was the stage's size
   (1824×1272), whatever the view: shrunk on the stage, stretched full screen. The panel now reports
   its size in screen pixels, and the stream restarts when that changes. The emulator fits the frame
   inside the size asked for, never larger than the device's own screen.
3. **A regular tablet turns to fit its region**, as Android Studio's rotate button does
   (`setPhysicalModel` ROTATION, about the screen's axis):
   - upright when full screen, which is tall (912×1170);
   - on its side on the stage, which is wide (912×636).

   `AndroidChoice.TurnFor` makes the choice:
   - 90° when the region's shape is the other way round from the device's screen, else 0;
   - nothing for a region within 5 % of square.

   Each frame says how the emulator turned it (`SkinRotation`), and touch is turned back the same
   way onto the device's unturned screen (`AndroidChoice.ToDevice`, using Android Studio's mapping).
   **Automotive is never turned.** A car's screen does not rotate and its interface does not follow
   the sensor, so turning it would only show a sideways picture of an interface that stayed put. A
   tablet whose Android has auto-rotate off would do the same, which is why the walkthrough checks
   for it.
4. **The emulator gets more of a 16 GB tablet.** It is started with `-memory 4096` (the AVDs ask for
   2 GB, and the tablet's launcher had stopped responding) and `-cores 4`. `EmulatorResources` sets
   these:
   - 4 GB from 14 GiB of memory up, otherwise the AVD's own;
   - half the logical processors, from two to four.

   The quick-boot snapshot was saved with the old memory, so the first start after this change is a
   **cold boot**. Later starts are quick again.

5. **Android takes the region's own shape, so there are no bars.** After rotation worked, the owner
   saw black bars beside the tablet, and Automotive not filling its space. One screen cannot be
   both shapes: full screen is 912×1170 (tall) and the stage 912×636 (wide), and no device turned
   or not matches both. gRPC cannot resize the emulator's main screen (`setDisplayConfigurations`
   leaves display 0 alone). Android itself can be told to use part of its screen, though:
   - The emulator starts at **the stage's own size** (`-skin 1824x1272`), so on the stage it fills
     it pixel for pixel. That costs one more cold boot, the first time.
   - For any other shape, Android is told the region's shape through the SDK's ADB (`wm size`).
     It draws that, centred, with black either side, and maps touch into it the same way. The frames
     are cropped to that part (`AndroidFit.Crop`), and a touch on the crop goes back through it
     (`AndroidFit.ToFrame`) before the rotation is undone.
   - `wm density` makes one dp the size of one of the dash's units (160 × the display scale, scaled
     by how much the picture is enlarged). Android then looks the same size in both placements, and
     the stage is 912×636 dp, which is still a tablet layout.
   - Full screen is drawn from fewer pixels than it covers: 1272×1632 for the turned tablet, 992×1272
     for Automotive, enlarged 1.4× and 1.8×. It is softer than the stage, and no slower.
   - If ADB cannot be reached (the two-ADB clash again), nothing is cropped and the bars are back,
     tried again every 15 s; `--shot` says why. Android keeps the size and density it was told after
     the dash closes. To give the AVD back to Android Studio as it was:
     `adb shell wm size reset` and `adb shell wm density reset`.

What is still open is unchanged: Automotive's vehicle data over ADB (the two-ADB clash first),
audio, and shared-memory frames (`MMAP`) if the tablet's frame rate is still the limit.

## Addendum — 2026-10-22: feeding Android the truck, the night, the microphone and the internet; less sluggish

The owner asked what else Android should be handed besides GPS. Its internet comes from the
Surface's connection, and its time from the Surface's clock, with nothing to do. Three things were
missing, most important first.

1. **The truck's motion, for Automotive's driving restrictions.** Android Automotive limits what can
   be done on screen while the car moves: video, the keyboard, long lists. It decides "moving" from
   speed and gear. Never told either, it believes it is parked for ever, so nothing was limited at
   100 km/h. Now, once a beat, `CarFeed` turns the dash's signals into what Android should believe,
   and the commands that tell it, sending only what changed:
   - speed from `vehicle.speed`, or the GPS ground speed when that is not live;
   - gear from `transmission.gearSelector` when the truck gives one;
   - parking brake (`warning.parkingBrake`), fuel level in litres of the tank set in Settings
     (`fuel.levelPercent`), and engine speed (`engine.rpm`);
   - night (below).

   **Gear is worked out until the truck says it**, and the guess errs towards restricted:
   - moving is Drive;
   - stopped with the engine running is still Drive, because idling at a light is not parked;
   - stopped with the engine off, or with the parking brake on, is Park;
   - the truck gone silent (the ignition off) while the GPS says it is still is Park;
   - with no speed at all nothing is said, and Android keeps what it was told last.

   Only live readings are used, so a stale speed is never passed on as the truck's.

   It goes in over the SDK's ADB, the way the fit does:
   - written into the vehicle HAL's own store (`dumpsys …IVehicle/default --set`), so the value
     holds;
   - or, on images that refuse that, as the car service's injected events
     (`cmd car_service inject-vhal-event`).

   Which one works is asked once per start. The signals are required from the arbiter while
   Automotive runs, so they are polled even when no gauge shows them. **This writes to the emulator,
   never to the truck.**
2. **Night.** The dash's own answer (`ThemeService.IsNight`: the headlights, or the sun) goes to
   both flavours:
   - Automotive's `NIGHT_MODE` property;
   - a tablet's dark theme (`cmd uimode night`).
3. **The microphone.** The emulator gives Android silence unless host input is allowed. The dash now
   allows it at start (`setMicrophoneState`, what Android Studio's switch does), so "Hey Google" and
   voice typing hear the cab. Windows must let desktop apps use the microphone. It is a
   settings-file switch, `androidMicrophone`, on unless turned off.

The hold menu's line for the running flavour says whether it is working:
- *car data live · 62 km/h · DRIVE*, or *car data refused (…)*;
- *mic on* or *mic off*.

4. **Internet that resolves.** On the Surface the tablet said *AndroidWifi · Connected / No internet
   access*. The emulator shares the Surface's connection, but on Windows it can hand Android the DNS
   servers of a virtual adapter, such as the hypervisor's own switch, which answers nothing. The dash
   now passes `-dns-server` (`EmulatorNetwork.DnsServers`):
   - up to two of the Surface's own, from the adapters with a gateway;
   - then `1.1.1.1` and `8.8.8.8`, so names still resolve when the truck moves from the house's Wi-Fi
     to the phone's hotspot while Android runs (the emulator's DNS is fixed at start).

   If Android still says no internet, the next suspects are a VPN on the Surface, and Android's clock
   after resuming from the snapshot.
5. **Sluggishness: more of the tablet, and less work per frame.** The probe measured that copying
   frames out costs about as much as Android drawing them (half-size frames gave 40 % more). So:
   - **cores:** all the Surface's logical processors but two (six of eight, `EmulatorResources`),
     instead of half;
   - **no copy of each frame:** the pixels are read straight out of the gRPC message. Each frame used
     to be copied into a new array of several megabytes, garbage that made the dash's collector work
     several times a second;
   - **four pixels at a time:** RGB to the bitmap's BGRX with one SIMD shuffle per four pixels;
   - **how it draws, shown, and the GPU asked for:** the emulator's own words about its GPU mode go
     in the hold menu (`EmulatorGpu.Renderer`). On the Surface it said **`sw`**: left to itself, the
     emulator drew Android in software, on the same processor running Android. That is most of the
     sluggishness, and the probe's 10 fps for the tablet. It does not trust the Intel Iris Plus
     driver, whose Vulkan is older than it wants. So the dash now **asks for `-gpu host`**, the
     tablet's GPU through its OpenGL driver. If the emulator will not start that way, it starts
     again with the emulator's own choice (`EmulatorGpu.Choose`; `androidGpu`: `auto` is that,
     `emulator` leaves it to the emulator, or name a mode). Once asked for the GPU, the emulator printed
     nothing the menu recognised, so what draws is now read from the running emulator's own configuration
     (`getStatus`: the resolved `hw.gpu.mode`, with the cores and memory Android really has) and,
     when Android answers, from SurfaceFlinger's `GLES` line, which names the GPU behind the
     emulator's translator (`EmulatorGpu.FromConfig`, `FromGles`);
   - memory stays at 4 GB: Android was not short of it, and the dash and Windows need the rest.

   The change in cores makes the next start a cold boot, as the memory change did. Not done, because
   it cannot be checked without a real emulator: frames through shared memory (`ImageTransport`
   `MMAP`), which would remove the gRPC copy altogether. It is next if this is not enough.

Still open: Android's sound ducking under directions, and steering-wheel keys.

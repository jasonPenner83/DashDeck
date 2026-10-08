# Android Auto and CarPlay on the stage

DashDeck shows Android Auto (and CarPlay) on the stage through a **Carlinkit dongle** plugged into
the tablet. The dongle does the phone's half of the work in its own firmware and sends DashDeck the
picture and the sound over USB; DashDeck draws the picture, plays the sound and sends your touches
back ([ADR-0019](decisions/ADR-0019-phone-projection.md),
[ADR-0057](decisions/ADR-0057-android-auto-through-the-dongle.md)).

**Android Auto always needs the phone.** The apps (Maps, Spotify, messages) run on the phone and the
dongle is the head unit it projects to. The phone can stay in a pocket: after the first pairing it
connects over Bluetooth and Wi-Fi by itself.

## What to buy

A **Carlinkit CPC200-CCPA** — the one sold as *"Android Auto + CarPlay adapter for Android head
units"*, often with the *Autokit* app named on the box. It is made to plug into a screen that is not
a car's own head unit, which is what the tablet is.

**Not** the Carlinkit 3.0 / 4.0 / 5.0 / "2air" adapters. Those are made for cars that *already have*
wired CarPlay — they pretend to be a phone to the car's head unit and have no picture to give a
tablet.

Check that it shows up as **USB vendor `1314`, product `1520` or `1521`**. If the listing does not
say, check after plugging it in (see below). A different number is a different protocol.

## Binding it to WinUSB (once)

Windows has no driver for the dongle. DashDeck talks to it through **WinUSB**, Microsoft's own generic
driver, which has to be attached to it once by hand. This is the second driver exception ADR-0019
allows; nothing else is installed.

1. Plug the dongle into the Surface (a USB-A to USB-C adapter is fine).
2. Download **Zadig** from <https://zadig.akeo.ie> (one exe, no install).
3. Run it. **Options ▸ List All Devices**.
4. Pick the dongle in the list — it is named something like *Auto Box* or *Carlinkit*. Check the
   **USB ID** boxes read `1314` `1520` (or `1521`). **If they read anything else, stop**: it is the
   wrong device, and replacing its driver can break it (a keyboard, the camera).
5. To the right of the green arrow, choose **WinUSB**. Click **Replace Driver** (or **Install
   Driver**). Wait for *successful*.
6. Unplug the dongle and plug it back in.

To undo it: Device Manager ▸ the dongle ▸ Uninstall device ▸ *Delete the driver software*.

## Pairing the phone (once)

1. On DashDeck, choose **PHONE** on the launcher bar. With the dongle plugged in it reads **WAITING
   FOR A PHONE**.
2. On the phone, Bluetooth ▸ pair with **DashDeck** (the dongle names itself that). Accept the code.
3. The phone asks to use Android Auto with the new car; accept. Within half a minute the stage reads
   **ANDROID AUTO** and shows the Android Auto screen.

After that, choosing PHONE is enough: the dongle asks for the last phone it knew.

## The Raspberry Pi and SYNC 3

The Pi gives SYNC 3 wireless Android Auto, and it stays doing that. A phone runs Android Auto on one
head unit at a time, so with both powered **the phone picks one** — usually whichever it reaches
first.

- **To use DashDeck:** leave the Pi unplugged (or its USB lead out of SYNC 3) for the drive.
- **To use SYNC 3:** unplug the dongle, or don't open PHONE.
- If the phone keeps choosing the wrong one: Android Auto settings ▸ *Previously connected cars* —
  turning *Add new cars to Android Auto* off, or forgetting one, settles it.

## Where the sound comes out

DashDeck plays Android Auto's sound on **Windows' default output** — music and directions, mixed.
DashDeck does not choose the truck's speakers; you do, in Windows:

- **The tablet's speakers** — nothing to set up.
- **The truck's stereo over Bluetooth** — pair the *Surface* (not the phone) with SYNC 3 as a media
  device, and pick SYNC 3 as Windows' output. SYNC 3's source must be *Bluetooth Stereo*.
- **AUX** — a cable from the Surface's headphone jack, if the truck has an AUX input.

Calls use the dongle's microphone.

## When it doesn't work

| The stage says | What it means | What to do |
|---|---|---|
| **NO DONGLE** — *No Carlinkit dongle is plugged in* | Windows sees no `1314:1520`/`1521` device. | Plug it in. Check the USB ID in Zadig. |
| **NO DONGLE** — *not bound to WinUSB* | It is there, without WinUSB. | Do *Binding it to WinUSB*. |
| **NO DONGLE** — *could not be opened* | Something else has it open, or the binding is half-done. | Close other programs; unplug, replug; redo Zadig. |
| **WAITING FOR A PHONE** | The dongle is set up, no phone has connected. | Pair (above). Check the Pi isn't taking the phone. |
| **ANDROID AUTO CONNECTED** — *no picture has arrived yet* | The phone connected; no video yet. | Wait 10 s. Unlock the phone. RECONNECT from the three-dot menu. |
| **ANDROID AUTO, NO PICTURE** — *The video decoder…* | LibVLC is missing from the install. | Reinstall DashDeck (`publish.ps1` ships it). |
| **LINK LOST** | The dongle stopped answering. | It is looked for again every 5 s; replug if it stays. |

At a desk without a dongle, `--synthetic-dongle` runs the PHONE stage against a pretend one that
connects an Android Auto phone and sends no picture.

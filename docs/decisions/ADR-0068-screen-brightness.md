# ADR-0068 — The screen's brightness: the tablet's backlight, by day and by night, given back on exit

**Status:** Accepted · 2026-10-18
**Extends:** [ADR-0013](ADR-0013-theming.md) — day and night now set the backlight as well as the
palette, from the same answer. **Keeps:** constraint C1 — nothing installed, no registry, no
service. **Leaves open:** F10 — the headlights, which will decide day and night for both.

## Context

Night mode dims the palette (ADR-0013, ADR-0036): surfaces ×0.55, text ×0.74. On the Surface that
helps less than it should, because a dark pixel on a backlit LCD is still lit by the backlight, and
the backlight is wherever Windows left it — usually high, because the tablet is used indoors the
rest of the time. In a dark cab the dash is a torch whatever colour it is painted.

The direction is set: the truck's **headlight state** says whether it is night, and the **console
dimmer** says how dim the driver wants the cluster — the same judgement the factory screens already
follow. Neither signal exists yet. Both are Ford body-module messages over MS-CAN (F10, R2, R4),
and `body.headlights` is a placeholder in the catalog (ADR-0053, ADR-0056). So this is built to
take them and runs on what exists now.

Two ways to make the screen dimmer were open.

**A dark layer over DashDeck's own window.** Touches nothing on the tablet. It also cannot reach
half the screen: the map, video, Android Auto and every hosted app draw in windows of their own,
and WPF cannot compose anything over them (ADR-0021, the reason the stage has a bar rather than
floating chips). A dimmed dash with a full-brightness map in the middle of it is worse than no
dimming.

**The panel's backlight.** Dims everything, including what DashDeck did not draw. But the Surface
is a personal device (C1), and its brightness is the person's setting, not the dash's.

Jason chose the backlight, given back on exit.

## Decision

1. **DashDeck sets the built-in panel's backlight**, through Windows' own WMI classes in
   `root\wmi` — `WmiMonitorBrightness` to read, `WmiMonitorBrightnessMethods.WmiSetBrightness`
   to write. These are what the Action Center slider uses. In-box, no elevation, nothing
   installed. `dxva2`'s `SetMonitorBrightness` was not an option: it speaks DDC/CI, which external
   monitors answer and a laptop's own panel does not. One new package, `System.Management`, from
   the .NET runtime (MIT).
2. **A day level and a night level**, chosen in Settings ▸ DISPLAY ▸ BRIGHTNESS. Day offers
   40–100 %, night 5–60 %. Defaults are 100 % and 30 %. Nothing goes below 5 %, including a
   hand-edited settings file, because 0 on a Surface is a black screen whose touch layer still
   works.
3. **Day or night is not decided here.** The backlight follows `ThemeService.IsNight`, the same
   answer the palette uses, so the two can never disagree. Today that is Appearance's DAY, NIGHT
   or AUTO (sunrise and sunset). When the headlight signal exists, `ThemeService.ResolveAuto`
   changes as F10 already says, and the backlight follows with no change of its own.
4. **The console dimmer will become the night level**, in `BrightnessPolicy.Target` and nowhere
   else. The person's own night level stays as the answer while the truck has not said. Not built:
   the signal's shape is not known yet.
5. **It borrows the backlight and gives it back.** The level found at launch goes back when
   DashDeck closes — **unless somebody moved it since**. If the level on exit is not the one
   DashDeck last set, a person or Windows changed it while the dash ran, and that change is newer
   than anything DashDeck remembers. One step either way counts as the same level, because a panel
   with coarse steps rounds what it is given; what is compared is the panel's own reading after
   the write, not the number asked for.
6. **A crash cannot give anything back, so the loan is written down when it is made.** The
   original and the level set go into `settings.json` (`backlightOriginal`, `backlightSet`) at
   every write. The next launch reads the tablet at the dimmed level it was left at. If that
   matches what was written down, the record supplies the real original. If someone has moved it
   since, the record is stale and dropped.
7. **It writes only when the level wanted changes**: a day/night switch, a settings tap, or ON.
   The shell's one-second beat asks, but nothing is written while the answer is the same. So a
   person who moves the Windows slider mid-drive is not fought every second; DashDeck speaks again
   at the next change.
8. **OFF gives the level back at once**, not at exit, so OFF means the tablet's own level from the
   moment it is tapped.
9. **All backlight calls run on one worker, in order.** WMI takes tens of milliseconds a call and
   more for the first connection, and a dash that stalls at dusk is worse than one that dims a
   moment late. Closing waits up to 3 s for the restore and then moves on, leaving the record
   written so the next launch can still put it back.
10. **A screen whose backlight cannot be read is left alone and says so.** A desktop on an
    external monitor has no instances of the class. Settings shows *This screen's backlight can't
    be set from Windows* rather than offering levels that do nothing.

The rules — which level, and when to give back what — are in `DashDeck.Core.Brightness`
(`BrightnessPolicy`, `BacklightSession`) and tested off Windows. The controller,
`ScreenBrightness`, is tested against a fake panel. Only `WmiBacklight` needs the tablet.

## Consequences

- At night the whole screen dims, the map and Android Auto included. Nothing else on the dash
  could do that.
- **Windows' automatic brightness can fight it.** The Surface has an ambient light sensor, and
  with *Change brightness automatically when lighting changes* on, Windows moves the level on its
  own. DashDeck sets it once per change rather than holding it, so whichever wrote last wins. The
  walkthrough checks this. If the two fight in practice, the answer is to turn Windows' option off
  while driving rather than have DashDeck reassert every second.
- A crash leaves the tablet dimmed until the next launch, or until the person moves the slider.
  That is the price of the backlight over a layer, and the record keeps it to one launch.
- `SettingsStore.Update` is now serialised with a lock, because the backlight's worker writes the
  file from a thread that is not the dispatcher. One process, but no longer one thread.
- `--theme NIGHT` previews now dim the real backlight too, on any machine with a built-in panel.
  That is what a preview of night should do.
- The palette still dims at night as well. The two compound, which is deliberate: the palette keeps
  the contrast right, and the backlight sets the overall light.

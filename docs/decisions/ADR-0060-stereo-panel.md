# ADR-0060 — The Stereo panel, and what gets out of the way for what

**Status:** Accepted · 2026-10-14
**Answers:** Q14's remaining half — the Stereo card, and the ducking contract it left owed.
**Relates to:** [ADR-0059](ADR-0059-audio-delay.md) (the output's delay),
[ADR-0057](ADR-0057-android-auto-through-the-dongle.md) (projection lands on the same output),
[ADR-0040](ADR-0040-climate-panel.md) and [ADR-0041](ADR-0041-console-dash.md) (the other two
bottom-third panels), [ADR-0025](ADR-0025-occupant-lifetime.md) (audio outliving what you are
looking at).

## Context

Q14 decided to build a Stereo card on 2026-10-13 and left two things undone: the card itself, and
**the audio-ducking contract** — because projection's audio and DashDeck's own land on the same
Windows output and Windows *mixes* rather than choosing, so without a contract a turn instruction
arrives underneath a chorus.

STEREO has been in the navigation since B3 with nothing behind it, drawn because the nav is the
destination list and an empty strip says less than "not built yet". It is the last such entry.

Q33 changed the stakes while this was being built: **there is no AUX jack in this truck**, so
Bluetooth is the only path to the speakers, and the Stereo card is the only way DashDeck makes
sound a passenger can hear.

## Decision

**A hand-written panel on the bottom third**, the same 912 × 390 canvas the climate panel and the
console use — not a JSON layout like those two. Both of them *display vehicle signals*, which is
what the layout engine is for; a stereo has transport controls, and the engine has no element that
invokes a command. Adding one to serve a single panel would be the tail wagging the dog.

**Playback is LibVLC, which is already here.** The video occupant bundles it precisely because
WPF's built-in player leans on whatever codecs Windows happens to have, and a clean tablet (C1)
plays about half of what a person owns. The same reasoning applies unchanged to audio, at no new
dependency.

**Files on the tablet, from one folder chosen in Settings, searched including subfolders.** The
dash is launched by hand in a truck that may have no signal (C5), so the one source that always
works is what is already on the disk. Streaming services need accounts, tokens and a network, and
each is its own component later if wanted.

**Windows' default output, never a device of DashDeck's choosing (C3).** The person points Windows
at the Bluetooth link; the sound follows. The same rule projection's audio follows (ADR-0057, F21).

### The ducking contract, which is the part Q14 actually owed

| What is claiming the speakers | What playback does | Why |
| --- | --- | --- |
| Nothing | Plays normally | — |
| **A spoken instruction** | **Lowers to 25 %**, keeps playing | A turn instruction lasts a second or two. Pausing for it leaves a hole longer than the words and a jarring restart. |
| **A warning chime** | **Lowers to 12 %** | Harder than speech: a chime is short, and a missed warning is the one failure that matters. |
| **The phone's own music, through projection** | **Pauses outright** | Two music sources mixed is noise with no useful reading. Burying ours at 20 % would not help anyone. |

**The strongest claim wins, not the latest.** More than one can hold at once — a chime during a
turn instruction during projected media is not far-fetched — and taking the strongest means the
music cannot be quietly restored by a weak claim ending while a stronger one still holds.

**A pause caused by ducking is remembered; a pause the person made is not.** When the claim lifts,
music the contract stopped resumes, and music the person stopped stays stopped. Resuming playback
someone deliberately silenced would be the kind of thing that makes a person distrust a dash.

**Volume has two owners and they multiply.** What the person set, and what the contract says. So
ducking can never lose the volume someone chose.

### The panel outlives itself

The climate and console panels are made when their destination is chosen and disposed when it is
left, so their signals are declared only while visible. **The Stereo panel is built and dropped the
same way, but the player behind it is owned by the shell for the session.** Disposing the player
with the panel would stop the music the moment you looked at the map, which is the opposite of what
a stereo is for — the same reasoning ADR-0025 applied to a stage occupant that keeps playing when
navigated away from.

### No delay compensation here, deliberately

ADR-0059's policy gives music nothing to be in sync with, so the 150 ms or more the Bluetooth link
adds is inaudible for playback. The panel therefore **shows** the figure in its corner rather than
acting on it. The delay matters for a video's picture and for spoken instructions, not for this.

## What else went into Settings

**AUDIO** — the music folder, the ducking switch, the output delay as a typed number (ADR-0059's
primary path, which always works), and the probe that measures it. The probe is the console tool's
measurement driven from a button: `DelayMeasurement` in `DashDeck.WinAudio` is the single
implementation, and both callers drive it. Four attempts on a real truck changed that method three
times; two copies would have drifted apart by the second.

**ABOUT** — what this build is, and where it keeps things, with a button that opens each folder.
Not asked for, and added because every answer on it was something that had to be dug out of a
terminal during bring-up: which build is on the tablet, what the adapter identified itself as,
where the layouts and captures live. In the truck there is no keyboard and no terminal, so a screen
that answers them is the difference between fixing something in the cab and writing it down for
later. Read-only, which is why it is safe to open with the engine running.

## Rejected

- **A JSON layout like the climate panel.** The layout engine has no command element, and giving it
  one to serve a single panel is backwards. If a second interactive panel ever wants one, that is
  the moment to reconsider.
- **Reading tags (ID3, Vorbis) for artist and album.** The file name and the folder name carry
  nearly all of it for a folder of music someone organised themselves, and a tag reader is a
  dependency and a parsing surface for a cosmetic gain. The track's folder stands in for the album.
- **Controlling SYNC 3's own radio.** Needs writes to an undocumented Ford network and fails the
  same gates the climate controls failed (ADR-0040). This is additive: DashDeck's playback, not the
  truck's.
- **Pausing music under a spoken instruction.** The obvious reading of "get out of the way", and
  wrong — see the table.
- **A seek slider with a thumb.** A thumb is a small target in a moving truck, and a slider bound
  two-way fights the once-a-second refresh that advances the bar. The position line is tapped
  instead.

## Consequences

- **A missing native LibVLC costs the music, never the dash.** The shell builds the player at
  startup so playback survives navigation, which means a constructor that threw would take the
  whole dash down over an audio library. `StereoPlayer.TryCreate` returns null with a reason, the
  panel says so, and everything else still runs. A dash that will not start is far worse than one
  that cannot play music, and from a window that never appears nobody can tell which broke.
- **`DashDeck.WinAudio` is a new project**, holding the `winmm` playback and capture that the
  console tool used to own privately. Still no package, no driver, no registry (C1).
- **The ducking contract has no caller yet.** `DuckPolicy` is built and tested, and
  `StereoPlayer.SetFocus` applies it, but nothing currently reports that projection is speaking —
  ADR-0057's dongle has not arrived (Q25). The setting exists, the mechanism exists, and the wire
  between them is the next change rather than this one. Said plainly here because a switch that
  does nothing yet is worse than no switch if nobody wrote down which.
- **`MusicLibrary` parses paths itself** rather than using `Path`, whose helpers follow the *host's*
  separator — so on Linux `GetFileName` of a Windows path returns the whole string. That made
  `DashDeck.Core` quietly not platform-neutral, which is the one thing it claims to be, and it
  showed up the moment the tests ran anywhere but Windows.

# ADR-0059 — The Bluetooth delay: set it by hand, measure it with the microphone

**Status:** Accepted · 2026-10-13
**Follows:** Q14 — the Stereo card and the decision to send audio to SYNC 3 over Bluetooth.
**Relates to:** [ADR-0057](ADR-0057-android-auto-through-the-dongle.md) (projected audio shares the
same output), [ADR-0058](ADR-0058-offline-map-and-routing.md) (spoken turn instructions),
[ADR-0025](ADR-0025-occupant-lifetime.md) (audio lives as long as the occupant).

## Context

Q14 settled the audio path on 2026-10-13: the Surface sends its sound to SYNC 3 over Bluetooth, so
DashDeck's music, its spoken directions and Android Auto's audio all come out of the truck's
speakers. The first thing that showed up in use is the delay.

**It is not a bug and it cannot be removed.** A2DP buffers on the sending side, encodes to SBC or
AAC, transmits, and the receiving end buffers again before it decodes, runs its equalisation and
speed-compensated volume, and reaches a speaker. Every stage is there to survive a radio link that
drops packets. Published figures for car head units sit around 150–250 ms, and SYNC 3's own
processing is inside that. Nothing on the tablet can subtract from it: whatever the dash hands to
Windows comes out of the truck a fifth of a second later.

So the request — "let us set the audio delay" — needs one correction before it can be built, because
taken literally it produces a control that does nothing. **A delay control can only ever add delay.**
The useful question is never *how much* but *late relative to what*, and the answer is different for
each kind of sound the dash makes.

## Decision

### One measured number, four different answers

The link's delay is **one property of one output**, measured or typed once, kept against the endpoint
it belongs to (`AudioDelay`). What is done about it is decided per use (`AudioDelayPolicy`), because
the same 200 ms is a different problem each time:

| What is playing | What is done | Why |
| --- | --- | --- |
| **Music, radio, podcasts** | Nothing. | There is nothing for it to be in sync with. Nobody can hear a delay that has no reference. |
| **A picture on the tablet's screen** — a video, Android Auto | **Hold the picture** by the delay. | The sound cannot be hurried, so the video waits for it. This is a *video* delay, which is the opposite of what the control's name suggests. |
| **A turn instruction, a warning chime** | **Start it early** by the delay. | The only real fix rather than a compromise. The dash knows the turn is coming, so it speaks ahead and the words land on time. |
| **A click answering a tap** | **Play it on the tablet's own speaker.** | The tap is already in the past. No scheduling brings the click back; the only escape is a different output. |

The second and third rows move in opposite directions from the same number, which is exactly why a
single global "audio delay" slider would have been wrong: set for lip sync it makes directions later
still, and set for directions it makes video worse.

**Pre-firing is the row that earns its keep.** At 100 km/h the truck covers about 5.6 m in 200 ms —
the difference between "turn right now" and having gone past it. Speaking early fixes that
completely, and costs nothing, because the instruction was known in advance. The caller must clamp
the amount to the lead time it actually has: a turn discovered 100 ms ahead cannot be announced
200 ms early.

### Setting it by hand is the primary path

A number field in Settings, in milliseconds, per output. It always works, needs no microphone, no
quiet moment and no permission, and a person can tune it by eye against a video in about three
tries. Automatic measurement is a convenience on top, never a dependency: if calibration fails,
refuses, or is not run, the typed number is what the dash uses.

### Measuring it with the microphone

The probe is a **swept sine of 100 ms, 500 Hz to 4 kHz**, played out the output under test and
recorded on the tablet's microphone. Finding it again is a matched filter: slide the reference over
the recording, take the strongest normalised correlation.

- **A sweep, not a click.** Bluetooth audio is lossy, and a codec smears a transient into something
  a correlator cannot place. A sweep spreads its energy across time and band and comes back
  recognisable, which also makes it immune to the stereo's equalisation and to road noise, because
  the match is integrated over the whole band rather than resting on one instant.
- **Both ends tapered** by a raised cosine over a tenth of the length. A sweep that starts on a step
  clicks, and the codec turns that click into correlator noise.
- **Five to seven repeats, and the median.** A dropped packet or a truck going past ruins one probe;
  a mean would carry a share of it into the stored number forever. The best-to-worst spread is kept
  and shown, because a wide spread is the signal that this link is not steady enough to compensate
  with a fixed number at all.

**The measurement is made twice and subtracted, because one measurement answers the wrong question.**
What the microphone hears is the speaker's delay *plus* the sound's flight across the cab *plus*
however long Windows took to hand over the capture — and that last term is 10–30 ms on a shared-mode
microphone, far too much to ignore when the answer is around 200 ms and is going to be used to shift
video. So the same probe is also sent to the **tablet's own speaker** and recorded through the same
microphone. The capture delay is identical in both runs and **cancels in the difference**:

```
stereo delay = (heard from stereo − heard from tablet speaker)
             + the tablet speaker's own output delay
             − the difference in flight across the cab   (about 3 ms)
```

Only the tablet speaker's output delay has to be trusted, and that is a built-in device whose
reported figure is honest — unlike a Bluetooth endpoint, where what Windows reports stops at the
radio and omits everything the far end does.

### Failing rather than guessing

A match is believed only above a **peak-to-sidelobe ratio of 8** — the peak against the spread of
every other lag, which stays meaningful at bad signal-to-noise where a plain correlation coefficient
collapses. The floor was measured, not guessed: over 400 recordings containing nothing but noise the
worst peaked at **5.3**, while a probe buried at a fifth of the noise amplitude still scored **10.2**
and landed on the exact sample.

This is deliberately tuned to fail rather than guess. Refusing a faint probe costs a retry or a
number typed by hand. Accepting a noise peak means shifting video by a lie, with nothing downstream
able to tell.

### The number is stamped, not trusted forever

A2DP delay depends on how full the receiving end's buffer is, so it moves when the link reconnects,
when the stream has been idle long enough for the stereo to re-buffer, and under radio interference.
The stored value therefore carries its endpoint, its source (typed or measured), its spread and when
it was taken, and Settings shows all of it with a re-measure button. It is a good approximation of
one connection, not a constant.

## What is built, and what is tested

`DashDeck.Core/Audio` is platform-neutral and holds all of the above except the microphone and the
speaker:

- `LatencyProbe` — the sweep, and finding it in a recording with a confidence that can say "no".
- `LatencyCalibration` — repeats to a median, and the two-run subtraction.
- `AudioDelay`, `AudioDelayPolicy` — the stored number and the four answers.

**It is tested against synthetic recordings, not against a truck.** A delay is put into a buffer on
purpose and the measurement has to get it back out: clean, under noise louder than the probe itself,
through clipping and a tilted filter standing in for the stereo's processing and a lossy codec, and
with no probe present at all — which must report failure rather than a number. Two tests pin the
confidence floor from both sides so retuning the probe later cannot quietly close the gap. The
two-run subtraction is checked against capture delays from 5 to 60 ms, recovering the same answer
every time. 26 tests.

What a truck is needed for is the number itself, and three things that cannot be checked here
(Q33, Q34, Q35).

**The tool that exercises it: `AudioProbe`** (`src/DashDeck.AudioProbe`), a console bring-up tool in
the same vein as `BringUp`, `IdHunter` and `SerialTap` — the dash itself has no audio-delay UI yet,
and a measurement this uncertain is worth proving on hardware before it gets a button. It lists the
wave devices, plays the sweep alternately to the reference speaker and the output under test inside
**one** recording, and prints the delay with its spread. Playback and capture are `winmm`
P/Invoke, matching `WaveOutPlayer`: no package, no driver, no registry (C1). The walkthrough is in
`docs/08-in-vehicle-testing.md`.

Its one piece of real logic — reading several probes out of one recording — lives in
`ProbeSession` in `DashDeck.Core.Audio` rather than in the tool, so it is tested. That logic has a
trap worth naming: each probe is searched for in a window running forward from its handover, and if
probes are spaced closer together than that window is long, **one probe's window reaches the next
one, matches it perfectly, and answers confidently with the wrong number**. `ProbeSession.Spacing`
states the minimum gap, and two tests hold it to both sides of the boundary — one proving correct
spacing works, one proving that breaking the rule does produce a wrong answer, so the rule is
load-bearing rather than decorative.

## Rejected

- **A single global "audio delay" slider.** The obvious reading of the request, and wrong: two of
  the four uses need the compensation to go in opposite directions, and a third cannot be
  compensated at all.
- **Delaying Android Auto's picture to match its sound.** Possible with the same number, but
  projection is a touch surface — holding frames adds that delay to every tap. Maps and music do not
  lip-sync, so the delay is left visible there and the frames are not held. Revisit only if it
  actually looks wrong.
- **Fixing it with a published figure for SYNC 3.** The numbers in circulation are community
  measurements of other head units, they vary by codec and by phone, and they do not survive a
  reconnect. Measuring takes one button.
- **A virtual audio driver or a low-latency audio stack** (ASIO, WASAPI exclusive mode through a
  third-party driver). Would reduce the Windows-side contribution, which is the small end of the
  problem, and every option needs an install — against C1.
- **Sending a click and timing the first peak above a threshold.** Simpler, and defeated by the
  codec, by cabin noise and by the stereo's own noise floor. The sweep costs 15 lines more.

## What the truck taught this ADR, 2026-10-14

Four attempts to measure a 2019 F-150 found three things the design above had wrong. Recorded here
because each was invisible in 400-odd synthetic tests and obvious within one run on hardware.

**The probe design was fighting A2DP, and that was the root cause of every bad number.** Probes
played as isolated bursts with silence between them let Windows **suspend** the Bluetooth stream,
and a car head unit reads each suspend and restart as media pause and play. The owner saw SYNC 3's
screen toggling through the whole measurement. So every reading was **a cold stream start, not a
delay** — how long the head unit takes to wake its audio pipeline, measured repeatedly and reported
as latency. It accounts for readings near 1165 ms, spreads of 134 ms, and earlier ones near 440 ms,
none of which bore any relation to what the dash will experience during continuous playback.

The fix is **one continuous track per output** with the probes embedded at exact sample offsets
(`ProbeTrack`), preceded by a lead-in of inaudible carrier so the stream's single cold start is paid
for by nothing. The stream starts once and stays up, the sink buffers once and settles, the head
unit sees one play, and because every probe on an output shares one playback start, per-probe
handover jitter disappears — so a scattered reading now means something is genuinely wrong rather
than merely jittery.

**Two outputs need two distinguishable probes.** An upward sweep for the reference, a downward one
for the output under test. They hold the same energy in the same band, so neither is favoured by
the codec or the cab, but neither is found in the other's window. Without that, an output slower
than the search window delivers its probes out of order and its late arrivals are found in the
*other* path's window — which produced a reported delay of 0.6 ms, physically impossible, from a
real truck.

**The reference's own spread has to be a gate, not a readout.** A run reported a confident 445 ms
built on reference readings spanning **1605 ms**. The spread was computed and printed, and gated on
only for the measured output — yet the method subtracts the reference from it, so a bad reference
poisons the answer while the measured output's numbers still look respectable. Now checked before
the subtraction, with a 50 ms limit.

One more bug found along the way, unrelated to Bluetooth: the recorder was silently **skipping**
capture buffers that came back short. That drops a moment of time, not of sound — every later
sample moves earlier, so every arrival after the gap is wrong by a different amount each run. Gaps
are now padded so the timeline survives, and counted so a run that had them can be distrusted.

And one hypothesis raised and then discarded by measuring rather than believing: the confidence
floor is **not** too low for a wider search. Noise peaks reached 4.61 over 4000 lags, 4.88 over
32000, and 5.27 with the opposite sweep present, against a floor of 8. No width-scaling was added
for a cause that turned out not to exist.

**Q33 is answered and it raises the stakes: this truck has no AUX jack.** The cheap escape does not
exist, so Bluetooth is the only path and this ADR is load-bearing rather than a convenience.

## Correction of emphasis, 2026-10-14 — video is the case that matters

This ADR built the measurement, the policy and the probe, and wired **none of it to video** — the
one case where the delay is actually visible. The owner said so plainly: *"I'm more interested in
the offset for video... I'm not as interested in an offset for music as that isn't as obvious."*
He is right, and the table above already said so without my acting on it: music has nothing to be
in sync with, so the link's 150 ms or more is inaudible, while lips running ahead of the words are
obvious within one sentence.

Worse, the consequence section below claimed the video occupant "gets the delay for free" via
`MediaPlayer.SetAudioDelay`. It got nothing: the occupant applied no delay at all, and the stored
number reached only the Stereo card, which displays it and ignores it by design.

**What changed.** `VideoSync` (in `DashDeck.Core.Audio`) owns the offset, and
`VideoStageOccupant` applies it and exposes it as three actions in the stage's action bar:
**SOUND EARLIER**, a readout, and **SOUND LATER**, at 50 ms a step.

**Measured is the starting point, not the answer.** The link delay is only part of the total — the
file's own container offset and the decoder's buffering are the rest, and they vary per file. So
the measurement *seeds* the offset and the person tunes it by eye while watching, which is what
every media player does and why the control earns its place. What they settle on is remembered,
and seeding happens **once**: someone who deliberately nudges it back to zero should not find the
measurement has overruled them at the next launch.

**Direction is the correctness question.** The link makes sound arrive late, so the correction
moves sound *earlier* relative to the picture — a negative offset, which a player achieves by
holding the picture back, since sound cannot be produced before it exists. **A positive offset
would make the error exactly twice as bad.** A test pins the sign for that reason.

**The buttons are labelled by effect, not by sign**, because "−150 ms" on a button tells nobody
which way it will go, and the readout says "picture held back 150 ms" for the same reason. That is
what someone can check against a screen.

**Reapplied after every `Play`, not once at startup.** LibVLCSharp's own documentation says the
audio delay resets to zero each time the media changes — so setting it once is a bug that works
perfectly on the first file and silently stops working on the second.

**Still unverified:** that negative is the direction LibVLC takes, on hardware. The reasoning and
the test agree, and the API's own docs do not state the sign. If the first nudge moves it the
wrong way, the fix is one character and the control is the thing that reveals it.

## Consequences

- **A number typed by hand is enough to ship.** Measurement can arrive later without changing
  anything above it; both write the same field.
- **The video occupant does not get the delay for free, and this originally claimed it did.**
  LibVLCSharp 3.10.1 exposes `MediaPlayer.SetAudioDelay(long microseconds)`, wrapping
  `libvlc_audio_set_delay`, and **its own documentation warns the delay resets to zero on every
  media change** — so it has to be reapplied per item rather than set once. Having the API
  available is not the same as calling it: for a day this ADR described a video fix that nothing
  performed. Wired 2026-10-14; see the correction above.
- **UI click sounds need a second output.** Today there is one player on Windows' default device
  (`WaveOutPlayer`, ADR-0057). Routing taps to the tablet's own speaker means naming a specific
  device rather than taking the default, which `waveOut` can do by index but identifies only by a
  truncated name (Q34).
- **Calibration needs the microphone raw.** Surface microphones run array processing, noise
  suppression and echo cancellation by default, and echo cancellation exists precisely to remove
  sound the machine just played — it may erase the probe. The capture has to ask for raw mode, and
  if that is refused the measurement has to detect it rather than report a wrong number. The
  confidence floor is what detects it.
- **One more thing to measure in the truck**, and it has to be done with the engine running, because
  a quiet cab is not the case that matters.
- **The cheap escape is still worth checking first.** If this truck has a working AUX input, a
  3.5 mm cable removes the delay instead of compensating for it, at the cost of a cable to plug in
  (Q33). The Bluetooth path is worth having either way — it is what Android Auto's audio uses, and
  the tablet comes in and out of the truck by hand every time (C5).

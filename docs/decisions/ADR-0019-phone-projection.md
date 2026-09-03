# ADR-0019 — Android Auto arrives through a dongle, not through us

**Status:** Accepted · 2026-09-02
**Amends:** [ADR-0007](ADR-0007-usb-link-and-adapter.md) — admits a **second** driver
exception, where that ADR said the FTDI serial driver was the only one.
**Resolves:** Q13, in part — the stage's largest unanswered occupant.
**Relates to:** [ADR-0012](ADR-0012-widgets-and-applets.md) (GPL at arm's length; publishing
this project) and [ADR-0005](ADR-0005-mock-first.md) (build against a synthetic device).

## Context

Q13 recorded that **Android Auto cannot fill the stage**: it is a phone-projection protocol
with no standalone mode and no Windows client, and the standalone product — Android Automotive
OS — is a head-unit operating system, not an app. That is still true, and Google licenses no
receiver SDK for a PC. Anything here is unofficial.

Two routes exist regardless.

**Be the head unit.** [`aasdk`/`openauto`](https://github.com/opencardev/aasdk) implement
Google's projection protocol — proprietary protobuf messaging, an H.264 stream and specific
audio channel routing. Alive again under OpenCarDev with commits into 2026, GPLv3, and a
Boost + libusb + protobuf + OpenSSL + Qt toolchain aimed squarely at the Raspberry Pi. Using
it from a WPF shell means running it as a separate process and reparenting its Qt window into
the stage.

**Let a dongle be the head unit.** The Carlinkit CPC200 family terminates Android Auto *and*
CarPlay in its own firmware and hands back H.264 and PCM over USB. The protocol it speaks to
its host is [documented by the community](https://github.com/ludwig-v/wireless-carplay-dongle-reverse-engineering):
a 16-byte little-endian header — magic, length, type, inverted type — and about a dozen
message classes, of which four matter.

An existing Raspberry Pi running `aa-proxy-rs` was considered and does not help. That tool is a
**relay**: it presents itself to a head unit as a phone on a USB gadget port so the phone can
be wireless. It terminates nothing, so there is no picture in it to forward. Making it a source
would mean running a head unit on it as well — at which point it consumes the session instead
of passing it on, and the truck's own SYNC 3 loses Android Auto. Trading away working factory
function is the thing ADR-0006 exists to prevent.

## Decision

**The dongle does the protocol. We speak to the dongle.**

A Carlinkit CPC200 is chosen. DashDeck implements its USB message protocol in C#, decodes the
H.264 through the LibVLC already deployed for the video occupant, renders inside WPF, and sends
touch back as fractions normalised to the dongle's 0–10000 grid.

**Hardware is chosen and not bought**, so this is built the way the vehicle stack was built
(ADR-0005): `IDongleTransport` is the seam, `SyntheticDongleTransport` is behind it today, and
`UsbDongleTransport` is a deliberate one-class gap. Framing, the session, the heartbeat, the
touch mapping and the stage occupant are finished and tested without a device.

**The synthetic dongle emits no video.** It answers the handshake and keeps the link alive, and
the stage says `LINKED, NO PICTURE`.

**ADR-0007 gains a second exception.** One more Microsoft-signed WinUSB binding, for a single
known VID/PID.

**The code stays in the public repository**, documented plainly as speaking an unofficial
protocol, with Android Auto and CarPlay acknowledged as their owners' trademarks.

## Reasoning

**The dongle route is smaller by an order of magnitude, and the difference is not effort — it
is what it touches.** Implementing Google's protocol means Boost, Qt, protobuf and OpenSSL
built for Windows off a Pi-focused fork, plus `SetParent`-ing somebody else's window into the
stage and forwarding touch across a process boundary. The dongle route is a framed byte stream
with four interesting message types and a decoder we already ship.

**It avoids reparenting, which this project has rejected once already.** Q18 chose to *host*
Nuvio rather than reparent it, and every occupant that renders into a child window has cost us
something — the launcher bar and the action bar (ADR-0018) both exist because a child window
draws over all WPF content whatever the z-order says. Decoding H.264 ourselves puts the picture
in the visual tree, where it can be themed, laid out and screenshotted like anything else.

**It avoids the GPL question rather than arguing it.** ADR-0012's rule is arm's length:
displaying Nuvio's page is fine, linking its code is not. A separate OpenAuto process is
probably defensible aggregation — but "probably defensible" is a poor foundation for a
repository about to be published. A clean-room C# client for a documented protocol has no such
question to answer.

**CarPlay comes free**, because the dongle terminates both. That was not the reason to choose
it and it is worth more than it cost.

**The second driver exception is admitted rather than eroded.** ADR-0007 said the FTDI driver
was the single exception, and the honest way to add another is a decision record that names it,
not a quiet second install. The shape is identical: Microsoft-signed, in-box, one known device,
no service and no registry beyond the binding itself. C1's promise — uninstalling is deleting a
folder — is unchanged.

**Publishing it openly matches what the reverse-engineering projects themselves do.** The
alternative, a private plugin, would mean the published project could not build its own stage.

## Alternatives

- **Embed OpenAuto via `HwndHost`.** Rejected above: Windows toolchain, reparenting, GPL.
- **Repurpose the existing Pi as a head unit and stream to the Surface.** Attractive — no
  driver on the Surface, GPL entirely on the Pi, no hardware to buy — and rejected because that
  Pi is currently giving the factory head unit wireless Android Auto, and it cannot both consume
  the session and relay it. A *second* Pi would work and costs more than the dongle.
- **`scrcpy` phone mirroring.** Apache-2.0, no driver, works today. Rejected as the primary
  answer because it projects the phone's own UI rather than a driving one, but it remains the
  cheapest fallback if the dongle disappoints.
- **Google's Desktop Head Unit.** Official, Windows-native, and explicitly a developer testing
  tool requiring the phone in developer mode. Rejected as a production dependency.
- **Do nothing; leave Q13 answered "no".** Defensible, and what the outline said for a year.

## Consequences

- **Nothing projects until a dongle is bought.** The stage occupant exists, is honest about why
  it is empty, and every layer above the transport is already exercised.
- **`UsbDongleTransport` is a visible stub.** It reports no device rather than guessing at
  endpoint numbers. A guess that compiles would be worse than a gap that does not.
- **The H.264 path is not written.** LibVLC will decode it, but feeding a live elementary
  stream into a `MediaPlayer` is real work and untestable without frames to feed it.
- **Audio routing is unresolved.** The dongle sends PCM with a routing field, and C3 forbids
  taking over the truck's audio. Where projected audio goes is a question this ADR does not
  answer.
- **Two unofficial protocols now ship in a public repository** — this and, in a sense, the
  Ford PIDs to come. That is a posture, deliberately taken, and it is the strongest remaining
  argument for ADR-0012's rejected "stay private" option.
- **A second driver exception makes a third easier to argue for.** ADR-0007's "single
  exception" was doing real work as a phrase. It now needs the discipline of this file instead.

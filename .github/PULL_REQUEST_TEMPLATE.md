## What and why

<!-- What does this change, from the driver's seat? Link the issue: Closes #… -->

## How it was tested

<!-- Tests added? Run on the synthetic truck? If it's a touch gesture, was it tried on glass? -->

## How to test in the truck

<!-- Required for anything that touches the vehicle, the adapter, or the screen in the cab.
     The same walkthrough as the new section in docs/08-in-vehicle-testing.md: what you need
     (ignition off / on / running, internet, parked), numbered steps each with what you should
     see, and what a failure looks like. "Not tested on hardware" is not a walkthrough. -->

## Checklist

- [ ] Targets `develop` (not `main`)
- [ ] In-vehicle walkthrough added to `docs/08-in-vehicle-testing.md` (or this change can't be seen in the truck)
- [ ] Build is clean (warnings are errors) and `dotnet test DashDeck.slnx` passes
- [ ] **Read-only:** nothing writes to the vehicle
- [ ] Components use named signals only; any new PID is in the catalog JSON, not guessed
- [ ] Signal quality (`Live` / `Stale` / `Unavailable` / `Simulated`) is shown, not hidden
- [ ] Time comes from `IClock`, never `DateTime.Now`
- [ ] No registry writes, services, new drivers, or kiosk behaviour
- [ ] Architectural change? The ADR is in this PR. New dependency? `THIRD-PARTY-NOTICES.md` is updated
- [ ] Any new asset (image, font, sound) is one I have the right to publish
- [ ] `DashDeck.Abstractions` changed? `apiVersion` is bumped as ADR-0008 describes

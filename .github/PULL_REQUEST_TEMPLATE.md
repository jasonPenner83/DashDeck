## What and why

<!-- What does this change, from the driver's seat? Link the issue: Closes #… -->

## How it was tested

<!-- Tests added? Run on the synthetic truck? If it's a touch gesture, was it tried on glass? -->

## Checklist

- [ ] Targets `develop` (not `main`)
- [ ] Build is clean (warnings are errors) and `dotnet test DashDeck.slnx` passes
- [ ] **Read-only:** nothing writes to the vehicle
- [ ] Components use named signals only; any new PID is in the catalog JSON, not guessed
- [ ] Signal quality (`Live` / `Stale` / `Unavailable` / `Simulated`) is shown, not hidden
- [ ] Time comes from `IClock`, never `DateTime.Now`
- [ ] No registry writes, services, new drivers, or kiosk behaviour
- [ ] Architectural change? The ADR is in this PR. New dependency? `THIRD-PARTY-NOTICES.md` is updated
- [ ] Any new asset (image, font, sound) is one I have the right to publish
- [ ] `DashDeck.Abstractions` changed? `apiVersion` is bumped as ADR-0008 describes

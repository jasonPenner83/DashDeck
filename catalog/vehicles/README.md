# Vehicle files

**Empty on purpose.** DashDeck ships nothing specific to a vehicle (ADR-0052, [DISCLAIMER](../../DISCLAIMER.md)).

Your vehicle's file — its signals, module names, the identity its modules answer, the identifier
ranges worth sweeping and the measured rate of the bus on OBD pins 3/11 — goes in

    %LOCALAPPDATA%\DashDeck\vehicles\

on each machine that runs DashDeck, the ID hunter or the ID matcher. The format is in
[`catalog/README.md`](../README.md#vehicle-files-adr-0033-adr-0052). Without one, DashDeck reads the
standard OBD-II set, asks module scans the ISO 14229 identity question, and sends nothing on pins
3/11 of a real vehicle.

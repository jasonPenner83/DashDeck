# ADR-0081 — Calculated signals, and instant economy estimated by the dash

**Status:** Proposed · 2026-11-02
**Builds on:** [ADR-0041](ADR-0041-console-dash.md) (economy on the console),
[ADR-0004](ADR-0004-request-arbiter.md) (the arbiter), [ADR-0052](ADR-0052-nothing-vehicle-specific-shipped.md)
(placeholders), [ADR-0033](ADR-0033-vin-lookup-and-vehicle-packs.md) (the VIN decode).
**Keeps:** read-only towards the vehicle; nothing vehicle-specific shipped.

## Context

The owner wanted instant fuel economy. It was a placeholder (`fuel.economy`) waiting for the truck's
own identifier. Then they found out three things:
- the truck's cluster shows no instant economy, so there is no instant economy figure on the truck
  to find;
- the 2.7 EcoBoost answers neither mass airflow (`01 10`) nor fuel rate (`01 5E`): its supported-PID
  bitmaps leave both out;
- Infocar shows instant economy anyway. A tap log of it asked only standard mode 01 PIDs: manifold
  pressure, intake air temperature, RPM, speed, load, trims. It was working the number out itself,
  reading 0 while stopped.

The owner asked for the same, with litres per hour while stationary.

## Decision

1. **A catalog entry can be calculated:** `"calculate": "<name>"` in place of a request. It has no
   request. A calculation this build does not know is refused at load.
2. **Asking for one asks for its inputs.** When something declares a calculated signal, the arbiter
   plans its inputs too. Each input gets the same priority, at the declared rate or the input's own
   default rate, whichever is lower; intake temperature, for one, does not need 1 Hz. The calculated
   entry costs no budget. Withdrawing it withdraws them. An input missing from the catalog is not
   asked for.
3. **The polling loop works it out on its turn** from the bus's latest readings.
   - Its quality is the least trustworthy of its required inputs: Stale over Simulated over Live.
     A simulated input never makes it Live.
   - A missing required input makes it Unavailable, never a zero.
   - Its timestamp is the newest required input's.
4. **Two calculations ship**, pure in Core (`Calculations`):
   - **`fuelFlow`**, L/h, by speed density:
     - air density from manifold pressure and intake temperature, times the swept volume per
       second (displacement × RPM ÷ 120), times a volumetric efficiency of 0.85;
     - fuel is that air over 14.7 × commanded λ (`01 44`, when answered), raised by the fuel trims
       (short plus long, averaged over the banks that answered);
     - converted at 745 g/L, times the person's correction;
     - commanded λ above 1.5 is the fuel cut while coasting, and reads 0. RPM 0 reads 0.
   - **`fuelEconomy`**, from the same flow and `vehicle.speed`:
     - **L/100km** at 3 km/h and over, capped at 99.9;
     - **L/h below 3 km/h**. The reading's unit says which, so a card shows the unit the reading
       carries. A layout that wrote `L/100km` shows `L/h` when the reading does
       (`Calculations.UnitShown`), and no other layout unit is touched.
5. **The engine size comes from the VIN decode** (Settings ▸ Vehicle), read at launch. Without one
   there is no estimate: Unavailable, not a guess.
6. **A correction, 0.5–1.5**, in Settings ▸ Vehicle ▸ FUEL ESTIMATE CORRECTION. It applies straight
   away. It is tuned against the truck's own average economy over a steady stretch, which the truck
   measures from its injectors.
7. **Catalog:**
   - `fuel.economy` becomes `"calculate": "fuelEconomy"`, named *Fuel Economy — Instant (estimated)*.
   - `fuel.flowEstimate` is added, `"calculate": "fuelFlow"`.
   - `fuel.commandedLambda` (`01 44`) is added. It has no unit in the catalog: a DBC is Latin-1 and
     cannot hold λ, and the example database says what the catalog says.
   - Bank 2's trims stay out of the shipped catalog, as ADR-0032 keeps them, so a desk scan has
     something to find. The estimate uses them once a scan adds them.
   - `fuel.range` stays a placeholder.

## Consequences

- Instant economy on the console and in cards, on this truck and any petrol engine that answers MAP,
  intake temperature and RPM. It is an estimate, and every place it is named says so.
- Accuracy depends on how far the engine is from the fixed volumetric efficiency: least under light
  load, most under boost. Commanded λ catches boost enrichment and the fuel cut. The correction
  takes out the steady part of the error.
- Stopped, the console reads L/h, about 2 at idle on this truck: 40 kPa, 36 °C and 674 rpm from the
  tap log give 1.9 L/h, and 2.0 L/h with that log's trims.
- The ID matcher no longer lists `fuel.economy` as needing an ID. Settings ▸ Sensors says what it is
  worked out from.
- Asking for economy adds about four requests a second at 1 Hz where nothing else asks for its
  inputs. RPM and speed are usually asked already.

## Not done

- Average economy or fuel used over a trip: they need state carried between readings.
- Diesel. 14.7 and 745 g/L are petrol's; the fuel type from the VIN is not yet read.
- A volumetric efficiency that varies with RPM and load.
- Range from the estimate.

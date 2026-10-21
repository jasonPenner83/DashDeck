# Signal catalog

Signals are **data, not code**. Adding one is a JSON edit; it never requires a rebuild.

That is deliberate. Ford's interesting values — transmission temperature, per-wheel TPMS
pressures, real coolant, odometer — are not in the legislated OBD-II set and are largely
undocumented. They will be found by trial and error against the truck during P1.5, and
discovering one must cost a config edit rather than a code change.

## Files

| File | Contents |
|---|---|
| `signals.obd2-standard.json` | Legislated OBD-II mode 01 PIDs. Should work on any modern vehicle, not just the F-150. |
| `reference/sae-j1979-mode01.json` | The SAE J1979 mode 01 table — names and formulas Settings ▸ Sensors uses to describe a PID a scan finds. A reference, never polled. |
| `reference/iso-diagnostics.json` | What ISO 15765-4 / ISO 14229 define for the module sweep: the engine and transmission ids, the identity question (`F187`), the identification range. |
| `vehicles/` | **Empty on purpose.** Vehicle files are yours — see below. |

**The repository ships nothing specific to a vehicle** (ADR-0052, [DISCLAIMER](../DISCLAIMER.md)):
only what the public standards define, and placeholders with no identifier. No identifier, module
address, bus rate or scaling for any manufacturer is compiled in or shipped. Those go in **your own
vehicle file**, in `%LOCALAPPDATA%\DashDeck\vehicles\`, and your overlay, `signals.user.json` —
on your machines, out of the repository.

## Vehicle files (ADR-0033, ADR-0052)

A vehicle file ("pack") holds a manufacturer's own signals for one kind of vehicle, laid over the
standard set when the decoded VIN matches. **It lives in `%LOCALAPPDATA%\DashDeck\vehicles\`**,
not here: the dash, the ID hunter and the ID matcher all read that folder. A file there with the
same name as one in `catalog/vehicles/` replaces it.

```jsonc
{
  "name": "My truck",
  "match": {                      // every field given must match the decoded vehicle
    "make": "Example",            // required — a pack with no make would fit everything
    "model": "X-100",             // case, spaces and dashes ignored: X100 = X-100
    "yearMin": 2018,
    "yearMax": 2020,
    "displacementLitres": 2.7     // to a tenth of a litre
  },
  "pins311BitRate": 500000,       // the bus on OBD pins 3/11, MEASURED by listening (ADR-0044).
                                  // Absent: nothing is ever sent on pins 3/11 of a real vehicle.
  "modules": {                    // likely names for module addresses, for the module sweep
    "7A0": "Example module"       // labels only; the screen says "likely" (ADR-0035)
  },
  "identityDid": "F187",          // what SCAN FOR MODULES asks each address; ISO 14229's F187 if absent
  "identifierRanges": [           // offered for a module's identifier sweep, after F100–F1FF
    { "name": "1000–1FFF", "from": "1000", "to": "1FFF" }
  ],
  "signals": [ /* definitions, in the format below */ ]
}
```

The order at launch is **standard → matching pack → your own overlay**. Which vehicle it is comes
from **Settings ▸ Vehicle**: the VIN, read from the truck or typed, decoded once by NHTSA and
cached. A pack holds only signals confirmed on a real vehicle with TEST — an empty pack is
correct; a guessed PID is not.

## Your own signals (ADR-0032)

Signals found on the truck, and corrections to shipped ones, are made on the tablet in
**Settings ▸ Sensors** — never by editing these files there. They are written to
`%LOCALAPPDATA%\DashDeck\signals.user.json`, in exactly this format, and laid over the shipped
catalog at launch: a user definition with a shipped `id` replaces it, a new `id` adds one. A
deploy never touches that file. The merged catalog is validated as one; if it is invalid the
whole overlay is dropped, the shipped catalog runs alone, and the Sensors section says why.

The same section has **SCAN THE TRUCK**, which asks the supported-PID bitmaps (mode 01 PIDs `00`,
`20`, `40` …) and lists what the truck supports that no file defines, and what a file defines that
the truck does not support; and **TEST**, which sends one request and shows the raw bytes and what
the formula makes of them before anything is saved. **SCAN FOR MODULES** asks every module address
on both buses for its part number, the way FORScan lists modules, and a found module's
**identifiers** can be swept a range at a time; one that answers opens the editor with its module,
bus and mode 22 filled in (ADR-0035). Once a user definition has proved itself on
the truck, promote it by copying it into the right file here.

## From your own CAN database (ADR-0073, ADR-0074)

Instead of a request and a decode, an entry can point into a CAN database (`.dbc`) of your own in
`%LOCALAPPDATA%\DashDeck\dbc\`. The entry then gives only how the dash uses the value; the database
gives the bus, mode, PID, module, decode, unit and states:

```jsonc
{
  "id": "ford.oilTemp", "name": "Oil temperature", "category": "Engine",
  "defaultRateHz": 1, "min": -40, "max": 160,
  "dbc": { "file": "truck-hs.dbc", "message": "Module_7E0", "signal": "EngOilTemp" }
}
```

- `file` is a file name in that folder, nothing more; `message` is the module's answer message
  (`Module_7E0` for a value `7E0` answers on `7E8`); `signal` is the value in it. How a database
  holds answers is in [ADR-0073](../docs/decisions/ADR-0073-our-own-can-database.md).
- Leave out `bus`, `mode`, `pid`, `module`, `decode`, `states` and `placeholder` — an entry with a
  reference and any of them is refused. `min`/`max` stay yours: the database's range is not used.
- A reference the database cannot answer (no file, no such signal, no `DashDeckBus` attribute, a
  layout the catalog cannot decode yet) loads as a **placeholder**: nothing is asked, and Settings ▸
  Vehicle says which and why.
- Saving from Settings ▸ Sensors or the ID matcher writes the reference back alone. Changing the
  request or decode there detaches the signal from the database and saves the whole definition here.

`docs/examples/obd2-standard.dbc` is the standard set below as such a database, and
`docs/examples/dbc-test-vehicle.json` a vehicle file reading three of its values through it.

## Fields

```jsonc
{
  "id": "vehicle.speed",        // what components subscribe to; stable forever once used
  "name": "Vehicle Speed",      // human label
  "bus": "Hs",                  // Hs (pins 6/14, 500k) or Ms (pins 3/11, 125k)
  "mode": 1,                    // OBD-II service, defaults to 01
  "pid": 13,                    // decimal in JSON — 13 is 0x0D
  "module": null,               // hex text, e.g. "726": ask that module by address (ADR-0035).
                                // Left out, the request goes to the broadcast (7DF), which is
                                // what every standard mode 01 PID wants.
  "decode": {
    "byteOffset": 0,            // index into the payload, after the echoed mode and PID
    "byteLength": 1,            // 1, 2 or 4, big-endian
    "signed": false,
    "scale": 1,                 // value = (raw * scale) + offset
    "offset": 0,                // temperatures use -40 here
    "unit": "km/h"              // travels with every reading; never assumed by a component
  },
  "defaultRateHz": 4,           // used when a component does not ask for a rate
  "stalenessSeconds": null,     // defaults to five poll intervals
  "placeholder": false,         // true: its identifier is still to be found — leave out bus, mode and
                                // pid; nothing is ever asked for it (Unavailable on a real vehicle, the
                                // synthetic truck's value by id); the ID matcher lists it as NEEDS ID
                                // (ADR-0050, ADR-0052)
  "hidden": false,              // true (in your overlay): left out of the card editor's picker, still
                                // working for anything already using it (ADR-0051)
  "unconfirmed": false,         // true: typed by hand; not offered until TEST answers it (ADR-0051)
  "min": 0,                     // decoded values outside the range are rejected, not shown
  "max": 255,
  "dbc": null                   // or { "file", "message", "signal" }: where and how come from your CAN
                                // database instead, and bus/mode/pid/module/decode/states are left out
                                // (ADR-0074; see above)
}
```

`min`/`max` are a correctness guard, not decoration. A wrong `decode` spec or a corrupt
response usually produces a wildly out-of-range number, and publishing it would put a
plausible-looking wrong value on a dash. Out-of-range readings are dropped instead.

## Note on PID support

Not every vehicle answers every standard PID. `engine.fuelRate` (0x5E) in particular may
not be supported on the 2019 F-150 — open question Q4. When the vehicle replies `NO DATA`,
the polling loop marks the signal unsupported and stops asking, because on a tight request
budget, polling a signal that will never answer spends real capacity on nothing.

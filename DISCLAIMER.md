# Disclaimer — use at your own risk

DashDeck is a hobby project, published so others can learn from it and build on it. **Everything in
this repository is provided "as is", without warranty of any kind, and you use it entirely at your
own risk.** The [MIT License](LICENSE) says the same in legal terms, and it governs; this page says
it plainly and adds what is specific to software that talks to a vehicle.

## Connecting software to a vehicle

- **Your vehicle, your responsibility.** Plugging an adapter into the OBD-II port and sending
  requests over it can, in rare cases, upset a module, drain the battery, set fault codes, or
  interfere with a vehicle in ways nobody predicted. The authors and contributors accept no
  liability for any damage, loss, injury, fault, repair cost, voided warranty or failed inspection
  that follows from using this software, its tools, or anything derived from them.
- **Read-only is a design rule, not a guarantee.** DashDeck is built never to write to a vehicle
  (ADR-0006), and its discovery tools only ask for data. A request the vehicle was not designed to
  receive is still a message on its network, and bugs happen.
- **Nothing here is vehicle-specific.** The repository ships only what public standards define —
  SAE J1979 (OBD-II) and ISO 15765 / ISO 14229 — plus a synthetic vehicle whose identifiers are
  invented. Manufacturer-specific identifiers, module lists and bus rates live in files **you**
  create on your own machine (`%LOCALAPPDATA%\DashDeck\vehicles\` and `signals.user.json`). What
  you put there, and what it asks your vehicle, is yours to verify.
- **Values can be wrong.** A reading on the screen can be stale, mis-scaled or simply wrong. Never
  rely on DashDeck for anything that matters to safety — speed, temperatures, warning lights,
  tyre pressures. The vehicle's own instruments are the authority.

## On the road

- **Do not operate it while driving.** Set it up parked. Looking at or touching a screen while
  moving is dangerous and, in many places, illegal. Mount any device so it does not block your
  view, an airbag, or a control, and follow the laws where you drive.

## Not affiliated

DashDeck is independent. It is not affiliated with, endorsed by or supported by Ford Motor
Company, Microsoft, OBDLink / ScanTool.net, FORScan, Carlinkit, or any vehicle or equipment
maker. Their names appear only to describe compatibility, and their trademarks belong to them.

## Your data

A vehicle's answers can include its VIN. DashDeck keeps such data on your own device, and the
repository must never hold one. If you share logs, captures or files from your vehicle, check
them first.

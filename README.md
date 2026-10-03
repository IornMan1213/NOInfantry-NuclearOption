# NO Infantry: combined arms on foot for Nuclear Option

A BepInEx mod that lets you leave the cockpit and keep fighting as a soldier, then commandeer friendly
ground vehicles and ships, shoot down aircraft with a shoulder launcher, and walk back to an airbase to
take off again.

> **Multiplayer:** works when the **server (or host) and every player who wants to fight on foot** have the mod.
> Players without it can still join and play normally. On a server without the mod, infantry mode stays off.

## Install
1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (x64) into the Nuclear Option folder and run the game once.
2. Copy the `NOInfantry` folder (containing `NOInfantry.dll` and `models/`) into `Nuclear Option/BepInEx/plugins/`.
3. Optional: install [ConfigurationManager](https://github.com/BepInEx/BepInEx.ConfigurationManager) and press F1 in game to tweak every setting.

## How to play
| From | Do | Result |
|---|---|---|
| Aircraft, stopped on the ground | **F8** | climb out and continue on foot |
| Aircraft, in flight | eject as usual | land by parachute, then you're on foot |
| On foot, next to a friendly vehicle / on a friendly ship | **E** | drive it, or take the ship's helm |
| On foot, at a friendly airbase | **B** | open the aircraft selection menu and take off again |
| Any time on foot | **F7** | switch between first person and the game camera |

### On foot
- WASD move, Shift sprint, Space jump.
- **1** rifle: LMB fire, RMB aim down the iron sights, R reload.
- **2** launcher: hold RMB on an enemy near the reticle to lock, then LMB to fire. It uses an anti-air
  missile against aircraft and an anti-surface missile against vehicles and ships.
- **G** frag grenade.
- Enemy guns will shoot back (configurable).

### In a vehicle or ship
WASD drive, mouse aims the turrets (the green mark shows where the barrel actually points), LMB fires the guns,
RMB zooms, E gets out.

## How it works (for modders)
- The ejected pilot (`PilotDismounted`) is reused as the soldier; its grounded logic is replaced while controlled.
- Rifle hits and grenades go through the game's own damage model (`DamageEffects.ArmorPenetrate` / `BlastFrag`).
- Ground vehicles: inputs written into their job data with `mobile = false`. Ships: `ShipAI` disabled and
  `ShipInputs` driven. Turrets aimed through their private `AimTurret`.
- Launcher missiles are the game's own (`Encyclopedia.weaponMounts`), spawned with `Spawner.SpawnMissile`.
- Back to the air via the game's `AircraftSelectionMenu`.
- See `MODLOG.md` for the full notes and gotchas.

## Credits
- Rifle models: "Ultimate Gun Pack" by **Quaternius**, CC0 ([OpenGameArt](https://opengameart.org/content/low-poly-guns-pack)).
- Sounds and effects are borrowed at runtime from the game's own assets; no game files are shipped.
- Built with AI assistance (Claude Code).

## Multiplayer
- The host / dedicated server must run the mod; clients that want infantry need it too.
- Your client simulates your own soldier and streams it to the server (smooth movement for you).
- Everything that affects other units runs on the server, which checks you own the soldier: rifle damage,
  grenades, launcher missiles (the game's own networked missiles), driving vehicles and ships, returning to base.
- Other modded players see your tracers, grenades and turret aim; unmodded players see the soldier, vehicles,
  missiles and gunfire through the game's normal sync.
- Handshake: on joining, a modded client sends one hello; an unmodded server ignores it, so nothing changes there.

## Automated in-game test
The mod contains a self-test that drives the soldier through every feature without touching your
mouse or keyboard, then quits:

```
NuclearOption.exe -autoHost -state SinglePlayer -mission "Free Flight - Ignus Archipelago" -noiAutotest -screen-fullscreen 0
```

Results go to `BepInEx/NOInfantry_autotest.txt` and screenshots to `BepInEx/NOInfantry_shots/`.
It covers: spawn on foot, walk/sprint/jump, rifle hits, grenade, launcher vs ground and air targets
(lock, launch, damage), driving and firing a ground vehicle, a ship's helm (throttle, steering direction,
leaving the helm), the airbase aircraft menu, the hand-off when a new aircraft spawns, and F8 dismount.
Current result: 20/20 passing (v0.5.0).

Multiplayer self-test (two instances on one PC over UDP; run the host first):
```
NuclearOption.exe -autoHost -socket UDP -port 7777 -state Multiplayer -mission "Free Flight - Ignus Archipelago" -noiAutotest -noiRole host -screen-fullscreen 0
NuclearOption.exe -autoConnect 127.0.0.1 -socket UDP -port 7777 -noiAutotest -noiRole client -screen-fullscreen 0
```
Reports: `BepInEx/NOInfantry_mptest_host.txt` and `..._client.txt`. Current result: client 17/17, host 9/9.

## Build
`dotnet build -c Release` (edit `NuclearOptionPath` in `NOInfantry.csproj`). The build copies the DLL and
`models/` into the game's plugins folder.

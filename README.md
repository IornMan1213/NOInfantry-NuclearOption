# NO Infantry: combined arms on foot for Nuclear Option

> ## ⚠️ Canceled until further notice
> Development of this mod is paused indefinitely. The code and the last build (v0.8.0) are published as-is;
> there is no support and no further updates are planned for now. See *Known issues* below.

A BepInEx mod that turns Nuclear Option into a combined-arms war that starts on the ground: deploy on foot
with a kit, lead an AI fireteam, capture airbases with infantry alongside a living AI front line, pull a
vehicle from the motor pool, commandeer ships, shoot down aircraft with a shoulder launcher, and take off
from the airbases you've won.

> **Multiplayer:** works when the **server (or host) and every player who wants to fight on foot** have the mod.
> Players without it can still join and play normally. On a server without the mod, infantry mode stays off.

## Install
1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (x64) into the Nuclear Option folder and run the game once.
2. Copy the `NOInfantry` folder (containing `NOInfantry.dll` and `models/`) into `Nuclear Option/BepInEx/plugins/`.
3. Optional: install [ConfigurationManager](https://github.com/BepInEx/BepInEx.ConfigurationManager) and press F1 in game to tweak every setting.

## Ground War (infantry first)
- **Deploy on foot:** the aircraft selection menu has a **DEPLOY ON FOOT** panel (right side). Pick a kit:
  - **Rifleman**: rifle, 6 grenades.
  - **Anti-Tank**: rifle, 2 grenades, guided anti-surface rockets.
  - **Anti-Air**: rifle, 2 grenades, heat-seeking anti-air missiles.
  - **Medic**: rifle, 2 grenades, 6 bandages (**F** heals the friend in front of you, or yourself); revives twice as fast.
- **Respawn:** when your soldier dies the map opens; click a friendly airbase and deploy again (or fly).
- **Rally point (Y):** place a forward spawn where you stand (not within 150 m of enemies, every 90 s). The deploy
  panel then lets you choose Airbase or Rally. Enemies walking onto it destroy it.
- **Downed, not dead:** a lethal hit puts a soldier on foot (you, other players, AI) down for 45 s instead.
  Hold **F** next to a downed friend to revive them (6 s, medics 3 s); AI riflemen come and revive you.
  Downed friends show as red crosses. Hold **Space** while down to give up and redeploy. Big explosions still kill outright.
- **Capture on foot:** every soldier (yours, your fireteam's, the AI front line's) counts toward capturing
  an airbase while inside its capture radius. A bar at the top of the screen shows the owner and progress.
- **Fireteam orders (T, then 1-5):** Follow me / Hold here / Move there (where you look) / Free roam
  (they go and take the next airbase) / Disband. If you die, your riflemen carry on by themselves.
- **AI front line:** every couple of minutes each side sends a squad at the nearest airbase it doesn't
  hold, and garrisons its own bases when enemy soldiers get in. Squads arrive on foot a short walk outside
  the objective. Tune or switch off under `Ground War` in the config. (Troop trucks exist behind
  `Ground War -> TruckTransport` but are experimental and off: see Known issues.)
- **Motor pool (V, then 1-9):** at a friendly airbase, request a ground vehicle next to you, paid from your
  funds (cost and cooldown configurable). Then **E** to drive it to the front.

## How to play
| From | Do | Result |
|---|---|---|
| Spawn menu (aircraft selection) | **DEPLOY ON FOOT** panel | start on foot at that airbase with the chosen kit |
| Aircraft, stopped on the ground | **F8** | climb out and continue on foot |
| Aircraft, in flight | eject as usual | land by parachute, then you're on foot |
| On foot, next to a friendly vehicle / on a friendly ship | **E** | drive it, or take the ship's helm |
| On foot, at a friendly airbase | **B** | open the aircraft selection menu and take off again (or redeploy with another kit) |
| On foot, at a friendly airbase | **V** | motor pool: pick a vehicle with 1-9 |
| On foot, with a fireteam | **T** | orders menu: 1 follow, 2 hold, 3 move there, 4 free roam, 5 disband |
| On foot | **Y** | place your rally point (forward spawn) |
| Next to a downed friend | hold **F** | revive (medics: tap F to bandage) |
| Any time on foot | **F7** | switch between first person and the game camera |

### On foot
- WASD move, Shift sprint, Space jump, **C** crouch, **Z** prone (lower, slower, steadier aim, quieter footsteps).
- **1** rifle: LMB fire, RMB aim down the iron sights, R reload.
- **2** launcher: hold RMB on an enemy near the reticle to lock, then LMB to fire. Anti-Tank kits lock
  vehicles and ships, Anti-Air kits lock aircraft; an ejected pilot's launcher does both.
- **G** frag grenade.
- **H** call a fireteam: AI riflemen who follow you in a wedge, keep up when you run, and engage enemy soldiers
  and soft vehicles they can see (they won't shoot through friendlies).
- **J** send an enemy patrol at you (sandbox). Optional automatic patrols: `Squads -> AutoEnemyPatrols`.
- Enemy guns, vehicles and AI soldiers shoot back (all configurable).

### In a vehicle or ship
WASD drive, mouse aims the turrets (the green mark shows where the barrel actually points), LMB fires the guns,
RMB zooms, E gets out.

## Known issues (v0.8.0)
- **Troop trucks** (`TruckTransport`, off by default): the truck is wrecked within ~2 s of spawning in testing
  (it ends up below the terrain). The squads then bail out and continue on foot.
- **Map after death:** the map is opened by the mod 4 s after you die; in one automated scenario (dying right
  after redeploying as a medic) it did not open. Open the map yourself if that happens.
- Some mod managers sync the plugins folder and remove `NOInfantry` if it isn't registered with them.
- Heavy plugin sets slow loading a lot; the game may stutter for the first minute of a mission.

## How it works (for modders)
- The ejected pilot (`PilotDismounted`) is reused as the soldier; its grounded logic is replaced while controlled.
- Rifle hits and grenades go through the game's own damage model (`DamageEffects.ArmorPenetrate` / `BlastFrag`).
- Ground vehicles: inputs written into their job data with `mobile = false`. Ships: `ShipAI` disabled and
  `ShipInputs` driven. Turrets aimed through their private `AimTurret`.
- Launcher missiles are the game's own (`Encyclopedia.weaponMounts`), spawned with `Spawner.SpawnMissile`.
- Back to the air via the game's `AircraftSelectionMenu`; deploying on foot spawns a `PilotDismounted` owned by
  the player (`ServerObjectManager.Spawn(go, player.Owner)`) with no parent aircraft.
- Capture: soldiers get `Unit.ModifyCaptureStrength`, which the game's own `Capture` component sums per faction.
- AI soldiers are `PilotDismounted` units driven by the mod on the server (`AiSquads.cs`).
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
v0.8.0 results: single-player 43/47, ground-war step 19/21 (the failures are the issues above).
v0.7.0 result: 39/39 passing, including stances, an AI firefight and the Ground War step
(deploy on foot with a kit, capture strength, hold/move/follow orders, motor pool, AI front line, death -> map).
Add `-noiGroundWarOnly` to run just the Ground War step.

Multiplayer self-test (two instances on one PC over UDP; run the host first):
```
NuclearOption.exe -autoHost -socket UDP -port 7777 -state Multiplayer -mission "Free Flight - Ignus Archipelago" -noiAutotest -noiRole host -screen-fullscreen 0
NuclearOption.exe -autoConnect 127.0.0.1 -socket UDP -port 7777 -noiAutotest -noiRole client -screen-fullscreen 0
```
Reports: `BepInEx/NOInfantry_mptest_host.txt` and `..._client.txt`. The client starts like a player
(aircraft at the airbase, then F8), and finishes by deploying on foot, using the motor pool and ordering its
fireteam to hold. Current result: client 23/23, host 12/12 (v0.7.0).

## Build
`dotnet build -c Release` (edit `NuclearOptionPath` in `NOInfantry.csproj`). The build copies the DLL and
`models/` into the game's plugins folder.

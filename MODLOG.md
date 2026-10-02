# NOInfantry — MODLOG

Goal: first-person infantry combat in Nuclear Option. Done (v0.1) = after ejecting and landing,
the player walks/sprints/jumps in first person and shoots a rifle that damages real units, verified in game.

## Environment (2026-10-02)
- Game: `C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option` (Steam, Unity 2022.3.6, Mono)
- Assembly-CSharp.dll mtime 2026-08-14; decompile at `~/nuclearoption-decomp` (2026-10-01, current)
- Loader: BepInEx 5.4.23.5 already installed. Other plugins present: ConfigurationManager, Blueprinter 2.0.1, MiG-29.
- Log: `<game>\BepInEx\LogOutput.log`
- Toolchain: dotnet SDK 10, ilspycmd.
- Multiplayer: Mirage networking. v0.1 is **single-player / host only** (pilot sim is server-authoritative: `SetLocalSim(IsServer)`).

## Route
BepInEx plugin + Harmony. No loader API exists for this; the ejected pilot (`PilotDismounted : Unit, IDamageable`)
is already a networked ground body with Rigidbody, HP (100), armor, a run animation (`RunSpeed`, layer 1 weight).
We hijack it rather than spawning a new unit type (no prefab/network registration needed).

## Game facts (from decompile)
- `PilotDismounted.LocalFixedUpdate()` (private): ground spring (hover 1.3 m), velocity damping, water buoyancy,
  `TooMuchForce()` death, `CheckLanded()` → capture check + `NetworkisKinematic = true` after 35 s landed.
  → Prefix-skip it while controlled; we do our own movement.
- `PilotDismounted.Capture(Unit)` (public, [Server]) destroys the pilot when a friendly/enemy unit within 500 m captures. → Prefix-skip while controlled.
- `CheckForCapture()` also returns pilot to base if `NetworkHQ.AnyNearAirbase` 3 s after landing — NOT blocked yet (known issue).
- `Update()` sets state `parachuting` if chute open & radarAlt > 10 — running off a cliff re-chutes (harmless).
- `Player.RemovePilotDismounted` is called when capture-check starts (on landing) → `GameManager.GetLocalPilotDismounted` goes null after landing. Cache the pilot while descending; fallback = camera's `followingUnit`.
- Camera: `CameraStateManager` (SceneSingleton `.i`), `SwitchState(CameraBaseState)`; abstract `CameraBaseState` has Enter/Leave/UpdateState(LateUpdate)/FixedUpdateState. Subclass it for FPS cam.
- Cursor: `CursorManager` locks cursor when no UI flags → gate input on `Cursor.lockState == Locked`.
- Damage: host path = `DamageEffects.ArmorPenetrate(pos, velocity, muzzleVel, pierce, blast, dealerID)`; it linecasts from `pos` along `velocity*0.1`, so start slightly before the surface. Calls `IDamageable.TakeDamage`.
- Pilot damage: `(pierce - armor.pierceArmor)/armor.pierceTolerance` off 100 HP.
- Input: Rewired (`GameManager.playerInput`), legacy `Input` also works (game uses `Input.GetKey(KeyCode.LeftShift)`).

## Log
- 2026-10-02: recon, design, v0.1 scaffold written; builds clean (dotnet build -c Release -o bin/out).
- 2026-10-02: build now auto-deploys to `<game>\BepInEx\plugins\NOInfantry\` (user asked for this).
  Added: borrowed gun sounds + impact FX (smallest-pierce `Gun` prefab via Resources.FindObjectsOfTypeAll, private
  fields `fireSounds`, `impactEffectGround/Armor/Water`; pooled via `ParticleEffectManager.GetPrefabEffect`),
  grenades (G; `DamageEffects.BlastFrag` + `Explosion.SimulateForce`, visuals from smallest-yield `Missile.warhead.terrainEffect`),
  airbase auto-return blocked (prefix `FactionHQ.AnyNearAirbase` only at the controlled pilot's position).
- 2026-10-02: launched via steam://rungameid/2168680; log shows `NO Infantry 0.1.0 loaded`, no Harmony errors. In-mission behaviour NOT yet verified.

- 2026-10-02: user played v0.1: "it seems to work". Asked for features in order 4,3,2,1:
  - #4 first-person rifle: procedural primitives (ViewModel.cs), sway/bob/recoil/ADS/reload/sprint poses, muzzle flash + light.
  - #3 start on foot: F8 calls `Aircraft.StartEjectionSequence()` when speed < 10 m/s; the game's own ground bail-out
    (`PilotDismounted.Setup`: speed < 10 or no ejection seat → `ejectionSeat.BailOut`, walk-out spot) does the rest.
  - #2 vehicles (E): `GroundVehicleJob_Math1.Inputs()` returns early when `fields.mobile == false` but the job still
    applies `fields.inputs` (throttle/steering/brake) → we write them each physics step. Turret: replace `Turret.FixedUpdate`
    for our turrets, call private `AimTurret(Vector3)`; fire with `WeaponStation.Fire(vehicle, null)` (guns only by default).
    Turret's built-in manual mode NREs on ground vehicles (reads `aircraft.LocalSim`).
  - #1 AI: pilots score 0 in `CombatAI.AnalyzeTarget` (zero-value definition). Postfix gives only the controlled pilot a
    range-scaled opportunity for gun weapons. Detection itself is generic (TargetDetector.VisualCheck/LoS).
- 2026-10-02 GOTCHA (crash): a Harmony postfix on `GroundVehicle.UpdateJobFields` (method body uses
  `PtrAllocation<GroundVehicleFields>`, a `T*` struct) hard-crashed Mono at startup inside MonoMod
  `MMReflectionImporter` → `RuntimeType.GetElementType`. Never patch methods touching PtrAllocation/Ptr<T>, and don't use
  FieldRefAccess on them; use plain FieldInfo.GetValue (boxed copy keeps the pointer). Plugin now logs
  "Applying patches: <class>" before each PatchAll to localise native crashes. Relaunched: loads clean.

## Next
- Build, install to BepInEx/plugins (ask user), verify in a single-player mission: eject, land, walk, shoot a ground unit.
- Tune damage numbers from logged hits.
- Then: AI reacting to/targeting infantry, entering ground vehicles, spawning as infantry without a jet, first-person gun model, MP sync.

## 2026-10-02 viewmodel v2
- User video: ADS sights "move" — primitive rear sight 12 cm from eye filled the screen, and hip sway/bob/roll rotation
  stayed active while aiming so the front post wandered in the notch.
- Downloaded (user approved) CC0 packs from opengameart: Quaternius Ultimate Gun Pack (7.4 MB) and Robin Lamb FPS rifle+hands
  (162 KB; too blocky, unused). Shipped Quaternius AssaultRifle*.obj/mtl in `models/` with CREDITS.txt; downloads gitignored.
- ObjModel.cs: runtime OBJ/MTL loader. Source guns are +X forward/+Y up; map (x,y,z)->(z,y,x) = handedness flip, keep winding.
  Bore = centre of muzzle slice; front sight = highest vertex in front half (minus muzzle device); MTL Kd is linear -> gamma.
- Default M4 (AssaultRifle2_1): scale 0.1625, sight height 0.071 m, eye 0.47 m behind front post (~8 cm behind rear sight).
- ADS now: sway/bob ~10%, rotations 0, recoil straight back only.
- Blender 5.2 can't read files under the app's virtualised AppData; stage in ~/noinfantry-tmp for renders.

## 2026-10-02 v0.3 combined arms
- User: viewmodel works; asked for polish + ground/sea/air combat.
- Sea: `ShipAI` (MonoBehaviour, subclasses AssaultCarrierAI/LandingCraftAI) writes `Ship.GetInputs()` in Update;
  disable it and write throttle/steering. `ShipPropulsion` applies `steering * steeringThrust` sideways at the stern,
  so right turn = negative steering. Pilot rides at its boarding point on deck (ship local space).
- Air (ground->air): `GameplayUI` private `aircraftSelectionMenu` prefab + public `gameplayCanvas`;
  Instantiate + `AircraftSelectionMenu.Initialize(localPlayer, airbase)` like `GameplayUI.SelectAircraft`.
  When `GameManager.GetLocalAircraft` returns a non-ejected live aircraft we exit without touching the camera and
  return the pilot (Returned + destroy) if at a friendly airbase.
- Air (anti-air): Launcher picks from `Encyclopedia.i.weaponMounts` (info.missile, weaponPrefab has Missile):
  AA = antiAir>=0.5, prefer minIR>0, lightest; AT = antiSurface>=0.5, not laser-guided preferred, lightest.
  Spawned with `Spawner.SpawnMissile(prefab, pos, rot, vel, target, owner=pilot)` (server only). Candidates logged.
- AirbaseServices bypass flag lets our own airbase check skip the AnyNearAirbase lie.
- Load test v0.3.0: clean. In-mission untested: ship steering sign, missile behaviour with a pilot as owner.

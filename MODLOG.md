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

## 2026-10-02 v0.4 automated testing (user away)
- Built `AutoTest` (`-noiAutotest`) + `InputShim`: game's own `-autoHost -state SinglePlayer -mission <name>` loads a
  mission with no menus; harness joins a faction via `LeaderboardMenu.JoinFactionCallback` (closes the join panel),
  spawns its soldier with `Spawner.SpawnPilot` + `Networkplayer = localPlayer`, spawns targets with SpawnVehicle /
  SpawnShip / SpawnAircraft, writes BepInEx/NOInfantry_autotest.txt + screenshots, quits.
- Bugs it found and fixed:
  1. Optical AT missiles flew blind: `OpticalSeeker.Initialize` starts from `NetworkHQ.TryGetKnownPosition`; the soldier
     now spots locked targets (`FactionHQ.RpcUpdateTrackingInfo`) and keeps spotting for 30 s after launch.
  2. AT-145 passed 1 m from a truck without impact; launcher arms the game's proximity fuse (`Missile.SetProxyFuse`) on
     surface targets -> kills.
  3. Lock check only tested StaticsMask; locked through a blast wall. Now full raycast ignoring self/target.
  4. Driven turrets: idle turrets disable themselves, moving vehicles stow them (DeployablePart, StowForward pins them),
     and AimTurret sets `Safety = !onTarget` (<1 deg). Now: keep enabled, unstow each step, ballistic aim
     (low-arc solution from muzzleVelocity/gravMult), safety within 5 deg (with safety OFF a GMG fired into its own
     hull and wrecked the APC).
  5. Vehicle aim ray hit vehicle colliders not parented under the root and the seated soldier: skip by unit/rb and
     ignore hits nearer than the vehicle.
  6. Viewmodel sprint/reload poses pivoted at the eye and left the screen; now pivot at the grip with muzzle-up cants.
  7. EnterDistance 4 -> 5 m.
- Ship steering sign confirmed in game: D = right turn (yaw +40 deg in 10 s), 15 m/s.
- AircraftSelectionMenu.FlyAircraft refuses unowned airframes ("player doesn't own"), i.e. game economy; harness falls
  back to a direct SpawnAircraft to test the hand-off.
- Not ours: "NullReferenceException in SlowUpdate ... GroundVehicle.CheckObstacles" floods start right after the faction
  join in Free Flight Ignus, before any harness spawns (mission vehicles with null NetworkHQ). Absent in the user's
  normal session log.
- Result: 20/20 PASS.

## 2026-10-02 v0.5 multiplayer
- Mirage custom messages work from a mod: set `Writer<T>.Write` / `Reader<T>.Read` (public setters) by hand,
  `MessagePacker.RegisterMessage<T>()`, `server/client.MessageHandler.RegisterHandler<T>(MessageDelegateWithPlayer<T>, false)`.
  Message id = FullName stable hash & 0xFFFF (checked for clashes at startup). Handlers re-attached by polling because the
  game creates a new MessageHandler per session.
- Unknown message => Mirage charges the sender 60 of 200 error tokens (refilling); enough errors => kick, and the game's
  authenticator can escalate ErrorKick to a ban. So: client sends ONE NoiHello; only servers that ack get further traffic;
  servers only send to players that said hello.
- Owner-predicted soldier: client `SetLocalSim(true)` on its pilot (PilotDismountedNetworkTransform only applies snapshots
  when !LocalSim), streams NoiSoldierState at 20 Hz; server replaces LocalFixedUpdate with ServerStep (follow / snap > 25 m);
  the game's NT then relays it to everyone else.
- Server-authoritative: shots (fire-rate + origin < 6 m checks), grenades, missiles (Spawner.SpawnMissile), vehicles/ships
  (per-player VehicleDriver instances; clients run visual drivers fed by NoiTurretAim so barrels match), return to base.
  Ownership = `pilot.Networkplayer.Owner == sender`. Clients spot via the game's `FactionHQ.CmdUpdateTrackingInfo`.
- Server weapon fire already reaches clients (SetFiringState/RemoteFireAuto), so driving clients never fire locally.
- Two-instance test over UDP (`-autoHost -socket UDP` / `-autoConnect 127.0.0.1`), no Steam auth on UDP
  (Steam path rejects a second connection from the same licence).
- MP bugs found by the harness:
  1. `NetworkManagerNuclearOption.GamePlayers` is never filled; use `UnitRegistry.playerLookup`.
  2. Owner set after network spawn => client never links Player.PilotDismounted; controller now scans for owned pilots.
  3. Server "returned" a modded client's soldier at an airbase before the client could take control (landed state lags
     the 3 s capture check) => soldiers owned by modded players are protected from Capture/AnyNearAirbase.
  4. Spawn-time `TooMuchForce` killed a ground-spawned soldier on the server => ignored for 5 s after spawn for protected soldiers.
  5. Client launch point sat inside the lagging server copy => missile detonated on its own soldier. Server now launches
     from its copy (eye + 3 m along the aim).
- Results: MP client 17/17, host 9/9; SP 20/20.

## 2026-10-02 v0.6 stances, AI squads, MP hardening
- Crouch/prone: eye offset eased, speed x0.55/x0.25, spread x0.7/x0.45, AI accuracy vs you x1.3/x1.8. Procedural footsteps.
- SoldierMotor: the landed-pilot hover physics shared by player and AI.
- AI squads (server/host): PilotDismounted via Spawner.SpawnPilot, registered before the first physics step so the game's
  landed logic never runs; LocalFixedUpdate prefix -> AiSquads.Step. Fireteam wedge follows leader; patrols advance and
  hold at ~90 m when engaged. Bursts 3-5, 0.6-1.4 s reaction, spread grows with range/target speed. Check-fire with a
  0.3 m spherecast lane + per-round friendly test; sidestep after 3 blocked shots (first version: 44 friendly hits).
- MP root causes found with the two-instance harness:
  * Launcher missile detonated at once: (a) physical bump with the launching soldier = impact damage -> IgnoreCollision
    missile vs soldier colliders; (b) "underwater" detonation because the soldier itself had fallen into the sea.
  * The soldier fell because the client's copy of a never-moving pilot was stale (client local origin, ~425 m up) when the
    client took LocalSim. NoiCorrect: on first state, if client and server copies differ by > 5 m the server sends its
    authoritative position and ignores states for 0.6 s.
  * Harness: an all-layers raycast at an airbase hit an invisible collider ~400 m up; coastal airbases have sea 60 m out.
    The MP client now starts like a player (aircraft at the airbase, then F8), which exercises the real ejection path.
  * Unity null-equality: `a.Leader == me` is true for leaderless patrols once `me` is destroyed -> use (object) compares.
- Results: SP 27/27; MP client 19/19, host 10/10 on two consecutive runs.

## 2026-10-02 v0.7 Ground War (infantry first) + fireteam orders
- Deploy on foot: IMGUI panel over `AircraftSelectionMenu` (private `airbase` field); server instantiates
  `GameAssets.i.pilotDismounted` with `Networkplayer = player`, no `parentUnit`, and
  `NetworkManagerNuclearOption.i.ServerObjectManager.Spawn(go, player.Owner)`. Setup() then takes the
  "no parent" branch (detaching state); we accept control after 0.7 s instead of 3 s. Menu closed via private `HideSelection`.
  One soldier per player: an older one is marked Returned and destroyed. `GroundWar.Deployed` is protected like controlled soldiers.
- Kits (Rifleman / Anti-Tank / Anti-Air) limit launcher target classes; the server re-checks in OnLaunch.
- Capture: `Capture.GetInRangeUnits` sums `Unit.CaptureStrength` per faction (= definition + `extraCaptureStrength`);
  `Unit.ModifyCaptureStrength` is public, so soldiers on foot get +SoldierCaptureStrength (removed in vehicles).
  Units sit in BattlefieldGrid squares updated by the unit's slow update, so walking soldiers are counted.
  Airbase default CaptureDefense 10, CaptureRange 1000 m; rate/s = (strength diff) / (defense + defenders).
- Death -> `Player.ShowMap(4f)` (same call the game uses after a crash/ejection) to redeploy.
- AI orders: Follow / Hold / Move / Free (hunt soldiers within 600 m, else walk to `GroundWar.NextObjective`) / Disband.
  Leaderless fireteams go Free. Front line: per faction every SquadInterval, a squad spawns SquadStartDistance outside
  the nearest non-held land airbase on the faction's side; garrison squads when enemy soldiers are inside an own base.
- Motor pool: `Spawner.SpawnVehicle(..., player)` near the soldier at a friendly airbase, cost from `Player.Allocation`.
- Net: NoiDeploy, NoiOrder (protocol 2); motor pool rides NoiSquad kinds 32-40.
- GOTCHA (big): `Instantiate(prefab)` + `transform.SetPositionAndRotation` leaves the Rigidbody at the prefab origin; the
  first physics step moved the pilot there (the local origin: in testing, the sea ~500 m from the airbase, where
  `inWater` forces the `ejecting` state). The game's `Spawner.SpawnPilot` has the same pattern, so v0.6 AI soldiers could
  also start at the origin (riflemen "460 m away", front-line squads in the sea). Fix: set `rb.position/rotation` too
  (`GroundWar.PlaceBody`), for deployed soldiers and every AI spawn. Found with a per-physics-step spawn trace.
- Ground probes now use `PhysicsLayers.StaticsMask` only (terrain + buildings): an all-layers ray hit the selection
  menu's preview aircraft.
- Menu number keys: the key that picked a menu entry also switched weapon (menu closed before the weapon check).
- AI accuracy retuned after the fix (fights now happen at real ranges): spread 1.2 deg, x2 at 300 m, triangular
  distribution (key `Squads/AimSpread`). Patrol vs fireteam at 220 m resolves in ~25 s.
- Capture calibration from the game data: trucks 2, MRAP 6, APC 12; airbase defence 10. Soldier = 0.5.
- Results: SP 39/39; MP client 23/23, host 12/12.

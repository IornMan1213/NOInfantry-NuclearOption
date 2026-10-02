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

## Next
- Build, install to BepInEx/plugins (ask user), verify in a single-player mission: eject, land, walk, shoot a ground unit.
- Tune damage numbers from logged hits.
- Then: AI reacting to/targeting infantry, entering ground vehicles, spawning as infantry without a jet, first-person gun model, MP sync.

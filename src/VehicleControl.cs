using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Jobs;
using UnityEngine;

namespace NOInfantry
{
    // One player driving / gunning one friendly ground vehicle or ship.
    //
    // Ground vehicles: their Burst job skips its AI input code when fields.mobile == false but still applies
    //   fields.inputs, so ApplyInputs writes throttle/steering there each physics step.
    // Ships: ShipAI (a MonoBehaviour) writes Ship.GetInputs() every frame; we disable it and write them ourselves.
    //   ShipPropulsion pushes the stern sideways by +steering, so a right turn is negative steering.
    // Turrets: Turret's built-in "manual" mode assumes an aircraft (NRE otherwise), so we replace
    //   Turret.FixedUpdate for our turrets and call its private AimTurret(Vector3) with the aim vector.
    //
    // Multiplayer: the server/host runs an Authority driver per driving player (moves the vehicle, fires).
    // Clients run Visual drivers that only aim turrets locally so barrels point where the driver aims;
    // the actual rounds come from the server through the game's own weapon sync.
    internal class VehicleDriver
    {
        internal Unit Driven;
        internal float Throttle;
        internal float Steering;
        internal Vector3 AimPoint;
        internal Unit AimUnit;
        internal bool AimValid;
        internal bool Authority = true;
        internal readonly List<Turret> Turrets = new List<Turret>();

        // Where the pilot stood relative to the vehicle when boarding (ships: back on deck when leaving).
        internal Vector3 BoardLocalPos;

        private ShipAI disabledShipAI;
        private float nextFireDiag;

        internal void Enter(Unit unit, Vector3 pilotWorldPos, bool authority)
        {
            Driven = unit;
            Authority = authority;
            Throttle = 0f;
            Steering = 0f;
            AimValid = false;
            BoardLocalPos = unit.transform.InverseTransformPoint(pilotWorldPos);
            Turrets.Clear();
            foreach (Turret t in unit.GetComponentsInChildren<Turret>(true))
            {
                if (t.GetWeaponStation() != null) Turrets.Add(t);
            }
            if (authority && unit is Ship ship)
            {
                disabledShipAI = ship.GetComponent<ShipAI>();
                if (disabledShipAI != null) disabledShipAI.enabled = false;
            }
            VehicleControl.Register(this);
            Plugin.Log.LogInfo($"{(authority ? "Driving" : "Tracking")} {unit.unitName} ({unit.definition?.unitName}, {unit.GetType().Name}); turrets: {Turrets.Count}");
        }

        internal void Exit()
        {
            Unit unit = Driven;
            VehicleControl.Unregister(this);
            Driven = null;
            if (Authority && unit is GroundVehicle gv)
            {
                // Hand the vehicle back to its AI.
                var alloc = (PtrAllocation<GroundVehicleFields>)VehicleControl.JobFieldsField.GetValue(gv);
                if (alloc.IsCreated)
                {
                    ref GroundVehicleFields f = ref alloc.Ref();
                    f.mobile = Traverse.Create(gv).Field("mobile").GetValue<bool>();
                    f.inputs = default;
                }
            }
            else if (Authority && unit is Ship ship)
            {
                ShipInputs inputs = ship.GetInputs();
                inputs.throttle = 0f;
                inputs.steering = 0f;
                if (disabledShipAI != null) disabledShipAI.enabled = true;
            }
            disabledShipAI = null;
            Plugin.Log.LogInfo($"Left {(unit != null ? unit.unitName : "vehicle")}");
            Turrets.Clear();
        }

        // Called every physics step while driving.
        internal void ApplyInputs()
        {
            // Idle turrets disable themselves (no FixedUpdate => our aim prefix never runs); keep ours awake.
            // Moving vehicles stow their turrets (DeployablePart: deployed only after ~5 s stationary) and
            // Turret.StowForward then pins them forward; under player control we want to shoot on the move.
            foreach (Turret t in Turrets)
            {
                if (t == null) continue;
                if (VehicleControl.StowedRef(t)) t.SetStowed(false);
                if (!t.enabled) t.enabled = true;
            }
            if (!Authority) return;
            if (Driven is GroundVehicle gv)
            {
                var alloc = (PtrAllocation<GroundVehicleFields>)VehicleControl.JobFieldsField.GetValue(gv);
                if (!alloc.IsCreated) return;
                ref GroundVehicleFields f = ref alloc.Ref();
                f.mobile = false; // job keeps our inputs instead of computing AI ones
                f.inputs.throttle = Throttle;
                f.inputs.steering = Steering * 0.5f - f.angularVelocity.y * 0.2f;
                f.inputs.brake = Mathf.Clamp01(1f - Mathf.Abs(Throttle));
            }
            else if (Driven is Ship ship)
            {
                ShipInputs inputs = ship.GetInputs();
                inputs.throttle = Throttle;
                inputs.steering = -Steering;
            }
        }

        internal void FireHeld()
        {
            if (Driven == null || !Authority) return;
            // One line per second when the safety is holding fire, so "my gun won't shoot" is explainable from the log.
            if (Time.time >= nextFireDiag)
            {
                foreach (Turret t in Turrets)
                {
                    Weapon w = t != null ? t.GetWeapon() : null;
                    if (w == null || !w.Safety) continue;
                    nextFireDiag = Time.time + 1f;
                    float err = Mathf.Abs(VehicleControl.TraverseErrorRef(t)) + Mathf.Abs(VehicleControl.ElevationErrorRef(t));
                    Plugin.Log.LogInfo($"[fire] {t.GetWeaponStation()?.WeaponInfo?.weaponName} held by safety: barrel {err:F1} deg off the aim point (limit {Plugin.TurretFireTolerance.Value})");
                }
            }
            foreach (Turret t in Turrets)
            {
                WeaponStation ws = t.GetWeaponStation();
                if (ws == null || ws.Ammo <= 0 || ws.WeaponInfo == null) continue;
                if (ws.WeaponInfo.gun || ws.Weapons.Exists(w => w is Gun))
                {
                    ws.Fire(Driven, AimUnit);
                }
                else if (Plugin.AllowVehicleMissiles.Value && AimUnit != null && ws.Ready())
                {
                    // Guided weapons need a target: use the enemy unit under the crosshair.
                    ws.Fire(Driven, AimUnit);
                }
            }
        }

        internal string WeaponSummary()
        {
            if (Turrets.Count == 0) return "no turret";
            var parts = new List<string>();
            foreach (Turret t in Turrets)
            {
                WeaponStation ws = t.GetWeaponStation();
                if (ws == null || ws.WeaponInfo == null) continue;
                string name = string.IsNullOrEmpty(ws.WeaponInfo.shortName) ? ws.WeaponInfo.weaponName : ws.WeaponInfo.shortName;
                bool usable = ws.WeaponInfo.gun || ws.Weapons.Exists(w => w is Gun) || Plugin.AllowVehicleMissiles.Value;
                parts.Add(usable ? $"{name} {ws.Ammo}" : $"{name} (AI)");
            }
            return string.Join("  ", parts);
        }

        internal void AimTurret(Turret turret)
        {
            if (!AimValid) return;
            WeaponStation ws = turret.GetWeaponStation();
            Transform muzzle = ws != null && ws.Weapons.Count > 0 && ws.Weapons[0] != null ? ws.Weapons[0].transform : turret.transform;
            VehicleControl.AimTurretMethod(turret, VehicleControl.BallisticAim(muzzle.position, AimPoint, ws?.WeaponInfo));

            // AimTurret only releases the safety within 1 deg of the aim point, which a player-moved
            // camera (or an elevation limit) rarely satisfies. Allow fire within a player tolerance, but keep a
            // safety: out-of-reach aim points can leave the barrel pointing into its own hull
            // (a test run blew up its own APC with the GMG that way).
            Weapon safetyWeapon = turret.GetWeapon();
            if (safetyWeapon != null)
            {
                float error = Mathf.Abs(VehicleControl.TraverseErrorRef(turret)) + Mathf.Abs(VehicleControl.ElevationErrorRef(turret));
                safetyWeapon.Safety = error > Plugin.TurretFireTolerance.Value;
            }
        }
    }

    // Registry of active drivers plus the local player's driver (kept as statics for the controller/HUD).
    internal static class VehicleControl
    {
        internal static readonly VehicleDriver Local = new VehicleDriver();

        private static readonly Dictionary<Turret, VehicleDriver> turretOwners = new Dictionary<Turret, VehicleDriver>();
        private static readonly Dictionary<Unit, VehicleDriver> unitOwners = new Dictionary<Unit, VehicleDriver>();

        internal static readonly AccessTools.FieldRef<Turret, bool> StowedRef = AccessTools.FieldRefAccess<Turret, bool>("stowed");
        internal static readonly AccessTools.FieldRef<Turret, float> TraverseErrorRef = AccessTools.FieldRefAccess<Turret, float>("traverseError");
        internal static readonly AccessTools.FieldRef<Turret, float> ElevationErrorRef = AccessTools.FieldRefAccess<Turret, float>("elevationError");

        internal static readonly Action<Turret, Vector3> AimTurretMethod =
            AccessTools.MethodDelegate<Action<Turret, Vector3>>(AccessTools.Method(typeof(Turret), "AimTurret", new[] { typeof(Vector3) }));

        // Plain reflection on purpose: Harmony patches / FieldRefAccess over PtrAllocation<T> (a T* struct) make
        // MonoMod import a pointer-to-generic type, which hard-crashes Mono at startup. The boxed copy holds the
        // same pointer, so Ref() on it writes the real job memory.
        internal static readonly FieldInfo JobFieldsField = AccessTools.Field(typeof(GroundVehicle), "JobFields");

        // Local-player shorthands used by the controller, camera and HUD.
        internal static Unit Driven => Local.Driven;
        internal static List<Turret> Turrets => Local.Turrets;
        internal static Vector3 BoardLocalPos => Local.BoardLocalPos;
        internal static float Throttle { get => Local.Throttle; set => Local.Throttle = value; }
        internal static float Steering { get => Local.Steering; set => Local.Steering = value; }
        internal static Vector3 AimPoint { get => Local.AimPoint; set => Local.AimPoint = value; }
        internal static Unit AimUnit { get => Local.AimUnit; set => Local.AimUnit = value; }
        internal static bool AimValid { get => Local.AimValid; set => Local.AimValid = value; }
        internal static string WeaponSummary() => Local.WeaponSummary();

        internal static bool IsDrivable(Unit u) => u is GroundVehicle || u is Ship;

        internal static bool IsTaken(Unit u) => u != null && unitOwners.ContainsKey(u);

        internal static VehicleDriver DriverOf(Unit u) => u != null && unitOwners.TryGetValue(u, out VehicleDriver d) ? d : null;

        internal static void Register(VehicleDriver d)
        {
            if (d.Driven != null) unitOwners[d.Driven] = d;
            foreach (Turret t in d.Turrets) turretOwners[t] = d;
        }

        internal static void Unregister(VehicleDriver d)
        {
            if (d.Driven != null && unitOwners.TryGetValue(d.Driven, out VehicleDriver cur) && cur == d) unitOwners.Remove(d.Driven);
            foreach (Turret t in d.Turrets)
            {
                if (turretOwners.TryGetValue(t, out VehicleDriver o) && o == d) turretOwners.Remove(t);
            }
        }

        // Friendly drivable unit within reach. On clients `LocalSim` is false for every vehicle, so we only
        // require a server that can drive it for us (NetSync.ServerHasMod) or that we are the server.
        internal static Unit FindEnterable(PilotDismounted pilot)
        {
            Unit best = null;
            float bestDist = float.MaxValue;
            bool canDriveRemote = NetSync.ServerHasMod;
            foreach (Unit u in UnitRegistry.allUnits)
            {
                if (u == null || !IsDrivable(u) || u.disabled) continue;
                if (!u.LocalSim && !canDriveRemote) continue;
                if (u.NetworkHQ != pilot.NetworkHQ) continue;
                VehicleDriver owner = DriverOf(u);
                if (owner != null && owner != Local) continue; // someone else is at the controls
                float d = Vector3.Distance(u.transform.position, pilot.transform.position) - u.maxRadius;
                if (d < Plugin.EnterDistance.Value && d < bestDist)
                {
                    bestDist = d;
                    best = u;
                }
            }
            return best;
        }

        // Direction to fire so a gun round (muzzle velocity v, gravity g * gravMult) lands on `to`.
        // Uses the low arc; falls back to straight-line aim if the target is out of reach or for missiles.
        internal static Vector3 BallisticAim(Vector3 from, Vector3 to, WeaponInfo info)
        {
            Vector3 d = to - from;
            if (info == null || !info.gun || info.muzzleVelocity <= 0f) return d;
            float g = 9.81f * Mathf.Max(info.gravMult, 0.01f);
            float v = info.muzzleVelocity;
            Vector3 flat = new Vector3(d.x, 0f, d.z);
            float x = flat.magnitude;
            if (x < 1f) return d;
            float y = d.y;
            float v2 = v * v;
            float disc = v2 * v2 - g * (g * x * x + 2f * y * v2);
            if (disc < 0f) disc = 0f; // out of range: best (45 deg) shot
            float angle = Mathf.Atan((v2 - Mathf.Sqrt(disc)) / (g * x));
            return (flat.normalized * Mathf.Cos(angle) + Vector3.up * Mathf.Sin(angle)) * d.magnitude;
        }

        [HarmonyPatch(typeof(Turret), "FixedUpdate")]
        [HarmonyPrefix]
        private static bool TurretFixedUpdatePrefix(Turret __instance)
        {
            if (!turretOwners.TryGetValue(__instance, out VehicleDriver d) || d.Driven == null) return true;
            d.AimTurret(__instance);
            return false;
        }
    }
}

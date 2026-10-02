using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Jobs;
using UnityEngine;

namespace NOInfantry
{
    // Player driving + gunning of friendly ground vehicles and ships (host / single-player only).
    //
    // Ground vehicles: their Burst job skips its AI input code when fields.mobile == false but still applies
    //   fields.inputs, so ApplyInputs writes throttle/steering there each physics step.
    // Ships: ShipAI (a MonoBehaviour) writes Ship.GetInputs() every frame; we disable it and write them ourselves.
    //   ShipPropulsion pushes the stern sideways by +steering, so a right turn is negative steering.
    // Turrets: Turret's built-in "manual" mode assumes an aircraft (NRE otherwise), so we replace
    //   Turret.FixedUpdate for our turrets and call its private AimTurret(Vector3) with the camera's aim vector.
    internal static class VehicleControl
    {
        internal static Unit Driven;
        internal static float Throttle;
        internal static float Steering;
        internal static Vector3 AimPoint;
        internal static Unit AimUnit;
        internal static bool AimValid;

        internal static readonly List<Turret> Turrets = new List<Turret>();

        // Where the pilot stood relative to the vehicle when boarding (ships: back on deck when leaving).
        internal static Vector3 BoardLocalPos;

        private static ShipAI disabledShipAI;

        private static readonly AccessTools.FieldRef<Turret, bool> stowedRef = AccessTools.FieldRefAccess<Turret, bool>("stowed");
        private static readonly AccessTools.FieldRef<Turret, float> traverseErrorRef = AccessTools.FieldRefAccess<Turret, float>("traverseError");
        private static readonly AccessTools.FieldRef<Turret, float> elevationErrorRef = AccessTools.FieldRefAccess<Turret, float>("elevationError");

        private static readonly Action<Turret, Vector3> aimTurret =
            AccessTools.MethodDelegate<Action<Turret, Vector3>>(AccessTools.Method(typeof(Turret), "AimTurret", new[] { typeof(Vector3) }));

        // Plain reflection on purpose: Harmony patches / FieldRefAccess over PtrAllocation<T> (a T* struct) make
        // MonoMod import a pointer-to-generic type, which hard-crashes Mono at startup. The boxed copy holds the
        // same pointer, so Ref() on it writes the real job memory.
        private static readonly FieldInfo jobFieldsField = AccessTools.Field(typeof(GroundVehicle), "JobFields");

        internal static bool IsDrivable(Unit u) => u is GroundVehicle || u is Ship;

        internal static Unit FindEnterable(PilotDismounted pilot)
        {
            Unit best = null;
            float bestDist = float.MaxValue;
            foreach (Unit u in UnitRegistry.allUnits)
            {
                if (u == null || !IsDrivable(u) || u.disabled || !u.LocalSim) continue;
                if (u.NetworkHQ != pilot.NetworkHQ) continue;
                float d = Vector3.Distance(u.transform.position, pilot.transform.position) - u.maxRadius;
                if (d < Plugin.EnterDistance.Value && d < bestDist)
                {
                    bestDist = d;
                    best = u;
                }
            }
            return best;
        }

        internal static void Enter(Unit unit, PilotDismounted pilot)
        {
            Driven = unit;
            Throttle = 0f;
            Steering = 0f;
            BoardLocalPos = unit.transform.InverseTransformPoint(pilot.transform.position);
            Turrets.Clear();
            foreach (Turret t in unit.GetComponentsInChildren<Turret>(true))
            {
                if (t.GetWeaponStation() != null) Turrets.Add(t);
            }
            if (unit is Ship ship)
            {
                disabledShipAI = ship.GetComponent<ShipAI>();
                if (disabledShipAI != null) disabledShipAI.enabled = false;
            }
            Plugin.Log.LogInfo($"Entered {unit.unitName} ({unit.definition?.unitName}, {unit.GetType().Name}); turrets: {Turrets.Count}");
        }

        internal static void Exit()
        {
            Unit unit = Driven;
            Driven = null;
            if (unit is GroundVehicle gv)
            {
                // Hand the vehicle back to its AI.
                var alloc = (PtrAllocation<GroundVehicleFields>)jobFieldsField.GetValue(gv);
                if (alloc.IsCreated)
                {
                    ref GroundVehicleFields f = ref alloc.Ref();
                    f.mobile = Traverse.Create(gv).Field("mobile").GetValue<bool>();
                    f.inputs = default;
                }
            }
            else if (unit is Ship ship)
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
        internal static void ApplyInputs()
        {
            // Idle turrets disable themselves (no FixedUpdate => our aim prefix never runs); keep ours awake.
            // Moving vehicles stow their turrets (DeployablePart: deployed only after ~5 s stationary) and
            // Turret.StowForward then pins them forward; under player control we want to shoot on the move.
            foreach (Turret t in Turrets)
            {
                if (t == null) continue;
                if (stowedRef(t)) t.SetStowed(false);
                if (!t.enabled) t.enabled = true;
            }
            if (Driven is GroundVehicle gv)
            {
                var alloc = (PtrAllocation<GroundVehicleFields>)jobFieldsField.GetValue(gv);
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

        private static float nextFireDiag;

        internal static void FireHeld()
        {
            if (Driven == null) return;
            if (Time.time >= nextFireDiag && Turrets.Exists(t => t != null && t.GetWeapon() != null && t.GetWeapon().Safety))
            {
                nextFireDiag = Time.time + 1f;
                foreach (Turret t in Turrets)
                {
                    WeaponStation ws = t.GetWeaponStation();
                    if (ws == null) continue;
                    foreach (Weapon w in ws.Weapons)
                    {
                        if (w == null) continue;
                        var tr = Traverse.Create(w);
                        float err = Mathf.Abs(traverseErrorRef(t)) + Mathf.Abs(elevationErrorRef(t));
                        var tt = Traverse.Create(t);
                        Plugin.Log.LogInfo($"[fire] aim point {Vector3.Distance(w.transform.position, AimPoint):F0} m away, unit {AimUnit?.unitName}");
                        Plugin.Log.LogInfo($"[fire] turret trav={traverseErrorRef(t):F1} elev={elevationErrorRef(t):F1} minElev={tt.Field("minElevation").GetValue()} maxElev={tt.Field("maxElevation").GetValue()} travRange={tt.Field("traverseRange").GetValue()} v={ws.WeaponInfo?.muzzleVelocity} grav={ws.WeaponInfo?.gravMult}");
                        Plugin.Log.LogInfo($"[fire] {ws.WeaponInfo?.weaponName} {w.GetType().Name} safety={w.Safety} turretEnabled={t.enabled} aimError={err:F1} stationAmmo={ws.Ammo} " +
                            $"loaded={tr.Field("bulletsLoaded").GetValue()} reload={tr.Field("timeUntilReload").GetValue()} attached={w.IsAttached()}");
                    }
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

        internal static string WeaponSummary()
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
            if (Driven == null || !Turrets.Contains(__instance)) return true;
            if (AimValid)
            {
                WeaponStation ws = __instance.GetWeaponStation();
                Transform muzzle = ws != null && ws.Weapons.Count > 0 && ws.Weapons[0] != null ? ws.Weapons[0].transform : __instance.transform;
                aimTurret(__instance, BallisticAim(muzzle.position, AimPoint, ws?.WeaponInfo));

                // AimTurret only releases the safety within 1 deg of the aim point, which a player-moved
                // camera (or an elevation limit) rarely satisfies. Allow fire within a player tolerance.
                Weapon safetyWeapon = __instance.GetWeapon();
                if (safetyWeapon != null)
                {
                    // Same error terms the game uses for "on target", just with a wider tolerance.
                    float error = Mathf.Abs(traverseErrorRef(__instance)) + Mathf.Abs(elevationErrorRef(__instance));
                    // Keep a safety: when the aim point is outside the turret's limits the barrel can sit pointing
                    // into its own hull (a test run blew up its own APC with the GMG that way).
                    safetyWeapon.Safety = error > Plugin.TurretFireTolerance.Value;
                }
            }
            return false;
        }
    }
}

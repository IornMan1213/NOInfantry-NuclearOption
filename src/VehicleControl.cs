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

        internal static void FireHeld()
        {
            if (Driven == null) return;
            foreach (Turret t in Turrets)
            {
                WeaponStation ws = t.GetWeaponStation();
                if (ws == null || ws.Ammo <= 0 || ws.WeaponInfo == null) continue;
                if (ws.WeaponInfo.gun)
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
                string name = ws.WeaponInfo.shortName;
                bool usable = ws.WeaponInfo.gun || Plugin.AllowVehicleMissiles.Value;
                parts.Add(usable ? $"{name} {ws.Ammo}" : $"{name} (AI)");
            }
            return string.Join("  ", parts);
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
                aimTurret(__instance, AimPoint - muzzle.position);
            }
            return false;
        }
    }
}

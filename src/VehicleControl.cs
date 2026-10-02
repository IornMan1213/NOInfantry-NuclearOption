using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Jobs;
using UnityEngine;

namespace NOInfantry
{
    // Player driving + gunning of an existing GroundVehicle (host / single-player only).
    //
    // Driving: the vehicle's Burst job skips its AI input code when fields.mobile == false but still applies
    // whatever fields.inputs hold, so ApplyInputs writes our throttle/steering there each physics step.
    // Gunning: Turret's own "manual" mode assumes an aircraft (NRE on ground vehicles), so we replace
    // Turret.FixedUpdate for our turrets and call its private AimTurret(Vector3) with the camera's aim vector.
    internal static class VehicleControl
    {
        internal static GroundVehicle Driven;
        internal static float Throttle;
        internal static float Steering;
        internal static Vector3 AimPoint;
        internal static bool AimValid;

        internal static readonly List<Turret> Turrets = new List<Turret>();

        private static readonly Action<Turret, Vector3> aimTurret =
            AccessTools.MethodDelegate<Action<Turret, Vector3>>(AccessTools.Method(typeof(Turret), "AimTurret", new[] { typeof(Vector3) }));

        // Plain reflection on purpose: Harmony patches / FieldRefAccess over PtrAllocation<T> (a T* struct) make
        // MonoMod import a pointer-to-generic type, which hard-crashes Mono at startup. The boxed copy holds the
        // same pointer, so Ref() on it writes the real job memory.
        private static readonly FieldInfo jobFieldsField = AccessTools.Field(typeof(GroundVehicle), "JobFields");

        internal static GroundVehicle FindEnterable(PilotDismounted pilot)
        {
            GroundVehicle best = null;
            float bestDist = float.MaxValue;
            foreach (Unit u in UnitRegistry.allUnits)
            {
                if (!(u is GroundVehicle gv) || gv.disabled || !gv.LocalSim) continue;
                if (gv.NetworkHQ != pilot.NetworkHQ) continue;
                float d = Vector3.Distance(gv.transform.position, pilot.transform.position) - gv.maxRadius;
                if (d < Plugin.EnterDistance.Value && d < bestDist)
                {
                    bestDist = d;
                    best = gv;
                }
            }
            return best;
        }

        internal static void Enter(GroundVehicle gv)
        {
            Driven = gv;
            Throttle = 0f;
            Steering = 0f;
            Turrets.Clear();
            foreach (Turret t in gv.GetComponentsInChildren<Turret>(true))
            {
                if (t.GetWeaponStation() != null) Turrets.Add(t);
            }
            Plugin.Log.LogInfo($"Entered {gv.unitName} ({gv.definition?.unitName}); turrets: {Turrets.Count}");
        }

        internal static void Exit()
        {
            GroundVehicle gv = Driven;
            Driven = null;
            if (gv != null)
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
            Plugin.Log.LogInfo($"Left {(gv != null ? gv.unitName : "vehicle")}");
            Turrets.Clear();
        }

        internal static void FireHeld()
        {
            if (Driven == null) return;
            foreach (Turret t in Turrets)
            {
                WeaponStation ws = t.GetWeaponStation();
                if (ws == null || ws.Ammo <= 0) continue;
                if (!ws.WeaponInfo.gun && !Plugin.AllowVehicleMissiles.Value) continue;
                ws.Fire(Driven, null);
            }
        }

        internal static string WeaponSummary()
        {
            if (Turrets.Count == 0) return "no turret";
            var parts = new List<string>();
            foreach (Turret t in Turrets)
            {
                WeaponStation ws = t.GetWeaponStation();
                if (ws == null) continue;
                string name = ws.WeaponInfo != null ? ws.WeaponInfo.shortName : "?";
                bool usable = ws.WeaponInfo != null && (ws.WeaponInfo.gun || Plugin.AllowVehicleMissiles.Value);
                parts.Add(usable ? $"{name} {ws.Ammo}" : $"{name} (AI only)");
            }
            return string.Join("  ", parts);
        }

        // Called every physics step while driving. The job reads these fields on its next run; with
        // mobile == false its Inputs() returns early and leaves our values alone.
        internal static void ApplyInputs()
        {
            if (Driven == null) return;
            var alloc = (PtrAllocation<GroundVehicleFields>)jobFieldsField.GetValue(Driven);
            if (!alloc.IsCreated) return;
            ref GroundVehicleFields f = ref alloc.Ref();
            f.mobile = false;
            f.inputs.throttle = Throttle;
            f.inputs.steering = Steering * 0.5f - f.angularVelocity.y * 0.2f;
            f.inputs.brake = Mathf.Clamp01(1f - Mathf.Abs(Throttle));
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

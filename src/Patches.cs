using HarmonyLib;
using UnityEngine;

namespace NOInfantry
{
    internal static class Patches
    {
        // The game's own grounded behaviour (hover spring, velocity damping, going kinematic after
        // 35 s, capture checks, impact death) fights player movement, so we replace it while controlled.
        [HarmonyPatch(typeof(PilotDismounted), "LocalFixedUpdate")]
        [HarmonyPrefix]
        private static bool LocalFixedUpdatePrefix(PilotDismounted __instance)
        {
            if (InfantryController.Controlled != __instance) return true;
            InfantryController.Instance.PhysicsStep(__instance);
            return false;
        }

        // Friendly/enemy units within 500 m "capture" a landed pilot and destroy it.
        [HarmonyPatch(typeof(PilotDismounted), nameof(PilotDismounted.Capture))]
        [HarmonyPrefix]
        private static bool CapturePrefix(PilotDismounted __instance)
        {
            return InfantryController.Controlled != __instance;
        }

        // Pilots have a zero-value unit definition, so AI never engages them. Give only the player-controlled
        // pilot a target value for gun weapons (no missiles/SAMs), scaled by range like the game's own scoring.
        [HarmonyPatch(typeof(CombatAI), nameof(CombatAI.AnalyzeTarget))]
        [HarmonyPostfix]
        private static void AnalyzeTargetPostfix(WeaponStation weaponStation, Unit analyzer, TrackingInfo trackingInfo, float targetDistance, ref OpportunityThreat __result)
        {
            PilotDismounted p = InfantryController.Controlled;
            // While seated the pilot's collider is off, so AI would waste fire on it; the vehicle is a target anyway.
            if (p == null || !Plugin.AiTargetsInfantry.Value || __result.opportunity > 0f || VehicleControl.Driven != null) return;
            if (weaponStation?.WeaponInfo == null || !weaponStation.WeaponInfo.gun) return;
            if (!trackingInfo.TryGetUnit(out Unit unit) || unit != p || analyzer.NetworkHQ == p.NetworkHQ) return;

            float maxRange = weaponStation.WeaponInfo.targetRequirements.maxRange;
            if (targetDistance < 0f) targetDistance = Vector3.Distance(analyzer.transform.position, p.transform.position);
            float range = Mathf.Min(maxRange, Plugin.AiEngageRange.Value);
            if (targetDistance > range) return;
            __result = new OpportunityThreat(Plugin.AiInfantryPriority.Value * (1f - targetDistance / range), __result.threat);
        }

        // PilotDismounted.CheckForCapture "returns" a landed pilot to base (destroying it) when near a
        // friendly airbase. Only lie for the controlled pilot's exact position; every other caller is untouched.
        [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.AnyNearAirbase))]
        [HarmonyPrefix]
        private static bool AnyNearAirbasePrefix(Vector3 fromPosition, ref Airbase airbase, ref bool __result)
        {
            PilotDismounted p = InfantryController.Controlled;
            if (AirbaseServices.BypassAirbasePatch || p == null || (fromPosition - p.transform.position).sqrMagnitude > 1f) return true;
            airbase = null;
            __result = false;
            return false;
        }
    }
}

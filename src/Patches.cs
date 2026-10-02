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

        // PilotDismounted.CheckForCapture "returns" a landed pilot to base (destroying it) when near a
        // friendly airbase. Only lie for the controlled pilot's exact position; every other caller is untouched.
        [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.AnyNearAirbase))]
        [HarmonyPrefix]
        private static bool AnyNearAirbasePrefix(Vector3 fromPosition, ref Airbase airbase, ref bool __result)
        {
            PilotDismounted p = InfantryController.Controlled;
            if (p == null || (fromPosition - p.transform.position).sqrMagnitude > 1f) return true;
            airbase = null;
            __result = false;
            return false;
        }
    }
}

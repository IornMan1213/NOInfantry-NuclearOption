using HarmonyLib;

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
    }
}

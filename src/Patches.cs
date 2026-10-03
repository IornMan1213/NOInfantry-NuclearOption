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
            if (InfantryController.Controlled == __instance)
            {
                InfantryController.Instance.PhysicsStep(__instance);
                return false;
            }
            // AI riflemen (server/host).
            if (AiSquads.All.TryGetValue(__instance, out AiSoldier ai))
            {
                AiSquads.Step(ai);
                return false;
            }
            // Server: soldiers driven by remote players follow their owner's streamed state.
            if (NetSync.Remote.TryGetValue(__instance, out NetSync.RemoteSoldier rs))
            {
                NetSync.ServerStep(rs);
                return false;
            }
            return true;
        }

        // Friendly/enemy units within 500 m "capture" a landed pilot and destroy it.
        [HarmonyPatch(typeof(PilotDismounted), nameof(PilotDismounted.Capture))]
        [HarmonyPrefix]
        private static bool CapturePrefix(PilotDismounted __instance)
        {
            return !NetSync.IsProtected(__instance);
        }

        // Pilots have a zero-value unit definition, so AI never engages them. Give only the player-controlled
        // pilot a target value for gun weapons (no missiles/SAMs), scaled by range like the game's own scoring.
        [HarmonyPatch(typeof(CombatAI), nameof(CombatAI.AnalyzeTarget))]
        [HarmonyPostfix]
        private static void AnalyzeTargetPostfix(WeaponStation weaponStation, Unit analyzer, TrackingInfo trackingInfo, float targetDistance, ref OpportunityThreat __result)
        {
            if (!Plugin.AiTargetsInfantry.Value || __result.opportunity > 0f) return;
            if (weaponStation?.WeaponInfo == null || !weaponStation.WeaponInfo.gun) return;
            if (!trackingInfo.TryGetUnit(out Unit unit) || !(unit is PilotDismounted p) || !NetSync.IsProtectedOrAi(p)) return;
            // While seated the pilot's collider is off, so AI would waste fire on it; the vehicle is a target anyway.
            if (NetSync.InVehicle(p) || analyzer.NetworkHQ == p.NetworkHQ) return;

            float maxRange = weaponStation.WeaponInfo.targetRequirements.maxRange;
            if (targetDistance < 0f) targetDistance = Vector3.Distance(analyzer.transform.position, p.transform.position);
            float range = Mathf.Min(maxRange, Plugin.AiEngageRange.Value);
            if (targetDistance > range) return;
            __result = new OpportunityThreat(Plugin.AiInfantryPriority.Value * (1f - targetDistance / range), __result.threat);
        }

        // Diagnostics: why a player's soldier died (impact check vs damage).
        [HarmonyPatch(typeof(PilotDismounted), "KillPilot")]
        [HarmonyPrefix]
        private static bool KillPilotPrefix(PilotDismounted __instance)
        {
            if (!NetSync.IsProtected(__instance)) return true;
            // The impact check compares velocity between physics steps; the first steps after a network spawn on
            // the ground can jump and kill a player's soldier before they ever control it (seen in MP testing).
            if (__instance.timeSinceSpawn < 5f)
            {
                Plugin.Log.LogInfo($"Ignored spawn-time impact check on {__instance.UniqueName}");
                return false;
            }
            Plugin.Log.LogWarning($"Soldier {__instance.UniqueName} killed by impact: velocity {__instance.rb.velocity.magnitude:F1} m/s");
            return true;
        }

        [HarmonyPatch(typeof(PilotDismounted), nameof(PilotDismounted.TakeDamage))]
        [HarmonyPrefix]
        private static void PilotTakeDamagePrefix(PilotDismounted __instance, float pierceDamage, float blastDamage, float fireDamage, float collisionDamage, PersistentID dealerID)
        {
            if (!NetSync.IsProtected(__instance)) return;
            UnitRegistry.TryGetUnit(dealerID, out Unit dealer);
            Plugin.Log.LogInfo($"Soldier {__instance.UniqueName} hit: pierce {pierceDamage:F0} blast {blastDamage:F0} fire {fireDamage:F0} collision {collisionDamage:F0} by {dealer?.unitName ?? "?"}");
        }

        [HarmonyPatch(typeof(Missile), nameof(Missile.TakeDamage))]
        [HarmonyPrefix]
        private static void MissileTakeDamagePrefix(Missile __instance, float impactDamage, PersistentID dealerID)
        {
            if (!Launcher.Launched.ContainsKey(__instance) || impactDamage <= 0f) return;
            UnitRegistry.TryGetUnit(dealerID, out Unit dealer);
            Plugin.Log.LogWarning($"Launcher missile took impact damage {impactDamage:F1} (from {dealer?.unitName ?? "?"}) at {__instance.timeSinceSpawn:F2} s");
        }

        // Diagnostics for launcher missiles: log how and where they end.
        [HarmonyPatch(typeof(Missile), nameof(Missile.Detonate))]
        [HarmonyPrefix]
        private static void MissileDetonatePrefix(Missile __instance, bool hitArmor, bool hitTerrain)
        {
            if (!Launcher.Launched.TryGetValue(__instance, out Unit target)) return;
            Launcher.Launched.Remove(__instance);
            float dist = target != null ? Vector3.Distance(__instance.transform.position, target.transform.position) : -1f;
            Plugin.Log.LogInfo($"Launcher missile {__instance.definition?.unitName} detonating: armed={__instance.IsArmed()} hitArmor={hitArmor} hitTerrain={hitTerrain} distToTarget={dist:F1} m target={target?.unitName}");
            if (__instance.timeSinceSpawn < 1f)
            {
                Plugin.Log.LogWarning($"  early detonation: t={__instance.timeSinceSpawn:F2}s globalY={__instance.transform.position.GlobalY():F1} ownerGlobalY={(__instance.owner != null ? __instance.owner.transform.position.GlobalY() : 0f):F1} y-sea={__instance.transform.position.y - Datum.LocalSeaY:F1} speed={__instance.rb.velocity.magnitude:F0} " +
                                      $"owner={__instance.owner?.unitName} ownerDist={(__instance.owner != null ? Vector3.Distance(__instance.owner.transform.position, __instance.transform.position) : -1f):F1}" +
                                      "\n" + System.Environment.StackTrace);
            }
        }

        // PilotDismounted.CheckForCapture "returns" a landed pilot to base (destroying it) when near a
        // friendly airbase. Only lie for the controlled pilot's exact position; every other caller is untouched.
        [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.AnyNearAirbase))]
        [HarmonyPrefix]
        private static bool AnyNearAirbasePrefix(Vector3 fromPosition, ref Airbase airbase, ref bool __result)
        {
            if (AirbaseServices.BypassAirbasePatch) return true;
            bool isSoldier = false;
            foreach (PilotDismounted p in NetSync.AllProtected())
            {
                if ((fromPosition - p.transform.position).sqrMagnitude <= 1f) { isSoldier = true; break; }
            }
            if (!isSoldier) return true;
            airbase = null;
            __result = false;
            return false;
        }
    }
}

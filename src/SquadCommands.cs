using System.Linq;
using UnityEngine;

namespace NOInfantry
{
    // Player-facing squad commands and the optional automatic enemy patrols. AI always runs on the server/host.
    internal static class SquadCommands
    {
        private static float nextAutoPatrol;
        private static float lastRequest;

        // From the local player's key press.
        internal static void Request(PilotDismounted soldier, byte kind)
        {
            if (soldier == null || Time.time < lastRequest + 2f) return;
            lastRequest = Time.time;
            if (Combat.IsServer) Execute(soldier, kind);
            else if (NetSync.ServerHasMod) NetSync.SendSquad(soldier, kind);
        }

        // Server side, for a local or remote player's soldier.
        internal static void Execute(PilotDismounted soldier, byte kind)
        {
            if (soldier == null || soldier.disabled || soldier.NetworkHQ == null) return;
            BorrowedAssets.Find(); // AI uses the borrowed rifle sound/impacts
            if (kind == 1)
            {
                AiSquads.SpawnFireteam(soldier, Plugin.SquadSize.Value);
            }
            else if (kind == 2)
            {
                SpawnPatrolAgainst(soldier);
            }
        }

        private static void SpawnPatrolAgainst(PilotDismounted soldier)
        {
            FactionHQ enemy = FactionRegistry.GetAllHQs().FirstOrDefault(h => h != null && h != soldier.NetworkHQ);
            if (enemy == null) return;
            // Pick a dry spot at the configured distance (up to 12 tries around the compass).
            float dist = Plugin.PatrolDistance.Value;
            float start = Random.Range(0f, 360f);
            for (int i = 0; i < 12; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, start + i * 30f, 0f) * Vector3.forward;
                Vector3 spot = AiSquads.GroundPoint(soldier.transform.position + dir * dist);
                if (spot.GlobalY() < 2f) continue; // water
                AiSquads.SpawnPatrol(enemy, spot, soldier.transform.position, Plugin.PatrolSize.Value);
                return;
            }
            Plugin.Log.LogInfo("No dry ground for an enemy patrol nearby");
        }

        // Server: every AutoPatrolInterval, send a patrol at a random soldier on foot (if enabled).
        internal static void AutoPatrols()
        {
            if (!Plugin.EnemyPatrols.Value || Time.time < nextAutoPatrol) return;
            nextAutoPatrol = Time.time + Mathf.Max(30f, Plugin.PatrolInterval.Value);
            PilotDismounted[] onFoot = NetSync.AllControlled().Where(p => p != null && !p.disabled && !NetSync.InVehicle(p)).ToArray();
            if (onFoot.Length == 0) return;
            SpawnPatrolAgainst(onFoot[Random.Range(0, onFoot.Length)]);
        }
    }
}

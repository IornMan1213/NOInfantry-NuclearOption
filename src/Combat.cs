using NuclearOption.Networking;
using UnityEngine;

namespace NOInfantry
{
    // Rifle hit logic shared by the local soldier and the server acting for remote soldiers.
    internal static class Combat
    {
        internal static bool IsServer => NetworkManagerNuclearOption.i != null && NetworkManagerNuclearOption.i.Server.Active;

        // A client connected to someone else's server (not host, not single-player).
        internal static bool IsPureClient => NetworkManagerNuclearOption.i != null && !NetworkManagerNuclearOption.i.Server.Active
                                             && NetworkManagerNuclearOption.i.Client.Active;

        internal static bool Raycast(Vector3 origin, Vector3 dir, PilotDismounted self, out RaycastHit best)
        {
            best = default;
            RaycastHit[] results = Physics.RaycastAll(origin, dir, Plugin.Range.Value, ~(int)PhysicsLayers.ExclusionZonesMask, QueryTriggerInteraction.Ignore);
            float bestDist = float.MaxValue;
            foreach (RaycastHit h in results)
            {
                if (self != null && h.collider.transform.IsChildOf(self.transform)) continue;
                if (h.distance < bestDist)
                {
                    bestDist = h.distance;
                    best = h;
                }
            }
            return bestDist < float.MaxValue;
        }

        internal static bool IsWater(RaycastHit hit) =>
            hit.point.y < Datum.LocalSeaY || hit.collider.sharedMaterial == GameAssets.i.WaterMaterial;

        internal static void PlayImpact(RaycastHit hit, Vector3 dir)
        {
            bool armor = hit.collider.sharedMaterial != GameAssets.i.terrainMaterial;
            BorrowedAssets.PlayImpact(hit.point, hit.normal, dir, armor, IsWater(hit));
        }

        // Server only. Same path the game uses for confirmed gun hits: penetration vs armor, damage credited to the shooter.
        // Returns the unit hit (or null).
        internal static Unit ApplyRifleDamage(PilotDismounted shooter, RaycastHit hit, Vector3 dir)
        {
            IDamageable target = hit.collider.GetComponentInParent<IDamageable>();
            if (target == null || IsWater(hit)) return null;
            float muzzle = Plugin.MuzzleVelocity.Value;
            if (IsServer)
            {
                DamageEffects.ArmorPenetrate(hit.point - dir * 0.5f, dir * muzzle, muzzle, Plugin.PierceDamage.Value, 0f, shooter.persistentID);
            }
            return target.GetUnit();
        }
    }
}

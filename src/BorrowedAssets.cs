using HarmonyLib;
using UnityEngine;

namespace NOInfantry
{
    // Reuses the game's own sounds and effects from whatever gun and warhead prefabs are loaded,
    // so we ship no extracted assets. Everything is optional: missing pieces just skip the effect.
    internal static class BorrowedAssets
    {
        internal static AudioClip[] RifleShots;
        internal static GameObject ImpactGround;
        internal static GameObject ImpactArmor;
        internal static GameObject ImpactWater;
        internal static GameObject BlastTerrain;
        internal static GameObject BlastAir;

        private static bool searched;

        internal static void Find()
        {
            if (searched) return;
            searched = true;
            FindGun();
            FindWarhead();
        }

        private static void FindGun()
        {
            // Smallest-calibre gun with fire sounds is the closest thing to a rifle.
            Gun best = null;
            float bestPierce = float.MaxValue;
            foreach (Gun gun in Resources.FindObjectsOfTypeAll<Gun>())
            {
                if (gun.info == null) continue;
                var clips = Traverse.Create(gun).Field("fireSounds").GetValue<AudioClip[]>();
                if (clips == null || clips.Length == 0) continue;
                if (gun.info.pierceDamage < bestPierce)
                {
                    bestPierce = gun.info.pierceDamage;
                    best = gun;
                }
            }
            if (best == null)
            {
                Plugin.Log.LogWarning("No gun prefab with fire sounds loaded; rifle will be silent.");
                return;
            }
            var t = Traverse.Create(best);
            RifleShots = t.Field("fireSounds").GetValue<AudioClip[]>();
            ImpactGround = t.Field("impactEffectGround").GetValue<GameObject>();
            ImpactArmor = t.Field("impactEffectArmor").GetValue<GameObject>();
            ImpactWater = t.Field("impactEffectWater").GetValue<GameObject>();
            Plugin.Log.LogInfo($"Rifle sounds/effects borrowed from gun '{best.info.weaponName}' (pierce {best.info.pierceDamage})");
        }

        private static void FindWarhead()
        {
            Missile best = null;
            float bestYield = float.MaxValue;
            foreach (Missile m in Resources.FindObjectsOfTypeAll<Missile>())
            {
                float yield = Traverse.Create(m).Field("blastYield").GetValue<float>();
                if (yield <= 0f || yield >= bestYield) continue;
                object warhead = Traverse.Create(m).Field("warhead").GetValue();
                if (warhead == null || Traverse.Create(warhead).Field("terrainEffect").GetValue<GameObject>() == null) continue;
                bestYield = yield;
                best = m;
            }
            if (best == null)
            {
                Plugin.Log.LogWarning("No warhead prefab loaded; grenades will have no visual effect.");
                return;
            }
            var w = Traverse.Create(Traverse.Create(best).Field("warhead").GetValue());
            BlastTerrain = w.Field("terrainEffect").GetValue<GameObject>();
            BlastAir = w.Field("airEffect").GetValue<GameObject>();
            Plugin.Log.LogInfo($"Grenade effects borrowed from '{best.name}' (yield {bestYield})");
        }

        internal static void PlayImpact(Vector3 point, Vector3 normal, Vector3 dir, bool armor, bool water)
        {
            GameObject prefab = water ? ImpactWater : armor ? ImpactArmor : ImpactGround;
            if (prefab == null || SceneSingleton<ParticleEffectManager>.i == null) return;
            ParticleEffectManager.PrefabEffect fx = SceneSingleton<ParticleEffectManager>.i.GetPrefabEffect(prefab);
            fx?.Play(point, Quaternion.LookRotation(Vector3.Reflect(dir, normal) + normal, Vector3.up));
        }

        internal static void PlayBlast(Vector3 point, Vector3 normal, bool onGround)
        {
            GameObject prefab = onGround && BlastTerrain != null ? BlastTerrain : BlastAir;
            if (prefab == null) return;
            GameObject go = Object.Instantiate(prefab, Datum.origin);
            go.transform.SetPositionAndRotation(point, Quaternion.LookRotation(normal));
            Object.Destroy(go, 30f);
        }
    }
}

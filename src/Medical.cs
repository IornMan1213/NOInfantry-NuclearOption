using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NOInfantry
{
    // Downed / revive / heal. The server decides: a lethal hit on a soldier on foot (player or AI) puts it down with
    // 1 HP to bleed out instead of killing it, unless the hit is overwhelming. A friend next to it can revive it (medics
    // faster); medics can also patch up the living. Health changes go through the game's own RpcTakeDamage, so every
    // client's copy of the soldier stays in step (a negative amount heals).
    internal static class Medical
    {
        // Server: downed soldier -> time it bleeds out.
        internal static readonly Dictionary<PilotDismounted, float> Downed = new Dictionary<PilotDismounted, float>();

        // Clients: soldiers the server says are down (by net id), with the local time each bleeds out.
        internal static readonly Dictionary<uint, float> DownedIds = new Dictionary<uint, float>();

        internal static int Revives, Bleedouts, Downs;

        private static readonly HarmonyLib.AccessTools.FieldRef<PilotDismounted, float> hp =
            HarmonyLib.AccessTools.FieldRefAccess<PilotDismounted, float>("hitPoints");

        internal static float Health(PilotDismounted p) => p != null ? hp(p) : 0f;

        internal static bool IsDowned(PilotDismounted p)
        {
            if (p == null || p.disabled) return false;
            if (Combat.IsServer) return Downed.ContainsKey(p);
            return DownedIds.ContainsKey(NetSync.NetIdOf(p));
        }

        internal static float BleedLeft(PilotDismounted p)
        {
            if (p == null) return 0f;
            if (Combat.IsServer) return Downed.TryGetValue(p, out float t) ? t - Time.time : 0f;
            return DownedIds.TryGetValue(NetSync.NetIdOf(p), out float u) ? u - Time.time : 0f;
        }

        // ---------------- server ----------------

        // From the TakeDamage prefix. Returns false to swallow a lethal hit (the soldier goes down instead).
        internal static bool OnDamage(PilotDismounted p, float pierce, float blast, float affected, float fire)
        {
            if (!Plugin.ReviveEnabled.Value || !Combat.IsServer || p.disabled) return true;
            if (!(NetSync.IsControlled(p) || AiSquads.IsAi(p) || GroundWar.Deployed.Contains(p)) || NetSync.InVehicle(p)) return true;
            ArmorProperties a = p.GetArmorProperties();
            float dmg = Mathf.Max(pierce - a.pierceArmor, 0f) / a.pierceTolerance
                        + Mathf.Max(blast - a.blastArmor, 0f) * affected / a.blastTolerance
                        + Mathf.Max(fire - a.fireArmor, 0f) / a.fireTolerance;
            float health = hp(p);
            if (health - dmg > 0f) return true;                      // not lethal: the game handles it
            if (Downed.ContainsKey(p)) return true;                   // finished off
            if (dmg - health > Plugin.OverkillDamage.Value) return true; // torn apart: no saving this one
            GoDown(p);
            return false;
        }

        private static void GoDown(PilotDismounted p)
        {
            float health = hp(p);
            SetHealth(p, 1f); // 1 HP left
            Downed[p] = Time.time + Plugin.BleedOutTime.Value;
            Downs++;
            NetSync.BroadcastDowned(p, true, Plugin.BleedOutTime.Value);
            Plugin.Log.LogInfo($"{p.unitName} is down ({Plugin.BleedOutTime.Value:F0} s to bleed out)");
        }

        // Through the game's own RPC (a negative amount heals) so every copy agrees. A dedicated server has no local
        // client to run the RPC on, so set its copy directly there.
        private static void SetHealth(PilotDismounted p, float to)
        {
            float delta = hp(p) - to;
            p.RpcTakeDamage(delta);
            NuclearOption.Networking.NetworkManagerNuclearOption nm = NuclearOption.Networking.NetworkManagerNuclearOption.i;
            if (nm == null || nm.Client == null || !nm.Client.Active) hp(p) = to;
        }

        internal static bool Revive(PilotDismounted reviver, PilotDismounted target, bool medic)
        {
            if (!Combat.IsServer || target == null || !Downed.ContainsKey(target) || target.disabled) return false;
            if (reviver != null && (reviver.disabled || Downed.ContainsKey(reviver) || reviver.NetworkHQ != target.NetworkHQ
                                    || Vector3.Distance(reviver.transform.position, target.transform.position) > 4f)) return false;
            Downed.Remove(target);
            float to = medic ? Plugin.ReviveHealth.Value * 2f : Plugin.ReviveHealth.Value;
            SetHealth(target, to);
            Revives++;
            NetSync.BroadcastDowned(target, false, 0f);
            Plugin.Log.LogInfo($"{target.unitName} revived by {(reviver != null ? reviver.unitName : "?")} ({to:F0} HP)");
            return true;
        }

        internal static bool Heal(PilotDismounted medic, PilotDismounted target)
        {
            if (!Combat.IsServer || target == null || target.disabled || Downed.ContainsKey(target)) return false;
            if (medic == null || medic.disabled || medic.NetworkHQ != target.NetworkHQ
                || Vector3.Distance(medic.transform.position, target.transform.position) > 4f) return false;
            float health = hp(target);
            float to = Mathf.Min(100f, health + Plugin.BandageHeal.Value);
            if (to <= health) return false;
            SetHealth(target, to);
            Plugin.Log.LogInfo($"{medic.unitName} patched up {target.unitName}: {health:F0} -> {to:F0} HP");
            return true;
        }

        internal static void BleedOut(PilotDismounted p)
        {
            if (p == null) return;
            Downed.Remove(p);
            if (p.disabled) return;
            Bleedouts++;
            NetSync.BroadcastDowned(p, false, 0f);
            SetHealth(p, 0f);
            p.Networkdisabled = true;
            p.SetPilotState(PilotDismounted.PilotState.dead);
            Plugin.Log.LogInfo($"{p.unitName} bled out");
        }

        internal static void Tick()
        {
            if (Downed.Count == 0) return;
            foreach (PilotDismounted p in Downed.Keys.ToList())
            {
                if (p == null || p.disabled) { Downed.Remove(p); continue; }
                if (Time.time >= Downed[p]) BleedOut(p);
            }
        }

        // ---------------- local player requests ----------------

        internal const byte KindRevive = 0, KindHeal = 1, KindGiveUp = 2;

        internal static void Request(PilotDismounted me, PilotDismounted target, byte kind, bool medic)
        {
            if (Combat.IsServer)
            {
                if (kind == KindRevive) Revive(me, target, medic);
                else if (kind == KindHeal) Heal(me, target);
                else if (kind == KindGiveUp && Downed.ContainsKey(me)) BleedOut(me);
            }
            else NetSync.SendRevive(me, target, kind);
        }

        // Downed friendlies near `p` (for prompts and AI).
        internal static IEnumerable<PilotDismounted> DownedNear(PilotDismounted p, float range)
        {
            foreach (Unit u in UnitRegistry.allUnits)
            {
                if (u is PilotDismounted d && d != p && !d.disabled && d.NetworkHQ == p.NetworkHQ && IsDowned(d)
                    && Vector3.Distance(d.transform.position, p.transform.position) < range) yield return d;
            }
        }

        internal static void ResetMission()
        {
            Downed.Clear();
            DownedIds.Clear();
        }
    }
}

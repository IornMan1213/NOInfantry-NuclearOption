using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NOInfantry
{
    // Front-line squads ride a truck toward their objective (the game's own ground-vehicle pathfinding drives it),
    // then dismount short of it, when the enemy shows up, or when the truck is hit. Server only.
    internal class Convoy
    {
        internal GroundVehicle Truck;
        internal readonly List<AiSoldier> Riders = new List<AiSoldier>();
        internal Airbase Objective;
        internal float Started;
        internal float StoppedSince = -1f;
    }

    internal static class Transport
    {
        internal static readonly List<Convoy> Convoys = new List<Convoy>();
        internal static int Sent, Dismounts;

        private static VehicleDefinition truckDef;
        private static bool searched;

        // The troop truck: configured by name, else the first of a few stock troop carriers, else the lightest
        // unarmed vehicle that counts for capture.
        internal static VehicleDefinition TruckDef
        {
            get
            {
                if (searched) return truckDef;
                searched = true;
                IEnumerable<VehicleDefinition> all = Encyclopedia.i.vehicles.Where(v => v != null && v.unitPrefab != null && v.unitPrefab.GetComponent<GroundVehicle>() != null);
                string wanted = Plugin.TransportVehicle.Value;
                truckDef = string.IsNullOrEmpty(wanted) ? null : all.FirstOrDefault(v => v.unitName.Equals(wanted, System.StringComparison.OrdinalIgnoreCase));
                foreach (string name in new[] { "MSV MRAP", "LCV45 Recon Truck", "MSV Flatbed", "HLT Flatbed" })
                {
                    if (truckDef != null) break;
                    truckDef = all.FirstOrDefault(v => v.unitName == name);
                }
                truckDef ??= all.Where(v => v.captureStrength > 0f && v.unitPrefab.GetComponentsInChildren<Turret>(true).Length == 0).OrderBy(v => v.mass).FirstOrDefault();
                Plugin.Log.LogInfo($"Troop transport: {truckDef?.unitName ?? "none (squads walk)"}");
                return truckDef;
            }
        }

        // Spawn a truck at `start` with a squad aboard, driving to the objective.
        internal static Convoy Send(FactionHQ hq, Vector3 start, Airbase objective, int size)
        {
            VehicleDefinition def = TruckDef;
            if (def == null) return null;
            Vector3 to = objective.center.position - start;
            to.y = 0f;
            Quaternion rot = Quaternion.LookRotation(to.sqrMagnitude > 1f ? to.normalized : Vector3.forward);
            GroundVehicle truck = NetworkSceneSingleton<Spawner>.i.SpawnVehicle(def.unitPrefab, (start + Vector3.up * 1f).ToGlobalPosition(), rot, Vector3.zero,
                hq, $"NOI Transport {++Sent}", 1f, false, null);
            if (truck == null) return null;

            var c = new Convoy { Truck = truck, Objective = objective, Started = Time.time };
            foreach (AiSoldier ai in AiSquads.SpawnPatrol(hq, start + rot * Vector3.back * 8f, objective.center.position, size))
            {
                ai.IsPatrol = false;
                ai.Order = AiOrder.Free;
                ai.Objective = objective;
                ai.Riding = truck;
                ai.Seat = c.Riders.Count;
                ai.Pilot.SetCollidable(false);
                c.Riders.Add(ai);
            }
            Convoys.Add(c);
            Drive(c);
            Plugin.Log.LogInfo($"Front line: {hq.faction.factionName} {def.unitName} with {c.Riders.Count} soldiers heading for {GroundWar.AirbaseName(objective)} ({to.magnitude:F0} m)");
            return c;
        }

        private static void Drive(Convoy c)
        {
            Vector3 obj = c.Objective.center.position;
            Vector3 from = c.Truck.transform.position - obj;
            from.y = 0f;
            Vector3 drop = obj + from.normalized * Plugin.DismountDistance.Value;
            c.Truck.UnitCommand?.SetDestination(drop.ToGlobalPosition(), playerCommand: false);
        }

        // Seat position on the truck for rider i (rows on the back).
        internal static Vector3 Seat(GroundVehicle truck, int i)
        {
            float h = truck.definition != null ? truck.definition.height : 2.5f;
            return truck.transform.TransformPoint(new Vector3(i % 2 == 0 ? -0.55f : 0.55f, h * 0.55f, -0.8f - (i / 2) * 0.9f));
        }

        // Server, once a second.
        internal static void Tick()
        {
            for (int i = Convoys.Count - 1; i >= 0; i--)
            {
                Convoy c = Convoys[i];
                c.Riders.RemoveAll(r => r.Pilot == null || r.Pilot.disabled || (object)r.Riding != c.Truck);
                if (c.Riders.Count == 0) { Convoys.RemoveAt(i); continue; }

                GroundVehicle t = c.Truck;
                string why = null;
                if (t == null || t.disabled) why = "truck hit";
                else
                {
                    float dist = Vector3.Distance(t.transform.position, c.Objective.center.position);
                    if (dist < Plugin.DismountDistance.Value + 60f) why = "arrived";
                    else if (GroundWar.EnemiesNear(t.NetworkHQ, t.transform.position, 250f) > 0) why = "contact";
                    else if (Time.time - c.Started > 900f) why = "too long on the road";
                    else
                    {
                        // Stuck (or waiting at a blocked road) for 20 s after the first 15 s: walk from here.
                        bool stopped = Mathf.Abs(t.speed) < 0.7f && Time.time - c.Started > 15f;
                        if (!stopped) c.StoppedSince = -1f;
                        else if (c.StoppedSince < 0f) c.StoppedSince = Time.time;
                        else if (Time.time - c.StoppedSince > 20f) why = "truck stuck";
                    }
                }
                if (why != null)
                {
                    Dismount(c, why);
                    Convoys.RemoveAt(i);
                }
            }
        }

        internal static void Dismount(Convoy c, string why)
        {
            GroundVehicle t = c.Truck;
            bool wrecked = t == null || t.disabled;
            Vector3 center = c.Riders[0].Pilot.transform.position;
            Vector3 side = t != null ? t.transform.right : Vector3.right;
            Vector3 back = t != null ? -t.transform.forward : Vector3.back;
            float radius = t != null ? t.maxRadius : 2f;
            int n = 0;
            foreach (AiSoldier ai in c.Riders)
            {
                PilotDismounted p = ai.Pilot;
                Vector3 spot = center + side * ((n % 2 == 0 ? -1f : 1f) * (radius + 1.5f)) + back * (n / 2) * 1.8f;
                Vector3 g = AiSquads.GroundPoint(spot) + Vector3.up * SoldierMotor.HoverHeight;
                p.transform.position = g;
                p.rb.position = g;
                p.rb.velocity = t != null ? t.rb.velocity * 0.3f : Vector3.zero;
                p.SetCollidable(true);
                SetVisible(p, true);
                ai.Riding = null;
                ai.NextThink = Time.time + 0.5f + n * 0.2f;
                if (wrecked)
                {
                    // Bailing out of a burning truck: everyone is hurt, some badly.
                    p.TakeDamage(Random.Range(30f, 160f), 0f, 0f, 0f, 0f, PersistentID.None);
                }
                n++;
            }
            Dismounts++;
            Plugin.Log.LogInfo($"Front line: {c.Riders.Count} soldiers dismount ({why}) {Vector3.Distance(center, c.Objective.center.position):F0} m from {GroundWar.AirbaseName(c.Objective)}");
        }

        internal static void SetVisible(PilotDismounted p, bool visible)
        {
            foreach (Renderer r in p.GetComponentsInChildren<Renderer>(true))
            {
                if (r.enabled != visible) r.enabled = visible;
            }
        }

        internal static void ResetMission()
        {
            Convoys.Clear();
            searched = false;
            truckDef = null;
        }
    }
}

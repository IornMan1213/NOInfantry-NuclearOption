using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Mirage;
using NuclearOption.Networking;
using UnityEngine;

namespace NOInfantry
{
    internal enum Kit : byte { Rifleman, AntiTank, AntiAir, Medic }

    // "Infantry first": deploy on foot from the spawn menu with a kit, capture airbases on foot, a living AI
    // front line, and a motor pool at friendly airbases. Server-authoritative; clients ask through NetSync.
    internal static class GroundWar
    {
        // ---------------- kits ----------------

        internal static string KitName(Kit k) => k == Kit.AntiTank ? "Anti-Tank" : k == Kit.AntiAir ? "Anti-Air" : k == Kit.Medic ? "Medic" : "Rifleman";

        internal static string KitBlurb(Kit k) => k == Kit.AntiTank
            ? "Rifle, 2 grenades, guided anti-surface rockets"
            : k == Kit.AntiAir
                ? "Rifle, 2 grenades, heat-seeking anti-air missiles"
                : k == Kit.Medic
                    ? "Rifle, 2 grenades, bandages; revives twice as fast"
                    : "Rifle, 6 grenades, extra magazines";

        internal static int KitGrenades(Kit k) => k == Kit.Rifleman ? Plugin.GrenadeCount.Value * 2 : Mathf.Max(1, Plugin.GrenadeCount.Value - 1);
        internal static int KitRockets(Kit k) => k == Kit.Rifleman || k == Kit.Medic ? 0 : Plugin.LauncherAmmo.Value;
        internal static bool KitAllowsAir(Kit k) => k == Kit.AntiAir;
        internal static bool KitAllowsSurface(Kit k) => k == Kit.AntiTank;

        // The kit of the local player's soldier (chosen in the deploy menu; ejected pilots carry everything).
        internal static Kit? LocalKit;

        // Server: kits of deployed soldiers, so the server can refuse launches the kit doesn't allow.
        internal static readonly Dictionary<PilotDismounted, Kit> Kits = new Dictionary<PilotDismounted, Kit>();

        // Server: soldiers deployed through the menu, protected from the landed-pilot logic before anyone controls them.
        internal static readonly HashSet<PilotDismounted> Deployed = new HashSet<PilotDismounted>();

        internal static float LocalDeployAt = -100f;
        internal static bool RecentlyDeployed => Time.time - LocalDeployAt < 30f;
        internal static int DeployCount;

        // ---------------- deploy on foot ----------------

        private static readonly AccessTools.FieldRef<AircraftSelectionMenu, Airbase> menuAirbase =
            AccessTools.FieldRefAccess<AircraftSelectionMenu, Airbase>("airbase");

        private static AircraftSelectionMenu openMenu;
        private static AircraftSelectionMenu loggedPanelFor;
        private static float nextMenuScan;

        internal static AircraftSelectionMenu OpenMenu
        {
            get
            {
                if (Time.unscaledTime >= nextMenuScan)
                {
                    nextMenuScan = Time.unscaledTime + 0.3f;
                    openMenu = Object.FindObjectOfType<AircraftSelectionMenu>();
                }
                return openMenu;
            }
        }

        internal static Airbase MenuAirbase(AircraftSelectionMenu m) => m != null ? menuAirbase(m) : null;

        // ---------------- rally points ----------------

        // Server: each player's rally point (global, so floating-origin shifts don't move it).
        internal static readonly Dictionary<Player, GlobalPosition> Rally = new Dictionary<Player, GlobalPosition>();
        private static readonly Dictionary<Player, float> rallyPlacedAt = new Dictionary<Player, float>();

        // Local player: our rally point as the server last confirmed it.
        internal static GlobalPosition? LocalRally;
        internal static bool DeployAtRally; // the deploy panel's location toggle

        internal static void RequestRally(PilotDismounted soldier)
        {
            if (soldier == null) return;
            if (Combat.IsServer) ServerPlaceRally(soldier);
            else NetSync.SendSquad(soldier, SquadKinds.Rally);
        }

        // Server: place (or move) the owner's rally point where the soldier stands. Returns a reason when refused.
        internal static string ServerPlaceRally(PilotDismounted soldier)
        {
            Player player = soldier.Networkplayer;
            string refused = null;
            if (player == null) refused = "no player";
            else if (soldier.disabled || Medical.IsDowned(soldier) || NetSync.InVehicle(soldier)) refused = "must be on your feet";
            else if (rallyPlacedAt.TryGetValue(player, out float t) && Time.time < t + Plugin.RallyCooldown.Value)
                refused = $"wait {t + Plugin.RallyCooldown.Value - Time.time:F0} s";
            else if (EnemiesNear(soldier.NetworkHQ, soldier.transform.position, Plugin.RallyEnemyRadius.Value) > 0) refused = "enemies too close";
            if (refused != null)
            {
                NetSync.SendRallyTo(player, soldier.transform.position, placed: false, refused);
                return refused;
            }
            Rally[player] = soldier.transform.position.ToGlobalPosition();
            rallyPlacedAt[player] = Time.time;
            NetSync.SendRallyTo(player, soldier.transform.position, placed: true, "placed");
            Plugin.Log.LogInfo($"Rally point for {PlayerLabel(player)} at {Rally[player]}");
            return null;
        }

        // Enemy soldiers on foot and enemy ground vehicles within `radius`.
        internal static int EnemiesNear(FactionHQ hq, Vector3 at, float radius)
        {
            int n = EnemySoldiersNear(hq, at, radius);
            foreach (Unit u in UnitRegistry.allUnits)
            {
                if (u is GroundVehicle && !u.disabled && u.NetworkHQ != null && u.NetworkHQ != hq && Vector3.Distance(u.transform.position, at) < radius) n++;
            }
            return n;
        }

        // Server, from CaptureTick: enemies overrunning a rally point destroy it.
        private static void RallyTick()
        {
            foreach (Player pl in Rally.Keys.ToList())
            {
                if (pl == null) { Rally.Remove(pl); continue; }
                Vector3 at = Rally[pl].ToLocalPosition();
                if (pl.HQ == null || EnemiesNear(pl.HQ, at, Plugin.RallyOverrunRadius.Value) > 0)
                {
                    Rally.Remove(pl);
                    NetSync.SendRallyTo(pl, at, placed: false, "overrun by the enemy");
                    Plugin.Log.LogInfo($"Rally point of {PlayerLabel(pl)} overrun");
                }
            }
        }

        // Local player: from the deploy panel (or the test harness).
        internal static bool RequestDeploy(Airbase airbase, Kit kit, bool atRally = false)
        {
            if (airbase == null || !GameManager.GetLocalPlayer<Player>(out Player player)) return false;
            if (!Combat.IsServer && !NetSync.ServerHasMod)
            {
                Plugin.Log.LogWarning("This server does not run NO Infantry: deploying on foot is unavailable.");
                return false;
            }
            LocalKit = kit;
            LocalDeployAt = Time.time;
            PlayerPrefsKit = kit;
            if (Combat.IsServer) ServerDeploy(player, airbase.center.position, kit, atRally);
            else NetSync.SendDeploy(airbase.center.position, kit, atRally);

            AircraftSelectionMenu menu = OpenMenu;
            if (menu != null) AccessTools.Method(typeof(AircraftSelectionMenu), "HideSelection").Invoke(menu, null);
            openMenu = null;
            return true;
        }

        private static Kit PlayerPrefsKit
        {
            get => (Kit)Mathf.Clamp(Plugin.LastKit.Value, 0, 3);
            set => Plugin.LastKit.Value = (int)value;
        }

        // Server: spawn the player's soldier at a friendly airbase near `near`.
        internal static PilotDismounted ServerDeploy(Player player, Vector3 near, Kit kit, bool atRally = false)
        {
            if (!Combat.IsServer || player == null || player.HQ == null) return null;
            Airbase airbase = FactionRegistry.airbaseLookup.Values
                .Where(a => a != null && !a.disabled && a.CurrentHQ == player.HQ && a.center != null)
                .OrderBy(a => (a.center.position - near).sqrMagnitude).FirstOrDefault();
            bool rally = atRally && Rally.ContainsKey(player);
            if (!rally && (airbase == null || Vector3.Distance(airbase.center.position, near) > 3000f))
            {
                Plugin.Log.LogWarning($"Deploy refused for {PlayerLabel(player)}: no friendly airbase there");
                return null;
            }

            // One soldier per player: the old one goes home (its body is replaced by the new deployment).
            foreach (Unit u in UnitRegistry.allUnits.ToArray())
            {
                if (u is PilotDismounted old && !old.disabled && old.Networkplayer == player)
                {
                    NetSync.ForgetRemote(old);
                    old.NetworkunitState = Unit.UnitState.Returned;
                    old.Networkdisabled = true;
                    Object.Destroy(old.gameObject);
                }
            }

            Vector3 pos = rally ? RallySpot(Rally[player].ToLocalPosition()) : DeploySpot(airbase);
            Quaternion rot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            GameObject go = Object.Instantiate(GameAssets.i.pilotDismounted);
            go.transform.SetPositionAndRotation(pos, rot);
            PlaceBody(go, pos, rot);
            PilotDismounted p = go.GetComponent<PilotDismounted>();
            p.NetworkUniqueName = $"NOI Soldier {++DeployCount}";
            p.NetworkstartPosition = pos.ToGlobalPosition();
            p.NetworkstartRotation = rot;
            p.NetworkHQ = player.HQ;
            p.NetworkunitName = $"{KitName(kit)} ({PlayerLabel(player)})";
            p.Networkplayer = player;
            p.SetCollidable(enabled: true);
            Deployed.Add(p);
            Kits[p] = kit;
            NetworkManagerNuclearOption.i.ServerObjectManager.Spawn(go, player.Owner); // owned like the game's ejected pilots
            if (AutoTest.Requested) InfantryController.Instance.StartCoroutine(TraceSpawn(p));
            Plugin.Log.LogInfo(rally
                ? $"Deployed {PlayerLabel(player)} on foot at their rally point as {KitName(kit)}: global {pos.ToGlobalPosition()}"
                : $"Deployed {PlayerLabel(player)} on foot at {AirbaseName(airbase)} as {KitName(kit)}: global {pos.ToGlobalPosition()}, {Vector3.Distance(pos, airbase.center.position):F0} m from the centre");
            return p;
        }

        // Instantiate + transform.position leaves the Rigidbody at the prefab's origin until physics syncs, and the first
        // physics step then moved the soldier there (the local origin: in one test, the sea 500 m away). Move both.
        internal static void PlaceBody(GameObject go, Vector3 pos, Quaternion rot)
        {
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb == null) return;
            rb.position = pos;
            rb.rotation = rot;
            rb.velocity = Vector3.zero;
        }

        private static System.Collections.IEnumerator TraceSpawn(PilotDismounted p)
        {
            Collider col = HarmonyLib.Traverse.Create(p).Field("pilotCollider").GetValue<Collider>();
            for (int i = 0; i < 4 && p != null; i++)
            {
                bool ground = Physics.Raycast(p.transform.position, Vector3.down, out RaycastHit h, 50f, PhysicsLayers.StaticsMask);
                Plugin.Log.LogInfo($"  spawn trace {i}: globalY {p.transform.position.GlobalY():F2} rbY {p.rb.position.GlobalY():F2} vy {p.rb.velocity.y:F2} state {p.animationState} localSim {p.LocalSim} kin {p.rb.isKinematic} col {(col != null ? col.enabled.ToString() : "?")} radarAlt {p.radarAlt:F1} groundBelow {(ground ? h.distance.ToString("F2") + " " + h.collider.name : "none")} grav {p.rb.useGravity}");
                yield return new WaitForFixedUpdate();
            }
        }

        private static Vector3 RallySpot(Vector3 at)
        {
            for (int i = 0; i < 8; i++)
            {
                Vector3 offset = Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward * (2f + i * 0.5f);
                if (!StaticGround(at + offset, out Vector3 g) || g.GlobalY() < 0.5f) continue;
                if (Physics.CheckSphere(g + Vector3.up * 1.2f, 0.7f, ~(int)PhysicsLayers.ExclusionZonesMask, QueryTriggerInteraction.Ignore)) continue;
                return g + Vector3.up * SoldierMotor.HoverHeight;
            }
            StaticGround(at, out Vector3 fallback);
            return fallback + Vector3.up * SoldierMotor.HoverHeight;
        }

        // Open dry ground around the airbase centre. Only terrain and buildings count as ground: the selection
        // menu's preview aircraft once caught the ray, and the soldier fell into the sea when it was removed.
        private static Vector3 DeploySpot(Airbase airbase)
        {
            Vector3 c = airbase.center.position;
            for (int i = 0; i < 24; i++)
            {
                float dist = 40f + 10f * (i / 8);
                Vector3 offset = Quaternion.Euler(0f, i * 45f + (i / 8) * 22f + Random.Range(-10f, 10f), 0f) * Vector3.forward * dist;
                if (!StaticGround(c + offset, out Vector3 g) || g.GlobalY() < 1f) continue;
                if (Physics.CheckSphere(g + Vector3.up * 1.2f, 0.9f, ~(int)PhysicsLayers.ExclusionZonesMask, QueryTriggerInteraction.Ignore)) continue;
                return g + Vector3.up * SoldierMotor.HoverHeight;
            }
            StaticGround(c, out Vector3 fallback);
            return fallback + Vector3.up * SoldierMotor.HoverHeight;
        }

        internal static bool StaticGround(Vector3 p, out Vector3 ground)
        {
            if (Physics.Raycast(p + Vector3.up * 250f, Vector3.down, out RaycastHit hit, 600f, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
            {
                ground = hit.point;
                return true;
            }
            ground = p;
            return false;
        }

        internal static string PlayerLabel(Player p)
        {
            if (p == null) return "?";
            try { return p.GetDisplayName(PlayerNameContext.Other); } catch { return "player"; }
        }

        internal static string AirbaseName(Airbase a)
        {
            if (a == null) return "?";
            string n = a.SavedAirbase?.DisplayName;
            return string.IsNullOrEmpty(n) ? a.SavedAirbase?.UniqueName ?? a.name : n;
        }

        // The deploy panel, drawn over the game's aircraft selection menu.
        internal static void DrawDeployPanel()
        {
            if (!Plugin.GroundWarEnabled.Value) return;
            AircraftSelectionMenu menu = OpenMenu;
            Airbase airbase = MenuAirbase(menu);
            if (airbase == null) return;
            if (Event.current.type == EventType.Repaint && menu != loggedPanelFor)
            {
                loggedPanelFor = menu;
                Plugin.Log.LogInfo($"Deploy panel shown for {AirbaseName(airbase)} (map open {DynamicMap.mapMaximized}, menu active {menu.isActiveAndEnabled})");
            }

            float w = 330f, h = LocalRally.HasValue ? 252f : 214f;
            if (!LocalRally.HasValue) DeployAtRally = false;
            var box = new Rect(Screen.width - w - 24f, Screen.height * 0.5f - h * 0.5f, w, h);
            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.7f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(box.x + 12f, box.y + 8f, w - 24f, 22f), $"<b>DEPLOY ON FOOT</b>  at {AirbaseName(airbase)}");
            float y = box.y + 34f;
            if (LocalRally.HasValue)
            {
                float km = Vector3.Distance(LocalRally.Value.ToLocalPosition(), airbase.center.position) / 1000f;
                if (GUI.Button(new Rect(box.x + 12f, y, 150f, 30f), (DeployAtRally ? "" : "> ") + "Airbase")) DeployAtRally = false;
                if (GUI.Button(new Rect(box.x + 168f, y, 150f, 30f), (DeployAtRally ? "> " : "") + $"Rally ({km:F1} km)")) DeployAtRally = true;
                y += 38f;
            }
            Kit current = PlayerPrefsKit;
            for (int i = 0; i < 4; i++)
            {
                Kit k = (Kit)i;
                string label = $"{(k == current ? "> " : "")}{KitName(k)}";
                if (GUI.Button(new Rect(box.x + 12f, y, 110f, 36f), label))
                {
                    RequestDeploy(airbase, k, DeployAtRally);
                }
                GUI.Label(new Rect(box.x + 128f, y + 2f, w - 140f, 36f), $"<size=11>{KitBlurb(k)}</size>");
                y += 42f;
            }
            GUI.color = prev;
        }

        // ---------------- capture on foot ----------------

        private static readonly Dictionary<Unit, float> boosted = new Dictionary<Unit, float>();
        private static float nextCaptureTick;

        // Server, once a second: soldiers on foot count toward airbase capture (vehicles already do).
        internal static void CaptureTick()
        {
            if (Time.time < nextCaptureTick) return;
            nextCaptureTick = Time.time + 1f;
            float strength = Plugin.GroundWarEnabled.Value ? Plugin.SoldierCaptureStrength.Value : 0f;
            RallyTick();
            Transport.Tick();

            var want = new HashSet<Unit>();
            if (strength > 0f)
            {
                foreach (PilotDismounted p in NetSync.AllProtected())
                {
                    if (p != null && !p.disabled && !NetSync.InVehicle(p) && !Medical.IsDowned(p)) want.Add(p);
                }
            }
            foreach (Unit u in want)
            {
                if (boosted.TryGetValue(u, out float had) && Mathf.Approximately(had, strength)) continue;
                u.ModifyCaptureStrength(strength - (boosted.TryGetValue(u, out had) ? had : 0f));
                boosted[u] = strength;
            }
            foreach (Unit u in boosted.Keys.Where(k => !want.Contains(k)).ToList())
            {
                if (u != null) u.ModifyCaptureStrength(-boosted[u]);
                boosted.Remove(u);
            }
            Deployed.RemoveWhere(p => p == null || p.disabled);
            foreach (PilotDismounted gone in Kits.Keys.Where(k => k == null || k.disabled).ToList()) Kits.Remove(gone);
        }

        // ---------------- AI front line ----------------

        private static float nextWave;
        internal static int WavesSent;
        internal static bool SuspendWarfare; // the test harness keeps the front line out of its scripted steps
        internal static void ForceWave() => nextWave = 0f;

        private static readonly Dictionary<Airbase, bool> onLand = new Dictionary<Airbase, bool>();

        // Capturable airbases on dry land (carriers and sea bases excluded; the land test is cached).
        internal static IEnumerable<Airbase> LandAirbases() => FactionRegistry.airbaseLookup.Values
            .Where(a => a != null && !a.disabled && a.center != null && a.SavedAirbase != null && a.SavedAirbase.Capturable && OnLand(a));

        private static bool OnLand(Airbase a)
        {
            if (!onLand.TryGetValue(a, out bool land))
            {
                land = a.GetComponentInParent<Ship>() == null && AiSquads.GroundPoint(a.center.position).GlobalY() > 1f;
                onLand[a] = land;
            }
            return land;
        }

        // The objective a faction should push: the closest capturable airbase it doesn't hold, measured from its own bases.
        internal static Airbase NextObjective(FactionHQ hq, Vector3? from = null)
        {
            List<Airbase> ours = LandAirbases().Where(a => a.CurrentHQ == hq).ToList();
            Airbase best = null;
            float bestD = float.MaxValue;
            foreach (Airbase a in LandAirbases())
            {
                if (a.CurrentHQ == hq) continue;
                float d = from.HasValue
                    ? Vector3.Distance(from.Value, a.center.position)
                    : ours.Count == 0 ? float.MaxValue - 1f : ours.Min(o => Vector3.Distance(o.center.position, a.center.position));
                if (d < bestD) { bestD = d; best = a; }
            }
            return best;
        }

        // Server: every WaveInterval, each faction sends a squad at its next objective (reinforcements "arrive by road"
        // a short walk out, on their own side), and garrisons any of its airbases with enemy soldiers inside.
        internal static void WarfareTick()
        {
            if (!Plugin.GroundWarEnabled.Value || !Plugin.AiWarfare.Value || SuspendWarfare || Time.time < nextWave) return;
            if (Time.timeSinceLevelLoad < 20f) return;
            nextWave = Time.time + Mathf.Max(30f, Plugin.WaveInterval.Value);
            foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
            {
                if (hq == null) continue;
                int alive = AiSquads.All.Values.Count(a => a.Pilot != null && !a.Pilot.disabled && a.Pilot.NetworkHQ == hq);
                if (alive + Plugin.WaveSize.Value > Plugin.MaxAiPerFaction.Value) continue;

                Airbase threatened = LandAirbases().FirstOrDefault(a => a.CurrentHQ == hq && EnemySoldiersNear(hq, a.center.position, a.GetRadius()) > 0);
                if (threatened != null)
                {
                    SpawnSquadAt(hq, AiSquads.GroundPoint(threatened.center.position), threatened, AiOrder.Free);
                    continue;
                }
                Airbase objective = NextObjective(hq);
                if (objective == null) continue;
                Vector3 home = LandAirbases().Where(a => a.CurrentHQ == hq).OrderBy(a => Vector3.Distance(a.center.position, objective.center.position))
                    .Select(a => a.center.position).DefaultIfEmpty(objective.center.position + Vector3.forward * 3000f).First();
                Vector3 dir = (home - objective.center.position);
                dir.y = 0f;
                dir = dir.sqrMagnitude > 1f ? dir.normalized : Vector3.forward;
                float apart = Vector3.Distance(home, objective.center.position);
                if (Plugin.TruckTransport.Value && Transport.TruckDef != null && apart > Plugin.DismountDistance.Value + 800f)
                {
                    // By truck from up to ConvoyDistance out, on our side of the objective.
                    Vector3 start = FindDry(objective.center.position + dir * Mathf.Min(Plugin.ConvoyDistance.Value, apart * 0.7f), dir);
                    if (start != Vector3.zero && Transport.Send(hq, start, objective, Plugin.WaveSize.Value) != null)
                    {
                        WavesSent++;
                        continue;
                    }
                }
                float standOff = Mathf.Min(Plugin.WaveStandOff.Value, apart * 0.5f);
                Vector3 spot = FindDry(objective.center.position + dir * (objective.GetRadius() + standOff), dir);
                if (spot == Vector3.zero) continue;
                SpawnSquadAt(hq, spot, objective, AiOrder.Free);
            }
        }

        private static void SpawnSquadAt(FactionHQ hq, Vector3 spot, Airbase objective, AiOrder order)
        {
            List<AiSoldier> squad = AiSquads.SpawnPatrol(hq, spot, objective.center.position, Plugin.WaveSize.Value);
            foreach (AiSoldier ai in squad)
            {
                ai.IsPatrol = false;
                ai.Order = order;
                ai.Objective = objective;
            }
            WavesSent++;
            Plugin.Log.LogInfo($"Front line: {hq.faction.factionName} squad of {squad.Count} heading for {AirbaseName(objective)} ({Vector3.Distance(spot, objective.center.position):F0} m)");
        }

        private static Vector3 FindDry(Vector3 around, Vector3 dir)
        {
            for (int i = 0; i < 12; i++)
            {
                Vector3 p = AiSquads.GroundPoint(around + Quaternion.Euler(0f, (i % 2 == 0 ? 1 : -1) * (i / 2) * 25f, 0f) * dir * (i * 40f));
                if (p.GlobalY() > 1.5f) return p;
            }
            return Vector3.zero;
        }

        internal static int EnemySoldiersNear(FactionHQ hq, Vector3 at, float radius)
        {
            int n = 0;
            foreach (PilotDismounted p in NetSync.AllProtected())
            {
                if (p != null && !p.disabled && p.NetworkHQ != null && p.NetworkHQ != hq && Vector3.Distance(p.transform.position, at) < radius) n++;
            }
            return n;
        }

        // ---------------- motor pool ----------------

        private static List<VehicleDefinition> pool;
        private static readonly Dictionary<Player, float> lastVehicle = new Dictionary<Player, float>();
        private static readonly Dictionary<Player, GroundVehicle> issued = new Dictionary<Player, GroundVehicle>();

        internal static List<VehicleDefinition> MotorPool
        {
            get
            {
                if (pool != null) return pool;
                pool = Encyclopedia.i == null ? new List<VehicleDefinition>() : Encyclopedia.i.vehicles
                    .Where(v => v != null && v.unitPrefab != null && v.unitPrefab.GetComponent<GroundVehicle>() != null && v.mass < Plugin.MotorPoolMaxMass.Value)
                    .OrderBy(v => v.mass).Take(9).ToList();
                return pool;
            }
        }

        internal static float Cost(VehicleDefinition def) => Plugin.MotorPoolCostScale.Value * def.value;

        // Local player: request vehicle `index` of the motor pool next to the soldier.
        internal static void RequestVehicle(PilotDismounted soldier, int index)
        {
            if (soldier == null || index < 0 || index >= MotorPool.Count) return;
            if (Combat.IsServer) ServerVehicle(soldier, index);
            else NetSync.SendSquad(soldier, (byte)(SquadKinds.VehicleBase + index));
        }

        internal static GroundVehicle ServerVehicle(PilotDismounted soldier, int index)
        {
            if (!Combat.IsServer || soldier == null || soldier.disabled || index < 0 || index >= MotorPool.Count) return null;
            Player player = soldier.Networkplayer;
            Airbase airbase = AirbaseServices.NearFriendlyAirbase(soldier);
            if (airbase == null) { Plugin.Log.LogInfo("Motor pool: only at a friendly airbase"); return null; }
            if (player != null && lastVehicle.TryGetValue(player, out float t) && Time.time < t + Plugin.MotorPoolCooldown.Value)
            {
                Plugin.Log.LogInfo($"Motor pool: {PlayerLabel(player)} must wait {t + Plugin.MotorPoolCooldown.Value - Time.time:F0} s");
                return null;
            }
            VehicleDefinition def = MotorPool[index];
            float cost = Cost(def);
            if (player != null && cost > 0f)
            {
                if (player.Allocation < cost) { Plugin.Log.LogInfo($"Motor pool: {PlayerLabel(player)} can't afford {def.unitName} ({cost:F1})"); return null; }
                player.AddAllocation(-cost);
            }
            // The previous vehicle stays in the field as a faction asset; only the cooldown limits requests.
            Vector3 fwd = soldier.transform.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 0.01f ? fwd.normalized : Vector3.forward;
            Vector3 spot = soldier.transform.position + fwd * 12f;
            for (int i = 0; i < 8 && Physics.CheckSphere(AiSquads.GroundPoint(spot) + Vector3.up * 2f, 3f, ~(int)PhysicsLayers.ExclusionZonesMask, QueryTriggerInteraction.Ignore); i++)
            {
                spot = soldier.transform.position + Quaternion.Euler(0f, 45f * (i + 1), 0f) * fwd * 14f;
            }
            Vector3 pos = AiSquads.GroundPoint(spot) + Vector3.up * 1f;
            GroundVehicle gv = NetworkSceneSingleton<Spawner>.i.SpawnVehicle(def.unitPrefab, pos.ToGlobalPosition(), Quaternion.LookRotation(fwd), Vector3.zero,
                soldier.NetworkHQ, $"NOI Motor Pool {def.unitName}", 1f, true, player);
            if (player != null)
            {
                lastVehicle[player] = Time.time;
                issued[player] = gv;
            }
            Plugin.Log.LogInfo($"Motor pool: {def.unitName} for {(player != null ? PlayerLabel(player) : soldier.unitName)} at {AirbaseName(airbase)}");
            return gv;
        }

        internal static void ResetMission()
        {
            Kits.Clear();
            Deployed.Clear();
            boosted.Clear();
            lastVehicle.Clear();
            Rally.Clear();
            rallyPlacedAt.Clear();
            issued.Clear();
            pool = null;
            onLand.Clear();
            nextWave = 0f;
        }
    }

    // Kind bytes of NoiSquad requests.
    internal static class SquadKinds
    {
        internal const byte Fireteam = 1, Patrol = 2;
        internal const byte Rally = 3;
        internal const byte VehicleBase = 32; // 32..40: motor pool vehicle index
    }
}

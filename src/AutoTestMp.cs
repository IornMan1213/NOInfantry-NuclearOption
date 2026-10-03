using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using Mirage;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace NOInfantry
{
    // Two-instance multiplayer test on one PC (UDP, no Steam auth needed):
    //   host:   NuclearOption.exe -autoHost -socket UDP -port 7777 -state Multiplayer -mission "<name>" -noiAutotest -noiRole host
    //   client: NuclearOption.exe -autoConnect 127.0.0.1 -socket UDP -port 7777 -noiAutotest -noiRole client
    // The host spawns the client's soldier and any test units the client asks for (NoiTest), and reports what
    // the server actually did. The client drives the soldier through every feature over the network.
    internal class AutoTestMp : MonoBehaviour
    {
        internal static string Role
        {
            get
            {
                string[] args = Environment.GetCommandLineArgs();
                int i = Array.FindIndex(args, a => a.Equals("-noiRole", StringComparison.OrdinalIgnoreCase));
                return i >= 0 && i + 1 < args.Length ? args[i + 1].ToLowerInvariant() : null;
            }
        }

        private readonly StringBuilder report = new StringBuilder();
        private string role;
        private string shotDir;
        private int shotIndex, passed, failed;

        private Player player;
        private FactionHQ hq, enemyHq;
        private Airbase airbase;
        private PilotDismounted soldier;

        private void Start()
        {
            GroundWar.SuspendWarfare = true; // keep the AI front line out of the scripted steps
            role = Role;
            Application.runInBackground = true;
            shotDir = Path.Combine(Paths.BepInExRootPath, $"NOInfantry_shots_{role}");
            Directory.CreateDirectory(shotDir);
            foreach (string f in Directory.GetFiles(shotDir, "*.png")) File.Delete(f);
            StartCoroutine(role == "host" ? Host() : Client());
        }

        // ---------------- reporting ----------------

        private void Log(string line)
        {
            Plugin.Log.LogInfo($"[MPTEST {role}] {line}");
            report.AppendLine($"[{Time.realtimeSinceStartup:F0}s] {line}");
            try { File.WriteAllText(Path.Combine(Paths.BepInExRootPath, $"NOInfantry_mptest_{role}.txt"), report.ToString()); } catch { }
        }

        private void Result(string step, bool ok, string detail)
        {
            if (ok) passed++; else failed++;
            Log($"{(ok ? "PASS" : "FAIL")} {step}: {detail}");
        }

        private void Shot(string name)
        {
            ScreenCapture.CaptureScreenshot(Path.Combine(shotDir, $"{++shotIndex:D2}_{name}.png"));
        }

        private IEnumerator Finish()
        {
            Log($"=== DONE: {passed} passed, {failed} failed");
            InputShim.Scripted = false;
            yield return new WaitForSecondsRealtime(3f);
            Application.Quit();
        }

        private static IEnumerator WaitUntil(Func<bool> cond, float timeout)
        {
            float end = Time.realtimeSinceStartup + timeout;
            while (!cond() && Time.realtimeSinceStartup < end) yield return null;
        }

        private IEnumerator Step(string name, IEnumerator body)
        {
            Log($"--- {name}");
            InputShim.ReleaseAll();
            while (true)
            {
                object current;
                try
                {
                    if (!body.MoveNext()) break;
                    current = body.Current;
                }
                catch (Exception e)
                {
                    Result(name, false, "exception: " + e);
                    break;
                }
                yield return current;
            }
            InputShim.ReleaseAll();
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        private static Vector3 Ground(Vector3 p)
        {
            Vector3 top = new Vector3(p.x, p.y + 2000f, p.z);
            return Physics.Raycast(top, Vector3.down, out RaycastHit hit, 5000f, PhysicsLayers.StaticsMask) ? hit.point : p;
        }

        private IEnumerator JoinFaction()
        {
            yield return WaitUntil(() => GameManager.GetLocalPlayer(out player) && SceneSingleton<CameraStateManager>.i != null
                                         && FactionRegistry.GetAllHQs().Count >= 2, 240f);
            if (player == null) yield break;
            yield return new WaitForSecondsRealtime(4f);
            List<FactionHQ> hqs = FactionRegistry.GetAllHQs().ToList();
            hq = hqs.OrderByDescending(h => FactionRegistry.airbaseLookup.Values.Count(a => a != null && a.CurrentHQ == h)).First();
            if (role == "client")
            {
                // Join the host's side (airbase ownership may not have synced yet, so don't guess).
                Player hostPlayer = null;
                yield return WaitUntil(() =>
                {
                    hostPlayer = UnitRegistry.playerLookup.Values.FirstOrDefault(p => p != null && p != player && p.HQ != null);
                    return hostPlayer != null;
                }, 30f);
                if (hostPlayer != null) hq = hostPlayer.HQ;
            }
            enemyHq = hqs.First(h => h != hq);
            if (player.HQ != hq)
            {
                var join = SceneSingleton<NuclearOption.UI.LeaderboardMenu>.i;
                if (join != null) join.JoinFactionCallback(hq); else player.SetFaction(hq, true);
                yield return WaitUntil(() => player.HQ == hq, 15f);
                yield return new WaitForSecondsRealtime(1f);
                SceneSingleton<DynamicMap>.i?.Minimize();
            }
            airbase = FactionRegistry.airbaseLookup.Values.FirstOrDefault(a => a != null && a.CurrentHQ == hq);
        }

        // ================= HOST =================

        private IEnumerator Host()
        {
            Log("host started");
            yield return JoinFaction();
            Result("host mission", player != null && player.HQ == hq && airbase != null, $"faction {hq?.faction?.factionName}, airbase {airbase?.name}");
            if (airbase == null) { yield return Finish(); yield break; }

            NetSync.TestRequest = OnTestRequest;
            Log("waiting for a modded client");
            yield return WaitUntil(() => NetSync.ModdedPlayerCount > 0, 300f);
            Result("handshake (host side)", NetSync.ModdedPlayerCount > 0, $"modded clients: {NetSync.ModdedPlayerCount}");
            if (NetSync.ModdedPlayerCount == 0) { yield return Finish(); yield break; }

            Player remote = null;
            yield return WaitUntil(() =>
            {
                remote = UnitRegistry.playerLookup.Values.FirstOrDefault(p => p != null && p != player && p.HQ != null);
                return remote != null;
            }, 120f);
            Result("client joined faction", remote != null, $"client player {remote}, faction {remote?.HQ?.faction?.factionName}");
            if (remote == null) { yield return Finish(); yield break; }
            remote.AddAllocation(500f); // so the client's motor pool request doesn't depend on the economy
            if (remote.HQ != hq)
            {
                hq = remote.HQ;
                enemyHq = FactionRegistry.GetAllHQs().First(h => h != hq);
                airbase = FactionRegistry.airbaseLookup.Values.FirstOrDefault(a => a != null && a.CurrentHQ == hq) ?? airbase;
            }

            // The client starts the real way (aircraft at the airbase, then F8), so just wait for its soldier.
            PilotDismounted clientSoldier = null;
            yield return WaitUntil(() =>
            {
                clientSoldier = NetSync.Remote.Keys.FirstOrDefault(k => k != null && !k.disabled);
                return clientSoldier != null;
            }, 120f);
            Result("remote soldier streaming", clientSoldier != null, "server receives the client's soldier state");

            // Watch the soldier move on the server and report counters until the client leaves.
            Vector3 start = clientSoldier != null ? clientSoldier.transform.position : Vector3.zero;
            float maxMoved = 0f;
            float end = Time.realtimeSinceStartup + 600f;
            while (Time.realtimeSinceStartup < end && NetSync.ModdedPlayerCount > 0
                   && NetworkManagerNuclearOption.i.Server.AllPlayers.Count > 1)
            {
                if (clientSoldier != null) maxMoved = Mathf.Max(maxMoved, Vector3.Distance(Flat(start), Flat(clientSoldier.transform.position)));
                yield return new WaitForSecondsRealtime(0.5f);
            }
            Log("client left; server-side summary:");
            Result("server saw soldier move", maxMoved > 5f, $"max distance from spawn {maxMoved:F0} m");
            Result("server rifle shots", NetSync.StatShots > 0, $"{NetSync.StatShots} shots, {NetSync.StatShotHits} hits applied by the server");
            Result("server grenades", NetSync.StatGrenades > 0, $"{NetSync.StatGrenades}");
            Result("server missile launches", NetSync.StatLaunches > 0, $"{NetSync.StatLaunches}");
            Result("server vehicle control", NetSync.StatVehicleEnters > 0 && NetSync.StatVehicleInputs > 0, $"{NetSync.StatVehicleEnters} enters, {NetSync.StatVehicleInputs} input packets");
            Result("server AI squads", AiSquads.Spawned >= Plugin.SquadSize.Value, $"{AiSquads.Spawned} AI soldiers spawned on request");
            Result("server deploy on foot", GroundWar.DeployCount > 0, $"{GroundWar.DeployCount} soldiers deployed from the menu");
            Result("server squad orders", AiSquads.OrdersReceived > 0, $"{AiSquads.OrdersReceived} orders executed");
            yield return Finish();
        }

        private void OnTestRequest(INetworkPlayer sender, NoiTest req)
        {
            Spawner spawner = NetworkSceneSingleton<Spawner>.i;
            Vector3 pos = req.Pos.ToLocalPosition();
            Quaternion rot = req.Dir.sqrMagnitude > 0.01f ? Quaternion.LookRotation(req.Dir) : Quaternion.identity;
            try
            {
                switch (req.Kind)
                {
                    case 1: // enemy unarmed truck
                    {
                        VehicleDefinition def = Encyclopedia.i.vehicles.Where(v => v?.unitPrefab?.GetComponent<GroundVehicle>() != null
                            && v.unitPrefab.GetComponentsInChildren<Turret>(true).Length == 0).OrderBy(v => v.mass).First();
                        spawner.SpawnVehicle(def.unitPrefab, pos.ToGlobalPosition(), rot, Vector3.zero, enemyHq, req.Name, 1f, true, null);
                        break;
                    }
                    case 2: // enemy aircraft
                    {
                        AircraftDefinition def = Encyclopedia.i.aircraft.Where(a => a?.unitPrefab != null && a.aircraftParameters != null).OrderBy(a => a.value).First();
                        Loadout lo = def.aircraftParameters.loadouts != null && def.aircraftParameters.loadouts.Count > 0 ? def.aircraftParameters.loadouts[0] : null;
                        spawner.SpawnAircraft(null, def.unitPrefab, lo, 1f, default, pos.ToGlobalPosition(), rot, rot * Vector3.forward * 140f, null, enemyHq, req.Name, 0.5f, 0.5f);
                        break;
                    }
                    case 3: // friendly armed vehicle
                    {
                        VehicleDefinition def = Encyclopedia.i.vehicles.Where(v => v?.unitPrefab?.GetComponent<GroundVehicle>() != null
                            && v.unitPrefab.GetComponentsInChildren<Turret>(true).Length > 0).OrderBy(v => v.mass).First();
                        spawner.SpawnVehicle(def.unitPrefab, pos.ToGlobalPosition(), rot, Vector3.zero, hq, req.Name, 1f, true, null);
                        break;
                    }
                    case 4: // friendly ship
                    {
                        ShipDefinition def = Encyclopedia.i.ships.Where(s => s?.unitPrefab != null).OrderBy(s => s.mass).First();
                        spawner.SpawnShip(def.unitPrefab, pos.ToGlobalPosition(), rot, hq, req.Name, 1f, true);
                        break;
                    }
                    case 5: // an aircraft for the requesting player (tests the hand-off back to flying)
                    {
                        Player owner = UnitRegistry.playerLookup.Values.FirstOrDefault(p => p != null && p.Owner == sender);
                        AircraftDefinition def = Encyclopedia.i.aircraft.Where(a => a?.unitPrefab != null).OrderBy(a => a.value).First();
                        Loadout lo = def.aircraftParameters.loadouts != null && def.aircraftParameters.loadouts.Count > 0 ? def.aircraftParameters.loadouts[0] : null;
                        Vector3 p = Ground(pos) + Vector3.up * def.spawnOffset.y;
                        spawner.SpawnAircraft(owner, def.unitPrefab, lo, 1f, default, p.ToGlobalPosition(), rot, Vector3.zero, null, hq, req.Name, 1f, 1f);
                        break;
                    }
                }
                Log($"spawned test unit kind {req.Kind} '{req.Name}' for {sender}");
            }
            catch (Exception e)
            {
                Log($"test spawn kind {req.Kind} failed: {e.Message}");
            }
        }

        // ================= CLIENT =================

        private Unit Find(string uniqueName) =>
            UnitRegistry.allUnits.FirstOrDefault(u => u != null && u.UniqueName == uniqueName);

        private IEnumerator Request(byte kind, string name, Vector3 pos, Vector3 dir, Action<Unit> got)
        {
            NetSync.SendTest(kind, name, pos, dir);
            Unit u = null;
            yield return WaitUntil(() => (u = Find(name)) != null, 15f);
            if (u == null) Log($"requested '{name}' never appeared");
            got(u);
        }

        private Vector3 ClearDirection(float dist) => ClearDirection(dist, Flat(SceneSingleton<CameraStateManager>.i.mainCamera.transform.forward).normalized);

        private Vector3 ClearDirection(float dist, Vector3 baseDir)
        {
            Vector3 eye = soldier.transform.position + Vector3.up * Plugin.EyeHeight.Value;
            for (int i = 0; i < 24; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, i * 15f, 0f) * baseDir;
                Vector3 target = Ground(eye + dir * dist) + Vector3.up * 2f;
                bool blocked = Physics.RaycastAll(eye, (target - eye).normalized, Vector3.Distance(eye, target), ~(int)PhysicsLayers.ExclusionZonesMask, QueryTriggerInteraction.Ignore)
                    .Any(h => !h.collider.transform.IsChildOf(soldier.transform));
                if (!blocked) return dir;
            }
            return baseDir;
        }

        private IEnumerator Client()
        {
            Log("client started");
            yield return JoinFaction();
            Result("client in mission", player != null, $"faction {player?.HQ?.faction?.factionName}");
            yield return WaitUntil(() => NetSync.ServerHasMod, 30f);
            Result("handshake (client side)", NetSync.ServerHasMod, "server answered NoiHello");
            if (!NetSync.ServerHasMod) { yield return Finish(); yield break; }

            // Start like a player: an aircraft at our airbase (the host spawns it for us), then F8 to climb out.
            yield return Request(5, "NOI MP Start", airbase.center.position + airbase.center.right * 40f, airbase.center.forward, _ => { });
            yield return WaitUntil(() => AirbaseServices.HasNewAircraft(), 30f);
            Result("client aircraft", AirbaseServices.HasNewAircraft(), "flying a fresh aircraft at the airbase");
            yield return new WaitForSecondsRealtime(4f);
            InputShim.Scripted = true;
            InputShim.Press(KeyCode.F8);
            float waitEnd = Time.realtimeSinceStartup + 90f;
            float nextDiag = 0f;
            while (InfantryController.Controlled == null && Time.realtimeSinceStartup < waitEnd)
            {
                if (Time.realtimeSinceStartup > nextDiag)
                {
                    nextDiag = Time.realtimeSinceStartup + 5f;
                    foreach (Unit u in UnitRegistry.allUnits)
                    {
                        if (!(u is PilotDismounted pd)) continue;
                        Player owner = pd.Networkplayer;
                        Log($"diag pilot {pd.unitName} uniq={pd.UniqueName} owner={owner} local={(owner != null && GameManager.IsLocalPlayer(owner))} " +
                            $"state={pd.animationState} disabled={pd.disabled} localSim={pd.LocalSim} radarAlt={pd.radarAlt:F1}");
                    }
                    Log($"diag local player {player}, PilotDismounted={(player != null ? player.PilotDismounted : null)}");
                }
                yield return null;
            }
            soldier = InfantryController.Controlled;
            Result("client soldier control", soldier != null, "auto-entered infantry mode on the client");
            if (soldier == null) { yield return Finish(); yield break; }
            yield return WaitUntil(() => soldier.rb.velocity.magnitude < 2f, 20f);
            Log($"soldier settled: globalY {soldier.transform.position.GlobalY():F1}, speed {soldier.rb.velocity.magnitude:F1}");
            InputShim.Scripted = true;
            yield return new WaitForSecondsRealtime(1f);
            Shot("client_on_foot");

            yield return Step("walk", Walk());
            yield return Step("rifle", Rifle());
            yield return Step("grenade", GrenadeStep());
            yield return Step("launcher-air", LauncherAir());
            yield return Step("vehicle", VehicleStep());
            yield return Step("squad", SquadStep());
            yield return Step("ship", ShipStep());
            yield return Step("air hand-off", AirStep());
            yield return Step("deploy", DeployStep());
            yield return Finish();
        }

        private IEnumerator Walk()
        {
            Vector3 start = soldier.transform.position;
            InputShim.Hold(KeyCode.W, true);
            InputShim.Hold(KeyCode.LeftShift, true);
            yield return new WaitForSecondsRealtime(3f);
            InputShim.ReleaseAll();
            float moved = Vector3.Distance(Flat(start), Flat(soldier.transform.position));
            Result("walk", moved > 6f, $"moved {moved:F1} m locally (the host checks the server copy)");
        }

        private IEnumerator Rifle()
        {
            Vector3 dir = ClearDirection(80f);
            Unit truck = null;
            yield return Request(1, "NOI MP Truck", Ground(soldier.transform.position + dir * 80f) + Vector3.up, -dir, u => truck = u);
            if (truck == null) { Result("rifle", false, "no target"); yield break; }
            yield return new WaitForSecondsRealtime(2f);
            Log($"truck requested 80 m away, client sees it {Vector3.Distance(soldier.transform.position, truck.transform.position):F0} m away");
            int shots0 = InfantryController.Instance.ShotsFired, hits0 = InfantryController.Instance.Hits;
            InputShim.HoldMouse(1, true);
            InputShim.HoldMouse(0, true);
            float end = Time.realtimeSinceStartup + 3.5f;
            while (Time.realtimeSinceStartup < end)
            {
                if (truck != null) InfantryController.Instance.AimAt(truck.transform.position + Vector3.up);
                yield return null;
            }
            InputShim.ReleaseAll();
            Shot("client_rifle");
            Result("rifle", InfantryController.Instance.ShotsFired - shots0 > 10 && InfantryController.Instance.Hits - hits0 > 0,
                $"{InfantryController.Instance.ShotsFired - shots0} shots, {InfantryController.Instance.Hits - hits0} local hits (server applies damage)");

            // Launcher against the same kind of target, far enough to need guidance.
            Vector3 d2 = ClearDirection(250f, Quaternion.Euler(0f, 100f, 0f) * dir); // away from the first truck
            Unit target = null;
            yield return Request(1, "NOI MP Truck 2", Ground(soldier.transform.position + d2 * 250f) + Vector3.up, -d2, u => target = u);
            if (target == null) { Result("launcher-surface", false, "no target"); yield break; }
            yield return new WaitForSecondsRealtime(2f);
            yield return FireLauncher(target, "launcher-surface", 20f);
        }

        private IEnumerator GrenadeStep()
        {
            int before = InfantryController.Instance.Grenades;
            InfantryController.Instance.AimAt(soldier.transform.position + Flat(SceneSingleton<CameraStateManager>.i.mainCamera.transform.forward) * 20f + Vector3.up * 8f);
            yield return null;
            InputShim.Press(KeyCode.G);
            yield return new WaitForSecondsRealtime(Plugin.GrenadeFuse.Value + 1f);
            Result("grenade", InfantryController.Instance.Grenades == before - 1, "thrown (the host checks the server detonation)");
        }

        private IEnumerator LauncherAir()
        {
            Vector3 fwd = Flat(SceneSingleton<CameraStateManager>.i.mainCamera.transform.forward).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Unit bandit = null;
            yield return Request(2, "NOI MP Bandit", soldier.transform.position + fwd * 2500f + Vector3.up * 700f, right, u => bandit = u);
            if (bandit == null) { Result("launcher-air", false, "no target"); yield break; }
            yield return new WaitForSecondsRealtime(4f);
            yield return FireLauncher(bandit, "launcher-air", 30f);
        }

        private IEnumerator FireLauncher(Unit target, string step, float watch)
        {
            InputShim.Press(KeyCode.Alpha2);
            yield return null;
            Launcher launcher = InfantryController.Instance.ActiveLauncher;
            InputShim.HoldMouse(1, true);
            float end = Time.realtimeSinceStartup + Plugin.LockTime.Value + 2.5f;
            while (Time.realtimeSinceStartup < end && !(launcher.Locked && launcher.LockTarget == target))
            {
                if (target != null) InfantryController.Instance.AimAt(target.transform.position);
                yield return null;
            }
            if (!(launcher.Locked && launcher.LockTarget == target))
            {
                Result(step, false, $"no lock (progress {launcher.LockProgress:F2})");
                InputShim.Press(KeyCode.Alpha1);
                yield break;
            }
            int ammo = launcher.Ammo;
            InputShim.ClickMouse(0);
            yield return null;
            yield return null;
            InputShim.ReleaseAll();
            yield return WaitUntil(() => target == null || target.disabled, watch);
            Shot($"client_{step}");
            Result(step, launcher.Ammo == ammo - 1 && (target == null || target.disabled),
                $"launch requested; target destroyed (synced from server) = {target == null || target.disabled}");
            InputShim.Press(KeyCode.Alpha1);
            yield return null;
        }

        private IEnumerator VehicleStep()
        {
            Vector3 side = Flat(SceneSingleton<CameraStateManager>.i.mainCamera.transform.right).normalized;
            Unit apc = null;
            yield return Request(3, "NOI MP APC", Ground(soldier.transform.position + side * 5.5f) + Vector3.up,
                Flat(SceneSingleton<CameraStateManager>.i.mainCamera.transform.forward), u => apc = u);
            if (apc == null) { Result("vehicle", false, "no vehicle"); yield break; }
            yield return new WaitForSecondsRealtime(3f);
            InputShim.Press(KeyCode.E);
            yield return WaitUntil(() => VehicleControl.Driven == apc, 4f);
            if (VehicleControl.Driven != apc) { Result("vehicle enter", false, "could not enter"); yield break; }
            yield return new WaitForSecondsRealtime(1f);
            bool stillIn = VehicleControl.Driven == apc;
            Result("vehicle enter", stillIn, "server accepted the driver");
            if (!stillIn) yield break;

            Vector3 start = apc.transform.position;
            InputShim.Hold(KeyCode.W, true);
            yield return new WaitForSecondsRealtime(4f);
            InputShim.ReleaseAll();
            float moved = Vector3.Distance(start, apc.transform.position);
            Result("vehicle drive", moved > 5f, $"vehicle moved {moved:F1} m (server-simulated, seen on the client)");
            Shot("client_vehicle");

            int ammo0 = VehicleControl.Turrets.Select(t => t.GetWeaponStation()).Where(w => w != null).Sum(w => w.Ammo);
            Vector3 aim = apc.transform.position + Flat(apc.transform.forward).normalized * 150f + Vector3.up * 3f;
            InfantryController.Instance.AimAt(aim);
            yield return new WaitForSecondsRealtime(2f);
            InputShim.HoldMouse(0, true);
            yield return new WaitForSecondsRealtime(3f);
            InputShim.ReleaseAll();
            yield return new WaitForSecondsRealtime(1.5f);
            int ammo1 = VehicleControl.Turrets.Select(t => t.GetWeaponStation()).Where(w => w != null).Sum(w => w.Ammo);
            Result("vehicle guns", ammo1 < ammo0, $"turret ammo {ammo0} -> {ammo1} (as synced to the client)");
            InputShim.Press(KeyCode.E);
            yield return WaitUntil(() => VehicleControl.Driven == null, 3f);
            Result("vehicle exit", VehicleControl.Driven == null, "left the vehicle");
        }

        private IEnumerator ShipStep()
        {
            Vector3 center = airbase.center.position;
            Vector3? sea = null;
            for (float r = 2000f; r <= 40000f && sea == null; r += 2000f)
            {
                for (int i = 0; i < 24; i++)
                {
                    float a = i * Mathf.PI * 2f / 24f;
                    Vector3 p = center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
                    Vector3 top = new Vector3(p.x, Datum.LocalSeaY + 1500f, p.z);
                    float highest = float.MinValue;
                    foreach (RaycastHit h in Physics.RaycastAll(top, Vector3.down, 6000f))
                    {
                        if (h.collider.sharedMaterial == GameAssets.i.WaterMaterial || h.collider.isTrigger) continue;
                        highest = Mathf.Max(highest, h.point.y);
                    }
                    if (highest < Datum.LocalSeaY - 25f) { sea = new Vector3(p.x, Datum.LocalSeaY, p.z); break; }
                }
            }
            if (sea == null) { Result("ship", false, "no open water"); yield break; }
            Unit ship = null;
            yield return Request(4, "NOI MP Ship", sea.Value, Vector3.forward, u => ship = u);
            if (ship == null) { Result("ship", false, "no ship"); yield break; }
            yield return new WaitForSecondsRealtime(6f);

            Vector3 deck = ship.transform.position + Vector3.up * 5f;
            foreach (RaycastHit h in Physics.RaycastAll(ship.transform.position + Vector3.up * 80f, Vector3.down, 120f).OrderBy(h => h.distance))
            {
                if (h.collider.transform.IsChildOf(ship.transform)) { deck = h.point; break; }
            }
            soldier.transform.position = deck + Vector3.up * 1.4f;
            soldier.rb.position = soldier.transform.position;
            soldier.rb.velocity = ship.rb != null ? ship.rb.velocity : Vector3.zero;
            yield return new WaitForSecondsRealtime(3f);
            InputShim.Press(KeyCode.E);
            yield return WaitUntil(() => VehicleControl.Driven == ship, 4f);
            yield return new WaitForSecondsRealtime(1f);
            if (VehicleControl.Driven != ship) { Result("ship helm", false, "could not take the helm"); yield break; }
            Result("ship helm", true, "server accepted the helm");
            float yaw0 = ship.transform.eulerAngles.y;
            Vector3 p0 = ship.transform.position;
            InputShim.Hold(KeyCode.W, true);
            yield return new WaitForSecondsRealtime(8f);
            InputShim.Hold(KeyCode.D, true);
            yield return new WaitForSecondsRealtime(8f);
            InputShim.ReleaseAll();
            float turned = Mathf.DeltaAngle(yaw0, ship.transform.eulerAngles.y);
            float moved = Vector3.Distance(p0, ship.transform.position);
            Shot("client_ship");
            Result("ship drive", moved > 30f && turned > 3f, $"moved {moved:F0} m, yaw {turned:F0} deg (server-simulated)");
            InputShim.Press(KeyCode.E);
            yield return WaitUntil(() => VehicleControl.Driven == null, 3f);
        }

        private IEnumerator SquadStep()
        {
            InputShim.Press(Plugin.FireteamKey.Value);
            yield return new WaitForSecondsRealtime(5f);
            PilotDismounted me = InfantryController.Controlled;
            int near = UnitRegistry.allUnits.Count(u => u is PilotDismounted pd && !pd.disabled && pd.UniqueName != null
                && pd.UniqueName.StartsWith("NOI Rifleman") && me != null && Vector3.Distance(pd.transform.position, me.transform.position) < 60f);
            Result("squad (client request)", near >= Plugin.SquadSize.Value, $"{near} server-spawned riflemen visible near the client's soldier");
            Shot("client_squad");
        }

        private IEnumerator DeployStep()
        {
            // Deploy on foot from the aircraft selection menu: the server replaces our soldier with a fresh one.
            PilotDismounted old = InfantryController.Controlled;
            if (!AirbaseServices.OpenAircraftMenu(airbase)) { Result("deploy on foot (client)", false, "menu did not open"); yield break; }
            yield return WaitUntil(() => GroundWar.OpenMenu != null, 5f);
            yield return new WaitForSecondsRealtime(1.5f);
            Shot("client_deploy_panel");
            GroundWar.RequestDeploy(GroundWar.MenuAirbase(GroundWar.OpenMenu) ?? airbase, Kit.AntiAir);
            yield return WaitUntil(() => InfantryController.Controlled != null && InfantryController.Controlled != old, 25f);
            PilotDismounted me = InfantryController.Controlled;
            Result("deploy on foot (client)", me != null && me != old && InfantryController.Instance.CurrentKit == Kit.AntiAir,
                $"controlled {me?.unitName}, kit {InfantryController.Instance.CurrentKit}, old soldier gone {old == null}");
            if (me == null || me == old) yield break;
            soldier = me;
            InputShim.Scripted = true;
            yield return new WaitForSecondsRealtime(3f);
            Result("deployed soldier settled", me.transform.position.GlobalY() > 1f && !me.disabled, $"globalY {me.transform.position.GlobalY():F1}");
            Shot("client_deployed");

            // Motor pool (the host tops up our funds when we join).
            InputShim.Press(Plugin.MotorPoolKey.Value);
            yield return null;
            yield return null;
            InputShim.Press(KeyCode.Alpha1);
            yield return WaitUntil(() => UnitRegistry.allUnits.OfType<GroundVehicle>().Any(v => v != null && v.UniqueName != null && v.UniqueName.StartsWith("NOI Motor Pool")
                                                                                         && Vector3.Distance(v.transform.position, me.transform.position) < 40f), 8f);
            GroundVehicle issued = UnitRegistry.allUnits.OfType<GroundVehicle>().FirstOrDefault(v => v != null && v.UniqueName != null && v.UniqueName.StartsWith("NOI Motor Pool")
                                                                                               && Vector3.Distance(v.transform.position, me.transform.position) < 40f);
            Result("motor pool (client)", issued != null, $"{issued?.unitName}");

            // Fireteam, then a Hold order over the network: the riflemen stay while we walk off.
            Vector3 dir = ClearDirection(60f);
            InfantryController.Instance.AimAt(me.transform.position + dir * 60f + Vector3.up * 1.5f);
            yield return new WaitForSecondsRealtime(2.5f); // squad request throttle
            InputShim.Press(Plugin.FireteamKey.Value);
            yield return new WaitForSecondsRealtime(5f);
            Vector3 holdAt = me.transform.position;
            InputShim.Press(Plugin.SquadMenuKey.Value);
            yield return null;
            yield return null;
            InputShim.Press(KeyCode.Alpha2);
            yield return new WaitForSecondsRealtime(0.5f);
            InputShim.Hold(KeyCode.W, true);
            yield return new WaitForSecondsRealtime(6f);
            InputShim.ReleaseAll();
            yield return new WaitForSecondsRealtime(2f);
            float walked = Vector3.Distance(Flat(holdAt), Flat(me.transform.position));
            int holding = UnitRegistry.allUnits.Count(u => u is PilotDismounted pd && !pd.disabled && pd.UniqueName != null && pd.UniqueName.StartsWith("NOI Rifleman")
                                                           && Vector3.Distance(Flat(pd.transform.position), Flat(holdAt)) < 14f); // rear wedge slots are 9.2 m out
            Result("squad hold order (client)", walked > 12f && holding >= Plugin.SquadSize.Value - 1, $"walked {walked:F0} m, {holding} riflemen holding within 14 m of the hold point");
            Shot("client_hold");
        }

        private IEnumerator AirStep()
        {
            // Back on land at the airbase, then ask the host for an aircraft of our own (emulates the spawn menu,
            // which needs owned airframes) and check infantry mode hands over cleanly.
            Vector3 spot = Ground(airbase.center.position) + Vector3.up * 1.5f;
            soldier.transform.position = spot;
            soldier.rb.position = spot;
            soldier.rb.velocity = Vector3.zero;
            yield return new WaitForSecondsRealtime(2f);
            Result("airbase detect", AirbaseServices.NearFriendlyAirbase(soldier) != null, "friendly airbase in range");
            yield return Request(5, "NOI MP Ride", airbase.center.position + airbase.center.right * 40f, airbase.center.forward, _ => { });
            yield return WaitUntil(() => AirbaseServices.HasNewAircraft() && InfantryController.Controlled == null, 20f);
            Result("air hand-off", AirbaseServices.HasNewAircraft() && InfantryController.Controlled == null, "new aircraft, infantry released");
            yield return new WaitForSecondsRealtime(3f);
            Shot("client_aircraft");
            InputShim.Scripted = true;
            InputShim.Press(KeyCode.F8);
            yield return WaitUntil(() => InfantryController.Controlled != null, 25f);
            Result("dismount F8 (client)", InfantryController.Controlled != null, "climbed out and back in infantry mode");
        }
    }
}

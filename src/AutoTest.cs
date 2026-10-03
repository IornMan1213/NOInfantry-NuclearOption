using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace NOInfantry
{
    // Automated in-game test. Enabled by launching the game with:
    //   NuclearOption.exe -autoHost -state SinglePlayer -mission "<name>" -noiAutotest
    // It builds its own scenario with the game's Spawner, drives the soldier through InputShim,
    // writes BepInEx/NOInfantry_autotest.txt plus screenshots, then quits.
    internal class AutoTest : MonoBehaviour
    {
        private readonly StringBuilder report = new StringBuilder();
        private string shotDir;
        private int shotIndex;
        private int passed, failed;

        private Player player;
        private FactionHQ hq, enemyHq;
        private Airbase airbase;
        private PilotDismounted soldier;
        private GroundVehicle enemyTruck;

        internal static bool Requested => Environment.GetCommandLineArgs().Any(a => a.Equals("-noiAutotest", StringComparison.OrdinalIgnoreCase));

        private void Start()
        {
            Application.runInBackground = true;
            shotDir = Path.Combine(Paths.BepInExRootPath, "NOInfantry_shots");
            Directory.CreateDirectory(shotDir);
            foreach (string f in Directory.GetFiles(shotDir, "*.png")) File.Delete(f);
            StartCoroutine(Run());
            StartCoroutine(FpsSampler());
        }

        private IEnumerator FpsSampler()
        {
            while (true)
            {
                int f0 = Time.frameCount;
                float t0 = Time.realtimeSinceStartup, g0 = Time.time;
                yield return new WaitForSecondsRealtime(10f);
                float dt = Time.realtimeSinceStartup - t0;
                Log($"perf: {(Time.frameCount - f0) / dt:F1} fps, game time ran {(Time.time - g0) / dt * 100f:F0}% of real time");
            }
        }

        private void Log(string line)
        {
            Plugin.Log.LogInfo("[AUTOTEST] " + line);
            report.AppendLine($"[{Time.realtimeSinceStartup:F0}s] {line}");
            Flush();
        }

        private void Result(string step, bool ok, string detail)
        {
            if (ok) passed++; else failed++;
            Log($"{(ok ? "PASS" : "FAIL")} {step}: {detail}");
        }

        private void Flush()
        {
            try { File.WriteAllText(Path.Combine(Paths.BepInExRootPath, "NOInfantry_autotest.txt"), report.ToString()); }
            catch { }
        }

        private void Shot(string name)
        {
            ViewModel vm = InfantryController.Instance != null ? InfantryController.Instance.ActiveViewModel : null;
            Camera cam = SceneSingleton<CameraStateManager>.i != null ? SceneSingleton<CameraStateManager>.i.mainCamera : null;
            if (vm != null && cam != null && vm.isActiveAndEnabled && name.StartsWith("pose"))
            {
                Vector3 muzzle = cam.WorldToViewportPoint(vm.Muzzle.position);
                Vector3 grip = cam.WorldToViewportPoint(vm.transform.position);
                Log($"viewmodel {name}: local pos {vm.transform.localPosition} rot {vm.transform.localEulerAngles}; muzzle viewport {muzzle}, root viewport {grip}");
            }
            string path = Path.Combine(shotDir, $"{++shotIndex:D2}_{name}.png");
            ScreenCapture.CaptureScreenshot(path);
            Log($"screenshot {Path.GetFileName(path)}");
        }

        private IEnumerator Run()
        {
            Log("autotest started");
            GroundWar.SuspendWarfare = true;
            yield return Step("setup", Setup());
            if (soldier == null)
            {
                Finish();
                yield break;
            }
            if (Environment.GetCommandLineArgs().Any(x => x.Equals("-noiGroundWarOnly", StringComparison.OrdinalIgnoreCase)))
            {
                yield return Step("ground-war", GroundWarTest());
                Finish();
                yield break;
            }
            yield return Step("walk", Walk());
            yield return Step("rifle", Rifle());
            yield return Step("grenade", GrenadeTest());
            yield return Step("launcher-surface", LauncherSurface());
            yield return Step("launcher-air", LauncherAir());
            yield return Step("ground-vehicle", GroundVehicleTest());
            yield return Step("ship", ShipTest());
            yield return Step("airbase", AirbaseTest());
            yield return Step("squads", SquadTest());
            yield return Step("ground-war", GroundWarTest());
            Finish();
        }

        // Runs a step; exceptions inside a step are reported and the run continues.
        private IEnumerator Step(string name, IEnumerator body)
        {
            Log($"--- {name} ({1f / Mathf.Max(Time.smoothDeltaTime, 0.001f):F0} fps, timeScale {Time.timeScale:F2}, fixed dt {Time.fixedDeltaTime:F3}, max dt {Time.maximumDeltaTime:F2})");
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

        private void Finish()
        {
            Log($"=== DONE: {passed} passed, {failed} failed");
            InputShim.Scripted = false;
            StartCoroutine(QuitSoon());
        }

        private IEnumerator QuitSoon()
        {
            yield return new WaitForSecondsRealtime(3f);
            Application.Quit();
        }

        private static IEnumerator WaitUntil(Func<bool> cond, float timeout)
        {
            float end = Time.realtimeSinceStartup + timeout;
            while (!cond() && Time.realtimeSinceStartup < end) yield return null;
        }

        private static Vector3 Ground(Vector3 p)
        {
            Vector3 top = new Vector3(p.x, p.y + 2000f, p.z);
            return Physics.Raycast(top, Vector3.down, out RaycastHit hit, 5000f, PhysicsLayers.StaticsMask) ? hit.point : p;
        }

        private static Spawner Spawner => NetworkSceneSingleton<Spawner>.i;

        // The sea surface has its own collider, so look for the highest non-water hit instead.
        private static bool IsOpenWater(Vector3 p)
        {
            Vector3 top = new Vector3(p.x, Datum.LocalSeaY + 1500f, p.z);
            float highest = float.MinValue;
            foreach (RaycastHit h in Physics.RaycastAll(top, Vector3.down, 6000f))
            {
                if (h.collider.sharedMaterial == GameAssets.i.WaterMaterial || h.collider.isTrigger) continue;
                if (h.collider.attachedRigidbody != null) return false; // a ship or vehicle already there
                highest = Mathf.Max(highest, h.point.y);
            }
            return highest < Datum.LocalSeaY - 25f;
        }

        // A horizontal direction from the soldier with nothing solid in the way for `dist` metres.
        private Vector3 ClearDirection(float dist) =>
            ClearDirection(soldier.transform.position + Vector3.up * Plugin.EyeHeight.Value,
                           Flat(SceneSingleton<CameraStateManager>.i.mainCamera.transform.forward).normalized, dist, soldier.transform);

        private Vector3 ClearDirection(Vector3 eye, Vector3 baseDir, float dist, Transform ignore)
        {
            for (int i = 0; i < 24; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, i * 15f, 0f) * baseDir;
                Vector3 target = Ground(eye + dir * dist) + Vector3.up * 2f;
                bool blocked = false;
                foreach (RaycastHit h in Physics.RaycastAll(eye, (target - eye).normalized, Vector3.Distance(eye, target), ~(int)PhysicsLayers.ExclusionZonesMask, QueryTriggerInteraction.Ignore))
                {
                    if (!h.collider.transform.IsChildOf(ignore) && !h.collider.transform.IsChildOf(soldier.transform)) { blocked = true; break; }
                }
                if (!blocked) return dir;
            }
            Log("no fully clear direction found; using camera forward");
            return baseDir;
        }

        private static float PartHealth(Unit u)
        {
            if (u == null) return 0f;
            float sum = 0f;
            foreach (UnitPart part in u.partLookup) if (part != null) sum += part.hitPoints;
            return sum;
        }

        // ---------------- steps ----------------

        private IEnumerator Setup()
        {
            yield return WaitUntil(() => GameManager.GetLocalPlayer(out player) && Spawner != null
                                         && SceneSingleton<CameraStateManager>.i != null && FactionRegistry.GetAllHQs().Count >= 2, 240f);
            if (player == null)
            {
                Result("setup", false, "no local player / mission did not load");
                yield break;
            }
            Log($"mission loaded: {MissionManager.CurrentMission?.Name}, state {GameManager.gameState}");
            yield return new WaitForSecondsRealtime(5f);
            // Heavy plugin sets keep loading assets after the mission starts (seen: 65 s setup, early steps at a
            // few fps). Wait for 3 s of steady frames so movement/firing checks measure the mod, not the hitching.
            float steadySince = Time.realtimeSinceStartup, waitEnd = Time.realtimeSinceStartup + 90f;
            while (Time.realtimeSinceStartup < waitEnd && Time.realtimeSinceStartup - steadySince < 3f)
            {
                if (Time.unscaledDeltaTime > 0.1f) steadySince = Time.realtimeSinceStartup;
                yield return null;
            }
            Log($"frames steady after {Time.realtimeSinceStartup:F0} s ({1f / Mathf.Max(Time.unscaledDeltaTime, 0.001f):F0} fps)");

            List<FactionHQ> hqs = FactionRegistry.GetAllHQs().ToList();
            hq = player.HQ ?? hqs.OrderByDescending(h => FactionRegistry.airbaseLookup.Values.Count(a => a != null && a.CurrentHQ == h)).First();
            enemyHq = hqs.First(h => h != hq);
            if (player.HQ != hq)
            {
                // Same path as clicking the faction card, so the join panel closes like in real play.
                var join = SceneSingleton<NuclearOption.UI.LeaderboardMenu>.i;
                if (join != null) join.JoinFactionCallback(hq); else player.SetFaction(hq, true);
                yield return WaitUntil(() => player.HQ == hq, 10f);
                yield return new WaitForSecondsRealtime(1f);
                SceneSingleton<DynamicMap>.i?.Minimize();
            }
            airbase = FactionRegistry.airbaseLookup.Values.FirstOrDefault(a => a != null && a.CurrentHQ == hq);
            if (player.HQ != hq || airbase == null)
            {
                Result("setup", false, $"faction {player.HQ} airbase {airbase}");
                yield break;
            }
            Log($"faction {hq.faction?.factionName}, enemy {enemyHq.faction?.factionName}, airbase {airbase.name}");

            Vector3 spot = Ground(airbase.center.position + airbase.center.forward * 60f) + Vector3.up * 2f;
            soldier = Spawner.SpawnPilot(GameAssets.i.pilotDismounted, spot.ToGlobalPosition(), Quaternion.identity, hq, "NOI Test Soldier");
            GroundWar.PlaceBody(soldier.gameObject, spot, Quaternion.identity); // see PlaceBody
            soldier.Networkplayer = player;
            SceneSingleton<CameraStateManager>.i.SetFollowingUnit(soldier);
            yield return WaitUntil(() => InfantryController.Controlled == soldier, 30f);
            Result("setup", InfantryController.Controlled == soldier, $"soldier spawned at {spot}, controlled={InfantryController.Controlled != null}");
            if (InfantryController.Controlled != soldier) { soldier = null; yield break; }
            InputShim.Scripted = true;
            yield return new WaitForSecondsRealtime(1f);
            Shot("on_foot");
        }

        private IEnumerator Walk()
        {
            Vector3 start = soldier.transform.position;
            InputShim.Hold(KeyCode.W, true);
            yield return new WaitForSecondsRealtime(2f);
            InputShim.Hold(KeyCode.LeftShift, true);
            yield return new WaitForSecondsRealtime(1.5f);
            InputShim.Press(KeyCode.Space);
            yield return new WaitForSecondsRealtime(0.3f);
            float jumpY = soldier.transform.position.y;
            yield return new WaitForSecondsRealtime(0.7f);
            InputShim.ReleaseAll();
            float moved = Vector3.Distance(Flat(start), Flat(soldier.transform.position));
            Result("walk", moved > 6f, $"moved {moved:F1} m in 4.5 s (expect ~15 with sprint)");
            Shot("walked");

            // Viewmodel poses for visual review.
            yield return new WaitForSecondsRealtime(0.8f);
            Shot("pose_hip");
            InputShim.HoldMouse(1, true);
            yield return new WaitForSecondsRealtime(0.8f);
            Shot("pose_ads");
            InputShim.ReleaseAll();
            InputShim.Hold(KeyCode.W, true);
            InputShim.Hold(KeyCode.LeftShift, true);
            yield return new WaitForSecondsRealtime(0.8f);
            Shot("pose_sprint");
            InputShim.ReleaseAll();
            InputShim.HoldMouse(0, true);
            yield return new WaitForSecondsRealtime(0.3f);
            InputShim.ReleaseAll();
            InputShim.Press(KeyCode.R);
            yield return new WaitForSecondsRealtime(0.6f);
            Shot("pose_reload");
            yield return new WaitForSecondsRealtime(Plugin.ReloadTime.Value);

            // Stances: lower eye, slower movement.
            yield return StanceCheck(KeyCode.C, InfantryController.Stance.Crouch, "crouch");
            yield return StanceCheck(KeyCode.Z, InfantryController.Stance.Prone, "prone");
            InputShim.Press(KeyCode.Space); // stand up
            yield return new WaitForSecondsRealtime(0.8f);
            Result("stand up", InfantryController.Instance.CurrentStance == InfantryController.Stance.Stand, "jump key stands you up");
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        private IEnumerator StanceCheck(KeyCode key, InfantryController.Stance expected, string name)
        {
            InputShim.Press(key);
            yield return new WaitForSecondsRealtime(0.8f);
            float eye = InfantryController.Instance.EyeHeightNow;
            Vector3 start = soldier.transform.position;
            InputShim.Hold(KeyCode.W, true);
            yield return new WaitForSecondsRealtime(1.5f);
            InputShim.Hold(KeyCode.W, false);
            float moved = Vector3.Distance(Flat(start), Flat(soldier.transform.position));
            Shot("pose_" + name);
            bool ok = InfantryController.Instance.CurrentStance == expected && eye < Plugin.EyeHeight.Value - 0.3f && moved < Plugin.WalkSpeed.Value * 1.5f * 0.7f;
            Result(name, ok, $"stance {InfantryController.Instance.CurrentStance}, eye {eye:F2} m (standing {Plugin.EyeHeight.Value:F2}), moved {moved:F1} m in 1.5 s");
        }

        private IEnumerator Rifle()
        {
            VehicleDefinition truckDef = Encyclopedia.i.vehicles
                .Where(v => v != null && v.unitPrefab != null && v.unitPrefab.GetComponent<GroundVehicle>() != null
                            && v.unitPrefab.GetComponentsInChildren<Turret>(true).Length == 0)
                .OrderBy(v => v.mass).FirstOrDefault();
            if (truckDef == null) { Result("rifle", false, "no unarmed vehicle definition"); yield break; }
            Vector3 fwd = ClearDirection(80f);
            Vector3 pos = Ground(soldier.transform.position + fwd * 80f) + Vector3.up * 1f;
            enemyTruck = Spawner.SpawnVehicle(truckDef.unitPrefab, pos.ToGlobalPosition(), Quaternion.LookRotation(-fwd), Vector3.zero, enemyHq, "NOI Test Truck", 1f, true, null);
            Log($"spawned enemy {truckDef.unitName} at 80 m");
            yield return new WaitForSecondsRealtime(3f);

            int hitsBefore = InfantryController.Instance.Hits;
            int shotsBefore = InfantryController.Instance.ShotsFired;
            float end = Time.realtimeSinceStartup + 3.5f;
            InputShim.HoldMouse(1, true);
            InputShim.HoldMouse(0, true);
            bool shot = false;
            while (Time.realtimeSinceStartup < end)
            {
                if (enemyTruck != null) InfantryController.Instance.AimAt(enemyTruck.transform.position + Vector3.up * 1f);
                if (!shot && Time.realtimeSinceStartup > end - 2.5f) { Shot("rifle_ads"); shot = true; }
                yield return null;
            }
            InputShim.ReleaseAll();
            int shots = InfantryController.Instance.ShotsFired - shotsBefore;
            int hits = InfantryController.Instance.Hits - hitsBefore;
            Result("rifle", shots > 10 && hits > 0, $"{shots} shots, {hits} hits on {truckDef.unitName}, disabled={enemyTruck == null || enemyTruck.disabled}");
        }

        private IEnumerator GrenadeTest()
        {
            int before = Grenade.Detonations;
            int heldBefore = InfantryController.Instance.Grenades;
            InfantryController.Instance.AimAt(soldier.transform.position + Flat(SceneSingleton<CameraStateManager>.i.mainCamera.transform.forward) * 20f + Vector3.up * 8f);
            yield return null;
            InputShim.Press(KeyCode.G);
            yield return new WaitForSecondsRealtime(Plugin.GrenadeFuse.Value + 1f);
            Shot("grenade");
            Result("grenade", Grenade.Detonations > before && InfantryController.Instance.Grenades == heldBefore - 1,
                $"detonations {Grenade.Detonations - before}, grenades left {InfantryController.Instance.Grenades}");
        }

        private IEnumerator LauncherSurface()
        {
            // Always a fresh, unobstructed target so this step measures the missile, not the map.
            {
                VehicleDefinition def = Encyclopedia.i.vehicles.Where(v => v?.unitPrefab?.GetComponent<GroundVehicle>() != null
                                                                          && v.unitPrefab.GetComponentsInChildren<Turret>(true).Length == 0).OrderBy(v => v.mass).First();
                Vector3 fwd = ClearDirection(250f);
                Vector3 pos = Ground(soldier.transform.position + fwd * 250f) + Vector3.up;
                enemyTruck = Spawner.SpawnVehicle(def.unitPrefab, pos.ToGlobalPosition(), Quaternion.identity, Vector3.zero, enemyHq, "NOI Test Truck 2", 1f, true, null);
                yield return new WaitForSecondsRealtime(3f);
            }
            yield return FireLauncherAt(enemyTruck, "launcher-surface", 20f);
        }

        private IEnumerator LauncherAir()
        {
            AircraftDefinition def = Encyclopedia.i.aircraft
                .Where(a => a != null && a.unitPrefab != null && a.aircraftParameters != null)
                .OrderBy(a => a.value).FirstOrDefault();
            if (def == null) { Result("launcher-air", false, "no aircraft definition"); yield break; }
            Vector3 right = SceneSingleton<CameraStateManager>.i.mainCamera.transform.right;
            Vector3 fwd = Flat(SceneSingleton<CameraStateManager>.i.mainCamera.transform.forward).normalized;
            Vector3 pos = soldier.transform.position + fwd * 2500f + Vector3.up * 700f;
            Quaternion rot = Quaternion.LookRotation(Flat(right).normalized);
            Loadout loadout = def.aircraftParameters.loadouts != null && def.aircraftParameters.loadouts.Count > 0 ? def.aircraftParameters.loadouts[0] : null;
            Aircraft bandit = Spawner.SpawnAircraft(null, def.unitPrefab, loadout, 1f, default, pos.ToGlobalPosition(), rot, rot * Vector3.forward * 140f, null, enemyHq, "NOI Test Bandit", 0.5f, 0.5f);
            Log($"spawned enemy {def.unitName} 2.5 km out at 700 m");
            yield return new WaitForSecondsRealtime(4f);
            yield return FireLauncherAt(bandit, "launcher-air", 30f);
        }

        private IEnumerator FireLauncherAt(Unit target, string step, float watchSeconds)
        {
            InputShim.Press(KeyCode.Alpha2);
            yield return null;
            Launcher launcher = InfantryController.Instance.ActiveLauncher;
            Launcher.LastLaunched = null;
            InputShim.HoldMouse(1, true);
            float end = Time.realtimeSinceStartup + Plugin.LockTime.Value + 2f;
            while (Time.realtimeSinceStartup < end && !(launcher.Locked && launcher.LockTarget == target))
            {
                if (target != null) InfantryController.Instance.AimAt(target.transform.position);
                yield return null;
            }
            bool locked = launcher.Locked && launcher.LockTarget == target;
            Shot(step + "_lock");
            if (!locked)
            {
                Result(step, false, $"no lock on {target?.unitName} (lock target {launcher.LockTarget?.unitName}, progress {launcher.LockProgress:F2})");
                yield break;
            }
            InputShim.ClickMouse(0);
            yield return null;
            yield return null;
            Missile m = Launcher.LastLaunched;
            InputShim.ReleaseAll();
            if (m == null)
            {
                Result(step, false, "locked but no missile launched (see log for missile choice)");
                yield break;
            }
            float hpBefore = PartHealth(target);
            float minDist = float.MaxValue;
            float watchEnd = Time.realtimeSinceStartup + watchSeconds;
            bool screenshotted = false;
            while (Time.realtimeSinceStartup < watchEnd && m != null && target != null && !target.disabled)
            {
                minDist = Mathf.Min(minDist, Vector3.Distance(m.transform.position, target.transform.position));
                if (!screenshotted && Time.realtimeSinceStartup > watchEnd - watchSeconds + 1.5f) { Shot(step + "_flight"); screenshotted = true; }
                yield return null;
            }
            Log($"missile {(m == null ? "gone (detonated/destroyed)" : "still flying")}");
            yield return new WaitForSecondsRealtime(2f);
            bool killed = target == null || target.disabled;
            float hpAfter = PartHealth(target);
            Result(step, true, $"launched; closest approach {minDist:F0} m; target part HP {hpBefore:F0} -> {hpAfter:F0}; destroyed={killed}");
            Result(step + " effect", killed || hpAfter < hpBefore - 1f, $"damage dealt {hpBefore - hpAfter:F0}");
            InputShim.Press(KeyCode.Alpha1);
            yield return null;
        }

        private IEnumerator GroundVehicleTest()
        {
            VehicleDefinition def = Encyclopedia.i.vehicles
                .Where(v => v?.unitPrefab?.GetComponent<GroundVehicle>() != null
                            && v.unitPrefab.GetComponentsInChildren<Turret>(true).Length > 0)
                .OrderBy(v => v.mass).FirstOrDefault();
            if (def == null) { Result("ground-vehicle", false, "no armed vehicle definition"); yield break; }
            Vector3 side = SceneSingleton<CameraStateManager>.i.mainCamera.transform.right;
            Vector3 pos = Ground(soldier.transform.position + Flat(side).normalized * 5.5f) + Vector3.up;
            GroundVehicle gv = Spawner.SpawnVehicle(def.unitPrefab, pos.ToGlobalPosition(), Quaternion.LookRotation(Flat(SceneSingleton<CameraStateManager>.i.mainCamera.transform.forward)), Vector3.zero, hq, "NOI Test APC", 1f, true, null);
            Log($"spawned friendly {def.unitName} 5.5 m to the right (hull radius {gv.maxRadius:F1} m)");
            yield return new WaitForSecondsRealtime(3f);

            InputShim.Press(KeyCode.E);
            yield return WaitUntil(() => VehicleControl.Driven == gv, 3f);
            if (VehicleControl.Driven != gv)
            {
                Result("ground-vehicle", false, $"could not enter (nearest enterable {VehicleControl.FindEnterable(soldier)?.unitName}, dist {Vector3.Distance(gv.transform.position, soldier.transform.position) - gv.maxRadius:F1})");
                yield break;
            }
            Vector3 start = gv.transform.position;
            float yaw0 = gv.transform.eulerAngles.y;
            InputShim.Hold(KeyCode.W, true);
            yield return new WaitForSecondsRealtime(4f);
            InputShim.Hold(KeyCode.D, true);
            yield return new WaitForSecondsRealtime(2.5f);
            InputShim.ReleaseAll();
            float moved = Vector3.Distance(start, gv.transform.position);
            float turned = Mathf.DeltaAngle(yaw0, gv.transform.eulerAngles.y);
            Result("ground-vehicle drive", moved > 5f, $"{def.unitName} moved {moved:F1} m, yaw change {turned:F0} deg (D => expect positive)");
            Result("ground-vehicle steer", turned > 5f, $"yaw change {turned:F0}");
            Shot("vehicle_drive");

            List<WeaponStation> stations = VehicleControl.Turrets.Select(t => t.GetWeaponStation()).Where(w => w != null).ToList();
            int ammoBefore = stations.Sum(w => w.Ammo);
            // A point the turret can reach with nothing in between (the first version fired into a blast wall).
            Vector3 turretPos = gv.transform.position + Vector3.up * (gv.definition.height + 1f);
            Vector3 fireDir = ClearDirection(turretPos, Flat(gv.transform.forward).normalized, 150f, gv.transform);
            Vector3 aimPoint = Ground(turretPos + fireDir * 150f) + Vector3.up;
            InfantryController.Instance.AimAt(aimPoint);
            yield return new WaitForSecondsRealtime(2f); // let the turret traverse
            InputShim.HoldMouse(0, true);
            yield return new WaitForSecondsRealtime(3f);
            InputShim.ReleaseAll();
            int ammoAfter = stations.Sum(w => w.Ammo);
            Result("ground-vehicle guns", ammoAfter < ammoBefore && VehicleControl.Driven == gv && !gv.disabled,
                $"turret ammo {ammoBefore} -> {ammoAfter}, still driving={VehicleControl.Driven == gv}, vehicle intact={!gv.disabled}");
            Shot("vehicle_fire");

            InputShim.Press(KeyCode.E);
            yield return WaitUntil(() => VehicleControl.Driven == null, 3f);
            yield return new WaitForSecondsRealtime(1f);
            float outside = Vector3.Distance(soldier.transform.position, gv.transform.position);
            Result("ground-vehicle exit", VehicleControl.Driven == null && outside > gv.maxRadius * 0.5f, $"soldier {outside:F1} m from vehicle");
        }

        private IEnumerator ShipTest()
        {
            ShipDefinition def = Encyclopedia.i.ships.Where(s => s?.unitPrefab != null).OrderBy(s => s.mass).FirstOrDefault();
            if (def == null) { Result("ship", false, "no ship definition"); yield break; }

            // Find open water: sample rings around the airbase for terrain well below sea level.
            Vector3 center = airbase.center.position;
            Vector3? sea = null;
            for (float r = 2000f; r <= 40000f && sea == null; r += 2000f)
            {
                for (int i = 0; i < 24; i++)
                {
                    float a = i * Mathf.PI * 2f / 24f;
                    Vector3 p = center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
                    if (IsOpenWater(p))
                    {
                        sea = new Vector3(p.x, Datum.LocalSeaY, p.z);
                        break;
                    }
                }
            }
            if (sea == null) { Result("ship", false, "no open water within 40 km of the airbase"); yield break; }
            Ship ship = Spawner.SpawnShip(def.unitPrefab, sea.Value.ToGlobalPosition(), Quaternion.identity, hq, "NOI Test Ship", 1f, true);
            Log($"spawned friendly {def.unitName} at sea {Vector3.Distance(center, sea.Value) / 1000f:F0} km from base");
            yield return new WaitForSecondsRealtime(6f);

            // Put the soldier on deck.
            Vector3 above = ship.transform.position + Vector3.up * 80f;
            Vector3 deck = ship.transform.position + Vector3.up * 5f;
            foreach (RaycastHit h in Physics.RaycastAll(above, Vector3.down, 120f).OrderBy(h => h.distance))
            {
                if (h.collider.transform.IsChildOf(ship.transform)) { deck = h.point; break; }
            }
            soldier.transform.position = deck + Vector3.up * 1.4f;
            soldier.rb.position = soldier.transform.position;
            soldier.rb.velocity = ship.rb.velocity;
            yield return new WaitForSecondsRealtime(3f);
            float onDeck = Vector3.Distance(soldier.transform.position, ship.transform.position);
            Log($"soldier on deck: {onDeck:F1} m from ship centre, height above sea {soldier.transform.position.y - Datum.LocalSeaY:F1} m");
            Shot("ship_deck");

            InputShim.Press(KeyCode.E);
            yield return WaitUntil(() => VehicleControl.Driven == ship, 3f);
            if (VehicleControl.Driven != ship)
            {
                Result("ship helm", false, $"could not take helm (nearest {VehicleControl.FindEnterable(soldier)?.unitName})");
                yield break;
            }
            Result("ship helm", true, $"took the helm of {def.unitName}");
            float yaw0 = ship.transform.eulerAngles.y;
            InputShim.Hold(KeyCode.W, true);
            yield return new WaitForSecondsRealtime(8f);
            float speed = ship.rb.velocity.magnitude;
            InputShim.Hold(KeyCode.D, true);
            yield return new WaitForSecondsRealtime(10f);
            InputShim.ReleaseAll();
            float turned = Mathf.DeltaAngle(yaw0, ship.transform.eulerAngles.y);
            Result("ship throttle", speed > 1f, $"speed after 8 s {speed:F1} m/s");
            Result("ship steer", turned > 3f, $"yaw change {turned:F1} deg with D held (positive = right, correct)");
            Shot("ship_helm");
            InputShim.Press(KeyCode.E);
            yield return WaitUntil(() => VehicleControl.Driven == null, 3f);
            yield return new WaitForSecondsRealtime(1f);
            float afterExit = soldier.transform.position.y - Datum.LocalSeaY;
            Result("ship exit", VehicleControl.Driven == null && afterExit > 1f, $"soldier {afterExit:F1} m above sea after leaving the helm");
        }

        private IEnumerator SquadTest()
        {
            PilotDismounted me = InfantryController.Controlled;
            if (me == null) { Result("squads", false, "no soldier under control"); yield break; }
            InputShim.Scripted = true;
            InputShim.Press(Plugin.FireteamKey.Value);
            yield return new WaitForSecondsRealtime(3f);
            int team = AiSquads.AliveFollowing(me);
            List<PilotDismounted> fireteam = AiSquads.All.Values.Where(a => (object)a.Leader == me).Select(a => a.Pilot).ToList();
            Result("fireteam spawn", team == Plugin.SquadSize.Value, $"{team} riflemen following");
            Shot("fireteam");

            // Walk away and check they keep up.
            InfantryController.Instance.AimAt(me.transform.position + Flat(me.transform.forward) * 50f + Vector3.up * 1.5f);
            InputShim.Hold(KeyCode.W, true);
            yield return new WaitForSecondsRealtime(6f);
            InputShim.ReleaseAll();
            yield return new WaitForSecondsRealtime(2f);
            float avg = AiSquads.All.Values.Where(a => a.Leader == me && a.Pilot != null && !a.Pilot.disabled)
                .Select(a => Vector3.Distance(a.Pilot.transform.position, me.transform.position)).DefaultIfEmpty(999f).Average();
            Result("fireteam follows", avg < 20f, $"average distance to leader {avg:F1} m after a 6 s walk");
            Shot("fireteam_follow");

            // Enemy patrol close enough for a quick firefight (setting restored afterwards).
            float savedDist = Plugin.PatrolDistance.Value;
            Plugin.PatrolDistance.Value = 220f;
            int before = AiSquads.All.Count;
            int shots0 = AiSquads.ShotsFired, hits0 = AiSquads.HitsLanded;
            InputShim.Press(Plugin.PatrolKey.Value);
            yield return new WaitForSecondsRealtime(3f);
            Plugin.PatrolDistance.Value = savedDist;
            List<PilotDismounted> patrol = AiSquads.All.Values.Where(a => a.IsPatrol).Select(a => a.Pilot).ToList();
            Result("patrol spawn", patrol.Count == Plugin.PatrolSize.Value, $"{patrol.Count} enemy soldiers {Plugin.PatrolDistance.Value} m class distance");

            // Let them fight; prone to keep our own soldier alive.
            InputShim.Press(Plugin.ProneKey.Value);
            float end = Time.realtimeSinceStartup + 75f;
            bool shot = false;
            while (Time.realtimeSinceStartup < end && patrol.Any(p => p != null && !p.disabled))
            {
                if (!shot && AiSquads.ShotsFired - shots0 > 20) { Shot("firefight"); shot = true; }
                yield return new WaitForSecondsRealtime(0.5f);
            }
            int patrolDown = patrol.Count(p => p == null || p.disabled || Medical.IsDowned(p));
            int teamLeft = fireteam.Count(p => p != null && !p.disabled && !Medical.IsDowned(p)); // explicit list: Unity null-equality fooled the leader check
            int teamDown = fireteam.Count - teamLeft;
            // A real exchange of fire with casualties on either side (the outcome itself is a fair fight).
            Result("firefight", AiSquads.ShotsFired - shots0 > 0 && AiSquads.HitsLanded - hits0 > 0 && patrolDown + teamDown > 0,
                $"AI shots {AiSquads.ShotsFired - shots0}, hits {AiSquads.HitsLanded - hits0}, patrol down {patrolDown}/{patrol.Count}, fireteam left {teamLeft}, me alive {me != null && !me.disabled}");
        }

        private float TeamDistance(List<PilotDismounted> team, Vector3 to) =>
            team.Where(p => p != null && !p.disabled).Select(p => Vector3.Distance(Flat(p.transform.position), Flat(to))).DefaultIfEmpty(999f).Average();

        private IEnumerator GroundWarTest()
        {
            // Calibration data for capture balance and the motor pool.
            Log($"airbase {GroundWar.AirbaseName(airbase)}: capture defense {airbase.SavedAirbase.CaptureDefense}, range {airbase.SavedAirbase.CaptureRange}");
            foreach (VehicleDefinition v in Encyclopedia.i.vehicles.Where(v => v != null && v.captureStrength > 0f).Take(12))
                Log($"  vehicle {v.unitName}: captureStrength {v.captureStrength}, defense {v.captureDefense}, mass {v.mass:F0}, value {v.value}");
            foreach (VehicleDefinition v in GroundWar.MotorPool) Log($"  motor pool: {v.unitName} mass {v.mass:F0} value {v.value} cost {GroundWar.Cost(v):F2}");

            // A clean battlefield: the previous step's patrol is still hunting around this airbase.
            Log($"cleared {AiSquads.Despawn(_ => true)} AI soldiers from earlier steps");
            yield return new WaitForSecondsRealtime(1f);

            // 1. Deploy on foot from the aircraft selection menu, with a kit.
            PilotDismounted old = InfantryController.Controlled;
            if (!AirbaseServices.OpenAircraftMenu(airbase)) { Result("deploy menu", false, "could not open the aircraft selection menu"); yield break; }
            yield return WaitUntil(() => GroundWar.OpenMenu != null, 5f);
            yield return new WaitForSecondsRealtime(1.5f);
            Shot("deploy_panel");
            Airbase menuBase = GroundWar.MenuAirbase(GroundWar.OpenMenu);
            Result("deploy menu", menuBase == airbase, $"selection menu at {GroundWar.AirbaseName(menuBase)}");
            bool requested = GroundWar.RequestDeploy(menuBase, Kit.AntiTank);
            float waitEnd = Time.realtimeSinceStartup + 20f, nextDiag = 0f;
            while (!(InfantryController.Controlled != null && InfantryController.Controlled != old) && Time.realtimeSinceStartup < waitEnd)
            {
                if (Time.realtimeSinceStartup > nextDiag)
                {
                    nextDiag = Time.realtimeSinceStartup + 2f;
                    foreach (PilotDismounted pd in UnitRegistry.allUnits.OfType<PilotDismounted>().Where(x => x.Networkplayer == player))
                        Log($"  diag {pd.unitName}: {Vector3.Distance(Flat(pd.transform.position), Flat(airbase.center.position)):F0} m from centre, state {pd.animationState}, t {pd.timeSinceSpawn:F1}, vel {pd.rb.velocity.magnitude:F1}, globalY {pd.transform.position.GlobalY():F1}, disabled {pd.disabled}, localSim {pd.LocalSim}, kinematic {pd.rb.isKinematic}");
                }
                yield return null;
            }
            PilotDismounted me = InfantryController.Controlled;
            bool deployed = requested && me != null && me != old;
            float fromBase = me != null ? Vector3.Distance(Flat(me.transform.position), Flat(airbase.center.position)) : -1f;
            Result("deploy on foot", deployed && InfantryController.Instance.CurrentKit == Kit.AntiTank && fromBase < 600f && old == null && !DynamicMap.mapMaximized,
                $"controlled {me?.unitName}, kit {InfantryController.Instance.CurrentKit}, {fromBase:F0} m from the airbase centre, old soldier gone {old == null}, map open {DynamicMap.mapMaximized}, menu closed {GroundWar.OpenMenu == null}");
            if (!deployed) yield break;
            soldier = me;
            InputShim.Scripted = true;
            Launcher l = InfantryController.Instance.ActiveLauncher;
            Result("kit loadout", l != null && !l.AllowAir && l.AllowSurface && l.Ammo == GroundWar.KitRockets(Kit.AntiTank) && InfantryController.Instance.Grenades == GroundWar.KitGrenades(Kit.AntiTank),
                $"rockets {l?.Ammo}, AA {l?.AllowAir}, AT {l?.AllowSurface}, grenades {InfantryController.Instance.Grenades}");
            yield return new WaitForSecondsRealtime(1f);
            Shot("deployed");

            // 2. Soldiers count toward capture.
            yield return new WaitForSecondsRealtime(1.5f);
            Result("capture strength", Mathf.Approximately(me.CaptureStrength, Plugin.SoldierCaptureStrength.Value), $"soldier capture strength {me.CaptureStrength} (config {Plugin.SoldierCaptureStrength.Value})");

            // 3. Fireteam orders: hold, move, follow.
            Vector3 fwd = ClearDirection(60f);
            InfantryController.Instance.AimAt(me.transform.position + fwd * 60f + Vector3.up * 1.5f);
            InputShim.Press(Plugin.FireteamKey.Value);
            yield return new WaitForSecondsRealtime(4f);
            List<PilotDismounted> team = AiSquads.All.Values.Where(a => (object)a.Leader == me).Select(a => a.Pilot).ToList();
            Result("fireteam (deployed)", team.Count == Plugin.SquadSize.Value, $"{team.Count} riflemen");
            foreach (PilotDismounted r in team) Log($"  {r.UniqueName} {Vector3.Distance(r.transform.position, me.transform.position):F0} m from me; me global {me.transform.position.ToGlobalPosition()}");

            Vector3 holdAt = me.transform.position;
            InputShim.Press(Plugin.SquadMenuKey.Value);
            yield return null;
            yield return null;
            Shot("orders_menu");
            InputShim.Press(KeyCode.Alpha2);
            yield return new WaitForSecondsRealtime(0.5f);
            bool weaponUnchanged = InfantryController.Instance.ActiveLauncher != null && !InfantryController.Instance.ActiveLauncher.Visible;
            InputShim.Hold(KeyCode.W, true);
            yield return new WaitForSecondsRealtime(6f);
            InputShim.ReleaseAll();
            yield return new WaitForSecondsRealtime(2f);
            float leaderMoved = Vector3.Distance(Flat(holdAt), Flat(me.transform.position));
            float holdDist = TeamDistance(team, holdAt);
            Result("order hold", leaderMoved > 12f && holdDist < 9f && weaponUnchanged, $"leader walked {leaderMoved:F0} m, team stayed {holdDist:F1} m from the hold point (2 chose the order, not the launcher: {weaponUnchanged})");

            Vector3 movePoint = Ground(me.transform.position + fwd * 45f);
            InfantryController.Instance.AimAt(movePoint);
            yield return null;
            InfantryController.Instance.IssueOrder(3);
            yield return new WaitForSecondsRealtime(14f);
            float moveDist = TeamDistance(team, movePoint);
            Result("order move", moveDist < 12f, $"team {moveDist:F1} m from the ordered point");
            Shot("order_move");

            InfantryController.Instance.IssueOrder(1);
            yield return new WaitForSecondsRealtime(12f);
            float followDist = TeamDistance(team, me.transform.position);
            Result("order follow", followDist < 15f, $"team {followDist:F1} m from the leader");

            // Rally point where we stand (used later to redeploy as a medic).
            InputShim.Press(Plugin.RallyKey.Value);
            yield return new WaitForSecondsRealtime(1f);
            float rallyOff = GroundWar.LocalRally.HasValue ? Vector3.Distance(GroundWar.LocalRally.Value.ToLocalPosition(), me.transform.position) : -1f;
            Result("rally placed", GroundWar.LocalRally.HasValue && rallyOff < 3f, $"rally {(GroundWar.LocalRally.HasValue ? "set" : "missing")}, {rallyOff:F1} m from us");

            // 4. Motor pool at the airbase (funds topped up so the test doesn't depend on the economy).
            Vector3 home = Ground(airbase.center.position + airbase.center.forward * 60f) + Vector3.up * 1.5f;
            me.transform.position = home;
            me.rb.position = home;
            me.rb.velocity = Vector3.zero;
            yield return new WaitForSecondsRealtime(1f);
            if (GroundWar.MotorPool.Count > 0) player.AddAllocation(GroundWar.Cost(GroundWar.MotorPool[0]) + 10f);
            float funds0 = player.Allocation;
            InputShim.Press(Plugin.MotorPoolKey.Value);
            yield return null;
            yield return null;
            Shot("motor_pool");
            InputShim.Press(KeyCode.Alpha1);
            yield return new WaitForSecondsRealtime(3f);
            GroundVehicle issued = UnitRegistry.allUnits.OfType<GroundVehicle>().FirstOrDefault(v => v != null && v.UniqueName != null && v.UniqueName.StartsWith("NOI Motor Pool")
                                                                                               && Vector3.Distance(v.transform.position, me.transform.position) < 40f);
            Result("motor pool", issued != null && issued.NetworkHQ == hq, $"{issued?.unitName} at {(issued != null ? Vector3.Distance(issued.transform.position, me.transform.position) : -1f):F0} m, funds {funds0:F1} -> {player.Allocation:F1}");
            yield return new WaitForSecondsRealtime(1f);
            Shot("motor_pool_vehicle");

            // 5. The AI front line.
            int ai0 = AiSquads.All.Count;
            int waves0 = GroundWar.WavesSent;
            GroundWar.SuspendWarfare = false;
            GroundWar.ForceWave();
            yield return new WaitForSecondsRealtime(2f);
            GroundWar.SuspendWarfare = true;
            var objectives = AiSquads.All.Values.Where(a => a.Objective != null && a.Leader == null).GroupBy(a => a.Pilot.NetworkHQ)
                .Select(g => $"{g.Key.faction.factionName} x{g.Count()} -> {GroundWar.AirbaseName(g.First().Objective)}").ToList();
            Result("front line", GroundWar.WavesSent > waves0 && AiSquads.All.Count > ai0, $"{GroundWar.WavesSent - waves0} squads sent: {string.Join("; ", objectives)}");
            List<AiSoldier> movers = AiSquads.All.Values.Where(a => a.Objective != null && a.Leader == null && a.Pilot != null).ToList();
            Dictionary<AiSoldier, float> d0 = movers.ToDictionary(a => a, a => Vector3.Distance(a.Pilot.transform.position, a.Objective.center.position));
            foreach (AiSoldier a in movers) Log($"  front t0 {a.Pilot.UniqueName}: {d0[a]:F0} m to go, globalY {a.Pilot.transform.position.GlobalY():F1}, vel {a.Pilot.rb.velocity.magnitude:F1}");
            yield return new WaitForSecondsRealtime(25f);
            foreach (Convoy c in Transport.Convoys)
                Log($"  convoy {c.Truck?.unitName}: speed {(c.Truck != null ? c.Truck.speed : 0f):F1} m/s, {c.Riders.Count} aboard, {(c.Truck != null ? Vector3.Distance(c.Truck.transform.position, c.Objective.center.position) : -1f):F0} m to go");
            foreach (AiSoldier a in movers.Where(m => m.Pilot != null && !m.Pilot.disabled))
                Log($"  front t10 {a.Pilot.UniqueName}: {Vector3.Distance(a.Pilot.transform.position, a.Objective.center.position):F0} m to go, globalY {a.Pilot.transform.position.GlobalY():F1}, vel {a.Pilot.rb.velocity.magnitude:F1}, wish {a.Wish.magnitude:F1}, target {a.Target?.unitName}, kinematic {a.Pilot.rb.isKinematic}");
            int closer = movers.Count(a => a.Pilot != null && !a.Pilot.disabled && a.Objective != null && Vector3.Distance(a.Pilot.transform.position, a.Objective.center.position) < d0[a] - 15f);
            Result("front line advance", closer > 0, $"{closer}/{movers.Count} front-line soldiers moved 15+ m toward their objective in 25 s (trucks sent {Transport.Sent})");
            // Trucks: an enemy squad driven at our airbase from 1.8 km out.
            {
                Vector3 away = Flat(airbase.center.forward).normalized;
                Vector3 start = Vector3.zero;
                for (int i = 0; i < 12 && start == Vector3.zero; i++)
                {
                    Vector3 cand = Ground(airbase.center.position + Quaternion.Euler(0f, i * 30f, 0f) * away * 1800f);
                    if (cand.GlobalY() > 2f) start = cand;
                }
                Convoy convoy = start != Vector3.zero ? Transport.Send(enemyHq, start, airbase, 3) : null;
                if (convoy == null) Result("truck transport", false, $"no convoy (dry start {start != Vector3.zero}, truck {Transport.TruckDef?.unitName})");
                else
                {
                    float d0t = Vector3.Distance(convoy.Truck.transform.position, airbase.center.position);
                    yield return new WaitForSecondsRealtime(25f);
                    float d1t = convoy.Truck != null ? Vector3.Distance(convoy.Truck.transform.position, airbase.center.position) : -1f;
                    int aboard = convoy.Riders.Count(r => r.Riding != null && r.Pilot != null && Vector3.Distance(r.Pilot.transform.position, convoy.Truck.transform.position) < 6f);
                    Result("truck transport", convoy.Truck != null && d1t < d0t - 30f && aboard == convoy.Riders.Count && aboard > 0,
                        $"{convoy.Truck?.unitName}: {d0t:F0} -> {d1t:F0} m from the airbase in 25 s, {aboard}/{convoy.Riders.Count} riding");
                    Shot("truck");
                }
            }
            Convoy first = Transport.Convoys.FirstOrDefault(c => c.Riders.Count > 0);
            if (first != null)
            {
                List<AiSoldier> riders = first.Riders.ToList();
                Transport.Dismount(first, "test");
                Transport.Convoys.Remove(first);
                yield return new WaitForSecondsRealtime(3f);
                int onFoot = riders.Count(r => r.Pilot != null && !r.Pilot.disabled && r.Riding == null && r.Pilot.transform.position.GlobalY() > 0.5f);
                Result("truck dismount", onFoot == riders.Count, $"{onFoot}/{riders.Count} riders on the ground beside the {first.Truck?.unitName}");
            }
            Log($"cleared {AiSquads.Despawn(a => a.Leader == null)} front-line/convoy soldiers before the medical steps");

            // 6. Medical: go down, get revived by the fireteam, revive a rifleman, bandage as a medic.
            yield return MedicalSteps();
            me = InfantryController.Controlled;
            if (me == null) yield break;

            // 7. Killed -> map, to redeploy (an overwhelming hit: no going down).
            me.TakeDamage(5000f, 0f, 0f, 0f, 0f, PersistentID.None);
            yield return WaitUntil(() => DynamicMap.mapMaximized, 12f);
            Result("death -> map", DynamicMap.mapMaximized && InfantryController.Controlled == null, $"map open {DynamicMap.mapMaximized}, infantry released {InfantryController.Controlled == null}");
            Shot("respawn_map");
        }

        private IEnumerator MedicalSteps()
        {
            PilotDismounted me = InfantryController.Controlled;
            Vector3 home = Ground(airbase.center.position + airbase.center.forward * 60f) + Vector3.up * 1.5f;
            me.transform.position = home;
            me.rb.position = home;
            me.rb.velocity = Vector3.zero;
            InputShim.Press(Plugin.FireteamKey.Value); // top the fireteam up
            yield return new WaitForSecondsRealtime(5f);

            // a) A lethal (but not overwhelming) hit puts us down; our riflemen come and revive us.
            me.TakeDamage(170f, 0f, 0f, 0f, 0f, PersistentID.None);
            yield return new WaitForSecondsRealtime(0.5f);
            bool down = InfantryController.Instance.Downed;
            Result("go down", down && !me.disabled && Medical.Health(me) <= 1.01f, $"downed {down}, alive {!me.disabled}, HP {Medical.Health(me):F1}, bleed {Medical.BleedLeft(me):F0} s");
            Shot("downed");
            yield return WaitUntil(() => me == null || me.disabled || !Medical.IsDowned(me), Plugin.ReviveTime.Value + 20f);
            Result("AI revives player", me != null && !me.disabled && !Medical.IsDowned(me) && Medical.Health(me) > 20f,
                $"alive {me != null && !me.disabled}, downed {Medical.IsDowned(me)}, HP {Medical.Health(me):F0}, revives {Medical.Revives}");
            if (me == null || me.disabled) yield break;

            // b) Down a rifleman (the others sent away) and revive it ourselves with the interact key.
            List<AiSoldier> team = AiSquads.All.Values.Where(a => (object)a.Leader == me && a.Pilot != null && !a.Pilot.disabled).ToList();
            if (team.Count > 0)
            {
                AiSquads.Despawn(a => a != team[0]);
                PilotDismounted r = team[0].Pilot;
                r.TakeDamage(170f, 0f, 0f, 0f, 0f, PersistentID.None);
                yield return new WaitForSecondsRealtime(0.5f);
                Vector3 next = r.transform.position + Flat(r.transform.right).normalized * 1.2f;
                me.transform.position = next;
                me.rb.position = next;
                me.rb.velocity = Vector3.zero;
                yield return new WaitForSecondsRealtime(0.5f);
                Result("rifleman down", Medical.IsDowned(r), $"{r.unitName} downed {Medical.IsDowned(r)}");
                int revives0 = Medical.Revives;
                InputShim.Hold(Plugin.InteractKey.Value, true);
                yield return new WaitForSecondsRealtime(1f);
                Shot("reviving");
                yield return WaitUntil(() => !Medical.IsDowned(r), Plugin.ReviveTime.Value + 3f);
                InputShim.ReleaseAll();
                Result("player revives AI", !Medical.IsDowned(r) && !r.disabled && Medical.Revives > revives0,
                    $"{r.unitName} downed {Medical.IsDowned(r)}, HP {Medical.Health(r):F0}, distance {Vector3.Distance(r.transform.position, me.transform.position):F1} m");
            }

            // c) Redeploy as a medic, take a wound, bandage ourselves.
            if (!AirbaseServices.OpenAircraftMenu(airbase)) yield break;
            yield return WaitUntil(() => GroundWar.OpenMenu != null, 5f);
            PilotDismounted old = me;
            bool rallyKnown = GroundWar.LocalRally.HasValue;
            GroundWar.RequestDeploy(GroundWar.MenuAirbase(GroundWar.OpenMenu) ?? airbase, Kit.Medic, atRally: rallyKnown);
            yield return WaitUntil(() => InfantryController.Controlled != null && InfantryController.Controlled != old, 20f);
            me = InfantryController.Controlled;
            if (me == null || me == old) { Result("medic deploy", false, "no soldier"); yield break; }
            float fromRally = rallyKnown ? Vector3.Distance(Flat(GroundWar.LocalRally.Value.ToLocalPosition()), Flat(me.transform.position)) : -1f;
            Result("deploy at rally", rallyKnown && fromRally < 12f, $"medic deployed {fromRally:F1} m from the rally point");
            InputShim.Scripted = true;
            yield return new WaitForSecondsRealtime(1f);
            me.TakeDamage(60f, 0f, 0f, 0f, 0f, PersistentID.None);
            yield return new WaitForSecondsRealtime(0.5f);
            float hurt = Medical.Health(me);
            int b0 = InfantryController.Instance.Bandages;
            InputShim.Press(Plugin.InteractKey.Value);
            yield return new WaitForSecondsRealtime(1f);
            Result("medic bandage", Medical.Health(me) > hurt + 10f && InfantryController.Instance.Bandages == b0 - 1,
                $"kit {InfantryController.Instance.CurrentKit}, HP {hurt:F0} -> {Medical.Health(me):F0}, bandages {b0} -> {InfantryController.Instance.Bandages}");
        }

        private IEnumerator AirbaseTest()
        {
            Vector3 spot = Ground(airbase.center.position) + Vector3.up * 1.5f;
            soldier.transform.position = spot;
            soldier.rb.position = spot;
            soldier.rb.velocity = Vector3.zero;
            yield return new WaitForSecondsRealtime(2f);
            Airbase near = AirbaseServices.NearFriendlyAirbase(soldier);
            Result("airbase detect", near != null, $"near friendly airbase: {near?.name}");
            if (near == null) yield break;

            InputShim.Press(KeyCode.B);
            yield return WaitUntil(() => FindObjectOfType<AircraftSelectionMenu>() != null, 5f);
            AircraftSelectionMenu menu = FindObjectOfType<AircraftSelectionMenu>();
            Result("airbase menu", menu != null, "aircraft selection menu opened");
            Shot("aircraft_menu");
            if (menu == null) yield break;

            yield return new WaitForSecondsRealtime(2f);
            AircraftDefinition owned = Encyclopedia.i.aircraft.FirstOrDefault(a => a != null && menu.CanFlyAircraft(a) && player.OwnsAirframe(a, true));
            if (owned != null)
            {
                Log($"flying owned airframe {owned.unitName} via the menu");
                menu.SetSelectedType(owned);
                yield return null;
                menu.FlyAircraft();
            }
            else
            {
                // The game's economy won't let this test profile fly anything; emulate a spawn to test the hand-off.
                Log("player owns no airframe here; spawning one directly to test the hand-off");
                Destroy(menu.gameObject);
                AircraftDefinition def = Encyclopedia.i.aircraft.Where(a => a?.unitPrefab != null).OrderBy(a => a.value).First();
                Vector3 p = Ground(airbase.center.position + airbase.center.right * 40f) + Vector3.up * def.spawnOffset.y;
                Loadout lo = def.aircraftParameters.loadouts != null && def.aircraftParameters.loadouts.Count > 0 ? def.aircraftParameters.loadouts[0] : null;
                Spawner.SpawnAircraft(player, def.unitPrefab, lo, 1f, default, p.ToGlobalPosition(), airbase.center.rotation, Vector3.zero, null, hq, "NOI Test Ride", 1f, 1f);
            }
            yield return WaitUntil(() => AirbaseServices.HasNewAircraft() && InfantryController.Controlled == null, 30f);
            bool flying = AirbaseServices.HasNewAircraft();
            Result("airbase fly", flying && InfantryController.Controlled == null,
                $"new aircraft={flying}, infantry released={InfantryController.Controlled == null}, soldier returned={soldier == null}");
            yield return new WaitForSecondsRealtime(4f);
            Shot("new_aircraft");
            if (!flying) yield break;

            InputShim.Scripted = true;
            InputShim.Press(KeyCode.F8);
            yield return WaitUntil(() => InfantryController.Controlled != null, 25f);
            Result("dismount F8", InfantryController.Controlled != null, "climbed out of the stopped aircraft");
            yield return new WaitForSecondsRealtime(1f);
            Shot("dismounted");
        }
    }
}

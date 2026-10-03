using System.Collections.Generic;
using NuclearOption.Networking;
using UnityEngine;
using UnityEngine.Rendering;

namespace NOInfantry
{
    // Shoulder-fired lock-on launcher. Hold RMB on an enemy near the crosshair to lock, LMB to fire.
    // Aircraft get the game's lightest IR anti-air missile, surface targets its lightest anti-surface missile,
    // both picked from Encyclopedia.weaponMounts and spawned through the game's own Spawner (server/host only).
    internal class Launcher
    {
        internal int Ammo;
        internal bool AllowAir = true, AllowSurface = true; // kit restrictions
        internal static Missile LastLaunched;
        internal static readonly Dictionary<Missile, Unit> Launched = new Dictionary<Missile, Unit>();
        internal Unit LockTarget { get; private set; }
        internal float LockProgress { get; private set; }
        internal bool Locked => LockTarget != null && LockProgress >= 1f;

        private float nextShotTime;
        private Transform tube;
        private AudioSource tone;
        private AudioClip beepClip;
        private AudioClip lockClip;
        private float nextBeep;

        // The soldier acts as a spotter: optical/TV seekers start from the faction's tracking data
        // (NetworkHQ.TryGetKnownPosition), so a target nobody has reported would make them fly blind.
        private Unit spotTarget;
        private float spotUntil;
        private float nextSpot;

        private static GameObject aaMissile, atMissile;
        private static string aaName = "-", atName = "-";
        private static bool searched;

        internal string Summary => $"LAUNCHER {Ammo}" + (AllowAir ? $"   AA: {aaName}" : "") + (AllowSurface ? $"   AT: {atName}" : "");

        internal Launcher(GameObject host)
        {
            Ammo = Plugin.LauncherAmmo.Value;
            tone = host.AddComponent<AudioSource>();
            tone.playOnAwake = false;
            tone.spatialBlend = 0f;
            tone.volume = 0.25f;
            if (SoundManager.i != null) tone.outputAudioMixerGroup = SoundManager.i.InterfaceMixer;
            beepClip = MakeTone(1000f, 0.06f);
            lockClip = MakeTone(1400f, 0.5f);
        }

        internal static void FindMissiles()
        {
            if (searched) return;
            searched = true;
            float bestAA = float.MaxValue, bestAT = float.MaxValue;
            var seen = new List<string>();
            foreach (WeaponMount mount in Encyclopedia.i.weaponMounts)
            {
                WeaponInfo info = mount != null ? mount.info : null;
                if (info == null || !info.missile || info.nuclear || info.strategic || info.weaponPrefab == null) continue;
                if (info.weaponPrefab.GetComponent<Missile>() == null) continue;
                seen.Add($"{info.shortName}(AA {info.effectiveness.antiAir:F1} AS {info.effectiveness.antiSurface:F1} IR {info.targetRequirements.minIR} m {info.massPerRound} r {info.targetRequirements.maxRange} laser {info.laserGuided})");

                if (Matches(info.shortName, Plugin.AaMissileName.Value) ||
                    (Plugin.AaMissileName.Value == "" && info.effectiveness.antiAir >= 0.5f && info.effectiveness.antiSurface < 0.5f))
                {
                    // Prefer IR seekers (fire-and-forget like a real MANPADS), then the lightest.
                    float score = info.massPerRound + (info.targetRequirements.minIR > 0f ? 0f : 10000f);
                    if (score < bestAA) { bestAA = score; aaMissile = info.weaponPrefab; aaName = info.shortName; }
                }
                if (Matches(info.shortName, Plugin.AtMissileName.Value) ||
                    (Plugin.AtMissileName.Value == "" && info.effectiveness.antiSurface >= 0.5f && info.effectiveness.antiAir < 0.3f
                     && !info.bomb && info.targetRequirements.maxRange <= 20000f))
                {
                    // Laser-guided missiles need someone lasing the target; prefer self-guided ones.
                    float score = info.massPerRound + (info.laserGuided ? 10000f : 0f);
                    if (score < bestAT) { bestAT = score; atMissile = info.weaponPrefab; atName = info.shortName; }
                }
            }
            Plugin.Log.LogInfo($"Launcher missiles: AA={aaName}, AT={atName}. Candidates: {string.Join(", ", seen)}");
        }

        private static bool Matches(string name, string wanted) =>
            !string.IsNullOrEmpty(wanted) && name != null && name.Equals(wanted, System.StringComparison.OrdinalIgnoreCase);

        internal void CreateTube(Transform cameraTransform, Shader lit)
        {
            tube = new GameObject("NOInfantryLauncher").transform;
            tube.SetParent(cameraTransform, false);
            tube.localPosition = new Vector3(0.18f, -0.12f, 0.05f);
            Material olive = null;
            if (lit != null)
            {
                olive = new Material(lit) { color = new Color(0.26f, 0.3f, 0.2f) };
                if (olive.HasProperty("_BaseColor")) olive.SetColor("_BaseColor", olive.color);
            }
            AddPart(PrimitiveType.Cylinder, new Vector3(0f, 0f, 0.15f), new Vector3(0.09f, 0.6f, 0.09f), new Vector3(90f, 0f, 0f), olive);
            AddPart(PrimitiveType.Cube, new Vector3(-0.07f, 0.04f, 0.1f), new Vector3(0.05f, 0.05f, 0.12f), Vector3.zero, olive);  // sight unit
            AddPart(PrimitiveType.Cube, new Vector3(0f, -0.08f, 0.0f), new Vector3(0.03f, 0.09f, 0.04f), new Vector3(15f, 0f, 0f), olive); // grip
            tube.gameObject.SetActive(false);
        }

        private void AddPart(PrimitiveType type, Vector3 pos, Vector3 scale, Vector3 euler, Material mat)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(tube, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.transform.localEulerAngles = euler;
            var r = go.GetComponent<Renderer>();
            if (mat != null) r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
        }

        internal bool Visible => tube != null && tube.gameObject.activeSelf;

        internal void SetVisible(bool visible)
        {
            if (tube != null) tube.gameObject.SetActive(visible);
            if (!visible) ClearLock();
        }

        internal void Destroy()
        {
            if (tube != null) Object.Destroy(tube.gameObject);
        }

        internal void ClearLock()
        {
            LockTarget = null;
            LockProgress = 0f;
        }

        // Called every frame while the launcher is the active weapon.
        internal void Tick(PilotDismounted self, Camera cam, bool seeking, bool fire)
        {
            Spot(self, cam);
            if (!seeking)
            {
                ClearLock();
            }
            else
            {
                Unit candidate = FindTarget(self, cam, LockTarget != null ? Plugin.LockConeDegrees.Value * 1.5f : Plugin.LockConeDegrees.Value, AllowAir, AllowSurface);
                if (candidate != LockTarget)
                {
                    LockTarget = candidate;
                    LockProgress = 0f;
                }
                if (LockTarget != null && LockProgress < 1f)
                {
                    LockProgress = Mathf.Min(1f, LockProgress + Time.deltaTime / Plugin.LockTime.Value);
                    if (Time.time >= nextBeep)
                    {
                        tone.PlayOneShot(beepClip);
                        nextBeep = Time.time + 0.25f;
                    }
                    if (LockProgress >= 1f) tone.PlayOneShot(lockClip);
                }
            }

            if (fire && Locked && Ammo > 0 && Time.time >= nextShotTime) Fire(self, cam);
        }

        // Also called while the launcher is put away, so a missile in flight keeps getting updates.
        internal void Spot(PilotDismounted self, Camera cam)
        {
            Unit t = LockTarget != null ? LockTarget : (Time.time < spotUntil ? spotTarget : null);
            if (t == null || t.disabled || Time.time < nextSpot || self.NetworkHQ == null) return;
            nextSpot = Time.time + 0.5f;
            if (!HasLineOfSight(cam.transform.position, t, self)) return;
            if (Combat.IsServer) self.NetworkHQ.RpcUpdateTrackingInfo(t.persistentID);
            else self.NetworkHQ.CmdUpdateTrackingInfo(t.persistentID); // client: the game's own spotting RPC
        }

        private void Fire(PilotDismounted self, Camera cam)
        {
            Unit target = LockTarget;
            bool air = target is Aircraft;
            GameObject prefab = air ? aaMissile : atMissile;
            if (prefab == null)
            {
                Plugin.Log.LogWarning($"No {(air ? "anti-air" : "anti-surface")} missile available.");
                return;
            }
            spotTarget = target;
            spotUntil = Time.time + 30f;
            Transform eye = cam.transform;
            Vector3 pos = eye.position + eye.forward * 1.5f + eye.right * 0.2f;
            Vector3 dir = (eye.forward + Vector3.up * (air ? 0.08f : 0.03f)).normalized;
            if (Combat.IsServer)
            {
                SpawnFor(self, target, pos, dir);
            }
            else if (NetSync.ServerHasMod)
            {
                self.NetworkHQ?.CmdUpdateTrackingInfo(target.persistentID);
                NetSync.SendLaunch(self, target, pos, dir);
                Plugin.Log.LogInfo($"Launch request sent for {target.unitName}");
            }
            else
            {
                Plugin.Log.LogWarning("Launcher: this server doesn't run NO Infantry.");
                return;
            }
            Ammo--;
            nextShotTime = Time.time + Plugin.LauncherReload.Value;
            ClearLock();
        }

        // Server/host: spawn the game's own missile for `self` at `target`.
        internal static Missile SpawnFor(PilotDismounted self, Unit target, Vector3 pos, Vector3 dir)
        {
            FindMissiles();
            bool air = target is Aircraft;
            GameObject prefab = air ? aaMissile : atMissile;
            if (prefab == null) return null;
            self.NetworkHQ?.RpcUpdateTrackingInfo(target.persistentID);
            Vector3 vel = self.rb.velocity + dir * Plugin.LauncherEjectSpeed.Value;
            Missile m = NetworkSceneSingleton<Spawner>.i.SpawnMissile(prefab, pos, Quaternion.LookRotation(dir), vel, target, self);
            LastLaunched = m;
            if (m != null)
            {
                // A physical bump against the launching soldier counts as impact damage and detonates the missile
                // on the spot (seen on a multiplayer server, where the soldier copy can sit near the tube).
                foreach (Collider mc in m.GetComponentsInChildren<Collider>(true))
                {
                    foreach (Collider sc in self.GetComponentsInChildren<Collider>(true)) Physics.IgnoreCollision(mc, sc);
                }
                Launched[m] = target;
                // Surface targets: AT missiles fired this way can pass a small vehicle without registering
                // a hit, so arm the game's own proximity fuse on the target (detonates at closest approach).
                if (!air) m.SetProxyFuse(target.transform, target.rb);
            }
            Plugin.Log.LogInfo($"Launched {m?.definition?.unitName} at {target.unitName} ({Vector3.Distance(pos, target.transform.position):F0} m) for {self.unitName}; launch globalY {pos.GlobalY():F1}, soldier globalY {self.transform.position.GlobalY():F1}, seaY {Datum.LocalSeaY:F1}");
            return m;
        }

        private static Unit FindTarget(PilotDismounted self, Camera cam, float cone, bool air, bool surface)
        {
            Transform eye = cam.transform;
            Unit best = null;
            float bestAngle = cone;
            float maxRange = Plugin.LauncherRange.Value;
            foreach (Unit u in UnitRegistry.allUnits)
            {
                if (u == null || u.disabled || u == self || u.NetworkHQ == null || u.NetworkHQ == self.NetworkHQ) continue;
                if (!(u is Aircraft ? air : (u is GroundVehicle || u is Ship) && surface)) continue;
                Vector3 to = u.transform.position - eye.position;
                float dist = to.magnitude;
                if (dist > maxRange || dist < 30f) continue;
                float angle = Vector3.Angle(eye.forward, to);
                if (angle > bestAngle) continue;
                if (!HasLineOfSight(eye.position, u, self)) continue;
                bestAngle = angle;
                best = u;
            }
            return best;
        }

        // Anything solid between us and the target (terrain, walls, hangars, other vehicles) blocks the lock;
        // checking only the terrain layer let the AT missile lock through a blast wall and hit it.
        private static bool HasLineOfSight(Vector3 from, Unit target, PilotDismounted self)
        {
            Vector3 to = target.transform.position - from;
            foreach (RaycastHit h in Physics.RaycastAll(from, to.normalized, to.magnitude, ~(int)PhysicsLayers.ExclusionZonesMask, QueryTriggerInteraction.Ignore))
            {
                Transform t = h.collider.transform;
                if (t.IsChildOf(target.transform) || t.IsChildOf(self.transform)) continue;
                if (h.distance > to.magnitude - target.maxRadius) continue;
                return false;
            }
            return true;
        }

        internal void DrawHud(Camera cam)
        {
            if (LockTarget == null) return;
            Vector3 sp = cam.WorldToScreenPoint(LockTarget.transform.position);
            if (sp.z <= 0f) return;
            float y = Screen.height - sp.y;
            float size = Mathf.Lerp(60f, 26f, LockProgress);
            Color c = Locked ? new Color(1f, 0.25f, 0.2f) : new Color(1f, 0.85f, 0.2f);
            GUI.color = c;
            float t = 2f;
            GUI.DrawTexture(new Rect(sp.x - size, y - size, size * 2f, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(sp.x - size, y + size - t, size * 2f, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(sp.x - size, y - size, t, size * 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(sp.x + size - t, y - size, t, size * 2f), Texture2D.whiteTexture);
            float dist = Vector3.Distance(cam.transform.position, LockTarget.transform.position);
            GUI.Label(new Rect(sp.x + size + 6f, y - 10f, 300f, 22f), $"{LockTarget.unitName}  {dist / 1000f:F1} km  {(Locked ? "LOCKED" : "")}");
        }

        private static AudioClip MakeTone(float freq, float seconds)
        {
            int rate = 44100;
            int n = Mathf.CeilToInt(rate * seconds);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float env = Mathf.Min(1f, Mathf.Min(i, n - i) / (rate * 0.005f));
                data[i] = Mathf.Sin(2f * Mathf.PI * freq * i / rate) * 0.5f * env;
            }
            AudioClip clip = AudioClip.Create($"tone{freq}", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}

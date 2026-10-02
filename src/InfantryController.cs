using System.Collections.Generic;
using NuclearOption.Networking;
using UnityEngine;
using UnityEngine.Rendering;

namespace NOInfantry
{
    // Drives the local player's ejected pilot as a first-person soldier (host / single-player only).
    internal class InfantryController : MonoBehaviour
    {
        internal static InfantryController Instance;
        internal static PilotDismounted Controlled;

        internal bool Aiming;

        private const float HoverHeight = 1.3f; // matches PilotDismounted's ground spring

        private readonly InfantryCameraState camState = new InfantryCameraState();
        private readonly List<Renderer> hiddenRenderers = new List<Renderer>();

        private PilotDismounted cachedPilot;
        private PilotDismounted userExitedPilot;

        private Vector2 moveInput;
        private bool sprint;
        private bool jumpQueued;
        private bool grounded;

        private float nextShotTime;
        private int ammo;
        private float reloadDoneTime;
        private float hitMarkerUntil;
        private int shotsFired;
        private int hits;
        private bool loggedMouseFallback;
        private int grenades;
        private AudioSource audioSource;
        private ViewModel viewModel;
        private Vector2 lastLook;
        private float lastHp = 100f;
        private float hurtUntil;

        private static readonly HarmonyLib.AccessTools.FieldRef<PilotDismounted, float> hitPointsRef =
            HarmonyLib.AccessTools.FieldRefAccess<PilotDismounted, float>("hitPoints");

        private void Awake()
        {
            Instance = this;
        }

        private void Update()
        {
            TrackLocalPilot();
            PilotDismounted pilot = cachedPilot;
            if (Controlled == null && Plugin.DismountKey.Value.IsDown()) TryDismount();

            // (object) cast: a destroyed pilot compares == null under Unity rules but the reference is still set.
            if ((object)Controlled != null && (Controlled == null || Controlled.disabled))
            {
                Exit("pilot disabled or destroyed");
                return;
            }

            if ((object)VehicleControl.Driven != null && (VehicleControl.Driven == null || VehicleControl.Driven.disabled))
            {
                LeaveVehicle();
            }

            if (pilot != null && Plugin.ToggleKey.Value.IsDown())
            {
                if (Controlled == pilot)
                {
                    userExitedPilot = pilot;
                    Exit("toggle key");
                }
                else if (CanControl(pilot))
                {
                    Enter(pilot);
                }
            }

            if (Controlled == null && pilot != null && Plugin.AutoEnter.Value && pilot != userExitedPilot
                && pilot.animationState == PilotDismounted.PilotState.landing && CanControl(pilot))
            {
                Enter(pilot);
            }

            if (Controlled != null)
            {
                KeepCamera();
                ReadInput();
                if (viewModel != null && VehicleControl.Driven == null)
                {
                    float speed = new Vector2(Controlled.rb.velocity.x, Controlled.rb.velocity.z).magnitude;
                    viewModel.Tick(Aiming, sprint, reloadDoneTime > 0f, grounded ? speed : 0f, lastLook);
                }
            }
        }

        // Climb out of a stopped aircraft: the game's own ejection does a ground bail-out when speed < 10 m/s.
        private static void TryDismount()
        {
            if (!GameManager.GetLocalAircraft(out Aircraft aircraft) || aircraft.disabled || aircraft.HasEjected()) return;
            if (aircraft.speed > 10f || aircraft.radarAlt > aircraft.definition.spawnOffset.y + 3f)
            {
                Plugin.Log.LogInfo($"Dismount refused: speed {aircraft.speed:F1} m/s, radar alt {aircraft.radarAlt:F1} m");
                return;
            }
            Plugin.Log.LogInfo($"Dismounting from {aircraft.unitName}");
            aircraft.StartEjectionSequence();
        }

        private void TrackLocalPilot()
        {
            // Player.PilotDismounted is cleared as soon as the pilot lands, so remember it while descending.
            if (GameManager.GetLocalPilotDismounted(out PilotDismounted p))
            {
                cachedPilot = p;
                return;
            }
            if (cachedPilot != null && !cachedPilot.disabled) return;

            cachedPilot = null;
            CameraStateManager cam = SceneSingleton<CameraStateManager>.i;
            if (cam != null && cam.followingUnit is PilotDismounted followed && !followed.disabled
                && followed.Networkplayer != null && GameManager.IsLocalPlayer(followed.Networkplayer))
            {
                cachedPilot = followed;
            }
        }

        private static bool CanControl(PilotDismounted pilot)
        {
            if (pilot == null || pilot.disabled) return false;
            if (!pilot.LocalSim)
            {
                Plugin.Log.LogWarning("Pilot is simulated by the server; infantry mode is host/single-player only for now.");
                return false;
            }
            return SceneSingleton<CameraStateManager>.i != null;
        }

        private void Enter(PilotDismounted pilot)
        {
            Controlled = pilot;
            CameraStateManager cam = SceneSingleton<CameraStateManager>.i;

            if (pilot.NetworkisKinematic) pilot.NetworkisKinematic = false;
            pilot.rb.isKinematic = false;
            pilot.rb.drag = 0f;
            pilot.rb.angularDrag = 5f;
            pilot.SetPilotState(PilotDismounted.PilotState.landing);

            camState.Pilot = pilot;
            camState.Yaw = pilot.transform.eulerAngles.y;
            camState.Pitch = 0f;
            cam.SwitchState(camState);

            // Keep the body's shadow but don't render it into our own eyes.
            hiddenRenderers.Clear();
            foreach (Renderer r in pilot.GetComponentsInChildren<Renderer>())
            {
                if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                hiddenRenderers.Add(r);
            }

            Shader fallback = null;
            foreach (Renderer r in hiddenRenderers)
            {
                if (r.sharedMaterial != null) { fallback = r.sharedMaterial.shader; break; }
            }
            if (viewModel != null) Destroy(viewModel.gameObject);
            viewModel = ViewModel.Create(cam.mainCamera, fallback);

            ammo = Plugin.MagazineSize.Value;
            reloadDoneTime = 0f;
            grenades = Plugin.GrenadeCount.Value;
            lastHp = hitPointsRef(pilot);
            BorrowedAssets.Find();
            SetupAudio();
            Plugin.Log.LogInfo($"Infantry mode ON: {pilot.unitName} at {pilot.transform.position}");
        }

        private void SetupAudio()
        {
            if (audioSource != null) return;
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0f;
            if (SoundManager.i != null) audioSource.outputAudioMixerGroup = SoundManager.i.EffectsMixer;
        }

        private void Exit(string reason)
        {
            if ((object)VehicleControl.Driven != null) LeaveVehicle();
            PilotDismounted pilot = Controlled;
            Controlled = null;
            camState.Pilot = null;
            Aiming = false;

            foreach (Renderer r in hiddenRenderers)
            {
                if (r != null) r.shadowCastingMode = ShadowCastingMode.On;
            }
            hiddenRenderers.Clear();
            if (viewModel != null) Destroy(viewModel.gameObject);
            viewModel = null;

            CameraStateManager cam = SceneSingleton<CameraStateManager>.i;
            if (cam != null && cam.currentState == camState)
            {
                cam.SetFollowingUnit(pilot != null ? pilot : null);
            }
            Plugin.Log.LogInfo($"Infantry mode OFF ({reason}). Shots {shotsFired}, hits {hits}");
        }

        private void KeepCamera()
        {
            // Other game systems may swap the camera (follow changes, camera keys); take it back.
            CameraStateManager cam = SceneSingleton<CameraStateManager>.i;
            if (cam != null && cam.currentState != camState)
            {
                if (cam.followingUnit != Controlled) cam.followingUnit = Controlled;
                cam.SwitchState(camState);
            }
        }

        private bool InputAllowed => Cursor.lockState == CursorLockMode.Locked;

        private void ReadInput()
        {
            moveInput = Vector2.zero;
            sprint = false;
            Aiming = false;
            lastLook = Vector2.zero;
            if (!InputAllowed) return;

            // Mouse look
            Aiming = Input.GetMouseButton(1);
            Vector2 look = ReadMouse() * Plugin.MouseSensitivity.Value;
            if (Aiming) look *= 0.5f;
            lastLook = look;
            camState.Yaw += look.x;
            camState.Pitch = Mathf.Clamp(camState.Pitch - look.y, -85f, 85f);

            if (Input.GetKeyDown(Plugin.UseKey.Value))
            {
                if (VehicleControl.Driven != null) { LeaveVehicle(); return; }
                GroundVehicle gv = VehicleControl.FindEnterable(Controlled);
                if (gv != null) { EnterVehicle(gv); return; }
            }
            if (VehicleControl.Driven != null)
            {
                ReadVehicleInput();
                return;
            }

            // Movement
            if (Input.GetKey(KeyCode.W)) moveInput.y += 1f;
            if (Input.GetKey(KeyCode.S)) moveInput.y -= 1f;
            if (Input.GetKey(KeyCode.D)) moveInput.x += 1f;
            if (Input.GetKey(KeyCode.A)) moveInput.x -= 1f;
            moveInput = Vector2.ClampMagnitude(moveInput, 1f);
            sprint = Input.GetKey(KeyCode.LeftShift) && moveInput.y > 0f;
            if (Input.GetKeyDown(KeyCode.Space)) jumpQueued = true;

            // Rifle
            if (Input.GetKeyDown(KeyCode.R)) StartReload();
            if (reloadDoneTime > 0f && Time.time >= reloadDoneTime)
            {
                ammo = Plugin.MagazineSize.Value;
                reloadDoneTime = 0f;
            }
            if (Input.GetKeyDown(KeyCode.G) && grenades > 0) ThrowGrenade();
            if (Input.GetMouseButton(0) && Time.time >= nextShotTime && reloadDoneTime == 0f)
            {
                if (ammo > 0) Fire();
                else StartReload();
            }
        }

        private void ReadVehicleInput()
        {
            float throttle = 0f, steer = 0f;
            if (Input.GetKey(KeyCode.W)) throttle += 1f;
            if (Input.GetKey(KeyCode.S)) throttle -= 1f;
            if (Input.GetKey(KeyCode.D)) steer += 1f;
            if (Input.GetKey(KeyCode.A)) steer -= 1f;
            if (VehicleControl.Driven.speed < -0.5f) steer = -steer; // steer like a car when reversing
            VehicleControl.Throttle = throttle;
            VehicleControl.Steering = steer;
            if (Input.GetMouseButton(0)) VehicleControl.FireHeld();
        }

        private void EnterVehicle(GroundVehicle gv)
        {
            PilotDismounted pilot = Controlled;
            VehicleControl.Enter(gv);
            pilot.SetCollidable(false);
            foreach (Renderer r in hiddenRenderers) if (r != null) r.enabled = false;
            if (viewModel != null) viewModel.gameObject.SetActive(false);
        }

        private void LeaveVehicle()
        {
            GroundVehicle gv = VehicleControl.Driven;
            VehicleControl.Exit();
            VehicleControl.Throttle = 0f;
            VehicleControl.Steering = 0f;
            PilotDismounted pilot = Controlled;
            if (pilot == null) return;

            Vector3 spot = pilot.transform.position;
            if (gv != null)
            {
                spot = gv.transform.position + gv.transform.right * (gv.maxRadius + 1.5f) + Vector3.up * 3f;
                if (Physics.Raycast(spot, Vector3.down, out RaycastHit hit, 20f, ~(int)PhysicsLayers.ExclusionZonesMask))
                {
                    spot = hit.point + Vector3.up * HoverHeight;
                }
                pilot.rb.velocity = gv.rb.velocity;
            }
            pilot.transform.position = spot;
            pilot.rb.position = spot;
            pilot.SetCollidable(true);
            foreach (Renderer r in hiddenRenderers) if (r != null) r.enabled = true;
            if (viewModel != null) viewModel.gameObject.SetActive(true);
        }

        private Vector2 ReadMouse()
        {
            try
            {
                return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));
            }
            catch (System.ArgumentException)
            {
                if (!loggedMouseFallback)
                {
                    Plugin.Log.LogWarning("Legacy 'Mouse X' axis missing; falling back to Rewired Pan/Tilt View.");
                    loggedMouseFallback = true;
                }
                Rewired.Player input = GameManager.playerInput;
                return input == null ? Vector2.zero : new Vector2(input.GetAxis("Pan View"), input.GetAxis("Tilt View"));
            }
        }

        private void StartReload()
        {
            if (reloadDoneTime > 0f || ammo == Plugin.MagazineSize.Value) return;
            reloadDoneTime = Time.time + Plugin.ReloadTime.Value;
        }

        private void Fire()
        {
            PilotDismounted pilot = Controlled;
            Camera eye = SceneSingleton<CameraStateManager>.i.mainCamera;
            nextShotTime = Time.time + Plugin.FireInterval.Value;
            ammo--;
            shotsFired++;

            float spread = Plugin.SpreadDegrees.Value * (Aiming ? 0.5f : 1f) * (moveInput.sqrMagnitude > 0f ? 2f : 1f);
            Vector3 dir = Quaternion.Euler(Random.Range(-spread, spread), Random.Range(-spread, spread), 0f) * eye.transform.forward;
            Vector3 origin = eye.transform.position;

            camState.Pitch -= Random.Range(0.3f, 0.6f) * (Aiming ? 0.5f : 1f); // recoil
            AudioClip[] shots = BorrowedAssets.RifleShots;
            if (audioSource != null && shots != null && shots.Length > 0)
            {
                audioSource.pitch = Random.Range(1.15f, 1.3f); // small gun sounds a bit higher
                audioSource.PlayOneShot(shots[Random.Range(0, shots.Length)], Plugin.RifleVolume.Value);
            }

            viewModel?.OnFire(Aiming);
            Vector3 end = origin + dir * Plugin.Range.Value;
            if (Raycast(origin, dir, pilot, out RaycastHit hit))
            {
                end = hit.point;
                bool water = hit.point.y < Datum.LocalSeaY || hit.collider.sharedMaterial == GameAssets.i.WaterMaterial;
                bool armor = hit.collider.sharedMaterial != GameAssets.i.terrainMaterial;
                BorrowedAssets.PlayImpact(hit.point, hit.normal, dir, armor, water);
                if (!water) ApplyHit(pilot, hit, dir);
            }
            Vector3 tracerStart = viewModel != null ? viewModel.Muzzle.position : origin + dir * 1.5f - Vector3.up * 0.1f;
            Tracer.Spawn(tracerStart, end);
        }

        private void ThrowGrenade()
        {
            PilotDismounted pilot = Controlled;
            Transform eye = SceneSingleton<CameraStateManager>.i.mainCamera.transform;
            grenades--;
            Vector3 dir = (eye.forward + Vector3.up * 0.15f).normalized;
            Grenade.Throw(pilot, eye.position + eye.forward * 0.5f, pilot.rb.velocity + dir * Plugin.GrenadeThrowSpeed.Value);
        }

        private static bool Raycast(Vector3 origin, Vector3 dir, PilotDismounted self, out RaycastHit best)
        {
            best = default;
            RaycastHit[] results = Physics.RaycastAll(origin, dir, Plugin.Range.Value, ~(int)PhysicsLayers.ExclusionZonesMask, QueryTriggerInteraction.Ignore);
            float bestDist = float.MaxValue;
            foreach (RaycastHit h in results)
            {
                if (h.collider.transform.IsChildOf(self.transform)) continue;
                if (h.distance < bestDist)
                {
                    bestDist = h.distance;
                    best = h;
                }
            }
            return bestDist < float.MaxValue;
        }

        private void ApplyHit(PilotDismounted shooter, RaycastHit hit, Vector3 dir)
        {
            IDamageable target = hit.collider.GetComponentInParent<IDamageable>();
            if (target == null) return;

            Unit unit = target.GetUnit();
            float muzzle = Plugin.MuzzleVelocity.Value;
            if (NetworkManagerNuclearOption.i != null && NetworkManagerNuclearOption.i.Server.Active)
            {
                // Same path the game uses for confirmed gun hits: penetration vs armor, damage credited to us.
                DamageEffects.ArmorPenetrate(hit.point - dir * 0.5f, dir * muzzle, muzzle, Plugin.PierceDamage.Value, 0f, shooter.persistentID);
            }
            hits++;
            hitMarkerUntil = Time.time + 0.15f;
            Plugin.Log.LogInfo($"Hit {(unit != null ? unit.unitName : hit.collider.name)} ({hit.collider.name}) at {hit.distance:F0} m, armor {target.GetArmorProperties()?.pierceArmor}");
        }

        // Called from the LocalFixedUpdate prefix in place of the game's grounded logic.
        internal void PhysicsStep(PilotDismounted pilot)
        {
            Rigidbody rb = pilot.rb;
            GroundVehicle gv = VehicleControl.Driven;
            if (gv != null)
            {
                VehicleControl.ApplyInputs();
                // Ride along inside the vehicle (collider is off while seated).
                Vector3 seat = gv.transform.position + Vector3.up * (gv.definition.height * 0.5f);
                pilot.transform.position = seat;
                rb.position = seat;
                rb.velocity = gv.rb.velocity;
                rb.angularVelocity = Vector3.zero;
                return;
            }
            Vector3 pos = pilot.transform.position;
            bool inWater = pos.GlobalY() < 0f;

            rb.drag = 0f;
            rb.angularVelocity = Vector3.zero;
            rb.MoveRotation(Quaternion.Euler(0f, camState.Yaw, 0f));

            Vector3 groundVel = Vector3.zero;
            grounded = Physics.Linecast(pos, pos - Vector3.up * (HoverHeight + 0.4f), out RaycastHit hit, ~(int)PhysicsLayers.ExclusionZonesMask)
                       && !hit.collider.transform.IsChildOf(pilot.transform);
            if (grounded && hit.collider.attachedRigidbody != null)
            {
                groundVel = hit.collider.attachedRigidbody.GetPointVelocity(hit.point);
            }

            Quaternion yaw = Quaternion.Euler(0f, camState.Yaw, 0f);
            float speed = sprint ? Plugin.SprintSpeed.Value : Plugin.WalkSpeed.Value;
            if (Aiming) speed *= 0.5f;
            Vector3 wishVel = yaw * new Vector3(moveInput.x, 0f, moveInput.y) * speed + groundVel;

            Vector3 vel = rb.velocity;
            Vector3 horiz = new Vector3(vel.x - wishVel.x, 0f, vel.z - wishVel.z);

            if (inWater)
            {
                rb.AddForce(Vector3.up * rb.mass * 25f * Mathf.Clamp01(Datum.LocalSeaY - pos.y));
                rb.AddForce(-horiz * rb.mass * 2f - Vector3.up * vel.y * rb.mass);
            }
            else if (grounded)
            {
                // Spring to hover height (same constants as the game) plus strong horizontal control.
                float relVy = vel.y - groundVel.y;
                rb.AddForce(Vector3.up * (HoverHeight - hit.distance - relVy * 0.2f) * rb.mass * 25f - Vector3.up * relVy * rb.mass * 2f);
                rb.AddForce(-horiz * rb.mass * 12f);
                if (jumpQueued)
                {
                    rb.velocity = new Vector3(vel.x, groundVel.y + Plugin.JumpSpeed.Value, vel.z);
                }
                if (pilot.animationState != PilotDismounted.PilotState.landing)
                {
                    pilot.SetPilotState(PilotDismounted.PilotState.landing);
                }
            }
            else
            {
                rb.AddForce(-horiz * rb.mass * 1.5f); // a little air control
            }
            jumpQueued = false;
        }

        private GroundVehicle nearbyVehicle;

        private void DrawVehicleHud(float cx, float cy)
        {
            GUI.color = new Color(1f, 1f, 1f, 0.85f);
            GUI.DrawTexture(new Rect(cx - 2f, cy - 2f, 4f, 4f), Texture2D.whiteTexture);

            // Where the first gun actually points (turrets traverse slower than the camera).
            Camera camera = SceneSingleton<CameraStateManager>.i.mainCamera;
            foreach (Turret t in VehicleControl.Turrets)
            {
                WeaponStation ws = t.GetWeaponStation();
                if (ws == null || ws.Weapons.Count == 0 || ws.Weapons[0] == null) continue;
                Transform muzzle = ws.Weapons[0].transform;
                float range = Vector3.Distance(muzzle.position, VehicleControl.AimPoint);
                Vector3 sp = camera.WorldToScreenPoint(muzzle.position + muzzle.forward * range);
                if (sp.z > 0f)
                {
                    float y = Screen.height - sp.y;
                    GUI.color = new Color(0.4f, 1f, 0.4f, 0.9f);
                    GUI.DrawTexture(new Rect(sp.x - 9f, y - 1f, 6f, 2f), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(sp.x + 3f, y - 1f, 6f, 2f), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(sp.x - 1f, y + 3f, 2f, 6f), Texture2D.whiteTexture);
                }
                break;
            }

            GroundVehicle gv = VehicleControl.Driven;
            GUI.color = Color.white;
            GUI.Label(new Rect(20f, Screen.height - 84f, 700f, 24f), $"{gv.unitName}   {Mathf.Abs(gv.speed) * 3.6f:F0} km/h   {VehicleControl.WeaponSummary()}");
            GUI.Label(new Rect(20f, Screen.height - 60f, 500f, 24f), $"WASD drive   LMB fire   RMB zoom   [{Plugin.UseKey.Value}] get out");
        }

        private void OnGUI()
        {
            if (Controlled == null) return;

            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            Color prev = GUI.color;

            // Health + hurt flash
            float hp = hitPointsRef(Controlled);
            if (Event.current.type == EventType.Repaint)
            {
                if (hp < lastHp - 0.01f)
                {
                    hurtUntil = Time.time + 0.35f;
                    Plugin.Log.LogInfo($"Took {lastHp - hp:F1} damage, HP {hp:F1}");
                }
                lastHp = hp;
            }
            if (Time.time < hurtUntil)
            {
                GUI.color = new Color(0.8f, 0f, 0f, 0.35f * (hurtUntil - Time.time) / 0.35f);
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            }
            GUI.color = hp < 35f ? new Color(1f, 0.35f, 0.3f) : Color.white;
            GUI.Label(new Rect(20f, Screen.height - 108f, 200f, 24f), $"HEALTH  {Mathf.Max(hp, 0f):F0}");

            if (VehicleControl.Driven != null)
            {
                DrawVehicleHud(cx, cy);
                GUI.color = prev;
                return;
            }
            if (Event.current.type == EventType.Repaint && Time.frameCount % 15 == 0)
            {
                nearbyVehicle = VehicleControl.FindEnterable(Controlled);
            }
            if (nearbyVehicle != null)
            {
                GUI.Label(new Rect(cx - 150f, cy + 60f, 300f, 24f), $"[{Plugin.UseKey.Value}] enter {nearbyVehicle.unitName}");
            }

            GUI.color = Time.time < hitMarkerUntil ? Color.red : new Color(1f, 1f, 1f, Aiming ? 0f : 0.85f);
            float gap = 6f;
            GUI.DrawTexture(new Rect(cx - gap - 8f, cy - 1f, 8f, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx + gap, cy - 1f, 8f, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 1f, cy - gap - 8f, 2f, 8f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 1f, cy + gap, 2f, 8f), Texture2D.whiteTexture);

            GUI.color = Color.white;
            string ammoText = reloadDoneTime > 0f ? "RELOADING" : $"{ammo} / {Plugin.MagazineSize.Value}";
            GUI.Label(new Rect(Screen.width - 320f, Screen.height - 60f, 300f, 24f), $"RIFLE  {ammoText}    GRENADES  {grenades}");
            GUI.Label(new Rect(20f, Screen.height - 60f, 400f, 24f), $"INFANTRY  [{Plugin.ToggleKey.Value}] exit   R reload   RMB aim   G grenade");
            GUI.color = prev;
        }
    }
}

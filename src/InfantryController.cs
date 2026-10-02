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

        private void Awake()
        {
            Instance = this;
        }

        private void Update()
        {
            TrackLocalPilot();
            PilotDismounted pilot = cachedPilot;

            // (object) cast: a destroyed pilot compares == null under Unity rules but the reference is still set.
            if ((object)Controlled != null && (Controlled == null || Controlled.disabled))
            {
                Exit("pilot disabled or destroyed");
                return;
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
            }
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

            ammo = Plugin.MagazineSize.Value;
            reloadDoneTime = 0f;
            Plugin.Log.LogInfo($"Infantry mode ON: {pilot.unitName} at {pilot.transform.position}");
        }

        private void Exit(string reason)
        {
            PilotDismounted pilot = Controlled;
            Controlled = null;
            camState.Pilot = null;
            Aiming = false;

            foreach (Renderer r in hiddenRenderers)
            {
                if (r != null) r.shadowCastingMode = ShadowCastingMode.On;
            }
            hiddenRenderers.Clear();

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
            if (!InputAllowed) return;

            // Mouse look
            Vector2 look = ReadMouse() * Plugin.MouseSensitivity.Value;
            if (Aiming) look *= 0.5f;
            camState.Yaw += look.x;
            camState.Pitch = Mathf.Clamp(camState.Pitch - look.y, -85f, 85f);

            // Movement
            if (Input.GetKey(KeyCode.W)) moveInput.y += 1f;
            if (Input.GetKey(KeyCode.S)) moveInput.y -= 1f;
            if (Input.GetKey(KeyCode.D)) moveInput.x += 1f;
            if (Input.GetKey(KeyCode.A)) moveInput.x -= 1f;
            moveInput = Vector2.ClampMagnitude(moveInput, 1f);
            sprint = Input.GetKey(KeyCode.LeftShift) && moveInput.y > 0f;
            if (Input.GetKeyDown(KeyCode.Space)) jumpQueued = true;

            // Rifle
            Aiming = Input.GetMouseButton(1);
            if (Input.GetKeyDown(KeyCode.R)) StartReload();
            if (reloadDoneTime > 0f && Time.time >= reloadDoneTime)
            {
                ammo = Plugin.MagazineSize.Value;
                reloadDoneTime = 0f;
            }
            if (Input.GetMouseButton(0) && Time.time >= nextShotTime && reloadDoneTime == 0f)
            {
                if (ammo > 0) Fire();
                else StartReload();
            }
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

            Vector3 end = origin + dir * Plugin.Range.Value;
            if (Raycast(origin, dir, pilot, out RaycastHit hit))
            {
                end = hit.point;
                ApplyHit(pilot, hit, dir);
            }
            Tracer.Spawn(origin + dir * 1.5f - Vector3.up * 0.1f, end);
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
            float dt = Time.fixedDeltaTime;
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

        private void OnGUI()
        {
            if (Controlled == null) return;

            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            Color prev = GUI.color;

            GUI.color = Time.time < hitMarkerUntil ? Color.red : new Color(1f, 1f, 1f, 0.85f);
            float gap = Aiming ? 3f : 6f;
            GUI.DrawTexture(new Rect(cx - gap - 8f, cy - 1f, 8f, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx + gap, cy - 1f, 8f, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 1f, cy - gap - 8f, 2f, 8f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 1f, cy + gap, 2f, 8f), Texture2D.whiteTexture);

            GUI.color = Color.white;
            string ammoText = reloadDoneTime > 0f ? "RELOADING" : $"{ammo} / {Plugin.MagazineSize.Value}";
            GUI.Label(new Rect(Screen.width - 220f, Screen.height - 60f, 200f, 24f), $"RIFLE  {ammoText}");
            GUI.Label(new Rect(20f, Screen.height - 60f, 400f, 24f), $"INFANTRY  [{Plugin.ToggleKey.Value}] exit   R reload   RMB aim");
            GUI.color = prev;
        }
    }
}

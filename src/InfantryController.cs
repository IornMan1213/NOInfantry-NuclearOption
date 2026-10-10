using System.Collections.Generic;
using System.Linq;
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
        private bool clientSimulated;

        private enum Weapon { Rifle, Launcher }

        internal enum Stance { Stand, Crouch, Prone }
        internal Stance CurrentStance { get; private set; }
        private float eyeOffset;
        private Footsteps footsteps;

        // Camera height above the body origin, eased between stances.
        internal float EyeHeightNow => Plugin.EyeHeight.Value + eyeOffset;

        private static float StanceEyeOffset(Stance s) => s == Stance.Crouch ? -0.55f : s == Stance.Prone ? -1.05f : 0f;
        private static float StanceSpeed(Stance s) => s == Stance.Crouch ? 0.55f : s == Stance.Prone ? 0.25f : 1f;
        private static float StanceSpread(Stance s) => s == Stance.Crouch ? 0.7f : s == Stance.Prone ? 0.45f : 1f;
        private Weapon weapon = Weapon.Rifle;
        private Kit? kit;                       // null: an ejected pilot carries everything
        private enum Menu { None, Orders, MotorPool }
        private Menu menu;
        private string notice;
        private float noticeUntil;
        private Airbase zoneAirbase;            // the airbase whose capture zone we're standing in
        internal Kit? CurrentKit => kit;
        private int bandages;
        private float interactHeld;             // seconds the interact key has been held on the current target
        private PilotDismounted interactTarget;
        private float giveUpHeld;
        internal int Bandages => bandages;
        internal bool Downed => Controlled != null && Medical.IsDowned(Controlled);
        private Launcher launcher;
        private Airbase nearbyAirbase;
        private float lastHp = 100f;
        private float hurtUntil;

        private static readonly HarmonyLib.AccessTools.FieldRef<PilotDismounted, float> hitPointsRef =
            HarmonyLib.AccessTools.FieldRefAccess<PilotDismounted, float>("hitPoints");

        private void Awake()
        {
            Instance = this;
            NetSync.RallyNotice += text => Notice(text);
        }

        private void Update()
        {
            NetSync.Poll();
            TrackLocalPilot();
            PilotDismounted pilot = cachedPilot;
            if (Controlled == null && InputShim.KeyDown(Plugin.DismountKey.Value.MainKey)) TryDismount();

            // Back in the air through the game's own spawn menu: hand the camera over and return the soldier.
            if (Controlled != null && AirbaseServices.HasNewAircraft())
            {
                PilotDismounted returning = Controlled;
                Exit("spawned a new aircraft", restoreCamera: false);
                AirbaseServices.ReturnPilot(returning);
                return;
            }

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

            if (pilot != null && InputShim.KeyDown(Plugin.ToggleKey.Value.MainKey))
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
                && OnTheGround(pilot) && CanControl(pilot))
            {
                Enter(pilot);
            }

            if (Controlled != null)
            {
                KeepCamera();
                ReadInput();
                eyeOffset = Mathf.MoveTowards(eyeOffset, Downed ? -0.95f : StanceEyeOffset(CurrentStance), Time.deltaTime * 2.5f);
                if (VehicleControl.Driven == null && footsteps != null)
                {
                    float hspeed = new Vector2(Controlled.rb.velocity.x, Controlled.rb.velocity.z).magnitude;
                    float stride = CurrentStance == Stance.Stand ? (sprint ? 1.7f : 1.25f) : CurrentStance == Stance.Crouch ? 0.9f : 0.7f;
                    float loud = CurrentStance == Stance.Stand ? 1f : CurrentStance == Stance.Crouch ? 0.55f : 0.35f;
                    footsteps.Tick(grounded, hspeed, stride, loud);
                }
                if (viewModel != null && VehicleControl.Driven == null && weapon == Weapon.Rifle)
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
                return;
            }

            // Last resort (cheap, twice a second): any live soldier owned by us. In multiplayer the owner can
            // reach a client after the pilot's Setup ran, in which case the game never links Player.PilotDismounted.
            if (Time.time < nextPilotScan) return;
            nextPilotScan = Time.time + 0.5f;
            foreach (Unit u in UnitRegistry.allUnits)
            {
                if (u is PilotDismounted p2 && !p2.disabled && p2.Networkplayer != null && GameManager.IsLocalPlayer(p2.Networkplayer))
                {
                    cachedPilot = p2;
                    return;
                }
            }
        }

        private static float nextServerWarning;

        // Landed, or (as seen by a multiplayer client, where the state can lag) out of the seat and at rest.
        private static bool OnTheGround(PilotDismounted p)
        {
            if (p.animationState == PilotDismounted.PilotState.landing) return true;
            float settle = GroundWar.RecentlyDeployed ? 0.7f : 3f; // deployed on foot: no seat or parachute to wait for
            if (p.animationState != PilotDismounted.PilotState.detaching || p.timeSinceSpawn < settle) return false;
            return p.rb != null && p.rb.velocity.sqrMagnitude < 4f;
        }
        private float nextPilotScan;

        private static bool CanControl(PilotDismounted pilot)
        {
            if (pilot == null || pilot.disabled || AirbaseServices.HasNewAircraft()) return false;
            if (!pilot.LocalSim && !NetSync.ServerHasMod)
            {
                if (Time.time > nextServerWarning)
                {
                    nextServerWarning = Time.time + 30f;
                    Plugin.Log.LogWarning("This server does not run NO Infantry, so infantry mode is unavailable here.");
                }
                return false;
            }
            return SceneSingleton<CameraStateManager>.i != null;
        }

        private void Enter(PilotDismounted pilot)
        {
            Controlled = pilot;
            CameraStateManager cam = SceneSingleton<CameraStateManager>.i;
            // Multiplayer client: simulate our own soldier here (the game's pilot network transform stops
            // overwriting a LocalSim pilot) and stream its state to the server.
            clientSimulated = !pilot.LocalSim;
            if (clientSimulated) pilot.SetLocalSim(true);

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

            kit = GroundWar.RecentlyDeployed ? GroundWar.LocalKit : null;
            GroundWar.LocalDeployAt = -100f;
            menu = Menu.None;
            if (launcher == null) launcher = new Launcher(gameObject);
            launcher.Ammo = kit.HasValue ? GroundWar.KitRockets(kit.Value) : Plugin.LauncherAmmo.Value;
            launcher.AllowAir = !kit.HasValue || GroundWar.KitAllowsAir(kit.Value);
            launcher.AllowSurface = !kit.HasValue || GroundWar.KitAllowsSurface(kit.Value);
            launcher.CreateTube(cam.mainCamera.transform, Shader.Find("Universal Render Pipeline/Lit") ?? fallback);
            Launcher.FindMissiles();
            SelectWeapon(Weapon.Rifle);

            ammo = Plugin.MagazineSize.Value;
            reloadDoneTime = 0f;
            grenades = kit.HasValue ? GroundWar.KitGrenades(kit.Value) : Plugin.GrenadeCount.Value;
            bandages = kit == Kit.Medic ? Plugin.Bandages.Value : 0;
            CurrentStance = Stance.Stand;
            eyeOffset = 0f;
            if (footsteps == null) footsteps = new Footsteps(gameObject);
            lastHp = hitPointsRef(pilot);
            BorrowedAssets.Find();
            SetupAudio();
            Plugin.Log.LogInfo($"Infantry mode ON: {pilot.unitName} at {pilot.transform.position}, kit {(kit.HasValue ? GroundWar.KitName(kit.Value) : "pilot (everything)")}");
        }

        // The game's own Player.ShowMap silently does nothing while DynamicMap.AllowedToOpen is false (the aircraft
        // selection menu toggles it), which left a dead soldier with no way back in testing. Open it ourselves.
        private System.Collections.IEnumerator MapAfterDeath()
        {
            yield return new WaitForSecondsRealtime(4f);
            if (Controlled != null || AirbaseServices.HasNewAircraft() || DynamicMap.mapMaximized || GroundWar.OpenMenu != null)
            {
                Plugin.Log.LogInfo($"Map after death skipped: soldier {Controlled != null}, new aircraft {AirbaseServices.HasNewAircraft()}, map open {DynamicMap.mapMaximized}, menu {GroundWar.OpenMenu != null}");
                yield break;
            }
            DynamicMap map = SceneSingleton<DynamicMap>.i;
            if (map == null) yield break;
            bool wasAllowed = DynamicMap.AllowedToOpen;
            DynamicMap.AllowedToOpen = true;
            map.Maximize();
            Plugin.Log.LogInfo($"Killed: map opened to redeploy (was allowed {wasAllowed}, open now {DynamicMap.mapMaximized})");
        }

        private void SetupAudio()
        {
            if (audioSource != null) return;
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0f;
            if (SoundManager.i != null) audioSource.outputAudioMixerGroup = SoundManager.i.EffectsMixer;
        }

        private void Exit(string reason, bool restoreCamera = true)
        {
            if ((object)VehicleControl.Driven != null) LeaveVehicle();
            PilotDismounted pilot = Controlled;
            Controlled = null;
            camState.Pilot = null;
            Aiming = false;
            if (clientSimulated && pilot != null) pilot.SetLocalSim(false);
            clientSimulated = false;

            foreach (Renderer r in hiddenRenderers)
            {
                if (r != null) r.shadowCastingMode = ShadowCastingMode.On;
            }
            hiddenRenderers.Clear();
            if (viewModel != null) Destroy(viewModel.gameObject);
            viewModel = null;
            launcher?.Destroy();
            weapon = Weapon.Rifle;

            CameraStateManager cam = SceneSingleton<CameraStateManager>.i;
            if (restoreCamera && cam != null && cam.currentState == camState)
            {
                cam.SetFollowingUnit(pilot != null ? pilot : null);
            }
            Plugin.Log.LogInfo($"Infantry mode OFF ({reason}). Shots {shotsFired}, hits {hits}");
            menu = Menu.None;
            // Killed: back to the map to redeploy (not when we replaced the soldier ourselves).
            bool died = pilot == null || pilot.disabled && pilot.unitState != Unit.UnitState.Returned;
            if (reason == "pilot disabled or destroyed")
                Plugin.Log.LogInfo($"Soldier lost: died {died} (state {(pilot != null ? pilot.unitState.ToString() : "destroyed")}), recently deployed {GroundWar.RecentlyDeployed}, new aircraft {AirbaseServices.HasNewAircraft()}");
            if (reason == "pilot disabled or destroyed" && died && !GroundWar.RecentlyDeployed && Plugin.GroundWarEnabled.Value && Plugin.RespawnMap.Value
                && GameManager.GetLocalPlayer<Player>(out Player localPlayer) && !AirbaseServices.HasNewAircraft())
            {
                StartCoroutine(MapAfterDeath());
            }
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

        private bool InputAllowed => InputShim.Scripted || Cursor.lockState == CursorLockMode.Locked;

        // ---- Hooks for the automated test ----
        internal int ShotsFired => shotsFired;
        internal int Hits => hits;
        internal int Grenades => grenades;
        internal Launcher ActiveLauncher => launcher;
        internal ViewModel ActiveViewModel => viewModel;

        internal void AimAt(Vector3 worldPoint)
        {
            // In a vehicle the camera orbits a pivot, so aim from the pivot (the camera moves once rotated).
            Unit v = VehicleControl.Driven;
            Vector3 from = v != null
                ? v.transform.position + Vector3.up * (v.definition.height * 0.8f + 1.5f)
                : SceneSingleton<CameraStateManager>.i.mainCamera.transform.position;
            Vector3 d = worldPoint - from;
            camState.Yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            camState.Pitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
        }

        internal float Yaw => camState.Yaw;

        private void ReadInput()
        {
            moveInput = Vector2.zero;
            sprint = false;
            Aiming = false;
            lastLook = Vector2.zero;
            if (!InputAllowed) return;

            // Mouse look
            Aiming = InputShim.Mouse(1);
            Vector2 look = ReadMouse() * Plugin.MouseSensitivity.Value;
            if (Aiming) look *= 0.5f;
            lastLook = look;
            camState.Yaw += look.x;
            camState.Pitch = Mathf.Clamp(camState.Pitch - look.y, -85f, 85f);

            if (Downed)
            {
                // Down: look around, wait for help, or hold Space to give up (respawn).
                giveUpHeld = InputShim.Key(KeyCode.Space) ? giveUpHeld + Time.deltaTime : 0f;
                if (giveUpHeld > 2f)
                {
                    giveUpHeld = -999f;
                    Medical.Request(Controlled, Controlled, Medical.KindGiveUp, false);
                }
                return;
            }
            giveUpHeld = 0f;
            ReadInteract();

            if (InputShim.KeyDown(Plugin.UseKey.Value))
            {
                if (VehicleControl.Driven != null) { LeaveVehicle(); return; }
                Unit gv = VehicleControl.FindEnterable(Controlled);
                if (gv != null) { EnterVehicle(gv); return; }
            }
            if (VehicleControl.Driven != null)
            {
                ReadVehicleInput();
                return;
            }

            // Movement
            if (InputShim.Key(KeyCode.W)) moveInput.y += 1f;
            if (InputShim.Key(KeyCode.S)) moveInput.y -= 1f;
            if (InputShim.Key(KeyCode.D)) moveInput.x += 1f;
            if (InputShim.Key(KeyCode.A)) moveInput.x -= 1f;
            moveInput = Vector2.ClampMagnitude(moveInput, 1f);
            if (InputShim.KeyDown(Plugin.CrouchKey.Value)) CurrentStance = CurrentStance == Stance.Crouch ? Stance.Stand : Stance.Crouch;
            if (InputShim.KeyDown(Plugin.ProneKey.Value)) CurrentStance = CurrentStance == Stance.Prone ? Stance.Stand : Stance.Prone;
            sprint = InputShim.Key(KeyCode.LeftShift) && moveInput.y > 0f;
            if (sprint && CurrentStance != Stance.Stand) CurrentStance = Stance.Stand; // sprinting stands you up
            if (InputShim.KeyDown(KeyCode.Space))
            {
                if (CurrentStance == Stance.Stand) jumpQueued = true;
                else CurrentStance = Stance.Stand; // jump key gets you up first
            }

            // Command menus take the number keys while open.
            if (InputShim.KeyDown(Plugin.SquadMenuKey.Value)) menu = menu == Menu.Orders ? Menu.None : Menu.Orders;
            if (InputShim.KeyDown(Plugin.MotorPoolKey.Value))
            {
                if (nearbyAirbase == null && menu != Menu.MotorPool) Notice("The motor pool is only at friendly airbases");
                else menu = menu == Menu.MotorPool ? Menu.None : Menu.MotorPool;
            }
            bool menuOpen = menu != Menu.None; // the number key that picks an entry must not also pick a weapon
            if (menuOpen)
            {
                int n = NumberKey();
                if (n > 0) MenuChoice(n);
                if (InputShim.KeyDown(KeyCode.Escape)) menu = Menu.None;
            }

            // Weapons
            if (!menuOpen && InputShim.KeyDown(KeyCode.Alpha1)) SelectWeapon(Weapon.Rifle);
            if (!menuOpen && InputShim.KeyDown(KeyCode.Alpha2)) SelectWeapon(Weapon.Launcher);
            float scroll = InputShim.Scroll;
            if (scroll != 0f) SelectWeapon(weapon == Weapon.Rifle ? Weapon.Launcher : Weapon.Rifle);
            if (InputShim.KeyDown(KeyCode.G) && grenades > 0) ThrowGrenade();

            if (InputShim.KeyDown(Plugin.RallyKey.Value) && Plugin.GroundWarEnabled.Value) GroundWar.RequestRally(Controlled);

            // AI squads
            if (InputShim.KeyDown(Plugin.FireteamKey.Value)) SquadCommands.Request(Controlled, 1);
            if (InputShim.KeyDown(Plugin.PatrolKey.Value)) SquadCommands.Request(Controlled, 2);

            // Ground -> air
            if (InputShim.KeyDown(Plugin.BoardKey.Value) && nearbyAirbase != null) AirbaseServices.OpenAircraftMenu(nearbyAirbase);

            Camera eye = SceneSingleton<CameraStateManager>.i.mainCamera;
            if (weapon == Weapon.Launcher)
            {
                launcher.Tick(Controlled, eye, seeking: Aiming, fire: InputShim.MouseDown(0));
                return;
            }
            launcher?.Spot(Controlled, eye); // keep guiding a missile in flight after switching back

            // Rifle
            if (InputShim.KeyDown(KeyCode.R)) StartReload();
            if (reloadDoneTime > 0f && Time.time >= reloadDoneTime)
            {
                ammo = Plugin.MagazineSize.Value;
                reloadDoneTime = 0f;
            }
            if (InputShim.Mouse(0) && Time.time >= nextShotTime && reloadDoneTime == 0f)
            {
                if (ammo > 0) Fire();
                else StartReload();
            }
        }

        // Interact (F): hold on a downed friend to revive; medics tap to bandage the friend in front (or themselves).
        private void ReadInteract()
        {
            if (!Plugin.ReviveEnabled.Value || VehicleControl.Driven != null) { interactHeld = 0f; return; }
            PilotDismounted me = Controlled;
            PilotDismounted downed = Medical.DownedNear(me, 3.5f).OrderBy(d => Vector3.Distance(d.transform.position, me.transform.position)).FirstOrDefault();
            bool medic = kit == Kit.Medic;
            if (downed != null && InputShim.Key(Plugin.InteractKey.Value))
            {
                if (downed != interactTarget) { interactTarget = downed; interactHeld = 0f; }
                interactHeld += Time.deltaTime;
                if (interactHeld >= Plugin.ReviveTime.Value * (medic ? 0.5f : 1f))
                {
                    Medical.Request(me, downed, Medical.KindRevive, medic);
                    Notice($"Revived {downed.unitName}");
                    interactHeld = 0f;
                    interactTarget = null;
                }
                return;
            }
            interactHeld = 0f;
            interactTarget = downed;
            if (downed == null && medic && InputShim.KeyDown(Plugin.InteractKey.Value))
            {
                if (bandages <= 0) { Notice("No bandages left"); return; }
                Transform eye = SceneSingleton<CameraStateManager>.i.mainCamera.transform;
                PilotDismounted friend = UnitRegistry.allUnits.OfType<PilotDismounted>()
                    .Where(u => u != me && !u.disabled && u.NetworkHQ == me.NetworkHQ && Vector3.Distance(u.transform.position, me.transform.position) < 3f
                                && Vector3.Angle(eye.forward, u.transform.position - eye.position) < 45f)
                    .OrderBy(u => Vector3.Distance(u.transform.position, me.transform.position)).FirstOrDefault();
                PilotDismounted target = friend ?? me;
                if (Medical.Health(target) >= 99.5f) { Notice(friend != null ? $"{friend.unitName} isn't hurt" : "You aren't hurt"); return; }
                Medical.Request(me, target, Medical.KindHeal, true);
                bandages--;
                Notice($"Bandaged {(friend != null ? friend.unitName : "yourself")} ({bandages} left)");
            }
        }

        private static int NumberKey()
        {
            for (int i = 1; i <= 9; i++) if (InputShim.KeyDown(KeyCode.Alpha0 + i) || InputShim.KeyDown(KeyCode.Keypad0 + i)) return i;
            return 0;
        }

        private void Notice(string text)
        {
            notice = text;
            noticeUntil = Time.time + 3f;
        }

        private static readonly string[] OrderNames = { "Follow me", "Hold here", "Move there", "Free roam (take the next airbase)", "Disband (they fight on alone)" };

        internal void IssueOrder(int n) // also used by the test harness
        {
            Camera eye = SceneSingleton<CameraStateManager>.i.mainCamera;
            Vector3 point = Combat.Raycast(eye.transform.position, eye.transform.forward, Controlled, out RaycastHit hit) && hit.distance < 800f
                ? hit.point
                : AiSquads.GroundPoint(eye.transform.position + Vector3.ProjectOnPlane(eye.transform.forward, Vector3.up).normalized * 150f);
            AiOrder order = n == 1 ? AiOrder.Follow : n == 2 ? AiOrder.Hold : n == 3 ? AiOrder.Move : AiOrder.Free;
            SquadCommands.Order(Controlled, order, point, disband: n == 5);
            Notice($"Fireteam: {OrderNames[n - 1]}");
        }

        private void MenuChoice(int n)
        {
            if (menu == Menu.Orders && n <= 5)
            {
                IssueOrder(n);
                menu = Menu.None;
            }
            else if (menu == Menu.MotorPool && n <= GroundWar.MotorPool.Count)
            {
                VehicleDefinition def = GroundWar.MotorPool[n - 1];
                GroundWar.RequestVehicle(Controlled, n - 1);
                Notice($"Motor pool: requested {def.unitName}");
                menu = Menu.None;
            }
        }

        private void SelectWeapon(Weapon w)
        {
            if (w == Weapon.Launcher && kit.HasValue && GroundWar.KitRockets(kit.Value) == 0)
            {
                Notice($"No launcher in the {GroundWar.KitName(kit.Value)} kit");
                w = Weapon.Rifle;
            }
            weapon = w;
            if (viewModel != null) viewModel.gameObject.SetActive(w == Weapon.Rifle && VehicleControl.Driven == null);
            launcher?.SetVisible(w == Weapon.Launcher && VehicleControl.Driven == null);
        }

        private void ReadVehicleInput()
        {
            float throttle = 0f, steer = 0f;
            if (InputShim.Key(KeyCode.W)) throttle += 1f;
            if (InputShim.Key(KeyCode.S)) throttle -= 1f;
            if (InputShim.Key(KeyCode.D)) steer += 1f;
            if (InputShim.Key(KeyCode.A)) steer -= 1f;
            if (VehicleControl.Driven.speed < -0.5f) steer = -steer; // steer like a car when reversing
            VehicleControl.Throttle = throttle;
            VehicleControl.Steering = steer;
            bool fire = InputShim.Mouse(0);
            if (fire) VehicleControl.Local.FireHeld(); // no-op on clients (the server fires)
            NetSync.SendVehicleInput(Controlled, VehicleControl.Local, fire);
        }

        private void EnterVehicle(Unit gv)
        {
            PilotDismounted pilot = Controlled;
            bool authority = Combat.IsServer;
            VehicleControl.Local.Enter(gv, pilot.transform.position, authority);
            if (!authority) NetSync.SendVehicle(pilot, gv, enter: true);
            pilot.SetCollidable(false);
            foreach (Renderer r in hiddenRenderers) if (r != null) r.enabled = false;
            if (viewModel != null) viewModel.gameObject.SetActive(false);
            launcher?.SetVisible(false);
        }

        private void LeaveVehicle()
        {
            Unit gv = VehicleControl.Driven;
            if (!VehicleControl.Local.Authority && Controlled != null) NetSync.SendVehicle(Controlled, gv, enter: false);
            VehicleControl.Local.Exit();
            VehicleControl.Throttle = 0f;
            VehicleControl.Steering = 0f;
            PilotDismounted pilot = Controlled;
            if (pilot == null) return;

            Vector3 spot = pilot.transform.position;
            if (gv is Ship)
            {
                // Back where we boarded, on deck.
                spot = gv.transform.TransformPoint(VehicleControl.BoardLocalPos);
                pilot.rb.velocity = gv.rb.velocity;
            }
            else if (gv != null)
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
            SelectWeapon(weapon);
        }

        // The server said no (someone else took it, or it is out of reach): step back out.
        internal void ServerRefusedVehicle()
        {
            if (VehicleControl.Driven != null) LeaveVehicle();
        }

        private Vector2 ReadMouse()
        {
            if (InputShim.Scripted) return Vector2.zero;
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

            float spread = Plugin.SpreadDegrees.Value * (Aiming ? 0.5f : 1f) * (moveInput.sqrMagnitude > 0f ? 2f : 1f) * StanceSpread(CurrentStance);
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
            if (Combat.Raycast(origin, dir, pilot, out RaycastHit hit))
            {
                end = hit.point;
                Combat.PlayImpact(hit, dir);
                if (!Combat.IsWater(hit)) RegisterHit(pilot, hit, dir);
            }
            // Damage is the server's job: host/single-player applies it here, clients ask the server.
            if (Combat.IsPureClient) NetSync.SendShot(pilot, origin, dir);
            Vector3 tracerStart = viewModel != null ? viewModel.Muzzle.position : origin + dir * 1.5f - Vector3.up * 0.1f;
            Tracer.Spawn(tracerStart, end);
            NetSync.BroadcastLocalShot(tracerStart, end);
        }

        private void ThrowGrenade()
        {
            PilotDismounted pilot = Controlled;
            Transform eye = SceneSingleton<CameraStateManager>.i.mainCamera.transform;
            grenades--;
            Vector3 dir = (eye.forward + Vector3.up * 0.15f).normalized;
            Vector3 origin = eye.position + eye.forward * 0.5f;
            Vector3 vel = pilot.rb.velocity + dir * Plugin.GrenadeThrowSpeed.Value;
            Grenade.Throw(pilot, origin, vel, authoritative: Combat.IsServer); // clients: visual copy, server does damage
            if (Combat.IsPureClient) NetSync.SendGrenade(pilot, origin, vel);
            else NetSync.BroadcastLocalGrenade(origin, vel);
        }

        private void RegisterHit(PilotDismounted shooter, RaycastHit hit, Vector3 dir)
        {
            IDamageable target = hit.collider.GetComponentInParent<IDamageable>();
            if (target == null) return;

            Unit unit = target.GetUnit();
            Combat.ApplyRifleDamage(shooter, hit, dir); // no-op on clients
            hits++;
            hitMarkerUntil = Time.time + 0.15f;
            Plugin.Log.LogInfo($"Hit {(unit != null ? unit.unitName : hit.collider.name)} ({hit.collider.name}) at {hit.distance:F0} m, armor {target.GetArmorProperties()?.pierceArmor}");
        }

        // Called from the LocalFixedUpdate prefix in place of the game's grounded logic.
        internal void PhysicsStep(PilotDismounted pilot)
        {
            Rigidbody rb = pilot.rb;
            Unit gv = VehicleControl.Driven;
            NetSync.SendState(pilot, camState.Yaw);
            if (gv != null)
            {
                VehicleControl.Local.ApplyInputs();
                // Ride along (collider is off while seated): ships keep us where we boarded on deck.
                Vector3 seat = gv is Ship
                    ? gv.transform.TransformPoint(VehicleControl.BoardLocalPos)
                    : gv.transform.position + Vector3.up * (gv.definition.height * 0.5f);
                pilot.transform.position = seat;
                rb.position = seat;
                rb.velocity = gv.rb.velocity;
                rb.angularVelocity = Vector3.zero;
                return;
            }
            if (Downed)
            {
                grounded = SoldierMotor.Step(pilot, camState.Yaw, Vector3.zero, jump: false, downed: true);
                jumpQueued = false;
                return;
            }
            float speed = sprint ? Plugin.SprintSpeed.Value : Plugin.WalkSpeed.Value;
            speed *= StanceSpeed(CurrentStance);
            if (Aiming) speed *= 0.5f;
            Vector3 wish = Quaternion.Euler(0f, camState.Yaw, 0f) * new Vector3(moveInput.x, 0f, moveInput.y) * speed;
            grounded = SoldierMotor.Step(pilot, camState.Yaw, wish, jumpQueued);
            jumpQueued = false;
        }

        private Unit nearbyVehicle;

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

            Unit gv = VehicleControl.Driven;
            GUI.color = Color.white;
            GUI.Label(new Rect(20f, Screen.height - 84f, 700f, 24f), $"{gv.unitName}   {Mathf.Abs(gv.speed) * 3.6f:F0} km/h   {VehicleControl.WeaponSummary()}");
            GUI.Label(new Rect(20f, Screen.height - 60f, 500f, 24f), $"WASD drive   LMB fire   RMB zoom   [{Plugin.UseKey.Value}] get out");
        }

        private void DrawMedical(float cx, float cy)
        {
            if (!Plugin.ReviveEnabled.Value) return;
            Camera cam = SceneSingleton<CameraStateManager>.i.mainCamera;
            if (Downed)
            {
                GUI.color = new Color(0.5f, 0f, 0f, 0.45f);
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = Color.white;
                float left = Mathf.Max(0f, Medical.BleedLeft(Controlled));
                GUI.Label(new Rect(cx - 200f, cy - 40f, 400f, 24f), $"<b>DOWN</b>  -  bleeding out in {left:F0} s");
                GUI.Label(new Rect(cx - 200f, cy - 16f, 400f, 24f), "Wait for a teammate or medic to revive you");
                GUI.Label(new Rect(cx - 200f, cy + 8f, 400f, 24f), "Hold [Space] to give up and redeploy");
                return;
            }
            // Downed friendlies: a red cross over each one within 200 m.
            foreach (Unit u in UnitRegistry.allUnits)
            {
                if (!(u is PilotDismounted d) || d == Controlled || d.disabled || d.NetworkHQ != Controlled.NetworkHQ || !Medical.IsDowned(d)) continue;
                float dist = Vector3.Distance(d.transform.position, Controlled.transform.position);
                if (dist > 200f) continue;
                Vector3 sp = cam.WorldToScreenPoint(d.transform.position + Vector3.up * 0.8f);
                if (sp.z <= 0f) continue;
                float y = Screen.height - sp.y;
                GUI.color = new Color(1f, 0.15f, 0.15f, 0.9f);
                GUI.DrawTexture(new Rect(sp.x - 2f, y - 8f, 4f, 16f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(sp.x - 8f, y - 2f, 16f, 4f), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(sp.x + 10f, y - 10f, 200f, 20f), $"{dist:F0} m");
            }
            if (interactTarget != null && !interactTarget.disabled)
            {
                float need = Plugin.ReviveTime.Value * (kit == Kit.Medic ? 0.5f : 1f);
                GUI.Label(new Rect(cx - 150f, cy + 36f, 400f, 24f), $"Hold [{Plugin.InteractKey.Value}] revive {interactTarget.unitName}");
                if (interactHeld > 0f)
                {
                    GUI.color = new Color(0f, 0f, 0f, 0.5f);
                    GUI.DrawTexture(new Rect(cx - 100f, cy + 60f, 200f, 6f), Texture2D.whiteTexture);
                    GUI.color = new Color(0.3f, 1f, 0.3f);
                    GUI.DrawTexture(new Rect(cx - 100f, cy + 60f, 200f * Mathf.Clamp01(interactHeld / need), 6f), Texture2D.whiteTexture);
                    GUI.color = Color.white;
                }
            }
        }

        private void DrawRally()
        {
            if (!GroundWar.LocalRally.HasValue) return;
            Camera cam = SceneSingleton<CameraStateManager>.i.mainCamera;
            Vector3 at = GroundWar.LocalRally.Value.ToLocalPosition();
            Vector3 sp = cam.WorldToScreenPoint(at + Vector3.up * 2f);
            if (sp.z <= 0f) return;
            float y = Screen.height - sp.y;
            GUI.color = new Color(0.3f, 0.8f, 1f, 0.9f);
            GUI.DrawTexture(new Rect(sp.x - 5f, y - 5f, 10f, 10f), Texture2D.whiteTexture);
            GUI.Label(new Rect(sp.x + 8f, y - 10f, 200f, 20f), $"RALLY {Vector3.Distance(at, Controlled.transform.position):F0} m");
            GUI.color = Color.white;
        }

        // Capture status while inside an airbase's capture radius.
        private void DrawZone(float cx)
        {
            Airbase a = zoneAirbase;
            if (a == null || a.capture == null) return;
            FactionHQ owner = a.CurrentHQ;
            FactionHQ capturing = a.capture.capturingHQ;
            string who = owner != null ? owner.faction.factionName : capturing != null ? $"{capturing.faction.factionName} capturing" : "neutral";
            float bal = Mathf.Clamp01(a.capture.controlBalance);
            Color c = owner != null ? owner.faction.color : capturing != null ? capturing.faction.color : Color.gray;
            var bar = new Rect(cx - 120f, 40f, 240f, 8f);
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            GUI.color = c;
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * bal, bar.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(cx - 160f, 14f, 320f, 24f), $"{GroundWar.AirbaseName(a)}  -  {who}  {bal * 100f:F0}%");
        }

        private void DrawMenu(float cy)
        {
            if (Time.time < noticeUntil) GUI.Label(new Rect(20f, cy - 140f, 600f, 24f), notice);
            if (menu == Menu.None) return;
            var lines = new List<string>();
            if (menu == Menu.Orders)
            {
                lines.Add($"FIRETEAM ORDERS   [{Plugin.FireteamKey.Value}] call riflemen");
                for (int i = 0; i < OrderNames.Length; i++) lines.Add($"[{i + 1}] {OrderNames[i]}");
            }
            else
            {
                float funds = GameManager.GetLocalPlayer<Player>(out Player pl) ? pl.Allocation : 0f;
                lines.Add($"MOTOR POOL   funds {funds:F1}");
                for (int i = 0; i < GroundWar.MotorPool.Count; i++)
                {
                    VehicleDefinition d = GroundWar.MotorPool[i];
                    lines.Add($"[{i + 1}] {d.unitName}   {GroundWar.Cost(d):F1}");
                }
            }
            lines.Add("[Esc] close");
            float h = lines.Count * 22f + 12f;
            var box = new Rect(20f, cy - 100f, 380f, h);
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = Color.white;
            for (int i = 0; i < lines.Count; i++) GUI.Label(new Rect(box.x + 10f, box.y + 6f + i * 22f, 370f, 22f), lines[i]);
        }

        private void OnGUI()
        {
            // The game's aircraft selection menu (spawn, or B at an airbase): show the deploy panel, not the HUD.
            if (Controlled == null || GroundWar.OpenMenu != null)
            {
                GroundWar.DrawDeployPanel();
                return;
            }

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

            DrawMedical(cx, cy);
            if (Downed)
            {
                GUI.color = prev;
                return;
            }
            if (VehicleControl.Driven != null)
            {
                DrawVehicleHud(cx, cy);
                GUI.color = prev;
                return;
            }
            if (Event.current.type == EventType.Repaint && Time.frameCount % 15 == 0)
            {
                nearbyVehicle = VehicleControl.FindEnterable(Controlled);
                nearbyAirbase = AirbaseServices.NearFriendlyAirbase(Controlled);
                zoneAirbase = FactionRegistry.airbaseLookup.Values.FirstOrDefault(a => a != null && a.center != null && !a.disabled
                    && Vector3.Distance(a.center.position, Controlled.transform.position) < a.GetRadius());
            }
            DrawZone(cx);
            DrawMenu(cy);
            DrawRally();
            float promptY = cy + 60f;
            if (nearbyVehicle != null)
            {
                string verb = nearbyVehicle is Ship ? "take the helm of" : "enter";
                GUI.Label(new Rect(cx - 150f, promptY, 400f, 24f), $"[{Plugin.UseKey.Value}] {verb} {nearbyVehicle.unitName}");
                promptY += 22f;
            }
            if (nearbyAirbase != null)
            {
                GUI.Label(new Rect(cx - 150f, promptY, 400f, 24f), $"[{Plugin.BoardKey.Value}] request an aircraft   [{Plugin.MotorPoolKey.Value}] motor pool");
            }

            Camera eye = SceneSingleton<CameraStateManager>.i.mainCamera;
            if (weapon == Weapon.Launcher)
            {
                // Launcher reticle: a circle-ish box showing the lock cone.
                GUI.color = new Color(1f, 1f, 1f, 0.6f);
                float r = Mathf.Tan(Plugin.LockConeDegrees.Value * Mathf.Deg2Rad) / Mathf.Tan(eye.fieldOfView * 0.5f * Mathf.Deg2Rad) * Screen.height * 0.5f;
                GUI.DrawTexture(new Rect(cx - r, cy - 1f, 10f, 2f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx + r - 10f, cy - 1f, 10f, 2f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - 1f, cy - r, 2f, 10f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - 1f, cy + r - 10f, 2f, 10f), Texture2D.whiteTexture);
                launcher.DrawHud(eye);
            }
            else
            {
                GUI.color = Time.time < hitMarkerUntil ? Color.red : new Color(1f, 1f, 1f, Aiming ? 0f : 0.85f);
                float gap = 6f;
                GUI.DrawTexture(new Rect(cx - gap - 8f, cy - 1f, 8f, 2f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx + gap, cy - 1f, 8f, 2f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - 1f, cy - gap - 8f, 2f, 8f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - 1f, cy + gap, 2f, 8f), Texture2D.whiteTexture);
            }

            GUI.color = Color.white;
            string weaponText = weapon == Weapon.Launcher
                ? launcher.Summary
                : $"RIFLE  {(reloadDoneTime > 0f ? "RELOADING" : $"{ammo} / {Plugin.MagazineSize.Value}")}";
            GUI.Label(new Rect(Screen.width - 520f, Screen.height - 84f, 500f, 24f), weaponText);
            GUI.Label(new Rect(Screen.width - 520f, Screen.height - 60f, 500f, 24f), $"GRENADES  {grenades}    [1] rifle  [2] launcher");
            string help = weapon == Weapon.Launcher ? "RMB lock   LMB fire" : "RMB aim   R reload";
            int team = AiSquads.AliveFollowing(Controlled);
            if (team > 0) GUI.Label(new Rect(20f, Screen.height - 156f, 400f, 24f), $"FIRETEAM  {team}   [{Plugin.SquadMenuKey.Value}] orders");
            if (kit.HasValue) GUI.Label(new Rect(20f, Screen.height - 180f, 400f, 24f), GroundWar.KitName(kit.Value).ToUpperInvariant()
                + (kit == Kit.Medic ? $"   BANDAGES {bandages}  [{Plugin.InteractKey.Value}] heal" : ""));
            if (CurrentStance != Stance.Stand)
            {
                GUI.Label(new Rect(20f, Screen.height - 132f, 200f, 24f), CurrentStance == Stance.Crouch ? "CROUCHED" : "PRONE");
            }
            GUI.Label(new Rect(20f, Screen.height - 60f, 700f, 24f), $"INFANTRY  {help}   G grenade   [{Plugin.CrouchKey.Value}] crouch  [{Plugin.ProneKey.Value}] prone   [{Plugin.FireteamKey.Value}] fireteam   [{Plugin.RallyKey.Value}] rally   [{Plugin.ToggleKey.Value}] exit");
            GUI.color = prev;
        }
    }
}

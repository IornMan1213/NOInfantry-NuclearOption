using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NOInfantry
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "jayea.noinfantry";
        public const string Name = "NO Infantry";
        public const string Version = "0.8.0";

        internal static ManualLogSource Log;
        internal static string PluginDir;

        // Viewmodel
        internal static ConfigEntry<string> RifleModel;
        internal static ConfigEntry<float> RifleLength;
        internal static ConfigEntry<float> SightRadius;

        // Controls
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;
        internal static ConfigEntry<bool> AutoEnter;
        internal static ConfigEntry<KeyboardShortcut> DismountKey;
        internal static ConfigEntry<KeyCode> UseKey;
        internal static ConfigEntry<float> EnterDistance;
        internal static ConfigEntry<float> VehicleCamDistance;
        internal static ConfigEntry<bool> AllowVehicleMissiles;
        internal static ConfigEntry<float> TurretFireTolerance;
        internal static ConfigEntry<bool> AiTargetsInfantry;
        internal static ConfigEntry<float> AiEngageRange;
        internal static ConfigEntry<float> AiInfantryPriority;
        internal static ConfigEntry<KeyCode> BoardKey;
        internal static ConfigEntry<KeyCode> CrouchKey;
        internal static ConfigEntry<KeyCode> ProneKey;
        internal static ConfigEntry<float> FootstepVolume;

        // AI squads
        internal static ConfigEntry<KeyCode> FireteamKey;
        internal static ConfigEntry<KeyCode> PatrolKey;
        internal static ConfigEntry<int> SquadSize;
        internal static ConfigEntry<int> PatrolSize;
        internal static ConfigEntry<float> AiSoldierRange;
        internal static ConfigEntry<float> AiSpreadDegrees;
        internal static ConfigEntry<bool> EnemyPatrols;
        internal static ConfigEntry<float> PatrolInterval;
        internal static ConfigEntry<float> PatrolDistance;
        internal static ConfigEntry<bool> AllowClientSquads;

        // Ground War
        internal static ConfigEntry<bool> GroundWarEnabled;
        internal static ConfigEntry<float> SoldierCaptureStrength;
        internal static ConfigEntry<bool> AiWarfare;
        internal static ConfigEntry<float> WaveInterval;
        internal static ConfigEntry<int> WaveSize;
        internal static ConfigEntry<int> MaxAiPerFaction;
        internal static ConfigEntry<float> WaveStandOff;
        internal static ConfigEntry<bool> RespawnMap;
        internal static ConfigEntry<KeyCode> SquadMenuKey;
        internal static ConfigEntry<KeyCode> MotorPoolKey;
        internal static ConfigEntry<float> MotorPoolMaxMass;
        internal static ConfigEntry<float> MotorPoolCostScale;
        internal static ConfigEntry<float> MotorPoolCooldown;
        internal static ConfigEntry<int> LastKit;
        internal static ConfigEntry<KeyCode> RallyKey;
        internal static ConfigEntry<bool> TruckTransport;
        internal static ConfigEntry<string> TransportVehicle;
        internal static ConfigEntry<float> ConvoyDistance;
        internal static ConfigEntry<float> DismountDistance;
        internal static ConfigEntry<float> RallyCooldown;
        internal static ConfigEntry<float> RallyEnemyRadius;
        internal static ConfigEntry<float> RallyOverrunRadius;

        // Medical
        internal static ConfigEntry<bool> ReviveEnabled;
        internal static ConfigEntry<float> BleedOutTime;
        internal static ConfigEntry<float> ReviveTime;
        internal static ConfigEntry<float> ReviveHealth;
        internal static ConfigEntry<float> OverkillDamage;
        internal static ConfigEntry<float> BandageHeal;
        internal static ConfigEntry<int> Bandages;
        internal static ConfigEntry<KeyCode> InteractKey;

        // Launcher
        internal static ConfigEntry<int> LauncherAmmo;
        internal static ConfigEntry<float> LauncherReload;
        internal static ConfigEntry<float> LauncherRange;
        internal static ConfigEntry<float> LauncherEjectSpeed;
        internal static ConfigEntry<float> LockTime;
        internal static ConfigEntry<float> LockConeDegrees;
        internal static ConfigEntry<string> AaMissileName;
        internal static ConfigEntry<string> AtMissileName;
        internal static ConfigEntry<float> MouseSensitivity;
        internal static ConfigEntry<float> Fov;
        internal static ConfigEntry<float> AdsFov;
        internal static ConfigEntry<float> EyeHeight;

        // Movement
        internal static ConfigEntry<float> WalkSpeed;
        internal static ConfigEntry<float> SprintSpeed;
        internal static ConfigEntry<float> JumpSpeed;

        // Rifle
        internal static ConfigEntry<float> FireInterval;
        internal static ConfigEntry<float> MuzzleVelocity;
        internal static ConfigEntry<float> PierceDamage;
        internal static ConfigEntry<float> Range;
        internal static ConfigEntry<float> SpreadDegrees;
        internal static ConfigEntry<int> MagazineSize;
        internal static ConfigEntry<float> ReloadTime;
        internal static ConfigEntry<float> RifleVolume;

        // Grenades
        internal static ConfigEntry<int> GrenadeCount;
        internal static ConfigEntry<float> GrenadeYield;
        internal static ConfigEntry<float> GrenadeFuse;
        internal static ConfigEntry<float> GrenadeThrowSpeed;

        private void Awake()
        {
            Log = Logger;
            PluginDir = System.IO.Path.GetDirectoryName(Info.Location);

            RifleModel = Config.Bind("Viewmodel", "Model", "AssaultRifle2_1.obj", "OBJ file in the plugin's models folder (Quaternius CC0 pack. Iron sights line up on AssaultRifle2_1..4 (M4s) and AssaultRifle_2/4/5 (AKs); AssaultRifle_1/3 sights are obstructed)");
            RifleLength = Config.Bind("Viewmodel", "Length", 0.84f, "Overall rifle length in metres (model is scaled to this)");
            SightRadius = Config.Bind("Viewmodel", "EyeToFrontSight", 0.52f, "Distance from your eye to the front sight when aiming");

            ToggleKey = Config.Bind("Controls", "ToggleKey", new KeyboardShortcut(KeyCode.F7), "Enter/exit first-person infantry mode while dismounted");
            DismountKey = Config.Bind("Controls", "DismountKey", new KeyboardShortcut(KeyCode.F8), "Climb out of your aircraft when stopped on the ground (start a mission on foot)");
            AutoEnter = Config.Bind("Controls", "AutoEnter", true, "Automatically take control once your ejected pilot lands");
            MouseSensitivity = Config.Bind("Controls", "MouseSensitivity", 2f, "Mouse look sensitivity");
            Fov = Config.Bind("Controls", "FOV", 75f, "Field of view on foot");
            AdsFov = Config.Bind("Controls", "AimFOV", 40f, "Field of view while aiming (right mouse)");
            EyeHeight = Config.Bind("Controls", "EyeHeight", 0.45f, "Camera height above the pilot's body origin");

            WalkSpeed = Config.Bind("Movement", "WalkSpeed", 3.5f, "m/s");
            SprintSpeed = Config.Bind("Movement", "SprintSpeed", 7f, "m/s (hold Left Shift)");
            JumpSpeed = Config.Bind("Movement", "JumpSpeed", 4.5f, "Upward m/s on jump (Space)");

            FireInterval = Config.Bind("Rifle", "FireInterval", 0.1f, "Seconds between shots");
            MuzzleVelocity = Config.Bind("Rifle", "MuzzleVelocity", 850f, "m/s, feeds the game's armor-penetration model");
            PierceDamage = Config.Bind("Rifle", "PierceDamage", 20f, "Pierce damage per round at muzzle velocity");
            Range = Config.Bind("Rifle", "Range", 800f, "Hitscan range in metres");
            SpreadDegrees = Config.Bind("Rifle", "SpreadDegrees", 0.4f, "Hip-fire cone; halved when aiming");
            MagazineSize = Config.Bind("Rifle", "MagazineSize", 30, "Rounds per magazine");
            ReloadTime = Config.Bind("Rifle", "ReloadTime", 2.2f, "Seconds (R to reload)");
            RifleVolume = Config.Bind("Rifle", "Volume", 0.6f, "Gunshot volume (sound borrowed from the game's smallest gun)");

            GrenadeCount = Config.Bind("Grenades", "Count", 3, "Grenades carried (G to throw)");
            GrenadeYield = Config.Bind("Grenades", "Yield", 0.2f, "kg TNT equivalent, fed to the game's blast model (frag radius = 20 * cbrt(yield) m)");
            GrenadeFuse = Config.Bind("Grenades", "Fuse", 3.5f, "Seconds");
            GrenadeThrowSpeed = Config.Bind("Grenades", "ThrowSpeed", 16f, "m/s");

            UseKey = Config.Bind("Vehicles", "UseKey", KeyCode.E, "Enter / leave a friendly ground vehicle");
            EnterDistance = Config.Bind("Vehicles", "EnterDistance", 5f, "Metres from the vehicle's hull you can climb in");
            VehicleCamDistance = Config.Bind("Vehicles", "CameraDistance", 6f, "Extra third-person camera distance");
            TurretFireTolerance = Config.Bind("Vehicles", "FireTolerance", 5f, "Turret guns only fire when the barrel is within this many degrees of your aim point (stops guns firing into their own vehicle when the target is out of the turret's reach)");
            AllowVehicleMissiles = Config.Bind("Vehicles", "AllowMissiles", false, "Let LMB fire non-gun turret weapons (unguided, experimental)");

            AiTargetsInfantry = Config.Bind("AI", "TargetInfantry", true, "Enemy gun turrets/vehicles engage you while on foot");
            AiEngageRange = Config.Bind("AI", "EngageRange", 1200f, "Max metres at which AI guns will shoot at you");
            AiInfantryPriority = Config.Bind("AI", "Priority", 0.3f, "Target value vs other units (higher = AI prefers you over vehicles)");

            CrouchKey = Config.Bind("Controls", "CrouchKey", KeyCode.C, "Toggle crouch (lower, quieter, steadier aim)");
            ProneKey = Config.Bind("Controls", "ProneKey", KeyCode.Z, "Toggle prone (lowest, slowest, steadiest aim)");
            FootstepVolume = Config.Bind("Controls", "FootstepVolume", 0.35f, "Volume of your own footsteps (0 to mute)");
            FireteamKey = Config.Bind("Squads", "FireteamKey", KeyCode.H, "Call AI riflemen who follow you and engage enemies");
            PatrolKey = Config.Bind("Squads", "PatrolKey", KeyCode.J, "Send an enemy patrol at you (practice / sandbox)");
            SquadSize = Config.Bind("Squads", "FireteamSize", 4, "Riflemen in your fireteam");
            PatrolSize = Config.Bind("Squads", "PatrolSize", 5, "Soldiers per enemy patrol");
            AiSoldierRange = Config.Bind("Squads", "EngageRange", 350f, "Metres at which AI riflemen open fire");
            AiSpreadDegrees = Config.Bind("Squads", "AimSpread", 1.2f, "AI rifle spread in degrees at close range (x2 at 300 m, more vs moving targets); lower = deadlier");
            EnemyPatrols = Config.Bind("Squads", "AutoEnemyPatrols", false, "Periodically send enemy patrols at soldiers on foot");
            PatrolInterval = Config.Bind("Squads", "AutoPatrolInterval", 150f, "Seconds between automatic enemy patrols");
            PatrolDistance = Config.Bind("Squads", "PatrolSpawnDistance", 600f, "How far away patrols appear");
            AllowClientSquads = Config.Bind("Squads", "AllowClientRequests", true, "Server: let clients call fireteams/patrols");
            BoardKey = Config.Bind("Controls", "RequestAircraftKey", KeyCode.B, "At a friendly airbase: open the aircraft selection menu");

            GroundWarEnabled = Config.Bind("Ground War", "Enabled", true, "Infantry-first play: 'Deploy on foot' (with a kit) in the aircraft selection menu, soldiers capture airbases, motor pool at friendly airbases");
            SoldierCaptureStrength = Config.Bind("Ground War", "CaptureStrengthPerSoldier", 0.5f, "Capture strength of each soldier on foot (players and AI) inside an airbase's capture radius (a truck is 2, an APC 12; airbase defence is 10)");
            AiWarfare = Config.Bind("Ground War", "AiFrontLine", true, "Server: both sides send AI infantry squads to take the nearest enemy airbase, and garrison their own bases when attacked");
            WaveInterval = Config.Bind("Ground War", "SquadInterval", 120f, "Seconds between AI squads per faction");
            WaveSize = Config.Bind("Ground War", "SquadSize", 4, "Soldiers per AI front-line squad");
            MaxAiPerFaction = Config.Bind("Ground War", "MaxAiPerFaction", 16, "Cap on AI soldiers per faction (performance)");
            WaveStandOff = Config.Bind("Ground War", "SquadStartDistance", 600f, "How far outside the objective's capture radius squads arrive (they walk the rest)");
            RespawnMap = Config.Bind("Ground War", "MapOnDeath", true, "Open the map after your soldier dies so you can redeploy");
            SquadMenuKey = Config.Bind("Ground War", "SquadOrdersKey", KeyCode.T, "Open the fireteam orders menu (then 1-5)");
            MotorPoolKey = Config.Bind("Ground War", "MotorPoolKey", KeyCode.V, "At a friendly airbase: open the motor pool (then 1-9)");
            MotorPoolMaxMass = Config.Bind("Ground War", "MotorPoolMaxMass", 40000f, "Heaviest vehicle (kg) the motor pool offers");
            MotorPoolCostScale = Config.Bind("Ground War", "MotorPoolCostScale", 0.5f, "Vehicle cost as a fraction of its unit value, paid from your funds (0 = free)");
            MotorPoolCooldown = Config.Bind("Ground War", "MotorPoolCooldown", 45f, "Seconds between vehicles per player");
            LastKit = Config.Bind("Ground War", "LastKit", 0, "Last kit chosen (0 Rifleman, 1 Anti-Tank, 2 Anti-Air, 3 Medic)");

            TruckTransport = Config.Bind("Ground War", "TruckTransport", false, "EXPERIMENTAL (off: trucks were wrecked right after spawning in testing). AI front-line squads ride a truck toward the objective and dismount short of it; off = they arrive on foot");
            TransportVehicle = Config.Bind("Ground War", "TransportVehicle", "", "Vehicle name for troop trucks (empty = MSV MRAP or another stock troop carrier)");
            ConvoyDistance = Config.Bind("Ground War", "ConvoyStartDistance", 4000f, "How far from the objective the trucks start (on their own side)");
            DismountDistance = Config.Bind("Ground War", "DismountDistance", 500f, "Squads leave the truck this far from the objective's centre (or on contact)");
            RallyKey = Config.Bind("Ground War", "RallyKey", KeyCode.Y, "Place your rally point where you stand (a forward spawn for you)");
            RallyCooldown = Config.Bind("Ground War", "RallyCooldown", 90f, "Seconds between placing rally points");
            RallyEnemyRadius = Config.Bind("Ground War", "RallyEnemyRadius", 150f, "No enemy soldiers or vehicles this close when placing one");
            RallyOverrunRadius = Config.Bind("Ground War", "RallyOverrunRadius", 40f, "Enemies this close destroy your rally point");

            ReviveEnabled = Config.Bind("Medical", "Enabled", true, "Lethal hits on soldiers on foot (players and AI) put them down to bleed out instead; friends can revive them");
            BleedOutTime = Config.Bind("Medical", "BleedOutTime", 45f, "Seconds a downed soldier lasts without help");
            ReviveTime = Config.Bind("Medical", "ReviveTime", 6f, "Seconds holding the interact key to revive (medics take half)");
            ReviveHealth = Config.Bind("Medical", "ReviveHealth", 30f, "Health after a revive (medics give double)");
            OverkillDamage = Config.Bind("Medical", "OverkillDamage", 120f, "Damage beyond what kills you at which there is no going down, you just die (big explosions)");
            BandageHeal = Config.Bind("Medical", "BandageHeal", 40f, "Health a medic's bandage restores");
            Bandages = Config.Bind("Medical", "Bandages", 6, "Bandages a medic carries");
            InteractKey = Config.Bind("Medical", "InteractKey", KeyCode.F, "Hold to revive a downed friend; medics: tap to bandage a friend in front of you (or yourself)");

            LauncherAmmo = Config.Bind("Launcher", "Rockets", 4, "Missiles carried (2 to select the launcher)");
            LauncherReload = Config.Bind("Launcher", "Reload", 4f, "Seconds between launches");
            LauncherRange = Config.Bind("Launcher", "LockRange", 6000f, "Max lock range in metres");
            LauncherEjectSpeed = Config.Bind("Launcher", "EjectSpeed", 30f, "Initial missile speed out of the tube (m/s)");
            LockTime = Config.Bind("Launcher", "LockTime", 1.5f, "Seconds holding RMB on a target to lock");
            LockConeDegrees = Config.Bind("Launcher", "LockCone", 4f, "Degrees from the crosshair a target can be locked");
            AaMissileName = Config.Bind("Launcher", "AntiAirMissile", "", "Force a missile by short name (see log for candidates); empty = lightest IR anti-air missile");
            AtMissileName = Config.Bind("Launcher", "AntiSurfaceMissile", "", "Force a missile by short name; empty = lightest self-guided anti-surface missile");

            NetSync.RegisterSerializers();

            var harmony = new Harmony(Guid);
            foreach (System.Type patchClass in new[] { typeof(Patches), typeof(VehicleControl) })
            {
                // Logged before patching so a native crash still shows which class was being applied.
                Log.LogInfo($"Applying patches: {patchClass.Name}");
                try
                {
                    harmony.PatchAll(patchClass);
                }
                catch (System.Exception e)
                {
                    Log.LogError($"Patching {patchClass.Name} failed; related features disabled.\n{e}");
                }
            }

            var host = new GameObject("NOInfantry");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<InfantryController>();
            if (AutoTest.Requested)
            {
                Log.LogWarning("-noiAutotest given: running the automated in-game test, the game will quit when done");
                if (AutoTestMp.Role != null) host.AddComponent<AutoTestMp>();
                else host.AddComponent<AutoTest>();
            }

            Log.LogInfo($"{Name} {Version} loaded");
        }
    }
}

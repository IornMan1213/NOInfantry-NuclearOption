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
        public const string Version = "0.4.0";

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

            BoardKey = Config.Bind("Controls", "RequestAircraftKey", KeyCode.B, "At a friendly airbase: open the aircraft selection menu");

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
                host.AddComponent<AutoTest>();
            }

            Log.LogInfo($"{Name} {Version} loaded");
        }
    }
}

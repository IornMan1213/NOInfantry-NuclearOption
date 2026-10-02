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
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;

        // Controls
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;
        internal static ConfigEntry<bool> AutoEnter;
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

            ToggleKey = Config.Bind("Controls", "ToggleKey", new KeyboardShortcut(KeyCode.F7), "Enter/exit first-person infantry mode while dismounted");
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

            new Harmony(Guid).PatchAll(typeof(Patches));

            var host = new GameObject("NOInfantry");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<InfantryController>();

            Log.LogInfo($"{Name} {Version} loaded");
        }
    }
}

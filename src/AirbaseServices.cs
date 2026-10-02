using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;

namespace NOInfantry
{
    // Ground -> air: at a friendly airbase the soldier can open the game's own aircraft selection menu.
    // When the player's new aircraft spawns, infantry mode hands the camera back and the soldier
    // "returns to base" exactly like the game's own pilot-return path (Returned + destroyed).
    internal static class AirbaseServices
    {
        internal static bool BypassAirbasePatch;

        internal static Airbase NearFriendlyAirbase(PilotDismounted pilot)
        {
            if (pilot == null || pilot.NetworkHQ == null) return null;
            BypassAirbasePatch = true;
            try
            {
                return pilot.NetworkHQ.AnyNearAirbase(pilot.transform.position, out Airbase airbase) ? airbase : null;
            }
            finally
            {
                BypassAirbasePatch = false;
            }
        }

        internal static bool OpenAircraftMenu(Airbase airbase)
        {
            GameplayUI ui = SceneSingleton<GameplayUI>.i;
            if (ui == null || airbase == null || !GameManager.GetLocalPlayer<Player>(out Player localPlayer)) return false;
            if (localPlayer.HQ != airbase.CurrentHQ)
            {
                Plugin.Log.LogWarning("Airbase is not held by your faction.");
                return false;
            }
            var prefab = Traverse.Create(ui).Field("aircraftSelectionMenu").GetValue<GameObject>();
            if (prefab == null || ui.gameplayCanvas == null)
            {
                Plugin.Log.LogWarning("Aircraft selection menu not available.");
                return false;
            }
            Object.Instantiate(prefab, ui.gameplayCanvas.transform).GetComponent<AircraftSelectionMenu>().Initialize(localPlayer, airbase);
            Plugin.Log.LogInfo($"Opened aircraft selection at {airbase.name}");
            return true;
        }

        // True once the local player is flying a fresh aircraft (not the one they ejected from).
        internal static bool HasNewAircraft()
        {
            return GameManager.GetLocalAircraft(out Aircraft a) && a != null && !a.disabled && !a.HasEjected();
        }

        internal static void ReturnPilot(PilotDismounted pilot)
        {
            if (pilot == null || NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active) return;
            if (NearFriendlyAirbase(pilot) == null) return; // left in the field: stays as a pilot that can be rescued
            pilot.NetworkunitState = Unit.UnitState.Returned;
            pilot.Networkdisabled = true;
            Object.Destroy(pilot.gameObject);
        }
    }
}

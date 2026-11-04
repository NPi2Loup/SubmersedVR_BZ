using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine.XR;

namespace SubnauticaMapBZBridge
{
    // VR input bridge for the SubnauticaMap BZ mod (Nexus 15, "SubnauticaMap_BZ" by newman55, closed source).
    // Port of the Subnautica (SN1) SubnauticaMapBridge 0.3.8 — the BZ mod reuses the same API
    // (SubnauticaMap.Controller, CreateNoteEvent, MapRoomMapIcon) with the same world-eulerAngles
    // scan-circle bug. Talks to the map mod through reflection only.
    //
    // BZ differences vs SN1:
    // - the mod's Zoom gate has NO exact ±1 mouseScrollDelta check: ANY non-zero scroll fires a
    //   fixed 500px Zoom() every frame (decompiled Controller.Update L476-513) — with the right
    //   stick emulated into mouseScrollDelta by SubmersedVR that is "bcp trop rapide". The bridge
    //   therefore BLOCKS native zoom in the map context (zeroed scroll + zeroed controller
    //   fallback) and drives the mod's private Zoom(±50, true) from the right stick, 10 steps/s.
    // - the mod assembly name is not stable (SubnauticaMap_BZ.dll, title SubnauticaMapBZ) —
    //   detection scans the loaded AppDomain for the SubnauticaMap.Controller type instead of
    //   Assembly.Load.
    [BepInPlugin("SubnauticaMapBZBridge", "SubnauticaMap BZ VR Bridge", "0.1.0")]
    [BepInDependency("SubmersedVR")]
    public class Mod : BaseUnityPlugin
    {
        public static ManualLogSource logger;
        public static Harmony harmony;

        private void Awake()
        {
            logger = Logger;
            if (XRSettings.loadedDeviceName != "OpenVR")
            {
                Logger.LogInfo("Not running in OpenVR mode, bridge disabled.");
                return;
            }

            harmony = new Harmony("SubnauticaMapBZBridge");
            harmony.PatchAll();

            if (MapMod.Detect())
            {
                Logger.LogInfo("SubnauticaMap BZ detected, VR map controls active.");
            }
            else
            {
                Logger.LogInfo("SubnauticaMap BZ not detected, will retry at scene load. Bridge idle.");
            }
        }
    }
}

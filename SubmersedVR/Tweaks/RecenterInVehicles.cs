using HarmonyLib;
using UnityEngine;
using System.Collections;
using UWE;

namespace SubmersedVR
{
    static class VehiclesVR
    {
        public static Exosuit PilotedExosuit()
        {
            return Player.main?.currentMountedVehicle == null ? null : Player.main?.currentMountedVehicle as Exosuit;
        }
    }

    // 28/10 spec section 6: delayed vehicle recenter (SN1 port, lost in BZ -
    // RecenterInSeamoth was dead: no Seamoth in BZ). The immediate recenter
    // at entry captures the head orientation while the player is still
    // leaning in - straightening the head right after leaves a wrong view.
    // The recenter waits 1 s AND the entry cinematic to finish (head
    // settled, no view jump mid-cine), with a per-vehicle "still inside"
    // guard. Coroutine on CoroutineHost (persistent), not the vehicle
    // (destroyable on load).
    static class VehicleRecenter
    {
        public const float DelayedVehicleRecenterSeconds = 1.0f;
        public const float MaxWait = 30.0f;

        public static void Start(string what, UnityEngine.Object entryRef, System.Func<bool> stillIn)
        {
            CoroutineHost.StartCoroutine(DelayedVehicleRecenter(what, entryRef, stillIn));
        }

        static IEnumerator DelayedVehicleRecenter(string what, UnityEngine.Object entryRef, System.Func<bool> stillIn)
        {
            float t0 = Time.time;
            while (true)
            {
                yield return new WaitForSeconds(0.25f);
                if (entryRef == null || (UnityEngine.Object)(object)entryRef == null || !stillIn())
                {
                    yield break; // exited before the recenter - abort
                }
                if (Time.time - t0 >= DelayedVehicleRecenterSeconds
                    && PlayerCinematicController.cinematicModeCount == 0)
                {
                    VRUtil.Recenter();
                    Mod.logger.LogInfo($"[VR] delayed recenter ({what})");
                    yield break;
                }
                if (Time.time - t0 > MaxWait)
                {
                    Mod.logger.LogInfo($"[VR] delayed recenter ({what}) ABORTED (max wait)");
                    yield break;
                }
            }
        }
    }

    [HarmonyPatch(typeof(Exosuit), nameof(Exosuit.OnPlayerEntered))]
    static class RecenterInExosuit
    {
        public static void Postfix(Exosuit __instance)
        {
            // currentMountedVehicle IS set for the Exosuit
            // (Vehicle.EnterVehicle -> Player.main.currentMountedVehicle = this).
            VehicleRecenter.Start("Prawn", __instance, () => Player.main != null
                && (object)Player.main.currentMountedVehicle == (object)__instance);
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.EnterPilotingMode))]
    static class RecenterWhenPilotingCyclops
    {
        public static void Postfix()
        {
            VRUtil.Recenter();
        }
    }

    [HarmonyPatch(typeof(SeaTruckMotor), nameof(SeaTruckMotor.StartPiloting))]
    static class RecenterInSeaTruckMotor
    {
        public static void Postfix(SeaTruckMotor __instance)
        {
            // currentMountedVehicle is NOT set for the SeaTruck (StartPiloting
            // goes through Player.EnterLockedMode - decompiled L681-702):
            // guard on the motor's private property `piloting` (publicized,
            // getter side-effect free).
            VehicleRecenter.Start("SeaTruck", __instance, () =>
                (object)__instance != null && __instance.piloting);
        }
    }

    [HarmonyPatch(typeof(Hoverbike), nameof(Hoverbike.EnterVehicle))]
    static class RecenterInHoverbike
    {
        public static void Postfix()
        {
            VRUtil.Recenter();
        }
    }
}

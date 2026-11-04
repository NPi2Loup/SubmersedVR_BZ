using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    // VR fix for the precursor teleport green tunnel (TeleportScreenFX).
    // Ported from the SN1 line (Okabintaro/SubmersedVR, commit 45a0947,
    // MIT): the original tunnel is a 2D head-locked swirl (a full-screen
    // screen-space animation that follows the head) which causes eye strain
    // in VR (the two eyes see incoherent images). The fix replaces it with
    // a plain blit of the raw world + a world-locked 3D green sphere
    // enveloping the player (see TeleportSphere3D) - no full-screen
    // head-following animation.
    //
    // BZ adaptation: TeleportScreenFX.OnRenderImage is VOID here (in SN1 it
    // returned bool), so a bool prefix cannot skip the original - the raw
    // world blit is applied in a POSTFIX, which overwrites the swirl blit
    // the original just drew (source/destination are still valid).
    //
    // Settings.FixTeleportEffect:
    //   false: 100% original (the 2D swirl runs).
    //   true: the plain blit + the green sphere (the fix).
    static class TeleportScreenFXVRFix
    {
        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        // teleport-cycle tracking: fx.amount 0 -> >0 = cycle start
        static bool effectActive;
        static double activeSince;
        static int cycleCount;
        static Camera guardCam;
        // Set once per cycle at start: the sphere fix is only valid for the
        // PRECURSOR teleporters (a precursor door material within range).
        // Other TeleportScreenFX users (30/10 log: the manual
        // Seatruck_Teleportation_Module) keep the original swirl - the
        // fallback sphere looked like "missing effects" (user feedback).
        // (internal: also read by TeleportSphere3D's on-demand spawn path)
        internal static bool cycleFixActive;

        // ---- OnPreRender: cycle tracking + the sphere lifecycle ----

        public static void OnPreRender(TeleportScreenFX fx)
        {
            if (Mod.quitting)
            {
                return;
            }
            var cam = Camera.current;
            if (cam == null)
            {
                return;
            }
            // attach the per-frame driver (the sphere update) on the FX cam
            if (guardCam != cam)
            {
                guardCam = cam;
                cam.gameObject.GetOrAddComponent<TeleportMonoGuard>();
            }
            // grab the game's swirl "nerves" texture (recalls the vortex)
            // for the sphere (idempotent: set once, then ignored)
            if (fx.mat != null)
            {
                TeleportSphere3D.SetSwirlTexture(fx.mat.GetTexture("_NervesTex"));
            }
            if (fx.amount > 0f)
            {
                if (!effectActive)
                {
                    effectActive = true;
                    activeSince = Time.time;
                    cycleCount++;
                    // Decide ONCE per cycle: the sphere needs the game's
                    // precursor door material (within range). Without it
                    // (manual teleporters) the original swirl is kept.
                    cycleFixActive = Settings.FixTeleportEffect
                        && TeleportSphere3D.HasNearbyDoorMaterial(TeleportSphere3D.DoorMaxRangeSq);
                    Mod.logger.LogInfo("[TeleportFX] cycle " + cycleCount + " started (fix=" + cycleFixActive + ")");
                    if (cycleFixActive)
                    {
                        TeleportSphere3D.Spawn(fx.amount);
                    }
                }
            }
            else
            {
                if (effectActive)
                {
                    Mod.logger.LogInfo("[TeleportFX] cycle " + cycleCount + " ended after " + (Time.time - activeSince).ToString("F1", Inv) + "s");
                    effectActive = false;
                    cycleFixActive = false;
                    TeleportSphere3D.OnCycleEnd();
                }
            }
        }

        // Called from the sphere's safety timeout (stuck cycle): resets the
        // cycle state so the next teleport can spawn a new sphere.
        public static void ForceCycleEnd()
        {
            if (effectActive)
            {
                effectActive = false;
                cycleFixActive = false;
                Mod.logger.LogInfo("[TeleportFX] cycle " + cycleCount + " force-ended (safety)");
                TeleportSphere3D.OnCycleEnd();
            }
        }

        // ---- OnRenderImage: replace the 2D swirl with a plain blit ----

        public static void OnRenderImage(TeleportScreenFX fx, RenderTexture source, RenderTexture destination)
        {
            if (Mod.quitting || fx.amount <= 0f)
            {
                return;
            }
            // cycleFixActive (set once at cycle start) - the manual
            // teleporters keep their original swirl (no raw-world blit).
            if (cycleFixActive)
            {
                // Fix: blit the raw world (source) OVER the destination -
                // the original (already run, void method) just drew the 2D
                // swirl, this overwrites it with the raw scene. The sphere
                // (rendered by the main camera) is not in this texture and
                // stays visible.
                if (source != null && destination != null)
                {
                    Graphics.Blit(source, destination);
                }
            }
        }

        // Persistent per-frame driver on the FX camera's object (same
        // pattern as SonarScreenShaderFixV2's SonarScreenDriver): updates
        // the sphere (opacity = fx.amount) every frame.
        class TeleportMonoGuard : MonoBehaviour
        {
            void Update()
            {
                TeleportSphere3D.UpdateSphere(GetComponent<TeleportScreenFX>());
            }
        }
    }

    [HarmonyPatch(typeof(TeleportScreenFX), nameof(TeleportScreenFX.OnPreRender))]
    static class TeleportScreenFXPreRenderPatch
    {
        [HarmonyPrefix]
        static void Prefix(TeleportScreenFX __instance)
        {
            TeleportScreenFXVRFix.OnPreRender(__instance);
        }
    }

    [HarmonyPatch(typeof(TeleportScreenFX), nameof(TeleportScreenFX.OnRenderImage))]
    static class TeleportScreenFXOnRenderImagePatch
    {
        // BZ: void method -> POSTFIX (a void prefix cannot skip the
        // original; overwriting destination after the fact has the same
        // visible result as the SN1 prefix blit+skip).
        [HarmonyPostfix]
        static void Postfix(TeleportScreenFX __instance, RenderTexture source, RenderTexture destination)
        {
            TeleportScreenFXVRFix.OnRenderImage(__instance, source, destination);
        }
    }
}

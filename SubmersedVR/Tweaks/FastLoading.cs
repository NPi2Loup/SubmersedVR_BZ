using System;
using System.Collections;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using UXR = UnityEngine.XR;

namespace SubmersedVR
{
    // Fast loading screen. Approach credited to toebeann's "Fast Loading
    // Screen" for Subnautica Below Zero (nexusmods.com/subnauticabelowzero/
    // mods/171, github.com/toebeann/FastLoadingScreen, LGPL-3.0) --
    // re-implemented here, not copied.
    //
    // Part 1 (his trick): BZ advances its loading pipeline once per frame,
    // and in DESKTOP the frame rate is capped by the monitor refresh +
    // vSync, so a load takes ~2x longer than it needs to. While a load is
    // running, lift the frame cap (targetFrameRate = -1) and disable
    // vSync, then restore both when the WaitScreen closes. NOTE: in VR
    // mode the game already runs uncapped (saved values were -1/0), so
    // this part is a no-op here -- kept for parity.
    //
    // Part 2 (ours, the part that actually helps in VR): during the load
    // the headset keeps rendering the full-res stereo scene (100 %
    // render scale) while the CPU hammers on the world-streaming stages
    // (WorldMount/EntityCells ~20 s of the 32 s load) -- the loading
    // screen visibly stutters. Drop the render scale to LoadRenderScale
    // while loading (~1/4 of the GPU pixels per eye) and restore it when
    // the load finishes.
    static class FastLoading
    {
        //User preference (2026-10-18): loading at full res (the 0.5f drop
        //made the loading screen too blurry).
        const float LoadRenderScale = 1f;

        static int savedFrameRate = -1;
        static int savedVSync = -1;
        static float savedRenderScale = 1f;
        static bool boosting;
        static bool restoreScheduled;
        static Stopwatch watch;

        public static void BeginBoost()
        {
            if (boosting)
            {
                return;
            }
            boosting = true;
            watch = Stopwatch.StartNew();
            savedFrameRate = Application.targetFrameRate;
            savedVSync = QualitySettings.vSyncCount;
            savedRenderScale = UXR.XRSettings.renderScale;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            UXR.XRSettings.renderScale = LoadRenderScale;
            //Safety: if the load is cancelled before ReportStageDurations
            //fires, the restore below would never be scheduled -- cap it.
            CinematicFallbackWatcher.EnsureBehaviour().StartCoroutine(SafetyTimeout());
            Mod.logger.LogInfo($"[FastLoading] boosting load (saved frameRate={savedFrameRate} vSync={savedVSync} renderScale={savedRenderScale:0.00} -> {LoadRenderScale:0.00})");
        }

        public static void ScheduleRestore()
        {
            if (!boosting || restoreScheduled)
            {
                return;
            }
            restoreScheduled = true;
            CinematicFallbackWatcher.EnsureBehaviour().StartCoroutine(WaitAndRestore());
        }

        static IEnumerator SafetyTimeout()
        {
            yield return new WaitForSecondsRealtime(120f);
            if (boosting && !WaitScreen.IsWaiting)
            {
                Mod.logger.LogInfo("[FastLoading] 120s safety timeout -> restoring load-time settings (load cancelled?)");
                Restore();
            }
        }

        static IEnumerator WaitAndRestore()
        {
            yield return null;
            int frames = 0;
            while (WaitScreen.IsWaiting && frames < 600)
            {
                yield return null;
                frames++;
            }
            Restore();
        }

        static void Restore()
        {
            if (!boosting)
            {
                return;
            }
            boosting = false;
            restoreScheduled = false;
            Application.targetFrameRate = savedFrameRate;
            QualitySettings.vSyncCount = savedVSync;
            UXR.XRSettings.renderScale = savedRenderScale;
            double secs = watch != null ? watch.Elapsed.TotalSeconds : -1;
            Mod.logger.LogInfo($"[FastLoading] load finished in {secs:0.00}s -- restored frameRate={savedFrameRate} vSync={savedVSync} renderScale={savedRenderScale:0.00}");
        }
    }

    //One patch class per target method: HarmonyX MERGES multiple
    //[HarmonyPatch] attributes on the same class into ONE target (see
    //PatchClassProcessor: HarmonyMethod.Merge) -- with two attributes only
    //one of the two methods got patched (user test: the LoadGameAsync path
    //never boosted).
    [HarmonyPatch(typeof(uGUI_MainMenu), "LoadGameAsync")]
    static class FastLoading_LoadGameAsync
    {
        static void Prefix()
        {
            FastLoading.BeginBoost();
        }
    }

    [HarmonyPatch(typeof(uGUI_MainMenu), "StartNewGame")]
    static class FastLoading_StartNewGame
    {
        static void Prefix()
        {
            FastLoading.BeginBoost();
        }
    }

    [HarmonyPatch(typeof(WaitScreen), nameof(WaitScreen.ReportStageDurations))]
    static class FastLoading_LoadEnd
    {
        static void Prefix()
        {
            FastLoading.ScheduleRestore();
        }
    }
}

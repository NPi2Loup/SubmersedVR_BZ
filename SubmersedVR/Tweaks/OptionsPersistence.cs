using System.Collections;
using HarmonyLib;
using UWE;
using UnityEngine;
using UnityEngine.UI;

namespace SubmersedVR
{
    // Below Zero's options panel rebuilds every tab on OnEnable (AddTabs) and
    // only saves the settings on a proper close. When the panel is disabled
    // without a close (scene transition, back to the main menu) the pending
    // changes are dropped, and the next menu cycle runs
    // GameSettings.LoadAsync which rewrites every setting static (vanilla and
    // mod) from the file on disk. Result: sliders show the stale/default value
    // on reopen and the value the user set is lost - same for vanilla options
    // (UI Scale, ...). SN1 didn't have this problem because its panel persists.
    // Saving on disable makes the in-memory values durable the moment the
    // panel goes away, whatever the exit path.
    [HarmonyPatch(typeof(uGUI_OptionsPanel), nameof(uGUI_OptionsPanel.OnDisable))]
    static class OptionsPanel_SaveOnDisable
    {
        private static bool saving;
        private static float savingSince;

        static void Postfix()
        {
            // Paired with the "open" snapshot in Settings.CreateOptionsTab: the
            // before/after slider logs in between show exactly what the user
            // changed and what the save then wrote to disk.
            OptionsPanelDiagnostics.LogSnapshot("close");
            // Safety: if a previous save never reported back (exception inside
            // the coroutine), the flag would block every later save forever.
            if (saving && Time.time - savingSince < 10f)
            {
                return;
            }
            saving = true;
            savingSince = Time.time;
            GameSettings.OnSaveDelegate done = (op) => saving = false;
            CoroutineHost.StartCoroutine(GameSettings.SaveAsync(done));
        }
    }

    // Diagnostic: snapshot the setting statics (vanilla + mod) at panel
    // open/close so the log shows exactly what the panel saw and saved.
    static class OptionsPanelDiagnostics
    {
        internal static void LogSnapshot(string context)
        {
            Mod.logger.LogInfo($"Options snapshot ({context}): uiScale={MiscSettings.GetUIScale():0.###} gamma={GammaCorrection.gamma:0.###} subtitlesSpeed={Subtitles.speed:0.###} fov={MiscSettings.fieldOfView:0.###} canvasX={Settings.HudCanvasScaleX:0.###} canvasY={Settings.HudCanvasScaleY:0.###} loadingScreenScale={Settings.LoadingScreenScale:0.###} vrUiApplied={Settings.VrUiScaleDefaultApplied}");
        }
    }

    // The BZ panel also resets its scroll position on every reopen because the
    // content is rebuilt by AddTabs. Cache the position on disable and restore
    // it a couple of frames after the rebuild, once the layout has settled.
    static class OptionsPanelScroll
    {
        internal static Vector2? savedNormalizedPosition;
        internal static ScrollRect scroll;

        internal static ScrollRect Find(uGUI_OptionsPanel panel)
        {
            if (panel.panesContainer != null)
            {
                var sr = panel.panesContainer.GetComponentInParent<ScrollRect>();
                if (sr != null)
                {
                    return sr;
                }
            }
            return panel.GetComponentInParent<ScrollRect>();
        }
    }

    [HarmonyPatch(typeof(uGUI_OptionsPanel), nameof(uGUI_OptionsPanel.OnDisable))]
    static class OptionsPanel_CacheScroll
    {
        static void Postfix(uGUI_OptionsPanel __instance)
        {
            var scroll = OptionsPanelScroll.Find(__instance);
            if (scroll != null)
            {
                OptionsPanelScroll.scroll = scroll;
                OptionsPanelScroll.savedNormalizedPosition = scroll.normalizedPosition;
            }
        }
    }

    [HarmonyPatch(typeof(uGUI_OptionsPanel), nameof(uGUI_OptionsPanel.OnEnable))]
    static class OptionsPanel_RestoreScroll
    {
        static void Postfix(uGUI_OptionsPanel __instance)
        {
            var saved = OptionsPanelScroll.savedNormalizedPosition;
            if (saved == null)
            {
                return;
            }
            // Prefer the ScrollRect that belongs to this panel; only fall back
            // to the cached one (it may belong to another panel instance).
            var scroll = OptionsPanelScroll.Find(__instance);
            if ((UnityEngine.Object)(object)scroll == null)
            {
                scroll = OptionsPanelScroll.scroll;
            }
            if ((UnityEngine.Object)(object)scroll == null)
            {
                return;
            }
            __instance.StartCoroutine(Restore(__instance, scroll, saved.Value));
        }

        private static IEnumerator Restore(uGUI_OptionsPanel panel, ScrollRect scroll, Vector2 position)
        {
            yield return null;
            yield return null;
            if ((UnityEngine.Object)(object)scroll == null || !panel.isActiveAndEnabled)
            {
                yield break;
            }
            scroll.normalizedPosition = position;
        }
    }
}

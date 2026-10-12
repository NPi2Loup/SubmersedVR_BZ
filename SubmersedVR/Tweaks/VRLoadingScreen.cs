using System.Text;
using UnityEngine;
using UnityEngine.UI;
using HarmonyLib;



namespace SubmersedVR
{
    // Taken from https://github.com/IWhoI/SubnauticaVREnhancements/blob/master/VREnhancements/UIElementsFixes.cs#L271-L299
    /*
    MIT License

    Copyright (c) 2023 IWhoI

    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
    FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
    DEALINGS IN THE SOFTWARE.
    */
    [HarmonyPatch(typeof(uGUI_SceneLoading), nameof(uGUI_SceneLoading.Awake))]
    static class LoadingScreen_Patch
    {
        private const float ArtworkScale = 0.28f;

        // In stereo each eye's view pokes past the canvas edge on its outer
        // side (IPD offset at 1 m distance), so a thin sliver of live scene
        // stays visible next to the black loading plane (right side in the
        // 2026-10-12 session). The overscan plane is an extra black Image
        // stretched 10 % past the loading root on every side.
        private const float PlaneOverscan = 0.10f;

        private static Transform loadingScreen;

        static void Postfix(uGUI_SceneLoading __instance)
        {
            // loadingBackground is a uGUI_Fader on the LoadingScreen root GO:
            // "background" and "screen" are the same transform, so only
            // ApplyScale owns its scale.
            loadingScreen = __instance.transform;
            var background = __instance.loadingBackground.transform;
            EnsureOverscanPlane(background);
            Image loadingArtwork = background.Find("LoadingArtwork")?.GetComponent<Image>();
            Mod.logger.LogInfo($"Loading screen: screenScale={Settings.LoadingScreenScale} background children=[{DescribeChildren(background)}]");
            if (loadingArtwork != null)
            {
                loadingArtwork.rectTransform.localScale = Vector3.one * ArtworkScale;
            }
            if ((UnityEngine.Object)(object)__instance.loadingBar != null)
            {
                Mod.logger.LogInfo($"Loading bar: image path={TransformPath(__instance.loadingBar.transform)} rect={FormatRect(__instance.loadingBar.rectTransform)}");
            }
            ApplyScale();
        }

        // Anchored past the root (anchorMin/anchorMax outside 0..1, zero
        // sizeDelta) so it follows the root's live rect, which is resized by
        // the canvas factor. First sibling: it renders behind the artwork and
        // the bar/pengling strip (which keep their own fixed sizes and are
        // untouched). Inherits the fader's CanvasGroup (fade, raycast block)
        // and the root scale. The loading screen GO is recreated on every
        // scene load, so the child dies with it and no cleanup is needed.
        private static void EnsureOverscanPlane(Transform root)
        {
            if (root.Find("VRLoadBlackPlane") != null)
            {
                return;
            }
            var go = new GameObject("VRLoadBlackPlane", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(root, false);
            go.transform.SetAsFirstSibling();
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(-PlaneOverscan, -PlaneOverscan);
            rt.anchorMax = new Vector2(1f + PlaneOverscan, 1f + PlaneOverscan);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = Vector2.zero;
            var img = go.GetComponent<Image>();
            var faderImage = root.GetComponent<Image>();
            img.material = faderImage != null ? faderImage.material : null;
            img.color = Color.black;
            img.raycastTarget = true;
        }

        // Re-applied when the Loading Screen Scale setting changes, and (via
        // VRHud.ApplyHudScale) whenever the canvas factor changes: the loading
        // screen is a child of the screen canvas, so its displayed size would
        // otherwise inherit the canvas shrink (the "loading drift" from the
        // 18:41 session). The compensation keeps the displayed size a function
        // of the Loading Screen Scale alone.
        public static void ApplyScale()
        {
            if (loadingScreen != null && (UnityEngine.Object)(object)loadingScreen != null)
            {
                loadingScreen.localScale = new Vector3(
                    Settings.LoadingScreenScale / VRHud.HudCanvasScaleX,
                    Settings.LoadingScreenScale / VRHud.HudCanvasScaleY,
                    Settings.LoadingScreenScale);
            }
        }

        private static string TransformPath(Transform t)
        {
            var sb = new StringBuilder();
            while (t != null)
            {
                if (sb.Length > 0)
                {
                    sb.Insert(0, '/');
                }
                sb.Insert(0, t.name);
                t = t.parent;
            }
            return sb.ToString();
        }

        private static string FormatRect(RectTransform rt)
        {
            if ((UnityEngine.Object)(object)rt == null)
            {
                return "null";
            }
            return $"pos=({rt.localPosition.x:0.#},{rt.localPosition.y:0.#},{rt.localPosition.z:0.#}) size={rt.rect.width:0.#}x{rt.rect.height:0.#} worldScale={rt.lossyScale.x:0.###}";
        }

        private static string DescribeChildren(Transform root)
        {
            var sb = new StringBuilder();
            foreach (Transform t in root)
            {
                if (sb.Length > 0)
                {
                    sb.Append(" | ");
                }
                sb.Append(t.name);
                var rt = t as RectTransform;
                if (rt != null)
                {
                    sb.Append($" size={rt.rect.width:0.#}x{rt.rect.height:0.#} ls={t.localScale.x:0.###}");
                }
                if (t.GetComponent<Canvas>() != null)
                {
                    sb.Append(",Canvas");
                }
                if (t.GetComponent<uGUI_CanvasScaler>() != null)
                {
                    sb.Append(",Scaler");
                }
                foreach (Transform c in t)
                {
                    sb.Append('/').Append(c.name);
                    var crt = c as RectTransform;
                    if (crt != null)
                    {
                        sb.Append($" size={crt.rect.width:0.#}x{crt.rect.height:0.#}");
                    }
                    if (c.GetComponent<Canvas>() != null)
                    {
                        sb.Append(",Canvas");
                    }
                    if (c.GetComponent<uGUI_CanvasScaler>() != null)
                    {
                        sb.Append(",Scaler");
                    }
                }
            }
            return sb.Length > 0 ? sb.ToString() : "<none>";
        }
    }

    // The bar and the pengling strip are placed by the prefab as a full-width
    // stretch-anchored (0 -> 1) child of the loading screen root, 1920 px wide,
    // at y = 7 % of the root height (no game code repositions them) and
    // LoadingBarCanvas is not a Canvas: they draw on the main screen canvas.
    // The root is scaled by LoadingScreenScale / canvas factor, which amplifies
    // the prefab x offset and sends the bar far off the image, and leaves the
    // bar width unrelated to the image width. The strip is converted once to a
    // free-floating fixed-size strip, then re-seated every frame in the root's
    // local space: same displayed width as the artwork, just below it. Local
    // space (bar and artwork share the root parent) keeps the math independent
    // of the canvas factor.
    [HarmonyPatch(typeof(uGUI_SceneLoading), nameof(uGUI_SceneLoading.OnUpdate))]
    static class LoadingBar_Fix
    {
        // In the root's local units (root rect 640x685.7, artwork displayed
        // ~341x192): ~10 % of the artwork height.
        private const float BarMargin = 20f;

        private static RectTransform barCanvas;
        private static RectTransform artwork;
        private static bool layoutLogged;
        private static bool placementLogged;

        static void Postfix(uGUI_SceneLoading __instance)
        {
            if ((UnityEngine.Object)(object)__instance.loadingBar == null ||
                __instance.loadingBackground.canvasGroup.alpha <= 0f)
            {
                return;
            }
            // The loading screen GO is recreated on every scene load: the
            // fake-null operator catches the destroyed cached refs so they are
            // re-fetched from the fresh instance.
            if ((UnityEngine.Object)(object)artwork == null)
            {
                artwork = __instance.loadingBackground.transform.Find("LoadingArtwork") as RectTransform;
            }
            if ((UnityEngine.Object)(object)barCanvas == null)
            {
                barCanvas = __instance.loadingBar.transform.parent as RectTransform;
            }
            if ((UnityEngine.Object)(object)artwork == null ||
                (UnityEngine.Object)(object)barCanvas == null ||
                barCanvas.parent == null ||
                artwork.rect.width <= 0f || artwork.rect.height <= 0f)
            {
                return;
            }
            Place(__instance, artwork, barCanvas);
        }

        private static void Place(uGUI_SceneLoading instance, RectTransform art, RectTransform bar)
        {
            if (!layoutLogged)
            {
                layoutLogged = true;
                var barImage = instance.loadingBar;
                var root = instance.loadingBackground.transform as RectTransform;
                Mod.logger.LogInfo($"Loading bar layout: barPivot={barImage.rectTransform.pivot} barCanvasPivot={bar.pivot} barCanvasAnchors=({bar.anchorMin.x:0.##},{bar.anchorMin.y:0.##})-({bar.anchorMax.x:0.##},{bar.anchorMax.y:0.##}) barCanvasZ={bar.localPosition.z:0.##} artPivot={art.pivot} artPos=({art.localPosition.x:0.#},{art.localPosition.y:0.#}) rootRect={root.rect.width:0.#}x{root.rect.height:0.#} rootPivot={root.pivot}");
            }
            // Convert the stretch strip to a free-floating fixed-size strip
            // once: with stretch anchors, localPosition fights the anchor
            // layout and the bar ends up off-center (see session 21:41). The
            // strip's sizeDelta is (0,0) - its width came from the stretch
            // anchors - so the design width must be restored, otherwise the
            // rect collapses and the width scaling divides by ~0 (session
            // 22:12: barScale=34133340, bar invisible).
            if (bar.anchorMin.x != 0.5f || bar.anchorMax.x != 0.5f)
            {
                bar.anchorMin = new Vector2(0.5f, bar.anchorMin.y);
                bar.anchorMax = new Vector2(0.5f, bar.anchorMax.y);
                bar.sizeDelta = new Vector2(1920f, 0f);
                // The prefab strip pivot is (0, 0.5) - its left edge - which
                // would leave the bar shifted right of the artwork center;
                // center it (children are positioned relative to the strip
                // rect, so a pivot change moves nothing else).
                bar.pivot = new Vector2(0.5f, 0.5f);
                bar.anchoredPosition = new Vector2(0f, bar.anchoredPosition.y);
            }
            // All in the root's local units: the artwork's displayed bottom
            // edge (the pivot offset below the transform origin), and its
            // displayed width (the 1920 px strip is scaled to match it, the
            // pengling sibling follows). The scale must be uniform in X and Y:
            // scaling X only squishes the pengling sprite horizontally and it
            // looks stretched vertically (session 23:08).
            float artBottomY = art.localPosition.y - art.pivot.y * art.rect.height * art.localScale.y;
            float barScale = art.rect.width * art.localScale.x / Mathf.Max(1e-5f, bar.rect.width);
            bar.localScale = new Vector3(barScale, barScale, 1f);
            bar.localPosition = new Vector3(art.localPosition.x, artBottomY - BarMargin, bar.localPosition.z);
            if (!placementLogged)
            {
                placementLogged = true;
                Mod.logger.LogInfo($"Loading bar placed: art={art.rect.width:0.#}x{art.rect.height:0.#} artScale={art.localScale.x:0.###} artPos=({art.localPosition.x:0.#},{art.localPosition.y:0.#}) barScale={bar.localScale.x:0.###} pos=({bar.localPosition.x:0.#},{bar.localPosition.y:0.#},{bar.localPosition.z:0.#})");
            }
        }
    }

    [HarmonyPatch(typeof(WaitScreen), nameof(WaitScreen.Update))]
    static class LockInputWhileLoading
    {
        static void Postfix(WaitScreen __instance)
        {
            SteamVrGameInput.InputLocked = __instance.isWaiting;
        }
    }


    /*
    //We modified the screen scale to be smaller in order to display all UI items but we want
    //this screen to cover the entire UI so it needs to be scaled up
    [HarmonyPatch(typeof(WaitScreen), nameof(WaitScreen.Awake))]
    public static class WaitScreen_FixVRScale
    {
        [HarmonyPostfix]
        public static void Postfix(WaitScreen __instance)
        {
            uGUI.main.loading.transform.localScale = new Vector3(3.4f, 3.4f, 3.4f);
        }
    }
    */
}

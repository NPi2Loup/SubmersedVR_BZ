using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    // SpyPenguin HUD pinned to the robot (28/10 user: « figer le hud du
    // spypenguin sur le robot lui même »; confirmed: in front of the robot,
    // body-fixed).
    //
    // While piloting, the player's view is the penguin's aimable camera
    // (SpyPenguin drives SNCameraRoot to cameraTransform every frame), while
    // the uGUI_SpyPenguin overlay (viewfinder border, status, sliders, photo
    // preview) was head-locked in front of the player at the control
    // position. Now the overlay GO becomes a self-contained WorldSpace canvas
    // hung on a DontDestroyOnLoad anchor driven from the penguin's transform
    // each frame: a viewfinder floating in front of the robot, moving with
    // it, body-fixed (the camera servo swings it inside the view, as wanted).
    //
    // Deliberately NOT the loading-screen world pin (failed 28/10): that
    // fought the head-locked canvas's anchor layout. Here the panel gets its
    // own Canvas + an explicit sizeDelta under a plain (non-RectTransform)
    // anchor parent - nothing re-lays it out (same shape as the PDA world
    // attach). The panel is not parented to the penguin itself so it cannot
    // die with it (self-destruct / deconstruction mid-session).
    static class SpyPenguinWorldHud
    {
        // Panel placement relative to the penguin root, in world meters.
        private const float PanelForwardDistance = 1.5f;
        // Fallback only: the panel height is computed at attach as the
        // robot's camera height above its root (« au niveau des yeux du
        // robot », 28/10 user) and stored in panelEyeHeight.
        private const float PanelHeightFallback = 0.9f;

        // 1.3x and 1.0x were both reported too large (28/10 tests) - 0.75x;
        // tunable.
        private const float PanelScaleMultiplier = 0.75f;

        // The fader/flash/border must cover the WHOLE 110 deg VR view, not
        // just the screen canvas rect (~66 deg): stretch them past the
        // canvas (loading-screen overscan trick). 0.85 -> 2.85x the canvas:
        // the rect edges land OUTSIDE the view edges (the 0.65 value left
        // a ~1 deg gap: 28/10 test « le border de droite n'est pas collé au
        // bord de l'écran »).
        private const float FilterFullViewOverscan = 0.85f;

        // The border bands' dedicated plane: PARENTED TO THE VIEW CAMERA
        // (local (0,0,PlaneDistance)) so it is head-locked EXACTLY by
        // hierarchy - no per-frame pose copy to race the game's head-lock
        // (28/10 external review). 1 px = 1 mm (localScale 0.001).
        private const float PlaneDistance = 1.0f;
        // 6000 px = 6 m = ±71.6 deg vertical at 1 m: covers the widest VR
        // headset frustum (~±50-55 deg).
        private const float PlanePxSize = 6000f;
        private const float PlanePxPerMeter = 1000f;

        private static RectTransform bandPlane;
        private static Canvas bandPlaneCanvas;
        private static CanvasGroup bandPlaneGroup;
        // 28/10 TODO bandes #2: the UI/Default shader uses ZTest
        // [unity_GUIZTestMode] (LEqual by default) - at 1 m in front of
        // the penguin camera the cockpit/hull geometry (within ~1 m
        // off-axis) would occlude the bands. A custom material with ZTest
        // Always makes the gradient overlay the world like a viewfinder
        // (the Fader still covers it: canvas sortingOrder, and UI writes
        // no depth).
        private static Material bandZTestMaterial;
        // The main uGUI canvas (the screen canvas the HUD panel lived
        // under): the plane renders BELOW it so the game's Fader (black
        // disconnect overlay) keeps covering the bands.
        private static Canvas screenCanvasRef;
        private static bool renderCheckArmed;
        private static bool renderCheckDone;
        private static bool renderHooked;

        private static RectTransform panel;
        private static Transform anchor;
        private static Canvas addedCanvas;
        private static bool canvasOurs;
        // The HUD GO already carries its own Canvas (tree log: root
        // ",Canvas") - save its original state so Detach restores the
        // head-locked rendering, not a WorldSpace canvas.
        private static RenderMode origRenderMode;
        private static Camera origCanvasWorldCamera;
        private static bool origCanvasEnabled;
        private static uGUI_GraphicRaycaster addedRaycaster;
        private static bool raycasterOurs;
        private static Transform originalParent;
        private static Vector2 origAnchorMin;
        private static Vector2 origAnchorMax;
        private static Vector2 origOffsetMin;
        private static Vector2 origOffsetMax;
        private static Vector3 origLocalPos;
        private static Vector3 origLocalScale;
        private static Quaternion origLocalRot;
        private static int origLayer;
        private static int origSiblingIndex;
        private static bool attached;
        private static float panelEyeHeight;
        private static bool diagLogged;
        private static bool detachDiagLogged;

        // Filter children: the full-screen, mostly transparent overlay
        // (distance fade, viewfinder border, camera flash, camera-
        // unavailable, selfie border). 28/10 user: keep them camera-linked
        // (head-locked, full view) as a permanent "filter" on the world;
        // only the actual HUD (feed, status, sliders, pointer, preview)
        // goes to the robot. Full-screen under the full-screen HUD GO, so
        // reparenting them to the screen canvas keeps them full-screen.
        // (The noise/blur is a SpyPenguinCameraScreenFX OnRenderImage
        // post-effect: camera-linked by nature, untouched.)
        private class FilterChild
        {
            public RectTransform rt;
            public Transform parent;
            public Vector2 anchorMin;
            public Vector2 anchorMax;
            public Vector2 offsetMin;
            public Vector2 offsetMax;
            public Vector3 localPos;
            public Quaternion localRot;
            public Vector3 localScale;
            public int siblingIndex;
        }

        private static readonly System.Collections.Generic.List<FilterChild> filterChildren = new System.Collections.Generic.List<FilterChild>();

        // The viewfinder border bands (BorderLeft/BorderRight): re-parented
        // onto the camera-child plane at attach (see EnsureBorderBands),
        // placed once in camera space (PlaceBorderBands) - 28/10: the
        // screen-canvas plane is oblique/offset relative to the view axis
        // (band check log: corners at -62.7°/+61.7°), and the last
        // on-canvas placement attempts landed the bands at their prefab
        // position instead of the written offsets (external review: use a
        // dedicated camera-child plane + localPosition/sizeDelta writes).
        private class BandChild
        {
            public RectTransform rt;
            public Transform parent;
            public int siblingIndex;
            public Vector2 anchorMin;
            public Vector2 anchorMax;
            public Vector2 offsetMin;
            public Vector2 offsetMax;
            public Vector2 pivot;
            public Vector3 localPos;
            public Quaternion localRot;
            public Vector3 localScale;
            public int layer;
            public UnityEngine.UI.Image img;
            public Material origMaterial;
        }

        private static readonly System.Collections.Generic.List<BandChild> bandChildren = new System.Collections.Generic.List<BandChild>();
        private static RectTransform borderRt;

        // 28/10 user spec: clear central zone, gradient out past the view
        // edge - « un petit effet tunnel qui colle avec la caméra déportée
        // du robot ». 1st calibration 45/56: right band « pas mal mais à
        // étendre », left band not visible -> user spec 40/60 (28/10).
        // NOTE: in XR the nominal camFov is not the rendered frustum -
        // the per-eye view edges come from the stereo projection matrices
        // (logged by LogBandDiag); calibrate against those if the 40/60
        // spec is off.
        private const float BorderInnerAngle = 40f;
        private const float BorderOuterAngle = 60f;

        [HarmonyPatch(typeof(uGUI_SpyPenguin), nameof(uGUI_SpyPenguin.SetPenguin))]
        static class SetPenguin_Patch
        {
            static void Postfix(uGUI_SpyPenguin __instance, SpyPenguin penguin)
            {
                if ((UnityEngine.Object)(object)penguin != null && Settings.SpyPenguinHudOnRobot)
                {
                    Attach(__instance);
                }
                else
                {
                    Detach();
                }
            }
        }

        // The height option must move the CAMERA (the player's view) as
        // well as the panel (28/10 user: « l'option de hauteur doit modifier
        // la hauteur de la caméra ET le point central du HUD »): the view
        // stays centered on the panel. The game ABSOLUTELY assigns
        // cameraTransform -> SNCameraRoot.main.transform in
        // SpyPenguin.LateUpdate (L1046-1047, every frame after the first),
        // so the postfix re-adds the offset right after: self-correcting,
        // no cumulative drift, and it stops on its own when control ends
        // (ReturnUsingPenguin).
        [HarmonyPatch(typeof(SpyPenguin), "LateUpdate")]
        static class CameraHeight_Patch
        {
            static void Postfix(SpyPenguin __instance)
            {
                if (!Settings.SpyPenguinHudOnRobot)
                {
                    return;
                }
                if (!__instance.ReturnUsingPenguin())
                {
                    return;
                }
                float off = Settings.SpyPenguinHudHeightOffset / 100f;
                if (off == 0f)
                {
                    return;
                }
                var rig = SNCameraRoot.main;
                if (rig == null || (UnityEngine.Object)(object)rig == null)
                {
                    return;
                }
                rig.transform.position = rig.transform.position + __instance.transform.up * off;
            }
        }

        // Per-frame driver: the component's own LateUpdate always runs while
        // the main uGUI is alive. Keeps the anchor on the penguin; a
        // destroyed penguin (self-destruct) detaches the panel. GetPenguin
        // is the public accessor for the private activePenguin.
        [HarmonyPatch(typeof(uGUI_SpyPenguin), nameof(uGUI_SpyPenguin.LateUpdate))]
        static class LateUpdate_Patch
        {
            static void Postfix(uGUI_SpyPenguin __instance)
            {
                var p = __instance.GetPenguin();
                if (!attached)
                {
                    // Retry: the first SetPenguin can fire before the HUD
                    // rect is laid out (0x0 -> invisible panel; 28/10: no
                    // HUD on the first control, visible on the second).
                    // Attach as soon as the rect resolves while a penguin
                    // is active and the option is on.
                    if (Settings.SpyPenguinHudOnRobot && (UnityEngine.Object)(object)p != null)
                    {
                        var rt = __instance.transform as RectTransform;
                        // Review 28/10: check BOTH dims - Attach also
                        // rejects height < 100 and would log a warning
                        // every frame otherwise.
                        if (rt != null && rt.rect.width > 100f && rt.rect.height > 100f)
                        {
                            Attach(__instance);
                        }
                    }
                    return;
                }
                if (anchor == null || (UnityEngine.Object)(object)anchor == null)
                {
                    return;
                }
                if ((UnityEngine.Object)(object)p == null)
                {
                    Detach();
                    return;
                }
                var t = p.transform;
                // Body-fixed, in front of the robot, at the view camera's
                // height: the robot's camera height (captured at attach)
                // plus the user's offset - CameraHeight_Patch moves the
                // camera by the same offset, so the panel center stays
                // centered in the view (28/10 user request).
                //
                // Orientation: NO flip (28/10 test: the Y180 flip showed
                // the panel's BACK to the player - UGUI images render
                // mirrored from behind, TMP text is culled: « il est à
                // l'envers, je vois la face arrière », the text-based
                // indicators "missing" on the TV). Identity rotation puts
                // the readable side toward the camera.
                float h = panelEyeHeight + Settings.SpyPenguinHudHeightOffset / 100f;
                anchor.position = t.position + t.up * h + t.forward * PanelForwardDistance;
                anchor.rotation = t.rotation;
                // 28/10 log: BorderRight's rect was unresolved (width < 10)
                // at attach and the one-shot collect skipped it, so the
                // driver re-runs the (idempotent, ~2 children) collect
                // every frame: a band is picked up as soon as it is
                // laid out / the view camera exists.
                EnsureBorderBands(__instance);
                // 28/10 test 13 ROOT CAUSE: SNCameraRoot.main.mainCam is
                // RE-ASSIGNED when piloting starts (two GOs named
                // "MainCamera" in the log) - the plane parented to the
                // attach-time camera was left 3.14 m BELOW the pilot view
                // (bands at -73°, invisible). Re-parent it to the CURRENT
                // mainCam every frame (cheap: 1 reference + parent check).
                EnsurePlaneCamera();
                SyncBandAlpha(__instance);
                LogBandDiag(p);
            }
        }

        private static void Attach(uGUI_SpyPenguin hud)
        {
            if (attached && panel != null && (object)panel != null && panel == hud.transform)
            {
                return;
            }
            if (attached)
            {
                Detach();
            }
            var rt = hud.transform as RectTransform;
            if (rt == null)
            {
                Mod.logger.LogWarning("SpyPenguin world HUD: uGUI_SpyPenguin transform is not a RectTransform, keeping the head-locked panel");
                return;
            }
            // World size: the overlay's current world width on screen
            // (rect px * the head-locked canvas px->world scale). Capture the
            // RESOLVED rect size now: the GO is stretch-anchored under the
            // screen canvas (sizeDelta (0,0)), so changing the anchors to
            // point anchors first would collapse rect to (0,0) and the size
            // read would be zero (first test 28/10: px=(0x0) in the log).
            Vector2 onScreenSize = rt.rect.size;
            if (onScreenSize.x < 100f || onScreenSize.y < 100f)
            {
                // Not laid out yet: stay head-locked, the LateUpdate retry
                // attaches once the rect resolves (first-control case).
                Mod.logger.LogWarning($"SpyPenguin world HUD: rect not laid out yet ({onScreenSize.x:0.#}x{onScreenSize.y:0.#}), retrying");
                return;
            }
            float worldW = rt.rect.width * rt.lossyScale.x;
            // Panel height above the root = the robot's camera height above
            // the root (28/10 user: « au niveau des yeux du robot »).
            var p = hud.GetPenguin();
            panelEyeHeight = PanelHeightFallback;
            if ((UnityEngine.Object)(object)p != null)
            {
                var camT = p.cameraTransform;
                if ((UnityEngine.Object)(object)camT != null)
                {
                    panelEyeHeight = Vector3.Dot(camT.position - p.transform.position, p.transform.up);
                }
            }
            // Save everything Detach restores (stretch-anchored under the
            // main uGUI canvas today).
            originalParent = rt.parent;
            // The screen canvas (for the band plane's sorting order: it
            // must render BELOW this canvas so the Fader covers the bands).
            screenCanvasRef = originalParent != null ? originalParent.GetComponentInParent<Canvas>() : null;
            origAnchorMin = rt.anchorMin;
            origAnchorMax = rt.anchorMax;
            origOffsetMin = rt.offsetMin;
            origOffsetMax = rt.offsetMax;
            origLocalPos = rt.localPosition;
            origLocalRot = rt.localRotation;
            origLocalScale = rt.localScale;
            origLayer = rt.gameObject.layer;
            // SetParent (attach AND detach) always appends the GO to the
            // END of the parent's child list - save the original render
            // order so Detach restores it exactly (28/10: the black Fader
            // ended up above the "Connecting..." screen, hiding its text).
            origSiblingIndex = rt.GetSiblingIndex();
            // Self-contained rect: explicit size + centered anchors (the
            // anchors are meaningless under the plain anchor parent).
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = onScreenSize;
            // Its own WorldSpace canvas (the PDA pattern): rendered by the
            // cameras whose culling mask includes its layer - while
            // piloting that is the main camera sitting on the penguin.
            addedCanvas = rt.GetComponent<Canvas>();
            canvasOurs = addedCanvas == null;
            if (canvasOurs)
            {
                addedCanvas = rt.gameObject.AddComponent<Canvas>();
            }
            else
            {
                // Review 28/10: the HUD GO already has a Canvas - save its
                // original rendering state (restored in Detach).
                origRenderMode = addedCanvas.renderMode;
                origCanvasWorldCamera = addedCanvas.worldCamera;
                origCanvasEnabled = addedCanvas.enabled;
            }
            addedCanvas.renderMode = RenderMode.WorldSpace;
            addedCanvas.worldCamera = null;
            addedCanvas.enabled = true;
            addedRaycaster = rt.GetComponent<uGUI_GraphicRaycaster>();
            raycasterOurs = addedRaycaster == null;
            if (raycasterOurs)
            {
                addedRaycaster = rt.gameObject.AddComponent<uGUI_GraphicRaycaster>();
            }
            // The main camera renders world layers; the main uGUI layer is
            // not guaranteed to be in its culling mask - use the default
            // layer, like the PDA screen.
            SetLayerRecursive(rt, 0);
            if (anchor == null || (UnityEngine.Object)(object)anchor == null)
            {
                var go = new GameObject("SpyPenguinHUDAnchor");
                Object.DontDestroyOnLoad(go);
                anchor = go.transform;
            }
            rt.SetParent(anchor, false);
            panel = rt;
            float pxW = rt.rect.width;
            float s = (pxW > 1f ? worldW / pxW : 0.001f) * PanelScaleMultiplier;
            panel.localScale = Vector3.one * s;
            // Move the full-screen filter children back to the screen canvas
            // (head-locked "filter" on the world, 28/10 user).
            filterChildren.Clear();
            AddFilterChild(hud.fader, true);
            AddFilterChild(hud.cameraFlash, true);
            // The user's « fader »: the viewfinder border (the grain +
            // black-gradient bands). Full-view like the fader/flash: it
            // must frame the whole 110 deg view, not the canvas square
            // (28/10: « trop petit et carré ... plein champ ... full
            // screen sur chaque œil » - the head-locked plane is rendered
            // by the stereo camera into both eyes, so one plane = both).
            AddFilterChild(hud.exploreBorder, true);
            AddFilterChild(hud.cameraUnavailable, false);
            AddFilterChild(hud.selfieBorderManager, false);
            EnsureBorderBands(hud);
            attached = true;
            float worldH = rt.rect.height * s;
            Mod.logger.LogInfo($"SpyPenguin world HUD attached: panel={rt.name} px=({rt.rect.width:0.#}x{rt.rect.height:0.#}) world=({worldW:0.###}x{worldH:0.###}) scale={s:0.#####} eyeH={panelEyeHeight:0.###} originalParent={(originalParent != null ? originalParent.name : "null")} filterHeadLocked=[{string.Join(", ", filterChildren.ConvertAll(f => f.rt.gameObject.name))}]");
            // 28/10 test: the overscan fader was NOT visibly enlarged (a
            // RectMask2D/Mask clipping it?) and the legacy fader came back
            // small: log each filter child's full state at attach AND at
            // first restore, plus the mask components on both canvases.
            foreach (var f in filterChildren)
            {
                Mod.logger.LogInfo($"SpyPenguin filter {f.rt.name}: parent={(f.rt.parent != null ? f.rt.parent.name : "null")} anchors=({f.rt.anchorMin.x:0.##},{f.rt.anchorMin.y:0.##}..{f.rt.anchorMax.x:0.##},{f.rt.anchorMax.y:0.##}) offsets=({f.rt.offsetMin.x:0.#},{f.rt.offsetMin.y:0.#}..{f.rt.offsetMax.x:0.#},{f.rt.offsetMax.y:0.#}) rect=({f.rt.rect.width:0.#}x{f.rt.rect.height:0.#})");
            }
            Mod.logger.LogInfo($"SpyPenguin masks: panel={rt.name} RectMask2D={rt.GetComponent<UnityEngine.UI.RectMask2D>() != null} Mask={rt.GetComponent<UnityEngine.UI.Mask>() != null} | {originalParent.name} RectMask2D={originalParent.GetComponent<UnityEngine.UI.RectMask2D>() != null} Mask={originalParent.GetComponent<UnityEngine.UI.Mask>() != null}");
            // Per-attach geometry (28/10: the second open felt "at ground
            // level" - need the numbers of EVERY attach, not just the first).
            if ((UnityEngine.Object)(object)p != null)
            {
                var camT = p.cameraTransform;
                var camPos = (UnityEngine.Object)(object)camT != null ? camT.position : p.transform.position;
                var panelPos = p.transform.position + p.transform.up * panelEyeHeight + p.transform.forward * PanelForwardDistance;
                // Review 28/10: Unity-null (a destroyed SNCameraRoot.main
                // throws on .fieldOfView).
                var cam = (UnityEngine.Object)(object)SNCameraRoot.main != null ? SNCameraRoot.main.mainCam : null;
                Mod.logger.LogInfo($"SpyPenguin world HUD diag: root=({p.transform.position.x:0.##},{p.transform.position.y:0.##},{p.transform.position.z:0.##}) cam=({camPos.x:0.##},{camPos.y:0.##},{camPos.z:0.##}) panel=({panelPos.x:0.##},{panelPos.y:0.##},{panelPos.z:0.##}) camDist={(panelPos - camPos).magnitude:0.##} camFov={((UnityEngine.Object)(object)cam != null ? cam.fieldOfView : 0f):0.#}");
            }
            // One-shot: what is left on the TV (the panel tree AFTER the
            // filter children were moved out) AND what was moved to
            // head-locked - 28/10: the distance text + several indicators
            // are missing on the TV, find where they live.
            if (!diagLogged)
            {
                diagLogged = true;
                var sb = new System.Text.StringBuilder();
                Tree(panel, 4, sb);
                Mod.logger.LogInfo($"SpyPenguin world HUD tree (TV): {sb}");
                var sb2 = new System.Text.StringBuilder();
                foreach (var f in filterChildren)
                {
                    if (sb2.Length > 0)
                    {
                        sb2.Append(" || ");
                    }
                    sb2.Append(f.rt.name).Append(" => ");
                    Tree(f.rt, 3, sb2);
                }
                Mod.logger.LogInfo($"SpyPenguin head-locked subtrees: {sb2}");
            }
        }

        private static void Tree(Transform t, int depth, System.Text.StringBuilder sb)
        {
            sb.Append(t.name);
            var rt = t as RectTransform;
            if (rt != null)
            {
                sb.Append($"({rt.rect.width:0.#}x{rt.rect.height:0.#})");
            }
            if (t.GetComponent<Canvas>() != null)
            {
                sb.Append(",Canvas");
            }
            if (t.GetComponent<UnityEngine.UI.RectMask2D>() != null)
            {
                sb.Append(",RectMask2D");
            }
            if (t.GetComponent<UnityEngine.UI.Mask>() != null)
            {
                sb.Append(",Mask");
            }
            if (depth > 0)
            {
                sb.Append('[');
                bool any = false;
                foreach (Transform c in t)
                {
                    if (any)
                    {
                        sb.Append(" | ");
                    }
                    any = true;
                    Tree(c, depth - 1, sb);
                }
                sb.Append(']');
            }
        }

        private static void AddFilterChild(Component c, bool fullView)
        {
            if (c == null || (UnityEngine.Object)(object)c == null)
            {
                return;
            }
            var rt = c.transform as RectTransform;
            if (rt == null || rt == panel)
            {
                return;
            }
            foreach (var existing in filterChildren)
            {
                if (existing.rt == rt)
                {
                    return;
                }
            }
            if (!diagLogged)
            {
                Mod.logger.LogInfo($"SpyPenguin filter orig {rt.name}: parent={(rt.parent != null ? rt.parent.name : "null")} anchors=({rt.anchorMin.x:0.##},{rt.anchorMin.y:0.##}..{rt.anchorMax.x:0.##},{rt.anchorMax.y:0.##}) offsets=({rt.offsetMin.x:0.#},{rt.offsetMin.y:0.#}..{rt.offsetMax.x:0.#},{rt.offsetMax.y:0.#}) rect=({rt.rect.width:0.#}x{rt.rect.height:0.#})");
            }
            var f = new FilterChild
            {
                rt = rt,
                parent = rt.parent,
                anchorMin = rt.anchorMin,
                anchorMax = rt.anchorMax,
                offsetMin = rt.offsetMin,
                offsetMax = rt.offsetMax,
                localPos = rt.localPosition,
                localRot = rt.localRotation,
                localScale = rt.localScale,
                siblingIndex = rt.GetSiblingIndex()
            };
            filterChildren.Add(f);
            rt.SetParent(originalParent, false);
            // Full-view coverage: the 110 deg VR view extends far past the
            // screen canvas rect (~66 deg), so stretch the full-cover planes
            // past the canvas (anchors outside 0..1, zero offsets).
            if (fullView)
            {
                float o = FilterFullViewOverscan;
                rt.anchorMin = new Vector2(-o, -o);
                rt.anchorMax = new Vector2(1f + o, 1f + o);
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
            }
        }

        // The border (overscanned to the full view) carries two gradient
        // bands, BorderLeft / BorderRight (~300 px, fixed size). 28/10:
        // they are RE-PARENTED onto the camera-child plane (exact head
        // lock by hierarchy - the on-canvas placement attempts kept
        // landing at the prefab position, external review 28/10) and
        // placed once in camera space (PlaceBorderBands). Full original
        // state saved for Detach. IDEMPOTENT + re-run every driver frame
        // (28/10 log: BorderRight's rect was unresolved on the 1st frame
        // and a one-shot collect skipped it forever - the right band the
        // user saw was the NATIVE one on the screen canvas, not ours):
        // any named band not yet collected is picked up as soon as it
        // exists (no width gate - the name is the criterion).
        private static void EnsureBorderBands(uGUI_SpyPenguin hud)
        {
            var cg = hud.exploreBorder;
            if (cg == null || (UnityEngine.Object)(object)cg == null)
            {
                return;
            }
            var border = cg.transform;
            if (borderRt == null || (object)borderRt == null)
            {
                borderRt = border as RectTransform;
            }
            bool collectedAny = false;
            foreach (Transform c in border)
            {
                var rt = c as RectTransform;
                if (rt == null)
                {
                    continue;
                }
                // Review 28/10: only the two known bands - any other child
                // of Border is left untouched.
                if (rt.name != "BorderLeft" && rt.name != "BorderRight")
                {
                    continue;
                }
                bool already = false;
                foreach (var existing in bandChildren)
                {
                    if (existing.rt == rt)
                    {
                        already = true;
                        break;
                    }
                }
                if (already)
                {
                    continue;
                }
                var cam = (UnityEngine.Object)(object)SNCameraRoot.main != null ? SNCameraRoot.main.mainCam : null;
                if (cam == null || (UnityEngine.Object)(object)cam == null)
                {
                    return;
                }
                if (!EnsureBandPlane(cam.transform))
                {
                    return;
                }
                var b = new BandChild
                {
                    rt = rt,
                    parent = rt.parent,
                    siblingIndex = rt.GetSiblingIndex(),
                    anchorMin = rt.anchorMin,
                    anchorMax = rt.anchorMax,
                    offsetMin = rt.offsetMin,
                    offsetMax = rt.offsetMax,
                    pivot = rt.pivot,
                    localPos = rt.localPosition,
                    localRot = rt.localRotation,
                    localScale = rt.localScale,
                    layer = rt.gameObject.layer
                };
                bandChildren.Add(b);
                collectedAny = true;
                // 28/10 TODO bandes #2: ZTest Always so the cockpit/hull
                // geometry (within ~1 m off-axis) cannot occlude the band.
                b.img = rt.GetComponent<UnityEngine.UI.Image>();
                if (b.img != null)
                {
                    b.origMaterial = b.img.material;
                    if (bandZTestMaterial != null && (object)bandZTestMaterial != null)
                    {
                        b.img.material = bandZTestMaterial;
                    }
                }
                // Per-collection (28/10: a late-collected band - e.g.
                // BorderRight on a 2nd pilot - must log its original
                // state too): mirror detection (localScale.x / Z rotation
                // flip the gradient's dark edge), pivot (the placement
                // assumes 0.5/0.5), clone check (instanceID).
                {
                    var img = rt.GetComponent<UnityEngine.UI.Image>();
                    Mod.logger.LogInfo($"SpyPenguin border band {rt.name}: inst={rt.GetInstanceID()} path={FullPath(rt)} anchors=({rt.anchorMin.x:0.##},{rt.anchorMin.y:0.##}..{rt.anchorMax.x:0.##},{rt.anchorMax.y:0.##}) offsets=({rt.offsetMin.x:0.#},{rt.offsetMin.y:0.#}..{rt.offsetMax.x:0.#},{rt.offsetMax.y:0.#}) pivot=({rt.pivot.x:0.##},{rt.pivot.y:0.##}) rect=({rt.rect.width:0.#}x{rt.rect.height:0.#}) localScale=({rt.localScale.x:0.##},{rt.localScale.y:0.##},{rt.localScale.z:0.##}) rot=({rt.localRotation.eulerAngles.x:0.#},{rt.localRotation.eulerAngles.y:0.#},{rt.localRotation.eulerAngles.z:0.#}) imageType={(img != null ? img.type.ToString() : "null")}");
                }
                rt.SetParent(bandPlane, false);
                SetLayerRecursive(rt, 0);
            }
            if (collectedAny)
            {
                PlaceBorderBands();
            }
        }

        // The bands' plane, PARENTED TO THE VIEW CAMERA: local
        // (0,0,PlaneDistance), identity rotation - head-locked exactly by
        // hierarchy (no per-frame pose copy to race the game's head-lock,
        // external review 28/10). Re-created if the game destroys the
        // camera (level reload).
        private static bool EnsureBandPlane(Transform camT)
        {
            if (bandPlane != null && (object)bandPlane != null)
            {
                if (bandPlane.transform.parent != camT)
                {
                    bandPlane.SetParent(camT, false);
                }
                bandPlane.localPosition = new Vector3(0f, 0f, PlaneDistance);
                bandPlane.localRotation = Quaternion.identity;
                bandPlane.localScale = Vector3.one * 0.001f;
                bandPlane.gameObject.SetActive(true);
                return true;
            }
            var go = new GameObject("SpyPenguinBorderPlane");
            bandPlane = go.AddComponent<RectTransform>();
            bandPlaneCanvas = go.AddComponent<Canvas>();
            bandPlaneCanvas.renderMode = RenderMode.WorldSpace;
            bandPlaneCanvas.worldCamera = null;
            // Render BELOW the main uGUI canvas: the game's Fader (black
            // disconnect overlay, stretched to the full view on that
            // canvas) must keep covering the bands.
            bandPlaneCanvas.overrideSorting = true;
            bandPlaneCanvas.sortingOrder = (screenCanvasRef != null && (object)screenCanvasRef != null) ? screenCanvasRef.sortingOrder - 1 : -1;
            bandPlaneGroup = go.AddComponent<CanvasGroup>();
            if (bandZTestMaterial == null || (object)bandZTestMaterial == null)
            {
                var uiShader = Shader.Find("UI/Default");
                if (uiShader != null)
                {
                    bandZTestMaterial = new Material(uiShader)
                    {
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    bandZTestMaterial.SetFloat("unity_GUIZTestMode", (float)(int)UnityEngine.Rendering.CompareFunction.Always);
                }
            }
            bandPlane.sizeDelta = new Vector2(PlanePxSize, PlanePxSize);
            bandPlane.localScale = Vector3.one * 0.001f;
            bandPlane.SetParent(camT, false);
            bandPlane.localPosition = new Vector3(0f, 0f, PlaneDistance);
            bandPlane.localRotation = Quaternion.identity;
            SetLayerRecursive(go.transform, 0);
            return true;
        }

        // Camera-space placement on the camera-child plane: x =
        // tan(angle) * PlaneDistance, 1 px = 1 mm. Constant for the whole
        // session (the plane follows the camera by hierarchy) - set once
        // at collect, not per frame. Dark edge at BorderOuterAngle (just
        // past the view edge), transparent edge at BorderInnerAngle - the
        // « petit effet tunnel ». The original localScale is KEPT: if the
        // prefab band is mirrored (scale.x < 0), the gradient dark edge
        // flips - the one-shot state log (EnsureBorderBands) reports it.
        private static void PlaceBorderBands()
        {
            float xIn = Mathf.Tan(BorderInnerAngle * Mathf.Deg2Rad) * PlaneDistance * PlanePxPerMeter;
            float xOut = Mathf.Tan(BorderOuterAngle * Mathf.Deg2Rad) * PlaneDistance * PlanePxPerMeter;
            float width = xOut - xIn;
            float centerX = (xOut + xIn) * 0.5f;
            foreach (var b in bandChildren)
            {
                if (b.rt == null || (UnityEngine.Object)(object)b.rt == null)
                {
                    continue;
                }
                bool left = b.rt.name == "BorderLeft";
                b.rt.anchorMin = new Vector2(0.5f, 0.5f);
                b.rt.anchorMax = new Vector2(0.5f, 0.5f);
                b.rt.pivot = new Vector2(0.5f, 0.5f);
                b.rt.localPosition = new Vector3(left ? -centerX : centerX, 0f, 0f);
                b.rt.sizeDelta = new Vector2(width, PlanePxSize);
            }
        }

        // The game animates exploreBorder.alpha every frame (the border
        // fade-in + distance fade) - mirror it onto the plane so the bands
        // fade with the native border (they no longer live under that
        // CanvasGroup).
        private static void SyncBandAlpha(uGUI_SpyPenguin hud)
        {
            if (bandPlaneGroup == null || (object)bandPlaneGroup == null)
            {
                return;
            }
            var cg = hud.exploreBorder;
            bandPlaneGroup.alpha = (cg == null || (object)cg == null) ? 1f : cg.alpha;
        }

        // 28/10 test 13: mainCam is re-assigned when piloting starts -
        // the plane (and its bands) must follow the CURRENT mainCam, not
        // the one captured at collect. Also re-creates the plane if its
        // (old) parent camera was destroyed.
        private static void EnsurePlaneCamera()
        {
            var cam = (UnityEngine.Object)(object)SNCameraRoot.main != null ? SNCameraRoot.main.mainCam : null;
            if (cam == null || (UnityEngine.Object)(object)cam == null)
            {
                return;
            }
            if (bandPlane == null || (object)bandPlane == null)
            {
                EnsureBandPlane(cam.transform);
                return;
            }
            if (bandPlane.transform.parent != cam.transform)
            {
                string old = bandPlane.transform.parent != null ? bandPlane.transform.parent.name + "(inst=" + bandPlane.transform.parent.GetInstanceID() + ")" : "null";
                bandPlane.SetParent(cam.transform, false);
                bandPlane.localPosition = new Vector3(0f, 0f, PlaneDistance);
                bandPlane.localRotation = Quaternion.identity;
                bandPlane.localScale = Vector3.one * 0.001f;
                bandPlane.gameObject.SetActive(true);
                Mod.logger.LogInfo($"SpyPenguin plane re-parented: {old} -> {cam.name}(inst={cam.GetInstanceID()}) - mainCam swapped");
            }
        }

        // One-shot placement diagnostics (28/10, external review): the
        // camera-child plane state, each band's FULL state at placement
        // (instanceID/path - clone detection, pivot, lossyScale, the
        // written localPosition/sizeDelta), the TRUE per-eye view edges
        // from the stereo projection matrices (in XR the nominal camFov is
        // not the rendered frustum), every BorderLeft/BorderRight GO in
        // the scene (a second instance would explain the bands staying at
        // their prefab position), and a render-time snapshot
        // (Canvas.willRenderCanvases - a third-party writer between
        // LateUpdate and render would show up here).
        private static bool bandDiagLogged;

        private static void LogBandDiag(SpyPenguin p)
        {
            if (bandDiagLogged || bandChildren.Count == 0 || bandPlane == null || (object)bandPlane == null)
            {
                return;
            }
            var cam = SNCameraRoot.main != null ? SNCameraRoot.main.mainCam : null;
            if (cam == null || (UnityEngine.Object)(object)cam == null)
            {
                return;
            }
            // Only trust it once the game has copied the penguin camera to
            // the rig (attach can precede that copy on the first frame).
            var camT = p.cameraTransform;
            if ((UnityEngine.Object)(object)camT != null && (cam.transform.position - camT.position).magnitude > 0.5f)
            {
                return;
            }
            bandDiagLogged = true;
            Vector3 cpos = cam.transform.position;
            Vector3 cfwd = cam.transform.forward;
            Vector3 cup = cam.transform.up;
            Vector3 cright = Quaternion.LookRotation(cfwd, cup) * Vector3.right;
            // 28/10: the measured band angles (-73°) were impossible for a
            // camera-child point - log the camera ID/pose AND the plane's
            // parent ID to check they are the SAME object, plus the band
            // corners in WORLD below for a numeric cross-check.
            var planeParent = bandPlane.transform.parent;
            Mod.logger.LogInfo($"SpyPenguin plane: parent={(planeParent != null ? planeParent.name : "null")}(inst={((object)planeParent != null ? planeParent.GetInstanceID() : 0)}) localPos=({bandPlane.localPosition.x:0.###},{bandPlane.localPosition.y:0.###},{bandPlane.localPosition.z:0.###}) localScale={bandPlane.lossyScale.x:0.####} size={bandPlane.sizeDelta.x:0.#}x{bandPlane.sizeDelta.y:0.#} sort={bandPlaneCanvas.sortingOrder} screenCanvasSort={(screenCanvasRef != null && (object)screenCanvasRef != null ? screenCanvasRef.sortingOrder : -999)} alpha={bandPlaneGroup.alpha:0.##} camFov={cam.fieldOfView:0.#} (nominal) cam={cam.name}(inst={cam.GetInstanceID()}) camPos=({cpos.x:0.##},{cpos.y:0.##},{cpos.z:0.##}) camRot=({cam.transform.rotation.eulerAngles.x:0.#},{cam.transform.rotation.eulerAngles.y:0.#},{cam.transform.rotation.eulerAngles.z:0.#})");
            float xIn = Mathf.Tan(BorderInnerAngle * Mathf.Deg2Rad) * PlaneDistance * PlanePxPerMeter;
            float xOut = Mathf.Tan(BorderOuterAngle * Mathf.Deg2Rad) * PlaneDistance * PlanePxPerMeter;
            float width = xOut - xIn;
            float centerX = (xOut + xIn) * 0.5f;
            foreach (var b in bandChildren)
            {
                if (b.rt == null || (UnityEngine.Object)(object)b.rt == null)
                {
                    continue;
                }
                b.rt.ForceUpdateRectTransforms();
                Vector3[] corners = new Vector3[4];
                b.rt.GetWorldCorners(corners);
                Vector3 midMin = (corners[0] + corners[3]) * 0.5f;
                Vector3 midMax = (corners[1] + corners[2]) * 0.5f;
                Mod.logger.LogInfo($"SpyPenguin band {b.rt.name}: inst={b.rt.GetInstanceID()} path={FullPath(b.rt)} active={b.rt.gameObject.activeInHierarchy} anchors=({b.rt.anchorMin.x:0.##},{b.rt.anchorMin.y:0.##}..{b.rt.anchorMax.x:0.##},{b.rt.anchorMax.y:0.##}) pivot=({b.rt.pivot.x:0.##},{b.rt.pivot.y:0.##}) offsets=({b.rt.offsetMin.x:0.#},{b.rt.offsetMin.y:0.#}..{b.rt.offsetMax.x:0.#},{b.rt.offsetMax.y:0.#}) sizeDelta=({b.rt.sizeDelta.x:0.#}x{b.rt.sizeDelta.y:0.#}) localPos=({b.rt.localPosition.x:0.#},{b.rt.localPosition.y:0.#},{b.rt.localPosition.z:0.#}) localScale=({b.rt.localScale.x:0.##},{b.rt.localScale.y:0.##},{b.rt.localScale.z:0.##}) lossyScale={b.rt.lossyScale.x:0.####} written=(centerX={(b.rt.name == "BorderLeft" ? -centerX : centerX):0.#}, width={width:0.#}) midMin=({midMin.x:0.##},{midMin.y:0.##},{midMin.z:0.##}) midMax=({midMax.x:0.##},{midMax.y:0.##},{midMax.z:0.##}) midMin={AngleFromCamera(cpos, cfwd, cright, midMin):0.#}° midMax={AngleFromCamera(cpos, cfwd, cright, midMax):0.#}°");
            }
            // Clone detection: EVERY BorderLeft/BorderRight GO in the
            // scene (a second instance would be the one the user sees).
            var sb = new System.Text.StringBuilder();
            foreach (var rt in Resources.FindObjectsOfTypeAll<RectTransform>())
            {
                if (rt.name != "BorderLeft" && rt.name != "BorderRight")
                {
                    continue;
                }
                bool ours = false;
                foreach (var b in bandChildren)
                {
                    if (b.rt == rt)
                    {
                        ours = true;
                        break;
                    }
                }
                if (sb.Length > 0)
                {
                    sb.Append(" || ");
                }
                sb.Append(rt.name).Append(": inst=").Append(rt.GetInstanceID()).Append(" active=").Append(rt.gameObject.activeInHierarchy).Append(" path=").Append(FullPath(rt));
                if (ours)
                {
                    sb.Append(" [OURS]");
                }
            }
            Mod.logger.LogInfo($"SpyPenguin band instances: {sb}");
            // 28/10 test 13: the canvases are rendered by the
            // ImguiCamera in VR (not mainCam) - accept ANY camera, log
            // who fires (first 2 fires).
            renderCheckArmed = true;
            renderCheckDone = false;
            if (!renderHooked)
            {
                Canvas.willRenderCanvases += OnWillRenderCanvases;
                renderHooked = true;
            }
        }

        // Snapshot at CANVAS RENDER time (28/10 test 13), first 2 fires
        // with ANY camera (in VR the canvases are rendered by the
        // ImguiCamera, not mainCam - log the camera identities + the
        // frustum corners per fire: the pilot-view camera's fire gives
        // the true per-eye view edges; the bands' state at render time
        // catches a third-party writer between LateUpdate and render).
        private static int renderCheckFires;

        private static void OnWillRenderCanvases()
        {
            if (!renderCheckArmed || renderCheckDone)
            {
                return;
            }
            var cam = Camera.current;
            renderCheckFires++;
            if (cam == null || (UnityEngine.Object)(object)cam == null)
            {
                if (renderCheckFires >= 2)
                {
                    UnhookRenderCheck();
                }
                return;
            }
            string camId = cam.name + "(inst=" + cam.GetInstanceID() + ")";
            Vector3 cpos = cam.transform.position;
            Vector3 cfwd = cam.transform.forward;
            Vector3 cup = cam.transform.up;
            Vector3 cright = Quaternion.LookRotation(cfwd, cup) * Vector3.right;
            string frustum = "n/a";
            // P[2,3] (m32) = -1 in perspective, 0 in ortho (review 28/10:
            // P[1,1] is non-zero in BOTH, so it is not an ortho filter).
            if (Mathf.Abs(cam.projectionMatrix[2, 3]) > 0.5f)
            {
                Matrix4x4 invClipToWorld = (cam.projectionMatrix * cam.cameraToWorldMatrix).inverse;
                float aTL = AngleFromCamera(cpos, cfwd, cright, invClipToWorld.MultiplyPoint(new Vector4(-1f, 1f, 0f, 1f)));
                float aTR = AngleFromCamera(cpos, cfwd, cright, invClipToWorld.MultiplyPoint(new Vector4(1f, 1f, 0f, 1f)));
                float aBL = AngleFromCamera(cpos, cfwd, cright, invClipToWorld.MultiplyPoint(new Vector4(-1f, -1f, 0f, 1f)));
                float aBR = AngleFromCamera(cpos, cfwd, cright, invClipToWorld.MultiplyPoint(new Vector4(1f, -1f, 0f, 1f)));
                frustum = $"TL={aTL:0.#}° TR={aTR:0.#}° BL={aBL:0.#}° BR={aBR:0.#}°";
            }
            Mod.logger.LogInfo($"SpyPenguin render-time #{renderCheckFires} cam={camId} eye={cam.stereoTargetEye} {frustum} (camFov={cam.fieldOfView:0.#} nominal)");
            if (renderCheckFires == 1)
            {
                foreach (var b in bandChildren)
                {
                    if (b.rt == null || (UnityEngine.Object)(object)b.rt == null)
                    {
                        continue;
                    }
                    b.rt.ForceUpdateRectTransforms();
                    Vector3[] corners = new Vector3[4];
                    b.rt.GetWorldCorners(corners);
                    Vector3 midMin = (corners[0] + corners[3]) * 0.5f;
                    Vector3 midMax = (corners[1] + corners[2]) * 0.5f;
                    Mod.logger.LogInfo($"SpyPenguin band render-time {b.rt.name}: offsets=({b.rt.offsetMin.x:0.#},{b.rt.offsetMin.y:0.#}..{b.rt.offsetMax.x:0.#},{b.rt.offsetMax.y:0.#}) localPos=({b.rt.localPosition.x:0.#},{b.rt.localPosition.y:0.#},{b.rt.localPosition.z:0.#}) midMin={AngleFromCamera(cpos, cfwd, cright, midMin):0.#}° midMax={AngleFromCamera(cpos, cfwd, cright, midMax):0.#}°");
                }
            }
            if (renderCheckFires >= 2)
            {
                UnhookRenderCheck();
            }
        }

        private static void UnhookRenderCheck()
        {
            renderCheckDone = true;
            if (renderHooked)
            {
                Canvas.willRenderCanvases -= OnWillRenderCanvases;
                renderHooked = false;
            }
        }

        private static string FullPath(Transform t)
        {
            var sb = new System.Text.StringBuilder(t.name);
            while (t.parent != null)
            {
                t = t.parent;
                sb.Insert(0, t.name + "/");
            }
            return sb.ToString();
        }

        private static float AngleFromCamera(Vector3 cpos, Vector3 cfwd, Vector3 cright, Vector3 worldPoint)
        {
            Vector3 to = worldPoint - cpos;
            float d = Vector3.Dot(to, cfwd);
            if (d < 0.01f)
            {
                return Vector3.Dot(to, cright) > 0f ? 180f : -180f;
            }
            Vector3 perp = to - cfwd * d;
            return Mathf.Sign(Vector3.Dot(perp, cright)) * Mathf.Rad2Deg * Mathf.Atan2(perp.magnitude, d);
        }

        // internal: the "SpyPenguin HUD on robot" option toggle detaches.
        internal static void Detach()
        {
            if (attached && (panel == null || (UnityEngine.Object)(object)panel == null))
            {
                attached = false;
                return;
            }
            if (!attached || panel == null)
            {
                return;
            }
            var rt = panel;
            // Restore the filter children to the panel first (the panel is
            // still under the anchor). Ascending original sibling index:
            // SetSiblingIndex is only exact when every earlier-indexed
            // sibling is already restored.
            foreach (var f in filterChildren.OrderBy(x => x.siblingIndex))
            {
                if (f.rt == null || (UnityEngine.Object)(object)f.rt == null)
                {
                    continue;
                }
                f.rt.SetParent(f.parent, false);
                f.rt.SetSiblingIndex(f.siblingIndex);
                f.rt.anchorMin = f.anchorMin;
                f.rt.anchorMax = f.anchorMax;
                f.rt.offsetMin = f.offsetMin;
                f.rt.offsetMax = f.offsetMax;
                f.rt.localPosition = f.localPos;
                f.rt.localRotation = f.localRot;
                f.rt.localScale = f.localScale;
            }
            // Restore the border bands (re-parented to the camera plane
            // while attached, 28/10). Ascending original sibling index:
            // SetSiblingIndex is only exact when every earlier-indexed
            // sibling is already restored (same parent here).
            foreach (var b in bandChildren.OrderBy(x => x.siblingIndex))
            {
                if (b.rt == null || (UnityEngine.Object)(object)b.rt == null)
                {
                    continue;
                }
                b.rt.SetParent(b.parent, false);
                b.rt.SetSiblingIndex(b.siblingIndex);
                b.rt.anchorMin = b.anchorMin;
                b.rt.anchorMax = b.anchorMax;
                b.rt.offsetMin = b.offsetMin;
                b.rt.offsetMax = b.offsetMax;
                b.rt.pivot = b.pivot;
                b.rt.localRotation = b.localRot;
                b.rt.localScale = b.localScale;
                SetLayerRecursive(b.rt, b.layer);
                if (b.img != null && (object)b.img != null)
                {
                    b.img.material = b.origMaterial;
                }
            }
            bandChildren.Clear();
            // The plane stays under the camera (re-used on the next
            // attach) but renders nothing until then.
            if (bandPlane != null && (object)bandPlane != null)
            {
                bandPlane.gameObject.SetActive(false);
            }
            if (renderHooked)
            {
                Canvas.willRenderCanvases -= OnWillRenderCanvases;
                renderHooked = false;
            }
            renderCheckArmed = false;
            renderCheckDone = false;
            renderCheckFires = 0;
            bandDiagLogged = false;
            // First-restore diagnostics (28/10: legacy fader looked small).
            if (!detachDiagLogged)
            {
                detachDiagLogged = true;
                foreach (var f in filterChildren)
                {
                    if (f.rt == null || (UnityEngine.Object)(object)f.rt == null)
                    {
                        continue;
                    }
                    Mod.logger.LogInfo($"SpyPenguin filter restored {f.rt.name}: parent={(f.rt.parent != null ? f.rt.parent.name : "null")} anchors=({f.rt.anchorMin.x:0.##},{f.rt.anchorMin.y:0.##}..{f.rt.anchorMax.x:0.##},{f.rt.anchorMax.y:0.##}) offsets=({f.rt.offsetMin.x:0.#},{f.rt.offsetMin.y:0.#}..{f.rt.offsetMax.x:0.#},{f.rt.offsetMax.y:0.#}) rect=({f.rt.rect.width:0.#}x{f.rt.rect.height:0.#})");
                }
            }
            filterChildren.Clear();
            rt.SetParent(originalParent, false);
            rt.SetSiblingIndex(origSiblingIndex);
            rt.anchorMin = origAnchorMin;
            rt.anchorMax = origAnchorMax;
            rt.offsetMin = origOffsetMin;
            rt.offsetMax = origOffsetMax;
            rt.localPosition = origLocalPos;
            rt.localRotation = origLocalRot;
            rt.localScale = origLocalScale;
            SetLayerRecursive(rt, origLayer);
            if (raycasterOurs && addedRaycaster != null && (object)addedRaycaster != null)
            {
                Object.Destroy(addedRaycaster);
            }
            if (canvasOurs && addedCanvas != null && (object)addedCanvas != null)
            {
                Object.Destroy(addedCanvas);
            }
            else if (addedCanvas != null && (object)addedCanvas != null)
            {
                // Review 28/10: restore the pre-existing canvas state.
                addedCanvas.renderMode = origRenderMode;
                addedCanvas.worldCamera = origCanvasWorldCamera;
                addedCanvas.enabled = origCanvasEnabled;
            }
            addedRaycaster = null;
            addedCanvas = null;
            panel = null;
            attached = false;
            Mod.logger.LogInfo("SpyPenguin world HUD detached (head-locked panel restored)");
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            foreach (Transform c in t)
            {
                SetLayerRecursive(c, layer);
            }
        }
    }
}

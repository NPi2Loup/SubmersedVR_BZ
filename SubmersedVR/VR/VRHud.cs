using System;
using HarmonyLib;
using UnityEngine;
using TMPro;

namespace SubmersedVR
{
    extern alias SteamVRActions;
    extern alias SteamVRRef;
    using SteamVRActions.Valve.VR;
    using System.Collections.Generic;
    using System.Reflection.Emit;

    // Tweaks regarding the HUD of the game
    static class VRHud
    {
        internal static Transform screenCanvas;
        private static Transform overlayCanvas;
        private static Transform hud;

        private static Canvas staticHudCanvas = null;
        // Floor for the HUD canvas scale: file/static values below 0.25 are
        // clamped here so the HUD can never be scaled under the slider minimum.
        private const float MinHudScale = 0.25f;
        private static string lastHudScaleLog = null;
        private static string lastStaticHudLog = null;
        private static string lastHudStateLog = null;
        private static bool uiScaleListenerRegistered = false;
        private static bool menuIdentityLogged = false;
        private static bool overlayScalerLogged = false;

        // One-shot per-scaler audit (27/10): after the uiRig-follow-player
        // fix, which canvases run in Static mode and where they hang from
        // decides whether the StaticTrack re-dirty covers them (PDA "menu
        // dans la tablette", uGUI_BuilderMenu "figé dans le world")
        private static readonly HashSet<uGUI_CanvasScaler> auditedScalers = new HashSet<uGUI_CanvasScaler>();

        internal static void AuditScaler(uGUI_CanvasScaler s)
        {
            if (!auditedScalers.Add(s))
            {
                return;
            }
            var chain = new System.Text.StringBuilder();
            var t = s.transform;
            for (int i = 0; i < 6 && t != null; i++)
            {
                if (i > 0)
                {
                    chain.Append(" <- ");
                }
                chain.Append(t.name);
                t = t.parent;
            }
            var p = s.transform.position;
            Mod.logger.LogInfo($"[ScalerAudit] {s.name} mode={s.mode} vrMode={s.vrMode} dist={s.distance:0.##} worldPos=({p.x:0.##},{p.y:0.##},{p.z:0.##}) active={s.gameObject.activeInHierarchy} parents=[{chain}]");
        }

        // One-shot diagnostic for the "boot different from game" complaint:
        // the main menu UI hangs either off the screenCanvas (which we scale)
        // or its own canvas (uGUI_MainMenu.canvasScaler, which we never touch).
        // If the menu is on its own canvas, the menu can't follow the canvas
        // scale sliders - the perceived size gap between boot and game is then
        // a canvas-identity problem, not a scale problem.
        private static void LogMenuCanvasIdentity()
        {
            if (menuIdentityLogged)
            {
                return;
            }
            var menu = uGUI_MainMenu.main;
            if ((UnityEngine.Object)(object)menu == null || (UnityEngine.Object)(object)screenCanvas == null)
            {
                return;
            }
            menuIdentityLogged = true;
            bool onScreen = menu.transform.IsChildOf(screenCanvas);
            string scalerInfo = menu.canvasScaler != null
                ? $"{menu.canvasScaler.name} ref={menu.canvasScaler.referenceResolution.x:0}x{menu.canvasScaler.referenceResolution.y:0} dist={menu.canvasScaler.distance:0.##}"
                : "null";
            Mod.logger.LogInfo($"Menu canvas identity: onScreenCanvas={onScreen} menuScaler={scalerInfo}");
        }
        // private static OffsetCalibrationTool calibrationTool;

        internal static float HudCanvasScaleX => Mathf.Max(MinHudScale, Settings.HudCanvasScaleX);
        internal static float HudCanvasScaleY => Mathf.Max(MinHudScale, Settings.HudCanvasScaleY);

        public static void ApplyHudScale()
        {
            if ((UnityEngine.Object)(object)screenCanvas != null)
            {
                var scaler = screenCanvas.GetComponent<uGUI_CanvasScaler>();
                if (scaler != null)
                {
                    scaler.SetDirty();
                }
            }
            ApplyStaticHudScale();
            // The loading screen is a child of this canvas: re-apply its
            // compensation so it does not inherit the canvas factor change.
            LoadingScreen_Patch.ApplyScale();
        }

        // uGUI.Update copies MiscSettings.GetUIScale() into uGUI_CanvasScaler.uiScale
        // every frame. Re-dirtying the screen canvas scaler makes the update fixer
        // re-apply the static HUD scale and size with the new UI Scale value.
        private static void OnUiScaleChanged(float scale)
        {
            if ((UnityEngine.Object)(object)screenCanvas == null)
            {
                return;
            }
            screenCanvas.GetComponent<uGUI_CanvasScaler>()?.SetDirty();
        }

        // The canvas reference size is recomputed from public data only. The game's
        // uGUI_CanvasScaler.UpdateFrustum (decompiled) computes it as:
        //   baseScale = min(screenW / refW, screenH / refH)
        //   if (scaleMode > DontScale) baseScale *= uiScale
        //   size      = (screenW / baseScale, screenH / baseScale)
        // with screen = GraphicsUtil.GetScreenSize() and ref = the scaler's public
        // referenceResolution. Reading the scaler's private _width/_height fields
        // through the publicized reference is risky on Mono, so we recompute instead.
        private static void GetCanvasReferenceSize(uGUI_CanvasScaler scaler, out float width, out float height)
        {
            width = -1f;
            height = -1f;
            if ((UnityEngine.Object)(object)scaler == null)
            {
                return;
            }
            Vector2Int screen = GraphicsUtil.GetScreenSize();
            if (screen.x <= 0 || screen.y <= 0 ||
                scaler.referenceResolution.x <= 0f || scaler.referenceResolution.y <= 0f)
            {
                return;
            }
            float baseScale = Mathf.Min((float)screen.x / scaler.referenceResolution.x, (float)screen.y / scaler.referenceResolution.y);
            if (scaler.scaleMode > uGUI_CanvasScaler.ScaleMode.DontScale)
            {
                baseScale *= uGUI_CanvasScaler.uiScale;
            }
            width = (float)screen.x / baseScale;
            height = (float)screen.y / baseScale;
        }

        internal static void ApplyScreenCanvasScale(uGUI_CanvasScaler scaler)
        {
            if ((UnityEngine.Object)(object)scaler == null)
            {
                return;
            }
            GetCanvasReferenceSize(scaler, out float width, out float height);
            if (width <= 0f || height <= 0f)
            {
                return;
            }
            var rt = scaler.transform as RectTransform;
            if (rt == null)
            {
                return;
            }
            // The game's UpdateFrustum sets localScale from MainCamera's
            // frustum, but only on a cache miss (_scaleX != num11). After a
            // world reload with identical inputs (same FOV/distance/screen/
            // uiScale as the menu) the cached write is skipped and whatever
            // transform survived is used: a stale localScale rendered the HUD
            // at the wrong size until a transient uiScale change (dragging
            // the UI Scale slider) forced a cache miss. Write the same value
            // the game would have written (its exact formula: frustum size at
            // `distance` per screen pixel x baseScale) so every run leaves a
            // deterministic transform, then shrink only the rect.
            Camera cam = MainCamera.camera;
            if ((UnityEngine.Object)(object)cam != null)
            {
                Vector2Int s = GraphicsUtil.GetScreenSize();
                if (s.x > 0 && s.y > 0)
                {
                    float halfH = scaler.distance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                    float baseS = Mathf.Min((float)s.x / scaler.referenceResolution.x, (float)s.y / scaler.referenceResolution.y);
                    if (scaler.scaleMode > uGUI_CanvasScaler.ScaleMode.DontScale)
                    {
                        baseS *= uGUI_CanvasScaler.uiScale;
                    }
                    // num5 == num6 algebraically (frustum width / screenW ==
                    // frustum height / screenH): one uniform factor.
                    float ls = 2f * halfH / (float)s.y * baseS;
                    // Guarded write: since the Static re-dirty fix (27/10)
                    // this runs every frame for the screenCanvas; writing an
                    // identical value would mark the RectTransform dirty
                    // (layout rebuild) each frame.
                    var lsVec = new Vector3(ls, ls, ls);
                    if ((rt.localScale - lsVec).sqrMagnitude > 1e-10f)
                    {
                        rt.localScale = lsVec;
                    }
                }
            }
            // The shrunken rect keeps the HUD footprint at the user configured
            // fraction of the view frustum while the content follows the
            // vanilla UI Scale.
            GuardedSetSize(rt, width * HudCanvasScaleX, height * HudCanvasScaleY);
            ApplyHudOffset(rt);
            ApplyStaticHudScale();
            // Re-logged whenever the effective values change (slider moves,
            // canvas factor change, uiScale migration) so A/B tests show up
            // in the log. phys = physical world size of the canvas (rect x
            // accumulated scale): the number that actually determines how big
            // the HUD looks - it must match between world entry and after the
            // (no longer needed) UI Scale drag.
            Camera uiCam = (VRCameraRig.instance != null) ? VRCameraRig.instance.uiCamera : null;
            float physW = rt.rect.width * rt.lossyScale.x;
            float physH = rt.rect.height * rt.lossyScale.y;
            Vector2Int screenSize = GraphicsUtil.GetScreenSize();
            string ctx = uGUI.isMainLevel ? "game" : "menu";
            if (ctx == "menu")
            {
                LogMenuCanvasIdentity();
            }
            string signature = string.Format("ctx={0} canvasX={1:0.##} canvasY={2:0.##} offsetX={3:0.##} offsetY={4:0.##} uiScale={5:0.##} ref={6:0.#}x{7:0.#} screen={8}x{9} phys={10:0.###}x{11:0.###} ls={12:0.######} camFov={13} uiFov={14} dist={15:0.##}",
                ctx,
                HudCanvasScaleX, HudCanvasScaleY, Settings.HudOffsetX, Settings.HudOffsetY, uGUI_CanvasScaler.uiScale, width, height,
                screenSize.x, screenSize.y, physW, physH,
                rt.localScale.x,
                cam != null ? cam.fieldOfView.ToString("0.#") : "?",
                uiCam != null ? uiCam.fieldOfView.ToString("0.#") : "?",
                scaler.distance);
            if (signature != lastHudScaleLog)
            {
                lastHudScaleLog = signature;
                Mod.logger.LogInfo($"HUD scale applied: {signature} vrMode={scaler.vrMode} scaleMode={scaler.scaleMode}");
            }
            LogHudWorldState(rt);
        }

        // Diagnostic for "HUD items disappear when touching the canvas slider":
        // the scale log above is healthy (phys ~1.2 m), so the invisibility is
        // not a scale issue. Log the screenCanvas world state AND the SceneHUD
        // (hud) state together to see if the HUD content is being moved/hidden
        // when the canvas is re-scaled. The subtitles (TalkingHead) live
        // separately, so a screenCanvas-level move would hide the SceneHUD but
        // not the subtitles - matching the reported symptom.
        private static void LogHudWorldState(RectTransform rt)
        {
            string hudState = (UnityEngine.Object)(object)hud != null
                ? $"active={hud.gameObject.activeInHierarchy} parent={(hud.parent != null ? hud.parent.name : "?")} worldPos=({hud.position.x:0.###},{hud.position.y:0.###},{hud.position.z:0.###}) worldScaleX={hud.lossyScale.x:0.###} localPos=({hud.localPosition.x:0.###},{hud.localPosition.y:0.###},{hud.localPosition.z:0.###})"
                : "hud=null";
            string canvasState = $"worldPos=({rt.position.x:0.###},{rt.position.y:0.###},{rt.position.z:0.###}) anchoredPos=({rt.anchoredPosition.x:0.##},{rt.anchoredPosition.y:0.##}) anchorMin=({rt.anchorMin.x:0.##},{rt.anchorMin.y:0.##}) anchorMax=({rt.anchorMax.x:0.##},{rt.anchorMax.y:0.##}) pivot=({rt.pivot.x:0.##},{rt.pivot.y:0.##})";
            string sig = hudState + " || canvas: " + canvasState;
            if (sig != lastHudStateLog)
            {
                lastHudStateLog = sig;
                Mod.logger.LogInfo($"HUD state: {sig}");
            }
        }

        private static void ApplyHudOffset(RectTransform rt)
        {
            // Offsets are in CENTIMETERS: a physical shift of the whole HUD
            // canvas around its 1 m-in-front placement. anchoredPosition
            // cannot be used: the canvas parent is not a RectTransform (the
            // uiCamera / uiRig), so anchoredPosition is in METERS there -
            // raw pixel-scale values flew the HUD out of sight (the
            // "HUD disappears on any non-zero offset" bug).
            var lp = rt.localPosition;
            lp.x = Settings.HudOffsetX / 100f;
            lp.y = Settings.HudOffsetY / 100f;
            if ((rt.localPosition - lp).sqrMagnitude > 1e-10f)
            {
                rt.localPosition = lp;
            }
        }

        // SetSizeWithCurrentAnchors marks the layout dirty even when the
        // size is unchanged; skip it when the rect already has the target
        // size (this runs every frame for the screenCanvas since the
        // Static re-dirty fix)
        private static void GuardedSetSize(RectTransform rt, float targetW, float targetH)
        {
            if (Mathf.Abs(rt.rect.width - targetW) > 0.01f)
            {
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetW);
            }
            if (Mathf.Abs(rt.rect.height - targetH) > 0.01f)
            {
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, targetH);
            }
        }

        private static void ApplyStaticHudScale()
        {
            if (staticHudCanvas == null || (UnityEngine.Object)(object)screenCanvas == null)
            {
                return;
            }
            // The static HUD hosts the same content as the screen canvas and must
            // match the ON-FOOT HUD size: follow the screen canvas' current
            // frustum scale. A fixed base (0.00085 * uiScale) rendered vehicle
            // HUDs 1.6x smaller than the on-foot HUD (0.734 m vs 1.2 m).
            // Ported from the SMB line (2026-10-18): the "Vehicle HUD Scale"
            // option is a multiplier on top of that parity (1.0 = exact parity).
            float s = screenCanvas.transform.localScale.x * Settings.VehicleHudScale;
            if (s <= 0f)
            {
                return;
            }
            var sVec = new Vector3(s, s, s);
            if ((staticHudCanvas.transform.localScale - sVec).sqrMagnitude > 1e-10f)
            {
                staticHudCanvas.transform.localScale = sVec;
            }
            var scaler = screenCanvas.GetComponent<uGUI_CanvasScaler>();
            GetCanvasReferenceSize(scaler, out float width, out float height);
            if (width <= 0f || height <= 0f)
            {
                return;
            }
            var rt = staticHudCanvas.GetComponent<RectTransform>();
            GuardedSetSize(rt, width * HudCanvasScaleX, height * HudCanvasScaleY);
            // NO global HUD offset on the static canvas: its placement
            // (0, -0.1, 1.1) is the user-tuned in-vehicle position (ported
            // from the SMB line); stacking the on-foot offset on top would
            // shift it an extra 10 cm down.
            // Diagnostic for the "HUD disappeared" case the screenCanvas log
            // cannot see: while the player is in a vehicle, OnEnterVehicle
            // reparents the `hud` content onto THIS canvas. Its real physical
            // size, position and whether the HUD content is on it are logged
            // here so a breakage inside a vehicle is captured.
            float uiScale = uGUI_CanvasScaler.uiScale;
            float sPhysW = rt.rect.width * rt.lossyScale.x;
            float sPhysH = rt.rect.height * rt.lossyScale.y;
            bool hudOnStatic = (UnityEngine.Object)(object)hud != null && hud.parent == staticHudCanvas.transform;
            Vector3 sPos = staticHudCanvas.transform.localPosition;
            string sSig = string.Format("sPhys={0:0.###}x{1:0.###} sLs={2:0.######} uiScale={3:0.##} canvasX={4:0.##} canvasY={5:0.##} offset=({6:0.##},{7:0.##}) localPos=({8:0.##},{9:0.##},{10:0.##}) hudOnStatic={11} worldCam={12}",
                sPhysW, sPhysH, rt.lossyScale.x, uiScale, HudCanvasScaleX, HudCanvasScaleY,
                Settings.HudOffsetX, Settings.HudOffsetY, sPos.x, sPos.y, sPos.z,
                hudOnStatic,
                (UnityEngine.Object)(object)staticHudCanvas.worldCamera == null ? "null" : staticHudCanvas.worldCamera.name);
            if (sSig != lastStaticHudLog)
            {
                lastStaticHudLog = sSig;
                Mod.logger.LogInfo($"StaticHUD: {sSig}");
            }
        }

        public static void HideOverlays()
        {
            uGUI.main.overlays.gameObject.SetActive(false);
            uGUI.main.hud.gameObject.SetActive(false);
        }
        // TODO: Hud Distance needs dedicated canvas, since the Pips seem to assume the 1 meter canvas distance.
#if false
        public static float hudDistance = 1.0f;
        public static float HudDistance {
            get {
                return hudDistance;
            }
            set {
                hudDistance = value;
                if (staticHudCanvas == null || screenCanvas == null) {
                    return;
                }
                hud.transform.localPosition = Vector3.forward * (hudDistance - 1.0f);
            }
        }
        public static void OnHudDistanceChanged(float value) {
            HudDistance = value;
        }
#endif

        // Reticle root currently set up (null when the reticle was missing);
        // the VRCameraRig watchdog re-applies the configured mode whenever a
        // new reticle instance appears (save/level load recreates it after
        // VRHud.Setup already ran)
        public static Transform SetupReticle;

        public static void SetupHandReticle(string mode, Camera uiCamera, Transform rightControllerUI)
        {
            if (HandReticle.main == null) return;
            if (mode == HandReticleModes.PointerEnd)
            {
                SetupHandReticleOnPointerDot(uiCamera, rightControllerUI);
            }
            else if (mode == HandReticleModes.TargetInfo)
            {
                SetupHandReticleLaserPointer(uiCamera, rightControllerUI);
            }
            else
            {
                SetupHandReticleOnHand(uiCamera, rightControllerUI);
            }
            SetupReticle = HandReticle.main.transform;
            // Laser pointer modes: the target canvas is projected on the hit
            // point per frame (target info), or the whole reticle is kept
            // readable on the dot (dot)
            HandReticle.main.gameObject.GetOrAddComponent<ReticleBillboard>().enabled = mode == HandReticleModes.TargetInfo || mode == HandReticleModes.PointerEnd;
        }

        public static void SetupHandReticleOnHand(Camera uiCamera, Transform rightControllerUI)
        {
            ReticleSplit.Unsplit();
            // Steal Reticle and attach to the right hand
            var handReticle = HandReticle.main.gameObject.WithParent(rightControllerUI.transform);
            handReticle.GetOrAddComponent<Canvas>().worldCamera = uiCamera;
            handReticle.transform.localEulerAngles = new Vector3(90, 0, 0);
            handReticle.transform.localPosition = ReticleBillboard.AnchorOffset;
            handReticle.transform.localScale = ReticleBillboard.AnchorScale;
        }

        // The whole reticle follows the laser pointer dot; ReticleBillboard
        // keeps it readable per frame
        public static void SetupHandReticleOnPointerDot(Camera uiCamera, Transform rightControllerUI)
        {
            ReticleSplit.Unsplit();
            var rig = VRCameraRig.instance;
            // Unity fake-null: a destroyed LaserPointer / pointerDot passes
            // C#'s `?.` and would throw
            var pointerUI = rig.laserPointerUI;
            var dotGo = (UnityEngine.Object)(object)pointerUI == null ? null : pointerUI.pointerDot;
            var dot = (UnityEngine.Object)(object)dotGo == null ? null : dotGo.transform;
            if (dot == null)
            {
                return;
            }
            var handReticle = HandReticle.main.gameObject.WithParent(dot);
            handReticle.GetOrAddComponent<Canvas>().worldCamera = uiCamera;
            handReticle.transform.localPosition = new Vector3(0, -5, dot.localPosition.z);
            handReticle.transform.localScale = dot.localScale * 2;
        }

        public static void SetupHandReticleLaserPointer(Camera uiCamera, Transform rightControllerUI)
        {
            // Same anchor, size and fixed orientation as hand mode: the
            // reticle stays attached to the tool and rotates with it (legacy
            // layout). ReticleBillboard only projects the split target canvas
            // on the laser hit point per frame.
            var handReticle = HandReticle.main.gameObject.WithParent(rightControllerUI.transform);
            handReticle.transform.localEulerAngles = new Vector3(90, 0, 0);
            handReticle.transform.localPosition = ReticleBillboard.AnchorOffset;
            handReticle.transform.localScale = ReticleBillboard.AnchorScale;
            var canvas = handReticle.GetComponent<Canvas>();
            if (canvas == null)
            {
                canvas = handReticle.AddComponent<Canvas>();
            }
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = uiCamera;
            handReticle.layer = LayerID.UI;
            ReticleSplit.Split(uiCamera);
        }

        public static void OnHandReticleModeChanged(string mode)
        {
            var rig = VRCameraRig.instance;
            if (rig == null || rig.uiCamera == null || rig.rightControllerUI == null)
            {
                return;
            }
            SetupHandReticle(mode, rig.uiCamera, rig.rightControllerUI.transform);
        }



        public static Canvas CreateWorldCanvas(this GameObject go)
        {
            Canvas canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            go.layer = LayerID.UI;
            return canvas;
        }

        public static void Setup(Camera uiCamera, Transform rightControllerUI)
        {
            Mod.logger.LogDebug($"Setting up HUD for {uiCamera.name}");

            Transform prevScreen = screenCanvas;
            screenCanvas = uGUI.main.screenCanvas.gameObject.transform;
            // One identity line per Setup: reveals whether a world (re)load
            // created a new screenCanvas and how it relates to the previous
            // one (stale reference = the old canvas would keep the scale
            // while the new one renders at 100 % until this runs).
            if ((UnityEngine.Object)(object)prevScreen == null)
            {
                Mod.logger.LogInfo($"screenCanvas identity: {screenCanvas.name} #{screenCanvas.GetInstanceID()} (first)");
            }
            else if (!ReferenceEquals(prevScreen, screenCanvas))
            {
                Mod.logger.LogInfo($"screenCanvas identity: {screenCanvas.name} #{screenCanvas.GetInstanceID()} (replaced {prevScreen.name} #{prevScreen.GetInstanceID()})");
            }
            overlayCanvas = uGUI.main.overlays.gameObject.transform.parent;
            hud = uGUI.main.hud.transform;

            if (staticHudCanvas == null)
            {
                var uiRig = VRCameraRig.instance.uiRig.transform;
                var go = new GameObject("StaticHUDCanvas").WithParent(uiRig);
                staticHudCanvas = go.CreateWorldCanvas();
                var rt = go.GetComponent<RectTransform>();
                go.transform.localScale = screenCanvas.localScale;
                rt.sizeDelta = screenCanvas.GetComponent<RectTransform>().sizeDelta;
                rt.anchoredPosition = screenCanvas.GetComponent<RectTransform>().anchoredPosition;
                // SN1 parity: centered on the player. Ported placement from
                // the SMB line (2026-10-18, user-tuned): (0, -0.1, 1.1) under
                // the uiRig - 10 cm below the optical axis, 10 cm deeper than
                // the screen canvas (the original +0.1 m y offset rendered
                // too high; the plain 1 m position rendered on the screen
                // canvas plane).
                go.transform.localPosition = new Vector3(0f, -0.1f, 1.1f);
                go.transform.localRotation = Quaternion.identity;
            }
            staticHudCanvas.worldCamera = uiCamera;

            screenCanvas.SetParent(uiCamera.transform, true);
            overlayCanvas.SetParent(uiCamera.transform, true);

            // NO localScale pin on the screenCanvas: the game's UpdateFrustum
            // owns it (frustum-based) but only writes it on a cache miss. A
            // pinned value here survived the cache hit after each world reload
            // and rendered the HUD at the wrong size until a transient
            // uiScale drag forced a cache miss (the "HUD too small until you
            // touch UI Scale" bug). The canvas size is driven by
            // ApplyScreenCanvasScale (rect shrink + explicit localScale).
            overlayCanvas.transform.localScale = new Vector3(0.00032f, 0.00032f, 0.00032f);
            // staticHudCanvas keeps the screenCanvas scale copied at creation;
            // ApplyStaticHudScale syncs it with the frustum scale every update.
            if (!overlayScalerLogged)
            {
                overlayScalerLogged = true;
                Mod.logger.LogInfo($"overlayCanvas has scaler: {overlayCanvas.GetComponent<uGUI_CanvasScaler>() != null}");
            }

            SetupHandReticle(Settings.HandReticleMode, uiCamera, rightControllerUI);
            Settings.HandReticleModeChanged -= OnHandReticleModeChanged;
            Settings.HandReticleModeChanged += OnHandReticleModeChanged;

            if (!uiScaleListenerRegistered)
            {
                uiScaleListenerRegistered = true;
                uGUI_CanvasScaler.AddUIScaleListener(OnUiScaleChanged);
            }

            WristHud.Setup();

            screenCanvas.GetComponent<uGUI_CanvasScaler>()?.SetDirty();
            screenCanvas.GetComponentsInChildren<uGUI_CanvasScaler>().ForEach(cs => cs.SetDirty());
            ApplyHudScale();
            MiscSettings.cameraBobbing = VROptions.enableCinematics;

            LogMenuCanvasIdentity();
        }

        public static void OnEnterVehicle()
        {
            var player = Player.main;
            if (player != null)
            {
                hud.SetParent(staticHudCanvas.transform, false);
            }
        }

        public static void OnExitVehicle()
        {
            hud.SetParent(screenCanvas, false);
        }
    }

    static class WristHud
    {
        //private static TransformOffset wristOffset = new TransformOffset(new Vector3(-0.079f, 0.148f, -0.158f), new Vector3(350.494f, 88.400f, 244.161f));
        //This works for Valve Index
        private static TransformOffset wristOffset = new TransformOffset(new Vector3(-0.016f, 0.123f, -0.128f), new Vector3(15.494f, 74.4f, 245.161f));
        //private static TransformOffset wristOffset = new TransformOffset(new Vector3(-0.044f, 0.16f, -0.158f), new Vector3(15.494f, 88.400f, 244.161f));
        private static GameObject wristTarget;
        private static Canvas canvas;
        private static CanvasGroup canvasGroup;

        // Cached Values
        private static Transform hudContent;
        private static Transform uiCamera;
        private static Transform cachedIndexTip;
        private static FMODAsset turnOnSound;
        private static FMODAsset turnOffSound;

        // State
        public static bool isHudOn = true;
        private static bool touchingWrist = false;
        private static bool prevTouchingWrist = false;

        //public static GameObject pointerDot;
        public static TextMeshProUGUI entry;

        public static string AdjustHUD(float pX, float pY, float pZ, float aX, float aY, float aZ)
        {
            WristHud.wristOffset = new TransformOffset(new Vector3(WristHud.wristOffset.Pos.x + (pX/1000), WristHud.wristOffset.Pos.y + (pY/1000), WristHud.wristOffset.Pos.z + (pZ/1000)), new Vector3(WristHud.wristOffset.Angles.x + aX, WristHud.wristOffset.Angles.y + aY, WristHud.wristOffset.Angles.z + aZ));
            WristHud.wristOffset.Apply(WristHud.wristTarget.transform);
            return $"AdjustHUD\npX={WristHud.wristOffset.Pos.x.ToString("0.000")}\npY={WristHud.wristOffset.Pos.y.ToString("0.000")}\npZ={WristHud.wristOffset.Pos.z.ToString("0.000")}\naX={WristHud.wristOffset.Angles.x}\naY={WristHud.wristOffset.Angles.y}\naZ={WristHud.wristOffset.Angles.z}";
        }
        public static FMODAsset CreateFMODAsset(string eventPath)
        {
            FMODAsset asset = ScriptableObject.CreateInstance<FMODAsset>();
            asset.path = eventPath;
            return asset;
        }

        // Create Wrist World Canvas
        public static void Setup()
        {
            var rig = VRCameraRig.instance;
            uiCamera = rig.uiCamera.transform;
            hudContent = uGUI.main.hud.transform.GetChild(0);

            if (wristTarget == null)
            {
                wristTarget = new GameObject("WristTarget").WithParent(rig.leftControllerUI).ResetTransform();
                var wristCanvasGo = new GameObject("WristCanvas").WithParent(wristTarget).ResetTransform();
                canvas = wristCanvasGo.CreateWorldCanvas();
                canvasGroup = wristCanvasGo.AddComponent<CanvasGroup>();
                wristCanvasGo.transform.localScale = new Vector3(0.0004f, 0.0004f, 0.0004f);
                wristOffset.Apply(wristTarget.transform);

                GameObject obj = UnityEngine.Object.Instantiate(ErrorMessage.main.prefabMessage);
                entry = obj.GetComponent<TextMeshProUGUI>();
                entry.rectTransform.SetParent(wristCanvasGo.transform, false);
                //obj.SetActive(true);
                obj.layer = LayerMask.NameToLayer("UI");
                HideForScreenshots h = obj.AddComponent<HideForScreenshots>();
                h.type = HideForScreenshots.HideType.HUD;
                entry.horizontalAlignment = TMPro.HorizontalAlignmentOptions.Center;
                entry.enabled = false;
                entry.transform.localPosition = new Vector3(0f, 0.1f, 0f);
                entry.transform.localScale = new Vector3(0.66f, 1f, 1f);
                UpdateWristText();

/*
                Material newMaterial = new Material(ShaderManager.preloadedShaders.DebugDisplaySolid);
                newMaterial.SetColor(ShaderPropertyID._Color, Color.cyan);

                // Setup PointerDot at the end
                pointerDot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                pointerDot.transform.localScale = new Vector3(0.03f, 0.03f, 0.03f);
                pointerDot.transform.SetParent(wristCanvasGo.transform, false);
                //pointerDot.transform.parent = wristCanvasGo.transform;
                LaserPointer.Destroy(pointerDot.GetComponent<SphereCollider>());
                pointerDot.GetComponent<Renderer>().material = newMaterial;
                pointerDot.SetActive(true);
                //wristDotOffset.Apply(pointerDot.transform);
*/

            //laserPointer = new GameObject(nameof(laserPointer)).WithParent(wristTarget).AddComponent<LaserPointer>();
            //laserPointer.gameObject.SetActive(true);

                //var cube = GameObject.CreatePrimitive(PrimitiveType.Cube).WithParent(wristTarget).ResetTransform();
                //cube.GetComponent<MeshRenderer>().sharedMaterial.color = new Color(0,1,1,1);
/*
                var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube).WithParent(wristTarget).ResetTransform();
                //primitive.position = pos;
                primitive.transform.localScale = Vector3.one * .01f;
                primitive.name = "HI";
                primitive.SetActive(true);
            
                var renderer = primitive.GetComponent<MeshRenderer>();
                renderer.material.SetColor("_Color", Color.red);
*/
            }

            Settings.PutBarsOnWristChanged -= OnPutBarsOnHandChanged;
            Settings.PutBarsOnWristChanged += OnPutBarsOnHandChanged;
            Toggle(Settings.PutBarsOnWrist);

            turnOnSound = CreateFMODAsset("event:/tools/flashlight/turn_on");
            turnOffSound = CreateFMODAsset("event:/tools/flashlight/turn_off");
        }

        static void LogDescendants(Transform transform, int level) 
        {  
            foreach(Transform child in transform) {
                Mod.logger.LogInfo($"{new String('-', level)}{child.name}");
                LogDescendants(child, level + 1);   
            }
        }

        public static void UpdateWristText()
        {
            entry.text = isHudOn ? "HUD ON" : "HUD OFF";
        }
        public static Transform GetIndexFingerTip()
        {
            if (cachedIndexTip != null)
            {
                return cachedIndexTip;
            }
            var animator = Player.main?.playerAnimator;
            //animator.enabled = false;
            if (animator is Animator anim)
            {
                // TODO: Test this
                //LogDescendants(anim.transform, 1) ;
                //IK disabled
                var tip = anim.transform.Find("export_skeleton/head_rig/neck/chest/clav_R/clav_R_aim/shoulder_R/hand_R/hand_R_point_base/hand_R_point_mid/hand_R_point_tip_rig");
                if(tip == null)
                {
                    //IK enabled
                    tip = anim.transform.Find("export_skeleton/head_rig/neck/chest/clav_R/clav_R_aim/shoulder_R/elbow_R/hand_R/hand_R_point_base/hand_R_point_mid/hand_R_point_tip_rig");
                }
                if (tip != null)
                {
                    cachedIndexTip = tip;
                    return tip;
                }
            }
            return null;
        }

        public static void OnPutBarsOnHandChanged(bool isOn)
        {
            Toggle(isOn);
        }


        public static void OnUpdate()
        { 
            if (!uGUI.isMainLevel)
            {
                return;
            }
            var camPos = uiCamera.transform.position;
            var worldRigPos = VRCameraRig.instance.rigParentTarget.position;
            var wristPos = wristTarget.transform.position + wristTarget.transform.right * 0.05f + wristTarget.transform.up * 0.05f ; //centered on the display rather than the corner

            Vector3 wristDir = wristTarget.transform.TransformDirection(Vector3.forward);
            Vector3 toCam = (wristPos - camPos).normalized;

            float wristCamDot = Vector3.Dot(wristDir, toCam);
            bool isFacingCamera = wristCamDot > 0.1f;
            //DebugPanel.Show($"dot = {wristCamDot} <= {wristDir}, {toCam}, {camPos}");
            canvasGroup.alpha = Mathf.Max(wristCamDot, 0.0f);

            if (isFacingCamera && GetIndexFingerTip() is Transform indexTip)
            {
                entry.enabled = true;
                var uiIndexPos = indexTip.position - worldRigPos;
                var wristDistance = Vector3.Distance(uiIndexPos, wristPos);
                //DebugPanel.Show($"wristDistance = {wristDistance} <= uiPos{uiIndexPos}, {wristPos} uiIndexPos = {indexTip.transform.position} {indexTip.localPosition} {worldRigPos}", true);
                const float threshold = 0.05f;
                touchingWrist = wristDistance < threshold;
                if (touchingWrist && !prevTouchingWrist)
                {
                    isHudOn = !isHudOn;
                    UpdateWristText();
                    Utils.PlayFMODAsset(isHudOn ? turnOnSound : turnOffSound);
                    HapticsVR.PlayHaptics(0.0f, 0.1f, 10f, 0.8f, false, true, false);   
                }
                prevTouchingWrist = touchingWrist;
            }
        }

        public static void Toggle(bool isOn)
        {
            if (canvas == null)
            {
                Setup();
            }

            var barsPanel = uGUI.main.barsPanel;
            if (isOn)
            {
                // Move to wrist
                Mod.logger.LogDebug("Turning WristHud on");
                barsPanel.WithParent(canvas.transform).ResetTransform();
                barsPanel.GetComponent<RectTransform>().pivot = new Vector2(0, 0);
                //Switched this from LateUpdateLast to CanvasFirst because the VRHands calculations occur in LateUpdate
                //and were happening after the WristHud OnUpdate so finger position was being read as the default animated finger
                //position, not the user overridden VRHands finger position. CanvasFirst would generally be too late for rendering updates,
                //but since it's just a toggle, it should be fine
                ManagedUpdate.Subscribe(ManagedUpdate.Queue.CanvasFirst, new ManagedUpdate.OnUpdate(OnUpdate));
            }
            else
            {
                // Move back
                Mod.logger.LogDebug("Turning WristHud off");
                barsPanel.transform.SetParent(hudContent.transform, false);
                barsPanel.GetComponent<RectTransform>().pivot = new Vector2(0.5f, 0.5f);
                barsPanel.GetComponent<RectTransform>().anchoredPosition = new Vector2(0.0f, 0.0f);
                ManagedUpdate.Unsubscribe(ManagedUpdate.Queue.CanvasFirst, new ManagedUpdate.OnUpdate(OnUpdate));
                isHudOn = true;
                UpdateWristText();
                entry.enabled = false;
            }

        }
    }

    #region Patches

    //Handler for Exosuit entering only
    [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.OnPilotModeBegin))]
    public static class SetHudStaticInVehicles
    {
        public static void Postfix(Vehicle __instance)
        {
            Mod.logger.LogInfo("Vehicle.OnPilotModeBegin");
            // TODO: How to check for SeaTruck?
            // BZ has no Seamoth (only prawn suit + SeaTruck + hoverbike),
            // so Exosuit is the only Vehicle subclass that needs the
            // static canvas here; SeaTruck uses its own StartPiloting/
            // StopPiloting handlers below.
            if (__instance is Exosuit)
            {
                VRHud.OnEnterVehicle();
            }
        }
    }

    //Handler for Exosuit exiting only
    [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.OnPilotModeEnd))]
    public static class ResetHudStaticInVehicles
    {
        public static void Postfix(Vehicle __instance)
        {
            Mod.logger.LogInfo("Vehicle.OnPilotModeEnd {__instance is Exosuit}");
            if (__instance is Exosuit)
            {
                VRHud.OnExitVehicle();
                //This moves player off of the top of the suit after exiting
                Player.main.transform.position = Player.main.transform.position + SNCameraRoot.main.transform.forward * -2.5f;
                Player.main.transform.localPosition += new Vector3(0.0f, 0.5f, 0.0f);
            }
        }
    }

    //handler for Seatruck only entering the docking bay
    [HarmonyPatch(typeof(Dockable), nameof(Dockable.OnDockingComplete))]
    public static class ResetHudStaticWhenDocked
    {
        public static void Postfix(Dockable __instance)
        {
            Mod.logger.LogInfo("Dockable.OnDockingComplete");
            if(__instance.truckMotor)
            {
                VRHud.OnExitVehicle();      
            }
       }
    }

    //handler for Seatruck only exiting the docking bay
    [HarmonyPatch(typeof(Dockable), nameof(Dockable.OnUndockingComplete))]
    public static class ResetHudStaticWhenUndocked
    {
        public static void Postfix(Dockable __instance)
        {
            Mod.logger.LogInfo("Dockable.OnUndockingComplete");
            if(__instance.truckMotor)
            {
                VRHud.OnEnterVehicle();   
            }   
        }
    }

    //handler for Seatruck only entering
    [HarmonyPatch(typeof(SeaTruckMotor), nameof(SeaTruckMotor.StartPiloting))]
    static class SetHudStaticInSeaTrucker
    {
        public static void Postfix()
        {
            Mod.logger.LogInfo("SeaTruckMotor.StartPiloting");
            VRHud.OnEnterVehicle();
        }
    }

    //handler for Seatruck only exiting
    [HarmonyPatch(typeof(SeaTruckMotor), nameof(SeaTruckMotor.StopPiloting))]
    static class ResetHudStaticInSeaTrucker
    {
        public static void Prefix(SeaTruckMotor __instance, bool waitForDocking = false, bool forceStop = false, bool skipUnsubscribe = false, bool immediate = false, bool forceGetupAnimation = false)
        {
            //Mod.logger.LogInfo($"SeaTruckMotor.StopPiloting waitForDocking = {waitForDocking} forceStop = {forceStop} skipUnsubscribe = {skipUnsubscribe} immediate = {immediate} forceGetupAnimation = {forceGetupAnimation}");
            //Doing this check because SeaTruckMotor.StopPiloting gets called many times at startup for an unknown reason
            if (__instance.piloting) 
            {
                Mod.logger.LogInfo($"SeaTruckMotor.StopPiloting");
                VRHud.OnExitVehicle();
            }
        }
    }


    [HarmonyPatch(typeof(uGUI_PlayerSleep), nameof(uGUI_PlayerSleep.Start))]
    public static class ScaleSleep
    {
        public static void Postfix(uGUI_PlayerSleep __instance)
        {
             __instance.blackOverlay.transform.localScale = new Vector3(10f, 10f, 10f);
        }
    }

    [HarmonyPatch(typeof(uGUI_PlayerDeath), nameof(uGUI_PlayerDeath.Start))]
    public static class ScaleDeath
    {
        public static void Postfix(uGUI_PlayerDeath __instance)
        {
             __instance.blackOverlay.transform.localScale = new Vector3(10f, 10f, 10f);
        }
    }

    [HarmonyPatch(typeof(uGUI_Overlays), nameof(uGUI_Overlays.Awake))]
    public static class ScaleOverlays
    {
        public static void Postfix(uGUI_Overlays __instance)
        {
            __instance.gameObject.transform.localScale = new Vector3(10f, 10f, 10f);
        }
    }

    [HarmonyPatch(typeof(EndCreditsManager), nameof(EndCreditsManager.Update))]
    public static class ScaleEndCredits
    {
        public static void Postfix(EndCreditsManager __instance)
        {
             __instance.gameObject.transform.localScale = new Vector3(0.001f, 0.001f, 0.001f);
        }
    }
/*
    [HarmonyPatch(typeof(uGUI_ExpansionIntro), nameof(uGUI_ExpansionIntro.Start))]
    public static class ScaleuGUI_ExpansionIntro
    {
        public static void Postfix(uGUI_ExpansionIntro __instance)
        {
             __instance.gameObject.transform.localScale = new Vector3(1.4f, 1.4f, 1.4f);
        }
    }
*/

    [HarmonyPatch(typeof(uGUI_Pings), nameof(uGUI_Pings.IsVisibleNow))]
    public static class HidePingsWhenHudOff
    {
        public static void Postfix(ref bool __result)
        {
            __result &= WristHud.isHudOn;
        }
    }

    [HarmonyPatch(typeof(uGUI_DepthCompass), nameof(uGUI_DepthCompass.GetDepthInfo))]
    public static class HideDepthCompassWhenHudOff
    {
        public static void Postfix(ref uGUI_DepthCompass.DepthMode __result)
        {
            if (!WristHud.isHudOn)
            {
                __result = uGUI_DepthCompass.DepthMode.None;
            }
        }
    }

    [HarmonyPatch(typeof(uGUI_DepthCompass), nameof(uGUI_DepthCompass.IsCompassEnabled))]
    public static class HideDepthCompassCompassWhenHudOff
    {
        public static void Postfix(ref bool __result)
        {
            if (!WristHud.isHudOn)
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(uGUI_PinnedRecipes), nameof(uGUI_PinnedRecipes.GetMode))]
    public static class HidePinnedRecipesWhenHudOff
    {
        public static void Postfix(ref uGUI_PinnedRecipes.Mode __result)
        {
            if (!WristHud.isHudOn)
            {
                __result = uGUI_PinnedRecipes.Mode.Off;
            }
        }
    }

    [HarmonyPatch(typeof(uGUI_PowerIndicator), nameof(uGUI_PowerIndicator.IsPowerEnabled))]
    public static class HidePowerWhenHudOff
    {
        public static void Postfix(ref bool __result)
        {
            __result &= WristHud.isHudOn;
        }
    }

    [HarmonyPatch(typeof(uGUI_SeamothHUD), nameof(uGUI_SeamothHUD.Update))]
    public static class HideSeamothHudWhenHudOff
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var m = new CodeMatcher(instructions);
            m.MatchForward(true, new CodeMatch[] {
                new CodeMatch(OpCodes.Stloc_3),
            }).Advance(1).Insert(new CodeInstruction[] {
                new CodeInstruction(OpCodes.Ldloc_3),
                CodeInstruction.LoadField(typeof(WristHud), nameof(WristHud.isHudOn)),
                new CodeInstruction(OpCodes.And),
                new CodeInstruction(OpCodes.Stloc_3),
            });
            return m.InstructionEnumeration();
        }
    }

    [HarmonyPatch(typeof(uGUI_ExosuitHUD), nameof(uGUI_ExosuitHUD.Update))]
    public static class HideExosuitHudWhenHudOff
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var m = new CodeMatcher(instructions);
            m.MatchForward(true, new CodeMatch[] {
                new CodeMatch(OpCodes.Stloc_3),
            }).Advance(1).Insert(new CodeInstruction[] {
                new CodeInstruction(OpCodes.Ldloc_3),
                CodeInstruction.LoadField(typeof(WristHud), nameof(WristHud.isHudOn)),
                new CodeInstruction(OpCodes.And),
                new CodeInstruction(OpCodes.Stloc_3),
            });
            return m.InstructionEnumeration();
        }
    }

    //Make the pause menu a more comfortable scale
    [HarmonyPatch(typeof(IngameMenu), nameof(IngameMenu.Update))]
    class IngameMenu_Scale_Fixer
    {
        public static void Postfix(IngameMenu __instance)
        {
            __instance.transform.localScale = new Vector3(0.0013f , 0.0013f, 0.0013f);
            //__instance.transform.localPosition = SNCameraRoot.main.transform.forward * 1.5f;
       }
    }
    
    //Make the builder menu a more comfortable scale
    [HarmonyPatch(typeof(uGUI_BuilderMenu), nameof(uGUI_BuilderMenu.Update))]
    class uGUI_BuilderMenu_Scale_Fixer
    {
        public static void Postfix(uGUI_BuilderMenu __instance)
        {
            __instance.transform.localScale = new Vector3(0.0013f , 0.0013f, 0.0013f);
        }
    }
    
    //These next two functions eliminate the "squashed" HUD UI that comes from enabling XRSettings
    //by temporarily turning the setting off during execution
    [HarmonyPatch(typeof(uGUI_CanvasScaler), nameof(uGUI_CanvasScaler.UpdateFrustum))]
    static class uGUI_CanvasScalerFrustum_Fixer
    {
        public static bool Prefix()
        {
            XRSettingsEnabled.isEnabled = false;
            return true;
        }
        public static void Postfix()
        {
            XRSettingsEnabled.isEnabled = true;
        }
    }

    [HarmonyPatch(typeof(uGUI_CanvasScaler), nameof(uGUI_CanvasScaler.UpdateFrustum))]
    static class uGUI_CanvasScalerHudScale_Fixer
    {
        // Diagnostic: the signature dedup in ApplyScreenCanvasScale hides
        // re-runs with an identical signature. Count the first UpdateFrustum
        // runs of the screenCanvas in the main level to see whether the
        // scaler re-runs at world entry at all, and when.
        private static int mainLevelRunCount = 0;

        public static void Postfix(uGUI_CanvasScaler __instance)
        {
            VRHud.AuditScaler(__instance);
            if (VRHud.screenCanvas == null || !ReferenceEquals(__instance.transform, VRHud.screenCanvas))
            {
                return;
            }
            if (uGUI.isMainLevel && mainLevelRunCount < 8)
            {
                mainLevelRunCount++;
                Mod.logger.LogInfo($"screenCanvas frustum run #{mainLevelRunCount} t={Time.time:0.##}");
            }
            VRHud.ApplyScreenCanvasScale(__instance);
        }
    }

    // Builder-menu tracking (27/10): the game's Static mode places a canvas
    // ONCE (when dirty) at uiCamera.position + uiCamera.forward * distance,
    // then leaves it fixed in the world. In BZ the uiCamera follows the
    // player (reticle fix), so a placed-once panel is left behind as the
    // player walks ("menu de construction figé dans le world").
    //
    // Scoped to uGUI_BuilderMenu ONLY (27/10 user): the broad version
    // tracked every Static canvas and broke the HUD and the loading screen.
    // The builder menu is the only panel that stays open while walking; the
    // in-game/options menus are fine untracked (the game is paused while
    // they are open, so a placed-once panel stays in front).
    //
    // Mechanism: the game places once (position + rotation = uiCamera at
    // that moment); we capture the placement as a FIXED world offset from
    // the ui camera (prefix records isDirty before the game clears it),
    // then per frame re-apply ONLY the position (translation follow). The
    // rotation never follows the head (head-locked panels were unreadable
    // at the edges - user: "suivre le player mais pas la tête").
    [HarmonyPatch(typeof(uGUI_CanvasScaler), nameof(uGUI_CanvasScaler.OnUpdate))]
    static class uGUI_CanvasScalerStaticTrack_Fixer
    {
        class Track
        {
            public Vector3 offset;
            public bool hasPlacement;
            public bool wasDirty;
        }

        static readonly Dictionary<uGUI_CanvasScaler, Track> tracks = new Dictionary<uGUI_CanvasScaler, Track>();

        static bool IsTrackedScaler(uGUI_CanvasScaler __instance)
        {
            return __instance._active
                && __instance.currentMode == uGUI_CanvasScaler.Mode.Static
                && __instance.gameObject.GetComponent<uGUI_BuilderMenu>() != null;
        }

        public static void Prefix(uGUI_CanvasScaler __instance)
        {
            if (!IsTrackedScaler(__instance))
            {
                return;
            }
            Track t;
            if (!tracks.TryGetValue(__instance, out t))
            {
                t = new Track();
                tracks.Add(__instance, t);
            }
            // The game clears isDirty inside OnUpdate when it places; the
            // pre-update state tells the postfix a fresh placement happened
            t.wasDirty = __instance.isDirty;
        }

        public static void Postfix(uGUI_CanvasScaler __instance)
        {
            if (!IsTrackedScaler(__instance))
            {
                return;
            }
            Track t;
            if (!tracks.TryGetValue(__instance, out t))
            {
                return;
            }
            var uiCam = ManagedCanvasUpdate.GetUICamera();
            if (uiCam == null)
            {
                return;
            }
            var rt = __instance.rectTransform;
            if (t.wasDirty)
            {
                // The game just (re)placed the canvas: capture the placement
                // as a fixed world offset from the ui camera
                t.offset = rt.position - uiCam.transform.position;
                t.hasPlacement = true;
            }
            else if (t.hasPlacement)
            {
                // Translation follow only: keep the panel with the player
                // (the ui camera), the captured rotation stays world-fixed
                rt.position = uiCam.transform.position + t.offset;
            }
        }
    }

    [HarmonyPatch(typeof(uGUI_SafeAreaScaler), nameof(uGUI_SafeAreaScaler.Update))]
    static class uGUI_SafeAreaScaler_Fixer
    {
        public static bool Prefix()
        {
            XRSettingsEnabled.isEnabled = false;
            return true;
        }
        public static void Postfix()
        {
            XRSettingsEnabled.isEnabled = true;
        }
    }
/*
    [HarmonyPatch(typeof(PlayerMask))]
    [HarmonyPatch("Start")]
    internal static class PlayerMask_Start_Patch
    {
        static bool Prefix(PlayerMask __instance)
        {
            Debug.Log($"[ExtraFov] __instance is {__instance.referenceFov}");
            //__instance.referenceFov += 200.0f;
            return true;
        }
    }
*/
    
    //turn off the uicamera during screenshots
    [HarmonyPatch(typeof(HideForScreenshots), nameof(HideForScreenshots.Hide))]
    public static class HideForScreenshotsFix
    {
        public static void Postfix(HideForScreenshots __instance, HideForScreenshots.HideType hide)
        {
            //Mod.logger.LogInfo($"HideForScreenshots.Hide called {hide}");
            
            //HideForScreenshots.HideType.ViewModel is included for InGameMenu. Dont want to hide UI for that.
            if((hide & HideForScreenshots.HideType.HUD) == HideForScreenshots.HideType.HUD && (hide & HideForScreenshots.HideType.ViewModel) == 0)
            {
                VRCameraRig.instance.uiCamera.enabled = false;
            }
            else if(hide == HideForScreenshots.HideType.None)
            {
                VRCameraRig.instance.uiCamera.enabled = true;
            }  
        }
    }

    #endregion

}

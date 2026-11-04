using UnityEngine;

namespace SubmersedVR
{
    // 3D world-locked sphere for the precursor teleport (the VR fix,
    // Settings.FixTeleportEffect). Ported from the SN1 line
    // (Okabintaro/SubmersedVR, commit 45a0947, MIT): replaces the 2D
    // head-locked swirl with a 3D sphere that envelops the player.
    // Comfort: a WORLD-LOCKED 3D object (no full-screen head-following
    // animation) is comfortable, unlike the 2D swirl.
    //
    // Mechanics:
    // - The sphere is PARENTED to the player for ORIENTATION
    //   (localRotation stays identity). At spawn the pattern focus is
    //   aimed at the VIEW direction expressed in the body's local space
    //   (in BZ the body's forward is ~180 deg off the view - 31/10 spawn
    //   dirs log: bodyFront dot view = -0.997). The t=1s teleport snap
    //   re-rotates the body and the view TOGETHER (the VR rig copies the
    //   body rotation every frame), so the focus stays in front of the
    //   view for the rest of the cycle - no world-lock, no snap
    //   detection (31/10: the world-locked orientation + re-anchoring
    //   were workarounds for exactly this). Comfort is kept: the
    //   orientation follows the BODY (slow, physically plausible), not
    //   the headset. The POSITION is synced to the view's position every
    //   frame (SyncCenterToView, called from VRCameraRig.LateUpdate
    //   right after the rig copy): at arrival the game moves the player
    //   root up to ~0.6m (body placement, 31/10 log cycle 3:
    //   playerDelta=0.63 camDelta=0.00 in one frame, the camera catches
    //   up the next) - a purely player-parented position made the sphere
    //   appear to jump for 1-2 frames ("not every time": the delta
    //   depends on the arrival pose).
    // - Material: the game's own door effect material (the green
    //   water-door of the teleport room, VFXPrecursorTeleporter.portal-
    //   Renderer, shader FX/WBOIT-PrecursorPortal: a tiling noise texture
    //   + scroll + slow rotation + radial fade) is COPIED (new Material)
    //   and applied to the sphere. The copy is owned by us: the VFX On-
    //   Destroy destroys the game's live instance, not our copy, and
    //   driving our fade does not touch the door in the room. The fade
    //   mirrors the game's own door fade: the same uniform (_RadialFade)
    //   is driven here from fx.amount.
    // - Pattern center: the door shader's radial pattern focus is
    //   the texture center (UV 0.5, 0.5) - confirmed on BZ by the
    //   31/10 uniform dump (radial _ColorCenter/_ColorOuter, no focus
    //   offset uniform). At spawn the mesh vertex closest to that UV is
    //   found and rotated (FromToRotation) to the aim direction in the
    //   body's local space (the view, see above). Deterministic: no
    //   hardcoded angle, survives mesh/shader changes.
    // - Fallback (no door material found, e.g. a future game version):
    //   Sprites/Default (alpha blended) + the game's swirl
    //   "nerves" texture (static), the color lerps black -> green (fade
    //   in) and the alpha goes FadeInAlpha -> 0 (fade out).
    // - Known visual trade-offs (accepted): the game's VRUtil.Recenter()
    //   at arrival shifts the camera; the sphere is centered on the
    //   player (the recenter targets the player's world position) so it
    //   stays enveloping the view.
    //
    // No bundle / no custom shader: CreatePrimitive(Sphere) + a REVERSED
    // mesh (the interior faces become the front) so a Cull Back unlit
    // shader shows the inside.
    //
    // The sphere is rendered by the main camera (the player's layer), NOT by
    // the UI camera -> it never shows up in the PDA screen.
    static class TeleportSphere3D
    {
        const float Radius = 3.0f;            // interior radius (m)
        const float MonoR = 0.20f, MonoG = 1.0f, MonoB = 0.55f; // precursor green (fallback only)
        const float FadeInAlpha = 0.5f;       // fallback path: opacity at fade-in peak
        const float AliveSafety = 30.0f;      // a cycle is ~10 s

        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
        static readonly int RadialFadeID = Shader.PropertyToID("_RadialFade");
        static bool yawLogged;
        static bool doorPropsLogged;

        static GameObject sphereGo;
        static Material sphereMat;
        static bool usingDoorMat;
        static double spawnTime;
        static float prevAmount;
        static bool hasPrevAmount;
        static Texture2D whiteTexture;

        static Texture2D WhiteTexture()
        {
            if (whiteTexture == null)
            {
                whiteTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                whiteTexture.name = "TeleportSphereWhite";
                whiteTexture.SetPixel(0, 0, Color.white);
                whiteTexture.Apply();
            }
            return whiteTexture;
        }

        // The game's swirl "nerves" texture (grabbed at runtime from
        // TeleportScreenFX.mat in OnPreRender). Fallback path only: the
        // texture is static, so the sphere rotates around local Z to make
        // it look like it scrolls (the vortex effect). null = fallback to
        // white.
        static Texture swirlTexture;

        public static void SetSwirlTexture(Texture tex)
        {
            if (swirlTexture == null && tex != null)
            {
                swirlTexture = tex;
            }
        }

        // 30/10: the fix is only valid for the PRECURSOR teleporters. The
        // manual Seatruck_Teleportation_Module reuses TeleportScreenFX but
        // has no precursor door nearby: the sphere would fall back to the
        // plain shader and look "missing effects" compared to its own
        // swirl (user: keep desktop anim there). The cycle is gated on a
        // door material WITHIN RANGE (HasNearbyDoorMaterial).
        public const float DoorMaxRangeSq = 100f * 100f;

        public static bool HasNearbyDoorMaterial(float maxSqrDist)
        {
            return TryGetDoorMaterial(maxSqrDist) != null;
        }

        // The game's live door effect material (the green water-door of
        // the teleport room). The VFX is spawned at scene load so it
        // exists for the whole time the player is in the room. Nearest
        // door wins (the player stands in front of the one being
        // teleported through). The CALLER copies it (new Material) so it
        // owns the result.
        static Material TryGetDoorMaterial(float maxSqrDist)
        {
            try
            {
                Vector3 p = Player.main != null ? Player.main.transform.position : Vector3.zero;
                Material best = null;
                float bestDist = float.MaxValue;
                var vfxs = Object.FindObjectsOfType<VFXPrecursorTeleporter>();
                for (int i = 0; i < vfxs.Length; i++)
                {
                    var v = vfxs[i];
                    if (v == null || v.portalRenderer == null)
                    {
                        continue;
                    }
                    var m = v.portalRenderer.material;
                    if (m == null)
                    {
                        continue;
                    }
                    float d = (v.portalRenderer.transform.position - p).sqrMagnitude;
                    if (d < bestDist && d <= maxSqrDist)
                    {
                        bestDist = d;
                        best = m;
                    }
                }
                return best;
            }
            catch (System.Exception e)
            {
                Mod.logger.LogWarning("[TeleportSphere] door material lookup failed: " + e.Message);
                return null;
            }
        }

        // Create the sphere (idempotent). Called from the module's cycle-start
        // and on-demand from UpdateSphere (mid-teleport hot-swap). `amount`
        // is the current fx.amount, so the first rendered frame starts at
        // the right fade level (not at the door's live _RadialFade = 1,
        // which would pop one full frame).
        public static void Spawn(float amount)
        {
            if (sphereGo != null)
            {
                return;
            }
            var player = Player.main;
            if (player == null)
            {
                Mod.logger.LogInfo("[TeleportSphere] spawn skipped: Player.main NULL");
                return;
            }
            // 31/10: aim the pattern focus at the VIEW direction in the
            // body's local space. The sphere keeps localRotation
            // identity (body-parented orientation), so this aim frame is
            // the one that follows the t=1s snap together with the view
            // (the rig copies the body rotation). In BZ the body's
            // forward is ~180 deg off the view (spawn dirs log:
            // bodyFront dot view = -0.997), so aiming at the body
            // forward would put the vanishing point behind the player.
            Vector3 aimLocal = Vector3.back; // fallback: the body forward
            var camTf = SNCameraRoot.main != null ? SNCameraRoot.main.mainCamera.transform : null;
            if (camTf != null)
            {
                Vector3 view = camTf.forward;
                if (view.sqrMagnitude > 0.0001f)
                {
                    aimLocal = Quaternion.Inverse(player.transform.rotation) * view;
                }
            }
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "SubmersedVR_TeleportSphere";
            var col = go.GetComponent<Collider>();
            if (col != null)
            {
                Object.Destroy(col); // no player/sphere collision
            }
            // reversed mesh -> the interior faces become the front, so a
            // Cull Back unlit shader renders the inside; the vertices are
            // additionally yawed so the door pattern's focus faces the
            // player's forward (computed at runtime, see below)
            var mf = go.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                var src = mf.sharedMesh;
                var m = new Mesh();
                m.name = "TeleportSphereReversed";
                // Find the mesh vertex closest to UV (0.5, 0.5) = the
                // texture center = the door shader's radial pattern
                // focus. Rotate it to the aim direction (the view in the
                // body's local space, computed above). Deterministic: no
                // hardcoded angle, survives mesh/shader changes.
                var uvs = src.uv;
                int focusIdx = 0;
                float bestUvDist = float.MaxValue;
                for (int i = 0; i < uvs.Length; i++)
                {
                    float du = uvs[i].x - 0.5f, dv = uvs[i].y - 0.5f;
                    float d2 = du * du + dv * dv;
                    if (d2 < bestUvDist) { bestUvDist = d2; focusIdx = i; }
                }
                Vector3 fv = src.vertices[focusIdx];
                var aim = Quaternion.FromToRotation(fv.normalized, aimLocal.normalized);
                var verts = src.vertices;
                for (int i = 0; i < verts.Length; i++)
                {
                    verts[i] = aim * verts[i];
                }
                m.vertices = verts;
                m.uv = src.uv;
                int[] tris = src.triangles;
                int[] rev = new int[tris.Length];
                for (int i = 0; i < tris.Length; i += 3)
                {
                    rev[i] = tris[i];
                    rev[i + 1] = tris[i + 2];
                    rev[i + 2] = tris[i + 1];
                }
                m.triangles = rev;
                mf.mesh = m;
                if (!yawLogged)
                {
                    yawLogged = true;
                    Mod.logger.LogInfo("[TeleportSphere] focus aim (body-local) = " + CinematicsDiagnostics.FmtP(aimLocal)
                        + " (focus UV dist\u00b2 = " + bestUvDist.ToString("F4", Inv) + ")");
                }
            }
            // Try to reuse the game's door effect material (the green
            // water-door). Copy it: we own the copy, and the VFX OnDestroy
            // destroys the game's live instance, not our copy. No distance
            // cap here: Spawn is only reached when the cycle gate found a
            // nearby door, so the nearest door is the room's own door.
            var door = TryGetDoorMaterial(float.MaxValue);
            string matDesc;
            if (door != null)
            {
                sphereMat = new Material(door);
                usingDoorMat = true;
                matDesc = "door material " + sphereMat.name + " (shader " + sphereMat.shader.name + ")";
                // start at the current fade level (not the door's live
                // _RadialFade = 1, which would pop one full frame)
                sphereMat.SetFloat(RadialFadeID, Mathf.Clamp01(amount));
            }
            else
            {
                usingDoorMat = false;
                // Fallback: Sprites/Default is alpha blended and
                // exists in-game (the solid Unlit/Color ignores alpha); the
                // tint comes from the _Color (the white 1x1 mainTexture)
                var shader = Shader.Find("Sprites/Default");
                bool spriteShader = shader != null;
                if (!spriteShader)
                {
                    shader = Shader.Find("Unlit/Transparent");
                }
                if (shader == null)
                {
                    shader = Shader.Find("Unlit/Color");
                }
                if (shader == null)
                {
                    // No usable built-in shader (should not happen): skip
                    // the sphere rather than reference a game-internal
                    // shader field (the SN1 fallback, not verifiable in BZ).
                    Mod.logger.LogWarning("[TeleportSphere] no usable fallback shader found, sphere skipped");
                    Object.Destroy(go);
                    return;
                }
                sphereMat = new Material(shader);
                if (spriteShader)
                {
                    // the swirl "nerves" texture (recalls the vortex);
                    // fallback to white if it was not grabbed yet
                    sphereMat.mainTexture = swirlTexture != null ? swirlTexture : WhiteTexture();
                }
                // start black (UpdateSphere lerps the color black -> green to
                // fx.amount during the fade in; the opaque black masks the
                // raw world)
                sphereMat.color = Color.black;
                matDesc = "fallback shader " + shader.name;
            }
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.material = sphereMat;
            }
            // reset the fade direction tracking (the first frame is "fading in")
            prevAmount = 0f;
            hasPrevAmount = false;
            // parent to the player (position follows the snap), centered,
            // radius R; capture the body's world rotation for the
            // world-locked orientation.
            // Known limitation: vehicle teleports (Seamoth/Crawler through
            // a precursor door) move the vehicle, not the player root; the
            // sphere may not envelop the view. The swirl removal (the
            // comfort fix) still applies regardless.
            go.transform.SetParent(player.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            // 31/10: the orientation stays IDENTITY (body-parented) - the
            // mesh vertices were already aimed at the view in the body's
            // local space, and the t=1s snap re-rotates body and view
            // together, so the focus stays in front for the whole cycle
            // (no world-lock, no snap detection, no re-anchoring).
            if (usingDoorMat && !doorPropsLogged)
            {
                // one-shot: the shader's uniform set (if the vanishing
                // point is still misplaced after the view-aim fix, the
                // pattern focus is not the UV center and a uniform like a
                // _Center/_Focus will reveal where it actually is)
                doorPropsLogged = true;
                var sb = new System.Text.StringBuilder();
                var sh = sphereMat.shader;
                for (int i = 0; i < sh.GetPropertyCount(); i++)
                {
                    string name = sh.GetPropertyName(i);
                    var t = sh.GetPropertyType(i);
                    string val = t == UnityEngine.Rendering.ShaderPropertyType.Float
                        ? sphereMat.GetFloat(name).ToString("F3", Inv)
                        : t == UnityEngine.Rendering.ShaderPropertyType.Vector
                            ? sphereMat.GetVector(name).ToString()
                            : t == UnityEngine.Rendering.ShaderPropertyType.Color
                                ? sphereMat.GetColor(name).ToString()
                                : t.ToString();
                    sb.Append(name + "=" + val + " ");
                }
                Mod.logger.LogInfo("[TeleportSphere] door shader props: " + sb.ToString());
            }
            go.transform.localScale = Vector3.one * (Radius * 2f);
            go.layer = player.gameObject.layer;
            sphereGo = go;
            spawnTime = Time.time;
            Mod.logger.LogInfo("[TeleportSphere] spawned (r=" + Radius + "m, " + matDesc + ", parent=player, layer=" + player.gameObject.layer + ")");
        }

        // Called from the module's cycle-end (fx.amount reached 0 -> the
        // sphere is already faded out, destroy now, no pop).
        public static void OnCycleEnd()
        {
            if (sphereGo != null)
            {
                DestroySphere();
            }
        }

        static void DestroySphere()
        {
            if (sphereGo != null)
            {
                Object.Destroy(sphereGo);
                sphereGo = null;
            }
            if (sphereMat != null)
            {
                // our copy (door material) or our material (fallback):
                // safe to destroy either way
                Object.Destroy(sphereMat);
                sphereMat = null;
            }
            usingDoorMat = false;
            Mod.logger.LogInfo("[TeleportSphere] destroyed");
        }

        // Called every frame from TeleportMonoGuard (on the FX/main camera).
        public static void UpdateSphere(TeleportScreenFX fx)
        {
            if (Mod.quitting)
            {
                return;
            }
            bool fixEnabled = Settings.FixTeleportEffect;
            float amount = fx != null ? fx.amount : 0f;
            if (sphereGo == null)
            {
                // on-demand spawn: the user enabled the fix mid-teleport
                // (the cycle-start already passed). cycleFixActive keeps
                // the precursor-only gate even on this path.
                if (fixEnabled && amount > 0f && TeleportScreenFXVRFix.cycleFixActive)
                {
                    Spawn(amount);
                }
                return;
            }
            if (!fixEnabled)
            {
                DestroySphere(); // the user switched away from the sphere mode
                return;
            }
            // safety: a teleport cycle is ~10 s; if the sphere has been
            // alive > 30 s, the cycle is stuck -> force-end it (resets the
            // cycle tracking so the next teleport can spawn a new sphere)
            if (Time.time - spawnTime > AliveSafety)
            {
                Mod.logger.LogInfo("[TeleportSphere] safety: sphere alive > " + AliveSafety + " s (stuck cycle?), force-ending");
                TeleportScreenFXVRFix.ForceCycleEnd();
                return;
            }
            if (sphereMat == null)
            {
                return;
            }
            float a = Mathf.Clamp01(amount);
            // fade direction: the amount increases (fade in) or decreases
            // (fade out); used by the fallback color path
            bool fadingIn = !hasPrevAmount || a > prevAmount;
            prevAmount = a;
            hasPrevAmount = true;
            if (usingDoorMat)
            {
                // the game's own door fade: the same uniform
                // (VFXPrecursorTeleporter.Update drives it 0 -> 1 -> 0
                // during its own fade in/out), mirrored on fx.amount
                sphereMat.SetFloat(RadialFadeID, a);
            }
            else
            {
                // Fallback
                float peakAlpha = FadeInAlpha;
                Color green = new Color(MonoR, MonoG, MonoB, 1f);
                if (fadingIn)
                {
                    // fade in: the color lerps black -> green, the alpha
                    // goes 1 -> peakAlpha (the opaque black masks the raw
                    // world at the start, then the green appears and
                    // becomes translucent)
                    Color c = Color.Lerp(Color.black, green, a);
                    c.a = 1f - (1f - peakAlpha) * a;
                    sphereMat.color = c;
                }
                else
                {
                    // fade out (or peak): the color stays green, the alpha
                    // goes peakAlpha -> 0 (the green becomes transparent,
                    // the raw world shows through)
                    sphereMat.color = new Color(MonoR, MonoG, MonoB, peakAlpha * a);
                }
            }
        }

        // 31/10 (log cycle 3): at arrival the game moves the player root
        // ~0.6m and the camera catches up one frame later - the sphere
        // (parented to the player) appeared to jump for 1-2 frames. Keep
        // the sphere's center on the view's position every frame so it
        // always envelops the view, whatever the player<->camera
        // lag. Called from VRCameraRig.LateUpdate right after the rig
        // copy (same frame as the camera move). The orientation stays
        // body-relative; the spawn-time aim (body-local view direction)
        // stays valid because the focus point = center + R*aim = view
        // axis when the center is at the eye.
        public static void SyncCenterToView()
        {
            if (sphereGo == null)
            {
                return;
            }
            var rig = VRCameraRig.instance;
            if (rig == null || rig.vrCamera == null)
            {
                return;
            }
            sphereGo.transform.position = rig.vrCamera.transform.position;
        }
    }
}

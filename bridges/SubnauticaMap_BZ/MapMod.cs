using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SubnauticaMapBZBridge
{
    // Reflection bridge to the (closed source) SubnauticaMap BZ mod.
    // Everything is a no-op when the mod is not installed or its API changed.
    //
    // BZ API verified against the decompiled mod (v1.3.0, assembly SubnauticaMap_BZ.dll,
    // namespace SubnauticaMap): Controller.Instance / MapIsOpened / CreateNote /
    // private Zoom(float,bool), fields scrollView (ScrollRect), mapContainer (GameObject),
    // noteForm (GameObject), scanPanel (RectTransform), iconsContainer (GameObject),
    // mapRoomMapIconList (private Dictionary), CreateNoteEvent.OnPointerClick,
    // MapRoomMapIcon.Rotate + public Image scanCircle.
    static class MapMod
    {
        const string ModGuid = "snbz.subnauticamap.mod";
        const string ModTestedVersion = "1.3.0";
        const string ControllerTypeName = "SubnauticaMap.Controller";
        const string NoteEventType = "SubnauticaMap.CreateNoteEvent";
        const string MapRoomMapIconTypeName = "SubnauticaMap.MapRoomMapIcon";

        public static bool ModPresent;

        public static Type MapRoomMapIconType => mapRoomMapIconType;
        public static FieldInfo ScanCircleField => scanCircleField;

        // The reflected mod Controller type (set by Detect) - exposed for
        // the runtime-applied Harmony guards in Patches.cs (PatchAll runs
        // before the mod type is known).
        public static Type ControllerType => controllerType;
        public static FieldInfo MapContainerField => mapContainerField;

        static Type controllerType;
        static PropertyInfo instanceProperty;
        static MethodInfo mapIsOpenedMethod;
        static bool isMapOpenErrorLogged;
        static MethodInfo createNoteMethod;
        static MethodInfo zoomMethod;
        static MethodInfo getCursorScreenPositionMethod;
        static FieldInfo scrollViewField;
        static FieldInfo mapContainerField;
        static FieldInfo noteFormField;
        static FieldInfo lastRaycastField;
        static FieldInfo inputLastRaycastField;
        static FieldInfo scanPanelField;
        static FieldInfo iconsContainerField;
        static Type mapRoomMapIconType;
        static MethodInfo rotateMethod;
        static FieldInfo scanCircleField;
        static FieldInfo mapRoomMapIconListField;
        static Type noteEventType;
        static bool scanCirclePatched;
        static FPSInputModule fpsInput;
        static bool notePatched;

        public static bool IsActive => ModPresent;
        public static bool ZoomAvailable => zoomMethod != null;

        public static bool Detect()
        {
            if (ModPresent)
            {
                return true;
            }

            // The BZ mod ships SubnauticaMap_BZ.dll but its assembly identity name is not
            // stable across builds — Assembly.Load by name is unreliable. Scan the loaded
            // AppDomain for the Controller type instead (works whenever BepInEx has loaded
            // the mod, whatever the DLL/identity name).
            Assembly modAssembly = FindModAssembly();
            if (modAssembly == null)
            {
                return false;
            }

            LogModVersion();

            controllerType = modAssembly.GetType(ControllerTypeName);
            noteEventType = modAssembly.GetType(NoteEventType);
            if (controllerType == null)
            {
                Mod.logger.LogWarning($"Found map mod assembly ({modAssembly.GetName().Name}) but no {ControllerTypeName} type, bridge disabled.");
                return false;
            }

            instanceProperty = controllerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            mapIsOpenedMethod = controllerType.GetMethod("MapIsOpened", BindingFlags.Public | BindingFlags.Instance);
            createNoteMethod = controllerType.GetMethod("CreateNote", BindingFlags.Public | BindingFlags.Instance);
            zoomMethod = controllerType.GetMethod("Zoom", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(float), typeof(bool) }, null);
            scrollViewField = controllerType.GetField("scrollView", BindingFlags.NonPublic | BindingFlags.Instance);
            mapContainerField = controllerType.GetField("mapContainer", BindingFlags.NonPublic | BindingFlags.Instance);
            noteFormField = controllerType.GetField("noteForm", BindingFlags.NonPublic | BindingFlags.Instance);
            scanPanelField = controllerType.GetField("scanPanel", BindingFlags.NonPublic | BindingFlags.Instance);
            iconsContainerField = controllerType.GetField("iconsContainer", BindingFlags.NonPublic | BindingFlags.Instance);
            mapRoomMapIconType = modAssembly.GetType(MapRoomMapIconTypeName);
            if (mapRoomMapIconType != null)
            {
                rotateMethod = mapRoomMapIconType.GetMethod("Rotate", BindingFlags.Public | BindingFlags.Static);
                scanCircleField = mapRoomMapIconType.GetField("scanCircle", BindingFlags.Public | BindingFlags.Instance);
            }
            mapRoomMapIconListField = controllerType.GetField("mapRoomMapIconList", BindingFlags.NonPublic | BindingFlags.Instance);
            lastRaycastField = AccessTools.TypeByName("CursorManager").GetField("lastRaycast", BindingFlags.NonPublic | BindingFlags.Static);
            inputLastRaycastField = typeof(FPSInputModule).GetField("lastRaycastResult", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            getCursorScreenPositionMethod = typeof(FPSInputModule).GetMethod("GetCursorScreenPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (instanceProperty == null || mapIsOpenedMethod == null || createNoteMethod == null ||
                mapContainerField == null || lastRaycastField == null || inputLastRaycastField == null)
            {
                Mod.logger.LogWarning("SubnauticaMap/FPSInputModule API changed (missing members), bridge disabled.");
                return false;
            }

            if (zoomMethod == null)
            {
                Mod.logger.LogWarning("SubnauticaMap Zoom(float,bool) not found, VR zoom disabled (native scroll unchanged).");
            }

            if (rotateMethod == null)
            {
                Mod.logger.LogWarning("SubnauticaMap MapRoomMapIcon.Rotate not found, scan circle plane fix disabled.");
            }

            if (getCursorScreenPositionMethod == null)
            {
                Mod.logger.LogWarning("FPSInputModule.GetCursorScreenPosition not found, map hover coordinates disabled.");
            }

            if (scanPanelField == null || iconsContainerField == null)
            {
                Mod.logger.LogWarning("SubnauticaMap scanPanel/iconsContainer not found, VR zoom will not be gated over those panels.");
            }

            ModPresent = true;
            return true;
        }

        static Assembly FindModAssembly()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.GetType(ControllerTypeName) != null)
                    {
                        return asm;
                    }
                }
                catch (Exception)
                {
                    // reflective-only assemblies can throw on GetType — skip
                }
            }
            return null;
        }

        // The mod's real version is in the BepInEx plugin metadata
        // ([BepInPlugin("snbz.subnauticamap.mod", "SubnauticaMap", "1.3.0")]).
        // Warn on drift, never hard-block: a benign patch bump must not kill the bridge.
        static void LogModVersion()
        {
            try
            {
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(ModGuid, out BepInEx.PluginInfo pluginInfo))
                {
                    string version = pluginInfo.Metadata.Version.ToString();
                    Mod.logger.LogInfo($"SubnauticaMap BZ version detected: {version}");
                    if (version != ModTestedVersion)
                    {
                        Mod.logger.LogWarning($"SubnauticaMap BZ {version} detected (bridge tested against {ModTestedVersion}); some features may not work.");
                    }
                }
            }
            catch (Exception)
            {
                // version info only — never break detection
            }
        }

        // Patch CreateNoteEvent.OnPointerClick at runtime (the type lives in the closed source mod assembly).
        public static void ApplyNotePatch()
        {
            if (!ModPresent || notePatched)
            {
                return;
            }

            MethodInfo onPointerClick = noteEventType?.GetMethod("OnPointerClick", BindingFlags.Public | BindingFlags.Instance);
            if (onPointerClick == null)
            {
                Mod.logger.LogWarning($"SubnauticaMap {NoteEventType} is missing OnPointerClick, note creation disabled.");
                return;
            }

            Mod.harmony.Patch(onPointerClick, new HarmonyMethod(typeof(NoteOnMapClick), nameof(NoteOnMapClick.Prefix)));
            notePatched = true;
            Mod.logger.LogInfo("Map note creation patched (trigger click on the map).");
        }

        // The mod's Rotate coroutine spins the scan distance circle with transform.eulerAngles —
        // WORLD space. In 2D the PDA canvas world rotation is identity, so it behaves like an
        // in-plane spin; in VR the canvas is rotated to the PDA screen's world orientation
        // (SubmersedVR head-space placement), so each frame the circle is snapped to the world
        // XZ plane: out of the map plane while its center (localPosition) still tracks the room
        // icon. Redirecting both the setter AND the getter to localEulerAngles makes the
        // feedback loop (read z, write (0,0,z-30°dt)) purely in-plane — a constant 30°/s spin
        // independent of the canvas orientation (SN1 bridge v0.3.8 lesson).
        //
        // The IL lives in the nested coroutine state machine (MapRoomMapIcon/<Rotate>d__N::MoveNext),
        // NOT in the Rotate body — patch every nested MoveNext plus Rotate itself.
        public static void ApplyScanCirclePatch()
        {
            if (!ModPresent || scanCirclePatched)
            {
                return;
            }

            if (mapRoomMapIconType == null)
            {
                return;
            }

            HarmonyMethod transpiler = new HarmonyMethod(typeof(ScanCircleRotateLocal), nameof(ScanCircleRotateLocal.Transpiler));
            int patched = 0;

            if (rotateMethod != null)
            {
                Mod.harmony.Patch(rotateMethod, transpiler: transpiler);
                patched++;
            }

            foreach (Type nested in mapRoomMapIconType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                // NonPublic required: the compiler emits the state machine's MoveNext as a
                // PRIVATE explicit interface implementation.
                MethodInfo moveNext = nested.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (moveNext == null)
                {
                    continue;
                }
                Mod.harmony.Patch(moveNext, transpiler: transpiler);
                patched++;
            }

            scanCirclePatched = true;
            Mod.logger.LogInfo($"Scan circle rotation patched ({patched} method(s), world -> local eulerAngles, in-plane in VR).");
        }

        // The icon list is a private field on the Controller (MapRoomMapIcon is a plain C#
        // class — FindObjectsOfType cannot see it). Returns the scanCircle Image of every map
        // room icon currently in the list.
        public static Image[] GetScanCircles()
        {
            if (mapRoomMapIconListField == null || scanCircleField == null)
            {
                return null;
            }

            object controller = GetController();
            if (controller == null)
            {
                return null;
            }

            System.Collections.IDictionary list = mapRoomMapIconListField.GetValue(controller) as System.Collections.IDictionary;
            if (list == null)
            {
                return null;
            }

            Image[] circles = new Image[list.Count];
            int i = 0;
            foreach (object icon in list.Values)
            {
                circles[i++] = scanCircleField.GetValue(icon) as Image;
            }
            return circles;
        }

        static object GetController()
        {
            if (instanceProperty == null)
            {
                return null;
            }
            return instanceProperty.GetValue(null);
        }

        public static bool PdaOpen
        {
            get
            {
                return Player.main != null && Player.main.GetPDA() != null && Player.main.GetPDA().isOpen;
            }
        }

        public static bool IsMapOpen
        {
            get
            {
                object controller = GetController();
                if (controller == null)
                {
                    return false;
                }
                try
                {
                    return (bool)mapIsOpenedMethod.Invoke(controller, null);
                }
                catch (System.Exception e)
                {
                    // The mod's MapIsOpened dereferences mapContainer
                    // without a null check (30/10 log: NRE on map tab open,
                    // then 700+ per-frame NREs from this poll). The
                    // Harmony null-guards (Patches.cs) cover the known case;
                    // this catches the rest without log spam.
                    if (!isMapOpenErrorLogged)
                    {
                        isMapOpenErrorLogged = true;
                        Mod.logger.LogWarning($"IsMapOpen invoke failed (map mod state): {e.Message}");
                    }
                    return false;
                }
            }
        }

        public static bool NoteFormActive
        {
            get
            {
                object controller = GetController();
                if (controller == null)
                {
                    return false;
                }
                GameObject form = noteFormField?.GetValue(controller) as GameObject;
                return form != null && form.activeSelf;
            }
        }

        public static ScrollRect GetScrollView()
        {
            object controller = GetController();
            if (controller == null)
            {
                return null;
            }
            return scrollViewField?.GetValue(controller) as ScrollRect;
        }

        public static bool TryGetInputModule(out FPSInputModule input)
        {
            input = fpsInput;
            if (input == null)
            {
                input = UnityEngine.Object.FindObjectOfType<FPSInputModule>();
                fpsInput = input;
            }
            return input != null;
        }

        // FPSInputModule.GetCursorScreenPosition is private in the runtime build (SubmersedVR
        // patches it to return the UI event camera center in VR). Always call it through
        // reflection, like the rest of the bridge (SN1 v0.2.0 MethodAccessException lesson).
        public static bool TryGetLaserScreenPosition(out Vector2 screenPos)
        {
            screenPos = Vector2.zero;
            if (getCursorScreenPositionMethod == null || !TryGetInputModule(out FPSInputModule input))
            {
                return false;
            }

            object boxed = getCursorScreenPositionMethod.Invoke(input, null);
            if (boxed is Vector2 v2)
            {
                screenPos = v2;
                return true;
            }
            if (boxed is Vector3 v3)
            {
                screenPos = new Vector2(v3.x, v3.y);
                return true;
            }
            return false;
        }

        // FPSInputModule.lastRaycastResult is private in the runtime assembly — always read it
        // through reflection (SN1 v0.1 FieldAccessException lesson).
        public static bool TryGetLastRaycast(out RaycastResult raycast)
        {
            raycast = default;
            if (inputLastRaycastField == null || !TryGetInputModule(out FPSInputModule input))
            {
                return false;
            }

            object boxed = inputLastRaycastField.GetValue(input);
            if (boxed == null)
            {
                return false;
            }

            raycast = (RaycastResult)boxed;
            return raycast.isValid && raycast.gameObject != null;
        }

        public static bool IsHoveringMap()
        {
            if (!TryGetLastRaycast(out RaycastResult raycast))
            {
                return false;
            }

            object controller = GetController();
            if (controller == null)
            {
                return false;
            }

            GameObject container = mapContainerField.GetValue(controller) as GameObject;
            return container != null && raycast.gameObject.transform.IsChildOf(container.transform);
        }

        // Event-based variant: is the given transform inside the map container. Used by the note
        // click gate with the click event's own raycast — FPSInputModule.lastRaycastResult can
        // be stale/invalid on the click dispatch frame.
        public static bool IsChildOfMapContainer(Transform target)
        {
            if (target == null)
            {
                return false;
            }

            object controller = GetController();
            if (controller == null)
            {
                return false;
            }

            GameObject container = mapContainerField.GetValue(controller) as GameObject;
            return container != null && target.IsChildOf(container.transform);
        }

        // The map mod reads its own copy of the last raycast (CursorManager.lastRaycast, used for
        // note positions and hover coordinates — the BZ mod even reads it through its own
        // reflection, decompiled L120). Keep it in sync with the SubmersedVR laser pointer.
        public static void SyncCursorRaycast()
        {
            if (lastRaycastField != null && TryGetLastRaycast(out RaycastResult raycast))
            {
                lastRaycastField.SetValue(null, raycast);
            }
        }

        // Point the mod's cursor copy at a specific raycast (same UnityEngine.EventSystems type).
        // Used right before CreateNote(): the mod's note placement reads CursorManager.lastRaycast
        // and orphans the note at (0,0) when it's invalid — the click's own raycast is always
        // valid and on the map, so the note lands exactly where the user clicked.
        public static void SyncCursorFromRaycast(RaycastResult raycast)
        {
            if (lastRaycastField != null && raycast.isValid && raycast.gameObject != null)
            {
                lastRaycastField.SetValue(null, raycast);
            }
        }

        // The map mod cancels its native zoom when the laser is over the scan panel or the icons
        // container (its Update, decompiled L477). Direct Zoom() calls must respect the same
        // condition with the same raycast, or zooming diverges from native behavior over those
        // panels.
        public static bool IsLaserOverExcludedUi()
        {
            if (!TryGetLastRaycast(out RaycastResult raycast))
            {
                return false;
            }

            object controller = GetController();
            if (controller == null)
            {
                return false;
            }

            RectTransform scanPanel = scanPanelField?.GetValue(controller) as RectTransform;
            if (scanPanel != null && raycast.gameObject.transform.IsChildOf(scanPanel))
            {
                return true;
            }

            GameObject icons = iconsContainerField?.GetValue(controller) as GameObject;
            return icons != null && raycast.gameObject.transform.IsChildOf(icons.transform);
        }

        // The mod's native Zoom() is unreachable at a usable speed from a stick: any non-zero
        // scroll fires a FIXED 500px step (decompiled L541: !controller ? 500f : 50f), and
        // SubmersedVR feeds the right stick into mouseScrollDelta every frame. Call the private
        // Zoom() directly with the 50px "controller" step, 10 steps/s, for a dosable zoom.
        public static void InvokeZoom(float step)
        {
            if (zoomMethod == null)
            {
                return;
            }

            object controller = GetController();
            if (controller != null)
            {
                zoomMethod.Invoke(controller, new object[] { step, true });
            }
        }

        public static void CreateNote()
        {
            object controller = GetController();
            if (controller != null)
            {
                createNoteMethod.Invoke(controller, new object[] { null });
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace SubmersedVR
{
    // Watches the SeaTruck dock state.
    //
    // Game facts (decompiled, BZ 1.22):
    // - The seatruck parks at the cargo lane slot = VehicleDockingBay.dockingEndPos
    //   (NOT SeaTruckDockingBay.dockingPosition, which is the prawn suit slot).
    // - While a VehicleDockingBay holds a Dockable, its LateUpdate snaps the
    //   docked object to dockingEndPos every frame, and Dockable.SetDocked makes
    //   the truck rigidbody kinematic. A correctly docked truck is therefore
    //   immovable by design; the undock sequence (eject lever / OnUndockingComplete)
    //   is the only way out.
    // - On save load, a Dockable saved with docked=true starts
    //   InvokeRepeating("Redock", 0, 0.2): every 0.2s it re-docks itself to a
    //   VehicleDockingBay within 2m.
    // - Vehicle.OnPilotModeBegin -> Dockable.OnPlayerEntered -> bay.EjectDocked()
    //   clears the bay reference but never calls SetDocked(null): the rigidbody
    //   stays kinematic, so a piloted-but-undocked truck cannot move and the
    //   eject sequence (blocked exit point) cannot be used to leave.
    //
    // Manual rescue only (no auto-repair, no enable toggle):
    // F9: repair the broken dock state (cancel Redock, clear the holding bay,
    //     SetDocked(null) to unlock the rigidbody) and, if the truck is stuck in
    //     geometry or in a nonsense spot, move it back to the cargo lane slot.
    // Shift+F9: same repair, then push the truck out of the hangar so it can be
    //     driven away. Last-resort path: the options (and thus the button)
    //     are sometimes unreachable in exactly the states where the eject is
    //     needed, so the hotkeys stay even though the keyboard often does not
    //     reach the game in VR input mode.
    // Options button: same as F9, for when the keyboard does not reach the
    //     game in VR input mode. When the options open, the dock state is
    //     ANALYSED (no repair) and, if a problem is detected, the button is
    //     highlighted (red + warning label) so the user knows to click it.
    public class SeaTruckWatchdog : MonoBehaviour
    {
        public static SeaTruckWatchdog instance;

        public const string RescueButtonLabel = "SeaTruck: rescue now";

        // Distance from the cargo lane slot beyond which a held truck is considered misplaced
        private const float DockedMaxDistance = 3f;
        // An undocked truck closer than this to the slot is inside the bay area
        private const float UndockedInBayDistance = 8f;
        // Radius around the main cab: non-truck colliders closer than this mean the truck is stuck inside something
        private const float OverlapRadius = 5f;
        // How far past the slot the eject target lies (along the slot-to-outside direction)
        private const float EjectDistancePastSlot = 25f;
        // The slot is only a plausible target while the truck is in the vicinity of the base
        private const float RescueParkDistance = 20f;

        void Awake()
        {
            instance = this;
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                {
                    LogState("Shift+F9 pressed (eject)");
                    try
                    {
                        EjectTruck("Shift+F9");
                    }
                    catch (Exception e)
                    {
                        LogWarning($"eject failed: {e}");
                    }
                    return;
                }
                LogState("F9 pressed");
                try
                {
                    CheckAndRescue("F9");
                }
                catch (Exception e)
                {
                    LogWarning($"F9 failed: {e}");
                }
                return;
            }
        }

        // Options panel button: same as F9, for when the keyboard does not
        // reach the game (VR input mode).
        public static void RescueNow()
        {
            if (instance == null)
            {
                LogWarning("options button: watchdog not ready yet");
                return;
            }
            instance.CheckAndRescueFromOptions();
        }

        private void CheckAndRescueFromOptions()
        {
            LogState("options button: rescue now");
            try
            {
                CheckAndRescue("options button");
            }
            catch (Exception e)
            {
                LogWarning($"options button: rescue failed: {e}");
            }
        }

        private void CheckAndRescue(string context)
        {
            bool bad;
            string issue;
            try
            {
                bad = EvaluateIssue(out issue);
            }
            catch (Exception e)
            {
                LogWarning($"{context}: evaluate failed: {e}");
                return;
            }
            try
            {
                Rescue(bad ? issue : "manual");
            }
            catch (Exception e)
            {
                LogWarning($"{context}: rescue failed: {e}");
            }
        }

        // Called from Settings.CreateOptionsTab when the options panel opens:
        // analyse the SeaTruck dock state (NO repair) and, when a problem is
        // detected, make the rescue button stand out (red + warning label).
        public static void RefreshRescueButton(uGUI_TabbedControlsPanel panel)
        {
            TextMeshProUGUI buttonLabel = null;
            if (panel != null)
            {
                foreach (var t in panel.GetComponentsInChildren<TextMeshProUGUI>(true))
                {
                    if (t != null && t.text == RescueButtonLabel)
                    {
                        buttonLabel = t;
                        break;
                    }
                }
            }
            if (buttonLabel == null)
            {
                LogWarning("options open: rescue button label not found, no highlight");
                return;
            }
            bool bad;
            string issue;
            try
            {
                bad = EvaluateIssue(out issue);
            }
            catch (Exception e)
            {
                LogWarning($"options open: evaluate failed: {e}");
                return;
            }
            if (bad)
            {
                LogState($"options open: SEA TRUCK PROBLEM DETECTED: {issue}");
                buttonLabel.color = Color.red;
                buttonLabel.text = "SeaTruck: DOCK PROBLEM DETECTED - CLICK TO RESCUE";
            }
        }

        private static bool EvaluateIssue(out string issue)
        {
            issue = "none";
            SeaTruckSegment cab;
            SeaTruckDockingBay bay;
            if (!TryGetMainCab(out cab) || !TryGetBay(out bay))
            {
                return false;
            }
            var dockable = cab.dockable;
            if (dockable == null)
            {
                return false;
            }
            if (dockable.isInTransition || cab.IsDocking() || (cab.motor != null && cab.motor.IsPiloted()))
            {
                return false;
            }

            Vector3 slot;
            var slotBay = TryGetTruckSlot(out slot);
            if (dockable.isDocked)
            {
                // A truck held at the authoritative final slot (the
                // scene-set SeaTruckDockingBay.dockingPosition) is healthy.
                // Held anywhere else it is docked to the WRONG bay (27/10:
                // on load the game's own Redock loop docks the orphan truck
                // to an intermediate "DockBottom" bay whose slot sits at the
                // truck's own position, so it sits halfway in the dock
                // forever and every F9 was a no-op teleport) - rescue it to
                // the final slot.
                if (bay.dockingPosition != null)
                {
                    float distFinal = Vector3.Distance(cab.transform.position, bay.dockingPosition.position);
                    if (distFinal > DockedMaxDistance)
                    {
                        issue = $"docked at the wrong bay ({distFinal:F1}m from the final slot)";
                        return true;
                    }
                }
                return false;
            }

            // Orphan dock state (27/10, user save): the docked flag is saved
            // true but no bay holds the truck (lost dockedObject reference).
            // The Redock loop (armed at load while docked=true) polls for a
            // VehicleDockingBay within 2m and never finds one, so the truck
            // sits at the dock entrance forever, overlapping geometry
            // ("au début du dock pas complètement rentré").
            if (dockable.docked && !dockable.isDocked)
            {
                if (slotBay != null)
                {
                    float dist = Vector3.Distance(cab.transform.position, slot);
                    if (dist > DockedMaxDistance)
                    {
                        issue = $"orphan dock state (docked flag set, no holding bay, {dist:F1}m from the slot)";
                        return true;
                    }
                }
                return false;
            }

            // Broken dock state: no bay holds the truck but the rigidbody is still
            // kinematic (OnPlayerEntered cleared the bay without SetDocked(null)).
            // The thrusters are dead and the eject sequence cannot be used.
            if (cab.rb != null && cab.rb.isKinematic && cab.player == null)
            {
                var wst = cab.worldSettledTracker;
                var vfx = cab.vfxConstructing;
                if ((wst == null || wst.worldSettled) && (vfx == null || vfx.IsConstructed()))
                {
                    issue = "broken dock state (rigidbody kinematic with no holding bay)";
                    return true;
                }
            }

            if (slotBay != null)
            {
                float dist = Vector3.Distance(cab.transform.position, slot);
                if (dist < UndockedInBayDistance && OverlappingNonTruckCount(cab.transform.position) > 0)
                {
                    issue = $"undocked but stuck in the bay ({dist:F1}m from the slot, overlapping geometry)";
                    return true;
                }
            }
            return false;
        }

        private static void Rescue(string issue)
        {
            SeaTruckSegment cab;
            SeaTruckDockingBay bay;
            if (!TryGetMainCab(out cab) || !TryGetBay(out bay))
            {
                return;
            }
            var dockable = cab.dockable;
            if (dockable == null)
            {
                return;
            }
            if (dockable.isInTransition || cab.IsDocking())
            {
                LogInfo($"RESCUE ({issue}): a docking/undocking transition is in progress, aborting");
                return;
            }
            if (bay.dockingPosition == null)
            {
                LogInfo("RESCUE: no docking position marker, nothing to do");
                return;
            }

            FixDockState(cab, dockable);

            // Park the truck 20 m behind the dock (27/10 user request).
            // Teleporting it to its dock slot was wrong: the marker sits at
            // the prawn anchor, the parked truck overlaps the module wagons,
            // and the game's Redock loop then re-docked it to a wrong bay in
            // a loop (SetDocked(null) does not clear the serialized docked
            // flag, so the re-armed loop kept re-docking it). The undocking
            // force is applied along the marker's forward vector to push the
            // object out of the base, so forward = the exit direction.
            Vector3 outDir = bay.dockingPosition.forward;
            Vector3 target = bay.dockingPosition.position + outDir * RescueParkDistance;
            for (float d = RescueParkDistance; d <= 60f; d += 20f)
            {
                Vector3 candidate = bay.dockingPosition.position + outDir * d;
                if (OverlappingNonTruckCount(candidate) <= 1)
                {
                    target = candidate;
                    break;
                }
            }
            Quaternion targetRot = Quaternion.LookRotation(outDir);
            Teleport(cab, dockable, target, targetRot);

            // Fully undock: no holding bay (FixDockState) and the serialized
            // docked flag off, so the game's Redock loop (cancelled in
            // FixDockState and never re-armed here) cannot dock it back
            // while it is parked out of the base.
            dockable.docked = false;
            dockable.CancelInvoke("Redock");
            LogInfo($"RESCUE ({issue}): parked at {Fmt(cab.transform.position)} facing out, docked flag cleared - drive it back in once the modules are reassembled");
        }

        // Repair + push the truck out of the hangar so the user can drive away.
        private static void EjectTruck(string context)
        {
            SeaTruckSegment cab;
            SeaTruckDockingBay bay;
            if (!TryGetMainCab(out cab) || !TryGetBay(out bay))
            {
                LogInfo($"{context}: no main cab or bay found, nothing to eject");
                return;
            }
            var dockable = cab.dockable;
            if (dockable == null)
            {
                LogInfo($"{context}: no Dockable on the main cab, nothing to eject");
                return;
            }
            if (dockable.isInTransition || cab.IsDocking())
            {
                LogInfo($"{context}: a docking/undocking transition is in progress, aborting");
                return;
            }
            if (cab.motor != null && cab.motor.IsPiloted())
            {
                LogInfo($"{context}: you are piloting the SeaTruck, exit it first");
                return;
            }

            FixDockState(cab, dockable);

            Vector3 slot;
            var slotBay = TryGetLaneSlot(out slot);
            Vector3 dir = (slotBay != null) ? (slot - bay.dockingPosition.position).normalized : bay.transform.forward;
            if (dir.sqrMagnitude < 0.5f)
            {
                dir = bay.transform.forward;
            }
            // The seatruck bay marker sits inside the base; the slot (cargo lane) is
            // the way out. Continue along slot - marker past the slot to get outside.
            Vector3 target = slotBay != null
                ? slot + dir * EjectDistancePastSlot
                : bay.dockingPosition.position + dir * (EjectDistancePastSlot + 15f);

            Teleport(cab, dockable, target, Quaternion.LookRotation(dir));
            LogInfo($"EJECT done: now at {Fmt(cab.transform.position)}, docked={dockable.isDocked}");
        }

        // Cancel the auto-Redock loop, clear the bay that holds the truck and unlock
        // the rigidbody through the game's own SetDocked path.
        private static void FixDockState(SeaTruckSegment cab, Dockable dockable)
        {
            bool changed = false;
            dockable.CancelInvoke("Redock");
            var vdb = FindHoldingVehicleBay(dockable);
            if (vdb != null)
            {
                vdb.dockedObject = null;
                LogInfo($"FIX: cleared holding bay {vdb.name}");
                changed = true;
            }
            var stdb = FindHoldingSeatruckBay(dockable);
            if (stdb != null)
            {
                stdb.dockedObject = null;
                LogInfo($"FIX: cleared holding bay {stdb.name}");
                changed = true;
            }
            if (dockable.isDocked || (cab.rb != null && cab.rb.isKinematic))
            {
                dockable.SetDocked(null, Vehicle.DockType.None);
                LogInfo("FIX: SetDocked(null) - rigidbody unlocked, vehicle.docked cleared");
                changed = true;
            }
            if (!changed)
            {
                LogInfo("FIX: nothing to repair (no Redock loop, no holding bay, rigidbody already free)");
            }
        }

        private static void Teleport(SeaTruckSegment cab, Dockable dockable, Vector3 target, Quaternion targetRot)
        {
            LogInfo($"TELEPORT: from {Fmt(cab.transform.position)} rot {FmtRot(cab.transform.rotation)} to {Fmt(target)} rot {FmtRot(targetRot)}");
            cab.SetDockedCollisionEnabled(false);
            UnlockRedockLocks(cab);
            if (cab.rb != null)
            {
                cab.rb.velocity = Vector3.zero;
                cab.rb.angularVelocity = Vector3.zero;
            }
            var vehicle = dockable.vehicle;
            if (vehicle != null)
            {
                vehicle.TeleportVehicle(target, targetRot);
            }
            else
            {
                cab.transform.SetPositionAndRotation(target, targetRot);
            }
            if (cab.rb != null)
            {
                cab.rb.velocity = Vector3.zero;
                cab.rb.angularVelocity = Vector3.zero;
            }
            cab.SetDockedCollisionEnabled(true);
        }

        // The VehicleDockingBay that currently holds the truck's Dockable.
        private static VehicleDockingBay FindHoldingVehicleBay(Dockable dockable)
        {
            var bays = UnityEngine.Object.FindObjectsOfType<VehicleDockingBay>();
            foreach (var b in bays)
            {
                if (b.dockedObject == dockable)
                {
                    return b;
                }
            }
            return null;
        }

        private static SeaTruckDockingBay FindHoldingSeatruckBay(Dockable dockable)
        {
            var bays = UnityEngine.Object.FindObjectsOfType<SeaTruckDockingBay>();
            foreach (var b in bays)
            {
                if (b.dockedObject == dockable)
                {
                    return b;
                }
            }
            return null;
        }

        // The cargo lane slot of the truck: the slot of the VehicleDockingBay that
        // holds it, or (when no bay holds it) the slot of the nearest bay.
        // The authoritative truck slot is SeaTruckDockingBay.dockingPosition
        // (scene-set, static, never corrupted). The VehicleDockingBay
        // dockingEndPos values are NOT reliable for rescue: when the game
        // docks the truck to a bay, that bay's end pos can end up at the
        // truck's own position (27/10: the orphan truck was held by an
        // intermediate "DockBottom" bay whose slot sat exactly under it, so
        // the old "nearest bay" heuristic teleported the truck to where it
        // already was). The returned bay (nearest dockingEndPos to the
        // slot) only provides the docked rotation; it may be null.
        private static VehicleDockingBay TryGetTruckSlot(out Vector3 slot)
        {
            slot = Vector3.zero;
            SeaTruckSegment cab;
            SeaTruckDockingBay stdb;
            if (!TryGetMainCab(out cab) || cab.dockable == null || !TryGetBay(out stdb))
            {
                return null;
            }
            if (stdb.dockingPosition == null)
            {
                return null;
            }
            slot = stdb.dockingPosition.position;
            var bays = UnityEngine.Object.FindObjectsOfType<VehicleDockingBay>();
            VehicleDockingBay best = null;
            float bestDist = float.MaxValue;
            foreach (var b in bays)
            {
                if (b.dockingEndPos == null)
                {
                    continue;
                }
                float d = Vector3.Distance(slot, b.dockingEndPos.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = b;
                }
            }
            return best;
        }

        // Eject only: the lane slot nearest the truck (old heuristic). The
        // lane is the way out of the dock; dockingPosition is where the
        // truck docks, not where it leaves from.
        private static VehicleDockingBay TryGetLaneSlot(out Vector3 slot)
        {
            slot = Vector3.zero;
            SeaTruckSegment cab;
            if (!TryGetMainCab(out cab) || cab.dockable == null)
            {
                return null;
            }
            var holder = FindHoldingVehicleBay(cab.dockable);
            if (holder != null && holder.dockingEndPos != null)
            {
                slot = holder.dockingEndPos.position;
                return holder;
            }
            var bays = UnityEngine.Object.FindObjectsOfType<VehicleDockingBay>();
            VehicleDockingBay best = null;
            float bestDist = float.MaxValue;
            foreach (var b in bays)
            {
                if (b.dockingEndPos == null)
                {
                    continue;
                }
                float d = Vector3.Distance(cab.transform.position, b.dockingEndPos.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = b;
                }
            }
            if (best != null)
            {
                slot = best.dockingEndPos.position;
            }
            return best;
        }

        private static void UnlockRedockLocks(SeaTruckSegment cab)
        {
            var bays = UnityEngine.Object.FindObjectsOfType<VehicleDockingBay>();
            foreach (var vb in bays)
            {
                if (!vb.seaTruckRedockLock.IsLocked())
                {
                    continue;
                }
                var locked = vb.seaTruckRedockLock.GetLockedSeaTruck();
                if (locked == null || locked == cab)
                {
                    vb.seaTruckRedockLock.Unlock();
                    LogInfo($"RESCUE: redock lock on {vb.name} was locked, unlocked");
                }
            }
        }

        private static void LogRedockLocks(StringBuilder sb)
        {
            var bays = UnityEngine.Object.FindObjectsOfType<VehicleDockingBay>();
            if (bays.Length == 0)
            {
                sb.Append("  redockLocks: no VehicleDockingBay found\n");
                return;
            }
            foreach (var vb in bays)
            {
                var locked = vb.seaTruckRedockLock.GetLockedSeaTruck();
                sb.Append($"  redockLock ({vb.name}): locked={vb.seaTruckRedockLock.IsLocked()} truck={(locked != null ? locked.name : "null")} slot={(vb.dockingEndPos != null ? Fmt(vb.dockingEndPos.position) : "null")} holding={(vb.dockedObject != null ? vb.dockedObject.name : "null")}\n");
            }
        }

        public static void LogState(string context)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append($"[Seatruck] {context}\n");
                SeaTruckSegment cab;
                SeaTruckDockingBay bay;
                bool hasCab = TryGetMainCab(out cab);
                bool hasBay = TryGetBay(out bay);
                if (!hasCab)
                {
                    sb.Append("  mainCab: not found\n");
                }
                else
                {
                    var d = cab.dockable;
                    sb.Append($"  mainCab: {cab.name} pos={Fmt(cab.transform.position)} rot={FmtRot(cab.transform.rotation)} vel={Fmt(cab.rb != null ? cab.rb.velocity : Vector3.zero)}\n");
                    sb.Append($"    docked(bool)={(d != null && d.docked)} isDocked(bay)={(d != null && d.isDocked)} inTransition={(d != null && d.isInTransition)} docking={cab.IsDocking()} piloting={(cab.motor != null && cab.motor.IsPiloted())}\n");
                    sb.Append($"    rb={(cab.rb != null ? (cab.rb.isKinematic ? "kinematic" : "dynamic") : "null")} playerInside={(cab.player != null ? cab.player.name : "null")} worldSettled={(cab.worldSettledTracker != null ? cab.worldSettledTracker.worldSettled.ToString() : "null")}\n");
                    var holder = d != null ? FindHoldingVehicleBay(d) : null;
                    sb.Append($"    holdingBay={(holder != null ? $"{holder.name} (slot {Fmt(holder.dockingEndPos.position)})" : "none")}\n");
                }
                if (!hasBay)
                {
                    sb.Append("  bay: not found\n");
                }
                else
                {
                    sb.Append($"  seatruckBay: {bay.name} pos={Fmt(bay.transform.position)} rot={FmtRot(bay.transform.rotation)} prawnSlot={Fmt(bay.dockingPosition.position)} prawnSlotObject={(bay.dockedObject != null ? bay.dockedObject.name : "null")}\n");
                }
                LogRedockLocks(sb);
                if (hasCab)
                {
                    var overlaps = OverlappingNonTruckColliders(cab.transform.position);
                    sb.Append($"  overlaps: {overlaps.Count} non-truck collider(s) within {OverlapRadius}m");
                    if (overlaps.Count > 0 && overlaps.Count <= 10)
                    {
                        sb.Append(": " + string.Join(", ", overlaps.Take(10).Select(c => c)));
                    }
                    sb.Append("\n");
                }
                if (Player.main != null)
                {
                    sb.Append($"  player: {Fmt(Player.main.transform.position)}\n");
                }
                Mod.logger.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                LogWarning($"LogState({context}) failed: {e}");
            }
        }

        private static bool TryGetMainCab(out SeaTruckSegment cab)
        {
            cab = UnityEngine.Object.FindObjectsOfType<SeaTruckSegment>().FirstOrDefault(s => s.isMainCab);
            return cab != null;
        }

        private static bool TryGetBay(out SeaTruckDockingBay bay)
        {
            bay = UnityEngine.Object.FindObjectsOfType<SeaTruckDockingBay>().FirstOrDefault();
            return bay != null;
        }

        private static int OverlappingNonTruckCount(Vector3 center)
        {
            return OverlappingNonTruckColliders(center).Count;
        }

        private static List<string> OverlappingNonTruckColliders(Vector3 center)
        {
            var list = new List<string>();
            var colliders = Physics.OverlapSphere(center, OverlapRadius);
            foreach (var c in colliders)
            {
                if (IsTruckCollider(c))
                {
                    continue;
                }
                list.Add(c.name);
            }
            return list;
        }

        private static bool IsTruckCollider(Collider c)
        {
            Transform t = c.transform;
            while (t != null && t.parent != null)
            {
                t = t.parent;
            }
            return t != null && t.name.StartsWith("SeaTruck");
        }

        private static string Fmt(Vector3 v)
        {
            return $"({v.x:F2},{v.y:F2},{v.z:F2})";
        }

        private static string FmtRot(Quaternion q)
        {
            Vector3 e = q.eulerAngles;
            return $"({e.x:F1},{e.y:F1},{e.z:F1})";
        }

        private static void LogInfo(string msg)
        {
            Mod.logger.LogInfo($"[Seatruck] {msg}");
        }

        private static void LogWarning(string msg)
        {
            Mod.logger.LogWarning($"[Seatruck] {msg}");
        }
    }

    #region Patches

    // Create the persistent watchdog component
    [HarmonyPatch(typeof(uGUI), nameof(uGUI.Awake))]
    static class SetupSeaTruckWatchdog
    {
        public static void Postfix()
        {
            if (SeaTruckWatchdog.instance != null)
            {
                return;
            }
            var w = new GameObject(nameof(SeaTruckWatchdog)).AddComponent<SeaTruckWatchdog>();
            SeaTruckWatchdog.instance = w;
            UnityEngine.Object.DontDestroyOnLoad(w);
        }
    }

    // Log the state when the seatruck (prawn suit) bay is (re)loaded
    [HarmonyPatch(typeof(SeaTruckDockingBay), nameof(SeaTruckDockingBay.Start))]
    static class SeaTruckDockingBay_Start_Log
    {
        public static void Postfix(SeaTruckDockingBay __instance)
        {
            SeaTruckWatchdog.LogState($"SeaTruckDockingBay.Start bay={__instance.name}");
        }
    }

    // Log the state when a vehicle finishes docking
    [HarmonyPatch(typeof(Dockable), nameof(Dockable.OnDockingComplete))]
    static class Dockable_OnDockingComplete_Log
    {
        public static void Postfix(Dockable __instance)
        {
            SeaTruckWatchdog.LogState("Dockable.OnDockingComplete");
        }
    }

    // Log the state when a vehicle finishes undocking
    [HarmonyPatch(typeof(Dockable), nameof(Dockable.OnUndockingComplete))]
    static class Dockable_OnUndockingComplete_Log
    {
        public static void Postfix(Dockable __instance, Player player)
        {
            SeaTruckWatchdog.LogState("Dockable.OnUndockingComplete");
        }
    }

    #endregion
}

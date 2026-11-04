using UnityEngine;

namespace SubmersedVR
{
    // Debug F10 (requires the "Debug Overlays" toggle): escape a STUCK state
    // (a cinematic ride that never ended: the player's controller stays
    // disabled, position frozen, damage ticking -- e.g. a SeaTruck
    // stop-piloting get-up ride whose end event was lost). Ends EVERY active
    // ride, restores the player state, and if the player is still inside a
    // SeaTruck cabin, places them at its exit point.
    //
    // Ported from the SMB line (F9 FreeDockedVehicles dropped: the
    // SeaTruckWatchdog's F9/Shift+F9/Options-button rescue + auto-detection
    // covers the docked/stuck truck cases). Speed resets are owned by the
    // mirror watchdog (CinematicsDiagnostics): EndCinematicMode(true) drops
    // the ride and the watchdog's speed safety net resets the accelerated
    // director/animator on the next ManagedLateUpdate ("cine ended" branch).
    static class SeatruckDebugHack
    {
        public static void ForceUnstick()
        {
            int ended = 0;
            var rides = Object.FindObjectsOfType<PlayerCinematicController>();
            if (rides != null)
            {
                foreach (var r in rides)
                {
                    if (r == null || !r.IsCinematicModeActive())
                    {
                        continue;
                    }
                    Mod.logger.LogInfo($"[ForceUnstick] ending stuck ride '{r.gameObject.name}'");
                    r.EndCinematicMode(true);
                    ended++;
                }
            }

            var player = Player.main;
            if (player == null)
            {
                return;
            }
            //EndCinematicMode already restores the controller / stats /
            //cinematicModeCount, but restore defensively in case no ride
            //reported itself active (a desynced state).
            player.cinematicModeActive = false;
            player.playerController.SetEnabled(true);
            player.UnfreezeStats();

            //If the player is still inside a SeaTruck (stuck in the cabin
            //after a stop-piloting ride), place them at its exit point.
            var craft = Object.FindObjectsOfType<Dockable>();
            if (craft != null)
            {
                foreach (var c in craft)
                {
                    if (c == null || c.truckSegment == null)
                    {
                        continue;
                    }
                    if (CraftData.GetTechType(c.gameObject) != TechType.SeaTruck)
                    {
                        continue;
                    }
                    if ((player.transform.position - c.truckSegment.transform.position).sqrMagnitude > 4f * 4f)
                    {
                        continue;
                    }
                    if (c.truckSegment.FindExitPoint(out Vector3 exit, out bool skip, SeaTruckAnimation.Animation.Exit))
                    {
                        player.transform.parent = null;
                        player.transform.position = exit;
                        player.ToNormalMode(findNewPosition: false);
                        VRUtil.Recenter();
                        Mod.logger.LogInfo($"[ForceUnstick] player was inside '{c.gameObject.name}' -> placed at exit point {exit}");
                    }
                    else
                    {
                        Mod.logger.LogInfo($"[ForceUnstick] player near '{c.gameObject.name}' but FindExitPoint failed");
                    }
                    break;
                }
            }
            Player.allowSaving = true;
            Mod.logger.LogInfo($"[ForceUnstick] done, ended {ended} ride(s)");
        }
    }
}

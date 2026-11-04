using System.Collections.Generic;

namespace SubmersedVR
{
    // 28/10: per-cinematic rules in the code (spec
    // docs/superpowers/specs/2026-10-28-player-cinematics-management-design.md
    // section 1). Replaces the hard-coded skip list of Cinematics.cs: unknown
    // cinematics default to Play (= legacy behavior) + full contract capture
    // in the log. Entries are added case by case from the [Cine] logs.
    // When in doubt prefer { Play, 500 } ("clean skip": the timeline ends in
    // ~1-2 frames, EventTrack/anim events still fire via TriggerInRange,
    // endTransform applied) over { Skip } (no timeline events at all).
    public enum CineMode
    {
        Play,      // play the animation, position + rotation copied ("as intended")
        PlayNoRot, // play the animation, position only (anti-nausea)
        Skip       // native SkipCinematic (endTransform teleport, no timeline events)
    }

    public struct CineRule
    {
        public CineMode Mode;
        public float Speed; // 1 = normal, 5/20/500 = acceleration (with Play)
        public string Note;
        // 28/10 build 8 (user-approved principle, challenger review):
        // force-commit ~0.5s after entering Update (total ~0.75s from In)
        // for cines whose accelerated run provably cannot complete.
        // TABLE-GATED ONLY - never inferred from telemetry. Precondition
        // checked at runtime: endTransform must be present (the commit
        // teleports to the verified exit pose).
        public bool EarlyRelease;
        // 28/10 build 9 (test 10 data): speed to use during the In phase
        // (0 = same as Speed). The module's commit event sits at 99.2% of
        // the 1.25s "get in" clip = 1.24s anim time = 0.248s at 5x -
        // 2ms BEFORE the In->Update bool flip (In = 0.25s real time) ->
        // commit during In desyncs the module logic (test 9). In at 1x
        // keeps the event out of In (0.25s of 1.24s = 20%); acceleration
        // from Update commits after the flip (enter ~0.45s, exit ~0.53s).
        public float InSpeed;
    }

    static class CinematicsTable
    {
        public static readonly CineRule Default = new CineRule
        {
            Mode = CineMode.Play,
            Speed = 1f,
            Note = "unknown: played as legacy + contract capture"
        };

        static readonly Dictionary<string, CineRule> Rules = new Dictionary<string, CineRule>
        {
            // Proven skippable (27/10 logs)
            ["hatch_side_anim"] = new CineRule { Mode = CineMode.Skip, Speed = 1f, Note = "proven 27/10: skippable" },
            ["precursor_teleporter_cin"] = new CineRule { Mode = CineMode.Skip, Speed = 1f, Note = "proven 27/10: skippable" },

            // 28/10 test 1 (user: the parts where the BODY moves are the
            // annoying ones - make them instant). Character-body parts with
            // a working endTransform: skip = instant teleport.
            // NEVER accelerate a body part (fast body motion = nausea);
            // acceleration only makes sense for vehicle/timeline cines.
            ["hatch_end_anim"] = new CineRule { Mode = CineMode.Skip, Speed = 1f, Note = "body, 1.3s, endTransform OK, skip works (test 1+2)" },
            ["hatch_alienContainment_anims"] = new CineRule { Mode = CineMode.Skip, Speed = 1f, Note = "containment pool hatch, 1.3s, endTransform 0.14-0.33m off (test 2)" },

            // VEHICLE-ENTRY parts must NOT be skipped: the vehicle's
            // "player entered" state machine is confirmed by the animation
            // completing - a skip teleports the player but leaves the game
            // convinced they are outside (swim state, stuck, cannot exit -
            // test 2, prawn module).
            // 28/10 test 9 (build 8): REVERTED to 1x. The x5 acceleration
            // breaks this module's ENTER: the commit fired at the end of
            // In (0.25s) before the "get in" clip (1.25s, event
            // OnPlayerCinematicModeEnd@1.24) completed -> player not
            // mounted (mounted=null, 0.92m from the seat, walking mode
            // outside, fell). The 0.53s early release on the EXIT
            // desynced the game's vehicle logic afterwards (VEHICLE-ENTER
            // Exosuit teleport + EJECT chain) - the module's real-time
            // logic needs the full sequence; only the 2.5s net was safe.
            // 1x = game-native (enter ~1.5s, exit ~3s, both proven).
            // 1x baseline run (build 8c) captures the machine timeline to
            // design a correct x5 approach.
            // 28/10 test 10 (1x baseline, build 8c): the machine goes
            // DIRECTLY to the commit clip at the In start (get in 1.25s /
            // get out 1.67s), commit = AnimationEvent at 99.2% of that
            // clip, In = 0.25s real time, bool flip (enter/exit=1) at the
            // end of In. Build 9: phase-aware speed (In at 1x, Update at
            // 5x) - the invariant is that the commit event must not fire
            // before the bool flip (at 5x throughout, it fired 2ms early
            // on the enter, test 9).
            ["seatruck_module_prawn_anim"] = new CineRule { Mode = CineMode.Play, Speed = 5f, InSpeed = 1f, Note = "28/10 build 9: In@1x + Update@5x (commit event must not fire before the In->Update bool flip; test 9: 5x throughout committed the enter 2ms early, player not mounted)" },

            // 28/10 test 4 (user request): the moonpool ladder in/out - body
            // climb 1.6s, endTransform OK (nearEnd 0.2m at the Out end),
            // instant = user preference. NOTE: "model" is a generic GO name
            // - if another cinematic uses a GO named "model" it is skipped
            // too (only the moonpool ladder was seen in the 28/10 logs).
            ["model"] = new CineRule { Mode = CineMode.Skip, Speed = 1f, Note = "28/10 test 4 (user): moonpool ladder in/out, body, endTransform OK - skip requested" },

            // Dock machinery group: the dock has 8 sibling controllers that
            // the game ends together (OnPlayerCinematicModeEndForward
            // forwards the main's anim event to the group). None has an
            // endTransform (skip = no teleport, just not played). All were
            // skipped pre 28/10. The main one (DockPlayerCinematic,
            // playInVr=true, receiver moonpool_dock) is NOT skippable:
            // skipping it left the truck HUD stuck (old log 27/10).
            ["LaunchFromLeft"] = new CineRule { Mode = CineMode.Skip, Speed = 1f, Note = "dock machinery, no endTransform, proven pre 28/10" },
            ["LaunchFromRight"] = new CineRule { Mode = CineMode.Skip, Speed = 1f, Note = "dock machinery, no endTransform, proven pre 28/10" },
            ["LaunchFromLeftExo"] = new CineRule { Mode = CineMode.Skip, Speed = 1f, Note = "dock machinery, no endTransform, proven pre 28/10" },
            ["LaunchFromRightExo"] = new CineRule { Mode = CineMode.Skip, Speed = 1f, Note = "dock machinery, no endTransform, proven pre 28/10" },
            ["LaunchFromLeftTruck"] = new CineRule { Mode = CineMode.Skip, Speed = 1f, Note = "dock machinery, no endTransform, proven pre 28/10" },
            ["LaunchFromRightTruck"] = new CineRule { Mode = CineMode.Skip, Speed = 1f, Note = "dock machinery, no endTransform, proven pre 28/10" },
            ["ExoDockPlayerCinematic"] = new CineRule { Mode = CineMode.Skip, Speed = 1f, Note = "dock machinery, no endTransform, proven pre 28/10" },
            ["TruckDockPlayerCinematic"] = new CineRule { Mode = CineMode.Skip, Speed = 1f, Note = "dock machinery, no endTransform, proven pre 28/10" },

            // 30/10 live log: moonpool_launchleft/right are ANIMATOR PARAMS of
            // the seatruck_anim ride (with the enter_left/enter_right 4.17s
            // clips), not ride GO names - the SMB line identified the launch
            // ride by its anim param, these name-keyed entries can never
            // match. The launch therefore currently runs under the
            // ["seatruck_anim"] rule (5x). Restoring the proven 20x (18/10:
            // 50x/500x LOST the carrier's internal lift->push-out
            // AnimationEvent, truck stuck at y=-12.9) needs a param-aware
            // rule (ride name + animParam) - follow-up, not a 1-line change.
            ["moonpool_launchleft"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 20f, Note = "DEAD KEY (30/10): anim param of seatruck_anim, not a ride name - kept for reference" },
            ["moonpool_launchright"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 20f, Note = "DEAD KEY (30/10): anim param of seatruck_anim, not a ride name - kept for reference" },

            // 31/10: precursor_elevator_cine 500x REVERTED (user test): the
            // elevator car moves in REAL TIME (game logic), the animator
            // only poses the body and times the end event - and the ride
            // has NO endTransform (31/10 log: END endTransform=NULL). At
            // 500x the ride "ended" in 0.31s with the player only 1.92m
            // along the shaft -> game state desync (stuck at the bottom,
            // could no longer ascend, tilted landing view). Default
            // {Play, 1x} = the 13.63s ride the user validated twice on
            // 30/10. (No table entry = default rule.)

            // Special case: first sleep played, following ones skipped (toggle
            // kept in StartCinematicModeFixer - CinematicsVR.hasSlept)
            ["seatruck_module_sleeper_anim"] = new CineRule { Mode = CineMode.Play, Speed = 1f, Note = "special hasSlept: first played, then skipped" },

            // The 18 cinematics played WITHOUT rotation before 28/10 (explicit
            // anti-nausea) - pre-filled PlayNoRot so the initial behavior is
            // exactly today's. Move each to Play (rotation) when OK in test.
            ["seatruck_anim"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 5f, Note = "28/10 test 3 logs: piloting commit = StartPiloting at hatch click, independent of the cin; x5 (player must be animated to the pilot seat - no skip)" },
            // 30/10 live log: Bottom_Collision/Top_Collision are the GENERIC
            // LADDER-CLIMB rides (user: "les échelles dans la neige,
            // entièrement animées"). Contract: 2.0s timeline, animator
            // ladder_climbCineAttach, tracks Ladder (the ladder itself
            // animates) + Player (full-body climb) + on-off + step1..5
            // FMODEvents, useEnd=true endTransform. 500x "clean skip":
            // the 2s timeline ends in ~4ms (body motion is effectively
            // instant, no nausea) while the EventTracks still fire via
            // TriggerInRange (a Skip would drop the on-off/step events
            // entirely) and the endTransform teleports to the ladder end.
            ["Bottom_Collision"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 500f, Note = "ladder climb (climb UP, 30/10 log): 500x clean skip - events preserved via TriggerInRange, endTransform applied. Revert to 1f if the on-off/step events desync" },
            // 31/10 log: the down-variant ride name is Top_Left_Collision,
            // not Top_Collision (the 30/10 "seen at world load" capture was
            // misread) - the Top_Collision key never matched: this
            // session's descent played 2.03s at 1x instead of the
            // instant 500x.
            ["Top_Left_Collision"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 500f, Note = "ladder climb (down variant, 31/10 log: Top_Left_Collision, same ladder_climbCineAttach system): 500x clean skip - same notes as Bottom_Collision" },
            ["Top_Collision"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 500f, Note = "DEAD KEY (31/10): real ride name is Top_Left_Collision - kept for reference" },
            ["MaintenanceBox"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 1f, Note = "played no-rot pre 28/10 - move to Play if OK" },
            ["sanctuary_cube_cin"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 1f, Note = "played no-rot pre 28/10 - move to Play if OK" },
            ["marge_intro_cin"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 1f, Note = "played no-rot pre 28/10 - move to Play if OK" },
            ["marge_base1_cin"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 1f, Note = "played no-rot pre 28/10 - move to Play if OK" },
            ["marge_greenhouse_cin"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 1f, Note = "played no-rot pre 28/10 - move to Play if OK" },
            // 18/10 SMB: the whole dock sequence at 500x was user-confirmed
            // ("tres bien pour le docking"): the native end AnimationEvent
            // still fires at 500x and closes the ride in ~30ms. The watchdog
            // STUCK-NET force-ends a stuck accelerated ride (2.5s cap).
            ["DockPlayerCinematic"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 500f, Note = "dock ride (animator moon_pool_anim): 500x ported from SMB 18/10, native end event closes the ride, STUCK-NET backs it" },
            ["Mobile_Extractor_anim"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 1f, Note = "played no-rot pre 28/10 - move to Play if OK" },
            ["Precursor_fabricator_room_anim"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 1f, Note = "played no-rot pre 28/10 - move to Play if OK" },
            ["NoReturnTimeline"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 1f, Note = "played no-rot pre 28/10 - move to Play if OK" },
            ["Greetings"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 1f, Note = "played no-rot pre 28/10 - move to Play if OK" },
            ["RepairPillar1"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 1f, Note = "played no-rot pre 28/10 - move to Play if OK" },
            ["RepairPillar2"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 1f, Note = "played no-rot pre 28/10 - move to Play if OK" },
            ["ReturnArmsLeft"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 1f, Note = "played no-rot pre 28/10 - move to Play if OK" },
            ["ReturnArmsRight"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 1f, Note = "played no-rot pre 28/10 - move to Play if OK" },
            ["EnterShip"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 1f, Note = "played no-rot pre 28/10 - move to Play if OK" },
            ["Takeoff"] = new CineRule { Mode = CineMode.PlayNoRot, Speed = 1f, Note = "played no-rot pre 28/10 - move to Play if OK" },
        };

        public static CineRule Get(string name)
        {
            return Rules.TryGetValue(name, out var rule) ? rule : Default;
        }
    }
}

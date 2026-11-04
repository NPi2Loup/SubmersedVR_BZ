using HarmonyLib;
using UnityEngine;
using System.Collections;
using UWE;
using System;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UWEXR;

namespace SubmersedVR
{
    extern alias SteamVRActions;
    extern alias SteamVRRef;

    using rail;
    using UnityEngine.Playables;
    using UnityEngine.XR;

    // Tweaks regarding the cinematics in VR mode of the game
    static class CinematicsVR
    {
        public static bool hasSlept = false;
    }

    #region Patches

    [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.SetVrActiveParam))]
    public static class SetVrActiveParamFixer
    {
        public static void Postfix(PlayerCinematicController __instance)
        {
            string text = "vr_active";
            //Mod.logger.LogInfo($"PlayerCinematicController.SetVrActiveParam called animationMode={VRGameOptions.GetVrAnimationMode()}");
            bool vrAnimationMode = VRGameOptions.GetVrAnimationMode();
            try
            {
                if (__instance.animator != null)
                {
                    __instance.animator.SetBool(text, vrAnimationMode);
                }
                for (int i = 0; i < __instance.animParamReceivers.Length; i++)
                {
                    IAnimParamReceiver component = __instance.animParamReceivers[i].GetComponent<IAnimParamReceiver>();
                    if (component != null)
                    {
                        component.ForwardAnimationParameterBool(text, vrAnimationMode);
                    }
                }
            }
            catch (System.Exception e)
            {
                Mod.logger.LogWarning($"SetVrActiveParamFixer: receiver failed: {e}");
            }
        }
    }

    // In VR a skipped cinematic must still teleport the player to its
    // endTransform: on desktop the animation itself moves the player, but in
    // VR the rig does not follow the cinematic camera. The game's
    // onlyUseEndTransformInVr flag gates that teleport on
    // GetVrAnimationMode(), which is FALSE in static VR mode - so the exit
    // cinematics (hatch_end_anim exit_end, model ladder_out) skipped with
    // "NOT MOVED" and the player stayed in the base (27/10 log). Force the
    // endTransform teleport whenever VR is active.
    [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.UseEndTransform))]
    public static class UseEndTransformVrFix
    {
        // bool prefix (NOT void): returning false both skips the original
        // and makes __result the return value - unambiguous Harmony
        // semantics (a void prefix still runs the original).
        public static bool Prefix(PlayerCinematicController __instance, ref bool __result)
        {
            if (!__result && __instance.endTransform != null && XRSettings.enabled)
            {
                __result = true;
                Mod.logger.LogInfo($"[Cine] UseEndTransform forced true in VR (name={__instance.gameObject.name})");
                return false;
            }
            return true;
        }
    }


    //Disabled cinematic sequences no longer control the player's head rotation greatly reducing nausea affect of cinematics
    [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.UpdatePlayerPosition))]
    public static class ManagedLateUpdateFixer
    {
        public static bool Prefix(PlayerCinematicController __instance)
        {
            Transform component = __instance.player.GetComponent<Transform>();
            Transform component2 = MainCameraControl.main.GetComponent<Transform>();
            component.position = __instance.animatedTransform.position;
            component2.position = __instance.player.camAnchor.position;
        
            // 28/10 spec section 3: rotation rule from the code table -
            // legacy (checked) always copies rotation (unchanged);
            // unchecked: Play = with rotation ("as intended"),
            // PlayNoRot/Skip = without. Unknown default = Play.
            bool enableRotation = VROptions.enableCinematics
                || CinematicsTable.Get(__instance.gameObject.name).Mode == CineMode.Play;
            if (enableRotation)
            {
                component.rotation = __instance.animatedTransform.rotation;
                component2.rotation = __instance.animatedTransform.rotation;
            }
		
            return false;
        }
    }

    //Fixes PDA being offset after an animated climb/exit/etc
    //Bypassing the compiled code disables the system hand animation override during the cinematic. 
    //Might be better to somehow reset hands at the end instead?
    
    [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.InitDirector))]
    public static class InitDirectorFixer
    {
        public static bool Prefix(PlayerCinematicController __instance)
        {
            return false;
        }
    }
    

    [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.OnDirectorStopped))]
    public static class StoppedDirectorFixer
    {
        public static void Postfix(PlayerCinematicController __instance)
        {
            VRHands.instance.ResetHandTargets();
        }
    }
    
    //Enable specific cinematics to be bypassed
    [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.StartCinematicMode))]
    public static class StartCinematicModeFixer
    {
        public static bool Prefix(PlayerCinematicController __instance, Player setplayer)
        {
            VRUtil.Recenter();
            if (__instance.debug)
            {
                Debug.Log(__instance.gameObject.name + ".StartCinematicMode");
            }
            if (__instance.cinematicModeActive)
            {
                if (__instance.debug)
                {
                    Debug.Log(__instance.gameObject.name + " cinematic already active!");
                }
                return false;
            }

            //Ported from the SMB line: the bay's dock-ride controllers
            //(DockPlayerCinematic / exosuit, animator = moon_pool_anim) carry
            //the player to the docked position. Their clip runs 15s+ at 1x and
            //ends on an AnimationEvent.
            bool isDockCinematic = __instance.GetComponentInParent<VehicleDockingBay>() != null
                || (__instance.animator != null && __instance.animator.gameObject.name.Contains("moon_pool"));
            //Orphan guard: a dock ride started without a player (a save loaded
            //mid-dock replays the dock completion; the dock animation and its
            //end event do not replay) can never end. It would pin the static
            //cinematicModeCount > 0 forever, blocking every gated useable
            //(seatruck hatch, base entry, PDA, ...) and blocking saves. Do not
            //start the ride; settle the dockable instead so a stuck
            //isInTransition state is cleared.
            if (isDockCinematic && !setplayer)
            {
                Mod.logger.LogInfo($"{__instance.gameObject.name}: dock ride requested with no player (mid-dock save?) -> ride not started, settling dockable");
                var docked = CinematicFallbackWatcher.FindDockedTruck();
                if (docked != null)
                {
                    docked.OnDockingComplete();
                }
                return false;
            }

            CinematicsDiagnostics.LogStart(__instance, setplayer);
            Mod.logger.LogInfo($"StartCinematicMode name = {__instance.gameObject.name} playInVR = {__instance.playInVr} cinematicsEnabled = {VROptions.enableCinematics} ");
            __instance.player = null;
            // 28/10 spec section 2: per-cinematic rules in the code
            // (CinematicsTable) replace the hard-coded skip list. Unknown =
            // Play (legacy) + contract capture. playInVr=true is never
            // skipped (current semantics). The sleeper toggle is unchanged
            // and applies in both modes.
            string name = __instance.gameObject.name;
            bool legacy = VROptions.enableCinematics;
            bool skipIntro = name == "IntroCinematics(Clone)" && VROptions.skipIntro;
            bool sleeperSkip = false;
            if (name == "seatruck_module_sleeper_anim")
            {
                sleeperSkip = CinematicsVR.hasSlept;
                CinematicsVR.hasSlept = !CinematicsVR.hasSlept;
            }
            bool tableSkip = !legacy && !__instance.playInVr
                && CinematicsTable.Get(name).Mode == CineMode.Skip;
            if (skipIntro || sleeperSkip || tableSkip)
            {
                if (__instance.debug)
                {
                    Debug.Log(name + " skip cinematic");
                }
                Mod.logger.LogInfo($"[Cine] SKIP-DECISION name={name} intro={skipIntro} sleeper={sleeperSkip} table={tableSkip}");
                __instance.SkipCinematic(setplayer);
                return false;
            }
            // Played path (spec section 2): goal listener on ALL played
            // paths. 28/10 build 2: the no-director accel fallback is gone -
            // the watchdog applies Animator.speed to animator-only cinematics.
            CinematicsDiagnostics.CineGoalListener.currentCine = __instance;
            CinematicsDiagnostics.CineGoalListener.Add();
            //Ported from the SMB line: the seatruck entry/exit rides also play
            //the player MODEL's sit/stand transition (playerViewAnimationName,
            //e.g. 'seatruck_drive_in') at 1x -- clearly visible on re-entry.
            //Speed the player's animators up for a 5s window covering the
            //transition, then restore. Safe: the body sit/stand clips carry no
            //gameplay events (the sitting state is set by the flow, not by an
            //animation event), so no desync risk. (Dock rides are handled by
            //the table: their animator runs at the table speed until the
            //ride's native end event.)
            bool isSeatruckCinematic = __instance.GetComponentInParent<SeaTruckAnimation>() != null;
            if (XRSettings.enabled && isSeatruckCinematic && !isDockCinematic && setplayer != null)
            {
                CinematicFallbackWatcher.EnsureBehaviour().StartCoroutine(CinematicFallbackWatcher.PlayerTransitionSpeedUp(setplayer));
                Mod.logger.LogInfo($"{name}: player sit/stand transition at 500x for 5s (playerViewAnim='{__instance.playerViewAnimationName}')");
            }
            if (__instance.animator != null)
            {
                __instance.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            __instance.cinematicModeActive = true;
            __instance.timeCinematicModeStarted = Time.time;
            if (__instance.director != null)
            {
                __instance.director.played += __instance.OnDirectorPlayed;
                __instance.director.stopped += __instance.OnDirectorStopped;
                if (__instance.restoreControl)
                {
                    __instance.Invoke("DoFailSafeCancel", (float)__instance.director.duration + 0.2f);
                }
            }
            if (setplayer)
            {
                __instance.SetPlayer(setplayer);
                __instance.Subscribe(__instance.player, true);
            }
            __instance.state = PlayerCinematicController.State.In;
            if (__instance.informGameObject != null)
            {
                __instance.informGameObject.SendMessage("OnPlayerCinematicModeStart", __instance, SendMessageOptions.DontRequireReceiver);
            }
            if (__instance.player)
            {
                Transform component = __instance.player.GetComponent<Transform>();
                Transform component2 = MainCameraControl.main.GetComponent<Transform>();
                __instance.cameraPosition = __instance.player.camAnchor.InverseTransformPoint(component2.position);
                __instance.player.frozenMixin.Unfreeze();
                __instance.player.cinematicModeActive = true;
                __instance.player.FreezeStats();
                __instance.player.playerController.SetEnabled(false);
                Player.main.GetPDA().Close();
                if (__instance.leftIKTarget || __instance.rightIKTarget)
                {
                    __instance.player.armsController.SetWorldIKTargetSmooth(__instance.leftIKTarget, __instance.rightIKTarget, __instance.ikSmoothTime);
                }
                __instance.cameraFromRotation = component.rotation;
                __instance.playerFromPosition = component.position;
                __instance.playerFromRotation = component.rotation;
                if (__instance.playerViewInterpolateAnimParam.Length > 0)
                {
                    SafeAnimator.SetBool(__instance.player.GetComponentInChildren<Animator>(), __instance.playerViewInterpolateAnimParam, true);
                }
            }
            if (__instance.animator != null && !string.IsNullOrEmpty(__instance.interpolateAnimParam))
            {
                __instance.animator.SetBool(__instance.interpolateAnimParam, true);
            }
            if (__instance.interpolateDuringAnimation)
            {
                __instance.animState = true;
            }
            if (__instance.debug)
            {
                Debug.Log(__instance.gameObject.name + " successfully started cinematic");
            }
            float num = (__instance.director == null) ? 60f : ((float)__instance.director.duration + 10f);
            float num2 = Time.time + num;
            PlayerCinematicController.cinematicActivityExpireTime = ((PlayerCinematicController.cinematicModeCount == 0) ? num2 : Mathf.Max(PlayerCinematicController.cinematicActivityExpireTime, num2));
            PlayerCinematicController.cinematicModeCount++;
            HandReticle.main.RequestCrosshairHide();

            return false;
        }
    }

    //Ported from the SMB line: Prawn Suit (internal class name: Exosuit)
    //entry/exit. Unlike the SeaTruck (whose entry/exit is a
    //PlayerCinematicController = seatruck_anim), the Prawn's entry/exit is NOT
    //a cinematic: Vehicle.EnterVehicle teleports the player into the cockpit
    //(EnterLockedMode) and flips player.sitting, so the player MODEL's
    //sit/stand transition plays at 1x in VR -- the same "visible" issue as the
    //SeaTruck's re-entry. Apply the same PlayerTransitionSpeedUp (500x for
    //5s) on both. Enter hooks Exosuit.EnterVehicle (its override, so
    //Prawn-specific). Exit: the Prawn sits in LockedPiloting mode and every
    //exit funnels through Player.ExitLockedMode (TryEject -> ToNormalMode ->
    //ExitLockedMode); filter there on currentMountedVehicle being an Exosuit
    //(still set in the prefix -- the method nulls it internally). Safe: body
    //sit/stand clips carry no gameplay events.
    [HarmonyPatch(typeof(Exosuit), "EnterVehicle", new[] { typeof(Player), typeof(bool), typeof(bool) })]
    static class ExosuitEnterSitTransition
    {
        static void Postfix(Exosuit __instance, Player player)
        {
            if (player == null || !XRSettings.enabled)
            {
                return;
            }
            Mod.logger.LogInfo("[Exosuit] enter -> player sit transition at 500x for 5s");
            CinematicFallbackWatcher.EnsureBehaviour().StartCoroutine(CinematicFallbackWatcher.PlayerTransitionSpeedUp(player));
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.ExitLockedMode))]
    static class ExosuitExitSitTransition
    {
        static void Prefix(Player __instance)
        {
            if (__instance == null || !(__instance.currentMountedVehicle is Exosuit) || !XRSettings.enabled)
            {
                return;
            }
            Mod.logger.LogInfo("[Exosuit] exit -> player stand transition at 500x for 5s");
            CinematicFallbackWatcher.EnsureBehaviour().StartCoroutine(CinematicFallbackWatcher.PlayerTransitionSpeedUp(__instance));
        }
    }

    //Ported from the SMB line: when a SeaTruck docks at a base bay while the
    //player is piloting, the game's bay flow transfers the player (unparent +
    //ToNormalMode + dockPlayerCinematic) but never calls
    //SeaTruckMotor.StopPiloting: motor.piloting and
    //Player.main.inSeatruckPilotingChair stay true, so the player model keeps
    //the piloting pose ("piloting_seatruck" bool) and movement input is still
    //routed to the docked (immobile) truck -- the player is left with dead
    //keys. Stop the piloting when the docked segment's motor is piloted: the
    //player is released to normal mode at the seat (ExitLockedMode), then the
    //bay's player transfer + dock cinematic take over and end with the player
    //free in the bay.
    [HarmonyPatch(typeof(Dockable), nameof(Dockable.SetDocked), new[] { typeof(IDockingBay), typeof(Vehicle.DockType) })]
    static class SeatruckDockPilotCleanup
    {
        static void Postfix(Dockable __instance, IDockingBay bay)
        {
            if (bay == null || __instance == null || __instance.truckMotor == null)
            {
                return;
            }
            if (!__instance.truckMotor.IsPiloted())
            {
                return;
            }
            Mod.logger.LogInfo("SeatruckDockPilotCleanup: seatruck docked while piloting -> StopPiloting(forceStop) to release the pilot state");
            __instance.truckMotor.StopPiloting(waitForDocking: false, forceStop: true);
        }
    }

    //Ported from the SMB line (trimmed): the coroutine host + the helpers the
    //re-applied fixes need. The SMB full watcher (Schedule/Watch safety nets,
    //DockModeOverride, DockSequenceSpeed) is intentionally NOT ported: on this
    //base GetVrAnimationMode is permanently false (the desktop flow), the
    //watchdog (CinematicsDiagnostics) owns the accelerated-ride safety nets
    //(speed reset + STUCK force-end) and the per-cinematic speeds live in
    //CinematicsTable.
    static class CinematicFallbackWatcher
    {
        static FallbackBehaviour behaviour;

        public static FallbackBehaviour EnsureBehaviour()
        {
            if (behaviour == null || !behaviour)
            {
                var go = new GameObject("CinematicFallbackWatcher");
                UnityEngine.Object.DontDestroyOnLoad(go);
                behaviour = go.AddComponent<FallbackBehaviour>();
            }
            return behaviour;
        }

        public class FallbackBehaviour : MonoBehaviour
        {
        }

        //First docked truck found among the bays. The dock-ride controllers are
        //not children of the bay, so this is the only reliable lookup.
        public static Dockable FindDockedTruck()
        {
            var bays = UnityEngine.Object.FindObjectsOfType<VehicleDockingBay>();
            if (bays == null)
            {
                return null;
            }
            foreach (var b in bays)
            {
                if (b != null)
                {
                    var d = b.GetDockedObject();
                    if (d != null)
                    {
                        return d;
                    }
                }
            }
            return null;
        }

        //500x the player MODEL's animators for ~5s (a seatruck entry/exit
        //sit/stand transition, e.g. 'seatruck_drive_in': climb up to the
        //cockpit + sit, ~3-4s at 1x). ALL child animators: the first
        //version used GetComponentInChildren (first child in hierarchy
        //order -- often a hand-rig/PDA animator, not the body), so the body
        //pose anim (the visible "climb up and sit") still played at 1x.
        //The 5s window covers the state transition + the one-shot clip at
        //500x (it finishes in ~8ms); long-lived seated states are holds
        //(no visible effect), and the speed is restored after the window.
        public static System.Collections.IEnumerator PlayerTransitionSpeedUp(Player player)
        {
            var anims = player.GetComponentsInChildren<Animator>();
            foreach (var a in anims)
            {
                if (a != null)
                {
                    a.speed = 500f;
                }
            }
            yield return new WaitForSeconds(5f);
            foreach (var a in anims)
            {
                if (a != null)
                {
                    a.speed = 1f;
                }
            }
        }
    }

 /*
    [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.InitDirector))]
    public static class InitDirectorFixer
    {
        public static bool Prefix(PlayerCinematicController __instance)
        {
            if (__instance.director == null)
            {
                return false;
            }
            foreach (PlayableBinding playableBinding in __instance.director.playableAsset.outputs)
            {
                Type outputTargetType = playableBinding.outputTargetType;
                
                UnityEngine.Object sourceObject = playableBinding.sourceObject;
                string streamName = playableBinding.streamName;
                Type left = (sourceObject != null) ? sourceObject.GetType() : null;
                Mod.logger.LogInfo($"PlayerCinematicController.InitDirector name = { __instance.gameObject.name} left = {left} streamName = {streamName} outputTargetType = {outputTargetType}");

                if (left == typeof(AnimationTrack))
                {
                    if (streamName == "Player")
                    {
                        foreach (TimelineClip timelineClip in (sourceObject as AnimationTrack).GetClips())
                        {
                            AnimationClip anim = timelineClip.animationClip;                           
                            if(anim != null)
                            {
                                Mod.logger.LogInfo($"PlayerCinematicController.InitDirector animationClip = { anim.name} empty = {anim.empty}");
                                if(anim.name == "RT_climb_up")
                                {
                                    return false;
                                }
                                foreach (AnimationEvent ev in anim.GetEventsInternal())
                                {
                                     Mod.logger.LogInfo($"PlayerCinematicController.InitDirector event = {ev.functionName} {ev.GetType()}");
                                }
                            }
                            PropertyName exposedName = (timelineClip.asset as AnimationPlayableAsset).name;
                            Mod.logger.LogInfo($"PlayerCinematicController.InitDirector clipName = {exposedName}");
                        }
                        __instance.director.SetGenericBinding(sourceObject, __instance.player.playerAnimator);
                    }
                }
                else if (left == typeof(ControlTrack))
                {
                    GameObject gameObject = null;
                    if (streamName == "RightHand")
                    {
                        gameObject = __instance.player.socketRightHand;
                    }
                    else if (streamName == "LeftHand")
                    {
                        gameObject = __instance.player.socketLeftHand;
                    }
                    if (gameObject != null)
                    {
                        foreach (TimelineClip timelineClip in (sourceObject as ControlTrack).GetClips())
                        {
                            PropertyName exposedName = (timelineClip.asset as ControlPlayableAsset).sourceGameObject.exposedName;
                            Mod.logger.LogInfo($"PlayerCinematicController.InitDirector exposedName = {exposedName}");
                            __instance.director.SetReferenceValue(exposedName, gameObject);
                        }
                    }
                }
            }

            return false;
        }
    }
*/
/*
    //Checks UnityEngine.XR instead of UWEXR
    [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.SkipCinematic))]
    public static class SkipCinematicFixer
    {
        public static bool Prefix(PlayerCinematicController __instance, Player player)
        {
            Mod.logger.LogInfo($"Calling PlayerCinematicController.SkipCinematic");
            if (__instance.interruptAutoMove)
            {
                GameInput.SetAutoMove(false);
            }
            __instance.player = player;
            if (player)
            {
                Transform component = player.GetComponent<Transform>();
                Transform component2 = MainCameraControl.main.GetComponent<Transform>();
                if (__instance.UseEndTransform())
                {
                    Mod.logger.LogInfo($"PlayerCinematicController.SkipCinematic using EndTransform");
                    player.playerController.SetEnabled(false);
                    if (XRSettings.enabled)
                    {
                        MainCameraControl.main.ResetCamera();
                        VRUtil.Recenter();
                    }
                    component.position = __instance.endTransform.position;
                    component.rotation = __instance.endTransform.rotation;
                    component2.rotation = component.rotation;
                }
                player.playerController.SetEnabled(true);
                player.cinematicModeActive = false;
            }
            if (__instance.informGameObject != null)
            {
                Mod.logger.LogInfo($"PlayerCinematicController.SkipCinematic informGameObject = {__instance.informGameObject.ToString()}");
                __instance.informGameObject.SendMessage("OnPlayerCinematicModeEnd", __instance, SendMessageOptions.DontRequireReceiver);
            }
           return false;
        }
    }
*/

/*
    //Force Animations for climbing up and down the radio tower ladder (Bottom_Collision and Top_Left_Collision and Top_Right_Collision),
    //entering and exiting the seatruck (seatruck_anim), interacting with the AI cube (sanctuary_cube_cin)
    //because these interactions do not have an EndTranform set and so without the animation, the player is not moved
    //to the end location. Also in the case of the AI cube, not sure how/if the story would progress without the animation
    //Also handles the skip intro flag
    [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.StartCinematicMode))]
    public static class StartCinematicModeFixer
    {
        public static bool Prefix(PlayerCinematicController __instance, Player setplayer)
        {
            if (__instance.cinematicModeActive)
            {
                Mod.logger.LogInfo(__instance.gameObject.name + " cinematic already active!");
                return false;
            }
            __instance.player = null;
            bool skipIntro = __instance.gameObject.name == "IntroCinematics(Clone)" && VROptions.skipIntro;
            //Do not skip these or else actions won't happen or the story wont progress
            bool skipAnimations = !__instance.playInVr && VRGameOptions.GetVrAnimationMode() 
                && __instance.gameObject.name != "submarine_hatch_02_doorway_01_cin2" //enter/exit base through sub bay  
                //&& __instance.gameObject.name != "hatch_side_anim" //enter/exit base through hatch (can be skipped)                          
                && __instance.gameObject.name != "seatruck_anim" //enter/exit SeaTruck Motor section when not docked with base
                && __instance.gameObject.name != "seatruck_module_sleeper_anim" //when exiting the animation it puts the player on the ceiling and disables all input              
                && __instance.gameObject.name != "Top_Left_Collision" && __instance.gameObject.name != "Top_Right_Collision" && __instance.gameObject.name != "Bottom_Collision" //ladder for comm tower                
                && __instance.gameObject.name != "MaintenanceBox" // Animation of installing the maintence override (has a clipping issue slot0011)
                && __instance.gameObject.name != "sanctuary_cube_cin" // Cutscene when encountering AI Cube
                && __instance.gameObject.name != "marge_intro_cin" // Cutscene when meeting Marge for the first time (slot0001)               
                && __instance.gameObject.name != "marge_base1_cin" // Cutscene when entering marge's home (slot0009)
                && __instance.gameObject.name != "marge_greenhouse_cin" // Cutscene when entering marge's greenhouse (slot0012)
                && __instance.gameObject.name != "DockPlayerCinematic" //enter/exit seatruck when docked at base (hud doesnt release)
                && __instance.gameObject.name != "Mobile_Extractor_anim" //antidote insertion (slot 0015) Without this it still works but you dont see the bottle connected to the injector
                && __instance.gameObject.name != "Precursor_fabricator_room_anim" //Alien body mind transfer
                && __instance.gameObject.name != "NoReturnTimeline" //Alien transfer gate
                //&& __instance.gameObject.name != "precursor_teleporter_cin" //Alien gate cinematic (can be skipped)
                && __instance.gameObject.name != "Greetings" //Alien world cinematic       
                && __instance.gameObject.name != "RepairPillar1" //Repair Pillar 1                        
                && __instance.gameObject.name != "RepairPillar2" //Repair Pillar 2
                && __instance.gameObject.name != "ReturnArmsLeft" //Return robot arms   
                && __instance.gameObject.name != "ReturnArmsRight" //Return robot arms   
                && __instance.gameObject.name != "EnterShip" //Enter escape ship from below   
                && __instance.gameObject.name != "Takeoff" //Brace yourself on escape ship                                                                     
                //&& __instance.gameObject.name != "Necklace_Scene_Placements" //Picking up the necklace (slot 0017) does nothing with InitDirectorFixer disabled. When skipped ends with PlayerCinematicController.SkipCinematic informGameObject = FrozenLeviathan_Necklace(Clone)
                && __instance.gameObject.name != "IntroCinematics(Clone)";  //crash landing on planet  
            
            if(__instance.gameObject.name == "seatruck_module_sleeper_anim")   
            {
                skipAnimations = CinematicsVR.hasSlept;
                CinematicsVR.hasSlept = !CinematicsVR.hasSlept;
            }     
            
            Mod.logger.LogInfo($"{__instance.gameObject.name}.StartCinematicMode skipIntro = {skipIntro} skipAnimations = {skipAnimations} "); 
            //if (!__instance.playInVr && VRGameOptions.GetVrAnimationMode())
            if(skipIntro || skipAnimations)
            {
                Mod.logger.LogInfo(__instance.gameObject.name + " skipping cinematic");          
                __instance.SkipCinematic(setplayer);
                return false;
            }
            if (__instance.animator != null)
            {
                __instance.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            __instance.cinematicModeActive = true;
            __instance.timeCinematicModeStarted = Time.time;
            if (__instance.director != null)
            {
                __instance.director.played += __instance.OnDirectorPlayed;
                __instance.director.stopped += __instance.OnDirectorStopped;
                if (__instance.restoreControl)
                {
                    __instance.Invoke("DoFailSafeCancel", (float)__instance.director.duration + 0.2f);
                }
            }
            if (setplayer)
            {
                __instance.SetPlayer(setplayer);
                __instance.Subscribe(__instance.player, true);
            }
            __instance.state = PlayerCinematicController.State.In;
            if (__instance.informGameObject != null)
            {
                __instance.informGameObject.SendMessage("OnPlayerCinematicModeStart", __instance, SendMessageOptions.DontRequireReceiver);
            }
            if (__instance.player)
            {
                Transform component = __instance.player.GetComponent<Transform>();
                Transform component2 = MainCameraControl.main.GetComponent<Transform>();
                __instance.cameraPosition = __instance.player.camAnchor.InverseTransformPoint(component2.position);
                __instance.player.frozenMixin.Unfreeze();
                __instance.player.cinematicModeActive = true;
                __instance.player.FreezeStats();
                __instance.player.playerController.SetEnabled(false);
                Player.main.GetPDA().Close();
                if (__instance.leftIKTarget || __instance.rightIKTarget)
                {
                    __instance.player.armsController.SetWorldIKTargetSmooth(__instance.leftIKTarget, __instance.rightIKTarget, __instance.ikSmoothTime);
                }
                __instance.cameraFromRotation = component.rotation;
                __instance.playerFromPosition = component.position;
                __instance.playerFromRotation = component.rotation;
                if (__instance.playerViewInterpolateAnimParam.Length > 0)
                {
                    SafeAnimator.SetBool(__instance.player.GetComponentInChildren<Animator>(), __instance.playerViewInterpolateAnimParam, true);
                }
            }
            if (__instance.animator != null && !string.IsNullOrEmpty(__instance.interpolateAnimParam))
            {
                __instance.animator.SetBool(__instance.interpolateAnimParam, true);
            }
            if (__instance.interpolateDuringAnimation)
            {
                __instance.animState = true;
            }
            if (__instance.debug)
            {
                Mod.logger.LogInfo(__instance.gameObject.name + " successfully started cinematic");
            }
            float num = (__instance.director == null) ? 60f : ((float)__instance.director.duration + 10f);
            float num2 = Time.time + num;
            PlayerCinematicController.cinematicActivityExpireTime = ((PlayerCinematicController.cinematicModeCount == 0) ? num2 : Mathf.Max(PlayerCinematicController.cinematicActivityExpireTime, num2));
            PlayerCinematicController.cinematicModeCount++;
            HandReticle.main.RequestCrosshairHide();

            //CoroutineHost.StartCoroutine(Cinematics.DelayedCancel(__instance, 0.2f)); //Doesnt work
            return false;
        }
    }
*/
/*
    [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.EndCinematicMode))]
    public static class EndCinematicModeFixer
    {
        public static bool Prefix(PlayerCinematicController __instance, bool forceRestoreControl = false)
        {
            Mod.logger.LogInfo($"PlayerCinematicController.EndCinematicMode 1");
            if (__instance.cinematicModeActive)
            {
                Mod.logger.LogInfo($"PlayerCinematicController.EndCinematicMode 2");
                if (__instance.IsInvoking("DoFailSafeCancel"))
                {
                    Mod.logger.LogInfo($"PlayerCinematicController.EndCinematicMode 3");
                    __instance.CancelInvoke("DoFailSafeCancel");
                }
                Mod.logger.LogInfo($"PlayerCinematicController.EndCinematicMode 4");
                if (__instance.interruptAutoMove)
                {
                    Mod.logger.LogInfo($"PlayerCinematicController.EndCinematicMode 5");
                    GameInput.SetAutoMove(false);
                }
                Mod.logger.LogInfo($"PlayerCinematicController.EndCinematicMode 6");
                if (__instance.animator != null && __instance.resetToCullCompletely)
                {
                    Mod.logger.LogInfo($"PlayerCinematicController.EndCinematicMode 7");
                    __instance.animator.cullingMode = AnimatorCullingMode.CullCompletely;
                }
                Mod.logger.LogInfo($"PlayerCinematicController.EndCinematicMode 8");
                __instance.animState = false;
                __instance.state = PlayerCinematicController.State.None;
                if (__instance.player)
                {
                    Mod.logger.LogInfo($"PlayerCinematicController.EndCinematicMode 9");
                    __instance.player.playerController.SetEnabled(true);
                    __instance.player.cinematicModeActive = false;
                    __instance.player.UnfreezeStats();
                    if (!__instance.restoreControl && !forceRestoreControl)
                    {
                        Mod.logger.LogInfo($"PlayerCinematicController.EndCinematicMode 10");
                        __instance.player.playerController.SetEnabled(false);
                    }
                    if (__instance.leftIKTarget || __instance.rightIKTarget)
                    {
                        Mod.logger.LogInfo($"PlayerCinematicController.EndCinematicMode 11");
                        __instance.player.armsController.SetWorldIKTarget(null, null);
                    }
                }
                Mod.logger.LogInfo($"PlayerCinematicController.EndCinematicMode 12");
                __instance.cinematicModeActive = false;
                PlayerCinematicController.cinematicModeCount--;
                HandReticle.main.UnrequestCrosshairHide();
                if (__instance.rumbleCinematic)
                {
                    Mod.logger.LogInfo($"PlayerCinematicController.EndCinematicMode 13");
                    __instance.rumbleCinematic.Stop();
                }
                Mod.logger.LogInfo($"PlayerCinematicController.EndCinematicMode 14");
            }           
            
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.OnPlayerCinematicModeEnd))]
    public static class OnPlayerCinematicModeEndFixer
    {
        public static bool Prefix(PlayerCinematicController __instance)
        {
            Mod.logger.LogInfo($"PlayerCinematicController.OnPlayerCinematicModeEnd 1");
            if (__instance.cinematicModeActive && !__instance.onCinematicModeEndCall)
            {
                Mod.logger.LogInfo($"PlayerCinematicController.OnPlayerCinematicModeEnd 2");
                if (__instance.director != null)
                {
                    Mod.logger.LogInfo($"PlayerCinematicController.OnPlayerCinematicModeEnd 3");
                   __instance.director.played -= __instance.OnDirectorPlayed;
                    __instance.director.stopped -= __instance.OnDirectorStopped;
                }
                Mod.logger.LogInfo($"PlayerCinematicController.OnPlayerCinematicModeEnd 4");
                if (__instance.player)
                {
                    Mod.logger.LogInfo($"PlayerCinematicController.OnPlayerCinematicModeEnd 5");
                    __instance.UpdatePlayerPosition();
                }
                Mod.logger.LogInfo($"PlayerCinematicController.OnPlayerCinematicModeEnd 6");
                __instance.animState = false;
                if (__instance.UseEndTransform())
                {
                    Mod.logger.LogInfo($"PlayerCinematicController.OnPlayerCinematicModeEnd using EndTransform");
                    __instance.state = PlayerCinematicController.State.Out;
                    if (__instance.player)
                    {
                        Mod.logger.LogInfo($"PlayerCinematicController.OnPlayerCinematicModeEnd 7");
                        Transform component = __instance.player.GetComponent<Transform>();
                        __instance.playerFromPosition = component.position;
                        __instance.playerFromRotation = component.rotation;
                    }
                }
                else
                {
                    Mod.logger.LogInfo($"PlayerCinematicController.OnPlayerCinematicModeEnd 8");
                    __instance.EndCinematicMode(false);
                }
                Mod.logger.LogInfo($"PlayerCinematicController.OnPlayerCinematicModeEnd 9");
                if (__instance.informGameObject != null)
                {
                    Mod.logger.LogInfo($"PlayerCinematicController.OnPlayerCinematicModeEnd 10");
                    __instance.onCinematicModeEndCall = true;
                    __instance.informGameObject.SendMessage("OnPlayerCinematicModeEnd", __instance, SendMessageOptions.DontRequireReceiver);
                    __instance.onCinematicModeEndCall = false;
                }
                if (__instance.player)
                {
                    Mod.logger.LogInfo($"PlayerCinematicController.OnPlayerCinematicModeEnd 11");
                    __instance.player.playerController.ForceControllerSize();
                }
                Mod.logger.LogInfo($"PlayerCinematicController.OnPlayerCinematicModeEnd 12");
            }
            Mod.logger.LogInfo($"PlayerCinematicController.OnPlayerCinematicModeEnd 13");
           
            return false;
        }
    }
*/
/*
    [HarmonyPatch(typeof(PlayableDirector), nameof(PlayableDirector.SetGenericBinding)) ]
    public static class PlayableDirector_mod
    {
        public static bool Prefix(PlayableDirector __instance, Object key, Object value)
        {
            Mod.logger.LogInfo($"PlayableDirector.SetGenericBinding {key} {value}");
            return true;
        }
    }

    [HarmonyPatch(typeof(PlayableDirector), nameof(PlayableDirector.Play), new[] {typeof(PlayableAsset), typeof(DirectorWrapMode)})]
    public static class PlayableDirectorPlay_mod
    {
        public static bool Prefix(PlayableDirector __instance, PlayableAsset asset, DirectorWrapMode mode)
        {
            Mod.logger.LogInfo($"PlayableDirector.SetGenericBinding {asset.name} {mode}");
            return true;
        }
    }
*/
 
    #endregion

}

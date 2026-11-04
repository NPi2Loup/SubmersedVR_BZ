using HarmonyLib;
using System;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UWEXR;
using Story;

namespace SubmersedVR
{
    // Full instrumentation of the PlayerCinematicController pipeline (27/10
    // session): for every cinematic start, log every phase (director tracks +
    // clips = the timeline phases), every event (state machine transitions,
    // director stopped, skip, end) and the player position/rotation before
    // and after, so each entry/exit animation can be decided case by case
    // (skip / play / endTransform teleport).
    //
    // NOTE: the skip prefix in Cinematics.cs re-implements
    // PlayerCinematicController.StartCinematicMode entirely and always returns
    // false, so a separate Prefix on StartCinematicMode would be skipped by
    // the prefix chain as soon as that one runs first. The start context is
    // therefore logged from inside that prefix via LogStart().
    static class CinematicsDiagnostics
    {
        private static float startTime = -1f;
        private static Vector3 startPos;
        private static Quaternion skipFromRot;
        private static Vector3 skipFromPos;

        // 28/10 spec section 4: story-goal observation during a played
        // cinematic. POCO static singleton (NOT a MonoBehaviour: nothing to
        // destroy on load). Removed on every exit path: EndCinematicMode
        // postfix (Task 5), sceneUnloaded. main is Unity-null after a scene
        // load (never reset to null in OnDestroy) - hence the (object) cast.
        public class CineGoalListener : IStoryGoalListener
        {
            public static PlayerCinematicController currentCine;
            static readonly CineGoalListener Instance = new CineGoalListener();
            static readonly System.Collections.Generic.List<string> goalKeys = new System.Collections.Generic.List<string>();

            public static void Add()
            {
                if ((object)StoryGoalManager.main == null)
                {
                    return;
                }
                StoryGoalManager.main.RemoveListener(Instance);
                StoryGoalManager.main.AddListener(Instance);
            }

            public static void Remove()
            {
                if ((object)StoryGoalManager.main == null)
                {
                    return;
                }
                StoryGoalManager.main.RemoveListener(Instance);
            }

            public static void Clear()
            {
                goalKeys.Clear();
                currentCine = null;
            }

            public static string Summary()
            {
                return goalKeys.Count == 0 ? "none" : string.Join(",", goalKeys);
            }

            public void NotifyGoalComplete(string key)
            {
                goalKeys.Add(key);
                string name = currentCine != null ? currentCine.gameObject.name : "?";
                double dirT = currentCine != null && currentCine.director != null ? currentCine.director.time : -1d;
                Mod.logger.LogInfo($"[Cine] GOAL cine={name} key={key} dirT={dirT:F2}");
            }

            public void NotifyGoalReset(string key)
            {
                string name = currentCine != null ? currentCine.gameObject.name : "?";
                Mod.logger.LogInfo($"[Cine] GOAL-RESET cine={name} key={key}");
            }

            public void NotifyGoalsDeserialized()
            {
            }
        }

        static CinematicsDiagnostics()
        {
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        // 28/10 spec section 4/5: on scene load, drop the goal listener
        // (death is an in-place respawn in BZ - no sceneUnloaded; death is
        // covered by the EndCinematicMode postfix, Task 5).
        private static void OnSceneUnloaded(UnityEngine.SceneManagement.Scene scene)
        {
            WatchdogReset();
            CineGoalListener.Remove();
            CineGoalListener.Clear();
            // GO names are reused across scenes (a new base re-instantiates
            // "seatruck_anim" etc.) - dump the topology again.
            topologyLogged.Clear();
        }

        private static string NullStr
        {
            get
            {
                return "null";
            }
        }

        public static string FmtP(Vector3 v)
        {
            return $"({v.x:F2},{v.y:F2},{v.z:F2})";
        }

        public static string FmtR(Quaternion q)
        {
            Vector3 e = q.eulerAngles;
            return $"({e.x:F1},{e.y:F1},{e.z:F1})";
        }

        private static string FmtPlayer(Player p)
        {
            if (p == null)
            {
                return "null";
            }
            return "pos=" + FmtP(p.transform.position) + " rot=" + FmtR(p.transform.rotation);
        }

        // The real UseEndTransform() is private in the game assembly
        // (publicized for compile only - a direct call would throw
        // MethodAccessException at runtime), so this logs the ORIGINAL
        // logic's ingredients. The authoritative outcome is the SKIP line's
        // "-> pos" / "NOT MOVED" (what SkipCinematic actually did) and the
        // "[Cine] UseEndTransform forced true in VR" line when the fix fires.
        private static string UseEnd(PlayerCinematicController c)
        {
            if (c.endTransform == null)
            {
                return "false (no endTransform)";
            }
            if (c.onlyUseEndTransformInVr)
            {
                return VRGameOptions.GetVrAnimationMode() ? "true (vrAnimMode)" : "false (onlyInVr) - VR fix should force";
            }
            return "true";
        }

        private static string DescribeDirector(PlayerCinematicController c)
        {
            if (c.director == null)
            {
                return "director=null";
            }
            var sb = new StringBuilder();
            sb.Append("director dur=").Append(c.director.duration.ToString("F2")).Append(" time=").Append(c.director.time.ToString("F2"));
            string labelTrack = string.IsNullOrEmpty(c.labelTrack) ? "null" : c.labelTrack;
            string label = c.label.ToString();
            if (string.IsNullOrEmpty(label))
            {
                label = "none";
            }
            sb.Append(" labelTrack=").Append(labelTrack).Append(" label=").Append(label);
            var asset = c.director.playableAsset;
            if (asset != null)
            {
                foreach (var b in asset.outputs)
                {
                    var src = b.sourceObject;
                    string kind = src != null ? src.GetType().Name : "null";
                    sb.Append(" [track stream=").Append(b.streamName).Append(" type=").Append(kind);
                    var anim = src as AnimationTrack;
                    if (anim != null)
                    {
                        foreach (var clip in anim.GetClips())
                        {
                            sb.Append(" ").Append(clip.asset != null ? clip.asset.GetType().Name : "clip").Append(":").Append(clip.duration.ToString("F2")).Append("@").Append(clip.start.ToString("F2"));
                        }
                    }
                    var ctrl = src as ControlTrack;
                    if (ctrl != null)
                    {
                        foreach (var clip in ctrl.GetClips())
                        {
                            sb.Append(" ").Append(clip.asset != null ? clip.asset.GetType().Name : "clip").Append("@").Append(clip.start.ToString("F2"));
                        }
                    }
                    sb.Append(']');
                }
            }
            return sb.ToString();
        }

        // 28/10 spec section 4: enumerate the timeline contract - anim
        // events (AnimationClip.events is PUBLIC; GetEventsInternal is
        // internal to UnityEngine which our publicizer does not expose),
        // EventTrack events (they survive frame gaps via TriggerInRange),
        // ControlTrack clips - and audit the bindings (InitDirectorFixer
        // disables re-binding: a NULL binding on the Player stream or an
        // EventTrack = the animation/events target nothing).
        private static string DescribeContract(PlayerCinematicController c)
        {
            if (c.director == null || (object)c.director == null)
            {
                // 28/10 build 1 (smart-skip plan): entry/exit cinematics are
                // animator-only - the contract lives in the animation itself
                // (events + bool parameters), not in a timeline.
                if (c.animator != null && (object)c.animator != null)
                {
                    return "director=null (no timeline) animator: " + DescribeAnimatorContract(c);
                }
                return "director=null (no timeline)";
            }
            var sb = new StringBuilder();
            string updateMode = c.director.timeUpdateMode.ToString();
            string extrap = c.director.extrapolationMode.ToString();
            var asset = c.director.playableAsset;
            if (asset == null)
            {
                return "dur=" + c.director.duration.ToString("F2") + " mode=" + updateMode + "/" + extrap + " asset=null";
            }
            sb.Append("dur=").Append(c.director.duration.ToString("F2")).Append(" mode=").Append(updateMode).Append('/').Append(extrap);
            foreach (var b in asset.outputs)
            {
                var src = b.sourceObject;
                string binding = "null";
                var bound = c.director.GetGenericBinding(src);
                if ((object)bound != null)
                {
                    var comp = bound as Component;
                    binding = comp != null ? comp.gameObject.name : bound.name;
                }
                sb.Append(" [stream=").Append(b.streamName).Append(" type=").Append(src != null ? src.GetType().Name : "null").Append(" bind=").Append(binding);
                var anim = src as AnimationTrack;
                if (anim != null)
                {
                    foreach (var clip in anim.GetClips())
                    {
                        sb.Append(" anim@").Append(clip.start.ToString("F2")).Append('+').Append(clip.duration.ToString("F2"));
                        var ac = clip.animationClip;
                        if (ac != null)
                        {
                            foreach (var ev in ac.events)
                            {
                                // approx: clip-local event time + clip start
                                double t = clip.start + ev.time;
                                sb.Append(" {").Append(ev.functionName).Append('@').Append(t.ToString("F2")).Append('}');
                            }
                        }
                    }
                    var inf = anim.infiniteClip;
                    if (inf != null)
                    {
                        foreach (var ev in inf.events)
                        {
                            sb.Append(" inf{").Append(ev.functionName).Append('@').Append(ev.time.ToString("F2")).Append('}');
                        }
                    }
                    if (binding == "null" && (b.streamName == "Player" || string.IsNullOrEmpty(b.streamName)))
                    {
                        Mod.logger.LogInfo($"[Cine] RISK-BINDING name={c.gameObject.name} track={src.GetType().Name} stream={b.streamName}");
                    }
                }
                var evt = src as UWE.Timeline.EventTrack;
                if (evt != null)
                {
                    foreach (var clip in evt.GetClips())
                    {
                        var ec = clip.asset as UWE.Timeline.EventClip;
                        // PropertyName.ToString() may be a hash in player
                        // builds - clip.displayName is the readable id.
                        string ename = ec != null ? ec.eventName.ToString() : "null";
                        sb.Append(" event=").Append(clip.displayName).Append('(').Append(ename).Append('@').Append(clip.start.ToString("F2")).Append(')');
                    }
                    if (binding == "null")
                    {
                        Mod.logger.LogInfo($"[Cine] RISK-BINDING name={c.gameObject.name} track=EventTrack stream={b.streamName}");
                    }
                }
                var ctrl = src as ControlTrack;
                if (ctrl != null)
                {
                    foreach (var clip in ctrl.GetClips())
                    {
                        sb.Append(" ctrl=").Append(clip.displayName).Append('@').Append(clip.start.ToString("F2"));
                    }
                }
                sb.Append(']');
            }
            return sb.ToString();
        }

        // 28/10 build 1 (smart-skip plan): the animator-only contract - what
        // a skip would miss on a director-less cinematic: the clips of the
        // controller's animator (AnimationClip.events = the function calls
        // that drive the state machines, e.g. the vehicle entry commit),
        // the controller parameters, and the receiver GOs.
        private static string DescribeAnimatorContract(PlayerCinematicController c)
        {
            var sb = new StringBuilder();
            var anim = c.animator;
            sb.Append("animator=").Append(anim.gameObject.name);
            sb.Append(" animParam=").Append(string.IsNullOrEmpty(c.animParam) ? "null" : c.animParam);
            sb.Append(" interpParam=").Append(string.IsNullOrEmpty(c.interpolateAnimParam) ? "null" : c.interpolateAnimParam);
            sb.Append(" recvParam=").Append(string.IsNullOrEmpty(c.receiversAnimParam) ? "null" : c.receiversAnimParam);
            sb.Append(" receivers=");
            for (int i = 0; i < c.animParamReceivers.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append(c.animParamReceivers[i] != null ? c.animParamReceivers[i].name : "null");
            }
            // The concrete AnimatorController type is not in the compile-time
            // reference set (that engine module is missing from the BepInEx
            // install) but exists at runtime - reflect.
            var ac = anim.runtimeAnimatorController;
            if (ac == null || (object)ac == null)
            {
                sb.Append(" controller=null");
                return sb.ToString();
            }
            var acType = ac.GetType();
            var parsProp = acType.GetProperty("parameters");
            var pars = parsProp != null ? parsProp.GetValue(ac) as Array : null;
            if (pars != null)
            {
                sb.Append(" params=");
                for (int i = 0; i < pars.Length; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }
                    var p = pars.GetValue(i);
                    var pt = p.GetType();
                    sb.Append(pt.GetProperty("name").GetValue(p)).Append('(').Append(pt.GetProperty("type").GetValue(p)).Append(')');
                }
            }
            var layersProp = acType.GetProperty("layers");
            var layers = layersProp != null ? layersProp.GetValue(ac) as Array : null;
            if (layers != null)
            {
                for (int li = 0; li < layers.Length; li++)
                {
                    var layer = layers.GetValue(li);
                    var sm = layer.GetType().GetProperty("stateMachine")?.GetValue(layer);
                    if (sm == null)
                    {
                        continue;
                    }
                    var states = sm.GetType().GetProperty("states")?.GetValue(sm) as Array;
                    if (states == null)
                    {
                        continue;
                    }
                    for (int si = 0; si < states.Length; si++)
                    {
                        var state = states.GetValue(si)?.GetType().GetProperty("state")?.GetValue(states.GetValue(si));
                        if (state == null)
                        {
                            continue;
                        }
                        var clip = state.GetType().GetProperty("clip")?.GetValue(state) as AnimationClip;
                        if (clip == null)
                        {
                            continue;
                        }
                        var stateName = state.GetType().GetProperty("name")?.GetValue(state)?.ToString();
                        sb.Append(" clip[").Append(stateName).Append("]=").Append(clip.name).Append('@').Append(clip.length.ToString("F2"));
                        foreach (var ev in clip.events)
                        {
                            sb.Append(" {").Append(ev.functionName).Append('@').Append(ev.time.ToString("F2")).Append('}');
                        }
                    }
                }
            }
            return sb.ToString();
        }

        // 28/10 build 1 (smart-skip plan): the current animation state
        // (time into the active clip + clip length + state name) via
        // GetCurrentAnimatorStateInfo(0) - the AnimatorControllerStateInfo
        // type is not in the compile-time reference set, so reflect.
        private static MethodInfo stateInfoMethod;
        private static double lastAnimStateLog = -10d;

        private static void LogAnimState(string cine, Animator anim, string stateName, double stateSpeed, double stateSpeedMult, bool stateLoop, double len, double now)
        {
            lastAnimStateLog = Time.time;
            bool inTrans = false;
            string dest = null;
            double transDur = -1d;
            try
            {
                inTrans = anim.IsInTransition(0);
                if (inTrans)
                {
                    TryGetTransitionInfo(anim, out dest, out transDur);
                }
            }
            catch
            {
            }
            Mod.logger.LogInfo($"[Cine] ANIM-STATE cine={cine} state={stateName} stateSpeed={stateSpeed:F2} stateSpeedMult={stateSpeedMult:F2} loop={stateLoop} clip={len:F2} animNow={now:F2} inTrans={inTrans} dest={dest} transDur={transDur:F2}");
            Mod.logger.LogInfo($"[Cine] ANIM-PARAMS cine={cine} " + DumpParamSnapshot(anim));
        }

        // 28/10 build 7: the values of every controller parameter right now.
        // Test 8: the prawn module sat on a 0.25s clip at ~1x effective
        // (animator.speed=5, stateSpeed=1.0) and the state NAME read back
        // empty - the param snapshot (exit/exit_prep/player_inside/
        // piloting/speed(Float)/...) shows which condition the missing
        // prep->exit transition is waiting on, and whether the float
        // "speed" parameter (bindable to state speed in the controller)
        // carries a ~0.2 default that eats the 5x.
        private static string DumpParamSnapshot(Animator anim)
        {
            try
            {
                var gp = anim.GetType().GetMethod("GetParameter");
                var pcProp = anim.GetType().GetProperty("parameterCount");
                var pcVal = pcProp != null ? pcProp.GetValue(anim) : null;
                int pc = pcVal is int ? (int)pcVal : 0;
                var sb = new StringBuilder();
                for (int i = 0; i < pc && gp != null; i++)
                {
                    var p = gp.Invoke(anim, new object[] { i });
                    string n = Prop(p, "name") as string;
                    string ty = Prop(p, "type") as string;
                    string val = "?";
                    try
                    {
                        if (ty == "Int")
                        {
                            // GetInt is not in the compile-time reference set.
                            var gpm = anim.GetType().GetMethod("GetInt");
                            val = Convert.ToInt32(gpm.Invoke(anim, new object[] { n })).ToString();
                        }
                        else if (ty == "Float")
                        {
                            val = anim.GetFloat(n).ToString("F2");
                        }
                        else
                        {
                            val = anim.GetBool(n) ? "1" : "0";
                        }
                    }
                    catch
                    {
                    }
                    if (sb.Length > 0)
                    {
                        sb.Append(' ');
                    }
                    sb.Append(n).Append('=').Append(val);
                }
                return sb.Length > 0 ? sb.ToString() : "(none)";
            }
            catch
            {
                return "(error)";
            }
        }

        private static MethodInfo transInfoMethod;

        private static void TryGetTransitionInfo(Animator anim, out string dest, out double duration)
        {
            dest = null;
            duration = -1d;
            try
            {
                if (transInfoMethod == null)
                {
                    transInfoMethod = anim.GetType().GetMethod("GetCurrentAnimatorTransitionInfo");
                }
                if (transInfoMethod == null)
                {
                    return;
                }
                var ti = transInfoMethod.Invoke(anim, new object[] { 0 });
                if (ti == null)
                {
                    return;
                }
                var t = ti.GetType();
                var dn = t.GetProperty("destinationStateName");
                dest = dn != null ? dn.GetValue(ti) as string : null;
                var dd = t.GetProperty("duration");
                if (dd != null)
                {
                    duration = (double)(float)dd.GetValue(ti);
                }
            }
            catch
            {
            }
        }

        // 28/10 build 6: also returns the per-STATE speed multiplier
        // (AnimatorControllerStateInfo.speed - test 7: the machine sat on
        // a state whose effective playback was ~1.1x despite
        // animator.speed=5, i.e. a state speed of ~0.22) and loopTime
        // (is the current state looping).
        // 28/10 build 7b (challenger review): also returns speedMultiplier
        // (the state's own authored multiplier, distinct from speed which
        // is the effective one) - on this build neither correlated with
        // the measured rate (truck: stateSpeed=0.01 at ~1.0x reported
        // rate), so log both and stop trusting either.
        private static bool TryGetAnimState(Animator anim, out double now, out double len, out string stateName, out double stateSpeed, out double stateSpeedMult, out bool stateLoop)
        {
            now = -1d;
            len = -1d;
            stateName = null;
            stateSpeed = -1d;
            stateSpeedMult = -1d;
            stateLoop = false;
            try
            {
                if (stateInfoMethod == null)
                {
                    stateInfoMethod = anim.GetType().GetMethod("GetCurrentAnimatorStateInfo");
                }
                if (stateInfoMethod == null)
                {
                    return false;
                }
                var info = stateInfoMethod.Invoke(anim, new object[] { 0 });
                if (info == null)
                {
                    return false;
                }
                var t = info.GetType();
                float nt = (float)t.GetProperty("normalizedTime").GetValue(info);
                float l = (float)t.GetProperty("length").GetValue(info);
                now = (double)nt * (double)l;
                len = (double)l;
                // 28/10 build 7: test 8 read "name" back EMPTY - try the
                // full name and short name properties as well.
                string[] nameProps = { "fullName", "shortName", "name" };
                for (int npi = 0; npi < nameProps.Length && string.IsNullOrEmpty(stateName); npi++)
                {
                    var nm = t.GetProperty(nameProps[npi]);
                    if (nm != null)
                    {
                        stateName = nm.GetValue(info) as string;
                    }
                }
                var sp = t.GetProperty("speed");
                if (sp != null)
                {
                    stateSpeed = (double)(float)sp.GetValue(info);
                }
                var spm = t.GetProperty("speedMultiplier");
                if (spm != null)
                {
                    stateSpeedMult = (double)(float)spm.GetValue(info);
                }
                var lp = t.GetProperty("loopTime");
                if (lp != null)
                {
                    stateLoop = (bool)lp.GetValue(info);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // 28/10 build 5 (test 6 follow-up): full state-machine topology
        // dump, once per controller. Test 6 showed a NEW stuck mode the
        // logs could not explain: on the prawn module exit the machine
        // sat on a 0.25s LOOPING clip for 2.5s (the transition to the
        // real exit clip never fired) and the effective anim speed was
        // ~1x despite animator.speed=5. Every guess so far (which clip
        // ends the sequence, what the prep state is, why the transition
        // doesn't fire, the 1x anomaly) needs the actual topology:
        // states, per-state speed (AnimatorState.speed multiplies the
        // clip!), clips, events + targets, transitions + conditions.
        // The concrete controller type (AnimatorOverrideController) is
        // not in the compile-time reference set - reflect everything,
        // originalController gives the base AnimatorController.
        private static readonly System.Collections.Generic.HashSet<string> topologyLogged = new System.Collections.Generic.HashSet<string>();

        private static object Prop(object o, string name)
        {
            try
            {
                if (o == null)
                {
                    return null;
                }
                // Unity fake-null (destroyed Object) - only the Unity
                // == overload (left operand statically Object) catches it.
                if (o is UnityEngine.Object uo && uo == null)
                {
                    return null;
                }
                var p = o.GetType().GetProperty(name);
                return p != null ? p.GetValue(o) : null;
            }
            catch
            {
                return null;
            }
        }

        private static string ObjName(object o)
        {
            if (o == null)
            {
                return "null";
            }
            if (o is UnityEngine.Object uo && uo == null)
            {
                return "null";
            }
            try
            {
                var v = Prop(o, "name");
                if (v != null)
                {
                    return v.ToString();
                }
            }
            catch
            {
            }
            try
            {
                return o.ToString();
            }
            catch
            {
                return "?";
            }
        }

        private static string DescrTrans(string go, string prefix, object tr)
        {
            var dest = Prop(tr, "destinationState");
            var hasExit = Prop(tr, "hasExitTime") as bool? ?? false;
            var isExit = Prop(tr, "isExit") as bool? ?? false;
            var mode = Prop(tr, "conditionMode");
            var s = $"[Cine] TOPOLOGY {go} {prefix}-> {ObjName(dest)} exitTime={hasExit} isExit={isExit} mode={mode}";
            var conds = Prop(tr, "conditions") as Array;
            if (conds != null)
            {
                s += " cond=[";
                for (int i = 0; i < conds.Length; i++)
                {
                    if (i > 0)
                    {
                        s += ' ';
                    }
                    var c = conds.GetValue(i);
                    s += $"{Prop(c, "mode")}:{Prop(c, "parameter")}={Prop(c, "threshold")}";
                }
                s += "]";
            }
            var dur = Prop(tr, "duration");
            if (dur is float)
            {
                s += $" dur={((float)dur):F2}";
            }
            return s;
        }

        private static void DumpStateMachine(string go, object sm, int depth)
        {
            if (sm == null || (object)sm == null || depth > 2)
            {
                return;
            }
            string ind = new string(' ', depth * 2);
            var def = Prop(sm, "defaultState");
            Mod.logger.LogInfo($"[Cine] TOPOLOGY {go} {ind}sm={ObjName(sm)} default={ObjName(def)}");
            var states = Prop(sm, "states") as Array;
            if (states != null)
            {
                for (int i = 0; i < states.Length; i++)
                {
                    var st = Prop(states.GetValue(i), "state");
                    if (st == null || (object)st == null)
                    {
                        continue;
                    }
                    var speed = Prop(st, "speed");
                    var clip = Prop(st, "clip") as AnimationClip;
                    var s = $"[Cine] TOPOLOGY {go} {ind}  state={ObjName(st)} speed={speed}";
                    if (clip != null && (object)clip != null)
                    {
                        // AnimationEvent.target is not in the runtime API
                        // (2019.4) - the event lands on the animator's GO
                        // (or informGameObject via the controller).
                        s += $" clip={clip.name}@{clip.length:F2}";
                        foreach (var ev in clip.events)
                        {
                            s += $" {ev.functionName}@{ev.time:F2}";
                        }
                    }
                    else
                    {
                        s += " clip=null";
                    }
                    Mod.logger.LogInfo(s);
                }
            }
            var trans = Prop(sm, "transitions") as Array;
            if (trans != null)
            {
                for (int i = 0; i < trans.Length; i++)
                {
                    try
                    {
                        Mod.logger.LogInfo(DescrTrans(go, ind + "tr", trans.GetValue(i)));
                    }
                    catch (System.Exception e)
                    {
                        Mod.logger.LogWarning($"[Cine] TOPOLOGY trans desc failed: {e.Message}");
                    }
                }
            }
            var any = Prop(sm, "anyStateTransitions") as Array;
            if (any != null)
            {
                for (int i = 0; i < any.Length; i++)
                {
                    try
                    {
                        Mod.logger.LogInfo(DescrTrans(go, ind + "ANY", any.GetValue(i)));
                    }
                    catch (System.Exception e)
                    {
                        Mod.logger.LogWarning($"[Cine] TOPOLOGY anyTrans desc failed: {e.Message}");
                    }
                }
            }
            var subs = Prop(sm, "subStateMachines") as Array;
            if (subs != null)
            {
                for (int i = 0; i < subs.Length; i++)
                {
                    DumpStateMachine(go, subs.GetValue(i), depth + 1);
                }
            }
        }

        private static void LogStateMachineTopology(PlayerCinematicController c)
        {
            try
            {
                string go = c.gameObject.name;
                if (!topologyLogged.Add(go))
                {
                    return;
                }
                var anim = c.animator;
                if (anim == null || (object)anim == null)
                {
                    return;
                }
                var ac = anim.runtimeAnimatorController;
                if (ac == null || (object)ac == null)
                {
                    return;
                }
                var acType = ac.GetType();
                // 28/10 build 6: test 7 showed the runtime type is literally
                // "RuntimeAnimatorController" (no layers/parameters) - dump
                // the base type + every readable property name so the next
                // iteration knows what surface is actually there.
                string propNames = "";
                try
                {
                    var pn = new System.Collections.Generic.List<string>();
                    foreach (var pp in acType.GetProperties())
                    {
                        pn.Add(pp.Name);
                    }
                    propNames = string.Join(",", pn);
                }
                catch
                {
                }
                string baseTypeName = acType.BaseType != null ? acType.BaseType.Name : "null";
                object baseCtrl = Prop(ac, "originalController"); // AnimatorOverrideController -> base
                if (baseCtrl == null || (object)baseCtrl == null)
                {
                    baseCtrl = ac;
                }
                // Parameter list via the Animator-level API (works even when
                // the controller type is opaque) - reveals TRIGGER params
                // (a trigger set by code at a real-time delay would explain
                // the prep-loop stuck: the transition window closes first).
                var parsSb = new StringBuilder();
                var pars = Prop(baseCtrl, "parameters") as Array;
                if (pars != null)
                {
                    for (int i = 0; i < pars.Length; i++)
                    {
                        if (i > 0)
                        {
                            parsSb.Append(',');
                        }
                        var p = pars.GetValue(i);
                        parsSb.Append(Prop(p, "name")).Append('(').Append(Prop(p, "type")).Append(')');
                    }
                }
                else
                {
                    try
                    {
                        var pcProp = anim.GetType().GetProperty("parameterCount");
                        var pc = pcProp != null ? pcProp.GetValue(anim) as int? : null;
                        if (pc != null)
                        {
                            var gp = anim.GetType().GetMethod("GetParameter");
                            for (int i = 0; i < pc.Value && gp != null; i++)
                            {
                                if (i > 0)
                                {
                                    parsSb.Append(',');
                                }
                                var p = gp.Invoke(anim, new object[] { i });
                                parsSb.Append(Prop(p, "name")).Append('(').Append(Prop(p, "type")).Append(')');
                            }
                        }
                    }
                    catch
                    {
                    }
                }
                Mod.logger.LogInfo($"[Cine] TOPOLOGY name={go} ctrl={acType.Name} baseType={baseTypeName} base={baseCtrl.GetType().Name} speed={anim.speed:F2} ctrlProps=[{propNames}] params=[{parsSb}]");
                // 28/10 build 7: the ONE readable surface left on this
                // opaque controller type is animationClips (AnimationClip[]).
                // Dump every clip (name, length, AnimationEvents) - this
                // reveals which clip carries the commit event and what the
                // 0.25s prep clip is, without needing the (invisible)
                // state->clip mapping.
                var clips = Prop(ac, "animationClips") as AnimationClip[];
                var clipsSb = new StringBuilder();
                if (clips != null)
                {
                    for (int ci = 0; ci < clips.Length; ci++)
                    {
                        var cl = clips[ci];
                        if ((object)cl == null)
                        {
                            continue;
                        }
                        if (clipsSb.Length > 0)
                        {
                            clipsSb.Append("; ");
                        }
                        clipsSb.Append(cl.name).Append('(').Append(cl.length.ToString("F2")).Append(')');
                        if (cl.events != null && cl.events.Length > 0)
                        {
                            clipsSb.Append('[');
                            for (int ei = 0; ei < cl.events.Length; ei++)
                            {
                                if (ei > 0)
                                {
                                    clipsSb.Append(',');
                                }
                                clipsSb.Append(cl.events[ei].functionName).Append('@').Append(cl.events[ei].time.ToString("F2"));
                            }
                            clipsSb.Append(']');
                        }
                    }
                }
                Mod.logger.LogInfo($"[Cine] TOPOLOGY-CLIPS name={go} [{clipsSb}]");
                var layers = Prop(baseCtrl, "layers") as Array;
                if (layers == null)
                {
                    Mod.logger.LogInfo($"[Cine] TOPOLOGY {go} layers=null (base controller unreadable)");
                    return;
                }
                for (int li = 0; li < layers.Length; li++)
                {
                    var layer = layers.GetValue(li);
                    Mod.logger.LogInfo($"[Cine] TOPOLOGY {go} layer={ObjName(layer)}");
                    DumpStateMachine(go, Prop(layer, "stateMachine"), 1);
                }
            }
            catch (System.Exception e)
            {
                Mod.logger.LogWarning($"[Cine] LogStateMachineTopology failed: {e}");
            }
        }

        // 28/10 spec section 5: static-contract entries with time in (a, b]
        // - what a jumped interval covered. EventClip fired anyway
        // (TriggerInRange); anim events probably fired (Unity fires crossed
        // events on forward jumps).
        private static string DescribeContractGap(PlayerCinematicController c, double a, double b)
        {
            if (c.director == null || (object)c.director == null)
            {
                return "";
            }
            var asset = c.director.playableAsset;
            if (asset == null)
            {
                return "";
            }
            var sb = new StringBuilder();
            foreach (var tr in asset.outputs)
            {
                var src = tr.sourceObject;
                var anim = src as AnimationTrack;
                if (anim != null)
                {
                    foreach (var clip in anim.GetClips())
                    {
                        var ac = clip.animationClip;
                        if (ac == null)
                        {
                            continue;
                        }
                        foreach (var ev in ac.events)
                        {
                            double t = clip.start + ev.time; // approx, see DescribeContract
                            if (t > a && t <= b)
                            {
                                sb.Append(" animEvent{").Append(ev.functionName).Append('@').Append(t.ToString("F2")).Append(" probably fired} ");
                            }
                        }
                    }
                }
                var evt = src as UWE.Timeline.EventTrack;
                if (evt != null)
                {
                    foreach (var clip in evt.GetClips())
                    {
                        double t = clip.start;
                        if (t > a && t <= b)
                        {
                            sb.Append(" event{").Append(clip.displayName).Append('@').Append(t.ToString("F2")).Append(" fired (range)} ");
                        }
                    }
                }
            }
            return sb.ToString();
        }

        // Called from the start prefix in Cinematics.cs, before the skip
        // decision, so it logs every start (played or skipped).
        public static void LogStart(PlayerCinematicController c, Player setplayer)
        {
            try
            {
                var et = c.endTransform;
                string etDesc;
                if (et != null)
                {
                    etDesc = FmtP(et.position) + " rot=" + FmtR(et.rotation) + " onlyInVr=" + c.onlyUseEndTransformInVr;
                }
                else
                {
                    etDesc = "NULL";
                }
                Mod.logger.LogInfo($"[Cine] START name={c.gameObject.name} playInVr={c.playInVr} vrAnimMode={VRGameOptions.GetVrAnimationMode()} cineOption={VROptions.enableCinematics} active={c.cinematicModeActive} xrEnabled={XRSettings.enabled}");
                Mod.logger.LogInfo($"[Cine]   endTransform={etDesc} useEnd={UseEnd(c)}");
                string animName = c.animator != null ? c.animator.gameObject.name : "null";
                // 28/10 build 7b (challenger review): enforceCinematicModeEnd
                // gates the game's own force-end (PlayerCinematicController
                // L689) and was never read by the mod - if false, the game
                // can NEVER self-commit this cine and the mod's net is the
                // only exit.
                Mod.logger.LogInfo($"[Cine]   animator={animName} animParam={c.animParam} interpolateAnimParam={c.interpolateAnimParam} recvParam={c.receiversAnimParam} receivers={c.animParamReceivers.Length} enforce={c.enforceCinematicModeEnd}");
                string informGo = c.informGameObject != null ? c.informGameObject.name : "null";
                string animatedT = c.animatedTransform != null ? c.animatedTransform.name : "null";
                Mod.logger.LogInfo($"[Cine]   ikL={c.leftIKTarget != null} ikR={c.rightIKTarget != null} informGO={informGo} restoreControl={c.restoreControl} interruptAutoMove={c.interruptAutoMove} interpDuringAnim={c.interpolateDuringAnimation} animatedTransform={animatedT}");
                Mod.logger.LogInfo($"[Cine]   {DescribeDirector(c)}");
                Mod.logger.LogInfo($"[Cine]   trigger={FmtPlayer(setplayer)} current={FmtPlayer(Player.main)}");
                // 28/10 spec section 4: static contract from the timeline
                // asset (what a skip would miss) + binding audit + director
                // modes (Manual/DSPClock would make SetSpeed ineffective).
                Mod.logger.LogInfo($"[Cine]   contract {DescribeContract(c)}");
                // 28/10 build 5: state-machine topology, once per controller
                // (the answer to "which clip ends the sequence / why does
                // the transition not fire" - test 6 follow-up).
                if (c.director == null || (object)c.director == null)
                {
                    LogStateMachineTopology(c);
                    // 28/10 build 8c: the machine state BEFORE the In
                    // phase (which clip it was sitting on when the cine
                    // started). Test 9: the module enter committed at the
                    // end of In - only explicable from the pre-In state.
                    try
                    {
                        if (c.animator != null && (object)c.animator != null)
                        {
                            double n0;
                            double l0;
                            string sn0;
                            double ss0;
                            double sm0;
                            bool sl0;
                            if (TryGetAnimState(c.animator, out n0, out l0, out sn0, out ss0, out sm0, out sl0) && n0 >= 0d)
                            {
                                bool t0 = false;
                                try
                                {
                                    t0 = c.animator.IsInTransition(0);
                                }
                                catch
                                {
                                }
                                Mod.logger.LogInfo($"[Cine] ANIM-PRESTART cine={c.gameObject.name} state={sn0} stateSpeed={ss0:F2} stateSpeedMult={sm0:F2} loop={sl0} clip={l0:F2} animNow={n0:F2} inTrans={t0}");
                            }
                        }
                    }
                    catch
                    {
                    }
                }
                WatchdogReset();
                startTime = Time.time;
                var p = setplayer != null ? setplayer : Player.main;
                if (p != null)
                {
                    startPos = p.transform.position;
                }
            }
            catch (System.Exception e)
            {
                Mod.logger.LogWarning($"[Cine] LogStart failed: {e}");
            }
        }

        // The skip path: what SkipCinematic actually did to the player
        // (teleport to endTransform only happens when UseEnd() is true).
        [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.SkipCinematic))]
        public static class Skip_Log
        {
            public static void Prefix(PlayerCinematicController __instance, Player player)
            {
                if (player != null)
                {
                    skipFromPos = player.transform.position;
                    skipFromRot = player.transform.rotation;
                }
            }

            public static void Postfix(PlayerCinematicController __instance, Player player)
            {
                if (player == null)
                {
                    return;
                }
                Vector3 to = player.transform.position;
                bool moved = Vector3.Distance(skipFromPos, to) > 0.01f;
                string toDesc = moved ? FmtP(to) : "NOT MOVED (no endTransform teleport)";
                Mod.logger.LogInfo($"[Cine] SKIP name={__instance.gameObject.name} useEnd={UseEnd(__instance)} from={FmtP(skipFromPos)} -> {toDesc}");
            }
        }

        // The director finished (normal end of a played cinematic).
        [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.OnPlayerCinematicModeEnd))]
        public static class End_Log
        {
            public static void Postfix(PlayerCinematicController __instance)
            {
                try
                {
                    var p = __instance.player != null ? __instance.player : Player.main;
                    string moved = "";
                    if (startTime > 0f && p != null)
                    {
                        moved = $" movedFromStart={Vector3.Distance(startPos, p.transform.position):F2}m";
                    }
                    string etPos = __instance.endTransform != null ? FmtP(__instance.endTransform.position) : "NULL";
                    string t = startTime > 0f ? (Time.time - startTime).ToString("F2") : "?";
                    float nearEt = 0f;
                    bool hasEt = __instance.endTransform != null && (object)__instance.endTransform != null && p != null;
                    if (hasEt)
                    {
                        nearEt = Vector3.Distance(p.transform.position, __instance.endTransform.position);
                    }
                    string goals = CineGoalListener.Summary();
                    string near = hasEt ? nearEt.ToString("F2") + "m" : "n/a";
                    Mod.logger.LogInfo($"[Cine] END name={__instance.gameObject.name} t={t}s state={__instance.state} endTransform={etPos} nearEnd={near} goals=[{goals}] player {FmtPlayer(p)}{moved}");
                    Mod.logger.LogInfo($"[Cine] SKIP-CANDIDATE? name={__instance.gameObject.name} goals=[{goals}] (contract above - decide table entry)");
                    // 28/10 build 1 (smart-skip plan): vehicle commit
                    // verdict - the animation events commit the entry
                    // (EnterVehicle/OnPlayerEntered); ground truth for
                    // "was the entry actually committed?".
                    string mounted = Player.main != null && Player.main.currentMountedVehicle != null ? Player.main.currentMountedVehicle.name : "null";
                    bool exoFully = false;
                    var exo = Player.main != null ? Player.main.currentMountedVehicle as Exosuit : null;
                    if (exo != null && (object)exo != null)
                    {
                        exoFully = exo.playerFullyEntered;
                    }
                    Mod.logger.LogInfo($"[Cine] END-STATE name={__instance.gameObject.name} mounted={mounted} exoFullyEntered={exoFully}");
                    CineGoalListener.Clear();
                }
                catch (System.Exception e)
                {
                    Mod.logger.LogWarning($"[Cine] End_Log failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.EndCinematicMode))]
        public static class EndMode_Log
        {
            public static void Postfix(PlayerCinematicController __instance)
            {
                Mod.logger.LogInfo($"[Cine] END-MODE name={__instance.gameObject.name} active={__instance.cinematicModeActive}");
            }
        }

        // State machine transitions (None / In / Update / Out).
        [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.state), MethodType.Setter)]
        public static class State_Log
        {
            public static void Prefix(PlayerCinematicController __instance, PlayerCinematicController.State value)
            {
                string dt = __instance.director != null ? __instance.director.time.ToString("F2") : "null";
                Mod.logger.LogInfo($"[Cine] STATE name={__instance.gameObject.name} {__instance.state}->{value} t={Time.time:F2} directorTime={dt}");
            }
        }

        [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.OnDirectorStopped))]
        public static class DirectorStopped_Log
        {
            public static void Postfix(PlayerCinematicController __instance)
            {
                string t = startTime > 0f ? (Time.time - startTime).ToString("F2") : "?";
                Mod.logger.LogInfo($"[Cine] DIRECTOR-STOPPED name={__instance.gameObject.name} t={t}s");
            }
        }

        // 28/10 build 1 (smart-skip plan): state-machine signal logging. The
        // vehicle/dock state machines advance on these signals, not on
        // frames - this is what a native skip misses (plan
        // docs/superpowers/plans/2026-10-28-vehicle-entry-smart-skip.md).
        private static void LogAnimParam(string receiver, string parameterName, bool value)
        {
            Mod.logger.LogInfo($"[Cine] ANIM-PARAM receiver={receiver} param={parameterName} value={value}");
        }

        // Explicit interface implementation: the IL method name carries the
        // interface prefix (C# 7.3 has no nameof for that) - string form.
        [HarmonyPatch(typeof(Vehicle), "IAnimParamReceiver.ForwardAnimationParameterBool")]
        public static class VehicleAnimParam_Log
        {
            public static void Postfix(Vehicle __instance, string parameterName, bool value)
            {
                LogAnimParam(__instance.gameObject.name, parameterName, value);
            }
        }

        [HarmonyPatch(typeof(Dockable), "IAnimParamReceiver.ForwardAnimationParameterBool")]
        public static class DockableAnimParam_Log
        {
            public static void Postfix(Dockable __instance, string parameterName, bool value)
            {
                LogAnimParam(__instance.gameObject.name, parameterName, value);
            }
        }

        [HarmonyPatch(typeof(VehicleDockingBay), "IAnimParamReceiver.ForwardAnimationParameterBool")]
        public static class VehicleDockingBayAnimParam_Log
        {
            public static void Postfix(VehicleDockingBay __instance, string parameterName, bool value)
            {
                LogAnimParam(__instance.gameObject.name, parameterName, value);
            }
        }

        // Virtual base: covers the Exosuit/SeaMoth/Hoverbike overrides (they
        // call base.EnterVehicle).
        [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.EnterVehicle))]
        public static class VehicleEnter_Log
        {
            public static void Postfix(Vehicle __instance, Player player, bool teleport, bool playEnterAnimation)
            {
                string pname = player != null ? player.name : "null";
                Mod.logger.LogInfo($"[Cine] VEHICLE-ENTER name={__instance.gameObject.name} player={pname} teleport={teleport} playAnim={playEnterAnimation}");
            }
        }

        [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.SetPlayerInside))]
        public static class VehicleInside_Log
        {
            public static void Postfix(Vehicle __instance, bool inside)
            {
                Mod.logger.LogInfo($"[Cine] VEHICLE-INSIDE name={__instance.gameObject.name} inside={inside}");
            }
        }

        [HarmonyPatch(typeof(Exosuit), nameof(Exosuit.OnPlayerEntered))]
        public static class ExoEntered_Log
        {
            public static void Postfix(Exosuit __instance)
            {
                Mod.logger.LogInfo($"[Cine] EXO-ENTERED name={__instance.gameObject.name} fullyEntered={__instance.playerFullyEntered}");
            }
        }

        [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.EjectPlayer))]
        public static class VehicleEject_Log
        {
            public static void Postfix(Vehicle __instance)
            {
                Mod.logger.LogInfo($"[Cine] VEHICLE-EJECT name={__instance.gameObject.name}");
            }
        }

        // 28/10 spec section 5: per-frame watchdog (frame hitches with
        // contract correlation, stuck detection, speed application, speed
        // reset safety net).
        private static PlayerCinematicController activeWatch;
        private static double lastDirTime;
        private static bool lastDirValid;
        private static Vector3 lastPlayerPos;
        private static bool firstFrame;
        private static bool stuckLogged;
        private static PlayableDirector speedAppliedDirector;
        private static Animator speedAppliedAnimator;
        private static PlayerCinematicController speedAnimatorOwner;
        private static string accelAnimLoggedName;
        private static double lastAnimTime;
        private static bool lastAnimValid;
        // 28/10 build 4 (test 5 fix): accel STUCK-NET force-end. Build 3's
        // "anchor the current clip at In->Update, end when it finishes"
        // was wrong: the entry/exit sequences are MULTI-STATE (a short
        // first state, often looping, then the real clip) and the game's
        // end event sits on the LAST clip - build 3 force-ended at the end
        // of the FIRST state (test 5: seatruck exit killed at 0.25s,
        // player left unmounted and falling). The net only fires when the
        // sequence is STUCK: the clip sits at its end (normalizedTime
        // ~1.0, animNow ~ len, and < 1.5*len so multi-loop clips don't
        // count) for 0.3s, or the Update state runs > 2.5s real. Working
        // cines end by the game's own event long before either (test 4:
        // 0.35-0.39s), so they are never preempted.
        // The original bug (test 4) the net fixes: at speed > 1 the
        // game's own forced end (State.Update: real time >= CURRENT clip
        // length) waits a long idle clip with input locked (9.6s stuck).
        private static float lastArmedStart;
        private static double updateEnteredReal;
        private static double clipEndSince;
        private static bool forceEndDone;
        private static bool wasInUpdate;
        private static bool earlyReleaseSkipLogged;

        private static void ResetAnimatorSpeed(Animator anim)
        {
            if (anim == null || (object)anim == null)
            {
                return;
            }
            anim.speed = 1f;
        }

        private static void ResetSpeedOn(PlayableDirector director)
        {
            if (director == null || (object)director == null)
            {
                return;
            }
            var g = director.playableGraph;
            if (!g.IsValid())
            {
                return;
            }
            int n = g.GetRootPlayableCount();
            for (int i = 0; i < n; i++)
            {
                g.GetRootPlayable(i).SetSpeed(1f);
            }
        }

        private static void WatchdogReset()
        {
            ResetSpeedOn(speedAppliedDirector);
            speedAppliedDirector = null;
            ResetAnimatorSpeed(speedAppliedAnimator);
            speedAppliedAnimator = null;
            speedAnimatorOwner = null;
            accelAnimLoggedName = null;
            activeWatch = null;
            lastDirTime = 0d;
            lastDirValid = false;
            lastAnimTime = 0d;
            lastAnimValid = false;
            lastPlayerPos = Vector3.zero;
            firstFrame = true;
            stuckLogged = false;
            lastArmedStart = 0f;
            clipEndSince = -1d;
            forceEndDone = false;
            wasInUpdate = false;
            earlyReleaseSkipLogged = false;
        }

        [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.ManagedLateUpdate))]
        public static class Watchdog
        {
            public static void Postfix(PlayerCinematicController __instance)
            {
                // Speed safety net - BEFORE the activity filter on purpose:
                // DoFailSafeCancel and BreakCinematic deactivate the
                // controller without stopping the director, so a speed set on
                // a reusable graph would leak into the next play otherwise.
                if (speedAppliedDirector != null)
                {
                    bool destroyed = (object)speedAppliedDirector == null;
                    bool notPlaying = !destroyed && speedAppliedDirector.state != PlayState.Playing;
                    bool graphDead = !destroyed && !speedAppliedDirector.playableGraph.IsValid();
                    if (destroyed || graphDead || notPlaying)
                    {
                        ResetSpeedOn(speedAppliedDirector);
                        string dstate = destroyed ? "destroyed" : speedAppliedDirector.state.ToString();
                        Mod.logger.LogInfo($"[Cine] speed reset (watchdog net) cine={speedAppliedDirector.name} state={dstate}");
                        speedAppliedDirector = null;
                    }
                }
                // 28/10 build 2: same net for the animator - the same
                // deactivation paths (fail-safe cancel / break) leave an
                // Animator at speed > 1 if the owner stopped its cinematic.
                if (speedAppliedAnimator != null)
                {
                    bool aDestroyed = (object)speedAppliedAnimator == null;
                    bool ownerGone = !aDestroyed && (speedAnimatorOwner == null || (object)speedAnimatorOwner == null);
                    bool cineEnded = !aDestroyed && !ownerGone && !speedAnimatorOwner.cinematicModeActive;
                    if (aDestroyed || ownerGone || cineEnded)
                    {
                        ResetAnimatorSpeed(speedAppliedAnimator);
                        string astate = aDestroyed ? "destroyed" : (ownerGone ? "owner gone" : "cine ended");
                        Mod.logger.LogInfo($"[Cine] speed reset (watchdog net) anime={speedAppliedAnimator.name} state={astate}");
                        speedAppliedAnimator = null;
                        speedAnimatorOwner = null;
                        accelAnimLoggedName = null;
                    }
                }
                if (!__instance.cinematicModeActive)
                {
                    return;
                }
                // New (re)armed controller - or a NEW CINEMATIC on the same
                // controller (enter/exit share the GO: startTime is bumped
                // by LogStart on every start, so same-instance re-arms too).
                if (activeWatch != __instance || startTime != lastArmedStart)
                {
                    activeWatch = __instance;
                    lastArmedStart = startTime;
                    firstFrame = true;
                    stuckLogged = false;
                    lastDirValid = false;
                    lastAnimValid = false;
                    accelAnimLoggedName = null;
                    clipEndSince = -1d;
                    forceEndDone = false;
                    wasInUpdate = false;
                    earlyReleaseSkipLogged = false;
                }
                string name = __instance.gameObject.name;
                var rule = CinematicsTable.Get(name);
                // 28/10 build 2: any mode with Speed > 1 accelerates (the
                // PlayNoRot truck entry is accelerated too).
                bool accel = rule.Speed > 1f;
                double dt = (double)Time.unscaledDeltaTime;
                var director = __instance.director;
                if (dt > 0.1d)
                {
                    string tag = accel ? " gap=accel" : "";
                    Mod.logger.LogInfo($"[Cine] GAP-HITCH cine={name} dt={dt:F3}{tag}");
                }
                if (director != null && (object)director != null)
                {
                    double dirNow = director.time;
                    if (lastDirValid && !firstFrame)
                    {
                        double delta = dirNow - lastDirTime;
                        // threshold scaled by the applied speed (spec: an
                        // accelerated advance is not a hitch)
                        double threshold = (double)rule.Speed * dt * 2d + 0.05d;
                        if (delta > threshold)
                        {
                            string tag = accel ? " gap=accel" : "";
                            Mod.logger.LogInfo($"[Cine] GAP-DIR cine={name} dirT {lastDirTime:F2}->{dirNow:F2} (+{delta:F2}s){tag}");
                            string inGap = DescribeContractGap(__instance, lastDirTime, dirNow);
                            if (inGap.Length > 0)
                            {
                                Mod.logger.LogInfo($"[Cine] GAP-CONTRACT cine={name} inGap: {inGap}");
                            }
                        }
                        else if (delta < -0.05d)
                        {
                            Mod.logger.LogInfo($"[Cine] GAP-BACK cine={name} dirT {lastDirTime:F2}->{dirNow:F2} (wrap/seek, info only)");
                        }
                    }
                    lastDirTime = dirNow;
                    lastDirValid = true;
                    // Speed application (per frame, self-healing).
                    if (accel)
                    {
                        var g = director.playableGraph;
                        if (g.IsValid())
                        {
                            int n = g.GetRootPlayableCount();
                            for (int i = 0; i < n; i++)
                            {
                                g.GetRootPlayable(i).SetSpeed(rule.Speed);
                            }
                            speedAppliedDirector = director;
                        }
                    }
                }
                else if (__instance.animator != null && (object)__instance.animator != null)
                {
                    // 28/10 build 2 (smart-skip plan): animator-only
                    // cinematic. Speed application (per frame, self-healing)
                    // + progress tracking. The clip still plays in full
                    // (re-parenting + endTransform teleport intact); the
                    // enter/exit bools are persistent, not pulses, so no
                    // event can be missed.
                    // 28/10 build 9 (test 10 data): phase-aware speed for
                    // cines with InSpeed > 0. The commit event must never
                    // fire before the In->Update bool flip: the module's
                    // "get in" event sits at 99.2% of a 1.25s clip =
                    // 0.248s at 5x, 2ms BEFORE the flip (In = 0.25s).
                    // In at 1x keeps it at 20% of the clip during In;
                    // the flip then happens, and the 5x Update commits at
                    // ~0.45s (enter) / ~0.53s (exit) from the In start.
                    if (accel)
                    {
                        float effSpeed = rule.Speed;
                        if (rule.InSpeed > 0f && __instance.state == PlayerCinematicController.State.In)
                        {
                            effSpeed = rule.InSpeed;
                        }
                        __instance.animator.speed = effSpeed;
                        speedAppliedAnimator = __instance.animator;
                        speedAnimatorOwner = __instance;
                        if (accelAnimLoggedName != name)
                        {
                            accelAnimLoggedName = name;
                            Mod.logger.LogInfo($"[Cine] ACCEL-ANIM name={name} speed={rule.Speed}{(rule.InSpeed > 0f ? $" in={rule.InSpeed}" : "")}");
                        }
                    }
                    double animNow;
                    double animLen;
                    string animStateName;
                    double animStateSpeed;
                    double animStateSpeedMult;
                    bool animStateLoop;
                    if (TryGetAnimState(__instance.animator, out animNow, out animLen, out animStateName, out animStateSpeed, out animStateSpeedMult, out animStateLoop) && animNow >= 0d)
                    {
                        if (lastAnimValid && !firstFrame)
                        {
                            double delta = animNow - lastAnimTime;
                            double threshold = (double)rule.Speed * dt * 2d + 0.05d;
                            if (delta > threshold)
                            {
                                string tag = accel ? " gap=accel" : "";
                                Mod.logger.LogInfo($"[Cine] GAP-ANIM cine={name} t {lastAnimTime:F2}->{animNow:F2} (+{delta:F2}s){tag}");
                                LogAnimState(name, __instance.animator, animStateName, animStateSpeed, animStateSpeedMult, animStateLoop, animLen, animNow);
                            }
                            else if (delta < -0.5d)
                            {
                                Mod.logger.LogInfo($"[Cine] GAP-ANIM-RESET cine={name} t {lastAnimTime:F2}->{animNow:F2} (clip change/wrap, info only)");
                                LogAnimState(name, __instance.animator, animStateName, animStateSpeed, animStateSpeedMult, animStateLoop, animLen, animNow);
                            }
                        }
                        // 28/10 build 8c: sample In AND Update (test 9:
                        // the module enter committed at the end of In -
                        // the machine state during In was the missing
                        // evidence), for every cinematic (throttled).
                        if ((__instance.state == PlayerCinematicController.State.In
                            || __instance.state == PlayerCinematicController.State.Update)
                            && Time.time - lastAnimStateLog > 0.5d)
                        {
                            LogAnimState(name, __instance.animator, animStateName, animStateSpeed, animStateSpeedMult, animStateLoop, animLen, animNow);
                        }
                        lastAnimTime = animNow;
                        lastAnimValid = true;
                        if (accel)
                        {
                            bool inUpdate = __instance.state == PlayerCinematicController.State.Update;
                            if (inUpdate && !wasInUpdate)
                            {
                                updateEnteredReal = Time.time;
                            }
                            wasInUpdate = inUpdate;
                            // 28/10 build 8 (user-approved principle):
                            // table-gated EARLY RELEASE. Tests 6/8: this
                            // cine's prep->exit transition never fires at
                            // 5x and the cause is unobservable (opaque
                            // controller + untrustworthy state telemetry -
                            // challenger review). The game can never
                            // self-commit a stuck run, so commit ourselves
                            // once the run is proven in flight but not
                            // progressing: 0.5s into Update (total ~0.75s
                            // from In, vs ~2.8s with the 2.5s net).
                            // Safe only because BOTH preconditions hold
                            // for this cine (proven, not inferred):
                            //  - endTransform present -> the commit
                            //    teleports to the verified exit pose
                            //    (UseEnd runtime guard below);
                            //  - commit-free (no AnimationEvent ends the
                            //    sequence) -> ending early skips nothing
                            //    (tests 6/8: the forced end commits the
                            //    module transfer correctly).
                            // If the sequence ever completes naturally
                            // before the deadline, state leaves Update and
                            // this never fires.
                            if (inUpdate && !forceEndDone && rule.EarlyRelease
                                && Time.time - updateEnteredReal > 0.5d)
                            {
                                double totalT = Time.time - updateEnteredReal;
                                if ((object)__instance.endTransform != null)
                                {
                                    forceEndDone = true;
                                    Mod.logger.LogInfo($"[Cine] EARLY-RELEASE name={name} updateT={totalT:F2}s (table-gated, endTransform present) -> force end");
                                    __instance.OnPlayerCinematicModeEnd();
                                }
                                else if (!earlyReleaseSkipLogged)
                                {
                                    // precondition broken: do NOT mark
                                    // forceEndDone - the 2.5s stuck net
                                    // must still be able to take over.
                                    earlyReleaseSkipLogged = true;
                                    Mod.logger.LogWarning($"[Cine] EARLY-RELEASE SKIPPED name={name} (endTransform NULL - precondition broken, stuck net takes over)");
                                }
                            }
                            if (!inUpdate || forceEndDone)
                            {
                                // left Update (In/Out/None) or already fired.
                                clipEndSince = -1d;
                            }
                            else
                            {
                                // The clip "sat on its end": normalizedTime
                                // reached 1.0 and stopped (non-looping clip
                                // clamped, or a state machine waiting on a
                                // pending transition). Multi-loop clips are
                                // excluded (animNow several times the length
                                // is a loop, not an end), and an active
                                // crossfade is excluded too (a transition in
                                // flight = the sequence is progressing).
                                bool inTransition = true;
                                try
                                {
                                    inTransition = __instance.animator.IsInTransition(0);
                                }
                                catch
                                {
                                    inTransition = false;
                                }
                                bool atEnd = !inTransition
                                    && animLen > 0d
                                    && Math.Abs(animNow - animLen) <= 0.05d
                                    && animNow < 1.5d * animLen;
                                if (atEnd)
                                {
                                    if (clipEndSince < 0d)
                                    {
                                        clipEndSince = Time.time;
                                    }
                                }
                                else
                                {
                                    // a new clip started (or the current one
                                    // is still playing): the sequence is
                                    // progressing - not stuck.
                                    clipEndSince = -1d;
                                }
                                double totalT = Time.time - updateEnteredReal;
                                bool endAtEnd = clipEndSince >= 0d && Time.time - clipEndSince > 0.3d;
                                bool endTotal = totalT > 2.5d;
                                if (endAtEnd || endTotal)
                                {
                                    forceEndDone = true;
                                    string reason = endAtEnd ? "clip-end-stuck" : "total";
                                    // test 6: the effective anim speed was
                                    // ~1x despite speed=5 - log the real
                                    // value + normalizedTime to diagnose.
                                    float realSpeed = 1f;
                                    try
                                    {
                                        realSpeed = __instance.animator.speed;
                                    }
                                    catch
                                    {
                                    }
                                    double nt = animLen > 0d ? animNow / animLen : -1d;
                                    Mod.logger.LogInfo($"[Cine] ACCEL-STUCK-NET name={name} updateT={totalT:F2}s state={animStateName} stateSpeed={animStateSpeed:F2} stateSpeedMult={animStateSpeedMult:F2} loop={animStateLoop} clip={animLen:F2}s animNow={animNow:F2} nt={nt:F2} animeSpeed={realSpeed:F2} enforce={__instance.enforceCinematicModeEnd} reason={reason} -> force end @ {rule.Speed}x");
                                    Mod.logger.LogInfo($"[Cine] STUCK-PARAMS name={name} " + DumpParamSnapshot(__instance.animator));
                                    // the designed end path (the game's
                                    // animation events call the same
                                    // method; it is idempotent).
                                    __instance.OnPlayerCinematicModeEnd();
                                }
                            }
                        }
                    }
                }
                // Position jump = tunneling risk - only in mid-play at x1:
                // the In/Out lerps, endTransform teleports and accel jumps
                // are legitimate.
                var player = __instance.player;
                if (player != null && (object)player != null)
                {
                    if (!firstFrame && !accel && __instance.state == PlayerCinematicController.State.Update)
                    {
                        float jump = Vector3.Distance(lastPlayerPos, player.transform.position);
                        if (jump > 1f)
                        {
                            Mod.logger.LogInfo($"[Cine] RISK tunneling cine={name} jump={jump:F2}m");
                        }
                    }
                    lastPlayerPos = player.transform.position;
                }
                // Stuck: past the expire time, still active, and not merely
                // paused (a paused director is the game's "Pause" event -
                // the failsafe unscaled... scaled-time Invoke will fire).
                if (!stuckLogged && PlayerCinematicController.cinematicModeCount > 0
                    && Time.time > PlayerCinematicController.cinematicActivityExpireTime)
                {
                    bool dirNull = director == null || (object)director == null;
                    string dstate = dirNull ? "null" : director.state.ToString();
                    if (dirNull || director.state == PlayState.Playing)
                    {
                        stuckLogged = true;
                        Mod.logger.LogInfo($"[Cine] STUCK cine={name} directorState={dstate} (expireTime passed, still active)");
                    }
                }
                firstFrame = false;
            }
        }

        // 28/10 spec section 5: EndCinematicMode is the chokepoint of the
        // normal end, DoFailSafeCancel (the `stopped` event never fired -
        // that is its reason to exist), OnPlayerDeath and OnDisable. It does
        // not stop the director - reset the speed + drop the goal listener
        // here (death has no sceneUnloaded in BZ: respawn in place).
        [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.EndCinematicMode))]
        public static class EndCinematicMode_SpeedReset
        {
            public static void Postfix(PlayerCinematicController __instance)
            {
                ResetSpeedOn(__instance.director);
                if (speedAppliedDirector == __instance.director)
                {
                    speedAppliedDirector = null;
                }
                CinematicsDiagnostics.CineGoalListener.Remove();
                CinematicsDiagnostics.CineGoalListener.Clear();
            }
        }

        // 28/10 spec section 5: BreakCinematic (ExpansionIntroManagerV1)
        // deactivates the controller WITHOUT touching the director - no
        // `stopped` event, no EndCinematicMode: reset the speed here too.
        // The director keeps running (game behaviour, out of scope).
        [HarmonyPatch(typeof(PlayerCinematicController), nameof(PlayerCinematicController.BreakCinematic))]
        public static class BreakCinematic_SpeedReset
        {
            public static void Postfix(PlayerCinematicController __instance)
            {
                ResetSpeedOn(__instance.director);
                if (speedAppliedDirector == __instance.director)
                {
                    speedAppliedDirector = null;
                }
                Mod.logger.LogInfo($"[Cine] BREAK cine={__instance.gameObject.name} (director left running - game behaviour)");
            }
        }
    }
}

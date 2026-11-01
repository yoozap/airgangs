using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Plays ONE real Humanoid AnimationClip on a CharacterRig's Animator via
    /// the Playables API, for the duration of a single acrobatic move (roll,
    /// vault, mantle, ledge-grab, landing, ...). Built for this port because
    /// NOTHING in this project plays an Animator clip at all before this file:
    /// every veggie body's Animator exists only to carry the Humanoid Avatar
    /// (CharacterRig.IsHumanoid/GetBoneTransform); the hero's run/idle/roll/
    /// slide/climb poses are 100% procedural bone-twisting
    /// (CharacterRig.BoneTwist/BoneSet, FootPoses, FootController's own
    /// RollPose/SlidePose/DivePose/AirPose) — there is no AnimatorController
    /// asset anywhere in `unity/Assets` and no prior Playables usage to
    /// reconcile with.
    ///
    /// ================================ WHY THIS COMPOSES FOR FREE =================
    /// CharacterRig.BoneTwist/BoneSet/GetRec (CharacterRig.cs:650-716) capture
    /// "Base" off `bone.localRotation` AS IT STANDS the first time they touch a
    /// bone each frame — i.e. off whatever the ANIMATOR just wrote, clip or no
    /// clip (`ClipQ`, CharacterRig.cs:643, is the explicit accessor for this:
    /// "what the ANIMATOR put on this bone"). So once this class is driving a
    /// clip through the rig's real Animator, every existing procedural pose
    /// write (HandsOnLedge's world-locked IK, SetGrip, SettleFeet, PlantFoot)
    /// keeps composing correctly ON TOP of the clip with ZERO changes —
    /// FootClimb.cs's own HandsOnLedge already reads `rig.ClipQ(upper)` as its
    /// pre-IK base, which only makes sense if a real clip can be present. The
    /// callers that want the clip's shape to show UNCHANGED (the roll's tuck,
    /// the climb's mantle swing) instead SKIP their own FootPoses call while
    /// this class IsPlaying — see FootController.RollPose / FootClimb.Tick's
    /// MantleClipActive/HangClipActive gates — rather than this class trying
    /// to suppress them from the outside.
    ///
    /// ================================ THE GRAPH =================================
    /// One AnimationClipPlayable connected straight to an AnimationPlayableOutput
    /// bound to the rig's Animator (no mixer, no AnimatorController — there is
    /// nothing to blend against inside Unity's own animation system, because
    /// nothing else here ever occupies that slot). Unity auto-evaluates a
    /// Playing graph bound this way between Update and LateUpdate, same slot
    /// CharacterRig.cs's own header documents for the Animator in general, so
    /// by the time FootController's LateUpdate-driven Update() runs, the
    /// clip's current frame is already on the bones.
    ///
    /// ================================ THE BLEND-IN ===============================
    /// Unity's own evaluation is a hard sample — no fade. The blend-in is done
    /// the same way RollState.In already does it for ActGroup (FootController.
    /// RollPose: capture a start snapshot, Slerp toward the live target over a
    /// short ease): <see cref="Play"/> snapshots every bone's localRotation
    /// right before the new clip takes over, and <see cref="Tick"/> Slerps
    /// each one from that snapshot toward whatever the Animator just sampled,
    /// every frame, until the blend window closes.
    ///
    /// The blend-OUT is deliberately NOT engineered here: when a clip ends
    /// (or <see cref="Stop"/> is called), the bones are simply left exactly
    /// where the clip left them and the graph stops being evaluated, and the
    /// NEXT procedural writer (SwingArms/SwingBody's own springs, or another
    /// FootPoses call) reads that as ITS base and eases from there under its
    /// own existing spring/ease constants — the same "compose on whatever is
    /// there" mechanism as the blend-in, already proven throughout this rig.
    /// </summary>
    public sealed class ParkourClipPlayer
    {
        PlayableGraph _graph;
        AnimationPlayableOutput _output;
        AnimationClipPlayable _clipPlayable;
        Animator _anim;
        bool _graphBuilt;

        float _blendT, _blendDur;
        Dictionary<Transform, Quaternion> _blendFrom;
        bool _loop;
        float _clipLen;

        public bool IsPlaying { get; private set; }
        public string CurrentClip { get; private set; }

        /// <summary>0..1 of the clip's own length — NaN-safe for a probe/test
        /// that reads it before anything has ever played.</summary>
        public float NormalizedTime => (_graphBuilt && _clipPlayable.IsValid() && _clipLen > 0f)
            ? Mathf.Clamp01((float)(_clipPlayable.GetTime() / _clipLen)) : 0f;

        static float Smooth01(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        void EnsureGraph(Animator anim)
        {
            if (_graphBuilt && _anim == anim) return;
            if (_graphBuilt) _graph.Destroy();
            _anim = anim;
            _graph = PlayableGraph.Create("ParkourClipPlayer");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            _output = AnimationPlayableOutput.Create(_graph, "ParkourOutput", anim);
            _graphBuilt = true;
        }

        /// <summary>Start (or swap to) a clip. `loop` matches the few loop
        /// cases this port actually wants (the hang's ledge-grab-braced-idle);
        /// every one-shot move auto-stops itself via <see cref="Tick"/>.</summary>
        public void Play(Animator anim, AnimationClip clip, bool loop, float blendIn = 0.1f)
        {
            if (anim == null || clip == null) return;
            if (IsPlaying && CurrentClip == clip.name && _loop == loop) return; // already playing this one — don't restart/snap it
            EnsureGraph(anim);

            _blendFrom = SnapshotBones(anim.transform);
            if (_clipPlayable.IsValid()) _clipPlayable.Destroy();
            _clipPlayable = AnimationClipPlayable.Create(_graph, clip);
            _clipPlayable.SetApplyFootIK(false);
            _clipPlayable.SetApplyPlayableIK(false);
            _clipPlayable.SetTime(0);
            _output.SetSourcePlayable(_clipPlayable);
            if (!_graph.IsPlaying()) _graph.Play();

            IsPlaying = true;
            CurrentClip = clip.name;
            _loop = loop;
            _clipLen = Mathf.Max(0.0001f, clip.length);
            _blendDur = Mathf.Max(0.0001f, blendIn);
            _blendT = 0f;
        }

        /// <summary>Hand the body back to the procedural pose system — see
        /// class header's "the blend-out is deliberately not engineered
        /// here".</summary>
        public void Stop()
        {
            if (!IsPlaying) return;
            IsPlaying = false;
            if (_graphBuilt && _graph.IsValid() && _graph.IsPlaying()) _graph.Stop();
        }

        /// <summary>Call once a frame (FootController.Update, which this
        /// port's own rule already requires to run from LateUpdate — after
        /// Unity's automatic Animator/Playables evaluation for this frame, so
        /// the blend below reads the CLIP's just-sampled pose, not last
        /// frame's). Advances the blend-in and auto-stops a non-looping clip
        /// at its own end.</summary>
        public void Tick(float dt)
        {
            if (!IsPlaying || !_graphBuilt) return;

            if (_blendFrom != null)
            {
                _blendT += dt;
                float k = Smooth01(Mathf.Clamp01(_blendT / _blendDur));
                foreach (var kv in _blendFrom)
                {
                    if (kv.Key == null) continue;
                    kv.Key.localRotation = Quaternion.Slerp(kv.Value, kv.Key.localRotation, k);
                }
                if (k >= 1f) _blendFrom = null;
            }

            if (!_loop && _clipPlayable.IsValid())
            {
                double t = _clipPlayable.GetTime();
                if (t >= _clipLen - 1.0 / 30.0) Stop(); // one frame's grace at 30fps-equivalent
            }
        }

        /// <summary>Full teardown — GameLoop.OnDestroy's own PlayableGraph
        /// hygiene (a graph is a native handle, not GC-collected).</summary>
        public void Destroy()
        {
            if (_graphBuilt && _graph.IsValid()) _graph.Destroy();
            _graphBuilt = false;
            IsPlaying = false;
        }

        static Dictionary<Transform, Quaternion> SnapshotBones(Transform modelRoot)
        {
            var d = new Dictionary<Transform, Quaternion>();
            foreach (var t in modelRoot.GetComponentsInChildren<Transform>(true)) d[t] = t.localRotation;
            return d;
        }
    }
}

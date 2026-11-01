using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace TbilisiFly.Character
{
    /// <summary>
    /// THE ONE OWNER of a hero's Animator on foot (2026-11-01, user: "running,
    /// walking, whole movement take from the assets"): a PlayableGraph that
    /// plays the purchased Humanoid clips on CharacterRig.Anim —
    ///
    ///   layer 0  base mixer   [ loco A | loco B | move ]
    ///   layer 1  overlay      one masked clip (aim/shoot/cover/reload) — the
    ///                         cover-shoot integration layers its upper-body
    ///                         clips here through <see cref="PlayOverlay"/>
    ///                         instead of attaching its own AnimatorController.
    ///
    /// LOCOMOTION (<see cref="PlayLoco"/>): a looping clip chosen by
    /// FootController from its speed band (parkour-idle, Cover+Shoot
    /// walk/jog, the parkour sprint loop, strafe/sneak/crouch, fall) with a
    /// playback rate so the feet match the body's real speed; swapping the
    /// loop cross-fades A→B over `fade` seconds inside the mixer.
    ///
    /// MOVES (<see cref="Play"/>): a one-shot parkour clip (roll, vault,
    /// mantle, wall plant, leap, landing, kipup, ...) takes the base mixer
    /// over — its weight ramps 0→1 over `blendIn`, the loco weights scale
    /// down by the same amount, and when the move ends its weight ramps back
    /// so the loop underneath (or the procedural pose when nothing loops)
    /// takes over without a snap. A move started while NO loop is playing
    /// (an armed hero: the old procedural stance still owns the body) keeps
    /// the earlier snapshot-and-Slerp blend-in so the first frames do not
    /// read the bind pose.
    ///
    /// WHY THE PROCEDURAL LAYERS STILL COMPOSE: CharacterRig.BoneTwist/BoneSet
    /// capture "Base" off whatever the Animator just wrote (CharacterRig.cs:650-716
    /// GetRec/ClimbQ), so world-locked hands on a ledge, crouch, weapon reach
    /// etc. keep writing on top of the clip. The writers that must NOT fight
    /// a clip (the stick-limb gait, SwingArms/SwingBody, the air/roll/hang
    /// poses) are gated by FootController while a clip owns the body.
    ///
    /// Nothing else occupies the Animator: there is no AnimatorController in
    /// the project and this graph is the only Playables user. When nothing
    /// is playing the graph is STOPPED so the Animator writes nothing and
    /// the procedural stance owns the bones exactly as before this file.
    /// </summary>
    public sealed class ParkourClipPlayer
    {
        PlayableGraph _graph;
        AnimationPlayableOutput _output;
        AnimationLayerMixerPlayable _layers;
        AnimationMixerPlayable _base;
        AnimationClipPlayable _locoA, _locoB, _move, _overlay;
        Animator _anim;
        bool _built;

        // locomotion
        int _locoCur = -1;            // 0 = A, 1 = B, -1 = none
        int _locoPrev = -1;           // the slot fading OUT (-1 = none)
        float _locoFade = 1f, _locoFadeDur = 0.15f;
        string _locoName;
        float _locoRate = 1f;
        float _locoLenA, _locoLenB;

        // move (one-shot)
        bool _moveOn;
        float _moveW, _moveTarget, _moveBlend = 0.1f, _moveLen;
        bool _moveLoop;
        string _moveName;
        Dictionary<Transform, Quaternion> _blendFrom; float _blendT, _blendDur;

        // overlay
        float _overlayW, _overlayTarget, _overlayFade = 0.1f;
        string _overlayName;
        bool _overlayLoop; float _overlayLen;

        /// <summary>A MOVE clip currently owns the base (loops do not count).</summary>
        public bool IsPlaying => _moveOn;
        /// <summary>The move clip's name (take = file stem), null when no move.</summary>
        public string CurrentClip => _moveOn ? _moveName : null;
        /// <summary>The locomotion loop's clip name, null when none.</summary>
        public string LocoClip => _locoCur >= 0 ? _locoName : null;
        public string OverlayClip => _overlayTarget > 0f ? _overlayName : null;
        public float LocoRate => _locoRate;
        /// <summary>True when the graph is writing the body at all (loco, move or overlay).</summary>
        public bool OwnsBody => _locoCur >= 0 || _moveOn || _moveW > 0.001f;

        public float NormalizedTime => (_built && _move.IsValid() && _moveLen > 0f)
            ? Mathf.Clamp01((float)(_move.GetTime() / _moveLen)) : 0f;

        static float Smooth01(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        void Build(Animator anim)
        {
            if (_built && _anim == anim) return;
            if (_built) _graph.Destroy();
            _anim = anim;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate; // headless/offscreen must still evaluate
            anim.applyRootMotion = false;                          // FootController owns the root
            _graph = PlayableGraph.Create("HeroClips");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            _output = AnimationPlayableOutput.Create(_graph, "HeroOutput", anim);
            _layers = AnimationLayerMixerPlayable.Create(_graph, 2);
            _base = AnimationMixerPlayable.Create(_graph, 3);
            _graph.Connect(_base, 0, _layers, 0);
            _layers.SetInputWeight(0, 1f);
            _layers.SetInputWeight(1, 0f);
            _output.SetSourcePlayable(_layers);
            _built = true;
            _locoCur = -1; _moveOn = false; _moveW = _moveTarget = 0f; _overlayW = _overlayTarget = 0f;
        }

        AnimationClipPlayable MakeClip(AnimationClip clip)
        {
            var p = AnimationClipPlayable.Create(_graph, clip);
            p.SetApplyFootIK(false);
            p.SetApplyPlayableIK(false);
            p.SetTime(0);
            return p;
        }

        // ------------------------------------------------------------ locomotion
        /// <summary>Cross-fade the looping stance clip to `clip` (no-op if it is
        /// already the current loop; the rate is updated either way).</summary>
        public void PlayLoco(Animator anim, AnimationClip clip, float rate = 1f, float fade = 0.15f)
        {
            if (anim == null || clip == null) return;
            Build(anim);
            if (_locoCur >= 0 && _locoName == clip.name)
            {
                SetRate(rate);
                return;
            }
            int slot = _locoCur == 0 ? 1 : 0;
            if (slot == 0)
            {
                if (_locoA.IsValid()) { _graph.Disconnect(_base, 0); _locoA.Destroy(); }
                _locoA = MakeClip(clip); _graph.Connect(_locoA, 0, _base, 0); _locoLenA = clip.length;
            }
            else
            {
                if (_locoB.IsValid()) { _graph.Disconnect(_base, 1); _locoB.Destroy(); }
                _locoB = MakeClip(clip); _graph.Connect(_locoB, 0, _base, 1); _locoLenB = clip.length;
            }
            _locoPrev = _locoCur;
            _locoCur = slot;
            _locoName = clip.name;
            _locoFade = 0f; _locoFadeDur = Mathf.Max(0.0001f, fade);
            SetRate(rate);
            if (!_graph.IsPlaying()) _graph.Play();
        }

        void SetRate(float rate)
        {
            _locoRate = Mathf.Clamp(rate, 0.05f, 3f);
            if (_locoCur == 0 && _locoA.IsValid()) _locoA.SetSpeed(_locoRate);
            if (_locoCur == 1 && _locoB.IsValid()) _locoB.SetSpeed(_locoRate);
        }

        /// <summary>Drop the locomotion loop (the procedural stance takes the
        /// bones back once the fade-out below completes).</summary>
        public void StopLoco(float fade = 0.15f)
        {
            if (_locoCur < 0) return;
            _locoPrev = _locoCur;
            _locoCur = -1; _locoName = null;
            _locoFade = 0f; _locoFadeDur = Mathf.Max(0.0001f, fade);
        }

        // ------------------------------------------------------------ moves
        /// <summary>Start (or swap to) a one-shot move clip. `loop` is for the
        /// few held poses (ledge-grab-braced-idle, lying-back, midair).</summary>
        public void Play(Animator anim, AnimationClip clip, bool loop, float blendIn = 0.1f)
        {
            if (anim == null || clip == null) return;
            if (_moveOn && _moveName == clip.name && _moveLoop == loop) return;
            Build(anim);
            bool nothingUnder = _locoCur < 0 && _moveW < 0.01f;
            _blendFrom = nothingUnder ? SnapshotBones(anim.transform) : null;
            _blendT = 0f; _blendDur = Mathf.Max(0.0001f, blendIn);
            if (_move.IsValid()) { _graph.Disconnect(_base, 2); _move.Destroy(); }
            _move = MakeClip(clip);
            _graph.Connect(_move, 0, _base, 2);
            _moveOn = true;
            _moveName = clip.name;
            _moveLoop = loop;
            _moveLen = Mathf.Max(0.0001f, clip.length);
            _moveBlend = Mathf.Max(0.0001f, blendIn);
            _moveTarget = 1f;
            if (nothingUnder) _moveW = 1f; // the snapshot blend does the easing — the mixer must not mix toward the bind pose
            if (!_graph.IsPlaying()) _graph.Play();
        }

        /// <summary>Hand the body back (the move's weight fades out; the loop
        /// underneath or the procedural stance takes over).</summary>
        public void Stop()
        {
            if (!_moveOn) return;
            _moveOn = false;
            _moveTarget = 0f;
            _blendFrom = null;
        }

        // ------------------------------------------------------------ overlay
        /// <summary>Layer a masked clip (upper body: aim, shoot, reload, cover
        /// peek) over the base. Weight fades to `weight`; `mask` may be null
        /// for a whole-body override.</summary>
        public void PlayOverlay(Animator anim, AnimationClip clip, AvatarMask mask, float weight = 1f, bool loop = true, float fade = 0.1f)
        {
            if (anim == null || clip == null) return;
            Build(anim);
            if (_overlayName != clip.name || !_overlay.IsValid())
            {
                if (_overlay.IsValid()) { _graph.Disconnect(_layers, 1); _overlay.Destroy(); }
                _overlay = MakeClip(clip);
                _graph.Connect(_overlay, 0, _layers, 1);
                _overlayName = clip.name;
                _overlayLoop = loop; _overlayLen = Mathf.Max(0.0001f, clip.length);
            }
            if (mask != null) _layers.SetLayerMaskFromAvatarMask(1, mask);
            _layers.SetLayerAdditive(1, false);
            _overlayTarget = Mathf.Clamp01(weight);
            _overlayFade = Mathf.Max(0.0001f, fade);
            if (!_graph.IsPlaying()) _graph.Play();
        }

        public void StopOverlay(float fade = 0.1f)
        {
            _overlayTarget = 0f; _overlayFade = Mathf.Max(0.0001f, fade);
        }

        /// <summary>Everything off at once (teleports, boarding, death).</summary>
        public void StopAll()
        {
            Stop(); StopLoco(0.05f); StopOverlay(0.05f);
        }

        // ------------------------------------------------------------ per frame
        /// <summary>Once a frame from FootController.Update (LateUpdate, after
        /// the Animator evaluated): advance fades, wrap loops, auto-stop a
        /// finished one-shot, and stop the graph when nothing plays.</summary>
        public void Tick(float dt)
        {
            if (!_built) return;

            // loco cross-fade
            if (_locoFade < 1f) _locoFade = Mathf.Min(1f, _locoFade + dt / _locoFadeDur);
            float k = Smooth01(_locoFade);
            float wA = 0f, wB = 0f;
            if (_locoCur == 0) wA = k; else if (_locoCur == 1) wB = k;
            if (_locoPrev == 0 && _locoA.IsValid()) wA += 1f - k; else if (_locoPrev == 1 && _locoB.IsValid()) wB += 1f - k;
            if (k >= 1f) _locoPrev = -1;

            // move weight ramp
            if (_moveTarget > _moveW) _moveW = Mathf.Min(_moveTarget, _moveW + dt / _moveBlend);
            else if (_moveTarget < _moveW) _moveW = Mathf.Max(_moveTarget, _moveW - dt / Mathf.Max(0.05f, _moveBlend));
            float mw = Smooth01(_moveW);
            bool locoAlive = (wA + wB) > 0.001f;
            // with a loop underneath the move takes its share; without one the
            // move is the only thing writing (weight 1 the whole time — the
            // bone snapshot below does the easing, never the mixer).
            float lw = locoAlive ? (1f - mw) : 0f;
            _base.SetInputWeight(0, wA * lw);
            _base.SetInputWeight(1, wB * lw);
            _base.SetInputWeight(2, locoAlive ? mw : (_moveOn || _moveW > 0.001f ? 1f : 0f));

            // overlay fade
            if (_overlayTarget > _overlayW) _overlayW = Mathf.Min(_overlayTarget, _overlayW + dt / _overlayFade);
            else if (_overlayTarget < _overlayW) _overlayW = Mathf.Max(_overlayTarget, _overlayW - dt / _overlayFade);
            _layers.SetInputWeight(1, Smooth01(_overlayW));

            // loops: wrap by hand (a clip imported without loopTime just stops)
            Wrap(ref _locoA, _locoLenA); Wrap(ref _locoB, _locoLenB);
            if (_moveOn && _moveLoop) Wrap(ref _move, _moveLen);
            if (_overlayLoop && _overlay.IsValid()) Wrap(ref _overlay, _overlayLen);

            // snapshot blend-in for a move started over nothing
            if (_blendFrom != null)
            {
                _blendT += dt;
                float kb = Smooth01(Mathf.Clamp01(_blendT / _blendDur));
                foreach (var kv in _blendFrom)
                    if (kv.Key != null) kv.Key.localRotation = Quaternion.Slerp(kv.Value, kv.Key.localRotation, kb);
                if (kb >= 1f) _blendFrom = null;
            }

            // one-shot auto-stop
            if (_moveOn && !_moveLoop && _move.IsValid() && _move.GetTime() >= _moveLen - 1.0 / 30.0) Stop();

            bool anything = _locoCur >= 0 || locoAlive || _moveOn || _moveW > 0.001f || _overlayW > 0.001f || _overlayTarget > 0f;
            if (!anything && _graph.IsPlaying()) _graph.Stop();
            else if (anything && !_graph.IsPlaying()) _graph.Play();
        }

        static void Wrap(ref AnimationClipPlayable p, float len)
        {
            if (!p.IsValid() || len <= 0f) return;
            double t = p.GetTime();
            if (t >= len) p.SetTime(t % len);
        }

        /// <summary>Full teardown — GameLoop.OnDestroy's own PlayableGraph
        /// hygiene (a graph is a native handle, not GC-collected).</summary>
        public void Destroy()
        {
            if (_built && _graph.IsValid()) _graph.Destroy();
            _built = false;
            _moveOn = false; _locoCur = -1; _locoPrev = -1;
        }

        static Dictionary<Transform, Quaternion> SnapshotBones(Transform modelRoot)
        {
            var d = new Dictionary<Transform, Quaternion>();
            foreach (var t in modelRoot.GetComponentsInChildren<Transform>(true)) d[t] = t.localRotation;
            return d;
        }
    }
}

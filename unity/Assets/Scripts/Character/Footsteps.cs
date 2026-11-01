using System.Collections.Generic;
using TbilisiFly.Combat;
using TbilisiFly.Core;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Port of src/footsteps.js (139 lines) — steps read off the foot bones,
    /// ground classified per step.
    ///
    /// Every dependency this needed from elsewhere already exists and is reused
    /// rather than rebuilt:
    ///   - ground CLASS (bare earth/grass/water) off the terrain grid:
    ///     TbilisiFly.Data.TerrainSampler.ClassAt (Assets/Scripts/Data/TerrainSampler.cs,
    ///     verified) — src/footsteps.js:76 `w.groundClassAt`
    ///   - structural surface CLASSIFICATION (asphalt vs masonry vs a window cell):
    ///     TbilisiFly.Combat.SurfaceClassifier.Classify (Ballistics.cs) — the exact
    ///     same decision tree as src/ballistics.js's classifySurface, which
    ///     footsteps.js:3 imports and calls directly. Passing `World = null` (the
    ///     default) falls through to the material-name heuristics exactly as the
    ///     JS does "when world is absent" (SurfaceClassifier's own doc comment);
    ///     wire a real IBallisticsWorld in once City exposes one.
    ///   - the actual SOUND: TbilisiFly.Core.FxAudio.Footstep/Landing
    ///     (Assets/Scripts/Core/AudioFx.cs) — a line-for-line port of
    ///     src/audiofx.js's footstep()/landing(), gait gain/rate tables included.
    ///     This file only resolves WHICH role string to hand it (the ROLE table
    ///     below, identical to footsteps.js:28-32).
    ///
    /// ONE NATIVE REPLACEMENT worth naming: footsteps.js:67-71 hand-rolls a
    /// "nearby collidables" broadphase before raycasting one ray down through it
    /// (`w.collidablesNear`) because Three.js has no scene-graph spatial index of
    /// its own. Unity's `Physics.RaycastAll` against a layer mask IS that
    /// broadphase — there is nothing here standing in for `collidablesNear`.
    ///
    /// FOOT BONES, PER STEP: CharacterRig.LegPair() (characters.js:843-857,
    /// ported in CharacterRig.cs) gives the two foot/toe bones directly off the
    /// Humanoid Avatar for banana/eggplant/toad, and the hand-mapped 'toy' rig
    /// bones for the elephant. A body with neither (should not happen among the
    /// 4-body roster, but handled exactly as the JS does) falls back to a
    /// cadence derived from speed alone (footsteps.js:111-119).
    /// </summary>
    public sealed class Footsteps
    {
        const float ContactH = 0.075f;  // m above the ground a foot counts as down
        const float LiftH = 0.14f;      // m it has to rise before the next contact counts
        const float MinSpeed = 0.3f;    // m/s — shuffling on the spot is not a step
        const float SurfRecheck = 0.5f; // s between ground checks while moving

        static readonly Dictionary<string, float> Cadence = new()
        {
            { "crouch", 1.6f }, { "walk", 1.9f }, { "run", 2.9f }, { "sprint", 3.4f },
        };

        // footsteps.js:28-32 ROLE — identical surface -> bank-role mapping.
        static readonly Dictionary<string, string> Role = new()
        {
            { "concrete", "step_concrete" }, { "asphalt", "step_concrete" },
            { "masonry", "step_concrete" }, { "stone", "step_concrete" }, { "glass", "step_concrete" },
            { "grass", "step_grass" }, { "foliage", "step_grass" },
            { "soil", "step_dirt" },
            { "metal", "step_metal" },
            { "wood", "step_wood" },
            { "water", "step_water" },
        };

        /// <summary>Everything Update()/Land() need off the mover, in place of
        /// reaching into src/foot.js's `foot` object (foot.js is a different
        /// worker's port — this struct is the narrow contract instead).</summary>
        public struct Query
        {
            public Vector3 Pos;
            public float Speed;       // signed, m/s
            public bool Grounded;
            public bool Crouched;
            public bool Stroll;       // the gentleman's walk (Cmd toggle, foot.js)
            public float RunSpeed;    // this hero's race-based run speed
            /// <summary>true while a climb/roll/slide/dive owns the body outright
            /// (footsteps.js:89 `foot._climb || foot._roll || foot._slide || foot._dive`).</summary>
            public bool Suppressed;
            public CharacterRig Rig;
        }

        /// <summary>Optional — City's structural classification (facade/window/
        /// terrain/road). Null falls through to material-name heuristics, exactly
        /// as src/ballistics.js's classifySurface does with no `world`.</summary>
        public IBallisticsWorld World;

        /// <summary>Ground-height sampler for the terrain-class fallback
        /// (footsteps.js:76 groundClassAt) — set once at scene wiring time.</summary>
        public TerrainSampler Terrain;

        /// <summary>Colliders a step ray may land on. Defaults to everything —
        /// narrow this once a dedicated "walkable" layer exists.</summary>
        public LayerMask GroundMask = ~0;

        /// <summary>footsteps.js:66 boreFloorAt — true inside a flyable tunnel
        /// bore, which the JS always treats as concrete. Optional.</summary>
        public System.Func<float, float, float, float?> BoreFloorAt;

        /// <summary>A probe sets this to a list and gets every step appended —
        /// footsteps.js:48 `this.log`.</summary>
        public List<(float t, string side, string gait, string surface, string role)> Log;

        readonly Dictionary<string, string> _footState = new() { { "L", "up" }, { "R", "up" } };
        CharacterRig _rigFor;
        (Transform L, Transform R)? _feet;
        float _phase;
        string _alt = "L";
        string _surface = "concrete";
        float _surfT;

        string Gait(in Query foot)
        {
            float s = Mathf.Abs(foot.Speed);
            if (foot.Crouched) return "crouch";
            if (foot.Stroll || s < 2.6f) return "walk";
            return s > foot.RunSpeed * 1.25f ? "sprint" : "run";
        }

        /// <summary>footsteps.js:58-78 _surfaceAt — one ray straight down, classified
        /// the way a bullet strike is; falls back to the terrain class grid where
        /// nothing built is under the hero.</summary>
        string SurfaceAt(in Query foot)
        {
            Vector3 p = foot.Pos;
            if (BoreFloorAt != null && BoreFloorAt(p.x, p.z, p.y).HasValue) return "concrete";

            Vector3 origin = p + new Vector3(0f, 1.2f, 0f);
            var hits = Physics.RaycastAll(origin, Vector3.down, 2.5f, GroundMask);
            if (hits.Length > 1) System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                if (hit.point.y > p.y + 0.35f) continue; // footsteps.js:73 — skip anything above the hero's own feet
                return SurfaceClassifier.Classify(hit, World, hit.point);
            }
            int cl = Terrain != null ? Terrain.ClassAt(p.x, p.z) : 0;
            return cl == 2 ? "water" : cl == 1 ? "grass" : "soil";
        }

        void Reset()
        {
            _footState["L"] = "up";
            _footState["R"] = "up";
            _phase = 0.6f;
        }

        /// <summary>footsteps.js:85-120 update. Call once a frame (any update
        /// phase is fine — this does not write bones, only reads them and fires
        /// audio).</summary>
        public void Update(float dt, in Query foot)
        {
            var rig = foot.Rig;
            if (rig == null) return;
            float speed = Mathf.Abs(foot.Speed);
            if (!foot.Grounded || foot.Suppressed || speed < MinSpeed)
            {
                Reset();
                return;
            }
            string gait = Gait(foot);
            _surfT -= dt;
            if (_surfT <= 0f) { _surface = SurfaceAt(foot); _surfT = SurfRecheck; }
            if (!ReferenceEquals(_rigFor, rig))
            {
                _feet = rig.LegPair();
                _rigFor = rig;
                Reset();
            }
            if (_feet.HasValue)
            {
                StepFoot("L", _feet.Value.L, foot, gait);
                StepFoot("R", _feet.Value.R, foot, gait);
            }
            else
            {
                // no feet to read: a cadence from the speed, alternating — footsteps.js:111-119
                float rate = Cadence.TryGetValue(gait, out var c) ? c : 2.0f;
                float denom = gait == "walk" ? 1.9f : Mathf.Max(0.01f, foot.RunSpeed);
                _phase += dt * rate * Mathf.Clamp(speed / denom, 0.6f, 1.3f);
                if (_phase >= 1f)
                {
                    _phase -= 1f;
                    _alt = _alt == "L" ? "R" : "L";
                    Step(foot, gait, _alt);
                }
            }
        }

        void StepFoot(string side, Transform bone, in Query foot, string gait)
        {
            if (bone == null) return;
            float h = bone.position.y - foot.Pos.y;
            string st = _footState[side];
            if (st == "up" && h < ContactH) { _footState[side] = "down"; Step(foot, gait, side); }
            else if (st == "down" && h > LiftH) { _footState[side] = "up"; }
        }

        void Step(in Query foot, string gait, string side)
        {
            string role = Role.TryGetValue(_surface, out var r) ? r : "step_concrete";
            Log?.Add((Time.time, side, gait, _surface, role));
            FxAudio.Footstep(role, _surface, gait, SoundVolume);
        }

        /// <summary>Set by the host once from its own volume setting — footsteps.js
        /// reads `this.game.sound?.volume ?? 0.7`.</summary>
        public float SoundVolume = 0.7f;

        /// <summary>footsteps.js:130-138 land — both feet at once, off a jump or a
        /// drop, harder the further it was.</summary>
        public void Land(in Query foot, float drop = 0f)
        {
            _surface = SurfaceAt(foot);
            _surfT = SurfRecheck;
            string role = Role.TryGetValue(_surface, out var r) ? r : "step_concrete";
            Log?.Add((Time.time, "both", "land", _surface, role));
            FxAudio.Landing(role, _surface, Mathf.Min(1f, 0.45f + drop * 0.25f), SoundVolume);
            Reset();
            _footState["L"] = "down";
            _footState["R"] = "down";
        }
    }
}

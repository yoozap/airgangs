using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Combat;

namespace TbilisiFly.Fx
{
    /// <summary>
    /// Port of src/rockets.js's RocketTrail (rockets.js:25-93) — the dense
    /// white-grey propellant trail. Its own ring buffer, kept SEPARATE from
    /// FireSystem's smoke on purpose, exactly as the JS comment says: "so
    /// wing-damage smoke and rocket exhaust never fight over particles."
    /// Renders through the shared default mode of Shaders/ImpactSprite.shader
    /// (cream-to-grey life lerp, rockets.js:56).
    ///
    /// Combat/Rockets.cs's RocketLauncher (read-only here, never edited —
    /// Combat/*.cs is out of this port's scope) deliberately does NOT spawn
    /// trail particles itself: its own comment at rockets.js:250-254's port
    /// site reads "VFX hook: trail.spawn every ~0.016s" and is left
    /// unimplemented for exactly this file to pick up. Since RocketLauncher
    /// exposes no spawn event, this polls its public `Live` list every frame
    /// and keeps its OWN per-rocket cadence accumulator keyed by object
    /// identity — cosmetic VFX timing only, never touching the rocket's own
    /// simulated position/velocity (no re-simulation, per this port's brief).
    /// </summary>
    public sealed class RocketTrailFx
    {
        const int TrailMax = 360;      // src/rockets.js:22
        const float TrailLife = 2.4f;  // src/rockets.js:23
        const float PuffInterval = 0.016f; // src/rockets.js:251

        readonly Vector3[] _pos = new Vector3[TrailMax];
        readonly float[] _life = new float[TrailMax];
        int _cursor;

        readonly Dictionary<RocketLauncher.Rocket, float> _puffAcc = new();

        public Material Mat;
        readonly Matrix4x4[] _mScratch = new Matrix4x4[TrailMax];
        readonly Vector4[] _tintScratch = new Vector4[TrailMax];

        public System.Func<float> Rng = () => UnityEngine.Random.value;

        public RocketTrailFx()
        {
            for (int i = 0; i < TrailMax; i++) _life[i] = 1f;
            var sh = Shader.Find("TbilisiFly/ImpactSprite");
            if (sh == null) return; // shader asset not imported yet in this build — Mat stays null, Render() no-ops
            Mat = new Material(sh) { enableInstancing = true };
            Mat.SetColor("_ColorA", new Color(0.92f, 0.9f, 0.86f));
            Mat.SetColor("_ColorB", new Color(0.55f, 0.55f, 0.58f));
            Mat.EnableKeyword("_TINTMIX");
            Mat.SetFloat("_EdgeLo", 0.5f);
            Mat.SetFloat("_EdgeHi", 0.1f);
            Mat.SetFloat("_AlphaMul", 0.5f); // src/rockets.js:54
            Mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        }

        // src/rockets.js:64-71
        public void Spawn(Vector3 pos)
        {
            int i = _cursor; _cursor = (_cursor + 1) % TrailMax;
            _pos[i] = pos + new Vector3((Rng() - 0.5f) * 0.3f, (Rng() - 0.5f) * 0.3f, (Rng() - 0.5f) * 0.3f);
            _life[i] = 0.001f;
        }

        /// <summary>Feed this every frame with the launcher's live rockets — spawns
        /// trail puffs at the JS's own ~0.016s cadence, reading positions only.</summary>
        public void FeedRockets(IReadOnlyList<RocketLauncher.Rocket> live, float dt)
        {
            foreach (var rk in live)
            {
                float acc = _puffAcc.TryGetValue(rk, out var a) ? a : 0f;
                acc += dt;
                if (acc > PuffInterval)
                {
                    acc = 0f;
                    Spawn(rk.Pos - rk.Dir * 1.0f); // src/rockets.js:253
                }
                _puffAcc[rk] = acc;
            }
            if (_puffAcc.Count > 0 && live.Count < _puffAcc.Count)
            {
                // drop accumulators for rockets that detonated/expired this frame
                var dead = new List<RocketLauncher.Rocket>();
                foreach (var k in _puffAcc.Keys)
                {
                    bool found = false;
                    foreach (var rk in live) if (rk == k) { found = true; break; }
                    if (!found) dead.Add(k);
                }
                foreach (var k in dead) _puffAcc.Remove(k);
            }
        }

        public void Update(float dt)
        {
            for (int i = 0; i < TrailMax; i++)
            {
                if (_life[i] >= 1f) continue;
                _life[i] = Mathf.Min(1f, _life[i] + dt / TrailLife);
                _pos[i].y += 1.2f * dt; // src/rockets.js:79 — exhaust drifts up as it cools
            }
            Render();
        }

        void Render()
        {
            int n = 0;
            for (int i = 0; i < TrailMax; i++)
            {
                if (_life[i] >= 1f) continue;
                // src/rockets.js:47's `1.5 + life*20.0` growth shape rebased onto a
                // 0.5 m judgement-call size (no metres given in the GL_POINTS source).
                float size = 0.5f * (1.5f + _life[i] * 20.0f) / 1.5f;
                _mScratch[n] = Matrix4x4.TRS(_pos[i], Quaternion.identity, new Vector3(size, size, 1));
                _tintScratch[n] = new Vector4(0, 0, 0, _life[i]);
                n++;
            }
            InstancedSpriteBatch.Draw(InstancedSpriteBatch.UnitQuad, Mat, _mScratch, _tintScratch, null, n);
        }

        public void Dispose()
        {
            if (Mat != null) UnityEngine.Object.Destroy(Mat);
        }
    }
}

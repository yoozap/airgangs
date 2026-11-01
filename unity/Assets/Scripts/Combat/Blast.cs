using System;
using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Fx;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// Hooks into the smoke/ember/dust-wave system (src/smoke.js, src/fire.js —
    /// "core" scope per docs/UNITY_PORT.md's reimplement list, VFX Graph
    /// territory, not Combat). BlastFX below calls these at the exact counts,
    /// positions and timings src/blast.js's explode() does; only the particles
    /// themselves are someone else's system.
    /// </summary>
    public interface IFireFx
    {
        void Wave(float x, float y, float z, float radius);
        void SpawnEmber(float x, float y, float z);
        void SpawnSmoke(float x, float y, float z, float size, float dark, float dur,
            float vx, float vy, float vz, float spread);
        void Plume(float x, float y, float z, float radius, float strength);

        /// <summary>src/fire.js:518 `stampScorch(x, groundY, z, size)` — the
        /// PERMANENT dark burn mark on the ground under a collapsed building
        /// (citymap.js:2514 `fx.stampScorch(rec.cx, gy, rec.cz, span * 2.1)`).
        /// Fx/FireSystem.cs has carried the 220-slot ring of decals and its draw
        /// since it was written; this interface simply had no way to ask for one,
        /// so the collapse's own scorch was the last unpublished piece of the
        /// ground slam.</summary>
        void StampScorch(float x, float groundY, float z, float size);

        /// <summary>src/fire.js:434 `setSmolder(bi, anchors)` — the smouldering
        /// smoke columns over a rubble mound (citymap.js:2517
        /// `fx.setSmolder(rec.bi, mound)`). Keyed by the building's ORIGINAL
        /// index, as the JS keys it by `rec.bi`; an empty/null anchor array
        /// clears it, same as the JS's `else this.smolder.delete(bi)`. Nothing
        /// expires: the mound smokes for as long as the registry holds it (cap
        /// 18, oldest evicted), which is why City/DebrisField.MoundAnchors was
        /// computed and left unconsumed until now.</summary>
        void SetSmolder(int key, Vector3[] anchors);

        /// <summary>src/fire.js:415 `setBurning(bi, anchors, intensity, scale)` —
        /// citymap.js:2519 `fx.setBurning(`${rec.bi}:fire`, mound, 1, 2.4)`: the
        /// wreck BURNS, with 2.4x flame quads over the mound, not a polite wisp.
        /// A SEPARATE key from the smolder one (the JS's `${bi}:fire` string),
        /// because both emitters are live over the same anchors at once.</summary>
        void SetBurning(int key, Vector3[] anchors, float intensity, float scale);

        /// <summary>src/fire.js:429 `clearBurning(bi)` — citymap.js:2508, run
        /// first on collapse completion so the window fires that were burning up
        /// the standing facade stop before the mound's own fire replaces
        /// them.</summary>
        void ClearBurning(int key);
    }

    /// <summary>
    /// Port of src/blast.js's BlastFX — the composed rocket-blast event (fireball
    /// pool, flash lights, pressure rings, crater decals) and its lifecycle
    /// timing. The volumetric noise-shader fireball, the painted crater texture
    /// and the point lights' actual rendering are Shader Graph / URP light
    /// territory (docs/UNITY_PORT.md: "the math ports; the shaders do not") —
    /// what ports here is every curve that drives them: how fast a fireball
    /// inflates and cools, how a flash decays, how a ring expands and fades,
    /// and the exact composition of one `Explode()` call (radii, counts,
    /// delays) so the GAMEPLAY feel (how big, how long, how many secondaries)
    /// survives even before the art is wired in.
    /// </summary>
    public sealed class BlastFx
    {
        public const int FbMax = 7;
        public const int RingMax = 6;
        public const int CraterMax = 300;

        public sealed class Fireball
        {
            public Vector3 Pos;
            public float Scale;
            public float T, Dur, R, Rise;
            public bool Live;
        }

        public sealed class FlashLight
        {
            public Vector3 Pos;
            public float Intensity;
            public float Distance;
            public float T, Dur, Peak;
        }

        public sealed class Ring
        {
            public Vector3 Pos;
            public Quaternion Rot;
            public float Scale;
            public float Opacity;
            public float T, Dur, R;
            public bool Live;
        }

        public readonly Fireball[] Balls = new Fireball[FbMax];
        public readonly FlashLight[] Lights = new FlashLight[2];
        public readonly Ring[] Rings = new Ring[RingMax];
        public readonly DecalStamp[] Craters = new DecalStamp[CraterMax];
        public int CraterCount { get; private set; }
        int _bCursor, _lCursor, _rCursor, _cCursor;
        float _t;
        readonly List<(float t, Action fn)> _pending = new List<(float, Action)>();

        public IFireFx Fx;
        /// <summary>src/blast.js:151 heightAt(x,z) -> groundY | null.</summary>
        public Func<float, float, float?> HeightAt;
        public Func<float> Rng = () => UnityEngine.Random.value;

        /// <summary>Owned renderer for the fireball pool / flash lights / rings /
        /// crater decals this class drives. Constructed here so BlastFx is
        /// self-sufficient — it already ticks every frame via Core/
        /// CombatWorldAdapter.cs's `DelegateTickable(dt =&gt; blast.Update(dt))`, so
        /// no Core/* edit is needed to draw. WIRING: Fx.BlastRenderer previously
        /// had zero callers project-wide (grep confirmed).</summary>
        readonly BlastRenderer _renderer;

        public BlastFx()
        {
            for (int i = 0; i < FbMax; i++) Balls[i] = new Fireball { Dur = 1f, R = 1f, Rise = 3f };
            for (int i = 0; i < Lights.Length; i++) Lights[i] = new FlashLight();
            for (int i = 0; i < RingMax; i++) Rings[i] = new Ring { Dur = 1f, R = 1f };
            _renderer = new BlastRenderer(this);
        }

        /// <summary>Releases the owned renderer's Materials/Meshes/pooled Lights.
        /// Call when this blast system itself is torn down (matches
        /// Fx.BlastRenderer's own Dispose).</summary>
        public void Dispose() => _renderer.Dispose();

        // ---- primitives, src/blast.js:230-287 -----------------------------------

        // Named SpawnFireball (not Fireball) — a method can't share a name with
        // the nested Fireball class above (CS0102). src/blast.js:232 this.fireball(...).
        public Fireball SpawnFireball(float x, float y, float z, float r, float dur = 1.15f, float rise = 3.5f)
        {
            var b = Balls[_bCursor];
            _bCursor = (_bCursor + 1) % FbMax;
            b.Pos = new Vector3(x, y, z);
            b.Scale = r * 0.35f;
            b.T = 0f; b.Dur = dur; b.R = r; b.Rise = rise; b.Live = true;
            return b;
        }

        public FlashLight Flash(float x, float y, float z, float power = 1f, float dur = 0.5f)
        {
            var e = Lights[_lCursor];
            _lCursor = (_lCursor + 1) % Lights.Length;
            e.Pos = new Vector3(x, y, z);
            e.Peak = 5200f * power;
            e.Intensity = e.Peak;
            e.Distance = 160f + 220f * power;
            e.T = 0f; e.Dur = dur;
            return e;
        }

        public Ring MakeRing(float x, float y, float z, float nx, float ny, float nz, float r, float dur = 0.45f)
        {
            var ring = Rings[_rCursor];
            _rCursor = (_rCursor + 1) % RingMax;
            ring.Pos = new Vector3(x + nx * 0.5f, y + ny * 0.5f, z + nz * 0.5f);
            ring.Rot = Quaternion.LookRotation(new Vector3(nx, ny, nz).normalized, Vector3.up);
            ring.Scale = r * 0.2f;
            ring.Opacity = 0.4f;
            ring.T = 0f; ring.Dur = dur; ring.R = r; ring.Live = true;
            return ring;
        }

        // src/blast.js:274 crater — its own small ring buffer (CRATER_MAX=300), separate
        // from ImpactFx's DecalLayer because the JS keeps a dedicated InstancedMesh for it.
        public DecalStamp Crater(float x, float y, float z, float nx, float ny, float nz, float size)
        {
            Vector3 n = new Vector3(nx, ny, nz).normalized;
            Quaternion spin = Quaternion.AngleAxis(Rng() * 360f, Vector3.forward);
            Quaternion rot = Quaternion.LookRotation(n, Vector3.up) * spin;
            var stamp = new DecalStamp
            {
                Position = new Vector3(x + nx * 0.09f, y + ny * 0.09f, z + nz * 0.09f),
                Rotation = rot, Scale = new Vector3(size, size, 1f), Live = true,
            };
            int i = _cCursor;
            _cCursor = (_cCursor + 1) % CraterMax;
            Craters[i] = stamp;
            CraterCount = Mathf.Max(CraterCount, i + 1);
            return stamp;
        }

        // ---- the composed blast, src/blast.js:293 explode() ---------------------

        /// <summary>point: world hit point. normal: surface normal (null = air burst).
        /// r: blast radius in metres.</summary>
        public void Explode(Vector3 point, Vector3? normal, float r = 8f, bool ground = false, bool marks = true)
        {
            Vector3? n = normal.HasValue ? normal.Value.normalized : (Vector3?)null;
            float ox = n.HasValue ? n.Value.x * r * 0.3f : 0f;
            float oy = n.HasValue ? n.Value.y * r * 0.3f : 0f;
            float oz = n.HasValue ? n.Value.z * r * 0.3f : 0f;

            SpawnFireball(point.x + ox, point.y + oy + r * 0.15f, point.z + oz, r, dur: 1.25f, rise: 3.5f);
            Flash(point.x + ox, point.y + oy + r * 0.3f, point.z + oz, Mathf.Min(2.2f, r / 6f), 0.55f);

            // secondaries: the burning gas rolls out along the wall and up the face
            for (int k = 0; k < 3; k++)
            {
                float a = Rng() * Mathf.PI * 2f;
                float spread = r * (0.5f + Rng() * 0.6f);
                float delay = 0.06f + k * 0.09f;
                float fx2 = point.x + ox + Mathf.Cos(a) * spread * (n.HasValue ? 1f - Mathf.Abs(n.Value.x) * 0.4f : 1f);
                float fy2 = point.y + oy + r * (0.25f + Rng() * 0.7f);
                float fz2 = point.z + oz + Mathf.Sin(a) * spread * (n.HasValue ? 1f - Mathf.Abs(n.Value.z) * 0.4f : 1f);
                float fr2 = r * (0.45f + Rng() * 0.35f);
                float fdur = 1f + Rng() * 0.5f;
                After(delay, () => SpawnFireball(fx2, fy2, fz2, fr2, dur: fdur, rise: 4.5f));
            }

            if (n.HasValue) MakeRing(point.x, point.y, point.z, n.Value.x, n.Value.y, n.Value.z, r * 1.7f, 0.32f);

            float? gy = HeightAt?.Invoke(point.x, point.z);
            if (gy.HasValue && (ground || point.y - gy.Value < 7f)) Fx?.Wave(point.x, gy.Value, point.z, r * 0.9f);

            for (int k = 0; k < 26; k++) Fx?.SpawnEmber(point.x + ox, point.y + oy + Rng() * r * 0.6f, point.z + oz);

            for (int k = 0; k < 16; k++)
            {
                Fx?.SpawnSmoke(point.x + ox, point.y + oy + Rng() * r * 0.6f, point.z + oz,
                    r * (0.8f + Rng() * 0.7f), 0.9f, 4.5f, 0f, 5f + Rng() * 6f, 0f, r * 0.5f);
            }

            if (n.HasValue)
            {
                Vector3 nv = n.Value;
                // Tangent in the horizontal plane. This is NOT a `.crossVectors()` call in
                // the JS (it's the hand-expanded formula (-n.z, 0, n.x)) — the formula is
                // the SAME in both engines' component math, and since it only has to be
                // SOME horizontal vector perpendicular to n for a radially symmetric dust
                // spray, which specific perpendicular comes out does not change the result.
                // No operand swap applies here because there is no cross() call to swap.
                Vector3 t1 = new Vector3(-nv.z, 0f, nv.x).normalized;
                // HANDEDNESS: this IS a real cross() in the JS (`crossVectors(n, t1)`), so
                // the operands are SWAPPED for Unity's left-handed Cross: t2 = Cross(t1, n).
                Vector3 t2 = Vector3.Cross(t1, nv).normalized;
                for (int k = 0; k < 20; k++)
                {
                    float a = Rng() * Mathf.PI * 2f;
                    float sp = 7f + Rng() * 13f;
                    float dx = t1.x * Mathf.Cos(a) + t2.x * Mathf.Sin(a);
                    float dy = t1.y * Mathf.Cos(a) + t2.y * Mathf.Sin(a);
                    float dz = t1.z * Mathf.Cos(a) + t2.z * Mathf.Sin(a);
                    Fx?.SpawnSmoke(point.x + nv.x * 0.6f, point.y + nv.y * 0.6f, point.z + nv.z * 0.6f,
                        7f + Rng() * 9f, 0.35f + Rng() * 0.25f, 3.6f,
                        dx * sp + nv.x * 3f, dy * sp + 2.5f, dz * sp + nv.z * 3f, 1.5f);
                }
                if (marks) Crater(point.x, point.y, point.z, nv.x, nv.y, nv.z, r * 0.75f);
            }

            After(0.45f, () =>
            {
                for (int k = 0; k < 12; k++)
                {
                    Fx?.SpawnSmoke(point.x + ox, point.y + oy + r * (0.4f + Rng()), point.z + oz,
                        r * (1.1f + Rng() * 0.8f), 0.93f, 6.5f, 0f, 3f + Rng() * 3f, 0f, r * 0.7f);
                }
            });
        }

        void After(float delay, Action fn) => _pending.Add((delay, fn));

        public void Update(float dt)
        {
            _t += dt;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var (t, fn) = _pending[i];
                t -= dt;
                if (t <= 0f) { _pending.RemoveAt(i); fn(); }
                else _pending[i] = (t, fn);
            }

            foreach (var b in Balls)
            {
                if (!b.Live) continue;
                b.T += dt;
                float p = Mathf.Min(1f, b.T / b.Dur);
                b.Scale = b.R * (0.35f + 0.85f * Mathf.Pow(p, 0.45f));
                b.Pos.y += b.Rise * dt * (0.4f + p);
                if (p >= 1f) b.Live = false;
            }

            foreach (var e in Lights)
            {
                if (e.Intensity <= 0f) continue;
                e.T += dt;
                float p = Mathf.Min(1f, e.T / e.Dur);
                float f = (1f - p) * (1f - p) * (0.75f + 0.25f * Mathf.Sin(_t * 47f));
                e.Intensity = e.Peak * Mathf.Max(0f, f);
                if (p >= 1f) e.Intensity = 0f;
            }

            foreach (var r in Rings)
            {
                if (!r.Live) continue;
                r.T += dt;
                float p = Mathf.Min(1f, r.T / r.Dur);
                r.Scale = r.R * (0.2f + 1.1f * Mathf.Pow(p, 0.6f));
                r.Opacity = 0.4f * (1f - p) * (1f - p);
                if (p >= 1f) r.Live = false;
            }

            _renderer.Render(); // Fx.BlastRenderer's own doc comment: "call once per frame AFTER BlastFx.Update(dt) has run"
        }
    }
}

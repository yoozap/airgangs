using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.Combat
{
    // =========================================================================
    // BREAKING GLASS — port of src/glass.js (354 lines)
    // =========================================================================
    // A window dies in four beats: the pane instance is zeroed out of its
    // facade layer (City's concern, not this file's), a cloud of REAL tumbling
    // shards sprays out of the hole, a blown-out pane decal is stamped where the
    // glass used to be (a black hole with glass teeth, not a bare wall), and the
    // shatter is heard with a tail of shards raining on the pavement.
    //
    // VFX GRAPH WHERE IT FITS: the shard/glint SIMULATION (ballistic flight,
    // skid-then-rest bounce, fade-out) is pure data here — exactly the same
    // convention Combat/ImpactFx.cs's ParticleCloud already established for
    // debris/sparks ("Rendering ... is a Unity VFX concern left to whoever
    // wires the art"). Shards are irregular 4-gons lit by the scene HDR in the
    // JS (a THREE.MeshStandardMaterial catching a different reflection every
    // spin is exactly what sells falling glass) — reproducing that is a Shader
    // Graph / VFX Graph job (GPU-simulated or instanced from this class's
    // `Shards` array via Graphics.DrawMeshInstanced), not a shader this file
    // needs to hand-write: there is no discard/clip trick here, unlike
    // PlaneHull.shader — shards are just lit, mildly transparent glass. The
    // one HLSL file this scope's "custom shader" budget goes to for glass is
    // Assets/Shaders/GlassGlint.shader, the tiny additive point-sprite for the
    // glint burst (src/glass.js:124-151's inline ShaderMaterial), because unlike
    // the shard material it cannot be assembled from a standard URP Lit/
    // Unlit asset — it needs the exact distance-based falloff math.
    //
    // The broken-pane HOLE texture (makeBrokenPaneTexture, canvas-drawn) is
    // deferred the same way PlaneDamage.cs defers its own decal art (see that
    // file's class header) — same established precedent, not a separate call.
    //
    // HANDEDNESS: every position/velocity here lives in Unity WORLD space from
    // the moment it is created (the caller hands over an already-correct world
    // point + facade normal) and nothing in this file reads local/object space
    // or builds a tangent frame via a cross product — src/glass.js itself has
    // no cross products either, so there is nothing to swap operands on.
    // =========================================================================
    public sealed class BreakingGlass
    {
        public const int ShardMax = 900; // :13
        public const int SparkMax = 900; // :14
        public const int PaneMax = 700;  // :15
        public const float Gravity = 19f; // :16 — glass falls hard, reads as heavy

        public sealed class Shard
        {
            public float X, Y, Z;
            public float Vx, Vy, Vz;
            public float Rx, Ry, Rz;     // euler, radians
            public float Wx, Wy, Wz;     // spin rate, radians/s
            public float S;              // scale
            public float T, Dur;
            public bool Rest;
        }

        readonly Shard[] _slots = new Shard[ShardMax];
        int _sCursor;
        int _live;

        // glints — flat arrays, same layout convention as Combat/ImpactFx.cs's ParticleCloud
        public readonly Vector3[] KPos = new Vector3[SparkMax];
        public readonly Vector3[] KVel = new Vector3[SparkMax];
        public readonly float[] KLife = new float[SparkMax];
        int _kCursor;

        public struct PaneDecal { public Vector3 Position; public float YawRad; public float W, H; public bool Live; }
        public readonly PaneDecal[] Panes = new PaneDecal[PaneMax];
        int _pCursor;

        /// <summary>src/glass.js:88 heightAt — ground height at (x,z), or null over nothing.
        /// Shards land on the real terrain.</summary>
        public Func<float, float, float?> HeightAt;
        public float Volume = 0.7f; // kept in sync with the game's master volume
        public Vector3 Listener;    // camera position, for hit distance
        public Func<float> Rng = () => UnityEngine.Random.value;

        /// <summary>src/audiofx.js glassBreak(dist, vol, big) — audio is out of this port's
        /// scope; wired the same event-hook convention as Combat/Guns.cs's OnHitToast.</summary>
        public event Action<float, float, bool> OnGlassBreakSound;

        float _sndT = -1e9f;

        public int ShardCount => _slots.Length;
        public Shard ShardAt(int i) => _slots[i]; // null if the slot is free — for a VFX binder

        /// <summary>src/glass.js:182 shatter — a pane bursts: shards spray along the facade
        /// normal, glints flash. `power` scales speed and count (rocket blast vs one round).</summary>
        public void Shatter(float x, float y, float z, float nx, float nz, int count = 14, float power = 1f)
        {
            for (int k = 0; k < count; k++)
            {
                int i = _sCursor;
                _sCursor = (_sCursor + 1) % ShardMax;
                if (_slots[i] == null) _live++;
                float outSpeed = (1.5f + Rng() * 5f) * power;
                _slots[i] = new Shard
                {
                    X = x + (Rng() - 0.5f) * 1.4f, Y = y + (Rng() - 0.5f) * 1.6f, Z = z + (Rng() - 0.5f) * 1.4f,
                    Vx = nx * outSpeed + (Rng() - 0.5f) * 3.5f * power,
                    Vy = (0.5f + Rng() * 3.5f) * power,
                    Vz = nz * outSpeed + (Rng() - 0.5f) * 3.5f * power,
                    Rx = Rng() * 6.283f, Ry = Rng() * 6.283f, Rz = Rng() * 6.283f,
                    Wx = (Rng() - 0.5f) * 14f, Wy = (Rng() - 0.5f) * 14f, Wz = (Rng() - 0.5f) * 14f,
                    S = (0.22f + Rng() * 0.42f) * (0.8f + power * 0.3f),
                    T = 0f, Dur = 3.2f + Rng() * 2.2f, Rest = false,
                };
            }
            int glints = Mathf.Min(26, 6 + count);
            for (int k = 0; k < glints; k++)
            {
                int i = _kCursor;
                _kCursor = (_kCursor + 1) % SparkMax;
                KPos[i] = new Vector3(x + (Rng() - 0.5f) * 1.6f, y + (Rng() - 0.5f) * 1.8f, z + (Rng() - 0.5f) * 1.6f);
                KLife[i] = 0.001f;
                KVel[i] = new Vector3(
                    nx * (2f + Rng() * 7f) * power + (Rng() - 0.5f) * 4f,
                    1f + Rng() * 4f,
                    nz * (2f + Rng() * 7f) * power + (Rng() - 0.5f) * 4f);
            }
        }

        /// <summary>src/glass.js:223 stampPane — the black hole the glass leaves in the facade.</summary>
        public void StampPane(float x, float y, float z, float nx, float nz, float w = 1.7f, float h = 1.9f)
        {
            int i = _pCursor;
            _pCursor = (_pCursor + 1) % PaneMax;
            float yaw = Mathf.Atan2(nx, nz);
            Panes[i] = new PaneDecal
            {
                Position = new Vector3(x + nx * 0.07f, y, z + nz * 0.07f),
                YawRad = yaw,
                W = w * (0.9f + Rng() * 0.2f), H = h * (0.9f + Rng() * 0.2f),
                Live = true,
            };
        }

        /// <summary>src/glass.js:239 burstPane — everything a dead window does at once.</summary>
        public void BurstPane(float x, float y, float z, float nx, float nz, float power = 1f)
        {
            Shatter(x, y, z, nx, nz, power > 1f ? 12 : 8, power);
            StampPane(x, y, z, nx, nz);
        }

        /// <summary>src/glass.js:246 burstList — blow a whole list of windows; `list` is a
        /// flat [x,y,z,nx,nz,...] array in distance order (nearest first), matching
        /// Combat/CombatWorldHooks.cs's IDamageWorld.BlastWindows return convention.</summary>
        public int BurstList(IReadOnlyList<float> list, float power = 1.4f, int max = 46)
        {
            int total = list.Count / 5;
            int n = Mathf.Min(total, max);
            float now = Time.realtimeSinceStartup;
            if (now - _sndT > 0.09f)
            {
                _sndT = now;
                float d = Vector3.Distance(Listener, new Vector3(list[0], list[1], list[2]));
                OnGlassBreakSound?.Invoke(d, Volume, total > 60);
            }
            for (int i = 0; i < n; i++)
            {
                int o = i * 5;
                BurstPane(list[o], list[o + 1], list[o + 2], list[o + 3], list[o + 4], power);
            }
            // panes past the shard budget still lose their glass — decal only
            for (int i = n; i < total; i++)
            {
                int o = i * 5;
                StampPane(list[o], list[o + 1], list[o + 2], list[o + 3], list[o + 4]);
            }
            return n;
        }

        /// <summary>src/glass.js:269 update.</summary>
        public void Update(float dt)
        {
            if (_live > 0)
            {
                int live = 0;
                for (int i = 0; i < ShardMax; i++)
                {
                    var s = _slots[i];
                    if (s == null) continue;
                    s.T += dt;
                    if (s.T >= s.Dur) { _slots[i] = null; continue; }
                    live++;
                    if (!s.Rest)
                    {
                        s.Vy -= Gravity * dt;
                        s.X += s.Vx * dt; s.Y += s.Vy * dt; s.Z += s.Vz * dt;
                        s.Rx += s.Wx * dt; s.Ry += s.Wy * dt; s.Rz += s.Wz * dt;
                        float? gy = HeightAt?.Invoke(s.X, s.Z);
                        if (gy.HasValue && s.Y <= gy.Value + 0.05f && s.Vy < 0f)
                        {
                            if (Mathf.Abs(s.Vy) > 4f)
                            {
                                s.Y = gy.Value + 0.05f;
                                s.Vy = -s.Vy * 0.25f; s.Vx *= 0.55f; s.Vz *= 0.55f; s.Wx *= 0.4f; s.Wz *= 0.4f;
                            }
                            else
                            {
                                s.Y = gy.Value + 0.04f;
                                s.Rest = true;
                                s.Rx = -Mathf.PI / 2f; s.Rz = 0f;
                                s.Dur = Mathf.Min(s.Dur, s.T + 2.4f); // pavement glass fades out
                            }
                        }
                    }
                }
                _live = live;
            }

            bool alive = false;
            for (int i = 0; i < SparkMax; i++)
            {
                if (KLife[i] >= 1f) continue;
                alive = true;
                KLife[i] = Mathf.Min(1f, KLife[i] + dt / 0.55f);
                Vector3 v = KVel[i];
                v.y -= Gravity * dt;
                KVel[i] = v;
                KPos[i] += v * dt;
            }
            _ = alive; // the JS only uses this to gate a needsUpdate flag — no Unity analogue needed
        }

        public void Dispose() { }
    }
}

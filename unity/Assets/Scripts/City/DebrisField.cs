using System;
using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Concrete debris and rubble: ballistic chunks that fly, tumble, bounce once
    /// and settle — src/citymap.js:2527-2763 (_launchDebris, blastDebris,
    /// _updateDebris, _moundAnchors, _spawnRubble, _shedDebris).
    ///
    /// PURE DATA/MATH CLASS. The JS instances pieces into six global
    /// InstancedMesh "layers" cut from tools/build_rubble_kit.py's 6-chunk GLB
    /// (citymap.js:2675-2719, _initRubble) and writes one instance matrix per
    /// particle per frame. None of that — the GLB load, the InstancedMesh pool,
    /// the per-frame matrix upload, the Unity-native BatchRendererGroup
    /// equivalent (which the project's "no GameObject per building" rule points
    /// at anyway, per docs/UNITY_PORT.md) — is this file's job. This file is the
    /// PHYSICS: where a piece starts, how it falls, when it settles, and where a
    /// just-collapsed building's permanent rubble mound gets scattered. A caller
    /// with the real instanced layers turns each <see cref="Particle"/> /
    /// <see cref="ScatterPiece"/> into a TRS matrix (see the JS's own `m.compose`
    /// for the exact assembly: position, Euler(rx,0,rz) as rotation, uniform
    /// scale) and uploads it.
    ///
    /// Gravity here is -16 m/s², not -9.81 — citymap.js:2596's own comment:
    /// "heavier than real — video-game gravity reads better". Keep it; this was
    /// a tuned choice, not an oversight.
    ///
    /// HANDEDNESS: every position in and out of this file (building footprints,
    /// particle positions, scatter points) is in whatever frame the CALLER's
    /// building-damage records already use — which is UNITY space, since
    /// BuildingBuilder.cs converts footprints through GeoFrame.ToUnity before
    /// the chunk mesh (and therefore the damage records' cx/cz/min/max) ever
    /// exist. No GeoFrame call is needed here: every formula is translation/
    /// radius arithmetic with no handedness-sensitive cross product, so it is
    /// correct in either frame as long as the caller is consistent — but
    /// `heightAt` MUST be <see cref="TerrainSampler.HeightAt"/> (the
    /// mirrored-X overload), not HeightAtGame.
    /// </summary>
    public static class DebrisField
    {
        public const float Gravity = 16f;   // citymap.js:2596
        public const int MaxPerLayer = 4000; // citymap.js:2541 etc — the JS caps each InstancedMesh pool here

        public struct Particle
        {
            public Vector3 Pos;
            public Vector3 Vel;
            public float RotX, RotZ;   // citymap.js composes Euler(rx, 0, rz) — no yaw spin
            public float SpinX, SpinZ; // tumble rates, rad/s
            public float Scale;        // uniform (debris kit pieces aren't stretched)
            public bool Bounced;
            public float Hot;          // >0 while trailing smoke/embers; counts down, re-arms at 0.05s
        }

        /// <summary>Chunks blown off a collapsing building — citymap.js:2527-2560
        /// (_launchDebris). Call once per piece (n pieces per collapse event,
        /// citymap.js:2381: `8 + round(span/5)` on collapse start, `3` per
        /// secondary mid-fall blast, `6 + round(span/6)` on ground impact).</summary>
        public static Particle Launch(float cx, float cz, float fromY, float span, System.Random rnd)
        {
            float a = (float)rnd.NextDouble() * Mathf.PI * 2f;
            float rr = span * 0.3f * (float)rnd.NextDouble();
            float speed = 4f + (float)rnd.NextDouble() * 8f;
            return new Particle
            {
                Pos = new Vector3(cx + Mathf.Cos(a) * rr, fromY + ((float)rnd.NextDouble() - 0.3f) * 3f, cz + Mathf.Sin(a) * rr),
                Vel = new Vector3(Mathf.Cos(a) * speed, 2f + (float)rnd.NextDouble() * 6f, Mathf.Sin(a) * speed),
                RotX = (float)rnd.NextDouble() * Mathf.PI * 2f,
                RotZ = (float)rnd.NextDouble() * Mathf.PI * 2f,
                SpinX = ((float)rnd.NextDouble() - 0.5f) * 9f,
                SpinZ = ((float)rnd.NextDouble() - 0.5f) * 9f,
                Scale = 0.8f + (float)rnd.NextDouble() * 1.3f,
                Bounced = false,
                Hot = 0f,
            };
        }

        /// <summary>Chunks torn out of a wall along the blast cone, a third of
        /// them burning on the way down — citymap.js:2561-2591 (blastDebris).
        /// `dir` is the blast's outward direction (unit-ish; the JS does not
        /// normalize it either). `pieceIndex` is this piece's 0-based index
        /// within the burst (every third one ignites, `k % 3 === 0`).</summary>
        public static Particle LaunchBlast(Vector3 at, Vector3 dir, System.Random rnd, int pieceIndex)
        {
            float speed = 10f + (float)rnd.NextDouble() * 22f;
            float a = (float)rnd.NextDouble() * Mathf.PI * 2f;
            float spread = 0.55f + (float)rnd.NextDouble() * 0.8f;
            Vector3 jitter = new Vector3(
                ((float)rnd.NextDouble() - 0.5f) * 2f,
                ((float)rnd.NextDouble() - 0.5f) * 2f,
                ((float)rnd.NextDouble() - 0.5f) * 2f);
            return new Particle
            {
                Pos = at + jitter,
                Vel = new Vector3(
                    (dir.x + Mathf.Cos(a) * spread) * speed,
                    (dir.y + 0.35f + (float)rnd.NextDouble() * 0.6f) * speed * 0.55f,
                    (dir.z + Mathf.Sin(a) * spread) * speed),
                RotX = (float)rnd.NextDouble() * Mathf.PI * 2f,
                RotZ = (float)rnd.NextDouble() * Mathf.PI * 2f,
                SpinX = ((float)rnd.NextDouble() - 0.5f) * 16f,
                SpinZ = ((float)rnd.NextDouble() - 0.5f) * 16f,
                Scale = 0.7f + (float)rnd.NextDouble() * 1.6f,
                Bounced = false,
                Hot = pieceIndex % 3 == 0 ? 0.01f : 0f,
            };
        }

        /// <summary>
        /// One physics step. Returns true once the piece has come fully to rest
        /// (one bounce, then it skids and stops — citymap.js:2591-2654,
        /// _updateDebris). `reignited` is true exactly when the burning timer
        /// just rolled over and the caller should spawn a smoke puff (+ 50%
        /// chance of an ember) at the NEW position; `justBounced` is true the one
        /// frame the hard bounce happens (caller spawns a dust puff there too).
        /// Ground height comes from <paramref name="heightAt"/>, which must be in
        /// the SAME frame as <paramref name="p"/>.Pos.x/z — debris spawns off
        /// building damage records, which are chunk-mesh positions already in
        /// UNITY space per BuildingBuilder.cs, so pass
        /// <see cref="TerrainSampler.HeightAt"/> (the mirrored-X overload), not
        /// HeightAtGame. The +0.15 m ground clearance matches the JS's own
        /// `+ 0.15` (citymap.js:2622).
        /// </summary>
        public static bool Step(ref Particle p, float dt, Func<float, float, float> heightAt,
            out bool reignited, out bool justBounced)
        {
            reignited = false;
            justBounced = false;

            p.Vel.y -= Gravity * dt;
            p.Pos += p.Vel * dt;
            p.RotX += p.SpinX * dt;
            p.RotZ += p.SpinZ * dt;

            if (p.Hot > 0f)
            {
                p.Hot -= dt;
                if (p.Hot <= 0f) { p.Hot = 0.05f; reignited = true; }
            }

            float gy = heightAt(p.Pos.x, p.Pos.z) + 0.15f;
            bool settled = false;
            if (p.Pos.y <= gy && p.Vel.y < 0f)
            {
                p.Pos.y = gy;
                if (!p.Bounced && Mathf.Abs(p.Vel.y) > 3f)
                {
                    p.Bounced = true;
                    p.Vel.y = -p.Vel.y * 0.3f;
                    p.Vel.x *= 0.5f;
                    p.Vel.z *= 0.5f;
                    justBounced = true;
                }
                else
                {
                    settled = true;
                }
            }
            return settled;
        }

        /// <summary>Smolder/fire anchor points scattered over a collapsed
        /// building's footprint — citymap.js:2655-2674 (_moundAnchors). `bi` seeds
        /// both the angular offset and the per-point radius so the same building
        /// always gets the same mound shape.</summary>
        public static Vector3[] MoundAnchors(float minX, float maxX, float minZ, float maxZ,
            float baseY, float top, int bi)
        {
            float span = Mathf.Max(4f, Mathf.Max(maxX - minX, maxZ - minZ));
            float cx = (minX + maxX) * 0.5f, cz = (minZ + maxZ) * 0.5f;
            // JsMath.Round, NOT Mathf.RoundToInt: citymap.js:2658 uses
            // Math.round, which rounds a .5 tie UP, while Unity's rounds to
            // even. A 20 m building divides to exactly 2.5, so this placed 5
            // smoulder anchors where the game places 6. See JsMath's header.
            int n = Mathf.Min(8, 3 + Data.JsMath.Round(span / 8f));
            var pts = new Vector3[n];
            for (int k = 0; k < n; k++)
            {
                float a = (float)k / n * Mathf.PI * 2f + bi;
                float r = span * 0.25f * Mathf.Abs(Mathf.Sin(k * 2.7f + bi));
                pts[k] = new Vector3(cx + Mathf.Cos(a) * r, baseY + (top - baseY) * 0.12f + 1f, cz + Mathf.Sin(a) * r);
            }
            return pts;
        }

        /// <summary>One scattered rubble piece's placement — position (already
        /// mound-raised toward the footprint centre), a small random tilt + full
        /// yaw, and a slightly-squashed-in-Y scale. citymap.js composes this as
        /// `Euler(rotX, rotY, rotZ)` + `Vector3(sc, scaleY, sc)`.</summary>
        public struct ScatterPiece
        {
            public Vector3 Pos;
            public float RotX, RotY, RotZ;
            public float ScaleXZ, ScaleY;
        }

        /// <summary>The permanent rubble field left by a FULLY collapsed building —
        /// citymap.js:2728-2759 (_spawnRubble). Count scales with footprint area,
        /// clamped [10,46]; most pieces land on the footprint with a few thrown
        /// into a halo around it, mounded higher toward the centre.</summary>
        public static List<ScatterPiece> SpawnRubble(
            float minX, float maxX, float minZ, float maxZ, float baseY, float top,
            System.Random rnd, Func<float, float, float> heightAt)
        {
            float w = maxX - minX, d = maxZ - minZ;
            float span = Mathf.Max(w, d);
            float cx = (minX + maxX) * 0.5f, cz = (minZ + maxZ) * 0.5f;
            int n = Mathf.RoundToInt(Mathf.Clamp(w * d / 14f, 10f, 46f));
            var outList = new List<ScatterPiece>(n);
            for (int k = 0; k < n; k++)
            {
                float fx = minX - 2.5f + (float)rnd.NextDouble() * (w + 5f);
                float fz = minZ - 2.5f + (float)rnd.NextDouble() * (d + 5f);
                float gy = heightAt(fx, fz);
                float dc = Mathf.Sqrt((fx - cx) * (fx - cx) + (fz - cz) * (fz - cz));
                float mound = Mathf.Max(0f, (top - baseY) * 0.09f * (1f - dc / (span * 0.7f)));
                float sc = 0.9f + (float)rnd.NextDouble() * 1.7f;
                outList.Add(new ScatterPiece
                {
                    Pos = new Vector3(fx, gy + mound, fz),
                    RotX = ((float)rnd.NextDouble() - 0.5f) * 0.6f,
                    RotY = (float)rnd.NextDouble() * Mathf.PI * 2f,
                    RotZ = ((float)rnd.NextDouble() - 0.5f) * 0.6f,
                    ScaleXZ = sc,
                    ScaleY = sc * (0.8f + (float)rnd.NextDouble() * 0.4f),
                });
            }
            return outList;
        }

        /// <summary>Heavy-damage wall spall (stages 6-8): a handful of chunks just
        /// outside a random wall, flat on the ground — citymap.js:2763-2792
        /// (_shedDebris). Always 4 pieces; caller gates repeats per stage via
        /// `rec.shedAt` (citymap.js:2765), not reproduced here since that's a
        /// per-record cooldown, not physics.</summary>
        public static List<ScatterPiece> ShedDebris(
            float minX, float maxX, float minZ, float maxZ,
            System.Random rnd, Func<float, float, float> heightAt)
        {
            float w = maxX - minX, d = maxZ - minZ;
            var outList = new List<ScatterPiece>(4);
            for (int k = 0; k < 4; k++)
            {
                bool side = rnd.NextDouble() < 0.5;
                float fx = side
                    ? (rnd.NextDouble() < 0.5 ? minX - 1.2f : maxX + 1.2f)
                    : minX + (float)rnd.NextDouble() * w;
                float fz = side
                    ? minZ + (float)rnd.NextDouble() * d
                    : (rnd.NextDouble() < 0.5 ? minZ - 1.2f : maxZ + 1.2f);
                float gy = heightAt(fx, fz);
                float sc = 0.6f + (float)rnd.NextDouble() * 0.9f;
                outList.Add(new ScatterPiece
                {
                    Pos = new Vector3(fx, gy, fz),
                    RotX = ((float)rnd.NextDouble() - 0.5f) * 0.5f,
                    RotY = (float)rnd.NextDouble() * Mathf.PI * 2f,
                    RotZ = ((float)rnd.NextDouble() - 0.5f) * 0.5f,
                    ScaleXZ = sc,
                    ScaleY = sc,
                });
            }
            return outList;
        }
    }
}

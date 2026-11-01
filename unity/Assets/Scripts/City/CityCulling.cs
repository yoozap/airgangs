using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Data;   // TerrainSampler

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/citymap.js's per-frame visibility sweep: `_cullDue`
    /// (citymap.js:1121-1128) + the chunk-attach half of `update`
    /// (citymap.js:1144-1215), plus `_updateCollapses`'s pure kinematics
    /// (citymap.js:2409-2500, see <see cref="CollapseAnim"/> below).
    ///
    /// PURE DATA — no GameObject, no Instantiate. In the JS, "detach a chunk
    /// from the scene graph" is `this.group.remove(c.mesh)`; per
    /// .claude/skills/unity/SKILL.md ("No GameObject per building... Use
    /// BatchRendererGroup / instanced draws") and this port brief's own
    /// framing, Unity's analogue of "detach" is simply NOT issuing that
    /// chunk's Graphics.RenderMeshInstanced call this frame. This class only
    /// decides which chunks qualify (HiOn/LoOn/ShOn) — issuing or skipping the
    /// actual draw is the renderer's job (CityRenderer.cs or whatever system
    /// wires this in; see the port brief's hard rule against editing that file).
    ///
    /// GameLoop.WorldTick is `Action&lt;Vector3, float&gt;` (Core/GameLoop.cs:198) —
    /// <see cref="Tick"/> matches that signature so assigning
    /// <c>gameLoop.WorldTick = cityCulling.Tick</c> is a one-line wire-up.
    /// </summary>
    public sealed class CityCulling
    {
        // src/citymap.js:251 CULL_SLICES — each chunk is revisited once every
        // CULL_SLICES frames (66 ms of staleness at 60 fps against a 3500 m
        // radius is invisible), UNLESS a full sweep is forced.
        public const int CullSlices = 4;

        // src/citymap.js:1124 "500 m: teleport" -> force a full, unsliced sweep.
        public const float CullFullMoveSq = 500f * 500f;
        // src/citymap.js:1125 "2 m: nothing can flip" -> skip the sweep entirely.
        public const float CullSkipMoveSq = 2f * 2f;

        // src/citymap.js:1154 `dist = this.cullDist || 3500 + alt * 3`
        public const float BaseCullDist = 3500f;
        public const float CullDistPerAltitude = 3f;

        /// <summary>One chunk plus the attach/draw STATE the sweep flips.
        /// Mirrors the JS record's hiOn/loOn/shOn fields (citymap.js:7987)
        /// exactly; <see cref="ChunkedInstancer.Chunk"/> itself stays
        /// immutable/reusable so many VisChunks can share one partition.</summary>
        public sealed class VisChunk
        {
            public ChunkedInstancer.Chunk Data;
            public bool HiOn;   // full-detail chunk attached (citymap.js:1194-1198)
            public bool LoOn;   // baked-imposter chunk attached (citymap.js:1199-1203)
            public bool ShOn;   // close enough to cast a shadow (citymap.js:1204-1211)
        }

        public readonly List<VisChunk> Chunks = new List<VisChunk>();

        /// <summary>Ground probe under the camera — citymap.js:1148
        /// `this.heightAt(camPos.x, camPos.z) || 0`. Assign once after the
        /// terrain is built; null just means altitude is measured from sea
        /// level (same fallback the JS's `|| 0` gives before the city loads).</summary>
        public TerrainSampler Terrain;

        /// <summary>citymap.js:1154 `this.cullDist` — a hard override in
        /// metres (0 = use the altitude-scaled default). The cinema flypast
        /// sets this.</summary>
        public float CullDistOverride;

        /// <summary>citymap.js:1161 `this.propScale` — multiplies every
        /// chunk's own MaxDist/LodD2/ShadowD2 radius.</summary>
        public float PropScale = 1f;

        Vector3 _cullPos = new Vector3(float.PositiveInfinity, 0f, 0f); // citymap.js:1122
        bool _cullFull = true; // first-ever call is a "teleport" — one full sweep, nothing left stale
        int _cullSlice;

        public int VisibleHiCount { get; private set; }
        public int VisibleLoCount { get; private set; }
        public int VisibleShadowCount { get; private set; }
        public bool LastSweepRan { get; private set; }
        public bool LastSweepWasFull { get; private set; }

        public void Add(ChunkedInstancer.Chunk c) => Chunks.Add(new VisChunk { Data = c });
        public void AddRange(IEnumerable<ChunkedInstancer.Chunk> cs) { foreach (var c in cs) Add(c); }

        /// <summary>src/citymap.js:1117-1128 `_cullDue`. "A chunk's state can
        /// only flip when the camera MOVES, so standing still costs nothing at
        /// all; a jump across the map forces one unsliced full sweep."</summary>
        public bool CullDue(Vector3 camPos)
        {
            // double: the same "absolute game-frame coordinates span six
            // cities" precision trap CityCulling.Tick's h2 comment explains —
            // the 2 m/500 m thresholds here are small enough that float32
            // would rarely flip a decision, but there is no reason to leave
            // this one susceptible when the fix is two casts.
            double dx = (double)camPos.x - _cullPos.x, dy = (double)camPos.y - _cullPos.y, dz = (double)camPos.z - _cullPos.z;
            double d2 = dx * dx + dy * dy + dz * dz;
            if (d2 > CullFullMoveSq) _cullFull = true;
            else if (d2 < CullSkipMoveSq && !_cullFull) return false;
            _cullPos = camPos;
            return true;
        }

        /// <summary>
        /// src/citymap.js:1144-1215, the chunk-attach half of `update(camPos,
        /// dt)`. Matches GameLoop.WorldTick's `Action&lt;Vector3, float&gt;`
        /// signature (Core/GameLoop.cs:198) exactly, so
        /// <c>gameLoop.WorldTick = cityCulling.Tick</c> wires it straight in —
        /// `dt` itself is unused here (the JS chunk-cull section doesn't read
        /// it either; only `_updateCollapses`/the water shader do, and both are
        /// out of this method's scope, see class doc). Safe to call every
        /// frame: when <see cref="CullDue"/> says no sweep is due, the previous
        /// frame's Hi/Lo/Shadow state is left exactly as it was — "standing
        /// still costs nothing" is the whole point, so this does no redundant
        /// work in that case either.
        /// </summary>
        public void Tick(Vector3 camPos, float dt)
        {
            LastSweepRan = Chunks.Count > 0 && CullDue(camPos);
            if (LastSweepRan)
            {
                LastSweepWasFull = _cullFull;
                float groundY = Terrain != null ? Terrain.HeightAt(camPos.x, camPos.z) : 0f;
                float alt = Mathf.Max(0f, camPos.y - groundY);
                float dist = CullDistOverride > 0f ? CullDistOverride : BaseCullDist + alt * CullDistPerAltitude;
                float d2 = dist * dist;
                float ps2 = PropScale * PropScale;

                int slices = _cullFull ? 1 : CullSlices;           // citymap.js:1173
                int start = _cullFull ? 0 : _cullSlice;            // citymap.js:1175
                for (int i = start; i < Chunks.Count; i += slices)
                {
                    var c = Chunks[i];
                    var d = c.Data;
                    double md2 = d.MaxDist > 0f ? (double)d.MaxDist * d.MaxDist * ps2 : d2;   // citymap.js:1179
                    // Squared distance over ABSOLUTE game-frame coordinates — this
                    // frame spans six cities (|x| up to ~264 km); accumulate in
                    // double, the same lesson another agent's fix just paid for in
                    // WallMaterials/BuildingKit's footprint-area shoelace sum
                    // (float32 there silently mis-sized 25% of all buildings).
                    // A plain subtraction of two large floats loses nothing by
                    // itself, but squaring and summing two such terms in float32
                    // can round the result by metres at the far edge of this
                    // coordinate frame — double removes that margin entirely for
                    // the price of two casts. citymap.js:1180 is the float original.
                    double dxw = (double)camPos.x - d.Cx, dzw = (double)camPos.z - d.Cz;
                    double h2 = dxw * dxw + dzw * dzw;
                    bool vis = h2 < md2;                                             // citymap.js:1181
                    bool hi = vis, lo = false;
                    if (d.LodD2 > 0f)                                                // citymap.js:1184-1192
                    {
                        double dy = (double)camPos.y - d.Cy;
                        bool near = h2 + dy * dy < (double)d.LodD2 * ps2;
                        hi = vis && near;
                        lo = vis && !near;
                    }
                    c.HiOn = hi;
                    c.LoOn = lo;
                    c.ShOn = d.ShadowD2 > 0f ? h2 < d.ShadowD2 * ps2 : d.CastShadow;  // citymap.js:1204-1211
                }
                _cullSlice = (_cullSlice + 1) % CullSlices; // citymap.js:1213
                _cullFull = false;                          // citymap.js:1214
            }

            int hi2 = 0, lo2 = 0, sh2 = 0;
            foreach (var c in Chunks) { if (c.HiOn) hi2++; if (c.LoOn) lo2++; if (c.ShOn) sh2++; }
            VisibleHiCount = hi2; VisibleLoCount = lo2; VisibleShadowCount = sh2;
        }

        public IEnumerable<ChunkedInstancer.Chunk> VisibleHigh()
        {
            foreach (var c in Chunks) if (c.HiOn) yield return c.Data;
        }

        public IEnumerable<ChunkedInstancer.Chunk> VisibleLow()
        {
            foreach (var c in Chunks) if (c.LoOn) yield return c.Data;
        }
    }

    /// <summary>
    /// Port of src/citymap.js:2409-2500 `_updateCollapses`'s pure KINEMATICS —
    /// the falling-building animation curve. A collapse record owns its own
    /// (T, Dur, FromKh/FromDark/FromLean) and calls <see cref="Step"/> once a
    /// frame; the caller (whoever owns the per-building HP/stage list — out of
    /// this file's scope, see class doc) applies Kh/Tilt/Wobble/Buckle/Dark/
    /// Lean to its own geometry the same way `_writeInstMatrix`/
    /// `_writeLandmarkPose`/`_writeBuildingVerts` do in the JS.
    ///
    /// NOT PORTED: the dust/smoke/secondary-blast FX the JS spawns alongside
    /// this animation (citymap.js:2423-2440, 2462-2494 `fx.spawnSmoke`/
    /// `fx.explosion`/`onBlast`/`_launchDebris`) — no FX system is ported in
    /// this slice, and the brief's own CityLoader-scope hooks pattern
    /// (GameLoop.cs's `OnHint`/`OnZoneChanged`) is the precedent for leaving
    /// that as the caller's job once an FX port exists, not reimplementing it
    /// here as a guess.
    /// </summary>
    public struct CollapseState
    {
        public float T, Dur;
        public float FromKh, FromDark, FromLean;
        /// <summary>true for 'inst' (instanced church/house) or 'landmark'
        /// records (citymap.js:2416); false for an ordinary building SHELL
        /// (the ELSE branch, citymap.js:2449-2457).</summary>
        public bool IsInstOrLandmark;
    }

    public struct CollapseFrame
    {
        public float P;        // 0..1 progress, citymap.js:2413
        public float Kh;       // current height fraction, both branches
        public float Tilt;     // inst/landmark only, citymap.js:2419
        public float Wobble;   // inst/landmark only, citymap.js:2420
        public float Buckle;   // building-shell only, citymap.js:2453
        public float Dark;     // building-shell only, citymap.js:2454
        public float Lean;     // building-shell only, citymap.js:2455
        public bool Done;      // p >= 1, citymap.js:2441/2495
    }

    public static class CollapseAnim
    {
        /// <summary>src/citymap.js:2409-2457 per-collapse-record body (the FX
        /// calls at 2423-2440/2462-2494 excluded — see class doc).</summary>
        public static CollapseFrame Step(ref CollapseState c, float dt)
        {
            c.T += dt;
            float p = Mathf.Min(1f, c.T / c.Dur);     // citymap.js:2413
            float fall = p * p;                        // citymap.js:2414 "gravity: slow shear first, then it drops"

            var f = new CollapseFrame { P = p, Done = p >= 1f };

            if (c.IsInstOrLandmark)
            {
                // citymap.js:2416-2422 "instanced church/house or landmark
                // model: squash + tilt + wobble"
                f.Kh = c.FromKh + (0.13f - c.FromKh) * fall;
                f.Tilt = 0.02f + 0.06f * p;
                f.Wobble = Mathf.Sin(c.T * 31f) * 0.03f * (1f - p);
            }
            else
            {
                // citymap.js:2449-2455 ordinary building shell
                f.Kh = c.FromKh + (0.12f - c.FromKh) * fall;
                f.Buckle = 0.35f + 1.05f * p;   // "buckle hard on the way down"
                f.Dark = c.FromDark + (0.28f - c.FromDark) * p;
                f.Lean = c.FromLean + 0.09f * p; // "tips into the fall"
            }
            return f;
        }
    }
}

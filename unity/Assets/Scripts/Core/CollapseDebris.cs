using System;
using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.City;
using TbilisiFly.Combat;
using TbilisiFly.Fx;

namespace TbilisiFly.Core
{
    /// <summary>
    /// The runtime owner of the building-collapse debris field — the caller
    /// City/DebrisField.cs was written for and never had.
    ///
    /// src/citymap.js keeps ONE `this._debris` array on the single CityMap and
    /// steps it from `_updateDebris` (citymap.js:2593-2655) every frame, with
    /// four publishers:
    ///   :2405  _startCollapse  -> _launchDebris(rec, 8 + round(span/5), base + h*0.7)
    ///   :2473  _updateCollapses-> _launchDebris(rec, 3, by)   (per secondary blast)
    ///   :2521  _finishCollapse -> _launchDebris(rec, 6 + round(span/6), base + 2)
    ///   :2561  blastDebris     -> a warhead tearing chunks out of a wall
    ///                            (src/rockets.js:309 `g.world.blastDebris?.(...)`)
    /// plus two SETTLED scatters that are placed once and never stepped:
    ///   :2728  _spawnRubble    -> the permanent mound a fully collapsed building leaves
    ///   :2763  _shedDebris     -> 4 chunks spalling off a wall at stage >= 6 (:1553)
    ///
    /// This class is exactly that: the array, the step, and those six entry
    /// points. All the maths is City/DebrisField.cs (unchanged — it was already
    /// complete and probe-verified); all the drawing is Fx/RubbleFx.cs. Nothing
    /// here recomputes either.
    ///
    /// ONE INSTANCE, owned by Core/CombatWorldAdapter.Install and reached from
    /// the ~600 chunk components through the shared
    /// Core/CollapseFxHooks (the same static `CombatBuildingChunk.Fx` route the
    /// collapse already uses for glass/blast/shake/dust), because the JS's
    /// single CityMap owns the single array. A per-chunk field would give 604
    /// independent debris fields and 604 x 6 instanced pools.
    ///
    /// DETERMINISM: one System.Random, seeded from a constant, so a probe or a
    /// play-mode test gets the same field twice. The JS uses Math.random()
    /// throughout here (no seeding), so there is no cross-language number to
    /// match — only the shape and the counts.
    /// </summary>
    public sealed class CollapseDebris
    {
        public readonly RubbleFx Rubble = new RubbleFx();

        /// <summary>The smoke/ember system burning pieces trail on the way down
        /// (citymap.js:2611-2616) and the puff a hard bounce throws up
        /// (citymap.js:2626). Null in a scene with no fire system, exactly like
        /// the JS's own `if (d.hot &amp;&amp; this.fx)` / `this.fx?.spawnSmoke`.</summary>
        public IFireFx Fire;

        /// <summary>Terrain height under (x, z) in UNITY space — must be
        /// City/TerrainSampler.HeightAt (the mirrored-X overload), per
        /// DebrisField's own header. The JS's `this.heightAt(d.x, d.z) ?? 0`.</summary>
        public Func<float, float, float> GroundAt = (x, z) => 0f;

        readonly System.Random _rnd = new System.Random(20261031);

        struct Live
        {
            public DebrisField.Particle P;
            public int Layer, Slot;
        }

        readonly List<Live> _live = new List<Live>();

        /// <summary>Pieces still in the air. The JS's `this._debris.length`.</summary>
        public int LiveCount => _live.Count;
        /// <summary>Pieces that have come to rest and left the step list, their
        /// final matrix still in the pool (citymap.js:2644).</summary>
        public int SettledCount { get; private set; }
        /// <summary>Settled scatter pieces placed by <see cref="SpawnRubble"/> /
        /// <see cref="ShedDebris"/> — never stepped.</summary>
        public int ScatteredCount { get; private set; }
        /// <summary>Pieces a full 4000-slot pool refused (citymap.js:2535).</summary>
        public int RefusedCount { get; private set; }

        public bool EnsureLoaded() => Rubble.Load();

        // ====================================================================
        // citymap.js:2527-2556 _launchDebris — n ballistic chunks off a
        // collapsing building, from (cx, cz) within span*0.3 at height fromY.
        // ====================================================================
        public int Launch(float cx, float cz, float span, float fromY, int n)
        {
            if (!EnsureLoaded()) return 0;
            int made = 0;
            for (int k = 0; k < n; k++)
            {
                var slot = Rubble.Reserve(_rnd);
                if (slot == null) { RefusedCount++; continue; } // citymap.js:2535
                var p = DebrisField.Launch(cx, cz, fromY, span, _rnd);
                Add(p, slot.Value);
                made++;
            }
            return made;
        }

        // ====================================================================
        // citymap.js:2561-2591 blastDebris — chunks torn out of a wall along the
        // blast cone, every third one burning. Reached from
        // CombatWorldAdapter.BlastDebris (IDamageWorld), i.e. src/rockets.js:309.
        // ====================================================================
        public int LaunchBlast(Vector3 at, Vector3 dir, int n)
        {
            if (!EnsureLoaded()) return 0;
            int made = 0;
            for (int k = 0; k < n; k++)
            {
                var slot = Rubble.Reserve(_rnd);
                if (slot == null) { RefusedCount++; continue; }
                var p = DebrisField.LaunchBlast(at, dir, _rnd, k);
                Add(p, slot.Value);
                made++;
            }
            return made;
        }

        void Add(DebrisField.Particle p, (int layer, int slot) s)
        {
            _live.Add(new Live { P = p, Layer = s.layer, Slot = s.slot });
            Write(s.layer, s.slot, p);
        }

        void Write(int layer, int slot, in DebrisField.Particle p)
            // citymap.js:2637-2640 — Euler(rx, 0, rz), uniform scale. No yaw spin.
            => Rubble.SetPiece(layer, slot, p.Pos, p.RotX, 0f, p.RotZ, p.Scale, p.Scale);

        // ====================================================================
        // citymap.js:2728-2759 _spawnRubble — the permanent mound a FULLY
        // collapsed building leaves, and :2763-2792 _shedDebris — 4 chunks
        // spalling off a wall at heavy damage. Both are placed once, never
        // stepped: the JS writes their matrix straight into the layer.
        // ====================================================================
        public int SpawnRubble(float minX, float maxX, float minZ, float maxZ, float baseY, float top)
        {
            if (!EnsureLoaded()) return 0;
            return Scatter(DebrisField.SpawnRubble(minX, maxX, minZ, maxZ, baseY, top, _rnd, Ground));
        }

        public int ShedDebris(float minX, float maxX, float minZ, float maxZ)
        {
            if (!EnsureLoaded()) return 0;
            return Scatter(DebrisField.ShedDebris(minX, maxX, minZ, maxZ, _rnd, Ground));
        }

        int Scatter(List<DebrisField.ScatterPiece> pieces)
        {
            int made = 0;
            foreach (var s in pieces)
            {
                var slot = Rubble.Reserve(_rnd);
                if (slot == null) { RefusedCount++; continue; }
                Rubble.SetPiece(slot.Value.layer, slot.Value.slot, s.Pos, s.RotX, s.RotY, s.RotZ, s.ScaleXZ, s.ScaleY);
                made++;
                ScatteredCount++;
            }
            return made;
        }

        float Ground(float x, float z) => GroundAt != null ? GroundAt(x, z) : 0f;

        // ====================================================================
        // citymap.js:2593-2655 _updateDebris — one step for every piece in the
        // air, then the draw. Settled pieces leave this list and their last
        // matrix stays in the pool, which is what makes the field permanent.
        // ====================================================================
        public void Tick(float dt)
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var d = _live[i];
                bool settled = DebrisField.Step(ref d.P, dt, Ground, out bool reignited, out bool bounced);
                if (reignited && Fire != null)
                {
                    // citymap.js:2613-2615
                    Fire.SpawnSmoke(d.P.Pos.x, d.P.Pos.y, d.P.Pos.z,
                        size: 2.6f, dark: 0.6f, dur: 1.9f, vx: 0f, vy: 1.4f, vz: 0f, spread: 0.5f);
                    if (_rnd.NextDouble() < 0.5) Fire.SpawnEmber(d.P.Pos.x, d.P.Pos.y, d.P.Pos.z);
                }
                if (bounced && Fire != null)
                    // citymap.js:2626 — the puff is at the GROUND, 0.4 m up, not at the piece
                    Fire.SpawnSmoke(d.P.Pos.x, d.P.Pos.y + 0.4f, d.P.Pos.z,
                        size: 3f, dark: 0.28f, dur: 1.6f, vx: 0f, vy: 1f, vz: 0f, spread: 0f);
                Write(d.Layer, d.Slot, d.P);
                if (settled) { _live.RemoveAt(i); SettledCount++; }
                else _live[i] = d;
            }
            Rubble.Render();
        }

        public void Dispose() => Rubble.Dispose();
    }
}

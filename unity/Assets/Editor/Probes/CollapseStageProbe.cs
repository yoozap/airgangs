using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Core;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the ten crumble stages a BUILDING CHUNK actually paints, and that a
    /// collapse publishes its FX.
    ///
    /// Why this probe exists: City/BuildingDamage.cs has held src/citymap.js:1350's
    /// measured STAGE_H table and :1969-2012's per-vertex deformation since it was
    /// ported, and CityMapRestProbe already verifies BOTH against the JS. But
    /// Core/CombatBuildingChunk.PaintStage — the only thing that paints a real
    /// building in the game — never called either: it sank the roofline on a
    /// LINEAR curve (stage/9 * 1.8 m) with no buckle, no lean and a different soot
    /// ramp. A probe that exercises the pure function proves the calculation, not
    /// that the calculation is REACHED (see the unity skill's IsHumanoid lesson),
    /// so this one drives the CHUNK, through ApplyDamage, and reads the painted
    /// mesh back.
    ///
    /// Paired with tools/_probe_collapsestage.mjs, which prints the same KEY=value
    /// lines from src/citymap.js's own STAGE_H/_applyBuildingStage/
    /// _writeBuildingVerts arithmetic on the same synthetic building.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CollapseStageProbe.Run
    /// </summary>
    public static class CollapseStageProbe
    {
        // One 24 x 16 m building, 30 m tall, GAME frame — big enough that STAGE_H's
        // fractional drop is metres rather than centimetres at every stage.
        static readonly float[] Fp = { 0f, 0f, 24f, 0f, 24f, 16f, 0f, 16f };
        const float BaseY = 0f, Top = 30f;
        const int Bi = 0;

        public static void Run()
        {
            StageRooflines();
            CollapsePublishers();
            ProbeExit.Exit(0);
        }

        // ---- the synthetic chunk: City/BuildingBuilder.cs's real per-edge wall
        // quad + roof fan layout (GlassHoleProbe.BuildBuildingMesh's shape). ----

        static Mesh BuildMesh()
        {
            int vc = Fp.Length / 2;
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int v = 0; v < vc; v++)
            {
                int w = (v + 1) % vc;
                float ax = -Fp[v * 2], az = Fp[v * 2 + 1]; // mirror X into Unity space
                float bx = -Fp[w * 2], bz = Fp[w * 2 + 1];
                int b = verts.Count;
                verts.Add(new Vector3(ax, BaseY, az));
                verts.Add(new Vector3(bx, BaseY, bz));
                verts.Add(new Vector3(bx, Top, bz));
                verts.Add(new Vector3(ax, Top, az));
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
            }
            float ccx = 0f, ccz = 0f;
            for (int v = 0; v < vc; v++) { ccx += -Fp[v * 2]; ccz += Fp[v * 2 + 1]; }
            ccx /= vc; ccz /= vc;
            int centre = verts.Count;
            verts.Add(new Vector3(ccx, Top, ccz));          // vertex 4*vc — the roof apex
            int ring = verts.Count;
            for (int v = 0; v < vc; v++) verts.Add(new Vector3(-Fp[v * 2], Top, Fp[v * 2 + 1]));
            for (int v = 0; v < vc; v++) { tris.Add(centre); tris.Add(ring + v); tris.Add(ring + (v + 1) % vc); }

            var mesh = new Mesh { name = "probe_collapse_building" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.triangles = tris.ToArray();
            // Flat mid-grey vertex colours so the soot ramp is readable as a number.
            var cols = new Color32[verts.Count];
            for (int i = 0; i < cols.Length; i++) cols[i] = new Color32(200, 200, 200, 255);
            mesh.SetColors(cols);
            mesh.RecalculateBounds();
            return mesh;
        }

        static CombatBuildingChunk MakeChunk(string name)
        {
            int vc = Fp.Length / 2;
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = BuildMesh();
            var chunk = go.AddComponent<CombatBuildingChunk>();
            chunk.RangeStart = new[] { 0 };
            chunk.RangeCount = new[] { vc * 4 + vc + 1 };
            chunk.Heights = new[] { Top - BaseY };
            chunk.BuildingIndex = new[] { 0 };
            chunk.WallBase = new[] { BaseY };
            chunk.WallTop = new[] { Top };
            chunk.ShellTint = new[] { new Color32(190, 185, 178, 255) };
            chunk.ShellWallBucket = new[] { -1 };
            chunk.FootprintFlat = (float[])Fp.Clone();
            chunk.FootprintOffset = new[] { 0 };
            chunk.FootprintCount = new[] { vc };
            return chunk;
        }

        /// <summary>The roofline per stage: `base + (top - base) * STAGE_H[s]`,
        /// measured off the painted mesh at the roof-apex vertex (index 4*vc, the
        /// one vertex whose relY is exactly the full height, so kh scales it
        /// directly). vy carries the stage's own buckle jitter, which is part of
        /// the look and is reproduced on the JS side from the same hash.</summary>
        static void StageRooflines()
        {
            var chunk = MakeChunk("probe_stages");
            int apex = (Fp.Length / 2) * 4;
            float maxHp = chunk.MaxHpOf(Bi);
            Debug.Log($"COLLAPSEBLD base={BaseY:F4} top={Top:F4} apexVert={apex} maxHp={maxHp:F4}");

            // Walk the stages by taking real damage, exactly as a gun does — the
            // stage numbers must come out of ApplyDamage's own HpFrac/StageOf, not
            // be poked in.
            for (int s = 1; s <= 8; s++)
            {
                // citymap.js:1498 _stageOf inverted: the LOWEST hp still in stage s.
                // hp/maxHp just under 1 - s/9 lands in s.
                float wantHp = maxHp * (1f - s / 9f) - 0.5f;
                float cur = maxHp * chunk.HpFrac(Bi);
                chunk.ApplyDamage(Bi, Mathf.Max(0f, cur - wantHp));
                int got = chunk.StageOf(Bi);
                var mesh = chunk.GetComponent<MeshFilter>().sharedMesh;
                float vy = mesh.vertices[apex].y;
                BuildingDamage.StageParams(got, out float kh, out float jit, out float dark, out float lean);
                Debug.Log($"COLLAPSESTAGE s={got} kh={kh:F6} roofIdeal={(BaseY + (Top - BaseY) * kh):F6} " +
                          $"vy={vy:F6} jit={jit:F6} dark={dark:F6} lean={lean:F6}");
            }

            // Stage 9 is the COLLAPSE: an animated drop, then the rubble mound.
            // Tick it to completion and read the settled roofline.
            chunk.ApplyDamage(Bi, maxHp * 2f);
            Debug.Log($"COLLAPSESTART stage={chunk.StageOf(Bi)}");
            for (int i = 0; i < 400; i++) chunk.TickCollapses(0.02f); // 8 s > dur (2.2 + min(1.3, 30*0.03))
            {
                var mesh = chunk.GetComponent<MeshFilter>().sharedMesh;
                float vy = mesh.vertices[apex].y;
                BuildingDamage.StageParams(9, out float kh, out float jit, out float dark, out float lean);
                Debug.Log($"COLLAPSESTAGE s=9 kh={kh:F6} roofIdeal={(BaseY + (Top - BaseY) * kh):F6} " +
                          $"vy={vy:F6} jit={jit:F6} dark={dark:F6} lean={lean:F6}");
            }
        }

        /// <summary>The two src/game.js:427/429 publishers: a stage change must blow
        /// the glass above the sunken roofline, and a collapse must raise blasts at
        /// ignition, mid-fall and on the ground slam.</summary>
        static void CollapsePublishers()
        {
            var chunk = MakeChunk("probe_publish");
            int panes = 0, bursts = 0, blasts = 0, shakes = 0;
            float firstBlastR = 0f, lastBlastR = 0f;
            CombatBuildingChunk.Fx = new CollapseFxHooks
            {
                OnWindowsBlown = list => { bursts++; panes += list.Length / 5; },
                OnBlast = (x, y, z, r) => { blasts++; if (blasts == 1) firstBlastR = r; lastBlastR = r; },
                OnShake = (s, x, z) => shakes++,
                GroundAt = (x, z) => (float?)null,
            };
            float maxHp = chunk.MaxHpOf(0);

            // one stage step (stage 1): panes above base + h*0.97 - 1 go
            chunk.ApplyDamage(0, maxHp * 0.12f);
            Debug.Log($"COLLAPSEFX id=stage1 stage={chunk.StageOf(0)} bursts={bursts} panes={panes} " +
                      $"blasts={blasts} shakes={shakes}");

            int burstsAfterStages = bursts;
            chunk.ApplyDamage(0, maxHp * 2f); // the killing shot
            Debug.Log($"COLLAPSEFX id=ignite bursts={bursts} newBursts={bursts - burstsAfterStages} " +
                      $"panes={panes} blasts={blasts} blastR={firstBlastR:F4} shakes={shakes}");

            int blastsAtIgnition = blasts;
            for (int i = 0; i < 400; i++) chunk.TickCollapses(0.02f);
            Debug.Log($"COLLAPSEFX id=fall blasts={blasts} secondariesAndSlam={blasts - blastsAtIgnition} " +
                      $"slamR={lastBlastR:F4} shakes={shakes}");
            CombatBuildingChunk.Fx = null;
        }
    }
}

using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// A 60 m terrain cell cannot show a real edge on a 12 m river bank or a
    /// 20 m tunnel trench, so both TerrainBuilder (the main ground sheet) and
    /// GrassCover (the second grass/rock surface) drop the handful of cells
    /// either one passes through and this file rebuilds just those cells at
    /// 5 m — src/citymap.js:3961-4005 (_refinedCells) + :4007-4057
    /// (_buildCutTerrain). citymap.js calls _refinedCells separately from
    /// _buildTerrainMesh and from _buildGrass, but both calls read the same
    /// `this._river`/`this._cuts` state and so always agree; this port makes
    /// that implicit agreement explicit by exposing ONE function both
    /// TerrainBuilder's caller and GrassCover.Build can share.
    ///
    /// HANDEDNESS: Build() mirrors X into Unity space and flips winding, same
    /// as TerrainBuilder/GrassCover.
    /// </summary>
    public static class CutTerrainBuilder
    {
        public const int Sub = 12; // 60 m / 12 = 5 m sub-quads — citymap.js:4009 `N`

        /// <summary>
        /// Grid cell keys (r*g.W+c, over the cell grid 0..H-2 x 0..W-2 — the
        /// SAME key TerrainBuilder.Build's `excludeCells` and GrassCover's
        /// per-cell loop both index by) that need a 5 m rebuild instead of the
        /// standard 60 m quad:
        ///
        ///  - river != null: a cell whose 4 corners disagree on "inside the
        ///    carved river mask" — citymap.js:3968-3988. (The JS also gates
        ///    this on a `this._roadNear` hash that no longer exists once
        ///    RiverSheet.cs chose not to port it — see that file's own class
        ///    doc; omitting the gate here matches what the JS does when
        ///    `_roadNear` is itself falsy, i.e. every disagreeing cell is
        ///    refined, none skipped.)
        ///  - cutPlan != null &amp;&amp; cutPlan.HasCuts: every cell within a cut
        ///    segment's half-width + CutBatter + one grid step of either
        ///    endpoint's bounding box — citymap.js:3990-4003.
        /// </summary>
        public static HashSet<int> RefinedCells(TerrainSampler g, RiverSheet river, TunnelCuts.Plan cutPlan)
        {
            var cells = new HashSet<int>();
            if (g == null) return cells;

            if (river != null)
            {
                int n = 0;
                for (int r = 0; r < g.H - 1; r++)
                for (int c = 0; c < g.W - 1; c++)
                {
                    float x0 = g.MinX + c * g.Step;
                    float z0 = g.MinZ + r * g.Step;
                    bool a = river.InRiverMask(x0, z0);
                    if (a == river.InRiverMask(x0 + g.Step, z0)
                        && a == river.InRiverMask(x0, z0 + g.Step)
                        && a == river.InRiverMask(x0 + g.Step, z0 + g.Step))
                        continue; // not a bank cell
                    cells.Add(r * g.W + c);
                    n++;
                }
                Debug.Log($"CUTTERRAIN river bank cells refined to {(g.Step / Sub):F1} m: {n}");
            }

            if (cutPlan != null && cutPlan.HasCuts)
            {
                foreach (var seg in cutPlan.Cuts)
                {
                    float r = seg.Hw + TunnelCuts.CutBatter + g.Step; // margin so the seam is never open
                    float x0 = Mathf.Min(seg.X1, seg.X2) - r;
                    float x1 = Mathf.Max(seg.X1, seg.X2) + r;
                    float z0 = Mathf.Min(seg.Z1, seg.Z2) - r;
                    float z1 = Mathf.Max(seg.Z1, seg.Z2) + r;
                    int c0 = Mathf.Max(0, Mathf.FloorToInt((x0 - g.MinX) / g.Step));
                    int c1 = Mathf.Min(g.W - 2, Mathf.FloorToInt((x1 - g.MinX) / g.Step));
                    int r0 = Mathf.Max(0, Mathf.FloorToInt((z0 - g.MinZ) / g.Step));
                    int r1 = Mathf.Min(g.H - 2, Mathf.FloorToInt((z1 - g.MinZ) / g.Step));
                    for (int rr = r0; rr <= r1; rr++)
                    for (int cc = c0; cc <= c1; cc++)
                        cells.Add(rr * g.W + cc);
                }
            }
            return cells;
        }

        /// <summary>
        /// Rebuilds every cell in <paramref name="cells"/> as a Sub x Sub
        /// (5 m) sub-quad patch, draped with <paramref name="heightAt"/> and
        /// tinted with its parent cell's flat colour from
        /// <paramref name="parentColors"/> (TerrainBuilder.ComputeVertexColors's
        /// full-grid array) — citymap.js:4007-4057. Returns null if there is
        /// nothing to build (citymap.js:4042's early return).
        ///
        /// <paramref name="heightAt"/> should be RiverSheet.HeightAt when a
        /// river exists (river.HeightAt already layers
        /// TerrainSampler.HeightAtGame, which itself already reads
        /// TerrainSampler.GroundCut for a tunnel trench), else
        /// TerrainSampler.HeightAtGame directly — matching citymap.js's own
        /// single `this.heightAt(x, z)` call, which is the one height
        /// function every carve/cut layer in the JS build is wired through.
        /// </summary>
        public static Mesh Build(TerrainSampler g, HashSet<int> cells, Color[] parentColors,
            System.Func<float, float, float> heightAt)
        {
            if (g == null || cells == null || cells.Count == 0 || heightAt == null) return null;

            var pos = new List<Vector3>();
            var col = new List<Color>();
            var idx = new List<int>();

            foreach (var cell in cells)
            {
                int r = cell / g.W;
                int c = cell - r * g.W;
                float bx = g.MinX + c * g.Step;
                float bz = g.MinZ + r * g.Step;
                int baseIdx = pos.Count;
                // reuse the parent cell's tint — citymap.js:4020-4022
                Color tint = (parentColors != null && cell < parentColors.Length)
                    ? parentColors[cell] : Color.white;

                for (int j = 0; j <= Sub; j++)
                for (int i = 0; i <= Sub; i++)
                {
                    float x = bx + (i / (float)Sub) * g.Step;
                    float z = bz + (j / (float)Sub) * g.Step;
                    pos.Add(new Vector3(x, heightAt(x, z), z));
                    col.Add(tint);
                }
                for (int j = 0; j < Sub; j++)
                for (int i = 0; i < Sub; i++)
                {
                    int a = baseIdx + j * (Sub + 1) + i;
                    idx.Add(a); idx.Add(a + Sub + 1); idx.Add(a + 1);
                    idx.Add(a + 1); idx.Add(a + Sub + 1); idx.Add(a + Sub + 2);
                }
            }
            if (idx.Count == 0) return null;

            var verts = new Vector3[pos.Count];
            for (int i = 0; i < pos.Count; i++)
                verts[i] = new Vector3(-pos[i].x, pos[i].y, pos[i].z); // mirror X into Unity space

            var tris = idx.ToArray();
            GeoFrame.FlipWinding(tris);

            var mesh = new Mesh { name = "cutTerrain", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetColors(col);
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            // SNorm8 normals: 8 bytes a vertex, read back from the
            // RecalculateNormals above so the lighting is unchanged.
            MeshPack.Compact(mesh, "cutTerrain");
            Debug.Log($"CUTTERRAIN cells={cells.Count} verts={verts.Length} tris={tris.Length / 3}");
            return mesh;
        }
    }
}

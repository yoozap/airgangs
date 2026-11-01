using System.Collections.Generic;
using System.Linq;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves this session's citymap.js pass — TerrainBuilder's new
    /// ComputeVertexColors (the vertex-colour "splatting" half of
    /// _buildTerrainMesh, citymap.js:3879-3915) and the new
    /// CutTerrainBuilder.cs (RefinedCells/Build, citymap.js:3961-4057) —
    /// against the real numbers tools/_probe_cityrest.mjs reads straight out
    /// of src/citymap.js. Every KEY printed here has a matching line from the
    /// Node probe — diff them.
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CityRestProbe.Run
    /// </summary>
    public static class CityRestProbe
    {
        public static void Run()
        {
            // The four ground hexes, read off TerrainBuilder's own consts so this
            // compares the shipped value rather than a retyped copy.
            Debug.Log($"DRY_HEX={City.TerrainBuilder.DryHex}");
            Debug.Log($"HIGH_HEX={City.TerrainBuilder.HighHex}");
            Debug.Log($"GREEN_HEX={City.TerrainBuilder.GreenHex}");
            Debug.Log($"WATER_HEX={City.TerrainBuilder.WaterHex}");
            Debug.Log($"CUT_BATTER={TunnelCuts.CutBatter:F0}");
            Debug.Log($"N_SUB={CutTerrainBuilder.Sub}");

            RunVertexColors();
            RunRefinedCells();
            RunCutMesh();
        }

        // ---- 1. ComputeVertexColors, citymap.js:3879-3908 --------------------
        static void RunVertexColors()
        {
            // W=11,H=6 so (c=10,r=5) and (c=10,r=2) both exist. Default every
            // cell to 610/class 0 so the only min/max in the WHOLE grid are
            // the two cells deliberately set to 600/620 — matching the Node
            // probe's "minE=600, maxE=620" synthetic grid exactly.
            int w = 11, h = 6;
            var heights = new float[w * h];
            var classes = new byte[w * h];
            for (int i = 0; i < heights.Length; i++) heights[i] = 610f;

            int iWater = 0 * w + 0;   // r=0,c=0 — global min
            int iMax = 5 * w + 10;    // r=5,c=10 — global max
            int iBare = 5 * w + 3;    // r=5,c=3
            int iGrass = 2 * w + 10;  // r=2,c=10
            heights[iWater] = 600f; classes[iWater] = 2;
            heights[iMax] = 620f;
            heights[iBare] = 610f; classes[iBare] = 0;
            heights[iGrass] = 605f; classes[iGrass] = 1;

            var t = new TerrainSampler(w, h, 60f, 0f, 0f, heights, classes);
            var col = TerrainBuilder.ComputeVertexColors(t);

            PrintColor("VCOLOR_BARE_C3_R5", col[iBare]);
            PrintColor("VCOLOR_GRASS_C10_R2", col[iGrass]);
            PrintColor("VCOLOR_WATER_C0_R0", col[iWater]);
        }

        static void PrintColor(string key, Color c)
            => Debug.Log($"{key}={(c.r * 255f):F6},{(c.g * 255f):F6},{(c.b * 255f):F6}");

        // ---- 2. RefinedCells, tunnel-cut half, citymap.js:3990-4003 -----------
        static void RunRefinedCells()
        {
            var g = new TerrainSampler(10, 10, 60f, 0f, 0f, new float[100], new byte[100]);
            var plan = new TunnelCuts.Plan();
            plan.Cuts.Add(new TunnelCuts.CutSeg { X1 = 120f, Z1 = 0f, X2 = 300f, Z2 = 0f, Hw = 9f });

            var cells = CutTerrainBuilder.RefinedCells(g, null, plan);
            var sorted = cells.OrderBy(x => x).ToList();
            Debug.Log($"REFINEDCELLS_CUT_COUNT={sorted.Count}");
            Debug.Log($"REFINEDCELLS_CUT_KEYS={string.Join(",", sorted)}");
        }

        // ---- 3. CutTerrainBuilder.Build shape sanity, citymap.js:4007-4057 ----
        static void RunCutMesh()
        {
            var g = new TerrainSampler(10, 10, 60f, 0f, 0f, new float[100], new byte[100]);
            var cells = new HashSet<int> { 12 }; // r=1,c=2
            System.Func<float, float, float> heightAt = (x, z) => x * 0.1f + z * 0.2f + 5f;

            var mesh = CutTerrainBuilder.Build(g, cells, null, heightAt);
            int verts = mesh.vertexCount;
            int tris = mesh.triangles.Length / 3;
            Debug.Log($"CUTMESH_CELL12_VERTS={verts}");
            Debug.Log($"CUTMESH_CELL12_TRIS={tris}");

            // mirror X back OUT to the game frame for a direct Node diff —
            // Build() mirrors X into Unity space the same way TerrainBuilder does.
            var v0 = mesh.vertices[0];
            var vLast = mesh.vertices[verts - 1];
            Debug.Log($"CUTMESH_CELL12_V0_XYZ={(-v0.x):F3},{v0.y:F3},{v0.z:F3}");
            Debug.Log($"CUTMESH_CELL12_VLAST_XYZ={(-vLast.x):F3},{vLast.y:F3},{vLast.z:F3}");
        }
    }
}

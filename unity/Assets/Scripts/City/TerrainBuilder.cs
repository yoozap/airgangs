using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Builds the ground mesh from the same half-res grid TerrainSampler reads.
    ///
    /// THE TRIANGULATION IS LOAD-BEARING. Each cell is split as
    ///     (a, a+w, a+1) (a+1, a+w, a+w+1)
    /// i.e. on the h10..h01 diagonal, because TerrainSampler.HeightAtGame
    /// evaluates exactly that plane. Change the split here and every road,
    /// building and prop in the city sits on a surface that is not the one being
    /// drawn — that was the "grass swallowed the asphalt" bug in the JS build.
    ///
    /// Chunked into tiles because the grid is 417x408 = 170,136 vertices, well
    /// past the 65,535 a 16-bit index buffer can address, and because tiles are
    /// what culling wants anyway.
    ///
    /// HANDEDNESS: vertices are mirrored in X (game frame is X=west, Unity is
    /// X=east) and the winding is flipped to match, via GeoFrame.
    ///
    /// VERTEX COLOUR / "SPLATTING" (citymap.js:3874-3959 _buildTerrainMesh,
    /// the half this file previously left out). Every vertex gets a base
    /// colour from its ground CLASS (0 bare earth -> a dry/high-altitude
    /// gradient by elevation, 1 grass/woods -> park green, 2 water -> a blue
    /// that never actually renders since WaterBodies/RiverSheet draw a real
    /// surface on top) plus a small per-vertex hash tint so a flat colour
    /// field doesn't read as a single painted polygon. ComputeVertexColors is
    /// the full W*H array citymap.js keeps as `this._terrainCol` — exposed so
    /// CutTerrainBuilder can "reuse the parent cell's tint" exactly as
    /// citymap.js:4020-4022 does for the 5 m cut-terrain patches.
    ///
    /// Satellite-photo UV (citymap.js:3864-3872 _satUV, swapped in once
    /// tbilisi/satellite.jpg loads) is NOT ported here: CityData.cs does not
    /// yet read city.json's `meta.satellite`/`country` payload, so there is no
    /// tile-pixel metadata to map a vertex against. Vertex colour is exactly
    /// what the JS build itself shows before that texture arrives, so this is
    /// a real (if temporary) parity gap, not a cosmetic shortcut.
    /// </summary>
    public static class TerrainBuilder
    {
        /// <summary>Grid cells per tile edge. 64 cells x 60 m = ~3.8 km tiles.</summary>
        public const int TileCells = 64;

        // ---- ground-class palette, citymap.js:284-287 hexToRgb + 3879-3882 ----
        // (sRGB -> linear, same conversion WallMaterials.HexToLinear255 already
        // applies to every wall/roof colour in this port — reused rather than
        // re-derived so the two palettes can never silently disagree.)
        // Named so CityRestProbe can PRINT the hex and pair it with the value
        // tools/_probe_cityrest.mjs regexes out of the real src/citymap.js. An
        // inline literal inside Lin(...) is unreadable to a probe, so these four
        // ground colours were the only lines of that probe verifying nothing.
        internal const string DryHex = "#9c9273";
        internal const string HighHex = "#8a9464";
        internal const string GreenHex = "#5a9a3c";   // grassy park green
        internal const string WaterHex = "#4d7fb5";

        static readonly Color Dry = Lin(DryHex);
        static readonly Color High = Lin(HighHex);
        static readonly Color Green = Lin(GreenHex);
        static readonly Color WaterCol = Lin(WaterHex);

        static Color Lin(string hex)
        {
            var c = WallMaterials.HexToLinear255(hex);
            return new Color(c[0] / 255f, c[1] / 255f, c[2] / 255f);
        }

        public struct Tile
        {
            public Mesh Mesh;
            public int Col, Row;      // tile index
            public Bounds Bounds;     // Unity space
        }

        /// <summary>
        /// Per-vertex ground colour for the WHOLE grid (not per-tile), indexed
        /// exactly like the grid itself (`r * t.W + c`) — citymap.js:3883-3915.
        /// Computed once up front because the elevation gradient needs the
        /// GLOBAL min/max height, same as the JS build's single pass over
        /// `g.heights` before the per-vertex loop.
        /// </summary>
        public static Color[] ComputeVertexColors(TerrainSampler t)
        {
            var col = new Color[t.W * t.H];
            float minE = float.MaxValue, maxE = float.MinValue;
            for (int i = 0; i < t.Heights.Length; i++)
            {
                float y = t.Heights[i];
                if (y < minE) minE = y;
                if (y > maxE) maxE = y;
            }
            float span = maxE - minE + 1f;

            for (int r = 0; r < t.H; r++)
            for (int c = 0; c < t.W; c++)
            {
                int i = r * t.W + c;
                byte cls = t.Classes != null ? t.Classes[i] : (byte)0;
                Color baseCol;
                if (cls == 2) baseCol = WaterCol;
                else if (cls == 1) baseCol = Green;
                else
                {
                    float k = (t.Heights[i] - minE) / span;
                    baseCol = new Color(
                        Dry.r + (High.r - Dry.r) * k,
                        Dry.g + (High.g - Dry.g) * k,
                        Dry.b + (High.b - Dry.b) * k);
                }

                // Cheap deterministic per-vertex tint, citymap.js:3905. The
                // sin(..)*43758.5453 fract hash MUST run in double — q reaches
                // ~43758, past float32's ~0.004 resolution at that magnitude
                // (see Data/JsMath.cs's own note; the argument here is small
                // grid indices, but the multiplied-out constant is not).
                double hv = System.Math.Sin((double)c * 12.9898 + (double)r * 78.233) * 43758.5453;
                double frac = hv - System.Math.Floor(hv);
                float n = 0.92f + 0.08f * (float)frac;

                col[i] = new Color(baseCol.r * n, baseCol.g * n, baseCol.b * n);
            }
            return col;
        }

        /// <param name="excludeCells">
        /// Cell keys (r*t.W+c, over the cell grid 0..H-2 x 0..W-2) to leave
        /// OUT of this mesh's triangles — the handful of 60 m cells a river
        /// bank or tunnel cutting passes through, rebuilt at 5 m by
        /// CutTerrainBuilder.Build instead (citymap.js:3916-3930's `refine`
        /// set, from CutTerrainBuilder.RefinedCells). Null/empty = every cell
        /// triangulated normally, i.e. the prior behaviour of this method.
        /// </param>
        public static List<Tile> Build(TerrainSampler t, HashSet<int> excludeCells = null)
        {
            var tiles = new List<Tile>();
            int tilesX = Mathf.CeilToInt((t.W - 1f) / TileCells);
            int tilesZ = Mathf.CeilToInt((t.H - 1f) / TileCells);
            var allColors = ComputeVertexColors(t);
            bool hasExclude = excludeCells != null && excludeCells.Count > 0;

            for (int tz = 0; tz < tilesZ; tz++)
            for (int tx = 0; tx < tilesX; tx++)
            {
                int c0 = tx * TileCells, r0 = tz * TileCells;
                int c1 = Mathf.Min(c0 + TileCells, t.W - 1);
                int r1 = Mathf.Min(r0 + TileCells, t.H - 1);
                int nc = c1 - c0 + 1, nr = r1 - r0 + 1;
                if (nc < 2 || nr < 2) continue;

                var verts = new Vector3[nc * nr];
                var uvs   = new Vector2[nc * nr];
                var cols  = new Color[nc * nr];
                for (int r = 0; r < nr; r++)
                for (int c = 0; c < nc; c++)
                {
                    int gi = (r0 + r) * t.W + (c0 + c);
                    float gx = t.MinX + (c0 + c) * t.Step;
                    float gz = t.MinZ + (r0 + r) * t.Step;
                    // mirror X into Unity space
                    verts[r * nc + c] = new Vector3(-gx, t.Heights[gi], gz);
                    uvs[r * nc + c] = new Vector2((c0 + c) * t.Step / 120f,
                                                  (r0 + r) * t.Step / 120f);
                    cols[r * nc + c] = allColors[gi];
                }

                var trisList = new List<int>((nc - 1) * (nr - 1) * 6);
                for (int r = 0; r < nr - 1; r++)
                for (int c = 0; c < nc - 1; c++)
                {
                    if (hasExclude && excludeCells.Contains((r0 + r) * t.W + (c0 + c))) continue;
                    int a = r * nc + c;
                    // the split TerrainSampler assumes — do not reorder
                    trisList.Add(a);          trisList.Add(a + nc);     trisList.Add(a + 1);
                    trisList.Add(a + 1);      trisList.Add(a + nc);     trisList.Add(a + nc + 1);
                }
                var tris = trisList.ToArray();
                GeoFrame.FlipWinding(tris); // because X was mirrored

                var mesh = new Mesh { name = $"terrain_{tx}_{tz}" };
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.vertices = verts;
                mesh.uv = uvs;
                mesh.colors = cols;
                mesh.triangles = tris;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                tiles.Add(new Tile { Mesh = mesh, Col = tx, Row = tz, Bounds = mesh.bounds });
            }
            return tiles;
        }
    }
}

using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.Combat
{
    // =========================================================================
    // LOCAL BUILDING DESTRUCTION — port of src/breakwall.js (309 lines)
    // =========================================================================
    // A warhead takes a BITE out of the wall it hits — a hole through a flat
    // facade, a corner sheared clean off if you hit the corner — instead of the
    // whole building merely getting shorter and sootier.
    //
    // The city's buildings are extruded prisms (see City/BuildingBuilder.cs):
    // one quad per facade edge, base ring to roofline, no geometry near an
    // impact to break. The first time a building takes a warhead, its walls are
    // swapped for a TESSELLATED SHELL — same footprint, same wall tint — cut
    // into ~1.5x1.6 m cells. Damage then just drops the cells inside a blast
    // sphere, which is what makes the hole follow the real shape of the hit.
    // Behind the shell sits a dark LINING (the gutted interior) and a stack of
    // FLOOR SLABS: the lining loses a SMALLER disc than the facade, so the hole
    // reads with real wall thickness at its rim.
    //
    // ADAPTED INPUT, not a straight read of a chunk snapshot: src/breakwall.js
    // reads `rec.orig.{p,c,u}` — a pre-captured snapshot of a SHARED-RING mesh
    // layout (base ring + roofline ring + roof fan) that citymap.js's building
    // mesh happens to use. This Unity port's City/BuildingBuilder.cs builds
    // walls as unshared PER-EDGE quads instead (4 verts/edge, no shared ring —
    // see its own header comment), so there is no equivalent ring snapshot to
    // read. BuildShell therefore takes the building's own FOOTPRINT RING
    // directly (CityData.Footprint.Flat, the game-frame x/z pairs every wall
    // already extrudes from) plus the already-computed base/top/tint — i.e. the
    // same facts BuildingBuilder.cs derives per building, handed over instead of
    // re-read from a mesh. The cell/carve/crumble MATH below is an exact port;
    // only where the pristine ring comes from differs.
    //
    // HANDEDNESS: footprint vertices arrive in the GAME frame (X=west), exactly
    // like BuildingBuilder.cs's own input, and are mirrored HERE in the same
    // pass that reverses triangle winding (GeoFrame.FlipWinding) — same
    // contract, same place it happens, as the chunk builder.
    // =========================================================================
    public static class BreakWall
    {
        public const float CellW = 1.5f;    // :19
        public const float CellH = 1.6f;    // :20
        public const int MaxCells = 7000;   // :21
        public const float LiningIn = 0.42f; // :22
        public const float SlabStep = 3.2f;  // :23

        public struct WallCell { public float X, Y, Z, K; public int A, B, C, D; }
        public struct LiningCell
        {
            public bool IsSlab;
            public float X, Y, Z, K;
            public int A, B, C, D;
            public int Sa, Sb, Sc;
        }
        public struct BlastHole { public float X, Y, Z, R; }

        public sealed class Shell
        {
            public Mesh Mesh;      // facade — wallMat
            public Mesh Lining;    // interior + floor slabs — linMat
            public readonly List<WallCell> Cells = new List<WallCell>();
            public readonly List<LiningCell> LCells = new List<LiningCell>();
            public float[] OrigP, OrigC, OrigLP; // pristine snapshot, for WriteShellVerts
            public int Count, LCount;
            /// <summary>src/breakwall.js:269 `shell.live` — surviving facade cells after
            /// the last Carve(); a probe-visible proxy for "the hole follows the real
            /// shape of the hit" (QUALITY_BAR's crumble-stage verification spirit).</summary>
            public int Live;
            public readonly List<BlastHole> Holes = new List<BlastHole>();
        }

        /// <summary>What BuildShell needs — the per-building facts BuildingBuilder.cs
        /// already computes, handed over directly (see class header).</summary>
        public struct BuildingWallInput
        {
            public float[] FootprintFlatGameFrame; // x0,z0,x1,z1,... GAME frame, metres
            public float Base, Top;                 // world Y
            public Color32 Tint;
            public float CxGame, CzGame;             // centroid, GAME frame
        }

        /// <summary>src/breakwall.js:28 buildShell.</summary>
        public static Shell BuildShell(BuildingWallInput rec, Material wallMat, Material linMat)
        {
            int n = rec.FootprintFlatGameFrame.Length / 2;
            if (n < 3) return null;
            float baseY = rec.Base, topY = rec.Top;
            float h = Mathf.Max(2f, topY - baseY);
            float cx = -rec.CxGame, cz = rec.CzGame; // mirror X — :76

            var P = new Vector2[n];
            for (int i = 0; i < n; i++)
                P[i] = new Vector2(-rec.FootprintFlatGameFrame[i * 2], rec.FootprintFlatGameFrame[i * 2 + 1]);

            float perim = 0f;
            for (int i = 0; i < n; i++) { int j = (i + 1) % n; perim += Vector2.Distance(P[j], P[i]); }
            float want = (perim / CellW) * (h / CellH);
            float grow = want > MaxCells ? Mathf.Sqrt(want / MaxCells) : 1f;
            float cw = CellW * grow, chh = CellH * grow;

            var pos = new List<Vector3>();
            var col = new List<Color32>();
            var uv = new List<Vector2>();
            var cells = new List<WallCell>();
            var lPos = new List<Vector3>();
            var lCol = new List<Color32>();
            var lCells = new List<LiningCell>();

            float uCursor = 0f;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                float x0 = P[i].x, z0 = P[i].y, x1 = P[j].x, z1 = P[j].y;
                float len = Vector2.Distance(P[j], P[i]);
                if (len < 0.05f) continue;
                float u0 = uCursor, u1 = uCursor + len / 3f;
                uCursor = u1; // the closing edge naturally gets u0+len/3, matching :68's special case
                const float vB = 0f, vT = 1f;

                float nx = -(z1 - z0) / len, nz = (x1 - x0) / len;
                float mx = (x0 + x1) / 2f, mz = (z0 + z1) / 2f;
                if ((mx - cx) * nx + (mz - cz) * nz > 0f) { nx = -nx; nz = -nz; }

                int cols = Mathf.Clamp(Mathf.RoundToInt(len / cw), 1, 28);
                int rows = Mathf.Clamp(Mathf.RoundToInt(h / chh), 1, 28);
                int v0 = pos.Count;
                int lv0 = lPos.Count;

                for (int r = 0; r <= rows; r++)
                {
                    float fy = (float)r / rows;
                    float y = baseY + h * fy;
                    for (int c = 0; c <= cols; c++)
                    {
                        float fx = (float)c / cols;
                        float x = x0 + (x1 - x0) * fx;
                        float z = z0 + (z1 - z0) * fx;
                        pos.Add(new Vector3(x, y, z));
                        uv.Add(new Vector2(u0 + (u1 - u0) * fx, vB + (vT - vB) * fy));
                        // BuildingBuilder.cs tints the base and roofline rings IDENTICALLY (no
                        // grime ramp yet — see its own Color32 tint use), so this ramp currently
                        // has the same colour at both ends; kept (not collapsed) so it starts
                        // working for free the moment a base-grime tint is introduced upstream.
                        col.Add(rec.Tint);
                        lPos.Add(new Vector3(x + nx * LiningIn, y, z + nz * LiningIn));
                        float d = 0.006f + 0.005f * (((c * 7 + r * 13) % 5) / 5f);
                        lCol.Add(new Color32((byte)(d * 255), (byte)(d * 0.95f * 255), (byte)(d * 0.88f * 255), 255));
                    }
                }

                int stride = cols + 1;
                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < cols; c++)
                    {
                        int a = v0 + r * stride + c;
                        float fx = (c + 0.5f) / cols, fy = (r + 0.5f) / rows;
                        cells.Add(new WallCell
                        {
                            X = x0 + (x1 - x0) * fx, Y = baseY + h * fy, Z = z0 + (z1 - z0) * fx,
                            K = 0.72f + 0.5f * (((r * 7919 + c * 104729 + i * 1299721) % 97) / 97f),
                            A = a, B = a + 1, C = a + stride + 1, D = a + stride,
                        });
                        int la = lv0 + r * stride + c;
                        lCells.Add(new LiningCell
                        {
                            X = x0 + (x1 - x0) * fx + nx * LiningIn, Y = baseY + h * fy, Z = z0 + (z1 - z0) * fx + nz * LiningIn,
                            K = 0.8f + 0.4f * (((r * 6151 + c * 24593 + i * 393241) % 89) / 89f),
                            A = la, B = la + 1, C = la + stride + 1, D = la + stride,
                        });
                    }
                }
            }
            if (cells.Count == 0) return null;

            // ---- floor slabs (src/breakwall.js:142) ----
            // Fan triangulation off the centroid, not full ear-clipping — the SAME
            // simplification City/BuildingBuilder.cs's own roof already makes for OSM
            // footprints ("overwhelmingly convex or near-convex"), and literally
            // src/breakwall.js's OWN documented fallback when THREE.ShapeUtils.
            // triangulateShape throws (:158): `for (i=1;i<n-1;i++) tris.push([0,i,i+1])`.
            var contour = new List<Vector2>(n);
            for (int i = 0; i < n; i++)
            {
                float dx = P[i].x - cx, dz = P[i].y - cz;
                float l = Mathf.Sqrt(dx * dx + dz * dz); if (l < 1e-6f) l = 1f;
                float s = Mathf.Max(0f, l - LiningIn - 0.15f) / l;
                contour.Add(new Vector2(cx + dx * s, cz + dz * s));
            }
            float slabTop = topY - 0.6f;
            int floor = 0;
            for (float y = baseY + SlabStep; y < slabTop; y += SlabStep)
            {
                int s0 = lPos.Count;
                float g = 0.02f + 0.006f * (floor++ % 3);
                byte gb = (byte)(g * 255), gb2 = (byte)(g * 0.97f * 255), gb3 = (byte)(g * 0.92f * 255);
                foreach (var p in contour) { lPos.Add(new Vector3(p.x, y, p.y)); lCol.Add(new Color32(gb, gb2, gb3, 255)); }
                for (int i = 1; i < contour.Count - 1; i++)
                    lCells.Add(new LiningCell { IsSlab = true, Sa = s0, Sb = s0 + i, Sc = s0 + i + 1 });
            }

            var mesh = new Mesh { name = "wallshell" };
            mesh.indexFormat = pos.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(pos);
            mesh.SetColors(col);
            mesh.SetUVs(0, uv);
            mesh.triangles = new int[cells.Count * 6];

            var lMesh = new Mesh { name = "wallshell_lining" };
            lMesh.indexFormat = lPos.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            lMesh.SetVertices(lPos);
            lMesh.SetColors(lCol);
            lMesh.triangles = new int[lCells.Count * 6];

            var shell = new Shell
            {
                Mesh = mesh, Lining = lMesh, Count = pos.Count, LCount = lPos.Count,
            };
            shell.Cells.AddRange(cells);
            shell.LCells.AddRange(lCells);
            shell.OrigP = Flatten(pos);
            shell.OrigC = FlattenColor(col);
            shell.OrigLP = Flatten(lPos);
            Carve(shell); // no holes yet — just fills both index buffers (:209)
            return shell;
        }

        static float[] Flatten(List<Vector3> v)
        {
            var o = new float[v.Count * 3];
            for (int i = 0; i < v.Count; i++) { o[i * 3] = v[i].x; o[i * 3 + 1] = v[i].y; o[i * 3 + 2] = v[i].z; }
            return o;
        }
        static float[] FlattenColor(List<Color32> c)
        {
            var o = new float[c.Count * 3];
            for (int i = 0; i < c.Count; i++) { o[i * 3] = c[i].r / 255f; o[i * 3 + 1] = c[i].g / 255f; o[i * 3 + 2] = c[i].b / 255f; }
            return o;
        }

        /// <summary>src/breakwall.js:216 carve — rewrite both index buffers, degenerating
        /// (never compacting — a BVH/MeshCollider indexes this same array) every cell
        /// inside a blast sphere. The lining loses a SMALLER disc than the facade.</summary>
        public static void Carve(Shell shell, List<BlastHole> holes = null)
        {
            holes ??= shell.Holes;
            var idx = new int[shell.Cells.Count * 6];
            int live = 0;
            for (int k = 0; k < shell.Cells.Count; k++)
            {
                var c = shell.Cells[k];
                int o = k * 6;
                bool gone = false;
                foreach (var hl in holes)
                {
                    float dx = c.X - hl.X, dy = c.Y - hl.Y, dz = c.Z - hl.Z;
                    float rr = hl.R * c.K;
                    if (dx * dx + dy * dy + dz * dz < rr * rr) { gone = true; break; }
                }
                if (gone) { for (int q = 0; q < 6; q++) idx[o + q] = c.A; continue; }
                live++;
                idx[o] = c.A; idx[o + 1] = c.B; idx[o + 2] = c.C;
                idx[o + 3] = c.A; idx[o + 4] = c.C; idx[o + 5] = c.D;
            }
            shell.Live = live;

            var lIdx = new int[shell.LCells.Count * 6];
            for (int k = 0; k < shell.LCells.Count; k++)
            {
                var c = shell.LCells[k];
                int o = k * 6;
                if (c.IsSlab)
                {
                    lIdx[o] = c.Sa; lIdx[o + 1] = c.Sb; lIdx[o + 2] = c.Sc;
                    for (int q = 3; q < 6; q++) lIdx[o + q] = c.Sa;
                    continue;
                }
                bool gone = false;
                foreach (var hl in holes)
                {
                    float rr = hl.R * 0.62f * c.K; // the inner skin loses LESS than the facade
                    float dx = c.X - hl.X, dy = c.Y - hl.Y, dz = c.Z - hl.Z;
                    if (dx * dx + dy * dy + dz * dz < rr * rr) { gone = true; break; }
                }
                if (gone) { for (int q = 0; q < 6; q++) lIdx[o + q] = c.A; continue; }
                lIdx[o] = c.A; lIdx[o + 1] = c.B; lIdx[o + 2] = c.C;
                lIdx[o + 3] = c.A; lIdx[o + 4] = c.C; lIdx[o + 5] = c.D;
            }

            // Every vertex above was mirrored (X negated) at the moment it was built —
            // same contract as City/BuildingBuilder.cs's "one flip for the whole chunk,
            // since every vertex was mirrored". Degenerate (carved-away) triples flip to
            // another degenerate triple, so this is safe over the whole array unconditionally.
            GeoFrame.FlipWinding(idx);
            GeoFrame.FlipWinding(lIdx);
            shell.Mesh.triangles = idx;
            shell.Lining.triangles = lIdx;
            // src/breakwall.js never computes normals for this shell (its material is
            // read against position/color/uv only); Unity's URP Lit has no equivalent
            // automatic fallback and would render pitch black without them, which is a
            // regression the quality bar explicitly forbids — so these are computed,
            // deliberately beyond the literal JS, as a strictly-additive fix.
            shell.Mesh.RecalculateNormals();
            shell.Lining.RecalculateNormals();
            shell.Mesh.RecalculateBounds();
            shell.Lining.RecalculateBounds();
        }

        /// <summary>src/breakwall.js:274 writeShellVerts — same damage math the intact
        /// prism shells use (10 crumble stages): kh squashes height, jit buckles, dark
        /// soots, lean shears the whole thing.</summary>
        public static void WriteShellVerts(Shell s, float baseY, float kh, float jit, float dark,
            float leanX, float leanZ, int phase = 0)
        {
            if (s == null) return;
            // Double, not float — see BuildingDamage.Fr's note on why this
            // particular hash cannot survive float32 (q reaches ~43758, where
            // float32's resolution is coarser than the fraction being taken).
            float Fr(double v) { double q = System.Math.Sin(v) * 43758.5453; return (float)(q - System.Math.Floor(q)); }

            var pos = new Vector3[s.Count];
            var col = new Color32[s.Count];
            // height span, recovered from the pristine snapshot (top-most minus base) —
            // src/breakwall.js:277 reads this off `rec.top - rec.base` directly; this port
            // has no separate `rec` carrying that, so it is recovered from the shell itself.
            float relYMax = 0f;
            for (int v = 0; v < s.Count; v++) relYMax = Mathf.Max(relYMax, s.OrigP[v * 3 + 1] - baseY);
            float hgt = Mathf.Max(1f, relYMax);

            for (int v = 0; v < s.Count; v++)
            {
                int o = v * 3;
                float relY = Mathf.Max(0f, s.OrigP[o + 1] - baseY);
                float t = Mathf.Min(1f, relY / hgt);
                float a = jit * (0.35f + 0.65f * t);
                // src/breakwall.js's single `lean` scalar times rec.leanX/leanZ becomes two
                // explicit per-axis lean amounts here (leanX/leanZ already carry `relY * lean`'s
                // scale baked in by the caller, or pass relY-scaled values directly).
                // src/breakwall.js:282-284 — the `f` suffixes below used to force
                // `v * 12.9898f` etc. into float32 BEFORE Fr(double) could widen it,
                // which is exactly the mistake BuildingDamage.Fr's own doc comment
                // warns about ("computing v * 12.9898f in float32 first and widening
                // afterwards reintroduces the same error one step earlier"). Dropping
                // the suffixes lets `int v * double literal` promote to double, like
                // BuildingDamage.WriteBuildingVerts' unsuffixed call sites already do.
                // Proof (Node, v=0..4999): the float32-argument path disagreed with the
                // double path by >0.05 on 4497 of 5000 vertices (worst case ~0.996) —
                // not a rounding edge case, the dominant case.
                float px = s.OrigP[o] + (Fr(v * 12.9898 + phase * 7.31) - 0.5f) * a + relY * leanX;
                float py = baseY + relY * kh + (Fr(v * 78.233 + phase * 3.17) - 0.5f) * a * 0.5f;
                float pz = s.OrigP[o + 2] + (Fr(v * 39.425 + phase * 5.53) - 0.5f) * a + relY * leanZ;
                pos[v] = new Vector3(px, py, pz);
                col[v] = new Color32(
                    (byte)Mathf.Clamp(s.OrigC[o] * dark * 255f, 0, 255),
                    (byte)Mathf.Clamp(s.OrigC[o + 1] * dark * 255f, 0, 255),
                    (byte)Mathf.Clamp(s.OrigC[o + 2] * dark * 255f, 0, 255), 255);
            }
            s.Mesh.vertices = pos;
            s.Mesh.colors32 = col;

            var lpos = new Vector3[s.LCount];
            for (int v = 0; v < s.LCount; v++)
            {
                int o = v * 3;
                float relY = Mathf.Max(0f, s.OrigLP[o + 1] - baseY);
                float lx = s.OrigLP[o] + relY * leanX;
                float ly = baseY + relY * kh;
                float lz = s.OrigLP[o + 2] + relY * leanZ;
                lpos[v] = new Vector3(lx, ly, lz);
            }
            s.Lining.vertices = lpos;
        }
    }
}

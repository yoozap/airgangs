using System.Collections.Generic;
using TbilisiFly.Core;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves Core/CombatWorldAdapter.cs's NearWindow/BlastWindows/PunchHole/
    /// StripPropsAt are real against KNOWN SYNTHETIC footprints — not the real
    /// 17,425-building city (no CityData/BuildingFacts.Load() file I/O here).
    /// Paired with tools/_probe_glasshole.mjs, which prints the SAME KEY=value
    /// lines from src/citymap.js's own window-anchor math (_windowAnchors) and
    /// from a transcribed copy of blastWindows' sort/flatten step — diffed by
    /// hand (same KEY names/shapes on both sides).
    ///
    /// Also proves a bug this port fixed: City/BuildingDamage.BuildTriRanges'
    /// wall/roof split formula assumes citymap.js's SHARED-ring mesh (wall =
    /// 2/3 of a building's vertex range); City/BuildingBuilder.cs actually
    /// builds UNSHARED per-edge wall quads (4 verts/edge), so that formula
    /// mis-splits the last wall edge as "roof" — see CombatWorldAdapter.
    /// CombatBuildingChunk.TriRanges()'s own header. TriRangeFacts() below
    /// proves the FIXED split degenerates the WHOLE original wall quad
    /// (including the edge the old formula would have missed) when a hole is
    /// punched.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.GlassHoleProbe.Run
    /// </summary>
    public static class GlassHoleProbe
    {
        public static void Run()
        {
            WindowAnchorFacts();
            TriRangeFacts();
            BlastOrderFacts();
            ProbeExit.Exit(0);
        }

        // ---- shared synthetic-building geometry (the REAL per-edge-quad wall +
        // roof-fan layout City/BuildingBuilder.cs's Build()/BuildRich() both use —
        // not a stand-in shape) ----

        static Mesh BuildBuildingMesh(float[] footprintGame, float baseY, float top)
        {
            int vc = footprintGame.Length / 2;
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int v = 0; v < vc; v++)
            {
                int w = (v + 1) % vc;
                float ax = -footprintGame[v * 2], az = footprintGame[v * 2 + 1]; // mirror X
                float bx = -footprintGame[w * 2], bz = footprintGame[w * 2 + 1];
                var p0 = new Vector3(ax, baseY, az);
                var p1 = new Vector3(bx, baseY, bz);
                var p2 = new Vector3(bx, top, bz);
                var p3 = new Vector3(ax, top, az);
                int b = verts.Count;
                verts.Add(p0); verts.Add(p1); verts.Add(p2); verts.Add(p3);
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
            }
            float ccx = 0f, ccz = 0f;
            for (int v = 0; v < vc; v++) { ccx += -footprintGame[v * 2]; ccz += footprintGame[v * 2 + 1]; }
            ccx /= vc; ccz /= vc;
            int centre = verts.Count;
            verts.Add(new Vector3(ccx, top, ccz));
            int ring = verts.Count;
            for (int v = 0; v < vc; v++) verts.Add(new Vector3(-footprintGame[v * 2], top, footprintGame[v * 2 + 1]));
            for (int v = 0; v < vc; v++) { tris.Add(centre); tris.Add(ring + v); tris.Add(ring + (v + 1) % vc); }

            var mesh = new Mesh { name = "probe_building" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.triangles = tris.ToArray();
            mesh.RecalculateBounds();
            return mesh;
        }

        static CombatBuildingChunk MakeChunk(string name, float[] footprintGame, float baseY, float top)
        {
            var mesh = BuildBuildingMesh(footprintGame, baseY, top);
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            var chunk = go.AddComponent<CombatBuildingChunk>();
            int vc = footprintGame.Length / 2;
            chunk.RangeStart = new[] { 0 };
            chunk.RangeCount = new[] { vc * 4 + vc + 1 };
            chunk.Heights = new[] { top - baseY };
            chunk.BuildingIndex = new[] { 0 };
            chunk.WallBase = new[] { baseY };
            chunk.WallTop = new[] { top };
            chunk.ShellTint = new[] { new Color32(190, 185, 178, 255) };
            chunk.ShellWallBucket = new[] { -1 };
            chunk.FootprintFlat = (float[])footprintGame.Clone();
            chunk.FootprintOffset = new[] { 0 };
            chunk.FootprintCount = new[] { vc };
            return chunk;
        }

        // ---- City/BuildingDamage.WindowAnchors' position math, plus the per-edge
        // normal this probe needs and that method discards (CombatWorldAdapter.
        // ComputeWindowAnchors is the exact same duplication the shipped code uses). ----
        static void WindowAnchorFacts()
        {
            // 20 x 12 m rectangle, GAME frame, one storey 12.8 m tall — 3.2 m floor
            // grid gives bands at f=1,2 (2 bands; 12/3.2=3.75 -> nF=3, safely clear
            // of any float32-vs-float64 floor-boundary ambiguity).
            float[] fp = { 0f, 0f, 20f, 0f, 20f, 12f, 0f, 12f };
            float baseY = 0f, top = 12.0f;
            var ringUnity = new float[fp.Length];
            for (int i = 0; i < fp.Length; i += 2) { ringUnity[i] = -fp[i]; ringUnity[i + 1] = fp[i + 1]; }
            float cx = -10f, cz = 6f; // unity-mirrored centroid of the rectangle above
            var pos = new List<Vector3>();
            var nrm = new List<float>();
            CombatWorldAdapter.ComputeWindowAnchors(ringUnity, 4, baseY, top, cx, cz, (x, z) => null, pos, nrm);
            Debug.Log($"WINANCH id=rect20x12 count={pos.Count} nrmLen={nrm.Count} strideOk={(nrm.Count == pos.Count * 2 ? 1 : 0)}");
            if (pos.Count > 0)
            {
                var p0 = pos[0];
                float nx0 = nrm[0], nz0 = nrm[1];
                float nlen = Mathf.Sqrt(nx0 * nx0 + nz0 * nz0);
                Debug.Log($"WINANCH id=rect20x12_first x={p0.x:F4} y={p0.y:F4} z={p0.z:F4} nx={nx0:F4} nz={nz0:F4} nlen={nlen:F4}");
            }
        }

        // ---- the TriRanges() fix: a hole must degenerate the WHOLE original wall
        // quad, including the last edge the citymap.js-derived formula would have
        // misclassified as roof. ----
        static void TriRangeFacts()
        {
            float[] fp = { -5f, -5f, 5f, -5f, 5f, 5f, -5f, 5f }; // 10x10 m square, 4 corners
            var chunk = MakeChunk("probe_trirange", fp, 0f, 10f);
            var fallback = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            var lining = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Standard"));

            // a hole mid-height, well short of the roofline (top=10, hole r=2 ->
            // guard `hy < top-r` = 5<8 true -> roof must stay untouched)
            var hole = chunk.PunchHole(0, new Vector3(5f, 5f, 0f), 2f, fallback, lining);
            var finalTris = chunk.GetComponent<MeshFilter>().sharedMesh.triangles;
            // triangle0 = (0,1,2) at ints[0..3) — well inside ANY wallTop estimate.
            bool tri0Degenerate = finalTris[0] == finalTris[1] && finalTris[1] == finalTris[2];
            // triangle7 = (12,14,15) at ints[21..24) — edge3's SECOND triangle, the
            // one the citymap.js-derived "2/3 of the range" formula puts past its
            // (too-low) wallTop and so would NEVER degenerate. Only the real
            // 4*vc=16 threshold catches it.
            bool tri7Degenerate = finalTris[21] == finalTris[22] && finalTris[22] == finalTris[23];
            // roof triangle0 = (16,17,18) at ints[24..27) — must be UNTOUCHED (the
            // hole doesn't reach the roofline).
            bool roofIntact = !(finalTris[24] == finalTris[25] && finalTris[25] == finalTris[26]);
            Debug.Log($"TRIRANGE id=square4 holeBuilt={(hole.HasValue ? 1 : 0)} " +
                      $"tri0Degenerate={(tri0Degenerate ? 1 : 0)} tri7Degenerate={(tri7Degenerate ? 1 : 0)} " +
                      $"roofIntact={(roofIntact ? 1 : 0)}");
            Object.DestroyImmediate(chunk.gameObject);
        }

        // ---- BlastWindows/StripPropsAt: pane count, 5-wide stride, nearest-first,
        // and permanent consumption (a pane returned once is never returned again). ----
        static void BlastOrderFacts()
        {
            // 40 x 10 m wall, GAME frame, 9 m tall (9/3.2=2.8125 -> nF=2, one band at
            // f=1 — chosen clear of any floor-boundary ambiguity, same reason as above).
            float[] fp = { 0f, 0f, 40f, 0f, 40f, 10f, 0f, 10f };
            var chunk = MakeChunk("probe_blastwall", fp, 0f, 9.0f);

            var adapter = new CombatWorldAdapter(null, null); // no terrain — WindowHeightAt(_terrain==null) always null, so no anchor is "buried"

            // blast centred on the long unity-mirrored wall (x in [-40,0], z~0),
            // a generous 12 m radius so it catches several bays at different distances.
            float bx = -20f, by = 4.8f, bz = 0f, radius = 12f;
            var blown = adapter.BlastWindows(bx, by, bz, radius);
            bool strideOk = blown.Length % 5 == 0;
            int paneCount = blown.Length / 5;
            bool nearestFirst = true;
            float prevD2 = -1f;
            for (int i = 0; i < paneCount; i++)
            {
                int o = i * 5;
                float dx = blown[o] - bx, dy = blown[o + 1] - by, dz = blown[o + 2] - bz;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 + 1e-4f < prevD2) nearestFirst = false;
                prevD2 = d2;
            }
            Debug.Log($"BLASTWIN id=wall40x10 len={blown.Length} strideOk={(strideOk ? 1 : 0)} paneCount={paneCount} any={(paneCount > 0 ? 1 : 0)} nearestFirst={(nearestFirst ? 1 : 0)}");

            // the SAME blast again: every pane it already broke must be gone for
            // good — src/citymap.js:2371's `arr.fill(0,...)` permanently zeroes an
            // instance; this port's `broken` mask is the equivalent.
            var again = adapter.StripPropsAt(bx, by, bz, radius);
            Debug.Log($"BLASTWIN id=wall40x10_restrip len={again.Length} consumedForGood={(again.Length == 0 ? 1 : 0)}");

            // NearWindow: a broken pane's own position no longer reads as "near a
            // window" (it is gone); an UNTOUCHED building far away still does.
            object rec = (chunk, 0);
            bool nearBroken = paneCount > 0 && adapter.NearWindow(null, rec, new Vector3(blown[0], blown[1], blown[2]));

            float[] fp2 = { 200f, 0f, 220f, 0f, 220f, 10f, 200f, 10f };
            var chunk2 = MakeChunk("probe_untouched", fp2, 0f, 9.0f);
            object rec2 = (chunk2, 0);
            var (pos2, _, _) = chunk2.WindowDataOf(0, (x, z) => null);
            bool nearUntouched = pos2.Count > 0 && adapter.NearWindow(null, rec2, pos2[0]);
            Debug.Log($"BLASTWIN id=nearwindow nearBrokenPaneIsFalse={(!nearBroken ? 1 : 0)} nearUntouchedPaneIsTrue={(nearUntouched ? 1 : 0)}");

            Object.DestroyImmediate(chunk.gameObject);
            Object.DestroyImmediate(chunk2.gameObject);
        }
    }
}

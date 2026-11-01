using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// C# half of the airport.js/roads.js port check for this pass. Mirrors
    /// tools/_probe_airroads.mjs EXACTLY — same KEY names, same synthetic
    /// inputs, same shapes — per the task brief's own warning that a stray
    /// prefix or an extra field stops a line from pairing with its JS
    /// counterpart and the comparison then verifies nothing.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.AirRoadsProbe.Run
    ///        -logFile /dev/stdout
    ///
    /// PART 1 calls the REAL BridgeBuilder.BuildSpans (City/BridgeBuilder.cs,
    /// not owned by this unit — already covers src/roads.js:146-465, see
    /// docs/port_units/roads.json) on a synthetic flat-terrain scene, so this
    /// is an integration check of the already-ported algorithm, not a
    /// reimplementation of it.
    ///
    /// PART 2 calls the REAL AirportDetail.ClassifyRangeMeshes/
    /// BuildCounterBlocks (City/AirportDetail.cs, this unit's own new Section
    /// E, src/airport.js:564-738/820-911) on synthetic GameObjects/meshes, so
    /// no real .fbx asset needs to be imported for this check to run.
    /// </summary>
    public static class AirRoadsProbe
    {
        public static void Run()
        {
            try
            {
                RunSpans();
                RunRangeClassify();
                RunRangeBlocks();
                ProbeExit.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"AIRROADSPROBE_FAIL {e}");
                ProbeExit.Exit(1);
            }
        }

        // =====================================================================
        // PART 1 — see tools/_probe_airroads.mjs's own header for the full
        // scenario description: one east-west road crossed by one north-south
        // bridge over flat (height=100) terrain, no bespoke zones.
        // =====================================================================
        static void RunSpans()
        {
            var heights = new float[16];
            for (int i = 0; i < heights.Length; i++) heights[i] = 100f;
            var terrain = new TerrainSampler(4, 4, 1000f, -2000f, -2000f, heights, new byte[16]);

            var roads = new List<RoadEntry> { new RoadEntry { Width = 8f, Flat = new float[] { -50f, 0f, 50f, 0f } } };
            var bridges = new List<RoadEntry> { new RoadEntry { Width = 10f, Flat = new float[] { 0f, -100f, 0f, 100f } } };
            var bridgeMeta = new Dictionary<int, RoadMeta> { [0] = new RoadMeta { Layer = 1 } };
            var zones = new List<RealBridgeZone>();

            var result = BridgeBuilder.BuildSpans(terrain, bridges, bridgeMeta, roads, zones);

            int deckVerts = result.Deck != null ? result.Deck.vertexCount : 0;
            float deckMinY = 0f, deckMaxY = 0f;
            if (result.Deck != null) { deckMinY = result.Deck.bounds.min.y; deckMaxY = result.Deck.bounds.max.y; }
            int concVerts = result.Structure != null ? result.Structure.vertexCount : 0;

            Debug.Log($"AIRROADSPROBE_SPANS deckVerts={deckVerts} deckMinY={deckMinY:F3} deckMaxY={deckMaxY:F3} " +
                      $"concVerts={concVerts} nStruct={result.StructBays} nPier={result.Piers}");
        }

        // =====================================================================
        // PART 2a — AirportDetail.ClassifyRangeMeshes on the SAME 5 node names
        // tools/_probe_airroads.mjs transcribes from airport.js:584-600.
        // =====================================================================
        static void RunRangeClassify()
        {
            string[] names =
            {
                "SR_Divider_Door_dup_4.Shape_0",
                "SR_Divider_Door_dup_4.Shape_1",
                "SR Target Holder_dup_3.Shape_Target Paper_0",
                "SR Target Holder_dup_3.Shape_Holder_0",
                "Range_Divider_Shape_Counter_02",
            };

            var root = new GameObject("range_classify_root");
            foreach (var n in names)
            {
                var go = new GameObject(n);
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>(); // ClassifyRangeMeshes only needs the component to exist
            }

            var classified = AirportDetail.ClassifyRangeMeshes(root.transform);
            int papers = 0;
            foreach (var lp in classified.LaneParts) if (lp.IsPaper) papers++;

            Debug.Log($"AIRROADSPROBE_RANGE_CLASSIFY gates={classified.Gates.Count} " +
                      $"laneparts={classified.LaneParts.Count} papers={papers} counters={classified.Counters.Count}");

            Object.DestroyImmediate(root);
        }

        // =====================================================================
        // PART 2b — AirportDetail.BuildCounterBlocks on ONE synthetic "counter"
        // mesh made of two real 3-D boxes (slab A: x -10..-2, slab B: x
        // -0.6..7.4; both y 0..1.05, z -0.3..0.3) 0.6 m apart — the same gap
        // tools/_probe_airroads.mjs's PART 2 uses, which is wider than the
        // 0.05 m merge tolerance, so BuildCounterBlocks must keep them as TWO
        // segments, each then widened to the synthetic pitch (24.8 m) at
        // whichever end of the pair it sits on.
        // =====================================================================
        static void RunRangeBlocks()
        {
            const float pitch = 24.8f;
            var mesh = CombineBoxes(
                (new Vector3(-10f, 0f, -0.3f), new Vector3(-2f, 1.05f, 0.3f)),
                (new Vector3(-0.6f, 0f, -0.3f), new Vector3(7.4f, 1.05f, 0.3f)));

            var counterGo = new GameObject("counter");
            counterGo.transform.position = Vector3.zero;
            counterGo.transform.rotation = Quaternion.identity;
            counterGo.transform.localScale = Vector3.one;
            var mf = counterGo.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;

            var blocks = AirportDetail.BuildCounterBlocks(new List<Transform> { counterGo.transform }, pitch);
            Debug.Log($"AIRROADSPROBE_RANGE_BLOCKS n={blocks.Count}");
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                Debug.Log($"AIRROADSPROBE_RANGE_BLOCK i={i} cx={b.Center.x:F3} cy={b.Center.y:F3} cz={b.Center.z:F3} " +
                          $"sx={b.Size.x:F3} sy={b.Size.y:F3} sz={b.Size.z:F3}");
            }

            Object.DestroyImmediate(counterGo);
        }

        static Mesh MakeBoxMesh(Vector3 min, Vector3 max)
        {
            var v = new Vector3[8];
            v[0] = new Vector3(min.x, min.y, min.z); v[1] = new Vector3(max.x, min.y, min.z);
            v[2] = new Vector3(max.x, min.y, max.z); v[3] = new Vector3(min.x, min.y, max.z);
            v[4] = new Vector3(min.x, max.y, min.z); v[5] = new Vector3(max.x, max.y, min.z);
            v[6] = new Vector3(max.x, max.y, max.z); v[7] = new Vector3(min.x, max.y, max.z);
            int[] tris =
            {
                0,1,2, 0,2,3, // bottom
                4,6,5, 4,7,6, // top
                0,4,5, 0,5,1,
                1,5,6, 1,6,2,
                2,6,7, 2,7,3,
                3,7,4, 3,4,0,
            };
            var mesh = new Mesh();
            mesh.vertices = v;
            mesh.triangles = tris;
            return mesh;
        }

        /// <summary>Concatenates several boxes' triangles into one Mesh — only
        /// the per-triangle bounding boxes matter to BuildCounterBlocks, not
        /// winding/normals, so no further processing is needed.</summary>
        static Mesh CombineBoxes(params (Vector3 min, Vector3 max)[] boxes)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            foreach (var (mn, mx) in boxes)
            {
                var bm = MakeBoxMesh(mn, mx);
                int baseIdx = verts.Count;
                verts.AddRange(bm.vertices);
                foreach (var t in bm.triangles) tris.Add(t + baseIdx);
            }
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            return mesh;
        }
    }
}

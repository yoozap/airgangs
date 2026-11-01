using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Synthetic-road counterpart to RoadProbe.cs (which diffs the REAL
    /// roadnet against the REAL terrain). That probe already proves the drape
    /// height agrees with TerrainSampler to the centimetre on real data; what
    /// it does not isolate is the KERB SIDE — citymap.js:6650-6651's cross
    /// product `px = -a.y*w, pz = a.x*w` decides which side of the
    /// direction-of-travel the sidewalk/verge lands on, and per
    /// .claude/skills/unity/SKILL.md, "cross products are component-identical
    /// but geometrically mirrored, so operands must be SWAPPED" — a mistake
    /// there would not show up as a drape error (height stays right), only as
    /// a kerb on the wrong side, which real-data triangle counts cannot catch.
    ///
    /// Builds ONE synthetic road (width 16 m, straight segment (-20,0)-(20,0)
    /// in the GAME frame) over a SADDLE terrain cell (h00+h11 != h10+h01, the
    /// one shape where "triangle" and "bilinear" sampling disagree — see
    /// TerrainSampler.cs's own doc comment) and runs it through the REAL,
    /// unedited RoadBuilder.BuildRoadDeck. Same scenario, same key names/
    /// shapes, as tools/_probe_cityroads.mjs — diff the two outputs by hand
    /// (booleans are already lower-case via the PASS/FAIL string literals
    /// below, sidestepping the bool.ToString() "True" vs "true" trap the
    /// skill doc calls out).
    ///
    /// FINDING THIS PROBE EXISTS TO RECORD: `_buildRoads` (citymap.js:6597-
    /// 6868) is NOT an unclaimed gap in this port. RoadBuilder.cs's own class
    /// doc and "ROAD DECK" section header already claim it verbatim
    /// (BuildRoadDeck), CityLoader.cs already wires it in at the same load
    /// stage citymap.js does, and RoadProbe.cs already drape-checks it against
    /// real data. The manifest's roads.js unit note says "ALSO covers
    /// src/citymap.js:6597-6868" explicitly — it is filed under
    /// `js: "roads.js"` (RoadBuilder.cs's primary source file) rather than
    /// `js: "citymap.js"`, which is why a gap-measurement script filtering
    /// manifest units by `js === "citymap.js"` exactly reports a false gap
    /// here; docs/port_units/airroads.json already documents this exact
    /// failure mode once before, for roads.js/bridges.js's buildSpans split.
    /// This probe exists to verify that "ALSO covers" claim independently
    /// rather than take the note's word for it, plus supply the one check
    /// (kerb side) neither existing roads probe isolates. NO NEW GEOMETRY CODE
    /// WAS WRITTEN FOR THIS PASS — RoadBuilder.cs (off limits to edit per the
    /// port brief) is called exactly as CityLoader.cs calls it.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CityRoadsProbe.Run
    /// </summary>
    public static class CityRoadsProbe
    {
        public static void Run()
        {
            // ---- the same saddle cell tools/_probe_cityroads.mjs builds ----
            var terrain = new TerrainSampler(2, 2, 60f, -30f, -30f,
                new float[] { 0f, 10f, 10f, 0f }, new byte[4]);

            float triA = terrain.HeightAtGame(-20f, -8f);
            // Independent re-derivation of the BILINEAR value (NOT what
            // TerrainSampler.HeightAtGame computes) just to prove this saddle
            // cell actually distinguishes the two — if it didn't, a silent
            // regression to bilinear sampling would pass this probe by luck.
            float fx = Mathf.Clamp((-20f - (-30f)) / 60f, 0f, 2f - 1.001f);
            float fz = Mathf.Clamp((-8f - (-30f)) / 60f, 0f, 2f - 1.001f);
            float bilinearA = 0f * (1 - fx) * (1 - fz) + 10f * fx * (1 - fz) + 10f * (1 - fx) * fz + 0f * fx * fz;
            bool divergent = Mathf.Abs(triA - bilinearA) > 0.5f;

            Debug.Log($"CRR id=terrain_check tri={triA:F4} bilinear={bilinearA:F4} divergent={(divergent ? "true" : "false")}");

            // ---- the one synthetic road: width 16 m, (-20,0) -> (20,0) ----
            var road = new RoadEntry { Width = 16f, Flat = new float[] { -20f, 0f, 20f, 0f } };
            var roads = new List<RoadEntry> { road };

            var deck = RoadBuilder.BuildRoadDeck(terrain, roads, null);

            bool deckOk = deck.Asphalt != null && deck.Asphalt.vertexCount >= 4;
            bool sideOk = deck.Sidewalk != null && deck.Sidewalk.vertexCount >= 8;
            Debug.Log($"CRR id=mesh asphalt_verts={(deck.Asphalt?.vertexCount ?? 0)} " +
                      $"sidewalk_verts={(deck.Sidewalk?.vertexCount ?? 0)} " +
                      $"deckBuilt={(deckOk ? "true" : "false")} sidewalkBuilt={(sideOk ? "true" : "false")}");

            bool deckHeightOk = true;
            if (deckOk)
            {
                var verts = deck.Asphalt.vertices;
                for (int i = 0; i < 4; i++)
                {
                    var v = verts[i];
                    Debug.Log($"CRR id=deck v={i} x={v.x:F4} y={v.y:F4} z={v.z:F4}");
                }
                // v0 is the only one whose game-frame (x,z) is known in closed
                // form without re-deriving px/pz here (it is exactly
                // (ax-px, az-pz) = (-20, -8) un-mirrored): un-mirror its own
                // x back to the game frame and check it rides the TRIANGLE
                // sample + the deck's own 0.8 m band, matching RoadBuilder.cs's
                // formula and RoadProbe.cs's existing drape check.
                float gx0 = -verts[0].x, gz0 = verts[0].z;
                float expectedY0 = terrain.HeightAtGame(gx0, gz0) + 0.8f;
                deckHeightOk = Mathf.Abs(verts[0].y - expectedY0) < 1e-3f;
            }

            bool southIsNegZ = true, northIsPosZ = true;
            if (sideOk)
            {
                var sv = deck.Sidewalk.vertices;
                for (int i = 0; i < 8; i++)
                    Debug.Log($"CRR id=sidewalk v={i} x={sv[i].x:F4} y={sv[i].y:F4} z={sv[i].z:F4}");
                // sgn=-1 (south) is vertices 0..3, sgn=+1 (north) is 4..7 —
                // citymap.js:6679's own `for (const sgn of [-1, 1])` order,
                // matched exactly by RoadBuilder.cs's `static readonly int[]
                // Sgn = { -1, 1 };`. Z is NEVER mirrored (GeoFrame only flips
                // X), so this is a direct sign check, no un-mirroring needed.
                for (int i = 0; i < 4; i++) if (sv[i].z >= 0f) southIsNegZ = false;
                for (int i = 4; i < 8; i++) if (sv[i].z <= 0f) northIsPosZ = false;
            }

            Debug.Log($"CRR id=assert_deckHeight result={(deckHeightOk ? "PASS" : "FAIL")}");
            Debug.Log($"CRR id=assert_kerbSouthNegZ result={(southIsNegZ ? "PASS" : "FAIL")}");
            Debug.Log($"CRR id=assert_kerbNorthPosZ result={(northIsPosZ ? "PASS" : "FAIL")}");
            bool overall = deckOk && sideOk && deckHeightOk && southIsNegZ && northIsPosZ && divergent;
            Debug.Log($"CRR id=assert_overall result={(overall ? "PASS" : "FAIL")}");

            ProbeExit.Exit(0);
        }
    }
}

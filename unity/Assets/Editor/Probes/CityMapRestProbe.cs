using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves BuildingDamage/DebrisField/CollidableHash/TerrainGridOps (the
    /// citymap.js:1235-3165 "building damage / debris / terrain grid ops" port)
    /// against the real numbers tools/_probe_citymaprest.mjs reads straight out
    /// of src/citymap.js. Every KEY printed here has a matching line from the
    /// Node probe — diff them.
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CityMapRestProbe.Run
    /// </summary>
    public static class CityMapRestProbe
    {
        public static void Run()
        {
            // ---- 1. constants, read straight off the public fields -----------
            Debug.Log($"STAGE_H={string.Join(",", Fmt(BuildingDamage.StageH))}");
            Debug.Log($"I_STAGE_H={string.Join(",", Fmt(BuildingDamage.IStageH))}");
            Debug.Log($"MAX_RESTORED_SHELLS={BuildingDamage.MaxRestoredShells}");
            Debug.Log($"DMG_KEY={BuildingDamage.DmgKey}");
            Debug.Log($"HOLE_KEY={BuildingDamage.HoleKey}");
            Debug.Log($"DMG_SCALE_KEY={BuildingDamage.DmgScaleKey}");
            Debug.Log($"DMG_SCALE={BuildingDamage.DmgScale}");
            Debug.Log($"COLLIDABLES_CELL={CollidableHash<object>.Cell:F0}");
            Debug.Log($"COVER_TUNNELS_TUBE_H={TerrainGridOps.TubeH:F0}");
            Debug.Log($"COVER_TUNNELS_COVER={TerrainGridOps.Cover:F0}");
            Debug.Log($"COVER_TUNNELS_MOUTH={TerrainGridOps.Mouth:F0}");
            Debug.Log($"DEBRIS_GRAVITY={DebrisField.Gravity:F0}");

            // ---- 2. StageOf ----------------------------------------------------
            foreach (var (hp, maxHp) in new (float, float)[] { (500, 500), (480, 500), (300, 500), (1, 500), (0, 500), (250, 500) })
                Debug.Log($"STAGEOF_{(int)hp}_{(int)maxHp}={BuildingDamage.StageOf(hp, maxHp)}");

            // ---- 3. LeanAxis + StageParams + WriteBuildingVerts ----------------
            BuildingDamage.LeanAxis(7, out float leanX, out float leanZ);
            Debug.Log($"LEANAXIS_7={leanX:F6},{leanZ:F6}");

            BuildingDamage.StageParams(4, out float kh, out float jit, out float dark, out float lean);
            Debug.Log($"STAGEPARAMS_4={kh:F6},{jit:F6},{dark:F6},{lean:F6}");

            {
                // same synthetic single-vertex building as the Node probe:
                // base=100, top=112, vertex global index 1000, orig (10,108,-5),
                // orig color 0.8 (all channels), stage 4.
                var origPos = new float[] { 10f, 108f, -5f };
                var origCol = new float[] { 0.8f, 0.8f, 0.8f };
                var outPos = new float[1001 * 3 + 3];
                var outCol = new float[1001 * 3 + 3];
                // v0=1000 so (v-v0)*3 == 0 indexes into the 1-vertex orig arrays
                BuildingDamage.WriteBuildingVerts(origPos, origCol, outPos, outCol,
                    1000, 1001, 100f, 112f, kh, jit, dark, lean, leanX, leanZ, 0);
                Debug.Log($"WRITEVERT_V1000_STAGE4={outPos[3000]:F6},{outPos[3001]:F6},{outPos[3002]:F6},{outCol[3000]:F6}");
            }

            // C# bool.ToString() gives "True"; JS prints "true". The casing
            // alone stopped these two lines pairing.
            Debug.Log($"BORE_CELL={BoreField.Cell:F0}");

            // ---- 4. CarveRoof ----------------------------------------------------
            {
                float holeR = 3f;
                float r2 = (holeR * 1.15f) * (holeR * 1.15f);
                Debug.Log($"CARVEROOF_R2={r2:F6}");
                // one synthetic roof triangle whose pristine centroid is exactly
                // 1 m from the hole centre (inside) and a second test at 4 m (outside)
                var origPosLocal = new float[] { 0f, 100f, 1f,  0f, 100f, 1f,  0f, 100f, 1f }; // 3 verts, centroid (0,100,1)
                var idx = new int[] { 0, 1, 2 };
                bool insideAt1m = BuildingDamage.CarveRoof(idx, origPosLocal, 0, 0, 3, 0f, 100f, 0f, holeR);
                Debug.Log($"CARVEROOF_INSIDE_AT_1M={insideAt1m.ToString().ToLowerInvariant()}");

                var origPosLocal2 = new float[] { 0f, 100f, 4f, 0f, 100f, 4f, 0f, 100f, 4f }; // centroid (0,100,4)
                var idx2 = new int[] { 0, 1, 2 };
                bool insideAt4m = BuildingDamage.CarveRoof(idx2, origPosLocal2, 0, 0, 3, 0f, 100f, 0f, holeR);
                Debug.Log($"CARVEROOF_INSIDE_AT_4M={insideAt4m.ToString().ToLowerInvariant()}");
            }

            // ---- 5. HP-table migration -------------------------------------------
            {
                float h = 24f, area = Mathf.Max(1f, 18f * 22f * 0.8f);
                float oldMax = BuildingDamage.OldMaxHp(h, area);
                float scaled = BuildingDamage.RescaleHp(150f, oldMax, 900f);
                Debug.Log($"OLDMAXHP_H24_AREA={oldMax:F6}");
                Debug.Log($"RESCALEHP_150_900={scaled:F0}");
            }

            // ---- 6. MoundAnchors --------------------------------------------------
            {
                var pts = DebrisField.MoundAnchors(-10f, 10f, -8f, 8f, 90f, 102f, 42);
                Debug.Log($"MOUNDANCHORS_N={pts.Length}");
                Debug.Log($"MOUNDANCHORS_PT0={pts[0].x:F6},{pts[0].y:F6},{pts[0].z:F6}");
                var last = pts[pts.Length - 1];
                Debug.Log($"MOUNDANCHORS_LASTPT={last.x:F6},{last.y:F6},{last.z:F6}");
            }

            // ---- 7. CarveWater, same tiny synthetic grid as the Node probe -------
            {
                int w = 5, h = 5;
                var heights = new float[]
                {
                    10,10,10,10,10,
                    10,10,10,10,10,
                    10,10,50,10,10,
                    10,10,10,10,10,
                    10,10,10,10,10,
                };
                var classes = new byte[w * h];
                classes[12] = 2;
                var sampler = new TerrainSampler(w, h, 60f, 0f, 0f, heights, classes);
                TerrainGridOps.CarveWater(sampler);
                Debug.Log($"CARVEWATER_CENTRE={sampler.Heights[12]:F6}");
            }

            // ---- 8. Debris physics step, no RNG involved --------------------------
            {
                var p = new DebrisField.Particle
                {
                    Pos = new Vector3(0f, 10f, 0f),
                    Vel = new Vector3(2f, 1f, -3f),
                };
                DebrisField.Step(ref p, 1f / 30f, (x, z) => -1000f, out _, out _); // ground far below -> no settle
                Debug.Log($"DEBRISSTEP1={p.Pos.x:F6},{p.Pos.y:F6},{p.Pos.z:F6},{p.Vel.y:F6}");
            }

            Debug.Log("CITYMAPREST_OK=1");
            ProbeExit.Exit(0);
        }

        static string[] Fmt(float[] a)
        {
            var o = new string[a.Length];
            for (int i = 0; i < a.Length; i++) o[i] = a[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            return o;
        }
    }
}

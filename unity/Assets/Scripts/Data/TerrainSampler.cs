using UnityEngine;

namespace TbilisiFly.Data
{
    /// <summary>
    /// The ground height, ported from src/citymap.js `_sampleGrid` / `heightAt`.
    ///
    /// Two things here are load-bearing and were both bugs in the JS build:
    ///
    /// 1. HALF-RES GRID. city.json ships an 833x815 grid at 30 m, but the JS
    ///    samples AND renders a half-res grid (417x408 at 60 m), taking every
    ///    second sample. Sampling the full-res grid while drawing the half-res
    ///    one puts roads and buildings on a surface that is not the one on
    ///    screen. Same grid in, same grid out — see citymap.js:3049.
    ///
    /// 2. TRIANGLE, NOT BILINEAR. The terrain mesh is drawn as triangles split
    ///    on the h10..h01 diagonal, so the height must come from that plane.
    ///    Bilinear differs by dx*dz*(h00+h11-h10-h01), which on a 60 m saddle
    ///    cell in the Tbilisi hills is METRES, and negative exactly where the
    ///    ground bulges. In the JS build that laid the road ribbon up to 3.9 m
    ///    under the visible surface — "the grass swallowed the asphalt", on
    ///    2.3% of city road samples. See citymap.js:3178.
    ///
    /// HANDEDNESS: callers pass UNITY coordinates. The grid is stored in the game
    /// frame (X=west), so x is mirrored on the way in — see GeoFrame.
    /// </summary>
    public class TerrainSampler
    {
        public readonly int W, H;
        public readonly float Step, MinX, MinZ;
        public readonly float[] Heights;   // metres
        public readonly byte[] Classes;    // 0 bare earth, 1 grass/woods, 2 water

        public TerrainSampler(int w, int h, float step, float minX, float minZ,
                              float[] heights, byte[] classes)
        {
            W = w; H = h; Step = step; MinX = minX; MinZ = minZ;
            Heights = heights; Classes = classes;
        }

        /// <summary>
        /// Build the half-res sampler grid from city.json's full-res payload,
        /// exactly as citymap.js _buildTerrainSampler does.
        ///
        /// INCLUDING ITS LAST LINE, citymap.js:3068 `this._carveWater()`.
        /// That call had NO runtime caller in this port — City/TerrainGridOps
        /// .CarveWater was written, probe-verified against a synthetic grid by
        /// Editor/Probes/CityMapRestProbe, and never reached (the exact
        /// "a probe proves a CALCULATION, not that it is REACHED" shape in
        /// .claude/skills/unity/SKILL.md). Without it, every water-class cell
        /// keeps its raw DEM height, and since the DEM is a 60 m grid over a
        /// ~90 m channel the Mtkvari's bed sits ABOVE the measured water
        /// surface over much of its width and pokes straight through the river
        /// sheet — drawn in TerrainBuilder.WaterHex #4d7fb5, which is the
        /// bright pale-blue channel of docs/open_items.json
        /// `river-blue-channel`.
        ///
        /// IT BELONGS HERE AND NOT IN A RENDERER: several renderers call
        /// CityData.Load() for their own sampler, and
        /// Editor/BuildPlayableScene.cs bakes the terrain tiles from yet
        /// another one. Carving in any one of them would drape the road deck,
        /// the grass sheet, the cut-terrain patch and the baked tiles on
        /// DIFFERENT surfaces — structurally the same defect as the
        /// "roads under grass" one CityGroundData.cs's class doc describes.
        /// This is the one function every one of those paths goes through.
        /// The fully-qualified TbilisiFly.City reference is deliberate: the
        /// grid op lives with the rest of the city terrain code and is not
        /// worth moving for one call.
        /// </summary>
        public static TerrainSampler FromFullRes(int w, int h, float step,
                                                 float minX, float minZ,
                                                 ushort[] full, byte[] clsFull)
        {
            int w2 = (w - 1) / 2 + 1;
            int h2 = (h - 1) / 2 + 1;
            var heights = new float[w2 * h2];
            var classes = new byte[w2 * h2];
            for (int r = 0; r < h2; r++)
            {
                int fr = Mathf.Min(r * 2, h - 1);
                for (int c = 0; c < w2; c++)
                {
                    int fc = Mathf.Min(c * 2, w - 1);
                    heights[r * w2 + c] = full[fr * w + fc] / 10f; // decimetres
                    if (clsFull != null) classes[r * w2 + c] = clsFull[fr * w + fc];
                }
            }
            var s = new TerrainSampler(w2, h2, step * 2f, minX, minZ, heights, classes);
            if (CarveWaterOnLoad) TbilisiFly.City.TerrainGridOps.CarveWater(s); // citymap.js:3068
            return s;
        }

        /// <summary>citymap.js:3068's `_carveWater`, on by default because the
        /// JS has no way to turn it off. SETTABLE only so a probe can build the
        /// uncarved grid beside the carved one in ONE load and print both —
        /// same reason, same default, as City/CityRenderer.ApplyModelTransform.
        /// Nothing in the game writes it.</summary>
        internal static bool CarveWaterOnLoad = true;

        public bool Contains(float gameX, float gameZ)
            => gameX >= MinX && gameX <= MinX + (W - 1) * Step
            && gameZ >= MinZ && gameZ <= MinZ + (H - 1) * Step;

        /// <summary>Height under a UNITY-space point.</summary>
        public float HeightAt(float unityX, float unityZ)
            => HeightAtGame(-unityX, unityZ); // mirror X back into the game frame

        /// <summary>
        /// An optional ground-lowering hook, for tunnel trenches and the river bed.
        ///
        /// This is how src/citymap.js does it (heightAt, citymap.js:3226): the cut is
        /// ANALYTIC rather than baked into the 60 m grid, so a trench has real edges —
        /// and because it lives in the height function, the approach road, its kerbs,
        /// its markings and every prop beside it follow it down without knowing it
        /// exists. "A road that dives under a junction is a hole in the GROUND, not a
        /// pipe laid on the lawn."
        ///
        /// It may only LOWER the surface, never raise it — the JS performs exactly
        /// that test. Null by default, so the verified sampler behaviour (CityProbe,
        /// CityPlaceProbe) is unchanged until something attaches a plan.
        ///
        /// Signature: (x, z, uncutHeight) -> lowered height, or null for "no cut here".
        /// </summary>
        public System.Func<float, float, float, float?> GroundCut;

        /// <summary>Height at a point already in game-frame coordinates.</summary>
        public float HeightAtGame(float x, float z)
        {
            float fx = Mathf.Clamp((x - MinX) / Step, 0f, W - 1.001f);
            float fz = Mathf.Clamp((z - MinZ) / Step, 0f, H - 1.001f);
            int c = (int)fx, r = (int)fz;
            float dx = fx - c, dz = fz - r;
            int i = r * W + c;
            float h00 = Heights[i];
            float h10 = Heights[i + 1];
            float h01 = Heights[i + W];
            float h11 = Heights[i + W + 1];
            // The triangle the mesh actually draws — NOT the bilinear patch.
            float y = (dx + dz <= 1f)
                ? h00 + dx * (h10 - h00) + dz * (h01 - h00)
                : h11 + (1f - dx) * (h01 - h11) + (1f - dz) * (h10 - h11);

            if (GroundCut != null)
            {
                float? cut = GroundCut(x, z, y);
                // lower only, exactly as citymap.js:3226-3229 tests
                if (cut.HasValue && cut.Value < y) y = cut.Value;
            }
            return y;
        }

        /// <summary>Ground kind under a UNITY-space point.</summary>
        public byte ClassAt(float unityX, float unityZ)
        {
            float x = -unityX, z = unityZ;
            int c = (int)Mathf.Clamp((x - MinX) / Step, 0f, W - 1.001f);
            int r = (int)Mathf.Clamp((z - MinZ) / Step, 0f, H - 1.001f);
            return Classes[r * W + c];
        }
    }
}

using System;
using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// The facade/roof clutter FacadeRelief.cs's own class doc explicitly
    /// left out: "AC units, downpipes, entrance canopies, satellite dishes,
    /// flat-roof parapets/roof clutter". Ported from the same source region
    /// FacadeRelief.cs covers (src/citymap.js:4990-5407, the half of
    /// `_buildBuildings`'s per-edge relief loop and the roof-clutter block
    /// FacadeRelief.cs does not emit) plus the instancing SPECS from
    /// `_buildFacadeProps` (citymap.js:6124-6224) for these same six kinds.
    ///
    /// PURE DATA — no GameObject, no mesh, no Instantiate. Positions are
    /// already in UNITY frame (GeoFrame.ToUnity) and yaws already converted
    /// (GeoFrame.YawToUnity), same convention as FacadeRelief.Items.
    ///
    /// WHO CALLS THIS, and who DRAWS it (both were open until 2026-10-31,
    /// docs/open_items.json `facade-props-pitched`):
    ///   producer — BuildingBuilder.BuildRich's per-building loop, one line
    ///     after the sibling FacadeRelief.BuildFacade this function has to
    ///     stay in hash-step with, which is the only place the REAL
    ///     `pitchedFinal` exists (citymap.js:4818-4821). Output lands in
    ///     RichResult.Props.
    ///   renderer — City/FacadeKitBuilder.cs's Build* methods, driven by
    ///     CityRenderer.BuildFacadeBatches alongside the six FacadeRelief
    ///     layers, into the same batch list and the same cull loop the JS's
    ///     single `this.chunkMeshes` uses.
    ///
    /// NOT PORTED: the photo-facade (`pb`) branch's own AC/window relief
    /// (citymap.js:5168-5205) — that is the photo-facade agent's exclusive
    /// per-building system, same exclusion FacadeRelief.cs documents.
    /// </summary>
    public static class FacadeProps
    {
        public struct AcItem { public Vector3 Pos; public float AngUnity; }
        public struct DishItem { public Vector3 Pos; public float AngUnity; }
        public struct DrainpipeItem { public Vector3 Pos; public float AngUnity; public float Sy; }
        public struct EntranceItem { public Vector3 Pos; public float AngUnity; public string Variant; }
        public struct ParapetItem { public Vector3 Pos; public float AngUnity; public float Sx; }
        public struct RoofPropItem { public Vector3 Pos; public float AngUnity; public string Variant; }

        /// <summary>Citywide item pools — pass ONE instance through the whole
        /// build so the budgets below cap across every building exactly like
        /// the JS's shared arrays (citymap.js:4500-4509).</summary>
        public class Items
        {
            public readonly List<AcItem> Acs = new List<AcItem>();
            public readonly List<DishItem> Dishes = new List<DishItem>();
            public readonly List<DrainpipeItem> Drainpipes = new List<DrainpipeItem>();
            public readonly List<EntranceItem> Entrances = new List<EntranceItem>();
            public readonly List<ParapetItem> Parapets = new List<ParapetItem>();
            public readonly List<RoofPropItem> RoofProps = new List<RoofPropItem>();
        }

        public const int AcBudget = 50000;        // citymap.js:5157,5192 acItems.length<50000
        public const int DishBudget = 30000;      // citymap.js:5046 dishItems.length<30000
        public const int DrainBudget = 80000;     // citymap.js:5117 drainItems.length<80000
        public const int EntranceBudget = 60000;  // citymap.js:5136 entranceItems.length<60000
        public const int ParapetBudget = 70000;   // citymap.js:5359 parapetItems.length<70000
        public const int RoofPropBudget = 50000;  // citymap.js:5378 roofPropItems.length<50000

        /// <summary>Box-fallback + culling spec mirroring one `put(...)` call
        /// in `_buildFacadeProps` (citymap.js:6124-6224). `Kit` is the
        /// facade_kit.glb object name `geoOf`/`matOf` look up there; the box
        /// dimensions/offset/colour are the fallback used when that name is
        /// missing from the kit (citymap.js:6161-6211).</summary>
        public struct RenderSpec
        {
            public string Kit;
            public Vector3 FallbackSize;
            public Vector3 FallbackOffset;
            public Color32 FallbackColor;
            public float MaxDist;
            public float ChunkM;
            public bool Lodable;
        }

        static Color32 Hex(int rgb) => new Color32((byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff), 255);

        public static readonly RenderSpec Ac = new RenderSpec
        {
            Kit = "ac", FallbackSize = new Vector3(0.55f, 0.5f, 0.42f), FallbackOffset = new Vector3(0f, 0f, 0.26f),
            FallbackColor = Hex(0xdcdcd6), MaxDist = 650f, ChunkM = 500f, Lodable = true,
        }; // citymap.js:6161-6165

        public static readonly RenderSpec Drainpipe = new RenderSpec
        {
            Kit = "drainpipe", FallbackSize = new Vector3(0.1f, 3f, 0.1f), FallbackOffset = new Vector3(0f, 1.5f, 0.15f),
            FallbackColor = Hex(0x9aa0a3), MaxDist = 500f, ChunkM = 1000f, Lodable = false,
        }; // citymap.js:6178-6182 (put's own chunkM default — no third arg passed)

        public static readonly RenderSpec Entrance = new RenderSpec
        {
            Kit = "entrance", FallbackSize = new Vector3(1.4f, 2.2f, 0.12f), FallbackOffset = new Vector3(0f, 1.1f, 0.06f),
            FallbackColor = Hex(0x3a3a3c), MaxDist = 500f, ChunkM = 500f, Lodable = true,
        }; // citymap.js:6183-6188 (also used for the "ent_old" variant bucket)

        public static readonly RenderSpec Dish = new RenderSpec
        {
            Kit = "dish", FallbackSize = new Vector3(0.9f, 0.9f, 0.06f), FallbackOffset = new Vector3(0f, 0f, 0.4f),
            FallbackColor = Hex(0xb9bdc0), MaxDist = 500f, ChunkM = 500f, Lodable = true,
        }; // citymap.js:6194-6198

        public static readonly RenderSpec Parapet = new RenderSpec
        {
            Kit = "parapet", FallbackSize = new Vector3(3f, 0.55f, 0.22f), FallbackOffset = new Vector3(0f, 0.28f, 0f),
            FallbackColor = Hex(0xaba599), MaxDist = 1500f, ChunkM = 1000f, Lodable = false,
        }; // citymap.js:6200-6204 — "the whole point is reading from the AIR"

        public static readonly RenderSpec RoofVent = new RenderSpec
        {
            Kit = "roof_vent", FallbackSize = new Vector3(1.2f, 1.2f, 1.2f), FallbackOffset = new Vector3(0f, 0.6f, 0f),
            FallbackColor = Hex(0x9d978c), MaxDist = 1500f, ChunkM = 1000f, Lodable = false,
        }; // citymap.js:6205-6211 — shared box for roof_box/roof_tank/roof_vent/the roof "ac" cluster

        /// <summary>
        /// Ports citymap.js:4990-5207's AC/downpipe/entrance/dish half AND the
        /// independent roof-clutter block (citymap.js:5356-5407). Must be
        /// called with the SAME `contour`/`cx`/`cz`/`baseY`/`topY`/`relief` a
        /// sibling <see cref="FacadeRelief.BuildFacade"/> call gets for this
        /// building — a dish's hash seed is keyed off the exact (i, k, f) its
        /// balcony neighbour uses, so the two must walk the same geometry.
        /// `heightAt` mirrors `this.heightAt` (terrain probe). `pitchedFinal`
        /// is the OSM-combined roof-shape flag (gable roofs get no parapet/
        /// roof clutter — citymap.js:5356 `!pitched`). `area` must be the
        /// ABSOLUTE polygon area (BuildingKit.PolygonArea returns a SIGNED
        /// shoelace value — see that method's own float32-cancellation-bug
        /// doc — callers take Mathf.Abs() of it upstream, matching
        /// citymap.js's own `area = Math.abs(ShapeUtils.area(contour))`).
        /// </summary>
        public static void BuildFacadeProps(int bi, float[] contour, int n, float cx, float cz,
            float baseY, float topY, float h, float area, bool cottage,
            WallMaterials.BuildingStyle st, bool pitchedFinal,
            FacadeRelief.Flags relief, Func<float, float, float> heightAt, Items into,
            bool roofOnly = false)
        {
            // citymap.js:4990's own gating condition, with the `pb`/`pbRelief`
            // terms dropped (always false in this port, see class doc).
            bool wantAc = h >= 6f;
            bool runFacade = (relief.WantBalconies || relief.WantWoodBalc || relief.WantWindows || relief.WantCornice || wantAc)
                              && (relief.NFloors >= 2 || cottage);
            // `roofOnly` is the `pb` (photo-facade) case: citymap.js gates the
            // entrance (:5136) and AC (:5157) pushes on `!pb` and this port
            // skips the procedural facade relief for a photo building
            // altogether (the photo already shows real windows), but the roof
            // block below is NOT inside the `pb` branch in the JS — a photo
            // facade is a WALL cut and the flat slab above it still wears its
            // parapet lip and its vents. So the caller withholds the facade
            // half and keeps the roof half.
            if (roofOnly) runFacade = false;

            if (runFacade)
            {
                // us[]: perimeter metres / 3, with the SAME per-building phase
                // FacadeRelief.BuildFacade computes (citymap.js:4895-4905) —
                // duplicated here (not exposed there) so this file stays an
                // independently callable pure function, same house style.
                float uP = Mathf.Floor((float)(WallMaterials.Hash(bi + 8111) * 8));
                var us = new float[n + 1];
                us[0] = uP;
                float perim = 0f;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    float dx = contour[j * 2] - contour[i * 2], dz = contour[j * 2 + 1] - contour[i * 2 + 1];
                    perim += Mathf.Sqrt(dx * dx + dz * dz);
                    us[i + 1] = uP + perim / 3f;
                }

                // entSeg: the longest wall gets the entrance door — citymap.js:5014-5020
                int entSeg = -1; float entBest = 0f;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    float ddx = contour[j * 2] - contour[i * 2], ddz = contour[j * 2 + 1] - contour[i * 2 + 1];
                    float l2 = ddx * ddx + ddz * ddz;
                    if (l2 > entBest) { entBest = l2; entSeg = i; }
                }

                int pipeCount = 0; // citymap.js:5018 — resets per building, capped at 2
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    float x1 = contour[i * 2], z1 = contour[i * 2 + 1];
                    float ex = contour[j * 2] - x1, ez = contour[j * 2 + 1] - z1;
                    float len = Mathf.Sqrt(ex * ex + ez * ez);
                    if (len < 5f) continue;
                    float nx = -ez / len, nz = ex / len;
                    float mx = x1 + ex / 2f, mz = z1 + ez / 2f;
                    if ((mx - cx) * nx + (mz - cz) * nz < 0f) { nx = -nx; nz = -nz; } // face outward
                    float ang = Mathf.Atan2(nx, nz);
                    float angUnity = GeoFrame.YawToUnity(ang);
                    int bays2 = Mathf.FloorToInt(len / 3.4f);

                    // ---- AC units beside a window, street wall only — citymap.js:5157-5166
                    if (i == 0 && wantAc && into.Acs.Count < AcBudget && WallMaterials.Hash(bi + 31337) < 0.35)
                    {
                        for (int u = 0; u < 2 && bays2 > 1; u++)
                        {
                            float t = (Mathf.Floor((float)(WallMaterials.Hash(bi * 7 + u * 313) * bays2)) + 0.5f) / bays2;
                            int f = 1 + Mathf.FloorToInt((float)WallMaterials.Hash(bi * 13 + u * 71) * Mathf.Max(1, relief.NFloors - 1));
                            float px = x1 + ex * t + nx * 0.1f, pz = z1 + ez * t + nz * 0.1f;
                            float py = baseY + 2f + f * 3.2f - 1.1f;
                            if (py < heightAt(px, pz) + 0.5f) continue; // buried on slope
                            into.Acs.Add(new AcItem { Pos = GeoFrame.ToUnity(px, py, pz), AngUnity = angUnity });
                        }
                    }

                    // ---- downpipes at a corner, <=2 per building — citymap.js:5117-5135
                    if (pipeCount < 2 && into.Drainpipes.Count < DrainBudget && len > 7f && WallMaterials.Hash(bi * 31 + i * 7 + 5) < 0.6)
                    {
                        float tt = Mathf.Min(0.45f, 1.2f / len); // hug the corner
                        float px = x1 + ex * tt + nx * 0.13f, pz = z1 + ez * tt + nz * 0.13f;
                        float gy = heightAt(px, pz);
                        if (gy <= baseY + h * 0.5f && Mathf.Abs(gy - baseY) <= 6f)
                        {
                            float py = Mathf.Min(baseY + 2f, gy) - 0.3f;
                            into.Drainpipes.Add(new DrainpipeItem
                            {
                                Pos = GeoFrame.ToUnity(px, py, pz), AngUnity = angUnity,
                                Sy = Mathf.Max(1f, (baseY + 2f + h - py) / 3f), // stretch the 3 m section to the full facade
                            });
                            pipeCount++;
                        }
                    }

                    // ---- entrance canopy, the longest wall only — citymap.js:5136-5155
                    if (i == entSeg && (h >= 6f || cottage) && into.Entrances.Count < EntranceBudget)
                    {
                        float kEdge = Mathf.Round((us[i] + us[i + 1]) / 2f); // snap to a 3 m texture-cell edge
                        float t = Mathf.Clamp(((kEdge - us[i]) * 3f) / len, 0.12f, 0.88f);
                        float px = x1 + ex * t + nx * 0.06f, pz = z1 + ez * t + nz * 0.06f;
                        float gy = heightAt(px, pz);
                        if (gy <= baseY + h * 0.5f && Mathf.Abs(gy - baseY) <= 6f)
                        {
                            bool old = st.Style == 3 || st.District == WallMaterials.District.Historic
                                || (st.District == WallMaterials.District.Classic && WallMaterials.Hash(bi + 833) < 0.7)
                                || (cottage && WallMaterials.Hash(bi + 833) < 0.45);
                            into.Entrances.Add(new EntranceItem
                            {
                                Pos = GeoFrame.ToUnity(px, gy + 0.05f, pz), AngUnity = angUnity,
                                Variant = old ? "ent_old" : "entrance",
                            });
                        }
                    }

                    // ---- satellite dishes beside a balcony — citymap.js:5034-5057.
                    // Replays FacadeRelief.BuildFacade's own balcony k/f loop
                    // (same hash seeds) purely to land a dish beside the SAME
                    // balcony that loop already pushed; it pushes no balcony itself.
                    if ((relief.WantBalconies || relief.WantWoodBalc) && bays2 > 0)
                    {
                        for (int k = 0; k < bays2; k++)
                        {
                            if (WallMaterials.Hash(bi * 131 + i * 17 + k) < 0.45) continue;
                            float t = (k + 0.5f) / bays2;
                            float wx = x1 + ex * t + nx * 0.08f, wz = z1 + ez * t + nz * 0.08f;
                            float gy = heightAt(wx, wz);
                            for (int f = 1; f < relief.NFloors; f += 2)
                            {
                                float by = baseY + 2f + f * 3.2f + 0.4f;
                                if (by < gy + 0.4f) continue;
                                if (into.Dishes.Count < DishBudget && WallMaterials.Hash(bi * 131 + i * 17 + k * 7 + f + 40009) < 0.15)
                                {
                                    float sgn = t < 0.5f ? 1f : -1f; // beside the balcony, toward the wall middle
                                    into.Dishes.Add(new DishItem
                                    {
                                        Pos = GeoFrame.ToUnity(wx + (ex / len) * 1.55f * sgn + nx * 0.06f, by + 1.2f, wz + (ez / len) * 1.55f * sgn + nz * 0.06f),
                                        AngUnity = angUnity,
                                    });
                                }
                            }
                        }
                    }
                }
            }

            // ---- roof clutter: flat roofs only, independent of the facade gate above — citymap.js:5356-5407
            if (!pitchedFinal && h >= 10f)
            {
                if (into.Parapets.Count < ParapetBudget)
                {
                    for (int i = 0; i < n; i++)
                    {
                        int j = (i + 1) % n;
                        float px1 = contour[i * 2], pz1 = contour[i * 2 + 1];
                        float pex = contour[j * 2] - px1, pez = contour[j * 2 + 1] - pz1;
                        float plen = Mathf.Sqrt(pex * pex + pez * pez);
                        if (plen < 2.5f) continue; // one stretched lip per wall segment
                        into.Parapets.Add(new ParapetItem
                        {
                            Pos = GeoFrame.ToUnity(px1 + pex / 2f, topY, pz1 + pez / 2f),
                            AngUnity = GeoFrame.YawToUnity(Mathf.Atan2(-pez / plen, pex / plen)),
                            Sx = plen / 3f,
                        });
                    }
                }
                if (into.RoofProps.Count < RoofPropBudget)
                {
                    float rh1 = (float)WallMaterials.Hash(bi + 90001);
                    float rr = Mathf.Sqrt(area) * 0.22f; // stay well inside the footprint
                    if (h >= 20f)
                        into.RoofProps.Add(new RoofPropItem
                        {
                            Pos = GeoFrame.ToUnity(cx + (rh1 - 0.5f) * rr, topY, cz + ((float)WallMaterials.Hash(bi + 90002) - 0.5f) * rr),
                            AngUnity = GeoFrame.YawToUnity(rh1 * 6.28f), Variant = "roof_box",
                        });
                    if (rh1 < 0.55f)
                        into.RoofProps.Add(new RoofPropItem
                        {
                            Pos = GeoFrame.ToUnity(cx + ((float)WallMaterials.Hash(bi + 90003) - 0.5f) * rr * 1.6f, topY, cz + ((float)WallMaterials.Hash(bi + 90004) - 0.5f) * rr * 1.6f),
                            AngUnity = GeoFrame.YawToUnity((float)WallMaterials.Hash(bi + 90012) * 6.28f), Variant = "roof_tank",
                        });
                    int nv = 2 + Mathf.FloorToInt((float)WallMaterials.Hash(bi + 90005) * 3f);
                    for (int v2 = 0; v2 < nv; v2++)
                        into.RoofProps.Add(new RoofPropItem
                        {
                            Pos = GeoFrame.ToUnity(cx + ((float)WallMaterials.Hash(bi * 3 + v2 * 17 + 90006) - 0.5f) * rr * 2f, topY, cz + ((float)WallMaterials.Hash(bi * 5 + v2 * 29 + 90007) - 0.5f) * rr * 2f),
                            AngUnity = GeoFrame.YawToUnity((float)WallMaterials.Hash(bi + v2 + 90013) * 6.28f), Variant = "roof_vent",
                        });
                    if (WallMaterials.Hash(bi + 90008) < 0.4)
                        into.RoofProps.Add(new RoofPropItem
                        {
                            Pos = GeoFrame.ToUnity(cx + ((float)WallMaterials.Hash(bi + 90009) - 0.5f) * rr * 2f, topY + 0.25f, cz + ((float)WallMaterials.Hash(bi + 90010) - 0.5f) * rr * 2f),
                            AngUnity = GeoFrame.YawToUnity((float)WallMaterials.Hash(bi + 90011) * 6.28f), Variant = "ac", // roof AC cluster unit
                        });
                }
            }
        }
    }
}

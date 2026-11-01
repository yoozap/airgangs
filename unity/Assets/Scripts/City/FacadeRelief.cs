using System;
using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// 3D facade relief: real window frames, balconies (concrete/wood/glazed/
    /// loggia), cornices and glass curtain-wall mullions/bands — the geometry
    /// that makes a wall read as a building instead of a textured box.
    ///
    /// Ported from src/citymap.js:
    ///   decision flags (wantBalconies/wantWoodBalc/nFloors/wantWindows/
    ///   wantCornice)              citymap.js:4827-4844
    ///   winVariant/balcVariant/wantJuliet/wantGrille/shopGround
    ///                              citymap.js:5013-5033
    ///   per-edge relief loop (balconies, windows+juliet+grille, cornice)
    ///                              citymap.js:4990-5119
    ///   glass tower mullions/bands citymap.js:5202-5258
    ///
    /// PURE DATA — no GameObject, no mesh, no Instantiate. Produces placement
    /// lists (position already in UNITY frame via GeoFrame.ToUnity, yaw
    /// already converted via GeoFrame.YawToUnity) that an out-of-scope
    /// instancing layer is expected to turn into the Blender facade_kit.fbx
    /// pieces (tools/build_facade_kit.py), the way citymap.js's
    /// _buildFacadeProps turns these same item lists into InstancedMesh.
    ///
    /// NOT PORTED (explicitly out of scope / lower value for this pass):
    /// AC units, downpipes, entrance canopies, satellite dishes, flat-roof
    /// parapets/roof clutter, and the `pb`-photo-facade relief branch (that is
    /// the OTHER agent's exclusive per-building photo system). windowless
    /// plaster for everyone else falls naturally out of WantWindows being
    /// false — no extra code needed for that half of the requirement.
    /// </summary>
    public static class FacadeRelief
    {
        public struct Flags
        {
            public bool WantBalconies, WantWoodBalc, WantWindows, WantCornice, WantJuliet, WantGrille, ShopGround;
            public int NFloors;
            public string WinVariant;  // window, win_old, win_stalin, win_pvc, win_brick
            public string BalcVariant; // balc_wood, balcony, balc_glazed, balc_loggia_p, balc_loggia, balc_metal
        }

        /// <summary>
        /// citymap.js:4827-4844 + the early half of 5013-5033. Does NOT need a
        /// wallBucket (that is resolved FROM `WantWindows` below, so it can't
        /// come first) — only WinVariant/BalcVariant/WantJuliet/WantGrille/
        /// ShopGround do, which is what <see cref="DecideLate"/> is for.
        /// Must run AFTER WallMaterials.Classify and AFTER the final
        /// (OSM-combined) `pitched` flag is known.
        /// </summary>
        public static Flags DecideEarly(int bi, WallMaterials.BuildingStyle st, float h, float area, bool cottage, bool pitchedFinal)
        {
            var f = new Flags();
            f.WantBalconies = (st.Style == 1 || (st.Style == 0 && st.District == WallMaterials.District.Soviet))
                && h >= 10f && WallMaterials.Hash(bi + 999) < 0.45;
            f.WantWoodBalc = st.Style == 3 && h >= 8f && WallMaterials.Hash(bi + 999) < 0.4;
            f.NFloors = Mathf.Min(16, Mathf.FloorToInt(h / 3.2f));
            f.WantWindows = (st.Style == 0 || st.Style == 3) && !f.WantBalconies
                && h >= 5f && h < 51f && (f.NFloors >= 2 || (cottage && h >= 4f));
            f.WantCornice = (st.Style == 3 || (st.District == WallMaterials.District.Classic && st.Style == 0)) && !pitchedFinal;
            return f;
        }

        /// <summary>
        /// citymap.js:5013-5033, the half that needs the RESOLVED wallBucket
        /// (WallMaterials.ResolveWallBucket, which itself needs WantWindows
        /// from DecideEarly — this is why the style chain is split in two).
        /// </summary>
        public static void DecideLate(int bi, WallMaterials.BuildingStyle st, int wallBucket, float area, ref Flags f)
        {
            double vHash = WallMaterials.Hash(bi + 81);
            string winVariant = "window";
            if (st.Style == 3 || st.District == WallMaterials.District.Historic) winVariant = vHash < 0.6 ? "win_old" : "window";
            else if (st.District == WallMaterials.District.Classic) winVariant = vHash < 0.5 ? "win_stalin" : "win_pvc";
            else if (st.District == WallMaterials.District.Soviet) winVariant = vHash < 0.55 ? "window" : "win_pvc";
            if (wallBucket == 4 || wallBucket == 5) winVariant = "win_brick"; // brick walls wear the W5 sash
            f.WinVariant = winVariant;

            f.ShopGround = area > 400f && WallMaterials.Hash(bi + 271) < 0.35;
            double bA = WallMaterials.Hash(bi + 433), bB = WallMaterials.Hash(bi + 577),
                   bC = WallMaterials.Hash(bi + 601), bD = WallMaterials.Hash(bi + 613);
            f.BalcVariant = (st.Style == 3 || st.District == WallMaterials.District.Historic) ? "balc_wood"
                : bA < 0.5 ? "balcony"
                : bB < 0.25 ? "balc_glazed"
                : bC < 0.45 ? (bD < 0.3 ? "balc_loggia_p" : "balc_loggia")
                : "balc_metal";

            f.WantJuliet = (st.District == WallMaterials.District.Classic || st.Style == 3)
                && (winVariant == "win_stalin" || winVariant == "win_old") && WallMaterials.Hash(bi + 719) < 0.5;
            f.WantGrille = (st.District == WallMaterials.District.Soviet || st.District == WallMaterials.District.Classic)
                && WallMaterials.Hash(bi + 357) < 0.3;
        }

        public struct BalconyItem { public Vector3 Pos; public float AngUnity; public string Variant; }
        public struct WindowItem { public Vector3 Pos; public float AngUnity; public string Variant; }
        public struct GrilleItem { public Vector3 Pos; public float AngUnity; }
        public struct CorniceItem { public Vector3 Pos; public float AngUnity; public float Sx; }
        public struct GlassMullionItem { public Vector3 Pos; public float AngUnity; public float Sy; }
        public struct GlassBandItem { public Vector3 Pos; public float AngUnity; public float Sx; public float Sy; }

        /// <summary>Citywide item pools — pass ONE instance through the whole
        /// build so the budgets (160k balconies, 500k windows, ...) cap across
        /// every chunk exactly like the JS's shared arrays.</summary>
        public class Items
        {
            public readonly List<BalconyItem> Balconies = new List<BalconyItem>();
            public readonly List<WindowItem> Windows = new List<WindowItem>();
            public readonly List<GrilleItem> Grilles = new List<GrilleItem>();
            public readonly List<CorniceItem> Cornices = new List<CorniceItem>();
            public readonly List<GlassMullionItem> GlassMullions = new List<GlassMullionItem>();
            public readonly List<GlassBandItem> GlassBands = new List<GlassBandItem>();
        }

        /// <summary>
        /// The per-edge relief loop, citymap.js:4990-5119. `contour`/`cx`/`cz`
        /// are GAME-frame metres (same convention as BuildingKit); `baseY`/
        /// `topY` are the wall's extrusion floor/ceiling (BuildingBuilder's
        /// boxBase/top, already world Y — not mirrored, Y is never mirrored).
        /// `heightAt` mirrors `this.heightAt` (terrain probe, used to skip
        /// balconies/windows that would hang inside a hillside).
        /// </summary>
        public static void BuildFacade(int bi, float[] contour, int n, float cx, float cz,
            float baseY, float topY, bool cottage, Flags flags, Func<float, float, float> heightAt, Items into)
        {
            if (!((flags.WantBalconies || flags.WantWoodBalc || flags.WantWindows || flags.WantCornice)
                  && (flags.NFloors >= 2 || cottage)))
                return;

            // us[]: perimeter metres / 3, with a per-building phase so kit
            // frames don't repeat pixel-for-pixel between neighbours (citymap.js:4895-4905)
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

                if ((flags.WantBalconies || flags.WantWoodBalc) && into.Balconies.Count < 160000 && bays2 > 0)
                {
                    for (int k = 0; k < bays2; k++)
                    {
                        if (WallMaterials.Hash(bi * 131 + i * 17 + k) < 0.45) continue;
                        float t = (k + 0.5f) / bays2;
                        float wx = x1 + ex * t + nx * 0.08f, wz = z1 + ez * t + nz * 0.08f;
                        float gy = heightAt(wx, wz);
                        for (int f = 1; f < flags.NFloors; f += 2)
                        {
                            if (into.Balconies.Count >= 160000) break;
                            float by = baseY + 2f + f * 3.2f + 0.4f;
                            if (by < gy + 0.4f) continue;
                            into.Balconies.Add(new BalconyItem { Pos = GeoFrame.ToUnity(wx, by, wz), AngUnity = angUnity, Variant = flags.BalcVariant });
                        }
                    }
                }

                if (flags.WantWindows && into.Windows.Count < 500000)
                {
                    float u0 = us[i], u1 = us[i + 1];
                    int fMax = cottage ? Mathf.Max(1, flags.NFloors) : flags.NFloors - 1;
                    for (float k = Mathf.Ceil(u0 + 0.5f) - 0.5f; k + 0.5f <= u1; k += 1f)
                    {
                        float t = (k - u0) * 3f / len;
                        if (t < 0.02f || t > 0.98f) continue; // keep off the corners
                        float wx = x1 + ex * t + nx * 0.04f, wz = z1 + ez * t + nz * 0.04f;
                        float gy = heightAt(wx, wz);
                        for (int f = 1; f <= fMax; f++)
                        {
                            float wy = baseY + f * 3f + 1.5f;
                            if (wy + 1.1f > topY) break; // would poke through the eaves
                            if (wy < gy + 0.8f) continue; // buried on a slope
                            string variant = (f == 1 && flags.ShopGround) ? "win_shop" : flags.WinVariant;
                            into.Windows.Add(new WindowItem { Pos = GeoFrame.ToUnity(wx, wy, wz), AngUnity = angUnity, Variant = variant });
                            if (f == 1 && flags.WantGrille && !flags.ShopGround && into.Grilles.Count < 60000)
                                into.Grilles.Add(new GrilleItem { Pos = GeoFrame.ToUnity(wx + nx * 0.02f, baseY + 4.5f, wz + nz * 0.02f), AngUnity = angUnity });
                            if (flags.WantJuliet && f >= 2 && !(f == 1 && flags.ShopGround) && into.Balconies.Count < 160000 &&
                                WallMaterials.Hash(bi * 131 + i * 17 + k * 3 + f) < 0.35)
                                into.Balconies.Add(new BalconyItem { Pos = GeoFrame.ToUnity(wx + nx * 0.03f, wy - 1.15f, wz + nz * 0.03f), AngUnity = angUnity, Variant = "balc_juliet" });
                        }
                    }
                }

                if (flags.WantCornice && into.Cornices.Count < 60000)
                {
                    int segs = Mathf.Max(1, Mathf.RoundToInt(len / 3f));
                    for (int k = 0; k < segs; k++)
                    {
                        float t = ((k + 0.5f) * len / segs) / len;
                        into.Cornices.Add(new CorniceItem
                        {
                            Pos = GeoFrame.ToUnity(x1 + ex * t + nx * 0.02f, topY - 0.15f, z1 + ez * t + nz * 0.02f),
                            AngUnity = angUnity,
                            Sx = len / segs / 3f,
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Glass curtain-wall relief for style===2 towers: full-height corner
        /// mullion strips + horizontal slab bands every 3 floors + a crown band.
        /// citymap.js:5202-5258. Call only when BuildingStyle.Style == 2.
        /// </summary>
        public static void BuildGlassRelief(float[] contour, int n, float cx, float cz,
            float baseY, float topY, float h, Items into)
        {
            if (into.GlassBands.Count + into.GlassMullions.Count >= 40000) return;
            for (int i = 0; i < n; i++)
            {
                int iPrev = (i + n - 1) % n, j = (i + 1) % n;
                float px = contour[i * 2], pz = contour[i * 2 + 1];
                float ax = px - contour[iPrev * 2], az = pz - contour[iPrev * 2 + 1];
                float ex = contour[j * 2] - px, ez = contour[j * 2 + 1] - pz;
                float la = Mathf.Sqrt(ax * ax + az * az), le = Mathf.Sqrt(ex * ex + ez * ez);
                if (la == 0f || le == 0f) continue;

                float turn = Mathf.Atan2((ax * ez - az * ex) / (la * le), (ax * ex + az * ez) / (la * le));
                if (Mathf.Abs(turn) > 0.5f)
                {
                    float ox = px - cx, oz = pz - cz;
                    float ol = Mathf.Sqrt(ox * ox + oz * oz); if (ol == 0f) ol = 1f;
                    float mullionAng = Mathf.Atan2(ex, ez);
                    into.GlassMullions.Add(new GlassMullionItem
                    {
                        Pos = GeoFrame.ToUnity(px + (ox / ol) * 0.1f, baseY, pz + (oz / ol) * 0.1f),
                        AngUnity = GeoFrame.YawToUnity(mullionAng),
                        Sy = (h + 2f) / 3f,
                    });
                }
                if (le < 8f) continue; // slab bands only on real wall runs

                float nx = -ez / le, nz = ex / le;
                float mx = px + ex / 2f, mz = pz + ez / 2f;
                if ((mx - cx) * nx + (mz - cz) * nz < 0f) { nx = -nx; nz = -nz; }
                float ang = Mathf.Atan2(nx, nz);
                float angUnity = GeoFrame.YawToUnity(ang);
                float bx = mx + nx * 0.02f, bz = mz + nz * 0.02f;
                for (float fy = 9f; fy < h - 2f; fy += 9f) // one band every 3 floors
                    into.GlassBands.Add(new GlassBandItem { Pos = GeoFrame.ToUnity(bx, baseY + 2f + fy, bz), AngUnity = angUnity, Sx = le / 3f, Sy = 1f });
                // crown: a taller band right under the roof line
                into.GlassBands.Add(new GlassBandItem { Pos = GeoFrame.ToUnity(bx, topY - 0.55f, bz), AngUnity = angUnity, Sx = le / 3f, Sy = 2.6f });
            }
        }
    }
}

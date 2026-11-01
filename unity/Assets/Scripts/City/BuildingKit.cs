using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Decides which footprints get a REAL MODELLED KIT building (khrushchevka
    /// panel block, apartment block, shed/hangar, garage/kiosk/hut, suburban
    /// house) instead of an extruded box, and how to place it.
    ///
    /// Ported from src/citymap.js:
    ///   panelFit   citymap.js:4223-4248 (claim: 4661-4669)
    ///   houseFit   citymap.js:4433-4478 (claim: 4687-4694)
    ///   outFit     citymap.js:4288-4328 (claim: 4701-4709)
    ///   aptFit     citymap.js:4335-4378 (claim: 4715-4723)
    ///   shedFit    citymap.js:4380-4422 (claim: 4731-4739)
    ///   claim order citymap.js:4661-4739 — FIRST MATCH WINS, in this exact
    ///   order: panel, house (cottage), out (small), apt, shed.
    ///
    /// PURE DATA/MATH — no GameObject, no Instantiate, no mesh. This only
    /// decides WHICH footprints a kit model claims and the placement transform
    /// (position/angle/scale); a separate (out-of-scope) layer is expected to
    /// instantiate/instance the actual prefabs (tools/glb_to_fbx.py-imported
    /// Assets/Resources/Art/Props/{panel_block,apartments,houses,outbuildings,sheds}.fbx)
    /// at those transforms, the way _placePanelBlocks/_placeHouses/etc. do in
    /// the JS with InstancedMesh.
    ///
    /// Kit geometry is measured OFFLINE by tools/extract_kit_meta.py (reads
    /// glTF accessor min/max straight out of the public/models/*.glb — the
    /// SAME source FBX import — with no Blender/Unity needed) instead of at
    /// runtime like the JS's _loadXKit() (which traverses a loaded GLTF scene
    /// and merges geometry to get a bounding box). That script's manifest is
    /// unity/Assets/StreamingAssets/tbilisi/kit_meta.json.
    ///
    /// FRAME: all fit math runs in the GAME frame (matching BuildingFacts and
    /// the footprint data itself — X=west). The resulting `Ang` is therefore a
    /// GAME-frame heading and, per GeoFrame's rule, is NOT self-correcting (it
    /// was measured from UNMIRRORED vertices) — callers must take `AngUnity`
    /// (== GeoFrame.YawToUnity(Ang)) when actually rotating a placed prefab.
    /// </summary>
    public static class BuildingKit
    {
        public struct KitEntry { public string Name; public float W, D, H; }

        public class Catalog
        {
            public KitEntry[] Out = Array.Empty<KitEntry>();
            public KitEntry[] Apt = Array.Empty<KitEntry>();
            public KitEntry[] Shed = Array.Empty<KitEntry>();
            public KitEntry[] House = Array.Empty<KitEntry>();
            public int[] PanelSections = Array.Empty<int>(); // e.g. 2..6

            class FileShape
            {
                public List<KitEntry> @out;
                public List<KitEntry> apt;
                public List<KitEntry> shed;
                public List<KitEntry> house;
                [JsonProperty("panel_sections")] public List<int> panel_sections;
            }

            public static Catalog Load(string path = null)
            {
                path ??= Path.Combine(Application.streamingAssetsPath, "tbilisi", "kit_meta.json");
                var cat = new Catalog();
                if (!File.Exists(path))
                {
                    Debug.Log($"KIT missing {path} — no footprints will get a real model, all stay extruded boxes");
                    return cat;
                }
                try
                {
                    var shape = JsonConvert.DeserializeObject<FileShape>(File.ReadAllText(path));
                    if (shape == null) return cat;
                    if (shape.@out != null) cat.Out = shape.@out.ToArray();
                    if (shape.apt != null) cat.Apt = shape.apt.ToArray();
                    if (shape.shed != null) cat.Shed = shape.shed.ToArray();
                    if (shape.house != null) cat.House = shape.house.ToArray();
                    if (shape.panel_sections != null) cat.PanelSections = shape.panel_sections.ToArray();
                }
                catch (Exception e) { Debug.LogWarning($"KIT kit_meta.json unreadable ({e.Message}) — kits disabled"); }
                Debug.Log($"KIT out={cat.Out.Length} apt={cat.Apt.Length} shed={cat.Shed.Length} house={cat.House.Length} panel_sections={cat.PanelSections.Length}");
                return cat;
            }
        }

        // ------------------------------------------------------------------
        // Shared footprint measurement — the oriented bounding box in the
        // frame of the LONGEST EDGE (every *Fit function in the JS repeats
        // this exact block; citymap.js:4224-4238 et al.).
        // ------------------------------------------------------------------
        public struct Obb { public float Ang, Len, Dep; }

        /// <summary>contour: x,z pairs (GAME frame metres), n = vertex count.</summary>
        public static Obb MeasureObb(float[] contour, int n)
        {
            float bestLen = 0f, ang = 0f;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                float dx = contour[j * 2] - contour[i * 2];
                float dz = contour[j * 2 + 1] - contour[i * 2 + 1];
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d > bestLen) { bestLen = d; ang = Mathf.Atan2(dz, dx); }
            }
            float ca = Mathf.Cos(-ang), sa = Mathf.Sin(-ang);
            float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                float x = contour[i * 2], z = contour[i * 2 + 1];
                float u = x * ca - z * sa;
                float v = x * sa + z * ca;
                if (u < u0) u0 = u; if (u > u1) u1 = u;
                if (v < v0) v0 = v; if (v > v1) v1 = v;
            }
            return new Obb { Ang = ang, Len = u1 - u0, Dep = v1 - v0 };
        }

        /// <summary>
        /// Signed shoelace area, THREE.ShapeUtils.area equivalent.
        ///
        /// **ACCUMULATES IN `double`, DELIBERATELY.** The game uses ONE shared
        /// coordinate frame for six cities (city.json meta.cities: Tbilisi, Batumi,
        /// Kutaisi, Gori, Rustavi, Kazbegi), so |x| and |z| reach ~264 km and each
        /// shoelace term is ~1e10. In float32 that subtraction is catastrophic
        /// cancellation: measured across the real 193,146 footprints, 48,456 of them
        /// (25%) came out more than 50 m² wrong and some by over 4,000 m² against a
        /// true area of a few hundred. That is enough to flip the area-gated branches
        /// this function feeds — the cottage (40..260 m²), historic (<900), old-town
        /// (<500) and default (<320) tests, plus the three `obb.Len * obb.Dep`
        /// fill-ratio checks below — so buildings near a threshold claimed the wrong
        /// kit or wore the wrong wall material.
        ///
        /// The JS has no such problem: `THREE.ShapeUtils.area` runs on plain float64
        /// Numbers. Returning float is fine — only the accumulation needs the range.
        /// </summary>
        public static float PolygonArea(float[] contour, int n)
        {
            double a = 0.0;
            for (int p = n - 1, q = 0; q < n; p = q++)
                a += (double)contour[p * 2] * contour[q * 2 + 1]
                   - (double)contour[q * 2] * contour[p * 2 + 1];
            return (float)(a * 0.5);
        }

        // ------------------------------------------------------------------
        // Placement results
        // ------------------------------------------------------------------
        public struct PanelPlacement { public Vector3 Pos; public float AngUnity; public int Sections; public int Bi; }
        public struct HousePlacement { public Vector3 Pos; public float AngUnity; public int K; public float Sx, Sz; public int Bi; }
        public struct AptPlacement { public Vector3 Pos; public float AngUnity; public int K; public float Sx, Sz; public int Bi; }
        public struct ShedPlacement { public Vector3 Pos; public float AngUnity; public int K; public float Sx, Sy, Sz; public int Bi; }
        public struct OutPlacement { public Vector3 Pos; public float AngUnity; public int K; public float Sx, Sz; public int Bi; }

        public enum Claim { None, Panel, House, Out, Apt, Shed }

        // ---- panelFit: citymap.js:4223-4248 --------------------------------
        public const float PanelSectionW = 12.0f;   // must match tools/build_panel_block.py SECTION_W
        public const float PanelDepth = 11.4f;      // must match DEPTH
        // citymap.js:6061-6063 _addPanelCollider — a FIXED collider box per
        // block, independent of the real model's measured height (the model
        // itself still renders at its own height; only the invisible flight
        // collider uses this constant, exactly as the JS does).
        public const float PanelColliderHeight = 15.9f;

        /// <summary>
        /// citymap.js:6060-6071 `_addPanelCollider` — the invisible box every
        /// placed panel block adds to the collidables set so a district you can
        /// fly straight through does not replace the extrusion that used to
        /// provide that collision. PURE DATA: returns the world-space AXIS-
        /// ALIGNED bounds of the ROTATED LxD box (L = sections*PanelSectionW,
        /// D = PanelDepth, H = PanelColliderHeight), the same "encapsulate the
        /// rotated footprint's corners" convention CityRenderer.EncapsulateBounds
        /// and VegetationLoader.WorldBounds already use for every other kit
        /// collider in this port — CollidableHash&lt;T&gt; only ever consumes an
        /// AABB, never a true OBB mesh. `p.Pos`/`p.AngUnity` are already in
        /// UNITY space (BuildingKit.TryClaim ran them through GeoFrame.ToUnity/
        /// YawToUnity when the placement was decided), so no further frame
        /// conversion happens here.
        /// </summary>
        public static Bounds PanelColliderBounds(PanelPlacement p)
        {
            float L = p.Sections * PanelSectionW;
            float D = PanelDepth;
            float H = PanelColliderHeight;
            var trs = Matrix4x4.TRS(p.Pos + new Vector3(0f, H / 2f, 0f),
                Quaternion.AngleAxis(p.AngUnity * Mathf.Rad2Deg, Vector3.up), Vector3.one);
            Vector3 c0 = trs.MultiplyPoint3x4(new Vector3(-L / 2f, -H / 2f, -D / 2f));
            Vector3 c1 = trs.MultiplyPoint3x4(new Vector3(L / 2f, -H / 2f, -D / 2f));
            Vector3 c2 = trs.MultiplyPoint3x4(new Vector3(-L / 2f, -H / 2f, D / 2f));
            Vector3 c3 = trs.MultiplyPoint3x4(new Vector3(L / 2f, -H / 2f, D / 2f));
            var b = new Bounds(c0, Vector3.zero);
            b.Encapsulate(c1); b.Encapsulate(c2); b.Encapsulate(c3);
            b.Encapsulate(c0 + new Vector3(0f, H, 0f)); // same 4 corners, top face
            b.Encapsulate(c1 + new Vector3(0f, H, 0f));
            b.Encapsulate(c2 + new Vector3(0f, H, 0f));
            b.Encapsulate(c3 + new Vector3(0f, H, 0f));
            return b;
        }

        public static bool PanelFit(float[] contour, int n, out float ang, out int sections)
        {
            ang = 0; sections = 0;
            if (n < 4 || n > 6) return false;
            var obb = MeasureObb(contour, n);
            if (obb.Dep < 8.5f || obb.Dep > 15.5f) return false;
            if (obb.Len < 21f || obb.Len > 78f) return false;
            if (Mathf.Abs(PolygonArea(contour, n)) < obb.Len * obb.Dep * 0.82f) return false;
            sections = Mathf.Clamp(Mathf.RoundToInt(obb.Len / PanelSectionW), 2, 6);
            ang = obb.Ang;
            return true;
        }

        // ---- houseFit: citymap.js:4433-4478 --------------------------------
        public static bool HouseFit(float[] contour, int n, KitEntry[] kit, out float ang, out int k, out float sx, out float sz)
        {
            ang = 0; k = -1; sx = 1; sz = 1;
            if (kit == null || kit.Length == 0) return false;
            if (n < 4 || n > 16) return false;
            var obb = MeasureObb(contour, n);
            if (obb.Len < 5f || obb.Dep < 3.2f || obb.Len > 34f || obb.Dep > 18f) return false;
            if (obb.Len / obb.Dep > 4.2f) return false;
            float bestErr = float.MaxValue;
            for (int i = 0; i < kit.Length; i++)
            {
                var e = kit[i];
                float cx = obb.Len / e.W, cz = obb.Dep / e.D;
                if (cx < 0.55f || cx > 1.95f || cz < 0.55f || cz > 1.95f) continue;
                float aniso = Mathf.Max(cx / cz, cz / cx);
                if (aniso > 1.8f) continue;
                float err = Mathf.Abs(1f - cx) + Mathf.Abs(1f - cz) + (aniso - 1f) * 1.5f;
                if (err < bestErr) { bestErr = err; k = i; sx = cx; sz = cz; }
            }
            if (k < 0) return false;
            ang = obb.Ang;
            return true;
        }

        // ---- aptFit: citymap.js:4335-4378 -----------------------------------
        public static bool AptFit(float[] contour, int n, KitEntry[] kit, float h, out float ang, out int k, out float sx, out float sz)
        {
            ang = 0; k = -1; sx = 1; sz = 1;
            if (kit == null || kit.Length == 0) return false;
            if (n < 4 || n > 14) return false;
            var obb = MeasureObb(contour, n);
            if (obb.Len < 8f || obb.Dep < 5.5f || obb.Len > 52f || obb.Dep > 30f) return false;
            if (obb.Len / obb.Dep > 4.0f) return false;
            if (Mathf.Abs(PolygonArea(contour, n)) < obb.Len * obb.Dep * 0.64f) return false;
            float bestErr = float.MaxValue;
            for (int i = 0; i < kit.Length; i++)
            {
                var e = kit[i];
                float cx = obb.Len / e.W, cz = obb.Dep / e.D;
                if (cx < 0.48f || cx > 2.2f || cz < 0.48f || cz > 2.0f) continue;
                float aniso = Mathf.Max(cx / cz, cz / cx);
                if (aniso > 2.1f) continue;
                float dh = Mathf.Abs(e.H - h) / Mathf.Max(6f, h);
                float err = dh * 3.0f + Mathf.Abs(1f - cx) + Mathf.Abs(1f - cz) + (aniso - 1f);
                if (err < bestErr) { bestErr = err; k = i; sx = cx; sz = cz; }
            }
            if (k < 0) return false;
            ang = obb.Ang;
            return true;
        }

        // ---- shedFit: citymap.js:4380-4422 ----------------------------------
        public static bool ShedFit(float[] contour, int n, KitEntry[] kit, float h, out float ang, out int k, out float sx, out float sy, out float sz)
        {
            ang = 0; k = -1; sx = 1; sy = 1; sz = 1;
            if (kit == null || kit.Length == 0) return false;
            if (n < 4 || n > 14) return false;
            var obb = MeasureObb(contour, n);
            if (obb.Len < 12f || obb.Dep < 6.5f) return false;
            if (obb.Len / obb.Dep > 6.5f) return false;
            if (Mathf.Abs(PolygonArea(contour, n)) < obb.Len * obb.Dep * 0.60f) return false;
            float bestErr = float.MaxValue;
            for (int i = 0; i < kit.Length; i++)
            {
                var e = kit[i];
                float cx = obb.Len / e.W, cz = obb.Dep / e.D;
                if (cx < 0.35f || cx > 3.6f || cz < 0.35f || cz > 3.6f) continue;
                float aniso = Mathf.Max(cx / cz, cz / cx);
                if (aniso > 2.8f) continue;
                float err = Mathf.Abs(Mathf.Log(cx)) + Mathf.Abs(Mathf.Log(cz)) + (aniso - 1f);
                if (err < bestErr) { bestErr = err; k = i; sx = cx; sz = cz; }
            }
            if (k < 0) return false;
            ang = obb.Ang;
            sy = Mathf.Clamp(h / kit[k].H, 0.55f, 2.0f);
            return true;
        }

        // ---- outFit: citymap.js:4288-4328 -----------------------------------
        public static bool OutFit(float[] contour, int n, KitEntry[] kit, out float ang, out int k, out float sx, out float sz)
        {
            ang = 0; k = -1; sx = 1; sz = 1;
            if (kit == null || kit.Length == 0) return false;
            if (n < 4 || n > 12) return false;
            var obb = MeasureObb(contour, n);
            if (obb.Len < 1.8f || obb.Dep < 1.4f || obb.Len > 18f || obb.Dep > 9f) return false;
            float bestErr = float.MaxValue;
            for (int i = 0; i < kit.Length; i++)
            {
                var e = kit[i];
                float cx = obb.Len / e.W, cz = obb.Dep / e.D;
                if (cx < 0.5f || cx > 2.2f || cz < 0.5f || cz > 2.2f) continue;
                float err = Mathf.Abs(Mathf.Log(cx)) + Mathf.Abs(Mathf.Log(cz));
                if (err < bestErr) { bestErr = err; k = i; sx = cx; sz = cz; }
            }
            if (k < 0) return false;
            ang = obb.Ang;
            return true;
        }

        /// <summary>
        /// The claim order, citymap.js:4661-4739. FIRST MATCH WINS; a match
        /// means "skip the extrusion". `propBase` is the Y the JS props stand
        /// on (`Math.max(base, gmax - 2)`, citymap.js:4625) — real models
        /// stand ON the ground rather than being extruded through it.
        /// </summary>
        public static Claim TryClaim(
            Catalog cat, WallMaterials.District district, bool cottage,
            float[] contour, int n, float area, float h,
            float cx, float cz, float propBaseY, int bi,
            out PanelPlacement panel, out HousePlacement house, out OutPlacement outp,
            out AptPlacement apt, out ShedPlacement shed)
        {
            panel = default; house = default; outp = default; apt = default; shed = default;

            if (cat.PanelSections.Length > 0 && district == WallMaterials.District.Soviet && h >= 13f && h <= 19.5f)
            {
                if (PanelFit(contour, n, out float ang, out int sections) && Array.IndexOf(cat.PanelSections, sections) >= 0)
                {
                    panel = new PanelPlacement { Pos = GeoFrame.ToUnity(cx, propBaseY, cz), AngUnity = GeoFrame.YawToUnity(ang), Sections = sections, Bi = bi };
                    return Claim.Panel;
                }
            }

            if (cat.House.Length > 0 && cottage)
            {
                if (HouseFit(contour, n, cat.House, out float ang, out int k, out float sx, out float sz))
                {
                    house = new HousePlacement { Pos = GeoFrame.ToUnity(cx, propBaseY, cz), AngUnity = GeoFrame.YawToUnity(ang), K = k, Sx = sx, Sz = sz, Bi = bi };
                    return Claim.House;
                }
            }

            if (cat.Out.Length > 0 && area <= 46f)
            {
                if (OutFit(contour, n, cat.Out, out float ang, out int k, out float sx, out float sz))
                {
                    outp = new OutPlacement { Pos = GeoFrame.ToUnity(cx, propBaseY, cz), AngUnity = GeoFrame.YawToUnity(ang), K = k, Sx = sx, Sz = sz, Bi = bi };
                    return Claim.Out;
                }
            }

            if (cat.Apt.Length > 0 && !cottage && h >= 8.5f && h <= 34f && area >= 120f && area <= 1600f)
            {
                if (AptFit(contour, n, cat.Apt, h, out float ang, out int k, out float sx, out float sz))
                {
                    apt = new AptPlacement { Pos = GeoFrame.ToUnity(cx, propBaseY, cz), AngUnity = GeoFrame.YawToUnity(ang), K = k, Sx = sx, Sz = sz, Bi = bi };
                    return Claim.Apt;
                }
            }

            if (cat.Shed.Length > 0 && area >= 260f && h <= 22f && !cottage)
            {
                if (ShedFit(contour, n, cat.Shed, h, out float ang, out int k, out float sx, out float sy, out float sz))
                {
                    shed = new ShedPlacement { Pos = GeoFrame.ToUnity(cx, propBaseY, cz), AngUnity = GeoFrame.YawToUnity(ang), K = k, Sx = sx, Sy = sy, Sz = sz, Bi = bi };
                    return Claim.Shed;
                }
            }

            return Claim.None;
        }
    }
}

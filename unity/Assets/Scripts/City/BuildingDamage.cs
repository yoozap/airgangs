using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Per-building combat damage — src/citymap.js:1235-3048 (the "building damage"
    /// region + address lookup at its tail), minus the two things that need a live
    /// Three.js/Unity scene graph to even talk about (instancing the rubble kit,
    /// actually punching a hole through a tessellated shell — that shell comes
    /// from a SIBLING source file, src/breakwall.js, not citymap.js, and is out of
    /// scope here). Everything that is pure data/math is ported in full: the HP/
    /// 10-stage model, the per-vertex deformation that ages a chunk in place, the
    /// window query grid, the chunk-vertex-range bookkeeping a hit resolves
    /// through, the roof-carve test, the save/remote damage merge, and the HP-table
    /// migration.
    ///
    /// PURE DATA/MATH CLASS (no GameObject/Instantiate — the established house
    /// style: TerrainBuilder, InfraBuilder, MallPlacer). Every method takes the
    /// mesh's raw arrays (position/color/index) or small records and returns new
    /// values; nothing here holds a Mesh, a Renderer or a MonoBehaviour. A caller
    /// with the real chunk GameObjects wires: Mesh.vertices/colors assignment +
    /// MarkDynamic, the MeshCollider/BVH refit, and FX (explosion/plume/shake/
    /// burning particles — src/fx.js, a different unit entirely).
    ///
    /// HANDEDNESS: this operates on vertex arrays ALREADY in Unity space (the
    /// chunk mesh BuildingBuilder.cs built, X already mirrored). The deformation
    /// only moves Y (height) and perturbs X/Z by a position-seeded PSEUDORANDOM
    /// amount — mirroring X does not change which building leans which way in any
    /// way that matters, since the lean axis itself is an arbitrary hash, not a
    /// real-world direction. No GeoFrame call is needed in this file.
    /// </summary>
    public static class BuildingDamage
    {
        // ---- persistence keys + constants (citymap.js:1352-1360) --------------
        public const string DmgKey = "tbfly_building_damage_v1";
        public const string HoleKey = "tbfly_building_holes_v1";
        public const string DmgScaleKey = "tbfly_dmg_scale";
        public const string DmgScale = "2";

        /// <summary>Rebuilding a tessellated shell is expensive; beyond this many
        /// restored-from-save shells a holed building keeps its plain facade.
        /// citymap.js:1361.</summary>
        public const int MaxRestoredShells = 150;

        /// <summary>Ten damage "prototypes" for a merged-chunk building: 0 pristine
        /// .. 8 nearly gone, 9 = collapsed rubble mound. citymap.js:1362.</summary>
        public static readonly float[] StageH =
            { 1f, 0.97f, 0.94f, 0.9f, 0.85f, 0.79f, 0.72f, 0.63f, 0.52f, 0.12f };

        /// <summary>Same ten stages for an INSTANCED destructible (church, mosque,
        /// suburban house, landmark model) — squashes a little less per stage
        /// because the whole volume moves as one rigid instance, not a crumbling
        /// shell. citymap.js:1590 (I_STAGE_H).</summary>
        public static readonly float[] IStageH =
            { 1f, 0.975f, 0.95f, 0.92f, 0.88f, 0.83f, 0.77f, 0.69f, 0.58f, 0.13f };

        /// <summary>0..8 damaged, 9 = collapsed. citymap.js:1498.</summary>
        public static int StageOf(float hp, float maxHp)
            => hp <= 0f ? 9 : Mathf.Min(8, Mathf.FloorToInt((1f - hp / maxHp) * 9f));

        // ---- the one damage transition every call site shares ------------------
        // citymap.js:1526-1546 (damageBuildingHit), :1608-1619 (_damageInstanced)
        // and :1850-1860 (_damageLandmark) are the SAME six lines three times —
        // hp -=dmg clamped at 0, record, re-stage, report whether the stage
        // changed. Factored into one atomic step; the caller (which holds the
        // mesh/instance/landmark record) decides what a stage CHANGE does
        // (collapse vs. re-skin vs. nothing), because that differs by kind.
        public struct StageTransition
        {
            public float Hp;
            public int Stage;
            public bool Changed;
            public bool Destroyed;
            public int PrevStage;
        }

        public static StageTransition ApplyDamage(float hp, float maxHp, int stage, float dmg)
        {
            bool wasDead = hp <= 0f;
            var result = new StageTransition { Hp = hp, Stage = stage, PrevStage = stage, Changed = false };
            if (wasDead) { result.Destroyed = true; return result; }
            result.Hp = Mathf.Max(0f, hp - dmg);
            int newStage = StageOf(result.Hp, maxHp);
            result.Changed = newStage != stage;
            result.Stage = newStage;
            result.Destroyed = result.Hp <= 0f;
            return result;
        }

        // ---- chunk vertex-range bookkeeping -------------------------------------
        // BuildingBuilder.cs writes one merged chunk mesh per tile; each building's
        // triangles occupy a CONTIGUOUS vertex range [V0,V1) inside it (the massing
        // loop appends one building at a time). A hit resolves to a building by a
        // binary search over those ranges — citymap.js:1505-1522.

        public struct BuildingRange
        {
            public int V0, V1; // vertex range, half-open
            public int Bi;     // index into the chunk's building record list
        }

        /// <summary>Binary search: which range (if any) contains vertex <paramref name="vid"/>.
        /// `ranges` MUST be sorted by V0 (BuildingBuilder emits them in append order,
        /// which already is sorted). Returns -1 if none matches. citymap.js:1510-1520.</summary>
        public static int FindRangeIndex(BuildingRange[] ranges, int vid)
        {
            int lo = 0, hi = ranges.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                var r = ranges[mid];
                if (vid < r.V0) hi = mid - 1;
                else if (vid >= r.V1) lo = mid + 1;
                else return mid;
            }
            return -1;
        }

        /// <summary>Hit face -> building range, via the vertex range binary search.
        /// citymap.js:1505-1522 (buildingFromFace). `firstIndexOfFace` is the index
        /// buffer's value at [faceIndex*3] (the first corner of the hit triangle —
        /// every corner of an unsplit triangle falls in the same building's range,
        /// so one lookup is enough, exactly as the JS does it).</summary>
        public static int BuildingFromFace(BuildingRange[] sortedRanges, int[] indexArray, int faceIndex)
        {
            if (sortedRanges == null || sortedRanges.Length == 0 || indexArray == null) return -1;
            int vid = indexArray[faceIndex * 3];
            return FindRangeIndex(sortedRanges, vid);
        }

        /// <summary>Where a building's WALL triangles end and ROOF triangles begin
        /// inside its own vertex range. BuildingBuilder lays each building out as
        /// two wall rings (2/3 of the vertices) then the roof fan/cap (the
        /// remainder, +1 for a pitched apex) — so "wall" is simply "all three
        /// corners below wallTop". citymap.js:2226-2249 (_triRanges).</summary>
        public struct TriRange
        {
            public int W0, W1; // wall index-buffer byte range (half-open, in INDEX units not face units)
            public int R0, R1; // roof index-buffer range
            public bool HasWall, HasRoof;
        }

        public static Dictionary<int, TriRange> BuildTriRanges(
            int[] indexArray, BuildingRange[] sortedRanges)
        {
            var map = new Dictionary<int, TriRange>();
            int tris = indexArray.Length / 3;
            for (int f = 0; f < tris; f++)
            {
                int ri = BuildingFromFace(sortedRanges, indexArray, f);
                if (ri < 0) continue;
                var range = sortedRanges[ri];
                int bi = range.Bi;
                if (!map.TryGetValue(bi, out var e))
                    e = new TriRange { W0 = int.MaxValue, W1 = -1, R0 = int.MaxValue, R1 = -1 };
                int wallTop = range.V0 + (range.V1 - range.V0) / 3 * 2;
                int i = f * 3;
                bool wall = indexArray[i] < wallTop && indexArray[i + 1] < wallTop && indexArray[i + 2] < wallTop;
                if (wall)
                {
                    if (i < e.W0) e.W0 = i;
                    if (i + 3 > e.W1) e.W1 = i + 3;
                    e.HasWall = true;
                }
                else
                {
                    if (i < e.R0) e.R0 = i;
                    if (i + 3 > e.R1) e.R1 = i + 3;
                    e.HasRoof = true;
                }
                map[bi] = e;
            }
            return map;
        }

        // ---- the vertex writer every damage look goes through -------------------
        // citymap.js:1969-2012 (_writeBuildingVerts). kh scales height, jit buckles
        // walls, dark soots the paint, lean shears the whole shell sideways (toward
        // a position-seeded random axis — structural failure), phase re-rolls the
        // jitter pattern (advancing it during a collapse makes the shell visibly
        // shudder as it falls).

        /// <summary>Position-seeded lean axis for one building — citymap.js:1977-1981
        /// (`rec.leanX`/`rec.leanZ`, lazily cached off `rec.bi`). A caller computes
        /// this once per building and keeps it with the damage record.</summary>
        public static void LeanAxis(int buildingIndex, out float leanX, out float leanZ)
        {
            // citymap.js:1975 `((rec.bi * 2654435761) >>> 0) % 1000`. The
            // `>>> 0` is load-bearing: see Data.JsMath.ToUint32 for why writing
            // this with a uint literal silently picked a different lean axis.
            uint h = Data.JsMath.ToUint32((long)buildingIndex * 2654435761L);
            float a = (h % 1000u) / 1000f * Mathf.PI * 2f;
            leanX = Mathf.Cos(a);
            leanZ = Mathf.Sin(a);
        }

        /// <summary>citymap.js:2004-2007's `fr(n)` — a cheap deterministic hash
        /// pretending to be noise. NOT UnityEngine.Random: every vertex must land
        /// on the exact same jitter every time it's re-evaluated (stage changes,
        /// the window query, a mid-collapse re-roll), so it is a pure function of
        /// its input, not a stream.</summary>
        /// IN DOUBLE, DELIBERATELY — do not "simplify" this to Mathf.Sin and
        /// float arithmetic. This is the classic sin-fract hash, and it is
        /// pathologically precision-sensitive: `q` reaches ~43758, where
        /// float32's resolution is about 0.004, so the fractional part — which
        /// IS the hash — is what gets rounded away. A probe measured the damage:
        /// at v=1000, multiplier 12.9898, float64 gives 0.943384 and float32
        /// gives 0.474609. That is not a rounding difference, it is a different
        /// random number, and it moved a damaged vertex 1.15 m sideways.
        ///
        /// The ARGUMENT must be double too, which is why this takes a double
        /// and the call sites below use unsuffixed literals: computing
        /// `v * 12.9898f` in float32 first and widening afterwards reintroduces
        /// the same error one step earlier.
        ///
        /// Y happened to agree to ~0.003 (its multiplier is 78.233), which is
        /// exactly why this needed a probe rather than an eyeball — one of the
        /// three axes looked fine.
        public static float Fr(double n)
        {
            double q = System.Math.Sin(n) * 43758.5453;
            return (float)(q - System.Math.Floor(q));
        }

        /// <summary>
        /// Rewrites vertices [v0,v1) of a chunk mesh's position/color arrays from
        /// their PRISTINE snapshot, applying this damage look. `origPos`/`origCol`
        /// are the pristine snapshot sliced to the building's own range (length
        /// (v1-v0)*3, LOCAL indexing — citymap.js's `rec.orig.p`); `outPos`/`outCol`
        /// are the chunk's FULL live arrays (GLOBAL indexing, i.e. Mesh.vertices
        /// flattened) that get overwritten in place. citymap.js:1969-2012.
        /// </summary>
        public static void WriteBuildingVerts(
            float[] origPos, float[] origCol,
            float[] outPos, float[] outCol,
            int v0, int v1, float baseY, float top,
            float kh, float jit, float dark, float lean,
            float leanX, float leanZ, int phase)
        {
            float hgt = Mathf.Max(1f, top - baseY);
            for (int v = v0; v < v1; v++)
            {
                int o = (v - v0) * 3;
                float relY = Mathf.Max(0f, origPos[o + 1] - baseY);
                float t = Mathf.Min(1f, relY / hgt); // 0 at the base, 1 at the roofline
                float a = jit * (0.35f + 0.65f * t);  // upper floors crumble hardest
                float sh = relY * lean;               // lean shear grows with height

                outPos[v * 3] = origPos[o] + (Fr(v * 12.9898 + phase * 7.31) - 0.5f) * a + sh * leanX;
                outPos[v * 3 + 1] = baseY + relY * kh + (Fr(v * 78.233 + phase * 3.17) - 0.5f) * a * 0.5f;
                outPos[v * 3 + 2] = origPos[o + 2] + (Fr(v * 39.425 + phase * 5.53) - 0.5f) * a + sh * leanZ;

                outCol[v * 3] = origCol[o] * dark;
                outCol[v * 3 + 1] = origCol[o + 1] * dark;
                outCol[v * 3 + 2] = origCol[o + 2] * dark;
            }
        }

        /// <summary>Stage -> (kh, jit, dark, lean) for a merged-chunk building at
        /// rest (not mid-collapse). citymap.js:2014-2023 (_applyBuildingStage).</summary>
        public static void StageParams(int stage, out float kh, out float jit, out float dark, out float lean)
        {
            kh = StageH[stage];
            jit = stage == 9 ? 1.3f : stage * 0.12f;
            dark = 1f - 0.08f * stage;
            lean = stage == 9 ? 0.05f : stage * 0.028f;
        }

        /// <summary>Mid-collapse interpolation for a merged-chunk building —
        /// citymap.js:2446-2454 (the non-inst/landmark branch of _updateCollapses).
        /// `p` is clamp01(t/dur); `fall = p*p` (eased gravity: slow shear first,
        /// then it drops). `phase` re-rolls the jitter hash every ~45 ms so the
        /// shell visibly shudders as it falls (citymap.js:2452,
        /// `1 + Math.floor(c.t * 22)`).</summary>
        public static void CollapseParams(
            float t, float dur, float fromKh, float fromDark, float fromLean,
            out float kh, out float jit, out float dark, out float lean, out int phase)
        {
            float p = Mathf.Clamp01(t / dur);
            float fall = p * p;
            kh = fromKh + (0.12f - fromKh) * fall;
            jit = 0.35f + 1.05f * p;
            dark = fromDark + (0.28f - fromDark) * p;
            lean = fromLean + 0.09f * p;
            phase = 1 + Mathf.FloorToInt(t * 22f);
        }

        /// <summary>Starting parameters for a collapse, from the stage it fell
        /// from. citymap.js:2381-2389 (_startCollapse).</summary>
        public static void CollapseStart(int prevStage, float baseY, float top,
            out float dur, out float fromKh, out float fromDark, out float fromLean)
        {
            dur = 2.2f + Mathf.Min(1.3f, (top - baseY) * 0.03f);
            fromKh = StageH[prevStage];
            fromDark = 1f - 0.08f * prevStage;
            fromLean = prevStage * 0.028f;
        }

        // ---- instanced / landmark damage pose (rigid, not per-vertex) ----------
        // citymap.js:1657-1678 (_writeInstMatrix/_applyInstStage) and :1877-1908
        // (_writeLandmarkPose/_applyLandmarkStage). The whole instance/model
        // squashes in Y, tilts toward its own hash-seeded axis, and darkens —
        // no per-vertex buckle, because it's one rigid transform, not a shell.

        /// <summary>Stage -> (kh, tilt, dark) for an instanced destructible at
        /// rest. citymap.js:1678-1692.</summary>
        public static void InstStageParams(int stage, out float kh, out float tilt, out float dark)
        {
            kh = IStageH[stage];
            tilt = stage == 9 ? 0.1f : stage * 0.02f;
            dark = 1f - 0.085f * stage;
        }

        /// <summary>Stage -> (kh, tilt, dark) for a landmark model at rest.
        /// citymap.js:1892-1922. Numerically identical to InstStageParams (both
        /// read I_STAGE_H and the same 0.085 soot factor) — kept as a separate
        /// named entry point because the JS keeps them as separate methods and a
        /// `noPose` landmark (citymap.js:1810, e.g. the 152 m airport warehouse)
        /// skips the tilt/scale write entirely while still darkening.</summary>
        public static void LandmarkStageParams(int stage, out float kh, out float tilt, out float dark)
            => InstStageParams(stage, out kh, out tilt, out dark);

        /// <summary>Mid-collapse interpolation shared by instanced and landmark
        /// collapses — citymap.js:2418-2422. Squash target is 0.13 (not the 1-stage
        /// value of I_STAGE_H[9]=0.13 — same number, written as a literal in the
        /// JS) with a small tilt ramp and a decaying wobble.</summary>
        public static void InstCollapseParams(float t, float dur, float fromKh,
            out float kh, out float tilt, out float wobble)
        {
            float p = Mathf.Clamp01(t / dur);
            float fall = p * p;
            kh = fromKh + (0.13f - fromKh) * fall;
            tilt = 0.02f + 0.06f * p;
            wobble = Mathf.Sin(t * 31f) * 0.03f * (1f - p);
        }

        public static float InstCollapseDuration(float h) => 1.4f + h * 0.03f;     // citymap.js:1639
        public static float LandmarkCollapseDuration(float h) => 2.0f + h * 0.025f; // citymap.js:1849

        // ---- window query grid ---------------------------------------------------
        // citymap.js:2027-2122. Window cells sit on the pristine bottom wall ring,
        // recomputed from the SAME 3.2 m floor-height / 3.4 m bay-width grid the
        // facade props use, so fire anchors and hit tests snap to real windows.

        /// <summary>
        /// Builds the window-cell anchor list for one building from its pristine
        /// WALL-RING vertices. `wallRingPos` is `rec.orig.p` restricted to the
        /// bottom ring (the first third of the building's vertex range — see
        /// BuildTriRanges' wallTop math for why that's the wall ring boundary);
        /// pass it as (x,_,z) triples, ring-ordered. `heightAt` mirrors the JS's
        /// `this.heightAt(wx, wz)` ground-slope check so a cell buried on a hillside
        /// is skipped. citymap.js:2027-2060.
        /// </summary>
        public static List<Vector3> WindowAnchors(
            float[] ringXZ, int ringCount, float baseY, float top, float cx, float cz,
            Func<float, float, float?> heightAt)
        {
            var outPts = new List<Vector3>();
            int n = ringCount;
            float h = top - baseY;
            int nF = Mathf.Max(1, Mathf.FloorToInt(h / 3.2f));
            const int CAP = 3 * 64;
            for (int i = 0; i < n && outPts.Count < CAP; i++)
            {
                int j = (i + 1) % n;
                float x1 = ringXZ[i * 2], z1 = ringXZ[i * 2 + 1];
                float ex = ringXZ[j * 2] - x1, ez = ringXZ[j * 2 + 1] - z1;
                float len = Mathf.Sqrt(ex * ex + ez * ez);
                if (len < 4f) continue;
                float nx = -ez / len, nz = ex / len;
                float mx = x1 + ex / 2f, mz = z1 + ez / 2f;
                if ((mx - cx) * nx + (mz - cz) * nz < 0f) { nx = -nx; nz = -nz; }
                int bays = Mathf.FloorToInt(len / 3.4f);
                if (bays <= 0) continue;
                for (int k = 0; k < bays && outPts.Count < CAP; k++)
                {
                    float t = (k + 0.5f) / bays;
                    float wx = x1 + ex * t + nx * 0.5f;
                    float wz = z1 + ez * t + nz * 0.5f;
                    float? gy = heightAt(wx, wz);
                    for (int f = 1; f < nF && outPts.Count < CAP; f++)
                    {
                        float wy = baseY + 2f + f * 3.2f - 0.4f;
                        if (gy.HasValue && wy < gy.Value + 0.8f) continue; // buried on the slope
                        outPts.Add(new Vector3(wx, wy, wz));
                    }
                }
            }
            return outPts;
        }

        /// <summary>Did a round land on the actual window opening (~1.3 x 1.6 m,
        /// not the oversized 1.7 m ball nearWindow uses for big warheads)?
        /// Anchors are pristine; the wall crumbles under them, so the vertical
        /// position rides the stage's StageH. citymap.js:2079-2092.</summary>
        public static bool WindowAt(List<Vector3> anchors, int stage, float baseY,
            Vector3 p, float rh = 0.82f, float rv = 0.8f)
        {
            float rh2 = rh * rh;
            float kh = StageH[stage];
            foreach (var a in anchors)
            {
                float dx = a.x - p.x, dz = a.z - p.z;
                if (dx * dx + dz * dz > rh2) continue;
                float dy = baseY + (a.y - baseY) * kh - p.y;
                if (Mathf.Abs(dy) < rv) return true;
            }
            return false;
        }

        /// <summary>Did a bullet land near a window cell at all (the generous 1.7 m
        /// ball used for warheads/big-caliber rounds)? citymap.js:2102-2121.</summary>
        public static bool NearWindow(List<Vector3> anchors, int stage, float baseY, Vector3 p)
        {
            const float r2 = 1.7f * 1.7f;
            float kh = StageH[stage];
            foreach (var a in anchors)
            {
                float dx = a.x - p.x;
                float dy = baseY + (a.y - baseY) * kh - p.y;
                float dz = a.z - p.z;
                if (dx * dx + dy * dy + dz * dz < r2) return true;
            }
            return false;
        }

        // ---- roof carve ------------------------------------------------------------
        // A hole that reaches the roofline takes the roof deck with it — the shell
        // itself (breakwall.js, not ported here) carves the wall opening; this is
        // citymap.js's OWN piece of it, the roof half: _carveRoof, citymap.js:2316-
        // 2356. Pure index-buffer surgery: a roof triangle inside the blast sphere
        // is degenerated (its two trailing corners collapse onto the first), which
        // is how every hole here is "removed" without touching the vertex buffer or
        // the chunk's BVH structure.

        /// <summary>
        /// Degenerates every roof triangle in [r0,r1) whose PRISTINE centroid falls
        /// inside radius*1.15 of the hole. `origPosLocal` is the building's pristine
        /// snapshot (`rec.orig.p`, LOCAL indexing, length (v1-v0)*3); `indexArray` is
        /// the chunk's live index buffer (mutated in place). Returns true if
        /// anything was cut (caller must then mark the index buffer dirty and
        /// refit the BVH — citymap.js:2366-2368). Caller must first check
        /// `hole.y >= top - hole.r` (citymap.js:2318) — a hole that doesn't reach
        /// the roofline never touches it.
        /// </summary>
        public static bool CarveRoof(
            int[] indexArray, float[] origPosLocal, int v0, int r0, int r1,
            float holeX, float holeY, float holeZ, float holeR)
        {
            if (r1 < 0 || r0 >= r1) return false;
            float r2 = (holeR * 1.15f) * (holeR * 1.15f);
            bool cut = false;
            for (int i = r0; i < r1; i += 3)
            {
                int a = indexArray[i], b = indexArray[i + 1], c = indexArray[i + 2];
                if (a == b) continue; // already gone
                float x = 0f, y = 0f, z = 0f;
                bool bad = false;
                int[] corners = { a, b, c };
                foreach (int v in corners)
                {
                    int o = (v - v0) * 3;
                    if (o < 0 || o >= origPosLocal.Length) { bad = true; break; }
                    x += origPosLocal[o]; y += origPosLocal[o + 1]; z += origPosLocal[o + 2];
                }
                if (bad) continue;
                x /= 3f; y /= 3f; z /= 3f;
                float dx = x - holeX, dy = y - holeY, dz = z - holeZ;
                if (dx * dx + dy * dy + dz * dz > r2) continue;
                indexArray[i + 1] = a;
                indexArray[i + 2] = a;
                cut = true;
            }
            return cut;
        }

        // ---- saved-damage HP table migration ----------------------------------------
        // citymap.js:2913-2932 (_migrateDamageScale). Run ONCE per browser (guarded
        // by DmgScaleKey): old absolute-HP saves are rescaled by the ratio of the
        // OLD maxHp formula to the current one, so a stage-4 block doesn't suddenly
        // read as stage-8 just because the HP table changed under it.

        /// <summary>The HP-table formula this migration rescales FROM —
        /// citymap.js:2925 (`oldMax`). `h` = building height, `area` = footprint
        /// bounding-box area * 0.8.</summary>
        public static float OldMaxHp(float h, float area)
            => Mathf.Clamp(60f + h * 8f + Mathf.Max(1f, area) * 0.1f, 100f, 800f);

        /// <summary>citymap.js:2926. `area` must already be max(1, w*d*0.8) —
        /// see OldMaxHp.</summary>
        public static float RescaleHp(float hp, float oldMax, float maxHp)
            => Mathf.Round(Mathf.Clamp(hp / oldMax * maxHp, 0f, maxHp));

        // ---- remote damage DB merge --------------------------------------------------
        // citymap.js:1368-1413 (_fetchRemoteDamage). "More damage wins" for hp;
        // holes are a UNION (everyone flies the same broken city). Pure data merge —
        // the actual `fetch('/api/damage')` call is this file's caller's job
        // (UnityWebRequest against the same Express+Mongo endpoint); this is just
        // the merge arithmetic, which is where the actual bugs would hide.

        public struct RemoteRow
        {
            public float Hp;
            public float[][] Holes; // each hole: [x,y,z,r]
        }

        /// <summary>
        /// Merges `remote` into `localDmg`/`localHoles` in place and adds every key
        /// that now needs pushing back up to `dirty`. Hole lists are capped at the
        /// last 40 (citymap.js:1402, `.slice(-40)`).
        /// </summary>
        public static void MergeRemoteDamage(
            Dictionary<string, float> localDmg,
            Dictionary<string, List<float[]>> localHoles,
            HashSet<string> dirty,
            Dictionary<string, RemoteRow> remote)
        {
            foreach (var kv in remote)
            {
                string key = kv.Key;
                float hp = kv.Value.Hp;
                if (!localDmg.TryGetValue(key, out float local) || hp < local) localDmg[key] = hp;

                var rHoles = kv.Value.Holes;
                if (rHoles != null && rHoles.Length > 0)
                {
                    var mine = localHoles.TryGetValue(key, out var m) ? m : new List<float[]>();
                    var seen = new HashSet<string>();
                    foreach (var h in mine) seen.Add($"{h[0]}_{h[1]}_{h[2]}");
                    var merged = new List<float[]>(mine);
                    foreach (var h in rHoles)
                    {
                        string hk = $"{h[0]}_{h[1]}_{h[2]}";
                        if (seen.Add(hk)) merged.Add(h);
                    }
                    if (merged.Count != mine.Count)
                        localHoles[key] = merged.Count > 40
                            ? merged.GetRange(merged.Count - 40, 40)
                            : merged;
                }
            }
            foreach (var key in new List<string>(localDmg.Keys))
            {
                bool hasRemote = remote.TryGetValue(key, out var row);
                int localHoleCount = localHoles.TryGetValue(key, out var lh) ? lh.Count : 0;
                int remoteHoleCount = hasRemote && row.Holes != null ? row.Holes.Length : 0;
                if (!hasRemote || row.Hp > localDmg[key] || remoteHoleCount < localHoleCount)
                    dirty.Add(key);
            }
        }

        // ---- real street addresses --------------------------------------------------
        // citymap.js:2966-3017 (_loadAddresses + addressOf). An 8 m spatial hash
        // over building centroids, loaded from tbilisi/addresses.json.gz — same
        // gzip-or-plain sniff CityData/BuildingFacts already use (0x1f 0x8b magic).

        public sealed class AddressIndex
        {
            readonly Dictionary<long, List<(float x, float z, string label)>> _grid = new();
            public int Count { get; private set; }
            public int StreetCount { get; private set; }

            const float Cell = 8f;
            static long Key(int gi, int gj) => (long)gi * 100003 + gj;

            public static AddressIndex Load(string path = null)
            {
                path ??= Path.Combine(Application.streamingAssetsPath, "tbilisi", "addresses.json.gz");
                var idx = new AddressIndex();
                if (!File.Exists(path))
                {
                    Debug.Log($"ADDR missing {path} — damage toasts fall back to a building id");
                    return idx;
                }
                using var fs = File.OpenRead(path);
                Stream body = fs;
                int b0 = fs.ReadByte(), b1 = fs.ReadByte();
                fs.Position = 0;
                if (b0 == 0x1f && b1 == 0x8b) body = new GZipStream(fs, CompressionMode.Decompress);
                using var sr = new StreamReader(body);
                using var jr = new JsonTextReader(sr);

                var streets = new List<string>();
                var items = new List<(float x, float z, int streetIdx, int num)>();
                while (jr.Read())
                {
                    if (jr.TokenType != JsonToken.PropertyName) continue;
                    switch ((string)jr.Value)
                    {
                        case "streets":
                            jr.Read();
                            while (jr.Read() && jr.TokenType != JsonToken.EndArray)
                                streets.Add((string)jr.Value);
                            break;
                        case "items":
                            jr.Read(); // StartArray — the outer items list
                            while (jr.Read() && jr.TokenType != JsonToken.EndArray)
                            {
                                // jr is now on THIS item's own StartArray —
                                // each item is itself a 4-element JSON array:
                                // [xd, zd, streetIdx, num-or-null]
                                jr.Read(); float xd = Convert.ToSingle(jr.Value);
                                jr.Read(); float zd = Convert.ToSingle(jr.Value);
                                jr.Read(); int si = Convert.ToInt32(jr.Value);
                                jr.Read();
                                int num = jr.TokenType == JsonToken.Null ? 0 : Convert.ToInt32(jr.Value);
                                jr.Read(); // this item's own EndArray
                                items.Add((xd / 10f, zd / 10f, si, num));
                            }
                            break;
                        default: jr.Skip(); break;
                    }
                }
                idx.StreetCount = streets.Count;
                idx.Count = items.Count;
                foreach (var (x, z, si, num) in items)
                {
                    string label = num != 0 ? $"{streets[si]} {num}" : streets[si];
                    int gi = Mathf.RoundToInt(x / Cell), gj = Mathf.RoundToInt(z / Cell);
                    long k = Key(gi, gj);
                    if (!idx._grid.TryGetValue(k, out var cell)) idx._grid[k] = cell = new List<(float, float, string)>();
                    cell.Add((x, z, label));
                }
                return idx;
            }

            /// <summary>Nearest address within ~12 m of a building centroid, or ""
            /// if nothing matched. citymap.js:2996-3017.</summary>
            public string Query(float cx, float cz)
            {
                int gi = Mathf.RoundToInt(cx / Cell), gj = Mathf.RoundToInt(cz / Cell);
                string best = "";
                float bestD = 12f * 12f;
                for (int di = -1; di <= 1; di++)
                for (int dj = -1; dj <= 1; dj++)
                {
                    if (!_grid.TryGetValue(Key(gi + di, gj + dj), out var cell)) continue;
                    foreach (var e in cell)
                    {
                        float d = (e.x - cx) * (e.x - cx) + (e.z - cz) * (e.z - cz);
                        if (d < bestD) { bestD = d; best = e.label; }
                    }
                }
                return best;
            }
        }
    }
}

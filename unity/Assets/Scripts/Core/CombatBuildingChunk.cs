using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Combat;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.Core
{
    // SPLIT OUT OF CombatWorldAdapter.cs, and this is not a style preference.
    //
    // A MonoBehaviour must live in a file named after it or Unity cannot bind a
    // script ASSET to it. CombatWorldAdapter.cs declared three of them
    // (CombatTerrainTag, CombatBuildingChunk, PlayableSceneBoot) in a file named
    // after a fourth, non-MonoBehaviour class, so AddComponent<T>() produced
    // components that serialised with `m_Script: {fileID: 187830060}` and NO
    // guid, falling back to m_EditorClassIdentifier. The symptom was
    // PlayableProbe failing `playableSceneBootExists` on a freshly-built scene
    // that demonstrably contained a GameObject named PlayableSceneBoot and 605
    // chunks' worth of baked RangeStart data: the YAML was there, the live
    // component was not, so Install() never ran and CombatSystems/CrashFactory/
    // the hero body/the UI all came back missing TOGETHER.

    /// <summary>
    /// Per-chunk building damage. One of these sits next to the MeshCollider
    /// BuildPlayableScene.cs adds to every building chunk GameObject
    /// (BuildingBuilder.Chunk's Mesh), with RangeStart/RangeCount/Heights
    /// copied straight off that Chunk at build time (plain serializable
    /// int[]/float[] — survives the scene save/reload the rest of the runtime
    /// wiring cannot, see CombatBootstrap.cs's own header on that split).
    /// </summary>
    public sealed class CombatBuildingChunk : MonoBehaviour
    {
        public int[] RangeStart;
        public int[] RangeCount;
        public float[] Heights;

        // ---- hole-carving / window-query bakes (City/BuildingBuilder.cs's Chunk,
        // copied on 1:1 aligned with RangeStart/RangeCount by Editor/
        // BuildPlayableScene.cs — see that file and BuildingBuilder.Chunk's own
        // doc comments). ----
        public int[] BuildingIndex;
        public float[] WallBase;
        public float[] WallTop;
        public Color32[] ShellTint;
        public int[] ShellWallBucket;
        public float[] FootprintFlat;
        public int[] FootprintOffset;
        public int[] FootprintCount;

        Mesh _mesh;
        int[] _triangles;     // concatenated across every submesh, in RaycastHit.triangleIndex order
        int[] _subIndexStart; // cumulative length, INDEX units, per submesh — length subMeshCount+1
        Vector3[] _baseVerts;
        Color32[] _baseColors;
        Vector3[] _liveVerts;
        Color32[] _liveColors;
        float[] _hp;
        float[] _maxHp;       // src/citymap.js:5344's real per-building HP table — see MaxHpOf
        byte[] _stage;        // 0..9, last crumble stage actually painted
        float[] _cx, _cz, _top, _span;
        bool _ready;

        Dictionary<int, BuildingDamage.TriRange> _triRanges;
        Dictionary<int, BreakWall.Shell> _shells;
        Dictionary<int, List<Vector3>> _winPos;
        Dictionary<int, float[]> _winNormal;
        Dictionary<int, bool[]> _winBroken;

        const int Stages = 10;

        void EnsureInit()
        {
            if (_ready) return;
            _ready = true;
            var mf = GetComponent<MeshFilter>();
            _mesh = mf != null ? mf.sharedMesh : null;
            int n = RangeStart != null ? RangeStart.Length : 0;
            _hp = new float[n];
            _maxHp = new float[n];
            _stage = new byte[n];
            _cx = new float[n]; _cz = new float[n]; _top = new float[n]; _span = new float[n];

            if (_mesh == null) return;

            int subCount = Mathf.Max(1, _mesh.subMeshCount);
            var tris = new List<int>(_mesh.vertexCount * 2);
            _subIndexStart = new int[subCount + 1];
            for (int s = 0; s < subCount; s++)
            {
                var sub = _mesh.GetTriangles(s);
                tris.AddRange(sub);
                _subIndexStart[s + 1] = _subIndexStart[s] + sub.Length;
            }
            _triangles = tris.ToArray();

            _baseVerts = _mesh.vertices;
            var cols = _mesh.colors32;
            _baseColors = (cols != null && cols.Length == _baseVerts.Length) ? cols : null;
            _liveVerts = (Vector3[])_baseVerts.Clone();
            _liveColors = _baseColors != null ? (Color32[])_baseColors.Clone() : null;

            for (int bi = 0; bi < n; bi++)
            {
                int start = RangeStart[bi], count = RangeCount[bi];
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                float sumX = 0f, sumZ = 0f, maxY = float.MinValue;
                for (int v = start; v < start + count && v < _baseVerts.Length; v++)
                {
                    var p = _baseVerts[v];
                    sumX += p.x; sumZ += p.z;
                    if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
                    if (p.z < minZ) minZ = p.z; if (p.z > maxZ) maxZ = p.z;
                    if (p.y > maxY) maxY = p.y;
                }
                _cx[bi] = count > 0 ? sumX / count : 0f;
                _cz[bi] = count > 0 ? sumZ / count : 0f;
                _top[bi] = maxY;
                _span[bi] = Mathf.Max(2f, Mathf.Max(maxX - minX, maxZ - minZ));
                float h = (Heights != null && bi < Heights.Length) ? Heights[bi] : 8f;
                _maxHp[bi] = MaxHpOf(h, FootprintAreaOf(bi));
                _hp[bi] = _maxHp[bi];
            }
        }

        // =====================================================================
        // ---- the real HP table — src/citymap.js:5344 ------------------------
        //
        //     const maxHp = Math.round(clamp(180 + h * 14 + area * 0.35, 280, 3200));
        //
        // with the JS's own comment on the line above it: "HP scales with the
        // real MASS of the thing (height x footprint): a rocket (260) must blow
        // a hole in a building, not delete it. A small house still goes down in
        // two, a Soviet block takes five, a tower more than ten."
        //
        // This file previously used `220 + h * 26` — no footprint term, no
        // clamp, and no rounding. That is neither the current JS formula NOR
        // the pre-migration one City/BuildingDamage.OldMaxHp already records
        // (`clamp(60 + h*8 + area*0.1, 100, 800)`, citymap.js:2925), so it was
        // not a stale-table artefact either; it was simply a different number.
        // It is load-bearing twice over: HpFrac/StageOf drive which of the ten
        // crumble stages a building paints, and the damage-persistence restore
        // gate is `saved >= maxHp` (citymap.js:2933), so a wrong table silently
        // changes how many rounds a building takes AND which saved rows count
        // as damage at all.
        //
        // `area` is the footprint ring's own polygon area — citymap.js:4649's
        // `Math.abs(THREE.ShapeUtils.area(contour))` over exactly the ring
        // FootprintRingGameOf hands back (both read the same packed building
        // record). Summed in DOUBLE: 2.1M real coordinates reach |x| ~264 km in
        // the shared frame and a float accumulator over shoelace terms has
        // already drifted measurably in this project once (RiverSheet, 368 m2).
        // Mirroring X only flips the shoelace's SIGN, so |area| is identical in
        // either frame and the game-frame ring is fine to use here.
        // =====================================================================
        internal static float MaxHpOf(double h, double area)
            => JsMath.Round(System.Math.Clamp(180.0 + h * 14.0 + area * 0.35, 280.0, 3200.0));

        /// <summary>|shoelace| of entry bi's footprint ring, in double. Returns 0
        /// when no footprint was baked into this chunk (a pre-footprint scene, or
        /// a bare Build()/probe-built chunk) — the clamp's own 280 floor then
        /// stands in, which is the JS's value for any building whose area term
        /// cannot push it past the floor anyway.</summary>
        double FootprintAreaOf(int bi)
        {
            if (FootprintOffset == null || FootprintCount == null || FootprintFlat == null) return 0.0;
            if (bi < 0 || bi >= FootprintOffset.Length) return 0.0;
            int off = FootprintOffset[bi], cnt = FootprintCount[bi];
            if (cnt < 3 || (off + cnt) * 2 > FootprintFlat.Length) return 0.0;
            // THREE.ShapeUtils.area: sum over p=n-1,q=0.. of (x_p*y_q - x_q*y_p), * 0.5
            double a = 0.0;
            for (int q = 0, pr = cnt - 1; q < cnt; pr = q++)
            {
                double xp = FootprintFlat[(off + pr) * 2], yp = FootprintFlat[(off + pr) * 2 + 1];
                double xq = FootprintFlat[(off + q) * 2], yq = FootprintFlat[(off + q) * 2 + 1];
                a += xp * yq - xq * yp;
            }
            return System.Math.Abs(a * 0.5);
        }

        /// <summary>Entry bi's own HP ceiling (src/citymap.js:5344's `maxHp`).
        /// Calls EnsureInit, like every other derived accessor on this class must
        /// (see this file's "lazy-init accessor" lesson in docs/QUALITY_BAR.md).</summary>
        public float MaxHpOf(int bi)
        {
            EnsureInit();
            return (_maxHp != null && bi >= 0 && bi < _maxHp.Length) ? _maxHp[bi] : 280f;
        }

        int FindBuilding(int vertexIndex)
        {
            if (RangeStart == null || RangeStart.Length == 0) return -1;
            int lo = 0, hi = RangeStart.Length - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (RangeStart[mid] <= vertexIndex) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans;
        }

        /// <summary>Resolves a raw triangle hit on this chunk's collider to a building
        /// index, or -1 (hit the chunk but landed off every known range — e.g. a
        /// building BuildRich routed to a kit prop and never extruded).</summary>
        public int BuildingAt(int triangleIndex)
        {
            EnsureInit();
            if (_triangles == null || triangleIndex < 0) return -1;
            int t0 = triangleIndex * 3;
            if (t0 + 0 >= _triangles.Length) return -1;
            int vertexIndex = _triangles[t0];
            return FindBuilding(vertexIndex);
        }

        public float HpFrac(int bi) { EnsureInit(); return (bi < 0 || bi >= _hp.Length || _hp[bi] <= 0f) ? 0f : Mathf.Clamp01(_hp[bi] / Mathf.Max(1f, MaxHpOf(bi))); }
        // EnsureInit() FIRST — these four are derived from the mesh in EnsureInit,
        // and without it they silently fall back to transform.position and a span
        // of 8 m. That is not cosmetic: CollectPanes uses Cx/Cz/Span for its
        // proximity gate, so on a chunk nothing had touched yet the gate measured
        // from (0,0) with span 8 (gate^2 = 324) against a blast 20 m away
        // (d2 = 400) and skipped the building — meaning the FIRST rocket to hit a
        // pristine building broke no glass at all. Every later hit worked, because
        // StageOf/HpFrac had initialised the chunk by then, which is exactly the
        // kind of first-time-only bug that survives playtesting. A probe caught
        // it: BlastWindows returned 0 panes where its JS counterpart returned 10.
        public float CxOf(int bi) { EnsureInit(); return (_cx != null && bi >= 0 && bi < _cx.Length) ? _cx[bi] : transform.position.x; }
        public float CzOf(int bi) { EnsureInit(); return (_cz != null && bi >= 0 && bi < _cz.Length) ? _cz[bi] : transform.position.z; }
        public float TopOf(int bi) { EnsureInit(); return (_top != null && bi >= 0 && bi < _top.Length) ? _top[bi] : transform.position.y; }
        public float SpanOf(int bi) { EnsureInit(); return (_span != null && bi >= 0 && bi < _span.Length) ? _span[bi] : 8f; }
        public int BuildingCount => RangeStart != null ? RangeStart.Length : 0;
        public int StageOf(int bi) { EnsureInit(); return (_stage != null && bi >= 0 && bi < _stage.Length) ? _stage[bi] : 0; }
        public int OrigBuildingIndexOf(int bi) => (BuildingIndex != null && bi >= 0 && bi < BuildingIndex.Length) ? BuildingIndex[bi] : -1;

        /// <summary>The fire/smolder registry key for entry bi — the JS's
        /// `rec.bi`, i.e. the ORIGINAL city-wide building index, never the
        /// per-chunk one. It has to be the original: Fx/FireSystem's burning and
        /// smolder maps are ONE shared registry for the whole city (as the JS's
        /// single FireFX is), and `bi` restarts at 0 in every one of the ~600
        /// chunks, so a chunk-local key would have building 0 of Vake evict the
        /// mound fire of building 0 of Gldani. Also the MoundAnchors seed
        /// (citymap.js:2661 `+ rec.bi` in the anchor angle), so the anchor ring
        /// matches the JS's for the same building.</summary>
        internal int BurnKeyOf(int bi)
        {
            int obi = OrigBuildingIndexOf(bi);
            return obi >= 0 ? obi : bi;
        }

        /// <summary>citymap.js:2519's SECOND key, `${rec.bi}:fire` — the mound's
        /// flames live alongside its smoulder, so the two emitters must not share
        /// a key. The JS gets a distinct namespace for free (a string); this port
        /// buys one with a bias far above any building index (169,004 buildings
        /// in city.json, so 1&lt;&lt;24 cannot collide) and the plane-part keys
        /// stay out of the way on the negative side (see
        /// Core/CombatWorldAdapter.BurnKeyId).</summary>
        internal const int FireKeyBias = 1 << 24;
        internal int FireKeyOf(int bi) => FireKeyBias + BurnKeyOf(bi);
        /// <summary>World Y the wall quads start at — NOT (Top - Height); see
        /// BuildingBuilder.Chunk.WallBase's own doc on why that shortcut is wrong
        /// on a slope.</summary>
        public float WallBaseOf(int bi) => (WallBase != null && bi >= 0 && bi < WallBase.Length) ? WallBase[bi] : TopOf(bi) - Mathf.Max(2f, bi >= 0 && Heights != null && bi < Heights.Length ? Heights[bi] : 8f);
        public float WallTopOf(int bi) => (WallTop != null && bi >= 0 && bi < WallTop.Length) ? WallTop[bi] : TopOf(bi);
        public Color32 TintOf(int bi) => (ShellTint != null && bi >= 0 && bi < ShellTint.Length) ? ShellTint[bi] : new Color32(190, 185, 178, 255);

        /// <summary>Entry bi's own footprint ring, GAME frame (x,z pairs) — same
        /// convention as CityData.Footprint.Flat / BreakWall.BuildingWallInput.
        /// FootprintFlatGameFrame. Null if no footprint was baked (an older scene
        /// built before this port, or a plain Build() chunk).</summary>
        public float[] FootprintRingGameOf(int bi)
        {
            if (FootprintOffset == null || FootprintCount == null || bi < 0 || bi >= FootprintOffset.Length) return null;
            int off = FootprintOffset[bi], cnt = FootprintCount[bi];
            if (cnt < 3 || FootprintFlat == null || (off + cnt) * 2 > FootprintFlat.Length) return null;
            var r = new float[cnt * 2];
            System.Array.Copy(FootprintFlat, off * 2, r, 0, cnt * 2);
            return r;
        }

        /// <summary>Same ring, mirrored into Unity/mesh space (X negated) — the
        /// frame City/BuildingDamage.cs's WindowAnchors and this chunk's own
        /// CxOf/CzOf already operate in (see that file's class header: "this
        /// operates on vertex arrays ALREADY in Unity space").</summary>
        public float[] FootprintRingUnityOf(int bi)
        {
            var g = FootprintRingGameOf(bi);
            if (g == null) return null;
            var u = new float[g.Length];
            for (int i = 0; i < g.Length; i += 2) { u[i] = -g[i]; u[i + 1] = g[i + 1]; }
            return u;
        }

        /// <summary>Lazy per-chunk wall/roof index-buffer ranges for every building.
        /// NOT City/BuildingDamage.BuildTriRanges — that method's wallTop formula
        /// (`v0 + floor((v1-v0)/3)*2`, "two shared rings = 2/3 of the vertices")
        /// is ported from citymap.js's own SHARED-ring mesh, which is not the
        /// mesh this Unity port builds. City/BuildingBuilder.cs's Build()/
        /// BuildRich() both lay a building out as UNSHARED per-edge wall quads
        /// (4 fresh verts per edge = 4*vc wall vertices, own 4 verts each — see
        /// their "walls: a quad per edge" comments) followed by a 1+vc roof fan
        /// — Combat/BreakWall.cs's own class header flags this exact same
        /// shared-ring-vs-per-edge-quad mismatch for itself. Applying the
        /// citymap.js formula to THIS geometry puts wallTop at ~2/3 of the range
        /// when the true wall/roof boundary sits at 4*vc/(5*vc+1) (~80% for a
        /// typical footprint) — a four-cornered building's own LAST wall edge
        /// would misclassify as roof. Recomputed here against the real vertex
        /// count (FootprintCount, baked by BuildingBuilder.Chunk.AddBuildingFacts)
        /// instead of calling that method. Reuses this class's own FindBuilding
        /// binary search, same approach citymap.js:2232's buildingFromFace uses.</summary>
        Dictionary<int, BuildingDamage.TriRange> TriRanges()
        {
            if (_triRanges != null) return _triRanges;
            EnsureInit();
            var map = new Dictionary<int, BuildingDamage.TriRange>();
            if (_triangles != null)
            {
                int tris = _triangles.Length / 3;
                for (int f = 0; f < tris; f++)
                {
                    int i = f * 3;
                    int bi = FindBuilding(_triangles[i]);
                    if (bi < 0) continue;
                    int vc = (FootprintCount != null && bi < FootprintCount.Length && FootprintCount[bi] >= 3)
                        ? FootprintCount[bi]
                        : Mathf.Max(3, (RangeCount[bi] - 1) / 5); // fallback: total = 5*vc+1
                    int wallTop = RangeStart[bi] + 4 * vc;
                    bool wall = _triangles[i] < wallTop && _triangles[i + 1] < wallTop && _triangles[i + 2] < wallTop;
                    if (!map.TryGetValue(bi, out var e))
                        e = new BuildingDamage.TriRange { W0 = int.MaxValue, W1 = -1, R0 = int.MaxValue, R1 = -1 };
                    if (wall) { if (i < e.W0) e.W0 = i; if (i + 3 > e.W1) e.W1 = i + 3; e.HasWall = true; }
                    else { if (i < e.R0) e.R0 = i; if (i + 3 > e.R1) e.R1 = i + 3; e.HasRoof = true; }
                    map[bi] = e;
                }
            }
            _triRanges = map;
            return map;
        }

        /// <summary>Writes the (possibly just-degenerated) slice of `_triangles`
        /// covering INDEX range [i0,i1) back onto the real submesh it lives in —
        /// every building's own wall/roof range falls entirely inside one submesh
        /// by construction (BuildingBuilder.cs appends one building at a time into
        /// one bucket's triangle list), so exactly one submesh ever needs this.</summary>
        void PushSubmeshRange(int i0, int i1)
        {
            if (_mesh == null || _subIndexStart == null) return;
            for (int s = 0; s < _subIndexStart.Length - 1; s++)
            {
                if (i0 >= _subIndexStart[s] && i1 <= _subIndexStart[s + 1])
                {
                    int len = _subIndexStart[s + 1] - _subIndexStart[s];
                    var arr = new int[len];
                    System.Array.Copy(_triangles, _subIndexStart[s], arr, 0, len);
                    _mesh.SetTriangles(arr, s);
                    return;
                }
            }
        }

        /// <summary>Steps building bi's ORIGINAL flat wall quad aside — degenerate,
        /// never deleted, so the chunk's vertex/index layout (and RaycastHit.
        /// triangleIndex addressing) never moves. src/citymap.js:2272-2281
        /// _ensureShell's own trick, so the carved Combat/BreakWall.cs shell reads
        /// as a real hole instead of a textured overlay on a still-solid wall.</summary>
        void DegenerateWall(int bi)
        {
            if (!TriRanges().TryGetValue(bi, out var range) || !range.HasWall) return;
            for (int i = range.W0; i + 2 < _triangles.Length && i < range.W1; i += 3)
            { _triangles[i + 1] = _triangles[i]; _triangles[i + 2] = _triangles[i]; }
            PushSubmeshRange(range.W0, range.W1);
        }

        /// <summary>Pristine LOCAL-indexed positions for building bi's own vertex
        /// range — City/BuildingDamage.CarveRoof's `origPosLocal` input.</summary>
        float[] OrigLocalPos(int bi)
        {
            int v0 = RangeStart[bi], v1 = v0 + RangeCount[bi];
            var o = new float[(v1 - v0) * 3];
            for (int v = v0; v < v1 && v < _baseVerts.Length; v++)
            {
                var p = _baseVerts[v];
                int k = (v - v0) * 3;
                o[k] = p.x; o[k + 1] = p.y; o[k + 2] = p.z;
            }
            return o;
        }

        /// <summary>A hole that reaches the roofline takes the roof deck with it —
        /// City/BuildingDamage.CarveRoof, the roof half of src/citymap.js's
        /// _carveRoof (the wall half is Combat/BreakWall.cs's own shell carve).</summary>
        void CarveRoofFor(int bi, float hx, float hy, float hz, float hr)
        {
            if (!TriRanges().TryGetValue(bi, out var range) || !range.HasRoof) return;
            if (hy < WallTopOf(bi) - hr) return; // citymap.js:2318 — must reach the roofline
            var orig = OrigLocalPos(bi);
            bool cut = BuildingDamage.CarveRoof(_triangles, orig, RangeStart[bi], range.R0, range.R1, hx, hy, hz, hr);
            if (cut) PushSubmeshRange(range.R0, range.R1);
        }

        /// <summary>Cached per-building window-anchor grid (position + facade
        /// normal + a "already broken" mask) — City/BuildingDamage.WindowAnchors'
        /// position math, duplicated here (not called directly) ONLY because that
        /// method discards the per-edge normal BlastWindows/StripPropsAt need to
        /// hand back to Combat/BreakingGlass.cs; computed once, kept for the life
        /// of the chunk, exactly like the JS keeps `rec.win` lazily off `rec`.</summary>
        public (List<Vector3> pos, float[] normal, bool[] broken) WindowDataOf(int bi, System.Func<float, float, float?> heightAt)
        {
            EnsureInit();
            _winPos ??= new Dictionary<int, List<Vector3>>();
            _winNormal ??= new Dictionary<int, float[]>();
            _winBroken ??= new Dictionary<int, bool[]>();
            if (_winPos.TryGetValue(bi, out var cachedPos)) return (cachedPos, _winNormal[bi], _winBroken[bi]);

            var pos = new List<Vector3>();
            var nrm = new List<float>();
            var ring = FootprintRingUnityOf(bi);
            if (ring != null && ring.Length >= 6)
                CombatWorldAdapter.ComputeWindowAnchors(ring, ring.Length / 2, WallBaseOf(bi), WallTopOf(bi), CxOf(bi), CzOf(bi), heightAt, pos, nrm);

            _winPos[bi] = pos;
            var normalArr = nrm.ToArray();
            _winNormal[bi] = normalArr;
            var broken = new bool[pos.Count];
            _winBroken[bi] = broken;
            return (pos, normalArr, broken);
        }

        /// <summary>Builds (and caches) building bi's tessellated wall shell — the
        /// first warhead a building takes, src/citymap.js:2251 _ensureShell. Null
        /// forever after a first failed attempt (no footprint ring baked, or the
        /// building is already rubble) — exactly like `rec.shell = null` staying
        /// null in the JS.</summary>
        public BreakWall.Shell EnsureShell(int bi, Material fallbackWallMat, Material linMat)
        {
            EnsureInit();
            _shells ??= new Dictionary<int, BreakWall.Shell>();
            if (_shells.TryGetValue(bi, out var existing)) return existing;
            if (bi < 0 || bi >= BuildingCount || StageOf(bi) >= Stages - 1) { _shells[bi] = null; return null; }

            var ring = FootprintRingGameOf(bi);
            if (ring == null)
            {
                _shells[bi] = null;
                return null;
            }

            Material wallMat = fallbackWallMat;
            var mr = GetComponent<MeshRenderer>();
            if (ShellWallBucket != null && bi < ShellWallBucket.Length && ShellWallBucket[bi] >= 0
                && mr != null && mr.sharedMaterials != null && ShellWallBucket[bi] < mr.sharedMaterials.Length)
                wallMat = mr.sharedMaterials[ShellWallBucket[bi]];

            var input = new BreakWall.BuildingWallInput
            {
                FootprintFlatGameFrame = ring,
                Base = WallBaseOf(bi),
                Top = WallTopOf(bi),
                Tint = TintOf(bi),
                CxGame = -CxOf(bi), // BuildShell mirrors this back to CxOf(bi) internally
                CzGame = CzOf(bi),
            };
            var shell = BreakWall.BuildShell(input, wallMat, linMat);
            if (shell != null)
            {
                DegenerateWall(bi);
                var wallGo = new GameObject($"shell_wall_{bi}");
                wallGo.transform.SetParent(transform, false);
                wallGo.AddComponent<MeshFilter>().sharedMesh = shell.Mesh;
                wallGo.AddComponent<MeshRenderer>().sharedMaterial = wallMat;
                var liningGo = new GameObject($"shell_lining_{bi}");
                liningGo.transform.SetParent(transform, false);
                liningGo.AddComponent<MeshFilter>().sharedMesh = shell.Lining;
                liningGo.AddComponent<MeshRenderer>().sharedMaterial = linMat;
            }
            _shells[bi] = shell;
            return shell;
        }

        /// <summary>Tears a hole in building bi's facade, src/citymap.js:2294
        /// punchHole. Returns the hole (a real, non-null record) or null — stage-9
        /// rubble, or no shell could be built (no footprint baked for bi).</summary>
        public BreakWall.BlastHole? PunchHole(int bi, Vector3 p, float radius, Material fallbackWallMat, Material linMat)
        {
            EnsureInit();
            if (bi < 0 || bi >= BuildingCount || StageOf(bi) >= Stages - 1) return null;
            var shell = EnsureShell(bi, fallbackWallMat, linMat);
            if (shell == null) return null;

            float baseY = WallBaseOf(bi), top = WallTopOf(bi);
            float kh = BuildingDamage.StageH[StageOf(bi)];
            if (kh <= 0f) kh = 1f;
            var hole = new BreakWall.BlastHole
            {
                X = p.x,
                Y = Mathf.Min(baseY + (p.y - baseY) / kh, top + radius * 0.4f),
                Z = p.z,
                R = radius,
            };
            shell.Holes.Add(hole);
            if (shell.Holes.Count > 40) shell.Holes.RemoveAt(0); // citymap.js:2307
            BreakWall.Carve(shell);
            CarveRoofFor(bi, hole.X, hole.Y, hole.Z, hole.R);
            return hole;
        }

        /// <summary>Applies `damage` to building `bi`. Returns (changed, destroyed) —
        /// changed = a new crumble stage was actually painted this hit.</summary>
        public (bool changed, bool destroyed) ApplyDamage(int bi, float damage)
        {
            EnsureInit();
            if (bi < 0 || bi >= _hp.Length) return (false, false);
            if (_hp[bi] <= 0f) return (false, true); // already gone
            _hp[bi] = Mathf.Max(0f, _hp[bi] - damage);
            // src/citymap.js:1498-1501 `_stageOf(rec)`:
            //     rec.hp <= 0 ? 9 : Math.min(8, Math.floor((1 - rec.hp / rec.maxHp) * 9))
            // NINE, not ten, and capped at EIGHT: stage 9 is "collapsed" and is
            // reachable only by running the HP to zero. This read
            // `floor((1 - frac) * Stages)` clamped to Stages-1, i.e. *10 capped
            // at 9 — so a building with 5% of its hull left came out at
            // floor(9.5) = 9 and COLLAPSED while it still had HP, and every
            // stage above about 6 was one step ahead of the JS.
            float frac = HpFrac(bi);
            int stage = _hp[bi] <= 0f
                ? Stages - 1
                : Mathf.Min(Stages - 2, Mathf.Max(0, Mathf.FloorToInt((1f - frac) * (Stages - 1))));
            bool changed = stage != _stage[bi] || (_hp[bi] <= 0f && _stage[bi] != Stages - 1);
            if (changed)
            {
                int prevStage = _stage[bi];
                _stage[bi] = (byte)stage;
                if (stage == Stages - 1)
                {
                    // the killing shot: the building FALLS — citymap.js:1543-1545
                    // routes this to _startCollapse, an ANIMATED drop inside a dust
                    // avalanche, not an instant swap to the rubble prototype.
                    StartCollapse(bi, prevStage);
                }
                else
                {
                    PaintStage(bi, stage);
                    // citymap.js:1552 — windows/balconies/roof props above the
                    // SUNKEN roofline go with it, and the glass among them
                    // SHATTERS on the way out (_stripFacadeProps' `shatter` flag,
                    // which is what publishes onWindowsBlown at citymap.js:2833).
                    float b = WallBaseOf(bi);
                    ShatterPanesAbove(bi, b + (WallTopOf(bi) - b) * BuildingDamage.StageH[stage] - 1f);
                    // citymap.js:1553 `if (stage >= 6) this._shedDebris(rec, stage)`
                    // — chunks spall off the walls. The JS gates repeats on
                    // `rec.shedAt` (citymap.js:2765: "if ((rec.shedAt ?? 0) >=
                    // stage) return"), which is a per-record cooldown and so lives
                    // here rather than in City/DebrisField.cs (its own header says
                    // so). _shedAt is lazily allocated: a chunk whose buildings
                    // never reach stage 6 pays nothing.
                    if (stage >= 6 && Fx?.OnShed != null)
                    {
                        _shedAt ??= new byte[BuildingCount];
                        if (_shedAt[bi] < stage)
                        {
                            _shedAt[bi] = (byte)stage;
                            if (FootprintBox(bi, out float sx0, out float sx1, out float sz0, out float sz1))
                                Fx.OnShed(sx0, sx1, sz0, sz1);
                        }
                    }
                    // citymap.js:1554 — `if (this.fx && stage >= 3)`
                    if (stage >= 3) Fx?.OnShake?.Invoke(0.1f, CxOf(bi), CzOf(bi));
                }
            }
            return (changed, _hp[bi] <= 0f);
        }

        // =====================================================================
        // ---- the ten crumble stages — src/citymap.js:1350 + :1969-2023 -------
        //
        //     static STAGE_H = [1, .97, .94, .9, .85, .79, .72, .63, .52, .12]
        //
        // This method used to sink the roofline on a LINEAR curve
        // (`t = stage / (Stages - 1)`, `sink = t * 1.8 m`, `dark = 1 - t * 0.72`),
        // which is not the JS's look at any stage: STAGE_H is a measured table
        // that barely touches a building for the first three hits and then
        // accelerates (−3 %, −3 %, −4 %, −5 %, −6 %, −7 %, −9 %, −11 % and
        // finally −40 % into the rubble mound), and its drop is a FRACTION of the
        // building's own height, not a fixed 1.8 m for a cottage and a tower
        // alike. It also skipped the buckle jitter, the lean shear and the real
        // soot ramp entirely, so a damaged building was a clean box standing
        // slightly lower.
        //
        // City/BuildingDamage.StageParams/WriteBuildingVerts already hold the
        // table and the per-vertex deformation, probe-verified by
        // CityMapRestProbe/_probe_citymaprest.mjs (STAGE_H, STAGEPARAMS_4,
        // WRITEVERT_V1000_STAGE4) — they were simply never called from here.
        // WriteVerts below is that method against this chunk's Vector3[]/Color32[]
        // arrays (BuildingDamage's own signature is float[]-flattened, for the
        // JS's BufferAttribute layout); the arithmetic, the three hash constants
        // and the phase mixing are line for line the same.
        //
        // baseY/top are WallBaseOf/WallTopOf, NOT `_top - Heights[bi]`: the JS's
        // `rec.base`/`rec.top` are citymap.js:5346's `boxBase`/`top`, i.e. exactly
        // the extrusion range this chunk bakes into WallBase/WallTop. The old
        // shortcut is wrong on a slope — see WallBaseOf's own doc.
        // =====================================================================
        void PaintStage(int bi, int stage)
        {
            BuildingDamage.StageParams(stage, out float kh, out float jit, out float dark, out float lean);
            WriteVerts(bi, kh, jit, dark, lean, 0);
            PushMesh();
        }

        /// <summary>src/citymap.js:1969-2012 `_writeBuildingVerts`, against this
        /// chunk's own live vertex/colour arrays. Rewrites building bi's range from
        /// the PRISTINE snapshot every time (stages recompute, they do not
        /// accumulate — same as the JS's `rec.orig`).</summary>
        void WriteVerts(int bi, float kh, float jit, float dark, float lean, int phase)
        {
            if (_liveVerts == null || _baseVerts == null) return;
            // citymap.js:1975 — the lean axis is a hash of the building's ORIGINAL
            // index (rec.bi), not its position in the chunk, and goes through
            // JsMath.ToUint32 because Knuth's 2654435761 overflows int32 (see
            // City/BuildingDamage.LeanAxis).
            int obi = OrigBuildingIndexOf(bi);
            BuildingDamage.LeanAxis(obi >= 0 ? obi : bi, out float leanX, out float leanZ);

            int start = RangeStart[bi], count = RangeCount[bi];
            float baseY = WallBaseOf(bi), top = WallTopOf(bi);
            float hgt = Mathf.Max(1f, top - baseY);
            for (int v = start; v < start + count && v < _liveVerts.Length; v++)
            {
                var p = _baseVerts[v];
                float relY = Mathf.Max(0f, p.y - baseY);
                float t = Mathf.Min(1f, relY / hgt); // 0 at the base, 1 at the roofline
                float a = jit * (0.35f + 0.65f * t); // upper floors crumble hardest
                float sh = relY * lean;              // lean shear grows with height
                _liveVerts[v] = new Vector3(
                    p.x + (BuildingDamage.Fr(v * 12.9898 + phase * 7.31) - 0.5f) * a + sh * leanX,
                    baseY + relY * kh + (BuildingDamage.Fr(v * 78.233 + phase * 3.17) - 0.5f) * a * 0.5f,
                    p.z + (BuildingDamage.Fr(v * 39.425 + phase * 5.53) - 0.5f) * a + sh * leanZ);
                if (_liveColors != null)
                {
                    var c = _baseColors[v];
                    _liveColors[v] = new Color32(
                        (byte)Mathf.Clamp(c.r * dark, 0f, 255f),
                        (byte)Mathf.Clamp(c.g * dark, 0f, 255f),
                        (byte)Mathf.Clamp(c.b * dark, 0f, 255f), c.a);
                }
            }
        }

        /// <summary>The one mesh upload the deformation paths share.</summary>
        void PushMesh()
        {
            if (_mesh == null || _liveVerts == null) return;
            _mesh.SetVertices(_liveVerts);
            if (_liveColors != null) _mesh.SetColors(_liveColors);
            _mesh.RecalculateBounds();
            // Collider intentionally NOT recooked per hit (MeshCollider.sharedMesh
            // reassignment is expensive on a whole 1km chunk) — the collision shape
            // stays at the pristine silhouette while the render mesh crumbles. A
            // documented, bounded gap: post-collapse hit tests land a few tens of
            // centimetres proud of what you see, never through it.
        }

        // =====================================================================
        // ---- the collapse — src/citymap.js:2380-2525 -------------------------
        //
        // _startCollapse / _updateCollapses / _finishCollapse. The KINEMATICS were
        // already ported (City/CityCulling.cs's CollapseAnim.Step, probe-verified
        // by CityLoadProbe) and had no runtime caller; the FX were not ported at
        // all, and that file's own doc says so: "NOT PORTED: the dust/smoke/
        // secondary-blast FX the JS spawns alongside this animation
        // (citymap.js:2423-2440, 2462-2494 fx.spawnSmoke/fx.explosion/onBlast/
        // _launchDebris) — no FX system is ported in this slice". There is one
        // now: CollapseFxHooks, assigned by CombatWorldAdapter.Install().
        //
        // THE DEBRIS IS NOW PORTED TOO (2026-10-31). It used to say here: "NOT
        // ported here, and deliberately: _launchDebris/_spawnRubble
        // (citymap.js:2405/2473/2521/2527) ... it needs an INSTANCED RUBBLE
        // RENDERER fed from public/models/rubble_kit.glb — nothing in the port
        // loads that GLB yet. Publishing a hook with no consumer would just be a
        // fourth dead delegate." rubble_kit is imported now
        // (Assets/Resources/Art/Props/rubble_kit.fbx), Fx/RubbleFx.cs is the six
        // instanced layers (citymap.js:2675-2707 _initRubble) and
        // Core/CollapseDebris.cs is the one shared field + step
        // (citymap.js:2593-2655 _updateDebris), wired in
        // CombatWorldAdapter.Install. So CollapseFxHooks now carries OnDebris /
        // OnRubble / OnShed and all four JS call sites publish: :2405 above,
        // :2473 in TickCollapses, :2505 + :2521 in FinishCollapse, and
        // citymap.js:1553's stage>=6 shed in Damage (with the JS's own
        // `rec.shedAt` gate as _shedAt).
        //
        // THE SCORCH AND THE SMOULDER ARE PORTED TOO (2026-10-31). It used to
        // say here: "STILL not ported: fx.stampScorch (no ground-decal system in
        // Fx/ yet) and fx.setSmolder/setBurning over DebrisField.MoundAnchors —
        // the IFireFx interface (Combat/Blast.cs:15) has Wave/SpawnEmber/
        // SpawnSmoke/Plume and no smolder/burning registry to publish into, so
        // MoundAnchors stays an unconsumed (but correct) method rather than a
        // fifth dead hook." Both halves of that were wrong about Fx/: FireSystem.
        // cs has carried the 220-slot scorch ring (src/fire.js:310-330/518-532)
        // AND the burning/smolder registries with their caps and per-frame
        // emission (src/fire.js:411-442/588-625) since it was written — IFireFx
        // was simply four methods short of being able to ASK for any of it, and
        // FireSystem.Update was never ticked, so none of it drew. IFireFx now
        // carries StampScorch/SetSmolder/SetBurning/ClearBurning, FinishCollapse
        // publishes citymap.js:2508/2514/2517/2519 into them over
        // DebrisField.MoundAnchors, and CombatWorldAdapter.Install steps the
        // FireSystem from game.WorldTick next to the debris field.
        // =====================================================================

        /// <summary>citymap.js:2765 `rec.shedAt` — the highest crumble stage this
        /// building has already shed wall chunks for, so one stage sheds once.
        /// Lazily allocated (see the call site).</summary>
        byte[] _shedAt;

        /// <summary>The footprint's axis-aligned bbox in UNITY space — the
        /// `rec.minx/maxx/minz/maxz` every debris/rubble/shed call in
        /// citymap.js:2728-2792 is written against. Derived from the baked
        /// footprint ring rather than from <see cref="SpanOf"/>, because span is
        /// `max(w, d)` and cannot be unpacked back into w AND d.</summary>
        internal bool FootprintBox(int bi, out float minX, out float maxX, out float minZ, out float maxZ)
        {
            minX = maxX = minZ = maxZ = 0f;
            var ring = FootprintRingUnityOf(bi);
            if (ring == null || ring.Length < 4) return false;
            minX = maxX = ring[0];
            minZ = maxZ = ring[1];
            for (int i = 2; i + 1 < ring.Length; i += 2)
            {
                if (ring[i] < minX) minX = ring[i];
                if (ring[i] > maxX) maxX = ring[i];
                if (ring[i + 1] < minZ) minZ = ring[i + 1];
                if (ring[i + 1] > maxZ) maxZ = ring[i + 1];
            }
            return true;
        }

        /// <summary>Set ONCE by Core/CombatWorldAdapter.Install(). Static because
        /// all ~600 chunks publish into the one glass/blast/shake system, exactly
        /// as the JS's single CityMap does.</summary>
        public static CollapseFxHooks Fx;

        sealed class Collapse
        {
            public int Bi;
            public City.CollapseState State;
            public float FireAcc, DustAcc;
        }

        List<Collapse> _collapses;

        void Update()
        {
            // 600+ chunks in the scene each own this Update; it must cost nothing
            // until something on this chunk is actually falling.
            if (_collapses == null || _collapses.Count == 0) return;
            TickCollapses(Time.deltaTime);
        }

        /// <summary>src/citymap.js:2409-2500 `_updateCollapses`, the merged-chunk
        /// (non-instanced, non-landmark) branch. Separate from Update() so a probe
        /// can step it deterministically — AddComponent from an -executeMethod
        /// script never gets a scene tick.</summary>
        internal void TickCollapses(float dt)
        {
            if (_collapses == null || _collapses.Count == 0) return;
            bool wrote = false;
            for (int i = _collapses.Count - 1; i >= 0; i--)
            {
                var c = _collapses[i];
                var f = City.CollapseAnim.Step(ref c.State, dt);
                // citymap.js:2456 — the jitter hash is re-rolled every ~45 ms, which
                // is what makes the shell visibly SHUDDER on the way down.
                WriteVerts(c.Bi, f.Kh, f.Buckle, f.Dark, f.Lean, 1 + Mathf.FloorToInt(c.State.T * 22f));
                wrote = true;

                float baseY = WallBaseOf(c.Bi), top = WallTopOf(c.Bi);
                float span = SpanOf(c.Bi);
                float cx = CxOf(c.Bi), cz = CzOf(c.Bi);
                float topY = baseY + (top - baseY) * f.Kh;

                // citymap.js:2462-2474 — secondary blasts tearing through the shell
                // as it pancakes: gas, fuel, whatever was inside.
                c.FireAcc += dt;
                if (c.FireAcc > 0.3f && f.P < 0.9f)
                {
                    c.FireAcc = 0f;
                    float a = UnityEngine.Random.value * Mathf.PI * 2f;
                    float rr = span * (0.15f + UnityEngine.Random.value * 0.3f);
                    float by = baseY + (topY - baseY) * UnityEngine.Random.value;
                    Fx?.OnBlast?.Invoke(cx + Mathf.Cos(a) * rr, by, cz + Mathf.Sin(a) * rr, 3f + span * 0.1f);
                    // citymap.js:2473 `this._launchDebris(r, 3, by)` — three
                    // chunks off every secondary, at that blast's own height.
                    Fx?.OnDebris?.Invoke(cx, cz, span, by, 3);
                }

                // citymap.js:2475-2494 — dust pours off every floor line as the
                // shell drops through it: a churning collar that hugs the roofline
                // all the way down. 13 puffs every 55 ms.
                c.DustAcc += dt;
                if (Fx?.Fire != null && c.DustAcc > 0.055f)
                {
                    c.DustAcc = 0f;
                    for (int k = 0; k < 13; k++)
                    {
                        float a = UnityEngine.Random.value * Mathf.PI * 2f;
                        float rr = span * (0.42f + UnityEngine.Random.value * 0.2f);
                        Fx.Fire.SpawnSmoke(
                            cx + Mathf.Cos(a) * rr, topY + UnityEngine.Random.value * 2f, cz + Mathf.Sin(a) * rr,
                            size: 11f + UnityEngine.Random.value * 8f, dark: 0.3f, dur: 4f,
                            vx: Mathf.Cos(a) * 3f, vy: 1.2f, vz: Mathf.Sin(a) * 3f, spread: 2.5f);
                    }
                }

                if (f.Done)
                {
                    _collapses.RemoveAt(i);
                    FinishCollapse(c.Bi);
                    wrote = true;
                }
            }
            if (wrote) PushMesh();
        }

        /// <summary>src/citymap.js:2380-2407 `_startCollapse`.</summary>
        void StartCollapse(int bi, int prevStage)
        {
            float baseY = WallBaseOf(bi), top = WallTopOf(bi);
            BuildingDamage.CollapseStart(prevStage, baseY, top,
                out float dur, out float fromKh, out float fromDark, out float fromLean);
            _collapses ??= new List<Collapse>();
            _collapses.Add(new Collapse
            {
                Bi = bi,
                State = new City.CollapseState
                {
                    Dur = dur, FromKh = fromKh, FromDark = fromDark, FromLean = fromLean,
                    IsInstOrLandmark = false,
                },
            });
            // citymap.js:2392-2395 — windows/balconies/roof clutter cannot ride the
            // falling shell, so they are stripped NOW, masked by the blast. EVERY
            // pane on the building blows out at once: a curtain of glass.
            ShatterPanesAbove(bi, float.NegativeInfinity);

            float span = SpanOf(bi), h = top - baseY;
            float cx = CxOf(bi), cz = CzOf(bi);
            // citymap.js:2401-2405. fx.explosion's own fireball is BlastFx's job
            // (src/game.js:429 maps onBlast onto blast.explode), so one publish
            // covers both lines; the plume is the separate dust column.
            Fx?.OnBlast?.Invoke(cx, baseY + h * 0.55f, cz, Mathf.Max(8f, span * 0.5f));
            Fx?.Fire?.Plume(cx, baseY, cz, span * 0.4f, h * 0.5f);
            Fx?.OnShake?.Invoke(0.45f, cx, cz);
            // citymap.js:2405 `this._launchDebris(rec, 8 + Math.round(span / 5),
            // rec.base + h * 0.7)`. JsMath.Round, not Mathf.RoundToInt: JS rounds
            // a .5 tie UP and Unity's rounds to even, and span/5 hits .5 on any
            // 2.5 m-granular span (see JsMath's header and MoundAnchors' own note).
            Fx?.OnDebris?.Invoke(cx, cz, span, baseY + h * 0.7f, 8 + Data.JsMath.Round(span / 5f));
        }

        /// <summary>src/citymap.js:2502-2525 `_finishCollapse` — settle on the
        /// canonical rubble mound, then the ground slam.</summary>
        void FinishCollapse(int bi)
        {
            BuildingDamage.StageParams(Stages - 1, out float kh, out float jit, out float dark, out float lean);
            WriteVerts(bi, kh, jit, dark, lean, 0);

            float baseY = WallBaseOf(bi), top = WallTopOf(bi);
            float span = SpanOf(bi), cx = CxOf(bi), cz = CzOf(bi);
            // citymap.js:2509 `this.heightAt(rec.cx, rec.cz) ?? rec.base`
            float gy = Fx?.GroundAt?.Invoke(cx, cz) ?? baseY;
            // citymap.js:2508 — the window fires that were burning up the
            // standing facade stop BEFORE the mound's own fire replaces them.
            Fx?.Fire?.ClearBurning(BurnKeyOf(bi));
            Fx?.Fire?.Plume(cx, baseY, cz, span * 0.75f, (top - baseY) * 0.4f);
            Fx?.Fire?.Wave(cx, gy, cz, span);
            // citymap.js:2514 `fx.stampScorch(rec.cx, gy, rec.cz, span * 2.1)` —
            // the permanent burn mark, at the GROUND height the wave sits on,
            // not the building's base.
            Fx?.Fire?.StampScorch(cx, gy, cz, span * 2.1f);
            Fx?.OnBlast?.Invoke(cx, gy + Mathf.Max(3f, span * 0.15f), cz, Mathf.Max(7f, span * 0.42f));
            // citymap.js:2516-2519 — the mound SMOULDERS forever and the wreck
            // BURNS on top of it, both over the same anchor ring.
            // City/DebrisField.MoundAnchors is citymap.js:2655 `_moundAnchors`;
            // it was computed-but-unconsumed for exactly these two lines.
            if (Fx?.Fire != null && FootprintBox(bi, out float mx0, out float mx1, out float mz0, out float mz1))
            {
                var mound = City.DebrisField.MoundAnchors(mx0, mx1, mz0, mz1, baseY, top, BurnKeyOf(bi));
                Fx.Fire.SetSmolder(BurnKeyOf(bi), mound);
                Fx.Fire.SetBurning(FireKeyOf(bi), mound, 1f, 2.4f);
            }
            Fx?.OnShake?.Invoke(1.0f, cx, cz);
            // citymap.js:2505 `this._spawnRubble(rec)` — the permanent mound.
            // Published BEFORE the slam's own debris, same order as the JS
            // (_spawnRubble sits above the `if (this.fx)` block).
            if (Fx?.OnRubble != null && FootprintBox(bi, out float rx0, out float rx1, out float rz0, out float rz1))
                Fx.OnRubble(rx0, rx1, rz0, rz1, baseY, top);
            // citymap.js:2521 `this._launchDebris(rec, 6 + Math.round(span / 6),
            // rec.base + 2)` — the chunks the ground slam throws back up.
            Fx?.OnDebris?.Invoke(cx, cz, span, baseY + 2f, 6 + Data.JsMath.Round(span / 6f));
        }

        /// <summary>The glass half of src/citymap.js:2806-2834
        /// `_stripFacadeProps(recs, cutY, shatter=true)` for ONE building: every
        /// still-intact pane at or above `cutY` leaves its facade for good (the
        /// `broken` mask BlastWindows/NearWindow already share) and is reported to
        /// onWindowsBlown, so the windows visibly EXPLODE instead of quietly
        /// vanishing as the building crumbles.
        ///
        /// citymap.js:2823's `y &lt; cutY` skip is the test, against the pane's
        /// PRISTINE y (the prop instance's own matrix), not the stage-sunk y —
        /// that is why CollectPanes' `baseY + (a.y - baseY) * kh` has no
        /// counterpart here. Flat five-wide [x, y, z, nx, nz], capped at the JS's
        /// own 5 * 260.</summary>
        void ShatterPanesAbove(int bi, float cutY)
        {
            if (Fx?.OnWindowsBlown == null) return;
            System.Func<float, float, float?> heightAt = Fx.GroundAt ?? ((x, z) => (float?)null);
            var (pos, nrm, broken) = WindowDataOf(bi, heightAt);
            if (pos == null || pos.Count == 0) return;
            List<float> blown = null;
            for (int k = 0; k < pos.Count; k++)
            {
                if (broken[k]) continue;
                var a = pos[k];
                if (a.y < cutY) continue;
                broken[k] = true;
                if (blown == null) blown = new List<float>(64);
                if (blown.Count >= 5 * 260) continue; // citymap.js:2825
                blown.Add(a.x); blown.Add(a.y); blown.Add(a.z);
                blown.Add(nrm[k * 2]); blown.Add(nrm[k * 2 + 1]);
            }
            if (blown != null && blown.Count > 0) Fx.OnWindowsBlown(blown.ToArray()); // citymap.js:2833
        }
    }
}

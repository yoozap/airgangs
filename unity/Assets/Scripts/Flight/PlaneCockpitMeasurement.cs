using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using TbilisiFly.Character;

namespace TbilisiFly.Flight
{
    /// <summary>
    /// Port of src/planes.js:849-1281 — the cockpit MEASUREMENT half of the
    /// "findCockpit" system. PlaneCatalog.cs's own header explicitly defers
    /// this ("RUNTIME MESH MEASUREMENT... CONTROLS/PILOT_*... are likewise
    /// left out here. Building that successor... is its own unit of work").
    /// Character/FootBoarding.cs needs exactly this to seat a pilot (its own
    /// "WHAT THIS FILE DOES NOT BUILD" header section) and defines the data
    /// contract this file fills: <see cref="PlaneCockpitRig"/> /
    /// <see cref="FootBoarding.CockpitInfo"/>. <see cref="PlaneCockpitKit"/>
    /// (sibling file) is the half that BUILDS furniture/controls/the box
    /// shell on top of what this file measures.
    ///
    /// ================================ SCOPE ================================
    /// Ports: boxOf, isSkin, bellyUnder, fuselageAxis, hullProfile, findCockpit
    /// (planes.js:889-1281) as a self-contained, read-only measurement over
    /// an ALREADY-BUILT plane hierarchy — MeshFilter/SkinnedMeshRenderer under
    /// a wrapper Transform. Does not load, glaze or re-tag anything (that is
    /// src/planes.js's loadPlane()/glazeHood()/addInterior()/tagPropellers(),
    /// which this project's asset pipeline bakes at author time instead of at
    /// runtime — see this file's "WHAT IS NOT PORTED, AND WHY" below).
    ///
    /// =================== WHAT IS NOT PORTED, AND WHY =======================
    /// glazeHood/lineHullInside/addInterior/tagPropellers (planes.js:687-773)
    /// mutate a Three.js runtime scene graph that starts life as an opaque,
    /// un-tagged GLB. Unity's plane assets are pre-converted FBX/GLB imported
    /// at author time (unity/Assets/Resources/Art/Planes/*.fbx, *_cockpit.fbx siblings
    /// already present on disk per plane id, matching addInterior's own
    /// `def.file.replace(/\.glb$/, '_cockpit.glb')` naming) — nothing in this
    /// pass found a Unity system that attaches/re-materials them yet
    /// (PlaneController.cs has no reference to them; checked, not edited).
    /// Wiring "spawn a plane -> instantiate its `<id>_cockpit` interior as a
    /// child named cockpit_interior, glaze its canopy material" is scoped to
    /// whichever system owns plane SPAWNING (PlaneController.cs, out of this
    /// pass's file list) — flagged here, not silently assumed. This file's
    /// exclusion rules (<see cref="IsSkin"/>) still honour a `cockpit_interior`
    /// child and propeller-named nodes if/when that wiring lands, exactly as
    /// src/planes.js's isSkin() does off `userData.interior`/`userData.isPropeller`.
    ///
    /// ================================ HANDEDNESS ================================
    /// No cross product anywhere in this file (checked: `grep cross` over
    /// planes.js:849-1281 returns nothing but prose). Every measurement is an
    /// axis-aligned extent (min/max/median/half-width) read off vertices that
    /// Unity's own importer already placed correctly — there is no world-
    /// frame compass reference here to mirror (GeoFrame.ToUnity is for lat/
    /// lon-derived city positions; this file only ever compares a model's own
    /// vertices to its own bounds). All positions are computed through
    /// `wrapper.InverseTransformPoint`, so the result (<see cref="Result.Seat"/>
    /// etc.) is in WRAPPER-LOCAL space regardless of whether the wrapper has
    /// already been placed in the scene — stricter than the JS, which gets
    /// away with raw world-space math only because it runs before the
    /// wrapper is parented into the game world (planes.js:1528-1571 construction
    /// order). Functionally identical when the wrapper IS still at the scene
    /// origin (the common case), and safe either way.
    ///
    /// VERIFIED: constants only (PilotSit/PilotShoulder/PilotWide/FloorRatio/
    /// FlightdeckRatio/PilotGrowMax/PilotRoomMin) against src/planes.js's own
    /// literals — see tools/_probe_planechar.mjs. The MEASUREMENT ALGORITHM
    /// itself (hull sampling against real imported meshes) could not be run —
    /// this task's brief holds the Unity batch-mode lock elsewhere. Treat the
    /// geometry math as reviewed-not-executed.
    /// </summary>
    public static class PlaneCockpitMeasurement
    {
        // ---- src/planes.js:849-851, 869-874, 886 ----
        internal const float PilotSit = 0.92f;       // seat pan to crown
        internal const float PilotShoulder = 0.58f;  // seat pan to shoulders
        internal const float PilotWide = 0.52f;      // across the shoulders
        internal const float PilotGrowMax = 1f;      // growth disabled by default (see JS comment at :860-868)
        internal const float PilotRoomMin = 0.3f;    // below this the measurement is broken, not tight
        internal const float FlightdeckRatio = 0.50f;
        internal const float FloorRatio = 0.62f;

        static readonly Regex CanopyRx = new Regex(
            "glass|canopy|cocpit|cockpit|kabina|windscreen|window|vidrio|cristal|staklo|^cam|_cam|hood",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex WellRx = new Regex(
            "koltuk|seat|chair|cocpit|cockpit|kabina|interior|cabin|pilot",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // src/planes.js:742 PROP_NAME_RX — also used by PlaneCockpitKit's shell builder.
        internal static readonly Regex PropNameRx = new Regex(
            @"^(propeller|prop[._\d]|pervane|helice|h[ée]lice|airscrew|vint|screw[._\d])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Mirrors the JS's full `out` object (planes.js:1192-1224).
        /// FootBoarding.CockpitInfo is the TRIMMED subset foot.js's board/seat
        /// code actually reads (Seat/Deck/HalfW/Ceil/Bot) — <see cref="ToCockpitInfo"/>
        /// below projects down to it.</summary>
        public sealed class Result
        {
            public Vector3 Seat;          // wrapper-local
            public float Top, Floor, Room, Grow, WantGrow;
            public bool Sane, Closed, Roomy;
            public float Deck;
            public float Bot;             // hull underside, shallowest under the seated body
            public float HalfW;
            public float WellLen;
            public Transform Hood;        // the lid mesh, for PlaneCockpitKit.HingeHood — null if open/none
            public float Ceil;            // float.PositiveInfinity when open
            public string From;           // diagnostic, mirrors JS's `from`
        }

        public static FootBoarding.CockpitInfo ToCockpitInfo(Result r) => new FootBoarding.CockpitInfo
        {
            Seat = r.Seat,
            Deck = r.Deck,
            HalfW = r.HalfW,
            Ceil = r.Ceil,
            Bot = r.Bot,
        };

        // =====================================================================
        // isSkin / boxOf — planes.js:887-912
        // =====================================================================

        /// <summary>planes.js:910-920 isSkin — true if this renderer's geometry
        /// counts as the airframe's own skin for measurement purposes. `self`
        /// is the renderer's own transform; ancestors up to (not including)
        /// `root` are also checked, mirroring the JS's parent-chain walk.</summary>
        public static bool IsSkin(Transform self, Transform root)
        {
            if (self == null || self.name == "plane_shell") return false;
            for (Transform p = self; p != null; p = p.parent)
            {
                if (p.name == "plane_shell" || p.name == "cockpit_interior") return false;
                if (PropNameRx.IsMatch(p.name)) return false;
                if (p == root) break;
            }
            return true;
        }

        /// <summary>A renderer's POSED vertices in world space — skinned meshes
        /// are baked at their current pose first (planes.js reads the live
        /// buffer via `getVertexPosition`, which is posed for a skinned mesh).
        /// `strideTarget` 0 = every vertex (boxOf); >0 = the same "about N
        /// samples total" stride the JS uses for the big scans.</summary>
        internal static IEnumerable<Vector3> WorldVertices(Transform t, int strideTarget)
        {
            if (t.TryGetComponent(out SkinnedMeshRenderer smr) && smr.sharedMesh != null)
            {
                var baked = new Mesh();
                // useScale: TRUE. Measured on the only two skinned airframes in
                // the fleet (`SKINMX` in Editor/Probes/PlaneLiveryProbe.cs),
                // both of which import at scale 100 like t28.fbx does:
                //
                //   neonfly Object_9  Renderer.bounds (8.33, 1.74, 8.47) m
                //     BakeMesh(m)       * localToWorld -> (772, 139, 787)
                //     BakeMesh(m, true) * localToWorld -> (7.72, 1.39, 7.87)
                //
                // The default overload's output is already in a scaled space,
                // so multiplying by localToWorldMatrix applies the import scale
                // a SECOND time. That is why `carpet` measured 3350 x 1675 x
                // 2517 m and `neonfly` 957 x 301 x 1190 m, why PlaneHallFill
                // logged "carpet is 3350 m across" and "3986 m of aeroplanes on
                // 394 m of floor", and why both machines got a BoxCollider
                // kilometres wide -- which swallowed every incoming ray's
                // origin, so Collider.Raycast (a ray that STARTS inside a
                // collider never registers) answered no and Combat/PlaneDamage
                // was never even asked. The rest of the fleet has no skinned
                // renderer at all, so nothing else moves. Character/FootBelt,
                // ShoulderPocket and Props/WindRider already pass true.
                smr.BakeMesh(baked, true);
                var verts = baked.vertices;
                var m = smr.transform.localToWorldMatrix;
                int stride = strideTarget > 0 ? Mathf.Max(1, verts.Length / strideTarget) : 1;
                for (int i = 0; i < verts.Length; i += stride) yield return m.MultiplyPoint3x4(verts[i]);
                Object.DestroyImmediate(baked);
            }
            else if (t.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null)
            {
                var verts = mf.sharedMesh.vertices;
                var m = mf.transform.localToWorldMatrix;
                int stride = strideTarget > 0 ? Mathf.Max(1, verts.Length / strideTarget) : 1;
                for (int i = 0; i < verts.Length; i += stride) yield return m.MultiplyPoint3x4(verts[i]);
            }
        }

        static IEnumerable<Transform> MeshTransforms(Transform root)
        {
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true)) yield return mf.transform;
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) yield return smr.transform;
        }

        /// <summary>planes.js:889-891 boxOf — the PRECISE (vertex-walked) AABB
        /// of everything under `t`, no skin filtering. Points are converted
        /// into `frame`'s local space as they are gathered (see class header
        /// HANDEDNESS note on why — this is the one deliberate deviation from
        /// a literal transcription).</summary>
        public static Bounds BoxOf(Transform t, Transform frame)
        {
            bool any = false;
            var b = new Bounds();
            foreach (var mt in MeshTransforms(t))
                foreach (var wp in WorldVertices(mt, 0))
                {
                    var p = frame.InverseTransformPoint(wp);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                    else b.Encapsulate(p);
                }
            return b;
        }

        // =====================================================================
        // fuselageAxis — planes.js:934-968
        // =====================================================================
        static float FuselageAxis(Transform root, Transform frame, Bounds air, Vector3 size)
        {
            float zCut = air.min.z + size.z * 0.33f;
            float xWin = size.x * 0.25f;
            var xs = new List<float>();
            foreach (var mt in MeshTransforms(root))
            {
                if (!IsSkin(mt, root)) continue;
                foreach (var wp in WorldVertices(mt, 8000))
                {
                    var p = frame.InverseTransformPoint(wp);
                    if (p.z <= zCut && Mathf.Abs(p.x) <= xWin) xs.Add(p.x);
                }
            }
            if (xs.Count < 24) return 0f;
            xs.Sort();
            float mid = xs[xs.Count >> 1]; // median, not midpoint-of-range — see JS comment
            return Mathf.Abs(mid) > Mathf.Max(0.6f, size.x * 0.02f) ? mid : 0f;
        }

        // =====================================================================
        // hullProfile — planes.js:970-1009
        // =====================================================================
        sealed class Profile
        {
            public float[] Top, Bot, Wid;
            public float ZMin, ZMax;
            public int Bins;
        }

        static bool IsMaterialTransparent(Material m)
        {
            if (m == null) return false;
            // Best-effort heuristic only — see class header "VERIFIED" note.
            if (m.HasProperty("_Surface") && m.GetFloat("_Surface") >= 0.5f) return true; // URP Lit: 1 = Transparent
            if (m.HasProperty("_Color") && m.color.a < 0.95f) return true;
            return m.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        static Profile HullProfile(Transform root, Transform frame, float band, float zMin, float zMax,
            int bins, bool opaqueOnly, float axisX)
        {
            var top = new float[bins]; var bot = new float[bins]; var wid = new float[bins];
            for (int i = 0; i < bins; i++) { top[i] = float.NegativeInfinity; bot[i] = float.PositiveInfinity; }
            float span = Mathf.Max(1e-6f, zMax - zMin);
            foreach (var mt in MeshTransforms(root))
            {
                if (!IsSkin(mt, root)) continue;
                if (opaqueOnly)
                {
                    bool anyOpaque = false;
                    Material[] mats = null;
                    if (mt.TryGetComponent(out Renderer rend)) mats = rend.sharedMaterials;
                    if (mats != null) foreach (var m in mats) if (!IsMaterialTransparent(m)) { anyOpaque = true; break; }
                    if (!anyOpaque) continue;
                }
                foreach (var wp in WorldVertices(mt, 20000))
                {
                    var p = frame.InverseTransformPoint(wp);
                    float ax = Mathf.Abs(p.x - axisX);
                    if (ax > band || p.z < zMin || p.z > zMax) continue;
                    int b = Mathf.Min(bins - 1, Mathf.FloorToInt((p.z - zMin) / span * bins));
                    if (p.y > top[b]) top[b] = p.y;
                    if (p.y < bot[b]) bot[b] = p.y;
                    if (ax > wid[b]) wid[b] = ax;
                }
            }
            return new Profile { Top = top, Bot = bot, Wid = wid, ZMin = zMin, ZMax = zMax, Bins = bins };
        }

        // =====================================================================
        // bellyUnder — planes.js:921-932
        // =====================================================================
        static float BellyUnder(Profile prof, Profile wide, float station, float len)
        {
            float span = prof.ZMax - prof.ZMin;
            int At(float z) => Mathf.FloorToInt((z - prof.ZMin) / span * prof.Bins);
            int a = Mathf.Max(0, At(station - Mathf.Min(1.0f, len * 0.08f)));
            int b = Mathf.Min(prof.Bins - 1, At(station + Mathf.Min(0.45f, len * 0.04f)));
            float bot = float.PositiveInfinity;
            for (int i = a; i <= b; i++)
            {
                if (prof.Bot[i] < bot) bot = prof.Bot[i];
                if (wide.Bot[i] < bot) bot = wide.Bot[i];
            }
            return float.IsFinite(bot) ? bot : float.NegativeInfinity;
        }

        // =====================================================================
        // findCockpit — planes.js:1010-1281
        // =====================================================================
        sealed class Candidate { public float V; public Bounds Box; public string Name; public Transform Obj; }

        /// <summary>Everything the pilot needs to know about an airframe,
        /// measured in `wrapper`'s own local space (see class header
        /// HANDEDNESS). `wrapper` must already contain its finished model
        /// hierarchy (post scale/centre, pre pilot/kit/shell — planes.js's own
        /// construction order, makeGameplayPlane:1544-1571).</summary>
        public static Result Measure(Transform wrapper, CockpitOverride ovr = null)
        {
            var air = BoxOf(wrapper, wrapper);
            Vector3 size = air.size;
            float len = size.z;
            float vol = Mathf.Max(1e-6f, size.x * size.y * size.z);
            float band = Mathf.Max(0.3f, size.x * 0.06f);
            float axisX = FuselageAxis(wrapper, wrapper, air, size);
            float zMin = air.min.z + len * 0.05f;
            float zMax = air.min.z + len * 0.78f;

            Candidate glass = null, well = null;
            foreach (var mt in MeshTransforms(wrapper))
            {
                // planes.js:1026 `o.userData.interior` — see IsSkin's ancestor walk;
                // same exclusion rule, checked directly here since this loop does
                // not otherwise call IsSkin (glass/well candidates CAN be interior-
                // adjacent meshes the JS still measures by name/material alone —
                // but never ones actually tagged interior).
                // AND THE PROPELLER. The JS cannot pick a propeller here
                // because of ORDER, not filtering: planes.js:803-812 runs
                // tagPropellers at model LOAD (so every blade already carries
                // userData.isPropeller, which isSkin excludes) and
                // finishPropellers — the thing that creates the semi-
                // transparent BLUR DISC — only runs at :1593, long after
                // findCockpit. This port has no such guarantee: Flight/
                // PlaneHallFill builds the blades before it bakes the cockpit,
                // and a disc is a small, centred, see-through quad right on
                // the nose, i.e. a PERFECT canopy candidate. Measured on p100:
                // `from = "glass:propeller_disc"`, seat station 3.95 m forward
                // of where a pilot sits, which is the propeller. Same
                // exclusion isSkin already applies, applied here too so the
                // measurement does not depend on build order.
                bool isInterior = false;
                for (Transform p = mt; p != null; p = p.parent)
                {
                    if (p.name == "cockpit_interior") { isInterior = true; break; }
                    if (PropNameRx.IsMatch(p.name)) { isInterior = true; break; }
                    if (p.GetComponent<TbilisiFly.Combat.SpinningPartTag>() != null) { isInterior = true; break; }
                    if (p == wrapper) break;
                }
                if (isInterior) continue;

                Material[] mats = mt.TryGetComponent(out Renderer rend) ? rend.sharedMaterials : null;
                string mn = "";
                bool see = false;
                if (mats != null)
                    foreach (var m in mats) { if (m != null) mn += m.name + " "; if (IsMaterialTransparent(m)) see = true; }
                bool named = CanopyRx.IsMatch(mt.name) || CanopyRx.IsMatch(mn);
                bool isWell = WellRx.IsMatch(mt.name) || WellRx.IsMatch(mn);
                if (!named && !isWell && !see) continue;

                var b = BoxOf(mt, wrapper);
                Vector3 s = b.size; Vector3 c = b.center;
                if (s.x > size.x * 0.4f) continue;
                if (s.x * s.y * s.z > vol * 0.06f) continue;
                if (Mathf.Abs(c.x - axisX) > size.x * 0.25f) continue;
                if (c.z < zMin || c.z > zMax) continue;
                float v = s.x * s.y * s.z;
                if (see && (glass == null || v > glass.V)) glass = new Candidate { V = v, Box = b, Name = mt.name, Obj = mt };
                if ((named || isWell) && !see && (well == null || v > well.V)) well = new Candidate { V = v, Box = b, Name = mt.name, Obj = mt };
            }

            var prof = HullProfile(wrapper, wrapper, band, zMin, zMax, 48, false, axisX);
            var wide = HullProfile(wrapper, wrapper, band * 2.6f, zMin, zMax, 48, false, axisX);

            (float top, float bot, float wid) HullAt(float z)
            {
                int i = Mathf.Min(prof.Bins - 1, Mathf.Max(0,
                    Mathf.FloorToInt((z - prof.ZMin) / (prof.ZMax - prof.ZMin) * prof.Bins)));
                (float top, float bot, float wid) Read(Profile p, int reach)
                {
                    float t = float.NegativeInfinity, b = float.PositiveInfinity, w = 0f;
                    for (int k = Mathf.Max(0, i - reach); k <= Mathf.Min(p.Bins - 1, i + reach); k++)
                    {
                        if (p.Top[k] > t) t = p.Top[k];
                        if (p.Bot[k] < b) b = p.Bot[k];
                        if (p.Wid[k] > w) w = p.Wid[k];
                    }
                    return (t, b, w);
                }
                var h = Read(prof, 1);
                if (!float.IsFinite(h.top)) h = Read(wide, 1);
                if (!float.IsFinite(h.top)) h = Read(wide, 3);
                return h;
            }

            const float FwdEnd = 0.45f;
            float station;
            if (glass != null) station = glass.Box.center.z;
            else if (well != null) station = well.Box.center.z;
            else
            {
                int last = Mathf.Max(1, Mathf.FloorToInt(prof.Bins * (FwdEnd - 0.05f) / 0.73f));
                float best = float.NegativeInfinity;
                for (int i = 0; i < last; i++) if (prof.Top[i] > best) best = prof.Top[i];
                int hit = 0;
                for (int i = 0; i < last; i++) { if (prof.Top[i] >= best * 0.92f) { hit = i; break; } }
                station = prof.ZMin + (hit + 0.5f) / prof.Bins * (prof.ZMax - prof.ZMin);
            }
            if (ovr?.Z != null) station = ovr.Z.Value;

            var hull = HullAt(station);
            if (ovr?.Z != null && (!float.IsFinite(hull.top) || !float.IsFinite(hull.bot)))
            {
                float top = float.NegativeInfinity, bot = float.PositiveInfinity, wid = 0.25f;
                foreach (var mt in MeshTransforms(wrapper))
                {
                    if (mt.name == "plane_shell") continue;
                    var b = BoxOf(mt, wrapper);
                    if (b.min.z > station + len * 0.06f || b.max.z < station - len * 0.06f) continue;
                    foreach (var wp in WorldVertices(mt, 20000))
                    {
                        var p = wrapper.InverseTransformPoint(wp);
                        if (Mathf.Abs(p.z - station) > len * 0.03f || Mathf.Abs(p.x - axisX) > band * 3f) continue;
                        if (p.y > top) top = p.y;
                        if (p.y < bot) bot = p.y;
                        wid = Mathf.Max(wid, Mathf.Abs(p.x - axisX));
                    }
                }
                if (float.IsFinite(top) && float.IsFinite(bot)) hull = (top, bot, wid);
            }
            if (!float.IsFinite(hull.top) || !float.IsFinite(hull.bot))
            {
                int besti = -1; float bestd = float.NegativeInfinity;
                for (int i = 0; i < prof.Bins; i++)
                {
                    if (!float.IsFinite(prof.Top[i]) || !float.IsFinite(prof.Bot[i])) continue;
                    float d = prof.Top[i] - prof.Bot[i];
                    if (besti < 0 || d > bestd) { bestd = d; besti = i; }
                }
                if (besti < 0) return null;
                station = prof.ZMin + (besti + 0.5f) / prof.Bins * (prof.ZMax - prof.ZMin);
                hull = HullAt(station);
            }

            float deck = hull.top;
            bool openWell = well != null && glass == null && well.Box.max.y < deck - 0.12f;
            bool closed = ovr?.Closed ?? !openWell;
            float top2 = (closed && glass != null) ? glass.Box.max.y : deck;
            float floor = well != null ? well.Box.min.y
                : (glass != null && glass.Box.min.y < deck - 0.25f /* hoodWasOpaque unverified, see note */
                    ? glass.Box.min.y
                    : Mathf.Max(hull.bot, top2 - (top2 - hull.bot) * FloorRatio));
            float room = top2 - floor;
            float need = closed ? PilotSit : PilotShoulder;
            bool sane = room >= PilotRoomMin;
            float across = hull.wid * 2f * 0.82f;
            float widthGrow = hull.wid < band * 0.95f
                ? Mathf.Min(1.25f, Mathf.Max(1f, PilotWide / Mathf.Max(0.2f, across)))
                : 1f;
            float wantGrow = sane ? Mathf.Min(2.2f, Mathf.Max(1f, Mathf.Max(need / room, widthGrow))) : 1f;
            float grow = Mathf.Min(PilotGrowMax, ovr?.Grow ?? wantGrow);

            bool roomy = room > need * 1.5f;
            float seatY = roomy
                ? hull.bot + (top2 - hull.bot) * FlightdeckRatio
                : Mathf.Max(floor, top2 - need);

            var result = new Result
            {
                Seat = new Vector3(axisX, seatY, station),
                Top = top2, Floor = floor, Room = room, Grow = grow, WantGrow = wantGrow,
                Sane = sane, Closed = closed, Deck = deck, Roomy = roomy,
                Bot = BellyUnder(prof, wide, station, len),
                HalfW = Mathf.Max(0.25f, hull.wid),
                WellLen = Mathf.Max(0.9f, (well ?? glass) != null ? ((well ?? glass).Box.max.z - (well ?? glass).Box.min.z) : hull.wid * 3f),
                Hood = (closed && glass != null) ? glass.Obj : null,
                Ceil = closed ? top2 : float.PositiveInfinity,
                From = (closed && glass != null ? $"glass:{glass.Name}" : well != null ? $"well:{well.Name}" : "hull profile")
                    + (sane ? "" : " UNMEASURED"),
            };
            if (ovr != null)
            {
                if (ovr.Y != null) result.Seat.y = ovr.Y.Value;
                if (ovr.Room != null) result.Room = ovr.Room.Value;
                if (ovr.Grow != null) result.Grow = ovr.Grow.Value;
                result.From += " +override";
            }
            return result;
        }
    }
}

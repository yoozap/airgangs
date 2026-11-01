using System.Collections.Generic;
using System.Linq;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the genuinely MISSING pieces found this session by diffing
    /// src/propeller.js, src/planephys.js and src/planehall.js against their
    /// existing Unity ports — NOT a re-check of what earlier probes already
    /// cover (PropellerProbe.cs, PlanePhysicsProbe.cs, PlaneHallLayoutProbe.cs/
    /// PlaneHallFillProbe.cs all still stand). See tools/_probe_flightrest.mjs's
    /// own header for the exact split and for why each JS-side case is built
    /// the way it is.
    ///
    ///   1. PropellerSystem.SplitNamed/RepivotNamed/TagPropellers — the
    ///      named-trust dispatch src/propeller.js:484-525,711-775,865-908
    ///      had with NO Unity counterpart at all before this session — plus
    ///      the hub-as-AREA-CENTROID proof the task brief asks for by name.
    ///   2. PlanePhysicsSystem.HitPlayer — src/planephys.js:923-970
    ///      `_hitPlayer`, entirely unported before this session (Sweep/
    ///      `_sweep` existed; the player's-own-plane-gets-hit path did not).
    ///   3. PlaneHallVisibility.ComputeVisible — the runtime glue that
    ///      actually calls PlaneHallLayout.FleetVisible/Walled from a real
    ///      Transform, new this session.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.FlightRestProbe.Run
    /// </summary>
    public static class FlightRestProbe
    {
        static string B(bool b) => b ? "true" : "false"; // lower-case — see the unity skill

        // =====================================================================
        // shared box-rig helpers — IDENTICAL arithmetic to the .mjs counterpart
        // =====================================================================
        static readonly int[] BoxTris =
        {
            0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6, 0, 5, 1, 0, 4, 5,
            3, 2, 6, 3, 6, 7, 0, 3, 7, 0, 7, 4, 1, 5, 6, 1, 6, 2,
        };
        static Vector3[] BoxVerts(float hx, float hy, float hz) => new[]
        {
            new Vector3(-hx, -hy, -hz), new Vector3(hx, -hy, -hz), new Vector3(hx, hy, -hz), new Vector3(-hx, hy, -hz),
            new Vector3(-hx, -hy, hz), new Vector3(hx, -hy, hz), new Vector3(hx, hy, hz), new Vector3(-hx, hy, hz),
        };
        static void AddBlade(List<Vector3> v, List<int> t, Vector3 hub, Vector3 offset, float angle, float lw, float ll, float lt)
        {
            int baseI = v.Count;
            float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
            foreach (var v0 in BoxVerts(lw / 2f, ll / 2f, lt / 2f))
            {
                float x = v0.x, y = v0.y + ll / 2f;
                float xr = x * c - y * s, yr = x * s + y * c;
                v.Add(new Vector3(hub.x + offset.x + xr, hub.y + offset.y + yr, hub.z + offset.z + v0.z));
            }
            foreach (var i in BoxTris) t.Add(baseI + i);
        }
        static MeshEntry BladeEntry(Vector3 hub, Vector3 offset, int n)
        {
            var v = new List<Vector3>(); var t = new List<int>();
            for (int i = 0; i < n; i++) AddBlade(v, t, hub, offset, (i / (float)n) * Mathf.PI * 2f, 0.15f, 1.0f, 0.08f);
            return new MeshEntry { Positions = v.ToArray(), Tri = t.ToArray() };
        }
        static Vector3 BBoxSize(Vector3[] positions)
        {
            Vector3 lo = positions[0], hi = positions[0];
            foreach (var p in positions) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            return hi - lo;
        }
        static readonly Vector3 Nose = new Vector3(0, 0, -1); // same convention as _probe_propeller.mjs

        public static void Run()
        {
            Propeller();
            PlanePhys();
            PlaneHall();
            ProbeExit.Exit(0);
        }

        // =====================================================================
        // 1. PROPELLER
        // =====================================================================
        static void Propeller()
        {
            // ---- Case A: a named BONE, kept exactly as-is ----
            {
                var named = new List<PropellerSystem.NamedCandidate>
                {
                    new PropellerSystem.NamedCandidate { IsBone = true, OriginInRoot = new Vector3(1, 2, -5) },
                };
                var r = PropellerSystem.TagPropellers(named, new List<MeshEntry>(), Nose, 1, null);
                var p = r.Props.Count > 0 ? r.Props[0] : null;
                Debug.Log($"PROPTAG id=bone_asis n={r.Props.Count} " +
                    (p != null ? $"hx={p.Hub.x:F6} hy={p.Hub.y:F6} hz={p.Hub.z:F6} radius={p.Radius:F6}" : "hx=0 hy=0 hz=0 radius=0"));
            }

            // ---- Case B: a named MESH already centred on its true hub ----
            {
                var hub = new Vector3(3, 1, -4);
                var entry = BladeEntry(hub, Vector3.zero, 3); // baked WORLD-space: hub translation already in the vertices
                var size = BBoxSize(entry.Positions);
                var named = new List<PropellerSystem.NamedCandidate>
                {
                    new PropellerSystem.NamedCandidate { IsBone = false, Size = size, OriginInRoot = hub, Entry = entry },
                };
                var r = PropellerSystem.TagPropellers(named, new List<MeshEntry>(), Nose, 1, null);
                var p = r.Props.Count > 0 ? r.Props[0] : null;
                Debug.Log($"PROPTAG id=mesh_onhub n={r.Props.Count} " +
                    (p != null ? $"hx={p.Hub.x:F6} hy={p.Hub.y:F6} hz={p.Hub.z:F6} radius={p.Radius:F6}" : "hx=0 hy=0 hz=0 radius=0"));
            }

            // ---- Case C: a named MESH off its true hub — KNOWN analytic answer ----
            {
                var meshPos = new Vector3(5, 0, -2);
                var localOffset = new Vector3(0.3f, -0.2f, 0f);
                // entry positions baked at meshPos + localOffset + blade shape —
                // mirrors the JS's `mesh.position.copy(meshPos)` with blades
                // authored about `localOffset` inside the mesh's own geometry.
                var entry = BladeEntry(meshPos, localOffset, 3);
                var size = BBoxSize(entry.Positions);
                var named = new List<PropellerSystem.NamedCandidate>
                {
                    new PropellerSystem.NamedCandidate { IsBone = false, Size = size, OriginInRoot = meshPos, Entry = entry },
                };
                var r = PropellerSystem.TagPropellers(named, new List<MeshEntry>(), Nose, 1, null);
                var p = r.Props.Count > 0 ? r.Props[0] : null;
                var expect = meshPos + localOffset;
                Debug.Log($"PROPTAG id=mesh_offhub n={r.Props.Count} " +
                    (p != null ? $"hx={p.Hub.x:F6} hy={p.Hub.y:F6} hz={p.Hub.z:F6} radius={p.Radius:F6}" : "hx=0 hy=0 hz=0 radius=0"));
                Debug.Log($"PROPEXPECT id=mesh_offhub ehx={expect.x:F6} ehy={expect.y:F6} ehz={expect.z:F6}");
            }

            // ---- Case D: a named mesh sharing TWO propellers -> SplitNamed ----
            // See tools/_probe_flightrest.mjs's own comment on why blade
            // counts are deliberately distinct (5/4/3, no tie to break) and
            // why want=2 with TWO separately-named candidates is required to
            // ever reach SplitNamed through TagPropellers' own gate.
            {
                var hubA = new Vector3(10, 0, 0);
                var entryA = BladeEntry(hubA, Vector3.zero, 5);
                var sizeA = BBoxSize(entryA.Positions);

                var hub1 = new Vector3(-3, 0, 0);
                var hub2 = new Vector3(3, 0, 0);
                var e1 = BladeEntry(hub1, Vector3.zero, 3);
                var e2 = BladeEntry(hub2, Vector3.zero, 4);
                var combinedPos = e1.Positions.Concat(e2.Positions).ToArray();
                var combinedTri = e1.Tri.Concat(e2.Tri.Select(i => i + e1.Positions.Length)).ToArray();
                var entryB = new MeshEntry { Positions = combinedPos, Tri = combinedTri };
                var sizeB = BBoxSize(combinedPos);

                var named = new List<PropellerSystem.NamedCandidate>
                {
                    new PropellerSystem.NamedCandidate { IsBone = false, Size = sizeA, OriginInRoot = Vector3.zero, Entry = entryA },
                    new PropellerSystem.NamedCandidate { IsBone = false, Size = sizeB, OriginInRoot = Vector3.zero, Entry = entryB },
                };
                var r = PropellerSystem.TagPropellers(named, new List<MeshEntry>(), Nose, 2, null);
                Debug.Log($"PROPTAG id=shared_split n={r.Props.Count}");
                var sorted = r.Props.OrderBy(p => p.Hub.x).ToList();
                for (int i = 0; i < sorted.Count; i++)
                {
                    var p = sorted[i];
                    Debug.Log($"PROPSPLIT id=shared_split{i} hx={p.Hub.x:F6} hy={p.Hub.y:F6} hz={p.Hub.z:F6} blades={p.BladeCount}");
                }
            }

            // ---- Case E: hint.Make builds a propeller from scratch ----
            {
                // TagPropellers checks `allEntries.Count == 0` BETWEEN the
                // named-trust block and every hint/search path (src/propeller.
                // js:912-913) — a harmless decoy entry is required even though
                // hint.Make never reads allEntries itself. See this file's JS
                // counterpart for the same note.
                var decoy = BladeEntry(new Vector3(0, 0, 10), Vector3.zero, 1);
                var hint = new PropHint { Make = new PropMake { Hub = new Vector3(0.1f, 1.2f, 4.0f), Radius = 1.7f, Blades = 4 } };
                var r = PropellerSystem.TagPropellers(new List<PropellerSystem.NamedCandidate>(), new List<MeshEntry> { decoy }, Nose, 1, hint);
                var p = r.Props.Count > 0 ? r.Props[0] : null;
                Debug.Log($"PROPTAG id=hint_make n={r.Props.Count} " +
                    (p != null
                        ? $"hx={p.Hub.x:F6} hy={p.Hub.y:F6} hz={p.Hub.z:F6} radius={p.Radius:F6} blades={p.BladeCount}"
                        : "hx=0 hy=0 hz=0 radius=0 blades=0"));
            }

            // ---- hub-as-AREA-CENTROID proof (src/propeller.js:262-266) ----
            {
                var hub = new Vector3(2, 5, -3);
                var v = new List<Vector3>(); var t = new List<int>();
                for (int i = 0; i < 3; i++) AddBlade(v, t, hub, Vector3.zero, (i / 3f) * Mathf.PI * 2f, 0.15f, 1.0f, 0.08f);
                var entry = new MeshEntry { Positions = v.ToArray(), Tri = t.ToArray() };
                var size = BBoxSize(entry.Positions);
                var named = new List<PropellerSystem.NamedCandidate>
                {
                    // mesh "origin" at world/root origin — NOT at the hub, so a
                    // match here can only come from the triangle geometry.
                    new PropellerSystem.NamedCandidate { IsBone = false, Size = size, OriginInRoot = Vector3.zero, Entry = entry },
                };
                var r = PropellerSystem.TagPropellers(named, new List<MeshEntry>(), Nose, 1, null);
                var p = r.Props.Count > 0 ? r.Props[0] : null;
                Vector3 lo = v[0], hi = v[0];
                foreach (var pt in v) { lo = Vector3.Min(lo, pt); hi = Vector3.Max(hi, pt); }
                var bboxCentre = (lo + hi) / 2f;
                Debug.Log($"PROPCENTROID id=centroid3 hx={(p?.Hub.x ?? 0):F6} hy={(p?.Hub.y ?? 0):F6} hz={(p?.Hub.z ?? 0):F6}");
                Debug.Log($"PROPCENTROID id=centroidexpect hx={hub.x:F6} hy={hub.y:F6} hz={hub.z:F6}");
                Debug.Log($"PROPCENTROID id=bboxcentre hx={bboxCentre.x:F6} hy={bboxCentre.y:F6} hz={bboxCentre.z:F6}");
            }
        }

        // =====================================================================
        // 2. PLANEPHYS — HitPlayer
        // =====================================================================
        class NullWorld : IPlanePhysicsWorld
        {
            public float? GroundAt(float x, float z, float refY, float maxDist) => null;
            public float CameraDistanceTo(Vector3 point) => 50f;
            public float SoundVolume() => 0.7f;
            public void SpawnEmber(Vector3 at) { }
            public void SpawnSmoke(Vector3 at, float size, float dark, float dur, float vy, float spread) { }
            public void Explosion(Vector3 at, float radius) { }
            public void Plume(Vector3 at, float radius, float strength) { }
            public void Wave(Vector3 at, float radius) { }
            public void StampScorch(Vector3 at, float radius) { }
            public void OnShake(float power, float x, float z) { }
            public void DustBurst(Vector3 at, int count, float radius) { }
            public bool DamageBuilding(Vector3 point, Vector3 normal, float roundsEquivalent, out bool destroyed) { destroyed = false; return false; }
            public void SetCollidersEnabled(Transform wrapper, bool enabled_) { }
            public bool RaycastWorld(Vector3 origin, Vector3 dir, float maxDist, out RaycastHit hit) { hit = default; return false; }
        }

        static LooseAirframeActor MakeWreck(PlanePhysicsSystem sys, Vector3 pos, Vector3 vel)
        {
            var go = new GameObject("wreck_probe");
            go.transform.position = pos;
            var def = new PlaneDef { Id = "wreck", Name = "Test Wreck", Mass = 4000f, ColMult = 1.0f };
            var b = new LooseAirframeActor(go.transform, def, sys.World, 50f, 50f, 10f, 20f, false, 0f);
            b.Vel = vel;
            return b;
        }

        static void PlanePhys()
        {
            var world = new NullWorld();

            PilotState Pilot(Vector3 vel) => new PilotState
            {
                Position = Vector3.zero, Rotation = Quaternion.identity,
                Mass = 5000f, ColMult = 1.0f, HalfSpan = 2.5f, Size = 8f, Height = 3f,
                Airborne = true, Speed = 0f, Vel = vel, Rates = Vector3.zero,
            };

            var shellGo = new GameObject("shell_probe");
            var shellBox = shellGo.AddComponent<BoxCollider>();
            // PlanePhysicsSystem.HitPlayer takes the player's WHOLE shell as a
            // list (a real airframe is several box colliders), not one collider.
            var shell = new UnityEngine.Collider[] { shellBox };
            shellBox.center = Vector3.zero; shellBox.size = new Vector3(2, 2, 2);

            // Case 1: too far away — distance gate rejects before any raycast.
            {
                var sys = new PlanePhysicsSystem(world);
                var b = MakeWreck(sys, new Vector3(0, 0, -10000), new Vector3(0, 0, 8));
                var r = sys.HitPlayer(b, 1f / 30f, Pilot(Vector3.zero), shell, false);
                Debug.Log($"HITPLAYER id=too_far handled={B(r.Handled)}");
            }
            // Case 2: closing too slowly — relative-speed gate (V<5) rejects.
            {
                var sys = new PlanePhysicsSystem(world);
                var b = MakeWreck(sys, new Vector3(0, 0, -10), new Vector3(0, 0, 2));
                var r = sys.HitPlayer(b, 1f / 30f, Pilot(Vector3.zero), shell, false);
                Debug.Log($"HITPLAYER id=too_slow handled={B(r.Handled)}");
            }
            // Case 3: crashed pilot — the whole function no-ops.
            {
                var sys = new PlanePhysicsSystem(world);
                var b = MakeWreck(sys, new Vector3(0, 0, -10), new Vector3(0, 0, 8));
                var r = sys.HitPlayer(b, 1f / 30f, Pilot(Vector3.zero), shell, true);
                Debug.Log($"HITPLAYER id=crashed handled={B(r.Handled)}");
            }
            // Case 4: a real hit — deterministic axis-aligned geometry, full solve.
            {
                var sys = new PlanePhysicsSystem(world);
                var b = MakeWreck(sys, new Vector3(0, 0, -10), new Vector3(0, 0, 8));
                var r = sys.HitPlayer(b, 1f / 30f, Pilot(Vector3.zero), shell, false);
                int hints = (r.Handled && r.DamageToUs > 0f) ? 1 : 0;
                var dentDir = -r.ImpactNormal; // the direction the CALLER passes to _applyDamage — see HitPlayerResult's doc comment
                // PilotVelocity/PilotPositionDelta are what the JS mutates
                // directly on `game.vel`/`game.plane.position` through
                // PilotActor's closure — the caller is expected to add these
                // into its own flight state exactly like Ram()'s equivalents.
                Debug.Log($"HITPLAYER id=full_hit handled={B(r.Handled)} hints={hints} dmgUs={r.DamageToUs:F6} " +
                    $"nx={dentDir.x:F6} ny={dentDir.y:F6} nz={dentDir.z:F6} " +
                    $"bvelx={b.Vel.x:F6} bvely={b.Vel.y:F6} bvelz={b.Vel.z:F6} " +
                    $"pvelx={r.PilotVelocity.x:F6} pvely={r.PilotVelocity.y:F6} pvelz={r.PilotVelocity.z:F6} " +
                    $"pdx={r.PilotPositionDelta.x:F6} pdy={r.PilotPositionDelta.y:F6} pdz={r.PilotPositionDelta.z:F6}");
            }
        }

        // =====================================================================
        // 3. PLANEHALL — PlaneHallVisibility's runtime glue
        // =====================================================================
        static void PlaneHall()
        {
            const float yaw = 0.6458f; // rad — same arbitrary angle as the .mjs side
            var hallGo = new GameObject("hall_probe");
            hallGo.transform.position = new Vector3(100f, 0f, 50f);
            hallGo.transform.rotation = Quaternion.Euler(0f, yaw * Mathf.Rad2Deg, 0f);
            var vis = hallGo.AddComponent<PlaneHallVisibility>();
            vis.Fleet = new GameObject("fleet_probe");
            vis.Width = 190f;
            vis.Length = 440f;

            var cases = new (string id, float camX, float camZ)[]
            {
                ("inside_clear", 120f, 60f),
                ("behind_wall", 400f, 55f),
                ("far_away", 100f, 2000f),
            };
            foreach (var (id, camX, camZ) in cases)
            {
                bool visible = vis.ComputeVisible(new Vector3(camX, 0f, camZ));
                Debug.Log($"HALLVIS id={id} yaw={yaw:F6} cx=100 cz=50 camx={camX:F0} camz={camZ:F0} visible={B(visible)}");
            }
        }
    }
}

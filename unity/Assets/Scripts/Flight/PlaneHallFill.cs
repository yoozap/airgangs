using System;
using System.Collections.Generic;
using System.Linq;
using TbilisiFly.Data;
using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.Flight
{
    // Port of src/planehall.js's remainder — everything PlaneHallLayout.cs's
    // header says it did NOT port: addPlaneHall/_fill's GLB-loading/measuring
    // loop (:116-134, :221-312) and _solidify's collider bake (:320-335).
    // PlaneHallLayout.cs itself (siting, Pack/RankDepths/PlaceRanks, the
    // visibility predicates) is NOT modified — this file only calls into it,
    // together with ShedFill.BuildPlaneHall for the hall shell itself.
    //
    // EDITOR-TIME ONLY, same reasoning as ShedFill.cs's header: this loads 35
    // FBX/OBJ assets and instantiates them via AssetDatabase/PrefabUtility,
    // which only exist inside the editor. The whole thing runs once, at bake
    // time, to park the fleet into a saved scene — never at runtime. Wrapped
    // in #if UNITY_EDITOR so it still compiles (as a no-op) in a player build.
    //
    // ================================= HANDEDNESS =================================
    // PlaneHallLayout.Pack/RankDepths/PlaceRanks are pure LANE-LOCAL math in
    // the RAW JS sign convention (x = across the lane, left strip negative /
    // right strip positive; z = along the hall's length) — see that file's
    // own header. Those lane-local (x, z) pairs become real Unity
    // Transform.localPosition values UNDER the hall root, which itself was
    // already placed with GeoFrame.ToUnity (position) + GeoFrame.YawToUnity
    // (rotation) by ShedFill.Build. That composition is NOT free: a local
    // point built from game-frame math under a GeoFrame-converted parent must
    // have its OWN x negated too (not just the parent's), because mirroring
    // one axis is a reflection, not a rotation a parent Transform can absorb
    // on a child's behalf. Worked from the general case (yaw != 0 too):
    //
    //   world_unity = Mirror(Rotate_game(yaw) * local_game + pos_game)
    //   want:        = Rotate_unity(-yaw) * local_unity + Mirror(pos_game)
    //   solving the 2x2 system for local_unity, for ANY yaw, gives the unique
    //   solution local_unity = (-local_game.x, local_game.y, local_game.z).
    //
    // So every parked plane's local position is (-xGame, Wheel, zGame), and
    // every parked plane's local yaw is GeoFrame.YawToUnity(yawGame) — 0 or
    // 180 degrees here, matching src/planehall.js:278-279's `sgn * (LANE/2 +
    // at + span/2)` / `rotation.y = yaw` exactly, just mirrored+negated once
    // at the point they become a Transform. This is the SAME rule
    // ShedFill.cs's header documents for mesh vertices (negate + flip
    // winding) — a position has no winding, so only the negation applies.
    //
    // Each plane's own box collider is built from its MEASURED LOCAL bounds,
    // taken while the instantiated prefab was still at identity (world ==
    // local there, same trick ShedFill.Build uses for the warehouse bounds) —
    // so that collider needs NO mirroring at all: it is defined in the
    // plane's OWN local space, which the FBX importer already gives us
    // correctly oriented (same reasoning as ShedFill's door-cut, point 1).
    public static class PlaneHallFill
    {
        public const string PlanesFbxDir = "Assets/Resources/Art/Planes/";
        public const string SpitfireObjPath = "Assets/Resources/Art/Props/spitfire.obj";
        public const string CockpitKitPath = "Assets/Resources/Art/Props/cockpit.fbx";

        /// <summary>One parked aeroplane — src/planehall.js:270-294 `put`'s
        /// per-machine result, plus the bake-time measurements.</summary>
        public struct ParkedPlane
        {
            public string Id;
            public PlaneDef Def;
            public GameObject GameObject;
            public BoxCollider Collider;
            public Vector3 LocalPosition; // already mirrored — see file header
            public float LocalYawDeg;
            public float Span, Len;       // measured local X/Z extents, metres
            public Bounds LocalBounds;    // measured at identity — for the BoxCollider, no mirroring needed
            public int Rank;
            public Transform CockpitKit;  // PlaneCockpitBaker.Bake's result, flyable:false — null if unbaked/no kit asset
        }

        public struct FillResult
        {
            public GameObject Fleet;        // "planehall_fleet" — src/planehall.js:228-230
            public List<ParkedPlane> Parked;
            public List<string> Failed;     // plane ids that didn't load or had no renderers
            public int TankCount, DpsCount;
            public float TankDepth, DpsDepth; // m of floor used — src/planehall.js:300-301 far/near
        }

        struct BuiltPlane
        {
            public PlaneDef Def;
            public GameObject Go;
            public float Span, Len;
            public Bounds LocalBounds; // measured at identity — see header
        }

        /// <summary>src/planehall.js:221-312 `_fill`. `hall` is the result of
        /// a prior ShedFill.BuildPlaneHall call (its Shell gives the floor
        /// plan's real width/length, exactly like src/planehall.js:255
        /// `this.hall.userData.shedShell`).</summary>
        public static FillResult Fill(ShedFill.ShedBuildResult hall)
        {
            // src/planehall.js:228-230 — every machine under one switch, so
            // they can all be hidden at once. Attaching PlaneHallVisibility
            // below (src/planehall.js:146-194 update()/_walled, now ported —
            // see that file's header) is what actually flips it at runtime.
            var fleet = new GameObject("planehall_fleet");
            fleet.transform.SetParent(hall.Root.transform, false);

            // src/planehall.js:196-219 `_cockpits` needs a per-aeroplane kit
            // to toggle by camera distance. PlaneCockpitBaker.Bake() (Flight/
            // PlaneCockpitBaker.cs) was fully built and never called anywhere
            // in the project — not for the 35 hall-parked planes, not even
            // for the player's own (see BuildPlayableScene.cs/BuildFlyableScene.cs
            // for that half). This is the hall half of that fix: bake a
            // scenery (flyable:false — src/planehall.js:240-243's own
            // draw-call rationale) cockpit per parked machine, below, so
            // PlaneHallVisibility can actually toggle something real.
            var cockpitKitPrefab = ArtAssets.Load<GameObject>(CockpitKitPath);
            if (cockpitKitPrefab == null)
                Debug.LogWarning($"plane hall: missing cockpit kit at {CockpitKitPath} — hall planes will carry no cockpit interior.");

            var built = new List<BuiltPlane>();
            var failed = new List<string>();
            // src/planehall.js:236-252 — load and MEASURE everything first,
            // placing nothing (one per frame in the JS, to avoid a stall; no
            // frame budget to protect inside an editor bake, so this is just a
            // plain loop).
            foreach (var def in Planes.All)
            {
                string path = def.Id == "spitfire" ? SpitfireObjPath : PlanesFbxDir + def.Id + ".fbx";
                var prefab = ArtAssets.Load<GameObject>(path);
                if (prefab == null) { failed.Add(def.Id); Debug.LogWarning($"plane hall: {def.Id} skipped — no asset at {path}"); continue; }

                // NOT parented to `fleet` yet, on purpose: by the time Fill()
                // runs, `hall.Root` already carries its final SHED_YAW
                // rotation (ShedFill.Build set it before returning), so a
                // Renderer.bounds taken from a child of hall.Root would be a
                // WORLD-space AABB re-aligned to the hall's rotated axes, not
                // the plane's own local-axis bounding box — exactly the
                // "world == local" trick ShedFill.Build relies on, broken by
                // an ancestor that is no longer at identity. src/planehall.js
                // never hits this: `wrapper` there is measured as a bare,
                // unparented Object3D (:246-248) BEFORE `fleet.add(wrapper)`
                // (:285) ever happens. Replicated the same way: instantiate at
                // scene root (no parent), measure, THEN reparent under
                // `fleet` once placed, below.
                // NEVER RESET AN FBX'S NATURAL IMPORT TRANSFORM — .claude/
                // skills/unity/SKILL.md's own hard rule, and this line was
                // breaking it. The previous version instantiated the prefab
                // and immediately overwrote its root with
                // SetPositionAndRotation(zero, identity) + localScale =
                // def.Scale. Measured on the real assets (Editor/Probes/
                // PlanePropsProbe.cs's PROPSNAT line), 34 of the 35 import at
                // rotation (0,0,0) / scale (1,1,1) and did not care — and
                // **t28.fbx imports at rotation (270.02, 0, 0) / scale
                // (100, 100, 100)**, so the T-28 was parked in the hall
                // **12 cm across and lying on its back**. Nothing warned:
                // the floor-fit check only fires on a plane that is too WIDE,
                // and a 0.12 m airframe measures, packs, places and
                // collider-bakes without complaint.
                //
                // Composed instead of discarded, the same way the crane fix
                // did it: a plain holder GameObject carries the PLACEMENT
                // (identity here, the lane position/yaw later) and the prefab
                // keeps its own natural transform as its local one. Everything
                // downstream still sees one root per aeroplane — the measured
                // bounds below now simply include the import transform, which
                // is what makes them the real airframe.
                //
                // AND THE AIRFRAME IS STRAIGHTENED. The holder used to BE the
                // prefab's only parent, so the file's own stored orientation
                // was the orientation in the hall: four machines parked
                // tail-first (hsp75, javelin, h6k, il76 — `yaw = PI`), two
                // sideways (cessna310, f4f — `yaw = -PI/2`, which also made
                // this loop's `Span = size.x, Len = size.z` read the Cessna
                // 310 as 9.88 x 11.20 when it is 11.25 x 9.74), tornado and
                // tu144 ~40 deg askew, and neonfly rolled 90 deg onto a wing.
                // src/planes.js:826-836 straightens exactly that set; the
                // chain Flight/PlaneModelFrame.Attach builds carries it,
                // together with the 180 deg yaw that puts the straightened
                // nose on FlightModel's +Z. def.Scale moves onto the chain's
                // own `model_scale` node (the JS's `group`, planes.js:1537) so
                // that the cockpit measurement in `model_frame` sees a scaled
                // airframe exactly as findCockpit's `wrapper` does.
                var inner = (GameObject)ArtAssets.Instantiate(prefab);
                var go = new GameObject(def.Id);
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                go.transform.localScale = Vector3.one;
                PlaneModelFrame.Attach(go.transform, inner, def);

                var rends = go.GetComponentsInChildren<Renderer>(true);
                if (rends.Length == 0)
                {
                    failed.Add(def.Id);
                    Debug.LogWarning($"plane hall: {def.Id} skipped — no renderers in {path}");
                    UnityEngine.Object.DestroyImmediate(go);
                    continue;
                }
                // src/planehall.js:245-248 — measure the REAL built airframe,
                // not a stale table. `go` is unparented and at identity, so
                // this IS the plane's own local bounding box, and now also the
                // STRAIGHTENED one (nose +Z, span along X).
                //
                // VERTEX-PRECISE, not Renderer.bounds. src/planes.js:1530 says
                // why in its own comment: "precise=true walks the vertices:
                // for the models that carry an orientation fix, a plain AABB
                // gets inflated by the rotation and the plane reports a
                // wingspan half again as wide as it is". Renderer.bounds is a
                // world AABB of an already-rotated mesh AABB, i.e. exactly the
                // inflated kind — harmless while every model sat at identity,
                // wrong the moment tornado (-0.658 rad), tu144 (-0.702) and
                // neonfly (roll -PI/2) are actually straightened. BoxOf IS the
                // JS's setFromObject(obj, true).
                Bounds b = PlaneCockpitMeasurement.BoxOf(go.transform, go.transform);
                built.Add(new BuiltPlane { Def = def, Go = go, Span = b.size.x, Len = b.size.z, LocalBounds = b });
            }

            // src/planehall.js:260-264 — the floor-fit sanity warning.
            float halfW = hall.Shell.Width / 2f - PlaneHallLayout.Wall;  // :256
            float halfL = hall.Shell.Length / 2f - PlaneHallLayout.End; // :257
            float strip = halfW - PlaneHallLayout.Lane / 2f;             // :269
            if (built.Count > 0)
            {
                var widest = built.OrderByDescending(p => p.Span).First();
                if (widest.Span > strip)
                    Debug.LogWarning($"plane hall: {widest.Def.Id} is {widest.Span:F0} m across and the " +
                        $"strips beside the lane are {strip:F0} — widen the hall");
            }

            var tanks = built.Where(p => Planes.IsTankPlane(p.Def)).ToList(); // :258
            var rest = built.Where(p => !Planes.IsTankPlane(p.Def)).ToList();  // :259

            var parked = new List<ParkedPlane>();
            // src/planehall.js:300-301 — stacked from opposite ends: tanks from
            // -halfL outward (dir=+1, yaw=PI), the rest from +halfL inward
            // (dir=-1, yaw=0).
            float farDepth = PlaceGroup(tanks, strip, -halfL, +1, Mathf.PI, parked);
            float nearDepth = PlaceGroup(rest, strip, +halfL, -1, 0f, parked);
            if (built.Count > 0 && farDepth + nearDepth + PlaneHallLayout.Aisle > halfL * 2f) // :302-305
                Debug.LogWarning($"plane hall: {(farDepth + nearDepth + PlaneHallLayout.Aisle):F0} m of aeroplanes " +
                    $"on {(halfL * 2f):F0} m of floor — the two sides have met");

            int propPlanes = 0, propellers = 0;
            var propFailed = new List<string>();
            for (int i = 0; i < parked.Count; i++)
            {
                var p = parked[i];
                // Reparent now, once placed — see the load loop's comment on
                // why measurement had to happen unparented.
                p.GameObject.transform.SetParent(fleet.transform, false);
                p.GameObject.transform.localPosition = p.LocalPosition;     // :278
                p.GameObject.transform.localRotation = Quaternion.Euler(0f, p.LocalYawDeg, 0f); // :279

                // src/planes.js:803-812 + :1593 — FIND THE BLADES. The JS runs
                // tagPropellers at model load and finishPropellers on every
                // built wrapper, so a parked machine has its propeller cut out
                // onto a real pivot, tagged, and given a blur disc exactly like
                // a flyable one; src/planehall.js's `put` goes through the same
                // loadPlane/makeGameplayPlane pair as the player's aeroplane.
                // Before this line nothing in the project called either
                // function for an aircraft, so all 35 had their blades welded
                // to the nose and Combat/PlaneDamage classified them as flat
                // hull (its PartsOf CACHES Spin per record at first-hit time,
                // which is why this has to happen at BUILD, not lazily).
                // They do not SPIN — PlanePropellers reads 0 rpm for anything
                // that is not the plane the player is flying.
                //
                // BEFORE the shadow pass below, not after: the propeller's own
                // new renderers (a cut blade, or the P-51's built one, which
                // src/propeller.js:707 marks castShadow = true) must be caught
                // by src/planehall.js:284's "no shadow for 35 parked machines"
                // sweep like everything else under the plane.
                if (p.Def.Prop)
                {
                    var props = p.GameObject.AddComponent<PlanePropellers>();
                    props.PlaneId = p.Def.Id;
                    // THE FILE FRAME. Detection/cutting and the hand-measured
                    // hub hints are all quoted in the asset's own stored
                    // orientation (PlaneProps.FileNose), so the search must
                    // run below the straightening, not above it — otherwise
                    // the 9 straightened defs would be searched along an axis
                    // their geometry no longer lies on.
                    props.ModelRoot = PlaneModelFrame.FileFrameOf(p.GameObject.transform);
                    props.EnsureBuilt(); // AddComponent outside play mode never delivers OnEnable
                    propPlanes++;
                    propellers += props.Count;
                    if (props.Count == 0) propFailed.Add(p.Def.Id);
                }

                // src/planehall.js:284 — roofed hall, no shadow pass for 35
                // parked machines nobody will ever see cast one.
                foreach (var r in p.GameObject.GetComponentsInChildren<Renderer>(true))
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                // src/planehall.js:289, 320-335 `_solidify` — UNITY-NATIVE
                // SIMPLIFICATION per PlanePhysics.cs's own documented
                // precedent: a plain BoxCollider that follows its Transform
                // for free, instead of a baked world-space triangle-soup
                // Mesh collider. Sized from the measured LOCAL bounds taken
                // while the plane was still at identity (no mirroring needed
                // — see file header, last paragraph).
                var box = p.GameObject.AddComponent<BoxCollider>();
                box.center = p.LocalBounds.center;
                box.size = p.LocalBounds.size;
                p.Collider = box;

                // src/planehall.js:240-243 loadCockpitKit(..., flyable:false) —
                // scenery only, no stick/throttle/pedals (fitCockpitKit strips
                // them when flyable is false, same as the player's own bake).
                if (cockpitKitPrefab != null)
                {
                    var cockpitRig = PlaneCockpitBaker.Bake(p.GameObject, p.Def, cockpitKitPrefab,
                        flyable: false, out _);
                    p.CockpitKit = cockpitRig.CockpitKitValue;
                }
                // src/planehall.js:290 `citymap.targets?.register(p.wrapper,
                // p.def, col)` — NOW WIRED. It was left out as "a sibling
                // agent's territory, a few lines once someone owns it", and
                // those few lines are these: the Unity port's target registry
                // is not registered into directly, it is DISCOVERED.
                // Core/CombatWorldAdapter.Install() sweeps
                // `FindObjectsByType<PlaneDamageRecord>()` exactly once and
                // turns every hit into BOTH a PlaneDamage.Watch() subject and a
                // GameLoop.Targets entry; Character/FootBoarding.NearestPlane()
                // then walks that registry to decide which machine Enter
                // boards. With no PlaneDamageRecord on them, the 35 parked
                // machines were in no registry at all: not clickable, not
                // Tab-able, taking no damage, and not boardable — i.e. "walk up
                // to any parked plane and press Enter" could not work no matter
                // what else was right.
                //
                // Hull points are src/target.js:69's own `planeStamina(def)`,
                // already ported in PlaneCatalog.PlaneStamina (JsMath.Round in
                // double — NOT Mathf.RoundToInt; see .claude/skills/unity/
                // SKILL.md). The three airframe facts are src/planes.js:
                // 1574-1587's own formulae, read off the SAME identity-measured
                // local bounds this file already took (X is a symmetric extent
                // and Z is unmirrored between the two frames, so neither needs
                // a handedness flip — see this file's header).
                var rec = p.GameObject.AddComponent<TbilisiFly.Combat.PlaneDamageRecord>();
                rec.HalfSpan = p.Span * 0.5f;                             // planes.js:1574
                rec.Size = Mathf.Max(p.Span, p.Len);                      // planes.js:1575
                // planes.js:1587 `noseLen = -wbox.min.z` — the JS measures in a
                // frame whose nose is -Z. This frame's nose is +Z (the plane
                // is straightened now and `max.z` IS the spinner), so the same
                // quantity is +max.z. The sign is the whole fix in one line:
                // before it, the "nose" distance was measured to the TAIL, and
                // PlaneWallContact cast its contact ray that far behind the
                // aeroplane.
                rec.NoseLen = Mathf.Max(1f, p.LocalBounds.max.z);
                rec.MaxHullPoints = Planes.PlaneStamina(p.Def);           // target.js:69
                rec.HullPoints = rec.MaxHullPoints;
                rec.DisplayName = p.Def.Name;                             // planedamage.js:1341
                rec.IsPlayerOwn = false;
                rec.CockpitKit = p.CockpitKit;                            // set by the bake just above

                parked[i] = p; // ParkedPlane is a struct — write the collider/cockpit back into the list
            }

            // src/planehall.js:146-194 — the runtime fleet-visibility switch.
            // Attached to the hall root (not `fleet` itself: Update() needs to
            // keep running even while `fleet` is inactive) so the baked scene
            // carries its own driver with no further wiring.
            var visibility = hall.Root.AddComponent<PlaneHallVisibility>();
            visibility.Fleet = fleet;
            visibility.Width = hall.Shell.Width;
            visibility.Length = hall.Shell.Length;

            // src/planehall.js:196-219 `_cockpits` — draw a parked machine's
            // cockpit interior only for someone close enough to look into it.
            // Places are taken once here (the JS's own "places are taken once
            // -- a parked machine's matrix is frozen" — matrixAutoUpdate=false
            // there; our planes are likewise never moved post-bake) off each
            // plane's real WORLD position, matching src/planehall.js:199-202
            // reading `p.matrixWorld.elements` rather than a hall-local value.
            var cockpitEntries = new List<PlaneHallVisibility.CockpitEntry>();
            foreach (var p in parked)
                if (p.CockpitKit != null)
                    cockpitEntries.Add(new PlaneHallVisibility.CockpitEntry
                    { PlanePos = p.GameObject.transform.position, Kit = p.CockpitKit.gameObject });
            visibility.Cockpits = cockpitEntries.ToArray();

            // Evidence that the registry sweep above will actually FIND the
            // fleet — a count of real components on real scene objects, not a
            // calculation. CombatWorldAdapter.Install()'s
            // FindObjectsByType<PlaneDamageRecord>() runs from
            // PlayableSceneBoot.Start(), i.e. after every OnEnable in the
            // loading scene, so this is in place before it sweeps.
            int recs = 0; float hpSum = 0f;
            foreach (var p in parked)
            {
                var r = p.GameObject.GetComponent<TbilisiFly.Combat.PlaneDamageRecord>();
                if (r != null) { recs++; hpSum += r.MaxHullPoints; }
            }
            Debug.Log($"PLANEHALLFILL damageRecords={recs}/{parked.Count} hullPointsTotal={hpSum:F0} " +
                      $"boardable={(recs == parked.Count).ToString().ToLowerInvariant()} {ArtAssets.Tag}");

            // Evidence that the propeller pipeline is REACHED, per the port's
            // own rule that a probe proves a calculation and not that the
            // calculation runs: real components on real scene objects, plus
            // the per-machine detail the JS's own
            // `console.log(`${def.id}: ${found.length} propeller(s)`)`
            // (src/planes.js:810-812) prints.
            foreach (var p in parked)
            {
                var pr = p.GameObject.GetComponent<PlanePropellers>();
                if (pr == null) continue;
                var plan = PlaneProps.CachedPlan(p.Def.Id);
                Debug.Log($"PROPELLER id={p.Def.Id} want={Mathf.Max(1, p.Def.Props)} found={pr.Count} " +
                          $"how={(plan != null ? plan.How : "?")} tagged={pr.Count} " +
                          $"discs={CountDiscs(pr)} ms={(plan != null ? plan.Millis : 0f):F0}");
            }
            Debug.Log($"PROPELLERS planes={propPlanes} propellers={propellers} " +
                      $"missing={propFailed.Count}{(propFailed.Count > 0 ? " ids=" + string.Join(",", propFailed) : "")} {ArtAssets.Tag}");

            return new FillResult
            {
                Fleet = fleet, Parked = parked, Failed = failed,
                TankCount = tanks.Count, DpsCount = rest.Count,
                TankDepth = farDepth, DpsDepth = nearDepth,
            };
        }

        static int CountDiscs(PlanePropellers pr)
        {
            int n = 0;
            foreach (var i in pr.Props) if (i != null && i.Disc != null) n++;
            return n;
        }

        /// <summary>src/planehall.js:270-294 `put`. Returns the total depth
        /// consumed (:294), matching `far`/`near` at :300-301.</summary>
        static float PlaceGroup(List<BuiltPlane> group, float strip, float fromZ, int dir, float yawGame,
            List<ParkedPlane> outp)
        {
            if (group.Count == 0) return 0f;
            var items = group.Select(p => new PlaneHallLayout.PackItem { Tag = p.Go, Span = p.Span, Len = p.Len }).ToList();
            var placed = PlaneHallLayout.Pack(items, strip);
            var depths = PlaneHallLayout.RankDepths(items, placed);
            var (positions, totalDepth) = PlaneHallLayout.PlaceRanks(placed, depths, fromZ, dir);

            var byGo = group.ToDictionary(p => (object)p.Go, p => p);
            var placedByGo = placed.ToDictionary(p => p.Tag, p => p);
            float yawUnityDeg = GeoFrame.YawToUnity(yawGame) * Mathf.Rad2Deg;

            foreach (var (tag, xPartial, z) in positions)
            {
                var src = byGo[tag];
                var pl = placedByGo[tag];
                float sgn = pl.Side == 0 ? -1f : 1f;
                // PlaceRanks's own doc comment: its returned x is (Lane/2+At)
                // only — it does NOT carry span, so the caller re-derives the
                // full x exactly like src/planehall.js:278
                // `sgn * (LANE/2 + at + p.span/2)` before negating for Unity.
                float xGame = xPartial + sgn * src.Span / 2f;

                outp.Add(new ParkedPlane
                {
                    Id = src.Def.Id, Def = src.Def, GameObject = src.Go,
                    // Y: -LocalBounds.min.y, NOT the PlaneHallLayout.Wheel constant.
                    //
                    // `Wheel = 1.7` is only correct if the airframe's belly sits at
                    // local y = -1.7, which is how the JS arranges it — but at
                    // RUNTIME: src/planes.js:1563-1564 does
                    //     const drop = wbox.min.y + 1.7; group.position.y -= drop;
                    // so every JS plane ends up belly-at--1.7 by construction. The
                    // port never replicated that drop, and the raw FBX pivots do not
                    // have it baked in — measured across 8 real assets, min.y is
                    // ~0.000 for p51/f15/b737/an225/cessna310/tu144, 0.061 for mig21,
                    // 0.469 for spitfire. So placing at y=1.7 left all 35 parked
                    // planes FLOATING about 1.7 m above the hangar floor (the baked
                    // scene really did read localY=1.7000 for every one of them).
                    //
                    // -min.y puts the belly exactly on the floor, which is the same
                    // net result the JS reaches (belly -1.7 + wrapper +1.7 = 0) while
                    // using this plane's OWN measured box rather than assuming a
                    // normalisation step that does not happen here. `b` was measured
                    // two blocks up, unparented and at identity, so it is local.
                    LocalPosition = new Vector3(-xGame, -src.LocalBounds.min.y, z), // mirror local x — see file header
                    LocalYawDeg = yawUnityDeg,
                    Span = src.Span, Len = src.Len, LocalBounds = src.LocalBounds, Rank = pl.Rank,
                });
            }
            return totalDepth;
        }
    }
}

using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.Flight
{
    /// <summary>
    /// THE ONE PLACE THE FLIGHT FRAME AND THE MODEL FRAME MEET.
    ///
    /// Port of src/planes.js:813-836 `makePlaneModel`'s straightening tail and
    /// of :1528-1540 `makeGameplayPlane`'s wrapper/group nesting, as a real
    /// Transform chain. Before this file existed, the aeroplane model was
    /// parented to the flight Transform at IDENTITY
    /// (Editor/BuildPlayableScene.cs:246-247, Editor/BuildFlyableScene.cs:85-86,
    /// Flight/PlaneHallFill.cs) and PlaneDef.YawUnity/RollUnity had zero
    /// Transform consumers — so the player flew the Mustang TAIL-FIRST, four
    /// airframes were parked tail-first, two sideways, two ~40 degrees askew
    /// and neonfly was rolled onto a wing. docs/QUALITY_BAR.md's
    /// "THE BIGGEST VISIBLE BUG".
    ///
    /// ============================== THE CHAIN ==============================
    ///
    ///   planeRoot                   FLIGHT frame, nose = +Z = plane.forward
    ///    |                          (Flight/FlightModel.cs thrusts along it;
    ///    |                           Combat/Guns.cs's MuzzleLocal z = +3.65
    ///    |                           already assumes it)
    ///    +-- "model_frame"          localRotation = 180 deg about Y
    ///         |                     == src/planes.js's `wrapper` space,
    ///         |                        nose = -Z. EVERY -Z-ANCHORED
    ///         |                        MEASUREMENT RUNS IN HERE.
    ///         +-- "model_scale"     localScale = def.Scale
    ///              |                == the JS's `group` (planes.js:1537)
    ///              +-- "model_file" localRotation = the per-def orientation
    ///                   |           fix (YawUnity/RollUnity) == the JS's
    ///                   |           `fixed` group (planes.js:834). ITS OWN
    ///                   |           local space is the FILE frame, which is
    ///                   |           what Flight/PlaneProps.FileNose and
    ///                   |           PropellerSystem work in.
    ///                   +-- model   the imported FBX/OBJ, keeping its
    ///                               NATURAL import transform (t28.fbx is
    ///                               rot (270,0,0) / scale 100 — never reset
    ///                               it) and its local position at ZERO.
    ///
    /// Both of src/planes.js's two recentres land on `model_file`'s
    /// localPosition rather than on the model, and for the same reason (the
    /// propeller hub hints are authored in the FILE frame): the fix-pivot one
    /// (planes.js:830-832, only when def.yaw/roll exist) in <see
    /// cref="Attach"/>, and the unconditional X/Z one (planes.js:1533-1535)
    /// in <see cref="RecentreXZ"/>. Y is seated separately by
    /// <see cref="SeatTheBelly"/>, on `model_scale`, exactly as planes.js:1564
    /// does it.
    ///
    /// Three nodes, not one, because three different frames are genuinely
    /// needed and collapsing any two of them re-creates the bug:
    ///   * a frame whose local space INCLUDES def.Scale and has the nose at
    ///     -Z — that is `model_frame`, and it is the JS's `wrapper` exactly
    ///     (planes.js:1541-1543 measures findCockpit against the wrapper,
    ///     i.e. after the scale). A node's own localScale does NOT appear in
    ///     its own local space, so the scale has to live BELOW it.
    ///   * a frame whose local space EXCLUDES def.Scale and still has the
    ///     file's own (possibly tail-first) orientation — that is
    ///     `model_file`, and it is byte-identical to the frame
    ///     PlaneProps.Build/PropellerSystem have always run in, which is why
    ///     PROPSUM stays 31/31 with the same radii and the same hinted hubs.
    ///   * the flight frame itself, which is `planeRoot`.
    ///
    /// WHY 180 DEGREES AND NOT A Z MIRROR. Measured, not reasoned
    /// (Editor/Probes/PlanePropsProbe.Axis, all 35 real assets): the
    /// glTF -> Blender -> FBX bridge (tools/glb_batch_fbx.py,
    /// axis_forward='-Z') spends its one handedness flip on **X**, so a GLB
    /// (x,y,z) arrives here as (-x,y,z) and the JS's straightened nose
    /// (0,0,-1) is STILL (0,0,-1). Composing that X mirror with this 180 deg
    /// yaw gives (x,y,z)_js -> (x,y,-z)_root in the finished flight frame:
    /// X is kept and Z negates, which is exactly Flight/FlightModel.cs's
    /// body-frame rule ("right and up are the same in both; only forward's
    /// label flips"). MIRROR X ONCE, NOT TWICE — a geometry-derived quantity
    /// measured in `model_frame` self-corrects through
    /// <see cref="ModelPointToRoot"/>; a hand-authored constant must be
    /// carried through <see cref="PlaneDef.YawUnity"/>/<see cref="PlaneDef.RollUnity"/>,
    /// which own that negation and are read here and nowhere else.
    /// </summary>
    public static class PlaneModelFrame
    {
        public const string FrameName = "model_frame";
        public const string ScaleName = "model_scale";
        public const string FileName  = "model_file";

        /// <summary>The 180 degree yaw that turns a straightened -Z nose into
        /// FlightModel's +Z forward. The whole of this fix is this one
        /// rotation plus handing the sites below the frame it created.</summary>
        public static Quaternion FlightFromModel => Quaternion.AngleAxis(180f, Vector3.up);

        /// <summary>src/planes.js:834 `fixed.rotation.set(0, def.yaw, def.roll)`,
        /// through PlaneDef's own mirror accessors. No def in the table sets
        /// both at once (PlaneCatalog.cs's handedness header), so the Euler
        /// order cannot disagree — composed explicitly anyway.</summary>
        public static Quaternion FixOf(PlaneDef def)
        {
            if (def == null) return Quaternion.identity;
            if (def.Yaw == 0f && def.Roll == 0f) return Quaternion.identity;
            return Quaternion.AngleAxis(def.YawUnity * Mathf.Rad2Deg, Vector3.up)
                 * Quaternion.AngleAxis(def.RollUnity * Mathf.Rad2Deg, Vector3.forward);
        }

        /// <summary>The three frame nodes plus the model itself. Any of them
        /// can be null on a plane that was never built through this file.</summary>
        public struct Frames
        {
            public Transform Frame;   // model_frame — the JS wrapper; nose -Z. Measure HERE.
            public Transform Scale;   // model_scale — the JS group; carries def.Scale
            public Transform File;    // model_file  — the JS fixed group; the FILE frame
            public Transform Model;   // the imported asset root, natural transform intact

            public bool Valid => Frame != null && Scale != null && File != null;
        }

        /// <summary>Find an existing chain under `planeRoot`. Cheap: three
        /// name lookups, no allocation.</summary>
        public static Frames Of(Transform planeRoot)
        {
            var f = new Frames();
            if (planeRoot == null) return f;
            f.Frame = planeRoot.Find(FrameName);
            if (f.Frame == null) return f;
            f.Scale = f.Frame.Find(ScaleName);
            if (f.Scale == null) return f;
            f.File = f.Scale.Find(FileName);
            if (f.File != null && f.File.childCount > 0) f.Model = f.File.GetChild(0);
            return f;
        }

        /// <summary>The frame every -Z-anchored measurement must be given:
        /// `model_frame` when the chain exists, and `planeRoot` itself when it
        /// does not (which is exactly the pre-fix behaviour, so a plane built
        /// by some other path still measures the way it used to instead of
        /// throwing).</summary>
        public static Transform MeasureFrameOf(Transform planeRoot)
        {
            var f = Of(planeRoot);
            return f.Frame != null ? f.Frame : planeRoot;
        }

        /// <summary>The frame Flight/PlaneProps + Flight/PropellerSystem must
        /// be given — the FILE frame, unscaled, with the asset's own stored
        /// orientation. Falls back to `planeRoot` for the same reason.</summary>
        public static Transform FileFrameOf(Transform planeRoot)
        {
            var f = Of(planeRoot);
            return f.File != null ? f.File : planeRoot;
        }

        /// <summary>Re-express a point measured in `model_frame` (the JS
        /// wrapper space) in the flight root's own space. Reads the frame's
        /// ACTUAL orientation rather than assuming the 180 degrees, so a
        /// future change to the chain cannot silently desync a caller.</summary>
        public static Vector3 ModelPointToRoot(Transform planeRoot, Vector3 modelLocal)
        {
            var frame = Of(planeRoot).Frame;
            if (frame == null || planeRoot == null) return modelLocal;
            return planeRoot.InverseTransformPoint(frame.TransformPoint(modelLocal));
        }

        /// <summary>
        /// Build the chain under `planeRoot` and hang `modelInstance` (an
        /// already-instantiated FBX/OBJ root, with its natural import
        /// transform untouched) at the bottom of it.
        ///
        /// src/planes.js:826-836 in order: parent, recentre on the AIRFRAME
        /// when a fix exists (":828 Pivot on the AIRFRAME, not on the file's
        /// origin"), then rotate the wrapper around it.
        /// </summary>
        public static Frames Attach(Transform planeRoot, GameObject modelInstance, PlaneDef def)
        {
            if (planeRoot == null) return default;

            // UNPACK FIRST, BEFORE ANYTHING REPARENTS. Every caller hands us an
            // asset instance made with ArtAssets.Instantiate (the hall
            // fill, both scene builders), and the two passes that follow the
            // chain -- PlaneCockpitKit.HingeHood, which slips a hinge pivot
            // between the canopy and its parent, and PlaneProps, which cuts the
            // blades onto their own pivot -- reparent nodes INSIDE that
            // instance. Unity refuses that with "Setting the parent of a
            // transform which resides in a Prefab instance is not possible",
            // once per node: 10 errors on every edit-mode load of
            // Assets/Scenes/Game.unity (Glass_Glass_0, GlassFront,
            // Helice_Helice_MAT_0, pervane_Cube.001_pervane, ...), and the
            // hinge/propeller those errors belong to silently does not happen.
            // THE `!Application.isPlaying` GUARD THAT USED TO BE HERE WAS WRONG
            // (removed 2026-10-31). Its premise -- "an instance created in play
            // mode is not a tracked prefab instance at all" -- is FALSE for
            // ArtAssets.Instantiate, which is PrefabUtility.InstantiatePrefab in
            // the EDITOR, play mode included. So the editor's own play mode kept
            // the prefab link, the reparents below still threw, and the
            // play-mode run logged two of them every session:
            //   'pervane_Cube.001_pervane' and 'Helice_Helice_MAT_0'
            // -- the propeller nodes PlaneProps cuts onto their own pivot, i.e.
            // exactly the silent failure this unpack exists to prevent.
            // PlaneCockpitKit.UnpackPrefabAround was added as a local
            // workaround for the canopy half and stays (harmless, idempotent --
            // UnpackCompletely early-returns on a non-instance).
            //
            // There is nothing to lose by unpacking: these instances are built
            // fresh by ShedSceneFill.OnEnable on every scene load and are never
            // serialised, so the prefab link buys nothing and costs this. And in
            // a PLAYER ArtAssets.UnpackCompletely is a no-op, so this is free.
            if (modelInstance != null)
                ArtAssets.UnpackCompletely(modelInstance);

            var existing = Of(planeRoot);
            Transform frame = existing.Frame, scale = existing.Scale, file = existing.File;

            if (frame == null)
            {
                frame = new GameObject(FrameName).transform;
                frame.SetParent(planeRoot, false);
            }
            frame.localPosition = Vector3.zero;
            frame.localRotation = FlightFromModel;   // the single meeting point
            frame.localScale = Vector3.one;

            if (scale == null)
            {
                scale = new GameObject(ScaleName).transform;
                scale.SetParent(frame, false);
            }
            scale.localPosition = Vector3.zero;
            scale.localRotation = Quaternion.identity;
            float s = def != null && def.Scale > 0f ? def.Scale : 1f;
            scale.localScale = new Vector3(s, s, s);  // src/planes.js:1537

            var fix = FixOf(def);
            if (file == null)
            {
                file = new GameObject(FileName).transform;
                file.SetParent(scale, false);
            }
            file.localPosition = Vector3.zero;
            file.localRotation = fix;                 // src/planes.js:834
            file.localScale = Vector3.one;

            if (modelInstance != null)
            {
                // worldPositionStays:false — KEEPS the FBX's natural import
                // rotation/scale as its local one. .claude/skills/unity/SKILL.md's
                // hard rule; t28.fbx imports at rot (270.02,0,0) / scale 100.
                modelInstance.transform.SetParent(file, false);
                modelInstance.transform.localPosition = Vector3.zero;
                if (fix != Quaternion.identity)
                {
                    // src/planes.js:830 — pivot on the AIRFRAME, not on the
                    // file's origin: "a model whose origin sits off to one side
                    // swings out when rotated, and every consumer that measures
                    // the result (wingspan for the wingtip smoke, size for
                    // collisions) reads a bbox far bigger than the aircraft".
                    // Vertex-precise, which is the JS's own
                    // setFromObject(model, true).
                    //
                    // THE RECENTRE GOES ON THIS NODE, NOT ON THE MODEL, and it
                    // is pre-rotated. The JS puts it on the model
                    // (`model.position.sub(center)`) INSIDE the fixed group,
                    // which is algebraically the same thing —
                    // fix*(v - centre) == fix*v + (-(fix*centre)) — but it is
                    // not the same thing for this port, because `model_file`'s
                    // own local space is what Flight/PlaneProps searches in and
                    // the hub HINTS in src/planes.js (cessna310's propHubs,
                    // the P-51's propMake.hub) are hand-measured ABSOLUTE
                    // coordinates in the file's own origin. Translating the
                    // model under them put the Cessna 310's two hinted hubs
                    // 5 m from any geometry and its propeller count went
                    // 2 -> 0 (`PROPELLERS ... missing=1 ids=cessna310` in the
                    // bake log). MIRROR X ONCE, NOT TWICE has a translation
                    // twin: move a hand-measured constant's ORIGIN once and it
                    // is just as broken.
                    var b = PlaneCockpitMeasurement.BoxOf(modelInstance.transform, file);
                    file.localPosition = -(fix * b.center);
                }

                RecentreXZ(scale, file, modelInstance.transform, def);
                SeatTheBelly(frame, scale, modelInstance.transform);
            }

            return new Frames
            {
                Frame = frame, Scale = scale, File = file,
                Model = modelInstance != null ? modelInstance.transform : (file.childCount > 0 ? file.GetChild(0) : null),
            };
        }

        /// <summary>src/planes.js:1563 `const drop = wbox.min.y + 1.7;` — the
        /// airframe's local belly height. EVERY plane in the JS is normalised
        /// to this at build time, which is the ONLY reason src/game.js:67's
        /// single global `WHEEL_HEIGHT = 1.7` is safe for a fleet that runs
        /// from an 8.5 m racer to a 52 m transport.</summary>
        public const float BellyLocalY = -1.7f;

        /// <summary>
        /// src/planes.js:1556-1564 `makeGameplayPlane`'s belly seating, which
        /// this port had never replicated:
        ///
        ///     const wbox = new THREE.Box3().setFromObject(wrapper, true);
        ///     const drop = wbox.min.y + 1.7;   // WHEEL_HEIGHT in game.js
        ///     group.position.y -= drop;
        ///
        /// `frame` IS the JS's `wrapper` and `scale` IS its `group` (see THE
        /// CHAIN above), so this is the same two lines in the same two frames.
        ///
        /// WHY IT MATTERS, measured (PLAYMODE FLY_BELLY_OFFSET on the first
        /// play-mode run): without it the raw import pivot is the belly
        /// reference, and for the Spitfire that pivot sits 0.159 m ABOVE the
        /// lowest vertex, so PlaneController's measured
        /// `-BoxOf(...).min.y` came out at **-0.159** and
        /// PlaneGroundProbe.WheelHeightFor returned ~0: the ground roll asked
        /// for an origin 16 cm BELOW the street every frame, and only
        /// PlaneController.Step's floor clamp held the machine up. The two
        /// fought for all 231 taxi frames (`streetY=478.422 floorY=478.263
        /// y=478.710`, agl pinned at exactly 1.2 instead of the JS's 1.7).
        ///
        /// Seating the belly here restores the JS's invariant for every
        /// airframe at once, so the 1.7 m constant means what it says again,
        /// and the per-airframe <see cref="PlaneGroundProbe.BellyOffset"/>
        /// measures 1.7 for all of them instead of a different number each.
        /// The parked fleet does not move: PlaneHallFill.cs:485 places each
        /// machine at `-LocalBounds.min.y`, measured AFTER this runs, so its
        /// local y becomes +1.7 and the belly still lands exactly on the floor
        /// (that file's own comment predicted this: "belly -1.7 + wrapper
        /// +1.7 = 0").
        ///
        /// THE X/Z HALF OF THE SAME `makeGameplayPlane` PROLOGUE IS NOW DONE
        /// TOO, in <see cref="RecentreXZ"/> — this comment used to record it as
        /// a deliberate, documented deviation (docs/open_items.json
        /// `xz-recentre-deviation`) and it is CLOSED (2026-10-31). See that
        /// method for why the three hand-measured consumers it was held back
        /// for were in fact the argument FOR doing it.
        /// </summary>
        /// <summary>
        /// src/planes.js:1533-1535, the UNCONDITIONAL half of
        /// `makeGameplayPlane`'s prologue:
        ///
        ///     const box = new THREE.Box3().setFromObject(model, true);
        ///     const center = box.getCenter(new THREE.Vector3());
        ///     model.position.sub(center);
        ///
        /// Note WHICH node that is. At :1533 `model` is whatever
        /// `makePlaneModel` returned — the `fixed` group when the def carries
        /// an orientation fix, the bare clone when it does not — and it has
        /// not been parented into `group` yet, so its world matrix IS its
        /// local one and `box`/`center` are in the space `group` will give it.
        /// In THE CHAIN above that node is `model_file` and that space is
        /// `model_scale`. So this is one line on `file.localPosition`,
        /// measured in `scale`, and it runs for EVERY airframe — which is the
        /// deviation docs/open_items.json called `xz-recentre-deviation`:
        /// before this, the port recentred only inside the `fix != identity`
        /// branch above (planes.js:832, the OTHER recentre, which is a
        /// different line in a different frame for a different reason) and 20
        /// of the 35 machines kept their raw file origin.
        ///
        /// Y IS NOT TOUCHED HERE. The JS centres all three axes and then
        /// immediately overwrites Y at :1563 (`group.position.y -= drop`), so
        /// the Y term is dead on arrival; <see cref="SeatTheBelly"/> is that
        /// line and owns Y alone. Narrowing this to X/Z keeps the two
        /// seatings independent and keeps SeatTheBelly's own measurement (in
        /// `frame`, after this) unaffected — a shift in X/Z cannot move
        /// `wbox.min.y`.
        ///
        /// WHY THE THREE HAND-MEASURED CONSUMERS WANTED THIS, not the
        /// opposite (the old comment held the change back for them):
        ///   * Combat/GunPod.MuzzleLocal (0, -0.95, +3.65) and PodLocalPos
        ///     (0, -0.95, +1.55) are src/guns.js:409/417's own constants, and
        ///     the JS measured them on `game.plane` — the wrapper, i.e. AFTER
        ///     this recentre. Leaving the port's origin at the file's own
        ///     pivot is what made those two numbers mean something else here.
        ///   * `plane_shell` (PlaneCockpitKit.BuildPlaneShell, planes.js:1418)
        ///     splits fuselage from wing on `Mathf.Abs(p.x) <= fw` — it
        ///     ASSUMES the centreline is x = 0. On a model stored off-centre
        ///     it put the "fuselage" slab on one wing.
        ///   * PlaneHallFill's packing places each machine at
        ///     `xPartial + sgn * Span/2` (src/planehall.js:278), which assumes
        ///     the origin is the span's midpoint. Off-centre origins walked
        ///     the parked rows sideways into the lane.
        /// All three are geometry-derived or JS-frame constants, so all three
        /// get MORE correct here, and none of them needed a new number.
        ///
        /// It is safe for the propeller hub HINTS the `fix` branch above warns
        /// about at length, for one reason: this moves `file.localPosition`
        /// and never the model, so `model_file`'s OWN local space — the frame
        /// Flight/PlaneProps searches and the frame cessna310's `propHubs` and
        /// the P-51's `propMake.hub` are authored in — is bit-for-bit
        /// unchanged. PROPSUM stays 31/31.
        ///
        /// Prints AIRFRAME_RECENTRE per airframe so the shift is auditable in
        /// every bake and play-mode log: `dx`/`dz` as applied (in
        /// `model_scale`), and `dxRoot`/`dzRoot` the same shift seen from the
        /// flight root, where the frame's 180 deg yaw negates both.
        /// </summary>
        static void RecentreXZ(Transform scale, Transform file, Transform model, PlaneDef def)
        {
            if (scale == null || file == null || model == null) return;
            // Measured in `scale`, which is the JS's `group`: it includes
            // `file`'s rotation and the offset the fix branch just wrote, and
            // excludes `scale`'s own localScale (a node's localScale does not
            // appear in its own local space), exactly as the JS's box at :1533
            // is taken before `group.scale.setScalar(def.scale)` has anything
            // to say about it. Vertex-precise — the JS's setFromObject(..,true).
            var box = PlaneCockpitMeasurement.BoxOf(model, scale);
            float dx = -box.center.x, dz = -box.center.z;
            var p = file.localPosition;
            p.x += dx;
            p.z += dz;
            file.localPosition = p;
            Debug.Log($"AIRFRAME_RECENTRE id={(def != null ? def.Id : "?")} " +
                      $"dx={dx:F4} dz={dz:F4} dxRoot={-dx:F4} dzRoot={-dz:F4}");
        }

        static void SeatTheBelly(Transform frame, Transform scale, Transform model)
        {
            if (frame == null || scale == null || model == null) return;
            // scale.localPosition is zeroed above before this runs, so the box
            // is the untranslated airframe — measured in `frame`, the JS's own
            // wrapper space, so it includes def.Scale exactly as `wbox` does.
            var wbox = PlaneCockpitMeasurement.BoxOf(model, frame);
            float drop = wbox.min.y - BellyLocalY;      // == wbox.min.y + 1.7
            var p = scale.localPosition;
            p.y -= drop;
            scale.localPosition = p;
        }

        /// <summary>
        /// Make sure `planeRoot` has the chain, adopting whatever model
        /// children it already carries if it does not. This is the
        /// Enter-takes-a-plane / already-built-by-something-else path: a hall
        /// machine comes with the chain already (PlaneHallFill builds it), and
        /// a plane assembled by any other route gets straightened here instead
        /// of flying tail-first.
        ///
        /// Children the game attaches to the FLIGHT root rather than to the
        /// model — the belly gun pod, the box shell, the cockpit kit, a
        /// propeller pivot — are left where they are: they are already in the
        /// frame their own authors meant.
        /// </summary>
        public static Frames Ensure(Transform planeRoot, PlaneDef def)
        {
            if (planeRoot == null) return default;
            var f = Of(planeRoot);
            if (f.Valid) return f;

            Transform adopt = null;
            for (int i = 0; i < planeRoot.childCount; i++)
            {
                var c = planeRoot.GetChild(i);
                if (c.name == FrameName || c.name == "gun_pod" || c.name == "plane_shell"
                    || c.name == "cockpit_kit" || c.name == "cockpit_interior") continue;
                if (c.GetComponentInChildren<Renderer>(true) == null) continue;
                adopt = c;
                break;
            }
            return Attach(planeRoot, adopt != null ? adopt.gameObject : null, def);
        }
    }
}

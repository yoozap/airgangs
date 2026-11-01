using System;
using System.Collections.Generic;
using System.Linq;
using TbilisiFly.Data;
using TbilisiFly.Flight;
using TbilisiFly.Tools;
using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.Props
{
    // =========================================================================
    // Port of src/crane.js's GLB-load/traverse/position/collider ORCHESTRATION
    // — buildWarehouseCrane (:161-317), hangPlane (:318-479), standFans
    // (:480-589) and wireUp (:590-993) — the glue WarehouseCrane.cs/
    // CraneHookFinder.cs/PlaneSling.cs/WindFlowMesh.cs/WindTunnel.cs/
    // WindRider.cs explicitly left unported, each saying so in its own
    // header ("needs a loaded scene this port is not allowed to build").
    // This file is that scene: a real crane, a real slung P-51, three real
    // fans, real strop/cable geometry, all instantiated, measured, positioned
    // and collider-baked, exactly like the established siblings in this same
    // part of the port (Flight/ShedFill.cs, Flight/PlaneHallFill.cs) — same
    // EDITOR-TIME BAKE shape (AssetDatabase/PrefabUtility, #if UNITY_EDITOR,
    // "world == local while unparented/at identity" measurement trick), same
    // reason (this project currently only ever runs inside the Editor —
    // City/CityRenderer.cs's own header says so for the identical reason).
    //
    // CALL SITE: nothing calls this yet. CORRECTION (2026-10-27 audit): an
    // earlier version of this note pointed at
    // unity/Assets/Editor/BuildPlaneHallScene.cs's `ShedFill.BuildWarehouse(...)`
    // call — that is WRONG. BuildPlaneHallScene.cs bakes to
    // "Assets/Scenes/PlaneHall.unity", a scene that is not one of the four
    // committed scenes and is never loaded by anything at runtime, so wiring
    // in there would be just as unreachable as not wiring in at all (same
    // class of bug as the next paragraph). The scene that actually boots the
    // game is Assets/Scenes/Game.unity, built by
    // unity/Assets/Editor/BuildPlayableScene.cs — which does not call
    // ShedFill.BuildWarehouse/BuildPlaneHall or this file AT ALL today. The
    // fix is: in BuildPlayableScene.Run(), after `city`/`bRoot` exist, build
    // `meta`/`heightAtGame` the same way BuildPlaneHallScene.cs:46-51 does,
    // then `var wh = ShedFill.BuildWarehouse(bRoot, meta, heightAtGame);
    // WarehouseCraneFill.Build(wh);` (BuildPlayableScene.cs is outside this
    // pass's ownership; see the port report for the exact snippet). Once built, the
    // ONGOING per-frame physics (wind step, fan spin, strop re-aim, the hero
    // riding the wing) is driven by <see cref="WarehouseFanSwitch"/> and
    // <see cref="WarehouseCraneSlingRig"/>, both MonoBehaviours this bake
    // attaches to the baked objects — they need no further scene wiring,
    // exactly like PlaneHallFill.Fill attaching PlaneHallVisibility to the
    // hall root.
    //
    // NOT PORTED, same boundary as every sibling bake file in this set:
    //   * the #fan-ctl DOM control card (crane.js:758-865) — a UI/Hud concern
    //     (agent 2's territory, not this pass's to touch). WarehouseFanSwitch
    //     exposes everything such a card needs (NearSwitch, Mode, Press()) as
    //     a plain public surface, the same seam FanLane-style cards use.
    //   * fan audio (MachineVoice.FanVoice(), Core/AudioFx.cs) — already a
    //     separate, already-ported file; WarehouseFanSwitch calls its public
    //     Update(dt, ...) the same way crane.js's own propUpdater does.
    //   * propeller windmilling on the hung Mustang itself
    //     (spinPropellers(plane, PROP_RPM*p, dt), crane.js:1928) — NOW WIRED
    //     (this pass): HangPlane's own BuildPropellers (below) runs
    //     PlaneCatalog's p51 PropMake entry ("no propeller in the file at
    //     all; build one where the spinner is") through Flight/
    //     PropellerSystem.cs's BuildBladesMesh/FinishPropeller/
    //     BladesExcludingDisc — the SAME pipeline a real gameplay P-51 would
    //     need and, per a grep of the whole project, the first thing in this
    //     codebase that actually calls it outside that file's own probes
    //     (PlaneHallFill.cs's 35 OTHER parked airframes still do not — out
    //     of this pass's scope, left as a documented gap for whichever pass
    //     wires the flyable fleet). WarehouseFanSwitch.Update then drives it
    //     every frame with PropellerSystem.SpinPropellers(Propellers,
    //     propRpm, dt), reusing WindTunnel.Step's existing propRpm output
    //     rather than a second spinner (the task's own instruction).
    //   * the authored hook position (crane.js:184-187,464 `one["hook"]`,
    //     written into the GLB's own node extras by
    //     tools/build_crane_machine.py). FBX (what this project actually
    //     imports, crane_machine.fbx) has no equivalent of glTF node extras
    //     — `strings` on the asset confirms no "hook"/"block" node name
    //     survived either — so CraneHookFinder.Compute's `toldHook` has
    //     nothing to be handed here BY THE ASSET. `HookOverrideLocal` below
    //     (crane.js's own `told` parameter, Unity-side) is now SET, derived
    //     rather than guessed — chain of custody, fully reproducible:
    //       1. public/models/crane_machine.glb's root node carries
    //          extras.hook = [-12.499158, 4.331408, -0.145040] (read
    //          directly out of the GLB's JSON chunk — this is literally
    //          tools/build_crane_machine.py's own BLOCK point, written at
    //          :464, i.e. the MAIN BLOCK the game comment warns the
    //          geometric fallback misses on this machine, not the headache
    //          ball).
    //       2. crane_machine.fbx (what Unity imports) is the SAME mesh,
    //          verified by loading both headless in Blender and comparing
    //          bounding boxes/vertex counts: identical to 4 decimal places,
    //          131897 verts both sides — the GLB->FBX round trip
    //          (tools/glb_batch_fbx.py, axis_forward=-Z/axis_up=Y) is the
    //          identity on these axes, not a re-derivation.
    //       3. Unity's own FBX import mirrors X at import time (rule (a)
    //          below, same as glTFast) — so the Unity-space hook is
    //          (-gltf.x, gltf.y, gltf.z) = (12.499158, 4.331408, -0.145040).
    //       4. PROVEN, not assumed: WarehouseCraneFillProbe.HookDerivation
    //          measures the real loaded crane_machine.fbx's own vertices
    //          (at its PRESERVED natural import transform — see
    //          BuildCraneShell's own comment on that, a real bug this same
    //          pass found and fixed) and finds a surface vertex 2.2 cm from
    //          this exact point (HOOKDERIVE_NEAREST dist=0.0220); the
    //          UN-mirrored candidate misses by 8.02 m (HOOKDERIVE_NEAREST_
    //          UNMIRRORED), so the mirror direction is not a guess either.
    //     Leaving this at Vector3.zero would keep taking the GEOMETRIC
    //     fallback (crane.js's own comment: finds the headache ball, not
    //     the main block, on this machine) — now bypassed.
    //
    // ============================== HANDEDNESS ==============================
    // ONE RULE, applied once and then never re-litigated per call:
    //   (a) Anything measured off a REAL imported mesh (vertex positions,
    //       Renderer.bounds, CraneHookFinder's Hook/FeetCenter, the model-
    //       local Plug constant) needs NO mirroring at all — ShedFill.cs's
    //       own header point 1: an imported FBX's own geometry is already in
    //       Unity's correct convention, and a difference/offset of two such
    //       measurements (e.g. hookLocalCrane, the fan's centring offset) is
    //       likewise already correct, verbatim, including its sign.
    //   (b) Anything that is instead a HAND-AUTHORED game-frame CONSTANT
    //       transcribed from crane.js (At, Yaw, PlaneYaw, BoardAtX) does NOT
    //       self-correct and must be converted exactly once, at the point it
    //       is first turned into a position/rotation: negate its X component
    //       (a position) or run it through GeoFrame.YawToUnity (a yaw).
    //   (c) Once (a) and (b) are applied, EVERY downstream value in this
    //       file — fed through real Unity Transform.TransformPoint/
    //       InverseTransformPoint, or through WarehouseCrane.Compute/
    //       CableRoutes/CableFloor/ComputeSwitchboardLayout (all four of
    //       which this pass now treats as "Unity-space in, Unity-space out",
    //       verified below) — is ALREADY Unity-correct and gets used AS IS.
    //       No value in this file is mirrored twice, and nothing is
    //       converted back to JS sign and then mirrored again.
    //
    // WORKED CHECK (WarehouseCrane.Compute, the one real cross-product-
    // shaped term in this whole file): using tools/_probe_crane.mjs's own
    // ground truth (hook=(-0.1,7.9,31.35), aim=hook+(0,-0.3,0), Yaw=-pi/2,
    // PlaneYaw=3pi/2-Yaw) — feeding Compute the MIRRORED hook/aim
    // ((0.1,7.9,31.35) etc, i.e. rule (a)/(c): real Unity-space points,
    // never re-mirrored) through the method's own now-negated internal yaw
    // reproduces the mirror of every one of the three fans' JS positions
    // exactly (fan 0: JS (9.9,7.9,24.35) -> Unity (-9.9,7.9,24.35), checked
    // by hand for all 3 indices) with NO further negation needed on
    // Compute's OUTPUT either. An earlier draft of this file mirrored BOTH
    // the input AND the output, which is wrong (verified numerically: it
    // does not cancel back to the right answer) — fixed before this pass
    // ends, not left in.
    // =========================================================================
    public static class WarehouseCraneFill
    {
        public const string CraneModelPath = "Assets/Resources/Art/Props/crane_machine.fbx";
        public const string FanModelPath = "Assets/Resources/Art/Props/fan.fbx";
        public const string PlaneModelPath = PlaneHallFill.PlanesFbxDir + "p51.fbx";

        /// <summary>crane.js's `told` (:184-187) — see class header's
        /// NOT-PORTED note for the full derivation. The main block's own
        /// position, measured off the real mesh (public/models/
        /// crane_machine.glb's extras.hook, Unity-mirrored on X), NOT a
        /// guess — proven to land 2.2 cm from a real surface vertex of the
        /// loaded crane_machine.fbx by WarehouseCraneFillProbe.
        /// HookDerivation. Vector3.zero would mean "not authored" (matching
        /// the JS's own `told = null` default, CraneHookFinder.Compute
        /// falling back to its geometric search) — no longer the case.</summary>
        public static Vector3 HookOverrideLocal = new Vector3(12.499158f, 4.331408f, -0.145040f);

        public struct Result
        {
            public bool Built;
            public GameObject CraneRoot;
            public GameObject SwingPivot;
            public GameObject PlaneGo;
            public GameObject[] FanHolders;
            public GameObject BoardGo;
            public WarehouseFanSwitch Switch;
            public WarehouseCraneSlingRig Slings;
            // Diagnostics for a probe, same convention as ShedFill.ShedBuildResult.
            public float ScaleApplied;
            public Vector3 HookLocalInCraneUnity; // crane.js's own hookAt, in THIS file's Unity-space convention
            public float WallAtUnity;
            public int CablePolylinePoints;
        }

        /// <summary>crane.js's buildWarehouseCrane+hangPlane+standFans+wireUp,
        /// called once per shed. `warehouse` is a prior
        /// ShedFill.BuildWarehouse(...) result — same contract as
        /// PlaneHallFill.Fill(ShedFill.ShedBuildResult).</summary>
        public static Result Build(ShedFill.ShedBuildResult warehouse)
        {
            if (warehouse.Root == null) return default;
            Transform shedRoot = warehouse.Root.transform;

            var (craneGo, scale, hookLocalCrane) = BuildCraneShell(shedRoot);
            if (craneGo == null) return default;

            var (swingGo, planeGo, ownBox, feetPlaneLocal, skinColliders, planeLocalY, propellers) =
                HangPlane(craneGo.transform, hookLocalCrane);
            if (planeGo == null)
                Debug.LogWarning("WarehouseCraneFill: no aeroplane on the hook — " + PlaneModelPath + " missing/empty.");

            // crane.js:467-468 `mid` — the middle of the hanging airframe, in
            // the CRANE group's own frame (FanLayout.Compute's `aim`).
            Vector3 midCraneLocal = planeGo != null
                ? PlaneSling.MidAirframe(hookLocalCrane, planeLocalY, ownBox)
                : hookLocalCrane;

            var (fanHolders, fanRotors, plugsShedLocal) =
                StandFans(shedRoot, craneGo.transform, hookLocalCrane, midCraneLocal);

            var (boardGo, switchComp, polylinePoints, wallAtUnity) =
                WireUp(shedRoot, swingGo?.transform, planeGo?.transform,
                       plugsShedLocal, fanRotors, ownBox, planeLocalY, skinColliders);

            WarehouseCraneSlingRig slingRig = null;
            if (swingGo != null && planeGo != null)
                slingRig = BuildSlingRig(craneGo.transform, swingGo.transform, planeGo.transform, feetPlaneLocal);

            craneGo.AddComponent<DevPlacementTag>().id = "warehouse_crane";
            if (switchComp != null) switchComp.Propellers = propellers;

            Debug.Log($"WAREHOUSECRANEFILL crane={CraneModelPath.Split('/').Last()} scale={scale:F2} " +
                      $"hook=({hookLocalCrane.x:F2},{hookLocalCrane.y:F2},{hookLocalCrane.z:F2}) " +
                      $"fans={fanHolders.Length} cablePts={polylinePoints} wallAt={wallAtUnity:F2}");

            return new Result
            {
                Built = true, CraneRoot = craneGo, SwingPivot = swingGo, PlaneGo = planeGo,
                FanHolders = fanHolders, BoardGo = boardGo, Switch = switchComp, Slings = slingRig,
                ScaleApplied = scale, HookLocalInCraneUnity = hookLocalCrane,
                WallAtUnity = wallAtUnity, CablePolylinePoints = polylinePoints,
            };
        }

        // =====================================================================
        // ---- buildWarehouseCrane: crane.js:161-317 -------------------------
        // =====================================================================
        static (GameObject craneGo, float scale, Vector3 hookLocalCrane) BuildCraneShell(Transform shedRoot)
        {
            var prefab = ArtAssets.Load<GameObject>(CraneModelPath);
            if (prefab == null)
            {
                Debug.LogWarning($"WarehouseCraneFill: missing {CraneModelPath}");
                return (null, 1f, Vector3.zero);
            }

            // Unparented, AT ITS OWN ORIGIN — "world == local" (ShedFill.Build's
            // own established trick) so the measured box/vertices are the
            // model's own space, crane.js:162's `inner.updateMatrixWorld` +
            // Box3.setFromObject. UNLIKE ShedFill/PlaneHallFill's own models,
            // crane_machine.fbx imports with a NON-identity natural root
            // transform — position (0,0,0) but rotation (0,180,180) and
            // scale (100,100,100) (confirmed via WarehouseCraneFillProbe.
            // HookDerivation: fan.fbx has the identical natural transform;
            // p51.fbx does not, rot/scale already (0,1)). That natural
            // transform is Unity's OWN FBX-import correction for how these
            // two particular assets were exported (Blender's glb_batch_fbx.py
            // round trip) — resetting it to identity/one, as an earlier draft
            // did, silently measured the mesh at 1/100 of its real size and
            // with the wrong facing (HOOKDERIVE_BOX came back ~18cm across
            // for a machine documented at ~18.5 m). So: keep position at the
            // origin, but PRESERVE the natural rotation/scale for the
            // measurement AND for the final placement below (`naturalRot`/
            // `naturalScale`) rather than discarding them.
            var inner = (GameObject)ArtAssets.Instantiate(prefab);
            inner.name = "inner";
            inner.transform.position = Vector3.zero;
            Quaternion naturalRot = inner.transform.localRotation;
            Vector3 naturalScale = inner.transform.localScale;

            var renderers = inner.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                Debug.LogWarning($"WarehouseCraneFill: {CraneModelPath} instantiated with no renderers");
                UnityEngine.Object.DestroyImmediate(inner);
                return (null, 1f, Vector3.zero);
            }
            Bounds box = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) box.Encapsulate(renderers[i].bounds);
            float h = box.size.y;
            float scale = WarehouseCrane.ScaleToFit(h); // crane.js:167

            // crane.js:175-199 — every mesh's vertices in the model's own
            // world space (== local space here, see above). Rule (a): no
            // mirroring — this is raw imported-mesh data.
            var verts = new List<Vector3>();
            foreach (var mf in inner.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                var local = mesh.vertices;
                var m = mf.transform.localToWorldMatrix;
                for (int i = 0; i < local.Length; i++) verts.Add(m.MultiplyPoint3x4(local[i]));
            }

            Vector3? told = HookOverrideLocal != Vector3.zero ? HookOverrideLocal : (Vector3?)null;
            var found = CraneHookFinder.Compute(verts, box.min, box.max, told);

            // crane.js:220-227 — the crane GROUP (Yaw about the shed). `At`/
            // `Yaw` are hand-authored game-frame constants — rule (b): mirror
            // the position's X, run the yaw through GeoFrame.YawToUnity.
            var craneGo = new GameObject("warehouse_crane");
            craneGo.transform.SetParent(shedRoot, false);
            craneGo.transform.localPosition = new Vector3(-WarehouseCrane.At.x, WarehouseCrane.At.y, WarehouseCrane.At.z);
            craneGo.transform.localRotation = Quaternion.Euler(0f, GeoFrame.YawToUnity(WarehouseCrane.Yaw) * Mathf.Rad2Deg, 0f);

            inner.transform.SetParent(craneGo.transform, false);
            // crane.js:222-223 `inner.position.set(-c.x*s, -box.min.y*s, -c.z*s)`
            // — centring the raw model on its own measured footprint. `c`/
            // `box` are rule-(a) measured quantities; "-c.x" here is the
            // SAME kind of footprint-recentring ShedFill.Build's own
            // `innerGo.transform.localPosition = (-c.x*scale.x, ...)` does,
            // not a GeoFrame mirror (there is nothing game-frame-authored in
            // this offset) — it just happens to use the same minus sign.
            inner.transform.localPosition = new Vector3(
                -found.FeetCenter.x * scale, -box.min.y * scale, -found.FeetCenter.z * scale);
            // PRESERVE the natural import rotation/scale (see this method's
            // opening comment) — `scale` here is only the cap-not-size
            // ScaleToFit ratio (1 unless the real model exceeds the shed's
            // arch), composed WITH the natural (100,100,100)/(:,180,180)
            // import correction, not instead of it.
            inner.transform.localRotation = naturalRot;
            inner.transform.localScale = naturalScale * scale;

            foreach (var r in renderers)
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; // crane.js:230 — nothing in a roofed shed casts a shadow worth the draw
                r.receiveShadows = true;
            }
            // crane.js:243-277 — the collider. Plain Unity MeshColliders, one
            // per mesh (same simplification ShedFill.cs's own header
            // documents and uses for the shed shell itself — "plain Unity
            // Colliders ARE the collision registry in this port").
            foreach (var mf in inner.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
            }

            // crane.js:240-241 `hookAt` — in the CRANE GROUP's own local
            // frame (WarehouseCrane.HookLocalInCrane, already ported pure
            // math; this is its first real caller). Rule (a): a difference
            // of two measured quantities, no mirroring.
            Vector3 hookLocalCrane = WarehouseCrane.HookLocalInCrane(found.Hook, found.FeetCenter, box.min.y, scale);

            return (craneGo, scale, hookLocalCrane);
        }

        // =====================================================================
        // ---- hangPlane: crane.js:318-474 -----------------------------------
        // =====================================================================
        static (GameObject swingGo, GameObject planeGo, WindRider.PlaneBox ownBox,
                Vector3[] feetPlaneLocal, Collider[] skinColliders, float planeLocalY,
                List<PropellerSystem.PropellerInstance> propellers)
            HangPlane(Transform craneRoot, Vector3 hookLocalCrane)
        {
            var prefab = ArtAssets.Load<GameObject>(PlaneModelPath);
            if (prefab == null)
            {
                Debug.LogWarning($"WarehouseCraneFill: missing {PlaneModelPath}");
                return (null, null, default, Array.Empty<Vector3>(), Array.Empty<Collider>(), 0f, new List<PropellerSystem.PropellerInstance>());
            }

            // crane.js:327 `own = Box3.setFromObject(plane,true)` taken BEFORE
            // the wrapper is rotated/parented — same unparented-at-identity
            // trick as BuildCraneShell/PlaneHallFill.
            var planeGo = (GameObject)ArtAssets.Instantiate(prefab);
            planeGo.name = "p51";
            // src/crane.js:360 `plane.userData.noBoard = true` — "the hero cannot
            // climb onto a wing root four metres over his head". Character/
            // FootBoarding.cs:422 already tests for this component on the
            // damage record's wrapper and this class's own `nose` doc already
            // CLAIMED the marker was here; nothing actually attached it
            // (Character/NoBoardMarker read DEAD once it was un-nested into its
            // own file, 2026-10-31). It is attached here, on the same object the
            // JS tags — AND on the same object that now carries the
            // PlaneDamageRecord (below), which is what makes it bite:
            // Character/FootBoarding.NearestPlane() walks the target registry
            // and skips any entry whose Wrapper carries this component, so the
            // slung machine is a target and a damage subject and still refuses
            // Enter.
            planeGo.AddComponent<Character.NoBoardMarker>();
            planeGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var def = Planes.PlaneById(WarehouseCrane.PlaneId);
            planeGo.transform.localScale = def != null && def.Scale > 0f ? Vector3.one * def.Scale : Vector3.one;

            var renderers = planeGo.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                Debug.LogWarning($"WarehouseCraneFill: {PlaneModelPath} instantiated with no renderers");
                UnityEngine.Object.DestroyImmediate(planeGo);
                return (null, null, default, Array.Empty<Vector3>(), Array.Empty<Collider>(), 0f, new List<PropellerSystem.PropellerInstance>());
            }
            Bounds ownB = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) ownB.Encapsulate(renderers[i].bounds);
            // NOTE (see class header's NOT-PORTED list): this measures the
            // RAW model directly, like PlaneHallFill does for every parked
            // plane, rather than reproducing makeGameplayPlane's WHEEL_HEIGHT
            // wrapper offset — a documented, bounded simplification.
            var ownBox = new WindRider.PlaneBox
            {
                MinX = ownB.min.x, MaxX = ownB.max.x, MinY = ownB.min.y, MaxY = ownB.max.y,
                MinZ = ownB.min.z, MaxZ = ownB.max.z,
            };

            // crane.js:337-345 — the swing pivot, AT THE HOOK, in the crane
            // group's own frame. Rule (a): hookLocalCrane is already correct.
            var swingGo = new GameObject("crane_swing");
            swingGo.transform.SetParent(craneRoot, false);
            swingGo.transform.localPosition = hookLocalCrane;

            float planeLocalY = -(WarehouseCrane.Sling + ownBox.MaxY);
            planeGo.transform.SetParent(swingGo.transform, false);
            planeGo.transform.localPosition = new Vector3(0f, planeLocalY, 0f);
            // PlaneYaw is hand-authored (derived from Yaw) — rule (b).
            planeGo.transform.localRotation = Quaternion.Euler(0f, GeoFrame.YawToUnity(WarehouseCrane.PlaneYaw) * Mathf.Rad2Deg, 0f);
            foreach (var r in renderers) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // crane.js:348-361 — SOLID AND SHOOTABLE. Plain BoxCollider off
            // the measured bounds, same simplification PlaneHallFill.cs's
            // own header documents for every other parked airframe in this
            // port.
            var box = planeGo.AddComponent<BoxCollider>();
            box.center = ownB.center;
            box.size = ownB.size;

            // src/crane.js:356-361 `citymap.targets?.register(plane, def, shell)`
            // — "Click it or shoot it and it is an aeroplane with a card and
            // hull points, like every other parked machine." NOW WIRED
            // (docs/open_items.json `crane-plane-no-record`, 2026-10-31).
            //
            // The Unity port's target registry is not registered INTO, it is
            // DISCOVERED: Core/CombatWorldAdapter.Install() sweeps
            // `FindObjectsByType<PlaneDamageRecord>()` exactly once and turns
            // every hit into BOTH a PlaneDamage.Watch() subject and a
            // GameLoop.Targets entry (Tab/click selection, the target card, the
            // selection ring, hull points). So the whole of crane.js:361 is
            // this component, attached to the same GameObject the JS hands to
            // `register` — see Flight/PlaneHallFill.cs:338-355, which is the
            // identical block for the 35 parked machines and the pattern this
            // copies field for field. Reached in time: Build() runs from
            // Flight/ShedSceneFill.OnEnable and Install() from
            // Core/PlayableSceneBoot.Start(), and Unity delivers every OnEnable
            // in a loading scene before any Start.
            //
            // THE ONE FRAME DIFFERENCE FROM A PARKED MACHINE, and it is the
            // whole of it: the hall's airframes go through
            // Flight/PlaneModelFrame (nose straightened onto +Z), so
            // PlaneHallFill reads noseLen as `+max.z`. This one is deliberately
            // NOT in that chain — it is cargo, its local -Z IS the model's nose
            // (see BuildPropellers' header, "STILL -forward AFTER THE
            // 2026-10-30 ORIENTATION PASS, ON PURPOSE"), so src/planes.js:1587's
            // `noseLen = -wbox.min.z` is transcribed here VERBATIM, sign and
            // all. Getting that backwards is exactly the bug PlaneHallFill's
            // own comment records on the other 35.
            if (def != null)
            {
                var rec = planeGo.AddComponent<TbilisiFly.Combat.PlaneDamageRecord>();
                rec.HalfSpan = ownBox.Span * 0.5f;                       // planes.js:1574
                rec.Size = Mathf.Max(ownBox.Span, ownBox.Len);           // planes.js:1575
                rec.NoseLen = Mathf.Max(1f, -ownB.min.z);                // planes.js:1587, -Z nose
                rec.MaxHullPoints = Planes.PlaneStamina(def);            // target.js:69
                rec.HullPoints = rec.MaxHullPoints;
                rec.DisplayName = def.Name;                              // planedamage.js:1341
                rec.IsPlayerOwn = false;
                rec.CockpitKit = null;   // crane.js loads no cockpit kit on the cargo
                Debug.Log($"CRANEFILL_TARGET id={planeGo.name} record=1 hp={rec.MaxHullPoints:F0} " +
                          $"halfSpan={rec.HalfSpan:F3} size={rec.Size:F3} noseLen={rec.NoseLen:F3} " +
                          $"noBoard={(planeGo.GetComponent<Character.NoBoardMarker>() != null).ToString().ToLowerInvariant()} " +
                          $"name=\"{rec.DisplayName}\"");
            }

            // crane.js:390-421 — the four strop feet, found by a real ray
            // against the aeroplane's own skin (not the box above — a wing
            // is nowhere near its own bounding box). MeshColliders on every
            // visible mesh double as (a) this bake's own raycast target and
            // (b) WindRider's runtime SkinAt delegate (see WarehouseFanSwitch).
            // NOTE: MeshCollider.sharedMesh does not require Mesh.isReadable
            // (that flag only gates script-side vertex/triangle access —
            // PhysX bakes its own collision data internally regardless), so
            // this does not guard on it; an earlier draft did and silently
            // produced ZERO skin colliders against p51.fbx's own import
            // settings, which would have made every strop fall back to its
            // hard-coded point AND permanently broken WindRider's SkinAt —
            // caught by this file's own probe (CRANEFILL_SWITCH skinCols=0),
            // not by review.
            var skinColliders = new List<Collider>();
            foreach (var mf in planeGo.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                skinColliders.Add(mc);
            }

            Func<float, float, Vector3?> underRay = (x, z) => RayUnderSkin(planeGo.transform, skinColliders, ownBox.MaxY, x, z);
            Vector3[] feet = PlaneSling.FindFeet(ownBox.Span, ownBox.Len, ownBox.MaxY, underRay);

            // crane.js:1928 `spinPropellers(plane, PROP_RPM*p, dt)` — see
            // class header's NOW-WIRED note.
            var propellers = BuildPropellers(planeGo, def);

            return (swingGo, planeGo, ownBox, feet, skinColliders.ToArray(), planeLocalY, propellers);
        }

        /// <summary>src/propeller.js's own build-from-scratch path
        /// (:658-709 buildBlades), run for real against the hung P-51 —
        /// PlaneCatalog's p51 entry carries PropMake ("no propeller in the
        /// file at all; build one where the spinner is"), so this is the
        /// SAME path a flyable gameplay P-51 would need; nothing about this
        /// instantiation is special-cased for the crane scene.
        ///
        /// ===== THE HUB WAS 4.95 m BEHIND THE COCKPIT. FIXED 2026-10-30 =====
        /// This method used to say: "`plane.forward` (local +Z) = nose
        /// (Flight/FlightModel.cs :190/:301), and PropMake.Hub's `f`
        /// (nose-axis) component is positive, i.e. authored on that same
        /// +Z-is-nose convention — so no extra rotation is needed on the
        /// pivot, only a translation to Hub." Both halves of that are wrong,
        /// and they are wrong for the same reason.
        ///
        ///   1. FlightModel.cs's "+Z is the nose" is about the FLIGHT frame
        ///      — the Transform it thrusts. It is NOT a statement about the
        ///      art, and all 35 shipped airframes contradict it: the FBX
        ///      bridge (tools/glb_batch_fbx.py) mirrors X, not Z, so a file
        ///      arrives with its nose exactly where the GLB had it, along
        ///      **-Z**. Measured on all 35 at their natural import transform
        ///      by Editor/Probes/PlanePropsProbe.Axis, from the horizontal
        ///      stabiliser (the widest end slab is the tail); see
        ///      Flight/FlightModel.cs's second handedness block.
        ///   2. PropMake.Hub is NOT a raw axis triple. src/propeller.js:918-919
        ///      reads it as SCALARS along the measured basis —
        ///      `hub = r*m.hub[0] + u*m.hub[1] + f*m.hub[2]` — and
        ///      PropellerSystem's own hint path does exactly that through
        ///      FromRuf. Assigning the triple straight into localPosition
        ///      only happens to agree when the basis is the identity.
        ///
        /// With `f` = +Z and the triple assigned raw, the P-51's authored
        /// (0, 1.57, 4.95) landed at local (0, 1.57, +4.95) — 4.95 m aft of
        /// the spinner, i.e. just behind the cockpit, with the blades facing
        /// backwards and FinishPropeller measuring its spin axis off the
        /// wrong end. Now: the nose is `-planeGo.transform.forward` (the
        /// plane holder carries only placement, so its local -Z IS the
        /// model's nose) and the hub is composed along that basis, which puts
        /// the pivot at local (0, 1.57, -4.95) — on the spinner.
        ///
        /// ===== STILL -forward AFTER THE 2026-10-30 ORIENTATION PASS, ON PURPOSE =====
        /// Flight/PlaneModelFrame now straightens every FLYABLE aeroplane onto
        /// FlightModel's +Z and the player's own Mustang's hub reads
        /// (0, 1.57, +4.95). The crane's airframe is deliberately NOT put
        /// through that chain and `nose` here stays `-planeGo.transform.forward`:
        ///   * it has no PlaneController and no flight frame. It is cargo. The
        ///     only frame it has is the MODEL frame, exactly like src/crane.js,
        ///     whose own `PLANE_YAW` constant (WarehouseCrane.PlaneYaw) is
        ///     hand-authored against a -Z nose — straightening the model
        ///     without re-deriving that constant would swing the slung
        ///     aeroplane 180 degrees inside the warehouse and point it out of
        ///     the fan stream.
        ///   * it carries a Character/NoBoardMarker ("no wing root
        ///     to step onto"), so Enter can never take it and
        ///     Core/GameLoop.SwapToPlane can never put a flight frame on it.
        /// So: if a future pass ever makes the slung machine flyable, it must
        /// move to PlaneModelFrame.Attach AND flip this `nose` to +forward in
        /// the same change. Proof the current state is right:
        /// WarehouseCraneFillProbe's CRANEFILL_PROPNOSE onNose=1.</summary>
        static List<PropellerSystem.PropellerInstance> BuildPropellers(GameObject planeGo, PlaneDef def)
        {
            var list = new List<PropellerSystem.PropellerInstance>();
            if (planeGo == null || def?.PropMake == null) return list;
            var make = def.PropMake;
            int blades = make.Blades > 0 ? make.Blades : 4;

            var pivotGo = new GameObject("propeller_0");
            pivotGo.transform.SetParent(planeGo.transform, false);
            // src/propeller.js:918-919 — make.Hub is (r, u, f) SCALARS along
            // the nose basis, model-local metres, not an (x, y, z) position.
            // Composed exactly as the JS composes it (and as
            // PropellerSystem's own FromRuf does for the hinted path). r
            // (Hub.x) is 0 for the P-51 (nose-mounted, on the centreline), so
            // rule (a) vs (b)'s mirror question stays moot here — negating 0
            // changes nothing — but `f` is 4.95 and its direction is the whole
            // bug this replaces.
            Vector3 nose = -planeGo.transform.forward;   // see this method's header
            var basis = PropellerSystem.BasisOf(planeGo.transform.InverseTransformDirection(nose));
            pivotGo.transform.localPosition =
                basis.R * make.Hub.x + basis.U * make.Hub.y + basis.F * make.Hub.z;
            pivotGo.transform.localRotation = Quaternion.identity;

            var bladesGo = new GameObject("propeller_blades");
            bladesGo.transform.SetParent(pivotGo.transform, false);
            var mf = bladesGo.AddComponent<MeshFilter>();
            mf.sharedMesh = PropellerSystem.BuildBladesMesh(make.Radius, blades);
            var bladeMat = SimpleMaterial(HexColor(0x15161a));
            bladeMat.name = "propeller_blade_mat";
            var bmr = bladesGo.AddComponent<MeshRenderer>();
            bmr.sharedMaterial = bladeMat;
            bmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // src/propeller.js:1048-1049 / src/planes.js:1593 — FinishPropeller
            // wants the nose as a WORLD direction, and `nose` above already is
            // one (the plane holder's -Z, in world terms).
            var inst = PropellerSystem.FinishPropeller(pivotGo.transform, nose, make.Radius);

            if (inst.HasDisc)
            {
                var discGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
                discGo.name = "propeller_disc";
                var dcol = discGo.GetComponent<Collider>();
                if (dcol != null) UnityEngine.Object.DestroyImmediate(dcol);
                discGo.transform.SetParent(pivotGo.transform, false);
                discGo.transform.localPosition = Vector3.zero;
                discGo.transform.localRotation = inst.DiscLocalRotation;
                // src/propeller.js:1061 `blurDisc(radius*1.02, blades)` sized
                // as a diameter (Unity's unit Quad is 1x1, so scale = diameter).
                discGo.transform.localScale = Vector3.one * (inst.Radius * 2f * 1.02f);
                var discShader = Shader.Find("TbilisiFly/PropDisc");
                var discMat = new Material(discShader) { name = "propeller_disc_mat", color = new Color(0f, 0f, 0f, 0f) };
                discMat.SetTexture("_MainTex", PropellerSystem.BuildBlurDiscTexture(blades));
                var dmr = discGo.GetComponent<Renderer>();
                dmr.sharedMaterial = discMat;
                dmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                dmr.enabled = false; // PropellerSystem.SpinPropellers toggles this off blur fraction
                inst.Disc = discGo.transform;
                inst.DiscMaterial = discMat;
            }
            inst.Blades = PropellerSystem.BladesExcludingDisc(pivotGo.transform, inst.Disc);
            // SpinPropellers' own doc comment: "caller is expected to have
            // already given this renderer its OWN material instance" — give
            // each blade renderer its own clone so fading this propeller's
            // blades never touches another's (not that there is another on
            // this single-engine airframe, but matching the contract exactly
            // costs nothing and avoids a silent shared-material bug if a
            // second propeller is ever added here).
            foreach (var r in inst.Blades)
                if (r != null) r.material = new Material(r.sharedMaterial);

            list.Add(inst);
            return list;
        }

        /// <summary>crane.js:394-403 `under(x,z)` — a ray down the plane's own
        /// +Y against its real skin colliders, returned in the plane's LOCAL
        /// space (null = nothing there, PlaneSling.FindFeet's own fallback
        /// then applies — crane.js:407-411's "a wing is narrower than the
        /// fuselage is long").</summary>
        static Vector3? RayUnderSkin(Transform plane, List<Collider> skin, float maxY, float x, float z)
        {
            Vector3 fromLocal = new Vector3(x, maxY + 1.5f, z);
            Vector3 toLocal = new Vector3(x, maxY + 0.5f, z);
            Vector3 from = plane.TransformPoint(fromLocal);
            Vector3 dir = (plane.TransformPoint(toLocal) - from).normalized;
            var hits = Physics.RaycastAll(from, dir, 60f);
            foreach (var h in hits.OrderBy(h => h.distance))
                if (skin.Contains(h.collider)) return plane.InverseTransformPoint(h.point);
            return null;
        }

        // =====================================================================
        // ---- standFans: crane.js:480-589 -----------------------------------
        // =====================================================================
        static (GameObject[] holders, Transform[] rotors, Vector3[] plugsShedLocal)
            StandFans(Transform shedRoot, Transform craneRoot, Vector3 hookLocalCrane, Vector3 aimCraneLocal)
        {
            var prefab = ArtAssets.Load<GameObject>(FanModelPath);
            if (prefab == null)
            {
                Debug.LogWarning($"WarehouseCraneFill: missing {FanModelPath}");
                return (Array.Empty<GameObject>(), Array.Empty<Transform>(), Array.Empty<Vector3>());
            }

            // Measure once, unparented AT ITS OWN ORIGIN (crane.js:482-492).
            // Rule (a): no mirroring for any of these measured quantities —
            // but, same finding as BuildCraneShell (see that method's opening
            // comment and WarehouseCraneFillProbe.HookDerivation's
            // HOOKDERIVE_OTHER line), fan.fbx ALSO imports with a natural
            // non-identity root rotation/scale (0,180,180)/(100,100,100), so
            // that natural transform is preserved here too rather than
            // zeroed — zeroing it previously still happened to land on the
            // right fan HEIGHT (the `s` ratio below self-corrects for any
            // uniform scale, since it is derived FROM the same broken
            // baseline it is then applied to), but it silently dropped the
            // 180/180 facing correction on every fan.
            var probe = (GameObject)ArtAssets.Instantiate(prefab);
            probe.transform.position = Vector3.zero;
            Quaternion fanNaturalRot = probe.transform.localRotation;
            Vector3 fanNaturalScale = probe.transform.localScale;
            var pr = probe.GetComponentsInChildren<Renderer>(true);
            Bounds b = pr.Length > 0 ? pr[0].bounds : new Bounds(Vector3.zero, Vector3.one);
            for (int i = 1; i < pr.Length; i++) b.Encapsulate(pr[i].bounds);
            float s = WarehouseCrane.FanHeight / Mathf.Max(0.001f, b.size.y);
            Vector3 c = b.center;
            Vector3 size = Vector3.Scale(b.size, Vector3.one * s);
            float hubY = (c.y - b.min.y) * s;
            UnityEngine.Object.DestroyImmediate(probe);

            // hookLocal/aim are in the CRANE group's local frame; converted
            // to the SHED's local frame for FanLayout.Compute (crane.js:480's
            // own doc comment: "Both points are in the SHED's frame") via the
            // real Transform hierarchy — rule (c): already Unity-correct,
            // used AS IS, no manual sign flip (see class header's worked
            // check — this is precisely the call that check verifies).
            Vector3 hookShedWorld = craneRoot.TransformPoint(hookLocalCrane);
            Vector3 aimShedWorld = craneRoot.TransformPoint(aimCraneLocal);
            Vector3 hookShedLocal = shedRoot.InverseTransformPoint(hookShedWorld);
            Vector3 aimShedLocal = shedRoot.InverseTransformPoint(aimShedWorld);

            var placements = WarehouseCrane.Compute(hookShedLocal, aimShedLocal, hubY, size.z * 0.5f);

            var holders = new GameObject[3];
            var rotors = new Transform[3];
            var plugs = new Vector3[3];

            for (int i = 0; i < 3; i++)
            {
                var holder = new GameObject($"warehouse_fan_{i + 1}");
                holder.transform.SetParent(shedRoot, false);
                // Compute's own output is already Unity-space (rule (c)) —
                // used directly, no further mirroring.
                holder.transform.localPosition = placements[i].Position;
                holder.transform.localRotation = placements[i].Rotation;

                var fanGo = (GameObject)ArtAssets.Instantiate(prefab, holder.transform);
                fanGo.name = "fan";
                fanGo.transform.localPosition = new Vector3(-c.x * s, -b.min.y * s, -c.z * s); // footprint recentring, rule (a) — see BuildCraneShell's identical note
                fanGo.transform.localRotation = fanNaturalRot;   // preserve the natural import facing — see probe comment above
                fanGo.transform.localScale = fanNaturalScale * s;
                foreach (var r in fanGo.GetComponentsInChildren<Renderer>(true))
                {
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r.receiveShadows = true;
                }
                var rotor = fanGo.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "fan_rotor");
                rotors[i] = rotor;

                var shell = holder.AddComponent<BoxCollider>();
                shell.center = new Vector3(0f, size.y * 0.5f, 0f);
                shell.size = size;

                holder.AddComponent<DevPlacementTag>().id = holder.name;

                // crane.js:566-569 — where this fan's own flex ends, in the
                // SHED's local frame. `Plug`/`c`/`b` are all rule-(a) model-
                // space quantities (no mirror); the result is composed
                // through the holder's OWN real (already-correct) Transform,
                // so it is Unity-correct with no further sign flip.
                Vector3 plugLocal = new Vector3(
                    (WarehouseCrane.Plug.x - c.x) * s, (WarehouseCrane.Plug.y - b.min.y) * s, (WarehouseCrane.Plug.z - c.z) * s);
                Vector3 plugWorld = holder.transform.TransformPoint(plugLocal);
                plugs[i] = shedRoot.InverseTransformPoint(plugWorld);

                holders[i] = holder;
            }

            return (holders, rotors, plugs);
        }

        // =====================================================================
        // ---- wireUp: crane.js:590-993 (minus the DOM card/audio — see header)
        // =====================================================================
        static (GameObject boardGo, WarehouseFanSwitch switchComp, int polylinePoints, float wallAtUnity)
            WireUp(Transform shedRoot, Transform swingPivot, Transform plane,
                   Vector3[] plugsShedLocal, Transform[] fanRotors, WindRider.PlaneBox ownBox,
                   float planeLocalY, Collider[] skinColliders)
        {
            if (plugsShedLocal.Length < 2) return (null, null, 0, 0f);
            Vector3 mid = plugsShedLocal.Aggregate(Vector3.zero, (a, p) => a + p) / plugsShedLocal.Length;
            // BoardAtX is a hand-authored game-frame X constant — rule (b):
            // mirror once, here, at the point it becomes a real position.
            Vector3 board = new Vector3(-WarehouseCrane.BoardAtX, 0f, mid.z);

            // crane.js:599-610 — the wall-face raycast, excluding fan/crane/
            // plane_shell named colliders. JS casts from (board.x+2,...)
            // toward local +X; both constants are already Unity-mirrored
            // (board.x negative, see above), so the SAME physical direction
            // is "further negative" / shed-local -X here.
            Vector3 fromWorld = shedRoot.TransformPoint(new Vector3(board.x - 2f, 1.0f, board.z));
            Vector3 dirWorld = shedRoot.TransformDirection(Vector3.left);
            float wallAtUnity = -(48f / 2f - 0.6f); // crane.js:608 fallback, mirrored once (rule (b) — same physical wall)
            var hits = Physics.RaycastAll(fromWorld, dirWorld, 40f)
                .Where(h => h.collider != null && !System.Text.RegularExpressions.Regex.IsMatch(h.collider.name, "fan|crane|plane_shell|warehouse_crane"))
                .OrderBy(h => h.distance).ToList();
            if (hits.Count > 0)
                wallAtUnity = shedRoot.InverseTransformPoint(hits[0].point).x; // rule (c): already correct

            // ---- cables: crane.js:612-655 -------------------------------------
            // CableRoutes/CableFloor are "Unity-space in, Unity-space out"
            // (see class header) — board/plugs/wallAt are already Unity-
            // space at this point, fed straight in, no MirrorX anywhere.
            var sorted = plugsShedLocal.OrderBy(p => p.z).ToArray();
            var polylines = new List<Vector3[]>();
            for (int i = 0; i < sorted.Length; i++)
            {
                var cps = CableRoutes.BuildLeadControlPoints(sorted[i], board, i, sorted.Length);
                polylines.Add(CableFloor.BuildFloorSafePolyline(cps, WarehouseCrane.LeadRadius));
            }
            {
                var cps = CableRoutes.BuildTrunkControlPoints(board, wallAtUnity);
                polylines.Add(CableFloor.BuildFloorSafePolyline(cps, WarehouseCrane.TrunkRadius));
            }

            int totalPts = 0;
            var cableMeshes = new List<Mesh>();
            foreach (var poly in polylines) { totalPts += poly.Length; cableMeshes.Add(BuildTubeMesh(poly, WarehouseCrane.LeadRadius)); }
            var cablesGo = new GameObject("fan_cables");
            cablesGo.transform.SetParent(shedRoot, false);
            cablesGo.transform.localPosition = Vector3.zero;
            cablesGo.transform.localRotation = Quaternion.identity;
            var combine = new CombineInstance[cableMeshes.Count];
            for (int i = 0; i < cableMeshes.Count; i++) combine[i] = new CombineInstance { mesh = cableMeshes[i], transform = Matrix4x4.identity };
            var cableMesh = new Mesh();
            cableMesh.CombineMeshes(combine, true, false);
            var cmf = cablesGo.AddComponent<MeshFilter>();
            cmf.sharedMesh = cableMesh;
            var cmr = cablesGo.AddComponent<MeshRenderer>();
            cmr.sharedMaterial = SimpleMaterial(HexColor(0x1b1b1f));
            cmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // ---- the board, socket, button, lamp: crane.js:666-707 -----------
            var layout = WarehouseCrane.ComputeSwitchboardLayout(board, wallAtUnity);
            var boardGo = Primitive(shedRoot, "fan_distro_board", PrimitiveType.Cube,
                layout.BoardBoxCenter, layout.BoardBoxSize, HexColor(0xd8a515));
            Primitive(shedRoot, "fan_wall_socket", PrimitiveType.Cube,
                layout.SocketBoxCenter, layout.SocketBoxSize, HexColor(0x6b6f73));
            var btnGo = Primitive(shedRoot, "fan_switch", PrimitiveType.Cylinder,
                layout.ButtonPos, new Vector3(0.11f, 0.035f, 0.11f), HexColor(0x1f6b2a));
            var lampGo = Primitive(shedRoot, "fan_switch_lamp", PrimitiveType.Cylinder,
                layout.LampPos, new Vector3(0.06f, 0.03f, 0.06f), HexColor(0x3a1010));

            var switchComp = boardGo.AddComponent<WarehouseFanSwitch>();
            switchComp.FanRotors = fanRotors;
            switchComp.SwingPivot = swingPivot;
            switchComp.Plane = plane;
            switchComp.SkinColliders = skinColliders;
            switchComp.LampRenderer = lampGo.GetComponent<Renderer>();
            switchComp.ButtonRenderer = btnGo.GetComponent<Renderer>();
            switchComp.ShedRoot = shedRoot;
            switchComp.SwitchReach = WarehouseCrane.SwitchReach;
            switchComp.OwnBox = ownBox;
            switchComp.PlaneHangY = planeLocalY;

            // crane.js:1015-1087 — the moving-air sheets' MESH, now wired
            // (see class header's "NOT PORTED" list — Props/WindFlowMesh.cs
            // supplied the pure math, this is its first real caller).
            // `swingPivot.parent` IS the crane group (swingGo was parented
            // to craneGo in BuildCraneShell/HangPlane) — crane.js:1087
            // `crane.add(flow)`.
            if (swingPivot != null && plane != null)
                switchComp.FlowRenderer = BuildFlowMesh(swingPivot.parent, swingPivot, plane, ownBox, planeLocalY);

            return (boardGo, switchComp, totalPts, wallAtUnity);
        }

        /// <summary>crane.js:1015-1087 — builds the SEVEN sheets (Props/
        /// WindFlowMesh.cs, already-ported pure math) into one combined Mesh
        /// (crane.js's own `mergeGeometries`), parents it under the crane
        /// group at the machine's resting place (crane.js:1084-1086), and
        /// leaves it disabled (`flow.visible = false`) — WarehouseFanSwitch.
        /// Update drives Renderer.enabled/material opacity/UV offset every
        /// frame from WindTunnel.Step's own output, which already computed
        /// those three numbers but had nothing to apply them to until now.</summary>
        static Renderer BuildFlowMesh(Transform craneRoot, Transform swingPivot, Transform plane,
            WindRider.PlaneBox ownBox, float planeLocalY)
        {
            WindFlowMesh.StreamExtent(ownBox, out float z0, out float z1);
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var colors = new List<Color>();
            var tris = new List<int>();
            var gridIdx = WindFlowMesh.BuildGridIndices();
            for (int k = 0; k < WindFlowMesh.Sheets; k++)
            {
                WindFlowMesh.BuildSheet(ownBox, k, z0, z1, out var pos, out var uv, out var col);
                int baseIdx = verts.Count;
                verts.AddRange(pos);
                uvs.AddRange(uv);
                colors.AddRange(col);
                for (int i = 0; i < gridIdx.Length; i++) tris.Add(gridIdx[i] + baseIdx);
            }

            var mesh = new Mesh { name = "wind_tunnel_flow" };
            if (verts.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();

            var go = new GameObject("wind_tunnel_flow");
            go.transform.SetParent(craneRoot, false);
            // crane.js:1085-1086 — "A LEVEL, FIXED STREAM, hung off the crane
            // at the machine's RESTING place." swingPivot.localPosition is
            // crane.js's `swing.position`; `planeLocalY` is `plane.position.y`.
            go.transform.localPosition = new Vector3(
                swingPivot.localPosition.x, swingPivot.localPosition.y + planeLocalY, swingPivot.localPosition.z);
            go.transform.localRotation = Quaternion.Euler(0f, plane.localEulerAngles.y, 0f);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            var shader = Shader.Find("TbilisiFly/WindFlow");
            var mat = new Material(shader) { name = "wind_tunnel_flow_mat" };
            mat.SetTexture("_MainTex", WindTunnel.BuildHazeTexture());
            mat.SetColor("_Color", new Color(0.682f, 0.804f, 0.902f, 1f)); // 0xaecde6
            mat.SetFloat("_Opacity", 0f);
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.enabled = false; // crane.js:1082 `flow.visible = false`

            return mr;
        }

        static GameObject Primitive(Transform parent, string name, PrimitiveType type, Vector3 localPos, Vector3 size, Color color)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.DestroyImmediate(col); // crane.js's own userData.noCollide
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            // Unity primitives (Cube 1x1x1 / Cylinder r=0.5,h=2 by default).
            go.transform.localScale = type == PrimitiveType.Cylinder
                ? new Vector3(size.x * 2f, size.y * 0.5f, size.z * 2f)
                : size;
            go.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.GetComponent<Renderer>().sharedMaterial = SimpleMaterial(color);
            return go;
        }

        static Material SimpleMaterial(Color c)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var m = new Material(shader) { name = "WarehouseCraneFillDefault" };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            return m;
        }

        static Color HexColor(int hex) =>
            new Color(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f);

        /// <summary>crane.js:612-625 `run()`'s tube SWEEP (the polyline itself
        /// is CableFloor.BuildFloorSafePolyline, already ported pure math) —
        /// a simple ring-per-sample tube, not a full Frenet-frame
        /// TubeGeometry: each consecutive pair of sample points gets a ring
        /// of `radialSegments` vertices in a plane perpendicular to the
        /// segment, oriented by parallel-transporting a running "up"
        /// reference (classic cheap alternative to a Frenet frame — this
        /// polyline has no sharp twists, only floor-clamped dips, so it does
        /// not need one). Open tube (no caps): both ends sit inside a plug/
        /// socket/board model, same as crane.js's own TubeGeometry(...,
        /// openEnded=false) rendering — the caps are never seen either way.</summary>
        static Mesh BuildTubeMesh(Vector3[] pts, float radius, int radialSegments = 6)
        {
            int n = pts.Length;
            var verts = new List<Vector3>();
            var tris = new List<int>();
            Vector3 up = Vector3.up;
            for (int i = 0; i < n; i++)
            {
                Vector3 tangent = (i < n - 1 ? pts[i + 1] - pts[i] : pts[i] - pts[i - 1]).normalized;
                Vector3 refUp = Mathf.Abs(Vector3.Dot(tangent, up)) > 0.98f ? Vector3.forward : up;
                Vector3 side = Vector3.Cross(refUp, tangent).normalized;
                Vector3 normal = Vector3.Cross(tangent, side).normalized;
                for (int k = 0; k < radialSegments; k++)
                {
                    float ang = (float)k / radialSegments * Mathf.PI * 2f;
                    Vector3 ring = (side * Mathf.Cos(ang) + normal * Mathf.Sin(ang)) * radius;
                    verts.Add(pts[i] + ring);
                }
            }
            for (int i = 0; i < n - 1; i++)
            {
                int a0 = i * radialSegments, a1 = (i + 1) * radialSegments;
                for (int k = 0; k < radialSegments; k++)
                {
                    int k1 = (k + 1) % radialSegments;
                    tris.Add(a0 + k); tris.Add(a1 + k); tris.Add(a0 + k1);
                    tris.Add(a0 + k1); tris.Add(a1 + k); tris.Add(a1 + k1);
                }
            }
            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Builds the "unit rope" mesh PlaneSling.AimRopes' returned
        /// TRS matrices are written for: radius WarehouseCrane.RopeRadius,
        /// height 1 along local +Y, CENTRED at the origin (three.js's
        /// CylinderGeometry default pivot, crane.js:422) — not Unity's own
        /// built-in Cylinder primitive, which is twice this size and has a
        /// different default radius, and would need its scale re-derived
        /// instead of applied directly.</summary>
        static Mesh BuildUnitRopeMesh(float radius, int radialSegments = 5)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int ring = 0; ring < 2; ring++)
            {
                float y = ring == 0 ? -0.5f : 0.5f;
                for (int k = 0; k < radialSegments; k++)
                {
                    float ang = (float)k / radialSegments * Mathf.PI * 2f;
                    verts.Add(new Vector3(Mathf.Cos(ang) * radius, y, Mathf.Sin(ang) * radius));
                }
            }
            for (int k = 0; k < radialSegments; k++)
            {
                int k1 = (k + 1) % radialSegments;
                int b0 = radialSegments;
                tris.Add(k); tris.Add(b0 + k); tris.Add(k1);
                tris.Add(k1); tris.Add(b0 + k); tris.Add(b0 + k1);
            }
            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>crane.js:422-461 — the 4 strops, as real child GameObjects
        /// of the CRANE group (not the swing pivot: "these are driven in
        /// world space", crane.js:430), each sharing BuildUnitRopeMesh and
        /// re-aimed every frame by <see cref="WarehouseCraneSlingRig"/>.
        /// `feetPlaneLocal` is PlaneSling.FindFeet's own output, already in
        /// the plane's local space.</summary>
        static WarehouseCraneSlingRig BuildSlingRig(Transform craneRoot, Transform swingPivot, Transform plane, Vector3[] feetPlaneLocal)
        {
            var slingsGo = new GameObject("crane_slings");
            slingsGo.transform.SetParent(craneRoot, false);
            var mesh = BuildUnitRopeMesh(WarehouseCrane.RopeRadius);
            var mat = SimpleMaterial(HexColor(0x23242a));
            var ropes = new Transform[feetPlaneLocal.Length];
            for (int i = 0; i < ropes.Length; i++)
            {
                var go = new GameObject($"crane_sling_{i}");
                go.transform.SetParent(slingsGo.transform, false);
                var mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                ropes[i] = go.transform;
            }
            var rig = slingsGo.AddComponent<WarehouseCraneSlingRig>();
            rig.RopesParent = slingsGo.transform;
            rig.Block = swingPivot;
            rig.Plane = plane;
            rig.FeetPlaneLocal = feetPlaneLocal;
            rig.Ropes = ropes;
            return rig;
        }
    }
}

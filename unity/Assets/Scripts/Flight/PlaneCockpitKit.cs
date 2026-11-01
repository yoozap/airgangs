using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Core;
using TbilisiFly.Character;
using TbilisiFly.Combat;

namespace TbilisiFly.Flight
{
    /// <summary>
    /// Port of src/planes.js:1282-1527 — the half of the cockpit system that
    /// BUILDS on top of what PlaneCockpitMeasurement.cs measures: the furniture
    /// kit, the four hinged controls, the box-shell you can stand on, and the
    /// hood that opens. Together with the sibling file, this satisfies
    /// Character/FootBoarding.cs's own "WHAT THIS FILE DOES NOT BUILD" header —
    /// <see cref="PlaneCockpitBaker.Bake"/> is the entry point that populates a
    /// real <see cref="PlaneCockpitRig"/> so FootBoarding can
    /// actually seat a pilot, in place of an unfilled placeholder.
    ///
    /// ============================== THE KIT ASSET ==============================
    /// tools/build_cockpit.py's output (public/models/cockpit.glb) is already
    /// present in this project pre-converted at unity/Assets/Resources/Art/Props/cockpit.fbx
    /// (checked: its node names survived the conversion byte-for-byte —
    /// cockpit_kit/cockpit_stick/cockpit_throttle/cockpit_pedal_1/
    /// cockpit_pedal_-1/cockpit_tub, read directly out of the FBX's own string
    /// table). Nothing in the project assigns it to a serialized field anywhere
    /// yet, so <see cref="PlaneCockpitBaker.KitEditorFallbackPath"/> loads it by
    /// path via AssetDatabase as an EDITOR-ONLY fallback when no prefab
    /// reference is supplied — a build player has no AssetDatabase, so a real
    /// integration pass should assign the Inspector field instead. Flagged, not
    /// silently assumed.
    ///
    /// ================================ HANDEDNESS ================================
    /// THE ONE GENUINE RISK IN THIS FILE: CONTROLS' offsets (<see cref="StickRaw"/>
    /// etc.) are hand-authored numbers from tools/build_cockpit.py, quoted in
    /// that tool's own (pre-mirror) frame — exactly the "hand-tuned constant,
    /// does not self-correct" category src/planes.js's ×2 belong to (per this
    /// port's own brief). This project's glTF-derived assets are mirrored on X
    /// at import (GeoFrame.cs's documented rule, which the standard glTF-to-
    /// Unity conversion glTFast/UnityGLTF both perform by negating X and
    /// reversing winding) — Assets/Resources/Art/Props/cockpit.fbx is glTF-derived (via
    /// public/models/cockpit.glb), so its imported geometry is X-mirrored from
    /// the authored numbers too. <see cref="ToUnityLocal"/> negates X to
    /// compensate, so an offset meant for "the left hand" still lands on the
    /// physically-left control after the mesh itself was mirrored. UNVERIFIED —
    /// could not spawn a plane and look. The cheap check once the editor is
    /// free: board any parked plane and see whether the right hand finds the
    /// stick and the left the throttle; if they are swapped, delete the negate
    /// in ToUnityLocal, nothing else here depends on the sign.
    ///
    /// buildPlaneShell/hingeHood have NO cross product and no hand-tuned
    /// constant — box extents use `Mathf.Abs`, symmetric under an X mirror, and
    /// the hinge axis is derived at runtime relative to `wrapper`'s own +X (see
    /// <see cref="HingeHood"/>'s own comment), the same "runtime-derived, no
    /// table entry needed" case FootClimb.cs's header describes.
    ///
    /// VERIFIED: constants only (PILOT_SIT etc. live in the sibling file;
    /// SHELL_SLABS/SHELL_MIN_T/HOOD_SWING/the four CONTROLS offsets here)
    /// against src/planes.js's own literals — see tools/_probe_planechar.mjs.
    /// The kit-fitting/shell-building CODE PATHS could not be exercised (no
    /// Unity editor this pass) — reviewed, not executed.
    /// </summary>
    public static class PlaneCockpitKit
    {
        // ---- src/planes.js:1305-1310 CONTROLS, in the kit author's OWN frame ----
        public static readonly Vector3 StickRaw = new Vector3(0f, 0.335f, -0.327f);
        public static readonly Vector3 ThrottleRaw = new Vector3(-0.30f, 0.205f, -0.215f);
        public static readonly Vector3 PedalRRaw = new Vector3(0.135f, -0.058f, -0.608f);
        public static readonly Vector3 PedalLRaw = new Vector3(-0.135f, -0.058f, -0.608f);

        /// <summary>See class header HANDEDNESS note — negates X for this
        /// project's mirrored glTF-derived import.</summary>
        public static Vector3 ToUnityLocal(Vector3 raw) => new Vector3(-raw.x, raw.y, raw.z);

        // ---- src/planes.js:853-854 ----
        internal const float TubHalfW = 0.30f;
        internal const float TubSill = 0.46f;

        // ---- src/planes.js:1415-1416 ----
        internal const int ShellSlabs = 14;
        internal const float ShellMinT = 0.12f;

        // ---- src/planes.js:1491 ----
        internal const float HoodSwing = 0.95f; // rad, ~55 degrees

        /// <summary>planes.js:1321-1391 fitCockpitKit's four named parts, as the
        /// Part/Grip pairs FootBoarding.ControlParts wants.</summary>
        public readonly struct KitResult
        {
            public readonly Transform Kit;
            public readonly FootBoarding.ControlParts Controls;
            public KitResult(Transform kit, FootBoarding.ControlParts controls) { Kit = kit; Controls = controls; }
        }

        static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        /// <summary>planes.js:1321-1391 fitCockpitKit. `kitSource` is an
        /// uninstantiated prefab/asset root carrying the cockpit_kit hierarchy
        /// (see class header "THE KIT ASSET"); null is a documented no-op,
        /// exactly like the JS's own `if (!kit) return` inside its loadCockpitKit
        /// promise. `flyable` false strips the four hinged controls — the hall's
        /// 36 parked machines are scenery (planes.js:1316-1320's own draw-call
        /// rationale carries over unchanged).</summary>
        public static KitResult FitCockpitKit(Transform wrapper, PlaneCockpitMeasurement.Result cockpit,
            GameObject kitSource, bool flyable)
        {
            if (cockpit == null || kitSource == null) return default;
            var o = Object.Instantiate(kitSource, wrapper).transform;
            o.name = "cockpit_kit";
            if (!flyable)
            {
                foreach (var n in new[] { "cockpit_stick", "cockpit_throttle", "cockpit_pedal_1", "cockpit_pedal_-1" })
                {
                    var p = FindDeep(o, n);
                    if (p != null) DestroyEither(p.gameObject);
                }
            }
            o.localPosition = cockpit.Seat;
            o.localRotation = Quaternion.identity; // authored nose-forward, same frame as the wrapper

            var tub = FindDeep(o, "cockpit_tub");
            bool hasInteriorLiner = FindDeep(wrapper, "cockpit_interior") != null;
            if (tub != null)
            {
                if (hasInteriorLiner)
                {
                    // the aeroplane's own inside is the wall now (planes.js:1337-1340)
                    tub.gameObject.SetActive(false);
                }
                else
                {
                    var s = tub.localScale;
                    s.x = Mathf.Min(1f, cockpit.HalfW * 0.88f / TubHalfW);
                    s.y = Mathf.Min(1f, Mathf.Max(0.35f, (cockpit.Deck - cockpit.Seat.y) / TubSill));
                    tub.localScale = s;
                }
            }

            FootBoarding.ControlParts controls = null;
            if (flyable)
            {
                (Transform part, Transform grip) Marked(string name, Vector3 offsetRaw)
                {
                    var part = FindDeep(o, name);
                    if (part == null) return (null, null);
                    var grip = new GameObject(name + "_grip").transform;
                    grip.SetParent(part, false);
                    // offset is quoted from the SEAT PAN, i.e. the kit root `o`.
                    grip.position = o.TransformPoint(ToUnityLocal(offsetRaw));
                    return (part, grip);
                }
                var (stickPart, stickGrip) = Marked("cockpit_stick", StickRaw);
                var (throttlePart, throttleGrip) = Marked("cockpit_throttle", ThrottleRaw);
                var (pedalRPart, pedalRGrip) = Marked("cockpit_pedal_1", PedalRRaw);
                var (pedalLPart, pedalLGrip) = Marked("cockpit_pedal_-1", PedalLRaw);
                controls = new FootBoarding.ControlParts
                {
                    Stick = new FootBoarding.ControlPart { Part = stickPart, Grip = stickGrip },
                    Throttle = new FootBoarding.ControlPart { Part = throttlePart, Grip = throttleGrip },
                    PedalR = new FootBoarding.ControlPart { Part = pedalRPart, Grip = pedalRGrip },
                    PedalL = new FootBoarding.ControlPart { Part = pedalLPart, Grip = pedalLGrip },
                };
            }
            return new KitResult(o, controls);
        }

        /// <summary>planes.js:1392-1407 setPlaneFlyable — swaps a parked
        /// scenery kit for a flyable one (or back). Returns the new kit/controls
        /// so the caller can update its own PlaneCockpitRig; this function does
        /// not know about that type (PlaneCockpitBaker.Reflyable does).</summary>
        public static KitResult SetPlaneFlyable(Transform wrapper, Transform existingKit,
            PlaneCockpitMeasurement.Result cockpit, GameObject kitSource, bool flyable)
        {
            if (existingKit != null) DestroyEither(existingKit.gameObject);
            return FitCockpitKit(wrapper, cockpit, kitSource, flyable);
        }

        /// <summary>Object.Destroy is DEFERRED and logs
        /// "Destroy may not be called from edit mode" outside play mode, so the
        /// object survives the frame and the error count climbs — the plane hall
        /// / ShedSceneFill build the 36 parked machines from OnEnable in EDIT
        /// mode, which is 4 scenery controls stripped per plane
        /// (Probes/HeroScreenProbe logged HEROSCREEN_EDITMODE_DESTROY_COUNT=154,
        /// every one a cockpit_stick/throttle/pedal). Same
        /// Application.isPlaying switch Props/WarehouseInteriorFill.cs:222 and
        /// Flight/PlaneProps.cs:815 already use.</summary>
        static void DestroyEither(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) Object.Destroy(go);
            else Object.DestroyImmediate(go);
        }

        /// <summary>
        /// THE CANOPY REPARENT, MADE LEGAL. <see cref="HingeHood"/> slips a
        /// `hood_hinge` pivot between the glass and its parent (planes.js:1500-
        /// 1511), and every plane in the game arrives as a tracked PREFAB
        /// INSTANCE (Flight/PlaneHallFill.cs:186 / Flight/ShedFill.cs:165 /
        /// Core/StarterPlaneSkin.cs all go through
        /// PrefabUtility.InstantiatePrefab), which Unity refuses to reparent
        /// inside: "Setting the parent of a transform which resides in a Prefab
        /// instance is not possible (GameObject: 'Glass')", once per plane with
        /// a canopy — 17 errors PER PLAY SESSION (PLAYMODE ERRORX_0..16), and
        /// the hinge silently did not happen on any of them, so the canopy
        /// could never open.
        ///
        /// Flight/PlaneModelFrame.Attach already unpacks for exactly this
        /// reason, but behind an `!Application.isPlaying` guard whose premise
        /// ("an instance created in play mode is not a tracked prefab instance
        /// at all") is FALSE for PrefabUtility.InstantiatePrefab in the editor's
        /// own play mode — which is why the errors only ever showed up there.
        /// Unpacking here instead of widening that guard keeps the fix inside
        /// the cockpit builder, where the reparent is. The loader seam that
        /// pass mentions now exists — Core/ArtAssets — and owns the editor-only
        /// half, so this is a no-op in a player, where ArtAssets.Instantiate
        /// hands out a plain clone with no prefab link to break.
        ///
        /// Nothing is lost by unpacking: these instances are rebuilt by
        /// ShedSceneFill.OnEnable on every scene load and are never serialised.
        /// </summary>
        static void UnpackPrefabAround(Transform t)
        {
            if (t == null) return;
            ArtAssets.UnpackCompletely(t.gameObject);
        }

        // =====================================================================
        // BuildPlaneShell — planes.js:1415-1474, "the airframe you can stand on"
        // =====================================================================

        /// <summary>Builds a `plane_shell` child of `wrapper` carrying one
        /// BoxCollider per slab, tagged <see cref="PlaneShellTag"/> — Flight's
        /// own marker, per Combat/PlaneDamageRecord.cs's documented convention
        /// ("markers Flight attaches, and Combat only ever reads"). UNITY-NATIVE
        /// SIMPLIFICATION per Flight/PlanePhysics.cs's own header: a real
        /// Collider follows its Transform for free, so unlike the JS there is
        /// no merged static mesh to re-bake if the plane moves — PlanePhysics.cs
        /// (not edited here) is expected to toggle `.enabled` on the returned
        /// colliders while its own machine is actively flown.</summary>
        public static Transform BuildPlaneShell(Transform wrapper)
        {
            var air = PlaneCockpitMeasurement.BoxOf(wrapper, wrapper);
            Vector3 size = air.size;
            if (!(size.z > 0.5f)) return null;

            var pts = new List<Vector3>();
            foreach (var mf in wrapper.GetComponentsInChildren<MeshFilter>(true))
                if (PlaneCockpitMeasurement.IsSkin(mf.transform, wrapper))
                    CollectStrided(mf.transform, wrapper, pts);
            foreach (var smr in wrapper.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (PlaneCockpitMeasurement.IsSkin(smr.transform, wrapper))
                    CollectStrided(smr.transform, wrapper, pts);
            if (pts.Count < 32) return null;

            float noseEnd = air.min.z + size.z * 0.22f;
            float fw = 0f;
            foreach (var p in pts) if (p.z < noseEnd) fw = Mathf.Max(fw, Mathf.Abs(p.x));
            fw = Mathf.Max(fw, size.x * 0.04f);

            var shellGo = new GameObject("plane_shell");
            shellGo.transform.SetParent(wrapper, false);
            shellGo.AddComponent<PlaneShellTag>();

            void Add(float x0, float x1, float y0, float y1, float z0, float z1)
            {
                float sx = Mathf.Max(x1 - x0, ShellMinT);
                float sy = Mathf.Max(y1 - y0, ShellMinT);
                float sz = Mathf.Max(z1 - z0, ShellMinT);
                var box = shellGo.AddComponent<BoxCollider>();
                box.center = new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f);
                box.size = new Vector3(sx, sy, sz);
            }

            bool any = false;
            for (int i = 0; i < ShellSlabs; i++)
            {
                float z0 = air.min.z + size.z * i / ShellSlabs;
                float z1 = air.min.z + size.z * (i + 1) / ShellSlabs;
                float cy0 = float.PositiveInfinity, cy1 = float.NegativeInfinity, cx = 0f;
                float ox0 = float.PositiveInfinity, ox1 = float.NegativeInfinity;
                float oy0 = float.PositiveInfinity, oy1 = float.NegativeInfinity;
                foreach (var p in pts)
                {
                    if (p.z < z0 || p.z >= z1) continue;
                    if (Mathf.Abs(p.x) <= fw)
                    {
                        cy0 = Mathf.Min(cy0, p.y); cy1 = Mathf.Max(cy1, p.y); cx = Mathf.Max(cx, Mathf.Abs(p.x));
                    }
                    else
                    {
                        ox0 = Mathf.Min(ox0, p.x); ox1 = Mathf.Max(ox1, p.x);
                        oy0 = Mathf.Min(oy0, p.y); oy1 = Mathf.Max(oy1, p.y);
                    }
                }
                if (cy0 < cy1) { Add(-cx, cx, cy0, cy1, z0, z1); any = true; }
                if (ox0 < ox1) { Add(ox0, ox1, oy0, oy1, z0, z1); any = true; }
            }
            if (!any) { DestroyEither(shellGo); return null; }
            return shellGo.transform;
        }

        static void CollectStrided(Transform mt, Transform wrapper, List<Vector3> into)
        {
            // mirrors planes.js:1422-1427's own stride-to-~12000 sampling, but
            // reuses PlaneCockpitMeasurement's vertex reader for one code path.
            foreach (var wp in PlaneCockpitMeasurement.WorldVertices(mt, 12000))
                into.Add(wrapper.InverseTransformPoint(wp));
        }

        // =====================================================================
        // The hood that opens — planes.js:1491-1526
        // =====================================================================
        public sealed class HoodHinge
        {
            public Transform Pivot;
            public Vector3 Axis; // pivot-local
            public float Open;
        }

        /// <summary>planes.js:1493-1518 hingeHood. See class header HANDEDNESS:
        /// the axis is the plane's own +X carried into the pivot's local frame
        /// at the moment this runs, not a fixed world/compass direction, so no
        /// mirror term applies here independently of the CONTROLS one above.</summary>
        public static HoodHinge HingeHood(Transform wrapper, Transform hood)
        {
            if (hood == null || hood.parent == null) return null;
            UnpackPrefabAround(hood);
            var box = PlaneCockpitMeasurement.BoxOf(hood, wrapper);
            Vector3 c = box.center;
            // nose is -Z (planes.js convention, carried unchanged into this
            // project — PlaneCatalog.cs never flips the LOCAL nose axis, only
            // the def.yaw/def.roll orientation fix), so the hinge sits on the
            // AFT, upper edge of the glass.
            Vector3 hingeLocal = new Vector3(c.x, box.max.y, box.max.z);
            var pivot = new GameObject("hood_hinge").transform;
            pivot.SetParent(hood.parent, false);
            pivot.position = wrapper.TransformPoint(hingeLocal);
            hood.SetParent(pivot, true); // worldPositionStays — keeps the lid exactly where it is
            Vector3 axis = pivot.InverseTransformDirection(wrapper.right).normalized;
            return new HoodHinge { Pivot = pivot, Axis = axis, Open = 0f };
        }

        /// <summary>planes.js:1519-1526 setHoodOpen. 0 shut, 1 standing open.</summary>
        public static void SetHoodOpen(HoodHinge h, float t)
        {
            if (h == null) return;
            h.Open = t;
            h.Pivot.localRotation = Quaternion.AngleAxis(HoodSwing * t * Mathf.Rad2Deg, h.Axis);
        }
    }
}

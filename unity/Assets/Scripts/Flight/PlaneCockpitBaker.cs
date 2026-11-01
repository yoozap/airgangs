using UnityEngine;
using TbilisiFly.Character;
using TbilisiFly.Combat;
using TbilisiFly.Core;

namespace TbilisiFly.Flight
{
    /// <summary>
    /// THE ENTRY POINT this pass's brief asked for: fills in the real cockpit/
    /// controls/seat data Character/FootBoarding.cs's own header says "has no
    /// Unity port yet" and had to stub out with its own placeholder
    /// (<see cref="FootBoarding.IPlaneHost"/> / <see cref="PlaneCockpitRig"/>).
    /// This file is the "future cockpit-measurement bake step" that
    /// PlaneCockpitRig's own doc comment says attaches to a plane wrapper —
    /// written against that exact contract, not a new one.
    ///
    /// Drop this component on a plane's root GameObject (alongside whatever
    /// PlaneController/PlaneDamageRecord it already carries) and call
    /// <see cref="Bake"/> once — on Awake by default, or explicitly from
    /// whichever system spawns/clones a plane. It:
    ///   1. measures the cockpit (PlaneCockpitMeasurement.Measure)
    ///   2. instantiates the furniture kit and the four hinged controls
    ///      (PlaneCockpitKit.FitCockpitKit)
    ///   3. builds the standable box-shell (PlaneCockpitKit.BuildPlaneShell)
    ///   4. hinges the canopy, if the model has one (PlaneCockpitKit.HingeHood)
    ///   5. writes all of it into a real PlaneCockpitRig component
    ///      on the SAME GameObject, which is exactly what FootBoarding reads
    ///      through IPlaneHost — and into Combat/PlaneDamageRecord.CockpitKit
    ///      if one is already present, per that file's own documented
    ///      convention ("attached by whichever Flight system instantiates a
    ///      plane").
    ///
    /// NOT DONE HERE, AND WHY: wiring this component onto PlaneController's own
    /// spawn path (PlaneHallFill.cs / wherever a flyable plane is instantiated)
    /// is out of reach — those files are on this pass's forbidden-edit list.
    /// Until something calls Bake(), a plane behaves exactly as it does today
    /// (PlaneCockpitRig absent, FootBoarding's own degrade-to-false path).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlaneCockpitBaker : MonoBehaviour
    {
        [Tooltip("The cockpit furniture kit's root (cockpit_kit) — drag " +
                 "Assets/Resources/Art/Props/cockpit.fbx here, or a prefab wrapping it. " +
                 "Left null, Bake() tries KitEditorFallbackPath (editor only).")]
        [SerializeField] GameObject kitPrefab;

        [Tooltip("src/planes.js PLANES id, for looking up the per-airframe " +
                 "cockpit override (Flight/PlaneCatalog.cs PlaneDef.Cockpit). " +
                 "Leave blank to measure with no override.")]
        [SerializeField] string planeId;

        [Tooltip("False = scenery (hall-parked default, no stick/throttle/" +
                 "pedals — planes.js:1316-1320's draw-call rationale). True = " +
                 "the one machine the hero is actually seated in.")]
        [SerializeField] bool flyable;

        public PlaneCockpitRig Rig { get; private set; }
        public PlaneCockpitKit.HoodHinge Hood { get; private set; }

        /// <summary>Editor-only fallback source for the kit asset when no
        /// Inspector reference was wired — see class header "THE KIT ASSET".
        /// A build player must assign <see cref="kitPrefab"/> instead; this
        /// path never resolves outside the editor.</summary>
        public const string KitEditorFallbackPath = "Assets/Resources/Art/Props/cockpit.fbx";

        void Awake()
        {
            if (Rig == null) Bake();
        }

        public PlaneCockpitRig Bake()
        {
            PlaneDef def = null;
            if (!string.IsNullOrEmpty(planeId))
                foreach (var p in Planes.All) if (p.Id == planeId) { def = p; break; }

            var kit = kitPrefab != null ? kitPrefab : ResolveKitFallback();
            Rig = Bake(gameObject, def, kit, flyable, out _measured);
            Hood = _lastHood;
            return Rig;
        }

        PlaneCockpitKit.HoodHinge _lastHood;
        PlaneCockpitMeasurement.Result _measured;

        /// <summary>Static, reusable entry point — no dependency on this
        /// MonoBehaviour's own serialized fields, so another Flight system can
        /// call it directly once it is allowed to touch the spawn path (see
        /// class header "NOT DONE HERE").</summary>
        public static PlaneCockpitRig Bake(GameObject planeRoot, PlaneDef def,
            GameObject kitSource, bool flyable, out PlaneCockpitMeasurement.Result measuredOut)
        {
            // THE MODEL FRAME, HANDED OVER. This used to be
            // `planeRoot.transform`, i.e. the FLIGHT frame — which was the
            // same thing only for as long as the model child sat at identity
            // and the aeroplane therefore flew tail-first (docs/QUALITY_BAR.md
            // "THE BIGGEST VISIBLE BUG"). Every measurement below is anchored
            // on `air.min.z + len * 0.05 .. 0.78` (src/planes.js:1019-1020),
            // on the kit's hand-authored z offsets (planes.js:1305-1310,
            // quoted nose-toward--Z) and on the shell's nose-end slab — all of
            // them in the JS's `wrapper` space, which Flight/PlaneModelFrame
            // now materialises as the `model_frame` node. Measuring in there
            // is what makes f4f's cockpit measurable at all (it was the one
            // `cockpit=insane` of 35: its file is stored nose-WEST, so the
            // -Z window searched across the wingspan).
            var wrapper = PlaneModelFrame.MeasureFrameOf(planeRoot.transform);
            var rig = planeRoot.GetComponent<PlaneCockpitRig>();
            if (rig == null) rig = planeRoot.AddComponent<PlaneCockpitRig>();

            // wrapper.userData.halfSpan — planes.js:1571, measured BEFORE the
            // kit/shell/pilot are attached, same reason the JS measures `wbox`
            // early (a later measurement reads its own furniture as airframe).
            var air = PlaneCockpitMeasurement.BoxOf(wrapper, wrapper);
            rig.HalfSpanValue = air.size.x * 0.5f;   // a 180 deg yaw leaves size.x alone
            rig.FlightController = planeRoot.GetComponent<PlaneController>();

            var measured = PlaneCockpitMeasurement.Measure(wrapper, def?.Cockpit);
            PlaneCockpitKit.HoodHinge hood = null;
            if (measured != null)
            {
                // ...and handed BACK. The kit, the shell and the hinge are all
                // built as children of `wrapper`, so they stay in the model
                // frame where their authored offsets mean what they say. The
                // plain Vector3 numbers that leave this function do NOT: both
                // Character/FootBoarding.cs's seat/step/rim/wing arithmetic and
                // its `noseYaw` read them through `OwnPlane.Wrapper`, which is
                // this rig's own Transform == the FLIGHT root. So they are
                // re-expressed there, through the frame's ACTUAL orientation
                // rather than a hard-coded sign (PlaneModelFrame.ModelPointToRoot).
                // HalfW/Deck/Ceil/Bot are a half-width and three heights —
                // invariant under a yaw — and are carried across untouched.
                var info = PlaneCockpitMeasurement.ToCockpitInfo(measured);
                info.Seat = PlaneModelFrame.ModelPointToRoot(planeRoot.transform, info.Seat);
                rig.CockpitValue = info;
                var kitResult = PlaneCockpitKit.FitCockpitKit(wrapper, measured, kitSource, flyable);
                rig.CockpitKitValue = kitResult.Kit;
                rig.ControlsValue = kitResult.Controls;
                if (measured.Hood != null) hood = PlaneCockpitKit.HingeHood(wrapper, measured.Hood);
            }
            else
            {
                rig.CockpitValue = null;
            }
            measuredOut = measured;

            rig.ShellValue = PlaneCockpitKit.BuildPlaneShell(wrapper);

            var dmgRec = planeRoot.GetComponent<PlaneDamageRecord>();
            if (dmgRec != null && rig.CockpitKitValue != null) dmgRec.CockpitKit = rig.CockpitKitValue;

            // If a PlaneCockpitBaker component is also present (it needn't
            // be — PlaneHallFill.cs bakes the 35 scenery planes through this
            // static entry point alone, with no MonoBehaviour attached),
            // mirror the result onto its INSTANCE state too. Without this,
            // Rig/Hood/_measured stayed at their default null forever even
            // after a successful bake, so any later baker.SetFlyable()/
            // baker.SetHoodOpen()/baker.Hood read (BuildPlayableScene.cs's
            // own player-plane bake adds exactly such a component) would
            // silently no-op on its own `if (Rig == null) return;` guard —
            // found by PlaneCockpitBakeProbe hitting a NullReferenceException
            // on baker.Rig after this exact call shape.
            var baker = planeRoot.GetComponent<PlaneCockpitBaker>();
            if (baker != null)
            {
                baker.Rig = rig;
                baker._measured = measured;
                baker._lastHood = hood;
                baker.Hood = hood;
            }

            return rig;
        }

        /// <summary>planes.js:1392-1407 setPlaneFlyable, as an instance call —
        /// swaps the parked scenery kit for a flyable one (or back) WITHOUT
        /// re-measuring the cockpit (the measurement never changes; only the
        /// furniture does).</summary>
        public void SetFlyable(bool newFlyable)
        {
            if (Rig == null || Rig.CockpitValue == null || _measured == null) return;
            flyable = newFlyable;
            var kit = kitPrefab != null ? kitPrefab : ResolveKitFallback();
            // the MODEL frame, same as Bake — `_measured` is in it, and the
            // kit's authored offsets are quoted in it (see Bake's comment).
            var result = PlaneCockpitKit.SetPlaneFlyable(
                PlaneModelFrame.MeasureFrameOf(transform), Rig.CockpitKitValue, _measured, kit, newFlyable);
            Rig.CockpitKitValue = result.Kit;
            Rig.ControlsValue = result.Controls;
            var dmgRec = GetComponent<PlaneDamageRecord>();
            if (dmgRec != null) dmgRec.CockpitKit = result.Kit;
        }

        /// <summary>game.js's own per-frame `setHoodOpen(plane, t)` call,
        /// exposed for whichever climb-in system (FootClimb.cs / a future
        /// FootBoarding extension) ends up driving the canopy.</summary>
        public void SetHoodOpen(float t) => PlaneCockpitKit.SetHoodOpen(Hood, t);

        GameObject ResolveKitFallback()
        {
            var asset = ArtAssets.Load<GameObject>(KitEditorFallbackPath);
            if (asset == null)
                Debug.LogWarning($"PlaneCockpitBaker: no kit prefab assigned and " +
                    $"{KitEditorFallbackPath} did not resolve — cockpit will measure " +
                    "but carry no furniture/controls.");
            return asset;
        }
    }
}

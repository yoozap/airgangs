using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// Per-plane damage bookkeeping — the component src/planedamage.js's "rec" is:
    /// one of these lives on every aeroplane's root (wrapper) GameObject, attached
    /// by whichever Flight system instantiates a plane (mirrors how Combat/Guns.cs's
    /// PlaneSurfaceTag and Ballistics.cs's PlaneShellTag are markers Flight attaches,
    /// and Combat only ever reads).
    ///
    /// Grown from the placeholder a previous pass left behind (see git history) into
    /// the full record PlaneDamage.cs needs: tears per structural part, the severed
    /// set, the hole-texture set, the live marks, the breakup rig, canopy state.
    /// </summary>
    public sealed class PlaneDamageRecord : MonoBehaviour
    {
        [Header("Airframe facts (src/planes.js wrapper.userData.*)")]
        [Tooltip("The plane's real size in metres. Drives structural cut lines in " +
                 "AirframeBreakup and the refine-ray reach in PlaneDamage.")]
        public float Size = 11f;

        [Tooltip("Half wingspan, metres (wrapper.userData.halfSpan). Used to judge how " +
                 "much of a wing a cut at some chord-wise point actually took.")]
        public float HalfSpan = 6f;

        [Tooltip("Distance from the plane's own origin to the nose tip, metres " +
                 "(wrapper.userData.noseLen) — where the smoke/fire plume anchors.")]
        public float NoseLen = 3f;

        [Tooltip("The cockpit furniture root (wrapper.userData.cockpitKit), if any. " +
                 "Everything under it is excluded from the airframe's own damageable parts.")]
        public Transform CockpitKit;

        [Tooltip("Hull points, 0..MaxHp. Real empty/MTOW-derived values live in the plane " +
                 "table (Flight/PlaneCatalog.cs); kept here only as the fallback this file " +
                 "reads when no external hull tracker is wired (see HpGetter).")]
        public float HullPoints = 1f;
        public float MaxHullPoints = 1f;

        [Tooltip("True while this is the plane the player is currently flying. That plane " +
                 "is deliberately NOT in world.collidables (src/game.js ownPlaneTargets), so " +
                 "its own gun hits are handed to PlaneDamage a different way than a parked " +
                 "target's are — see src/planedamage.js:1552 _burn's ownership-changed guard.")]
        public bool IsPlayerOwn;

        [Tooltip("The machine is a wreck/has been destroyed (src/planedamage.js rec.dead). " +
                 "A dead airframe always smokes/burns regardless of hull fraction.")]
        public bool Dead;

        [Tooltip("Georgian display name for the hit-toast (src/planedamage.js:1341 rec.def.name).")]
        public string DisplayName = "თვითმფრინავი";

        /// <summary>Optional external hull-point source (e.g. a target registry). When set,
        /// PlaneDamage reads hull fraction through this instead of HullPoints/MaxHullPoints —
        /// mirrors src/planedamage.js:1432 `this.game.targets?.hpOf(rec) ?? rec.hp`.</summary>
        public System.Func<float> HpGetter;

        // ---- internal state PlaneDamage.cs owns (all src/planedamage.js "rec._*") ----
        internal PlanePartsCache PartsCache;
        internal readonly Dictionary<string, List<PlaneTear>> Tears = new Dictionary<string, List<PlaneTear>>();
        internal readonly List<PlaneTear> Exits = new List<PlaneTear>();
        internal readonly HashSet<string> CutOff = new HashSet<string>();
        internal HoleSet Holes;
        internal PlanePartsCache HoleParts;
        internal readonly List<PlaneMark> Marks = new List<PlaneMark>();
        internal AirframeBreakup Cutter;
        internal int CanopyHits;
        internal bool CanopyGone;
        internal Matrix4x4 LastWorldMatrix;
        internal bool HasLastWorldMatrix;
        internal bool Shown = true;
        internal float PuffTimer;
        internal string WhyNotCut; // this._why — exposed for probes
    }
}

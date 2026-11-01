using System;
using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Fx;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// THE WIRING GAP Core/PlaneHullDamage.cs's own header names explicitly:
    /// "construct one instance per active plane, subscribe the hooks to
    /// FxAudio/AirframeBreakup/SmokeTrail-equivalent/OnHint". Before this file,
    /// no plane anywhere in this port owned its own <see cref="AirframeBreakup"/>
    /// instance tied to its OWN hull-damage hooks (OnDetach/OnCrashScatter never
    /// had anything to call) and no plane's WING MESH had ever been resolved
    /// (OnWingBend/OnWingRepair had no Mesh/Material to touch) — see this port's
    /// final report for the exact lines GameLoop.cs needs to construct one of
    /// these per PlaneController and wire it to PlaneHullDamage's 5 unwired hooks
    /// (OnDetach, OnCrashScatter, OnSmokeClear, OnWingBend, OnWingRepair).
    ///
    /// NOT a duplicate port: Combat/AirframeBreakup.cs (src/breakup.js) already
    /// exists and is already constructed once per SHOT-AT target by
    /// Combat/PlaneDamage.cs:1069 (`new AirframeBreakup(BreakupWorld, rec.transform,
    /// null, false)`) — that is the general "any plane that takes a hit" path.
    /// This file is the OTHER half: the PLAYER'S OWN plane's structural-stress
    /// path (G-break/VNE/ground collisions — Core/PlaneHullDamage.cs), which is a
    /// separate state machine ported from game.js directly (not planedamage.js)
    /// and therefore needs its OWN AirframeBreakup instance, `isPlayerOwn: true`
    /// (so Restore() puts the wings back on at respawn — AirframeBreakup.cs:103-106),
    /// which nothing in this port constructs. This wraps that constructor call
    /// exactly as PlaneDamage.cs's own call shape, plus the wing-mesh resolution
    /// PlaneDamage.cs never needed (a shot-at target's wings only ever get CUT,
    /// never bent-and-repaired-while-still-attached).
    ///
    /// ===================== wing-mesh resolution — src/planes.js:1594-1611 =====================
    /// The JS finds every child mesh named "sagkanatsekil*" (right) / "solkanatsekil*"
    /// (left) at BUILD time and stores `wrapper.userData.wings[side] = {mesh, tip}`.
    /// The name prefix is NOT trusted at face value (the comment there says so): the
    /// side is decided by which half of the fuselage the mesh's own LOCAL bounding-box
    /// centre sits on (`center.x > 0 ? 'right' : 'left'`), because the model may be
    /// mirrored. Ported verbatim here, resolved LAZILY off the imported prefab's own
    /// child names instead (Unity has no equivalent "after-build, before-first-use"
    /// hook), using the same `rel = planeInverse * childWorld` local-bounds technique
    /// Combat/AirframeBreakup.cs's own Build() uses for its structural lines — so, per
    /// that file's own handedness header, NO mirror correction applies: a point
    /// classified against the model's own local axes, never a world axis, reads the
    /// same in both frames.
    ///
    /// `tip` in the JS is a plain local Vector3, read back later via
    /// `plane.localToWorld(wing.tip.clone())`. Core/PlaneHullDamage.cs's own WingTip()
    /// instead asks for a `Func&lt;string,Transform&gt; wingTipBone` (a NAMED CHILD
    /// TRANSFORM, "if the airframe prefab carries one" — see that method's doc
    /// comment), so this creates one real empty Transform per wing, parented to the
    /// plane with `localPosition = tip`, specifically to satisfy that delegate shape
    /// without needing Core/PlaneHullDamage.cs to change its signature.
    /// =================================================================================
    ///
    /// Material char/un-char (src/game.js:2278-2320) relies on three.js's explicit
    /// `material.clone()` so damage never bleeds into a shared material; Unity's
    /// `Renderer.materials` (plural) ALREADY clones per-renderer on first access,
    /// so no explicit clone step is needed here — see ApplyChar() below.
    /// </summary>
    public sealed class WingInfo
    {
        public Transform MeshTransform;
        public Renderer MeshRenderer;
        public Vector3 LocalTip;      // plane-local space — src/planes.js:1608 `tip`
        public Transform TipMarker;   // satisfies PlaneHullDamage.WingTip's `wingTipBone` Func<string,Transform>
        public float OrigRotZDeg;
        public bool RotCaptured;      // src/game.js:2287 `wing.mesh.userData.origRotZ ??=`
        public Color[] OrigColor;     // per-material, captured lazily — src/game.js:2290 `m.userData.origColor ??=`
        public bool ColorCaptured;
    }

    public sealed class PlaneAirframeHost
    {
        // src/game.js:2288/2308 — wingtip droop, radians (right droops down/negative,
        // left droops down/positive about local Z — see BendWing()).
        internal const float WingDroopRad = 0.16f;
        // src/game.js:2290 `m.color.setHex(0x2e2c26)` — charred.
        internal static readonly Color CharColor = new Color(0x2e / 255f, 0x2c / 255f, 0x26 / 255f, 1f);
        // src/game.js:2291/2312 `m.roughness = 1` / `m.roughness = 0.55` — note the
        // JS does NOT restore the material's ORIGINAL roughness on repair, it slams
        // to a fixed 0.55 regardless of what it was before. Ported verbatim; this is
        // not a bug to fix. Unity's analogue of "roughness" is "_Smoothness" = 1-roughness.
        internal const float CharSmoothness = 0f;
        internal const float RepairSmoothness = 0.45f;

        public readonly Transform Plane;
        public readonly AirframeBreakup Breakup;
        public readonly Dictionary<string, WingInfo> Wings = new Dictionary<string, WingInfo>();

        /// <summary>src/game.js:2316/2435-2436 `this.smoke` — a wing-tip smoke trail.
        /// NOW OWNED HERE: this host constructs its own Fx.SmokeTrailFx (below) and
        /// defaults this hook to clearing it, so PlaneHullDamage.OnSmokeClear works
        /// out of the box; a caller MAY still override it. The spawn-cadence half of
        /// src/game.js:2425-2439 lives in <see cref="Tick"/> — WIRING: Fx.SmokeTrailFx
        /// previously had zero callers project-wide (grep confirmed).</summary>
        public Action SmokeClear;

        readonly SmokeTrailFx _smoke = new SmokeTrailFx();
        float _smokeTimer;

        bool _wingsBuilt;

        public PlaneAirframeHost(IBreakupWorld world, Transform plane, Func<Vector3> velOf = null, bool isPlayerOwn = true)
        {
            Plane = plane;
            Breakup = new AirframeBreakup(world, plane, velOf, isPlayerOwn);
            SmokeClear = () => _smoke.Clear();
        }

        /// <summary>src/game.js:2321-2325 `_wingSmokePos` — the tip itself, or (once the
        /// wing is fully torn off, struct[part]==0) 30% of the way out from the plane's
        /// own centre, since there is no tip transform left to pour smoke from.</summary>
        public Vector3 WingSmokePos(string side, bool detached)
        {
            BuildWings();
            if (!Wings.TryGetValue(side, out var w) || w.TipMarker == null) return Plane != null ? Plane.position : Vector3.zero;
            Vector3 tip = w.TipMarker.position;
            return detached && Plane != null ? Vector3.Lerp(Plane.position, tip, 0.3f) : tip;
        }

        /// <summary>src/game.js:2425-2439 — call every frame this plane is flying or
        /// crashed. Owns the 0.05s spawn cadence and the SmokeTrailFx instance;
        /// nothing outside this file needs to touch Fx.SmokeTrailFx directly. The
        /// caller supplies PlaneHullDamage's own WingDmgLeft/WingDmgRight (whether
        /// smoke should pour at all) and Struct.Air.WingL/WingR == 0 (whether that
        /// wing is gone, for WingSmokePos's lerp) — this class owns no hull state.</summary>
        public void Tick(float dt, bool crashed, bool leftDamaged, bool rightDamaged,
            bool leftDetached, bool rightDetached)
        {
            _smokeTimer += dt;
            if (_smokeTimer > 0.05f)
            {
                _smokeTimer = 0f;
                if (crashed)
                {
                    _smoke.Spawn(Plane != null ? Plane.position : Vector3.zero);
                }
                else
                {
                    if (leftDamaged) _smoke.Spawn(WingSmokePos("left", leftDetached));
                    if (rightDamaged) _smoke.Spawn(WingSmokePos("right", rightDetached));
                }
            }
            _smoke.Update(dt);
        }

        // ---- the 5 hooks, one call each, matching Core/PlaneHullDamage.cs's
        // Action signatures exactly so GameLoop.cs's wiring is a one-liner. ----

        /// <summary>Wires PlaneHullDamage.OnDetach (Action&lt;string,float,bool&gt;).</summary>
        public bool DetachPart(string part, float spin, bool burn) => Breakup.Detach(part, null, spin, burn);

        /// <summary>Wires PlaneHullDamage.OnCrashScatter (Action&lt;Vector3,float&gt;).</summary>
        public void CrashScatter(Vector3 baseVel, float severity) => Breakup.CrashScatter(baseVel, severity);

        /// <summary>Wires PlaneHullDamage.OnSmokeClear (Action).</summary>
        public void ClearWingSmoke() => SmokeClear?.Invoke();

        /// <summary>Wires PlaneHullDamage.OnWingBend (Action&lt;string&gt;). `side` is
        /// "left"/"right". src/game.js:2278-2299's wing-bend half only (health/struct
        /// damage is PlaneHullDamage.DamageWing's own job, already done before this
        /// hook fires).</summary>
        public void BendWing(string side)
        {
            BuildWings();
            if (!Wings.TryGetValue(side, out var w) || w.MeshTransform == null) return;
            if (!w.RotCaptured)
            {
                w.OrigRotZDeg = w.MeshTransform.localEulerAngles.z;
                w.RotCaptured = true;
            }
            float deltaDeg = (side == "right" ? -WingDroopRad : WingDroopRad) * Mathf.Rad2Deg;
            Vector3 e = w.MeshTransform.localEulerAngles;
            e.z += deltaDeg;
            w.MeshTransform.localEulerAngles = e;
            ApplyChar(w, charred: true);
        }

        /// <summary>Wires PlaneHullDamage.OnWingRepair (Action&lt;string&gt;).
        /// src/game.js:2301-2314's per-side half (OnSmokeClear is the OTHER half of
        /// _repairWings and is wired separately — see ClearWingSmoke()).</summary>
        public void RepairWing(string side)
        {
            BuildWings();
            if (!Wings.TryGetValue(side, out var w) || w.MeshTransform == null) return;
            if (w.RotCaptured)
            {
                Vector3 e = w.MeshTransform.localEulerAngles;
                e.z = w.OrigRotZDeg;
                w.MeshTransform.localEulerAngles = e;
            }
            ApplyChar(w, charred: false);
        }

        /// <summary>Satisfies PlaneHullDamage.WingTip/WingSmokePos's
        /// `Func&lt;string,Transform&gt; wingTipBone` parameter.</summary>
        public Transform WingTipBone(string side)
        {
            BuildWings();
            return Wings.TryGetValue(side, out var w) ? w.TipMarker : null;
        }

        // ---- src/planes.js:1594-1611, resolved lazily off the live prefab ----
        void BuildWings()
        {
            if (_wingsBuilt) return;
            _wingsBuilt = true;
            if (Plane == null) return;
            Matrix4x4 planeInv = Plane.worldToLocalMatrix;
            foreach (var mf in Plane.GetComponentsInChildren<MeshFilter>(true))
                TryClassify(mf.gameObject.name, mf.sharedMesh, mf.transform, mf.GetComponent<Renderer>(), planeInv);
            foreach (var smr in Plane.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                TryClassify(smr.gameObject.name, smr.sharedMesh, smr.transform, smr, planeInv);
        }

        void TryClassify(string name, Mesh mesh, Transform t, Renderer renderer, Matrix4x4 planeInv)
        {
            if (mesh == null || renderer == null || name == null) return;
            if (!name.StartsWith("sagkanatsekil", StringComparison.Ordinal) &&
                !name.StartsWith("solkanatsekil", StringComparison.Ordinal)) return;

            // Local-space bounds of THIS mesh, in the plane's own local frame — the
            // same 8-corner transform AirframeBreakup.Build() uses for its own lines,
            // not Renderer.bounds (world AABB, which cannot be rotated back into a
            // correct local AABB by transforming just its centre/extent).
            Matrix4x4 rel = planeInv * t.localToWorldMatrix;
            Bounds b = mesh.bounds;
            bool any = false;
            Bounds local = default;
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = new Vector3(
                    (i & 1) != 0 ? b.max.x : b.min.x,
                    (i & 2) != 0 ? b.max.y : b.min.y,
                    (i & 4) != 0 ? b.max.z : b.min.z);
                Vector3 p = rel.MultiplyPoint3x4(c);
                if (!any) { local = new Bounds(p, Vector3.zero); any = true; }
                else local.Encapsulate(p);
            }
            if (!any) return;

            string side = local.center.x > 0f ? "right" : "left"; // src/planes.js:1604
            Vector3 tip = new Vector3(
                side == "right" ? local.max.x : local.min.x,       // src/planes.js:1605
                local.center.y, local.center.z);

            if (Wings.ContainsKey(side)) return; // first match wins, same as JS's plain object assignment order

            var marker = new GameObject($"wingTip_{side}").transform;
            marker.SetParent(Plane, false);
            marker.localPosition = tip;

            Wings[side] = new WingInfo
            {
                MeshTransform = t,
                MeshRenderer = renderer,
                LocalTip = tip,
                TipMarker = marker,
            };
        }

        void ApplyChar(WingInfo w, bool charred)
        {
            var mats = w.MeshRenderer.materials; // auto-instances per-renderer == JS's explicit .clone()
            if (!w.ColorCaptured)
            {
                w.OrigColor = new Color[mats.Length];
                for (int i = 0; i < mats.Length; i++) w.OrigColor[i] = GetBaseColor(mats[i]);
                w.ColorCaptured = true;
            }
            for (int i = 0; i < mats.Length; i++)
            {
                Color restore = (w.OrigColor != null && i < w.OrigColor.Length) ? w.OrigColor[i] : GetBaseColor(mats[i]);
                SetBaseColor(mats[i], charred ? CharColor : restore);
                SetSmoothness(mats[i], charred ? CharSmoothness : RepairSmoothness);
            }
            w.MeshRenderer.materials = mats;
        }

        static Color GetBaseColor(Material m) =>
            m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;

        static void SetBaseColor(Material m, Color c)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            else if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        static void SetSmoothness(Material m, float s)
        {
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", s);
            else if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", s);
        }

        public void Dispose()
        {
            foreach (var w in Wings.Values)
                if (w.TipMarker != null) UnityEngine.Object.Destroy(w.TipMarker.gameObject);
            Wings.Clear();
            Breakup?.Dispose();
            _smoke?.Dispose();
        }
    }
}

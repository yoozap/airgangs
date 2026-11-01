using System;
using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.City;

namespace TbilisiFly.Combat
{

    /// <summary>
    /// THE MISSING ASSEMBLY STEP for src/airport.js:574-906's two shooting
    /// ranges. City/AirportDetail.cs already has every builder
    /// (ClassifyRangeMeshes/BuildGatePivots/BuildCarriers/BuildPaperPivots/
    /// RangeFiringEnd), City/RangeDoorsPaper.cs already has the gate/paper
    /// swing math, and this project's own Combat/RangeLane.cs/PaperTargets.cs
    /// already have Adopt/Hit/Update — but as of this port, NOTHING calls any
    /// of them together: grepping the whole unity/Assets/Scripts tree for
    /// `RangeLanes`/`PaperTargets`/`RangeDoorsPaper` before writing this file
    /// found only their own declarations, never a caller. The task that found
    /// this gap named it precisely: "Combat.PaperTargets.Adopt and
    /// Combat.RangeLanes.Adopt both exist with the right shapes but nothing
    /// calls them; City/RangeDoorsPaper.Tick is likewise orphaned."
    ///
    /// This class is the Combat-side half of that missing step: given a
    /// shooting range's already-instantiated prop root (loading/placing the
    /// "shooting_range" FBX in the world is Props/City's job, out of this
    /// agent's scope this pass), <see cref="Build"/> runs AirportDetail's
    /// classification + pivot-building in airport.js's own order (gates then
    /// carriers then paper pivots, BEFORE RangeFiringEnd — AirportDetail.cs's
    /// own header documents this as an order dependency), then adopts every
    /// carrier into Combat/RangeLane.cs's RangeLanes and every paper sheet
    /// into Combat/PaperTargets.cs's PaperTargets, and builds the door/paper
    /// pivot lists City/RangeDoorsPaper.cs's Tick needs. <see cref="Tick"/>
    /// drives all three every frame; <see cref="TryRouteHit"/> is the one
    /// branch a bullet's hit-resolution needs to actually score a shot.
    ///
    /// ================ ITEMS 1-3 CLOSED THIS PASS (see bottom of class) ================
    ///  1. DiscoverAndAdoptAll() is the scene-assembly caller — called once
    ///     from Core/GameLoop.cs's BeginSession(). It does NOT instantiate a
    ///     fresh "shooting_range" FBX (that would duplicate, unflattened and
    ///     uncollided, what Editor/BuildAirportDetail.cs's BuildShootingRanges()
    ///     already bakes into the scene — see AdoptBaked's own header for why
    ///     Build() below is the wrong tool for this). It finds the baked
    ///     "warehouse_range" holder(s) instead and adopts them via AdoptBaked().
    ///  2. Core/GameLoop.cs's StageCombatAndFx now calls TickAll(dt, Foot.Pos,
    ///     aiming) once a frame — foot position, not the camera's, per City/
    ///     RangeDoorsPaper.cs's own header. `aiming` is read through a new
    ///     `GameLoop.IsAimingProvider` hook (same shape as the existing
    ///     IsArmedProvider), left unset (defaults to not-aiming) because
    ///     wiring it to Combat/Shooting.cs's Pistol.Aiming is Core/
    ///     CombatWorldAdapter.cs's Install() — off limits this pass, same as
    ///     IsArmedProvider's own assignment.
    ///  3. Combat/ImpactFx.cs's MarkHit (the pistol's hitscan path, Guns.cs's
    ///     own `Impacts?.MarkHit(...)`) and Resolve (the simulated-projectile
    ///     path) both now check `PaperCardTag` first and call TryRouteHitAll()
    ///     — mirroring Combat/Guns.cs's own PlaneSurfaceTag special-case —
    ///     before any surface classification or decal runs.
    ///
    /// ================ STILL NOT WIRED, OUTSIDE THIS AGENT'S OWNERSHIP ================
    ///  4. src/airport.js:842 also pushes the sheet onto `citymap.shootables`
    ///     — this port's equivalent raycast-target registration convention is
    ///     City's own (IBallisticsWorld / collidablesNear, per Combat/Guns.cs
    ///     and Combat/Ballistics.cs's own headers), not this file's to guess.
    ///     AdoptBaked()/Build() below both add a bare MeshCollider when the
    ///     sheet has none — the floor-level minimum for ANY raycast to land on
    ///     it at all — but do not register it with whatever spatial-hash/layer
    ///     convention City's own raycast target set actually uses. In practice
    ///     a plain `Physics.Raycast` (Guns.cs's own hitscan) still finds a
    ///     MeshCollider regardless of that registration, so aiming AT a sheet
    ///     and firing already works end to end; what City's own convention
    ///     would add on top is whatever broader-phase culling/listing it uses
    ///     elsewhere (e.g. a minimap blip), not hit detection itself.
    /// </summary>
    public static class RangeBootstrap
    {
        public sealed class Range
        {
            public readonly RangeLanes Lanes = new RangeLanes();
            public readonly PaperTargets Targets = new PaperTargets();
            public readonly List<RangeDoorsPaper.Door> Doors = new List<RangeDoorsPaper.Door>();
            public readonly List<RangeDoorsPaper.Paper> Sheets = new List<RangeDoorsPaper.Paper>();

            // paper pivot ("range_paper") Transform -> the lane whose carrier it
            // hangs under, for RangeDoorsPaper.Tick's swayOf hook (composing the
            // carrier's own pendulum swing with the touch-it-and-it-swings-away
            // push — airport.js:900's `p.pivot.userData.sway ?? 0`).
            readonly Dictionary<Transform, RangeLanes.Lane> _laneOfPivot = new Dictionary<Transform, RangeLanes.Lane>();

            public float SwayOf(Transform pivot) =>
                _laneOfPivot.TryGetValue(pivot, out var lane) ? lane.Th : 0f;

            internal void MapPivotToLane(Transform pivot, RangeLanes.Lane lane) => _laneOfPivot[pivot] = lane;
        }

        /// <summary>
        /// airport.js:574-672's own order, transcribed. `doorsRoot`/`paperRoot`
        /// must be fresh, unparented Transforms at world identity (airport.js's
        /// own fresh `new THREE.Group()`s, src/airport.js:574) — BuildGatePivots/
        /// BuildCarriers's own doc comments explain why that matters.
        /// </summary>
        public static Range Build(Transform sceneRoot, Transform doorsRoot, Transform paperRoot)
        {
            var range = new Range();
            var classified = AirportDetail.ClassifyRangeMeshes(sceneRoot);

            var gatePivots = AirportDetail.BuildGatePivots(classified.Gates, doorsRoot);
            var carriers = AirportDetail.BuildCarriers(classified.LaneParts, paperRoot);
            var paperPivots = AirportDetail.BuildPaperPivots(carriers, classified.LaneParts);
            var (firingZ, endZ) = AirportDetail.RangeFiringEnd(classified.Counters, sceneRoot);

            // src/rangelane.js's own adopt, called once per carrier, axis 'z'
            // exactly as airport.js:849 passes it.
            foreach (var kv in carriers)
            {
                var lane = range.Lanes.Adopt(kv.Value, firingZ, endZ, "z");
                if (lane == null) continue;
                // RangeLanes.Adopt already found this lane's "range_paper"
                // pivots itself (same traversal BuildPaperPivots's own output
                // produced) — reuse Lane.Papers rather than re-scanning.
                foreach (var pivot in lane.Papers) range.MapPivotToLane(pivot, lane);
            }

            foreach (var pivot in gatePivots) range.Doors.Add(RangeDoorsPaper.MakeDoor(pivot));
            foreach (var pivot in paperPivots) range.Sheets.Add(RangeDoorsPaper.MakePaper(pivot));

            // airport.js:840-846 — the SHEET itself (not its hinge pivot) is
            // what a round hits and what scores. PaperTargets.Adopt measures
            // it and gives it a blank ISSF face; a MeshCollider is the
            // floor-level minimum for any raycast to land on it (see class
            // header, item 4, for what is still missing beyond that).
            foreach (var lp in classified.LaneParts)
            {
                if (!lp.IsPaper) continue;
                range.Targets.Adopt(lp.Mesh);
                if (lp.Mesh.GetComponent<Collider>() == null)
                    lp.Mesh.gameObject.AddComponent<MeshCollider>();
                var tag = lp.Mesh.GetComponent<PaperCardTag>() ?? lp.Mesh.gameObject.AddComponent<PaperCardTag>();
                tag.Pivot = lp.Mesh.parent; // the range_paper hinge pivot, post BuildPaperPivots
            }
            return range;
        }

        /// <summary>
        /// Call once a frame with the personage's FOOT position (see class
        /// header, item 2) and whether the pistol is currently aiming (hides
        /// the lane-control snapshot the same way src/rangelane.js:371 hides
        /// its card). Runs the carrier drive + pendulum sway
        /// (Combat/RangeLane.cs) and the gate/paper touch swing
        /// (City/RangeDoorsPaper.cs), composed exactly as airport.js:900 does.
        /// </summary>
        public static RangeLanes.LaneHud Tick(Range range, float dt, Vector3 actorPos, bool aiming)
        {
            var hud = range.Lanes.Update(dt, actorPos, aiming);
            RangeDoorsPaper.Tick(range.Doors, range.Sheets, actorPos, dt, range.SwayOf);
            return hud;
        }

        /// <summary>
        /// src/papertargets.js:394-395 — a shot that SCORED. The JS sets
        /// `this.active = card; this._hide = 15; this._refresh(shot);` at the
        /// tail of `hit()`, i.e. the score card is shown off the very same hit
        /// that the card recorded; there is no second scoring pass and no
        /// polling. This event is that tail, for a view to render:
        /// <c>PaperTargetUi?.Show(targets.Snapshot(card, shot))</c>.
        ///
        /// Fired ONLY when <see cref="PaperTargets.Hit"/> returned a shot
        /// (it returns null for an off-card hit, papertargets.js:338's own
        /// `px &lt; -40 ...` bail), so a subscriber may assume `card`/`shot`
        /// non-null — `card` is `targets.Active`, which Hit() has just
        /// assigned (PaperTargets.cs:351, the JS's `this.active = card`).
        ///
        /// The 15 s dwell (`_hide = 15`) is deliberately NOT carried here: it
        /// is view state and UI/Combat/PaperTargetScoreView.cs's `Show` owns
        /// it, same split as UI/HitToastView.cs's own `_dwell`.
        ///
        /// Same exposure shape as Combat/Guns.cs / Rockets.cs / ImpactFx.cs's
        /// `OnHitToast` (subscribed with `+=` in Core/CombatWorldAdapter.
        /// Install), but STATIC because this class is — one subscription
        /// covers every range <see cref="DiscoverAndAdoptAll"/> has adopted
        /// or will adopt, which an instance event on each `Range.Targets`
        /// would not.
        ///
        /// BEING STATIC, IT OUTLIVES A SESSION: unlike `OnHitToast` on a
        /// freshly-constructed MachineGun, this delegate survives Install()
        /// being run twice in one editor session (and survives a domain
        /// reload) and would then update two cards per shot. Subscribe from
        /// Install() with a NAMED method and `-=` it first, not with a fresh
        /// lambda (a lambda cannot be unsubscribed), so re-installing is
        /// idempotent.
        /// </summary>
        public static event Action<PaperTargets, PaperTargets.Card, PaperTargets.Shot> OnPaperScore;

        /// <summary>
        /// The <see cref="PaperTargets"/> whose card scored last, or null
        /// before the first scored shot — the port's stand-in for the JS's
        /// single global `active` card.
        ///
        /// WHICH CARD'S SCORE SHOWS: in the JS there is exactly ONE
        /// PaperTargets for the whole game (src/game.js:172
        /// `this.paper = new PaperTargets(this)`) and `active` is set only in
        /// `hit()` (papertargets.js:393), so the card on screen is the sheet
        /// you last PUT A ROUND THROUGH — never the nearest one, never the
        /// lane you are standing at. Walking between lanes, or away from the
        /// range entirely, does not change or clear it; `_refresh` reads
        /// `this.active` and nothing else. This port has one PaperTargets per
        /// adopted range (two ranges are baked), so "which card" becomes
        /// "which range, then which card inside it" — hence this field.
        /// </summary>
        public static PaperTargets LastScored { get; private set; }

        /// <summary>
        /// src/papertargets.js:461-476 `reset()` + its own `_refresh(null)`,
        /// for the score card's "ახალი ბლანკი" button
        /// (UI/Combat/PaperTargetScoreView.cs's <c>OnResetClicked</c>).
        ///
        /// RESETS ONE CARD, NOT ALL OF THEM: the JS signature is
        /// `reset(card = this.active)` and the button calls it with no
        /// argument, so it blanks exactly the one card that last scored and
        /// leaves every other sheet on every other lane alone. Returns false
        /// when nothing has been shot yet (`if (!card?.canvas) return false`,
        /// :462) or when the active card is somehow not live — in which case
        /// the JS's `_refresh(null)` also early-returns on `!card` (:521) and
        /// the panel is left exactly as it was.
        ///
        /// Deliberately does NOT touch the 15 s dwell: `reset()` never
        /// assigns `_hide` in the JS, which is why PaperTargetScoreView has a
        /// separate `Refresh` that does not restart it. Call that one with
        /// this snapshot, not `Show`.
        /// </summary>
        public static bool TryResetScored(out PaperTargets.ScoreSnapshot snap)
        {
            snap = default;
            var targets = LastScored;
            if (targets == null || !targets.Reset()) return false; // papertargets.js:462
            var card = targets.Active;
            if (card == null) return false;                        // papertargets.js:521 `if (!el || !card) return`
            snap = targets.Snapshot(card, null);                   // papertargets.js:474 `_refresh(null)`
            return true;
        }

        /// <summary>
        /// A bullet's raycast hit on a paper target, to be called BEFORE the
        /// generic surface-mark path runs (see class header, item 3). Returns
        /// true when this hit WAS paper — the caller should skip its usual
        /// decal/mark call in that case, same as Combat/Guns.cs's own
        /// PlaneSurfaceTag branch.
        /// </summary>
        public static bool TryRouteHit(Range range, RaycastHit hit, Vector3 dir, float bulletMm, float power = 1f)
        {
            var tag = hit.collider != null ? hit.collider.GetComponent<PaperCardTag>() : null;
            if (tag == null) return false;
            Vector2? uv = hit.collider is MeshCollider ? hit.textureCoord : (Vector2?)null;
            var shot = range.Targets.Hit(hit.collider.transform, hit.point, dir, bulletMm, uv);
            range.Lanes.Hit(hit.collider.transform, hit.point, power);
            // src/papertargets.js:393-395 — the card records the shot and the
            // score card is shown off THIS hit, in this order. A miss (shot
            // null) leaves `active` and the panel untouched, exactly as the
            // JS's early `return null` at :338 does.
            if (shot != null)
            {
                LastScored = range.Targets;
                OnPaperScore?.Invoke(range.Targets, range.Targets.Active, shot);
            }
            return true;
        }

        // =====================================================================
        // ==================== THE THREE CALL SITES, CLOSED ====================
        // The "STILL NEEDED" list above (items 1-3) is done as of this pass.
        // Rather than re-run AirportDetail's classify/pivot-build a SECOND time
        // (which <see cref="Build"/> above does, and which would double-pivot a
        // range that Editor/BuildAirportDetail.cs's BuildShootingRanges() has
        // ALREADY classified+pivoted and baked into the scene — confirmed by
        // reading that file: it calls ClassifyRangeMeshes/BuildGatePivots/
        // BuildCarriers/BuildPaperPivots itself, then explicitly says, in its
        // own comment at the end of BuildShootingRanges(), that
        // Combat.PaperTargets.Adopt/Combat.RangeLanes.Adopt are the missing
        // wiring, "call it once per 'range_carrier' built above" — i.e. onto
        // its OWN baked names, not a fresh instance), <see cref="AdoptBaked"/>
        // below walks that ALREADY-BUILT hierarchy by the exact names both
        // files agree on ("warehouse_range" -> "range_doors"/"range_gate",
        // "range_paper_root"/"range_carrier"/"range_paper", and
        // "range_counter_blocks"/"range_counter_block") and adopts it directly
        // — no second mesh surgery pass, no duplicate geometry.
        //
        // (As of this pass none of the four committed .unity scenes actually
        // contain a baked "warehouse_range" yet — grepped, zero hits — because
        // nobody has run BuildAirportDetail.Run() against them and saved the
        // result. That bake is Editor-only tooling this agent cannot run or
        // write [Editor/ is off limits this pass]; DiscoverAndAdoptAll below
        // degrades to "found 0, logged, did nothing" until it has. That is the
        // honest state of item 1, not a bug in this file.)
        // =====================================================================

        /// <summary>Every adopted range, across however many "warehouse_range"
        /// holders <see cref="DiscoverAndAdoptAll"/> found. Core/GameLoop.cs's
        /// per-frame call and Combat/ImpactFx.cs's hit router both read this
        /// through <see cref="TickAll"/>/<see cref="TryRouteHitAll"/> rather
        /// than needing their own reference to a specific Range.</summary>
        public static readonly List<Range> All = new List<Range>();

        /// <summary>
        /// ITEM 1 — the scene-assembly caller. Finds every GameObject named
        /// "warehouse_range" already in the loaded scene (there are two,
        /// Editor/BuildAirportDetail.cs's own BuildShootingRanges() comment:
        /// "two mirrored range units, PITCH apart") and adopts each via
        /// <see cref="AdoptBaked"/>. Call once, after the scene/city has
        /// loaded (Core/GameLoop.cs's BeginSession, mirroring how that method
        /// already does its own one-time post-load wiring).
        /// </summary>
        public static int DiscoverAndAdoptAll()
        {
            int before = All.Count;
            var seen = new HashSet<Transform>();
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t.name != "warehouse_range" || !seen.Add(t)) continue;
                var range = AdoptBaked(t);
                if (range != null) All.Add(range);
            }
            int added = All.Count - before;
            Debug.Log($"RANGEBOOTSTRAP_DISCOVER holders_found={added}");
            return added;
        }

        /// <summary>
        /// Adopts an ALREADY-BUILT "warehouse_range" holder (Editor/
        /// BuildAirportDetail.cs's BuildShootingRanges() output) into a fresh
        /// <see cref="Range"/> — no mesh surgery, since that already happened
        /// at bake time. Walks the baked names directly:
        ///   "range_paper_root" -> each "range_carrier" child -> RangeLanes.Adopt
        ///     (which itself finds that carrier's "range_paper" pivots —
        ///     Combat/RangeLane.cs:106-107 — so they are not re-collected here)
        ///   each "range_paper" pivot's own child mesh -> PaperTargets.Adopt
        ///   "range_doors" -> each "range_gate" child -> RangeDoorsPaper.MakeDoor
        ///
        /// firingZ/endZ (RangeLanes.Adopt's own doc: "in the range UNIT's own
        /// local frame, since two range units can sit anywhere in the world")
        /// cannot be re-measured from the original counter MESHES — Editor/
        /// BuildAirportDetail.cs's FlattenProp already merged them away by the
        /// time this runs — so they are reconstructed from what DOES survive
        /// the bake: the "range_counter_block" BoxColliders (built from
        /// exactly those counter meshes' own world bounds,
        /// AirportDetail.BuildCounterBlocks) stand in for AirportDetail.
        /// RangeFiringEnd's `fireBox`, and the solid content's own Renderer
        /// bounds (everything under `holder` except the doors/paper subtrees —
        /// the same exclusion RangeFiringEnd's own "order dependency" note
        /// describes) stand in for its `envZ`. Both are converted from WORLD
        /// AABB corners into `holder`-local space via InverseTransformPoint
        /// (exact for the 8 corners regardless of any rotation/scale the
        /// warehouse itself carries in the real city — see this method's own
        /// "ZCornersLocal" helper) rather than read directly as world Z, which
        /// would be wrong the instant the warehouse is not axis-aligned.
        /// </summary>
        public static Range AdoptBaked(Transform holder)
        {
            if (holder == null) return null;
            Transform paperRoot = FindDescendant(holder, "range_paper_root");
            if (paperRoot == null) return null; // nothing baked here yet
            Transform doorsRoot = FindDescendant(holder, "range_doors");
            Transform blocksRoot = FindDescendant(holder, "range_counter_blocks");

            float firingZ = 0f;
            {
                Bounds? cb = null;
                if (blocksRoot != null)
                    foreach (var col in blocksRoot.GetComponentsInChildren<Collider>(true))
                        cb = cb.HasValue ? EncapsulateB(cb.Value, col.bounds) : col.bounds;
                if (cb.HasValue)
                {
                    float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                    foreach (float z in ZCornersLocal(holder, cb.Value)) { lo = Mathf.Min(lo, z); hi = Mathf.Max(hi, z); }
                    firingZ = (lo + hi) / 2f;
                }
            }
            float endZ = firingZ;
            {
                Bounds? envB = null;
                foreach (var r in holder.GetComponentsInChildren<Renderer>(true))
                {
                    if (doorsRoot != null && r.transform.IsChildOf(doorsRoot)) continue;
                    if (r.transform.IsChildOf(paperRoot)) continue;
                    envB = envB.HasValue ? EncapsulateB(envB.Value, r.bounds) : r.bounds;
                }
                if (envB.HasValue)
                {
                    float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                    foreach (float z in ZCornersLocal(holder, envB.Value)) { lo = Mathf.Min(lo, z); hi = Mathf.Max(hi, z); }
                    endZ = Mathf.Abs(hi - firingZ) > Mathf.Abs(lo - firingZ) ? hi : lo; // AirportDetail.RangeFiringEnd:659-660
                }
            }

            var range = new Range();
            foreach (Transform carrier in paperRoot)
            {
                if (carrier.name != "range_carrier") continue;
                var lane = range.Lanes.Adopt(carrier, firingZ, endZ, "z");
                if (lane == null) continue;
                foreach (var pivot in lane.Papers)
                {
                    range.MapPivotToLane(pivot, lane);
                    range.Sheets.Add(RangeDoorsPaper.MakePaper(pivot));
                    if (pivot.childCount == 0) continue;
                    var sheet = pivot.GetChild(0); // BuildPaperPivots parents exactly the one sheet mesh here
                    range.Targets.Adopt(sheet);
                    if (sheet.GetComponent<Collider>() == null) sheet.gameObject.AddComponent<MeshCollider>();
                    var tag = sheet.GetComponent<PaperCardTag>() ?? sheet.gameObject.AddComponent<PaperCardTag>();
                    tag.Pivot = pivot;
                }
            }
            if (doorsRoot != null)
                foreach (Transform gate in doorsRoot)
                    if (gate.name == "range_gate") range.Doors.Add(RangeDoorsPaper.MakeDoor(gate));

            return range;
        }

        static Transform FindDescendant(Transform root, string name)
        {
            foreach (Transform child in root)
            {
                if (child.name == name) return child;
                var found = FindDescendant(child, name);
                if (found != null) return found;
            }
            return null;
        }

        static Bounds EncapsulateB(Bounds a, Bounds b) { a.Encapsulate(b); return a; }

        /// <summary>The 8 corners of a WORLD-space AABB, each converted into
        /// `frame`'s LOCAL Z — exact per corner (Transform.InverseTransformPoint
        /// fully inverts the whole ancestor chain, rotation/scale included),
        /// unlike trying to rescale a size/extent across a rotated hierarchy.</summary>
        static float[] ZCornersLocal(Transform frame, Bounds worldBounds)
        {
            Vector3 c = worldBounds.center, e = worldBounds.extents;
            var zs = new float[8];
            int i = 0;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                        zs[i++] = frame.InverseTransformPoint(c + new Vector3(sx * e.x, sy * e.y, sz * e.z)).z;
            return zs;
        }

        /// <summary>ITEM 2 — Core/GameLoop.cs's one per-frame call. Drives every
        /// adopted range's carrier/sway/gate/paper swing; returns whichever
        /// range's lane the actor is actually near (there are two ranges, at
        /// most one can be "Near" at a time in practice), for a UI to show.</summary>
        public static RangeLanes.LaneHud TickAll(float dt, Vector3 actorPos, bool aiming)
        {
            RangeLanes.LaneHud result = default;
            foreach (var r in All)
            {
                var hud = Tick(r, dt, actorPos, aiming);
                if (hud.Lane != null) result = hud;
            }
            return result;
        }

        /// <summary>ITEM 3 — the one branch Combat/ImpactFx.cs's MarkHit/Resolve
        /// now call before the generic mark-hit path. Tries every adopted
        /// range; only the one that actually owns `hit.collider` (if any) does
        /// anything (PaperTargets.Hit/RangeLanes.Hit both no-op on an unknown
        /// Transform), so trying all of them is correct, not just convenient.</summary>
        public static bool TryRouteHitAll(RaycastHit hit, Vector3 dir, float bulletMm, float power = 1f)
        {
            foreach (var r in All)
                if (TryRouteHit(r, hit, dir, bulletMm, power)) return true;
            return false;
        }
    }
}

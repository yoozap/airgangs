using System;
using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.UI.Target;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// Port of src/target.js's <c>Targets</c> class — the REGISTRY/PICK/CYCLE/
    /// DAMAGE half. The CARD paint half is already ported at UI/Target/
    /// TargetCardView.cs + TargetSnapshot.cs; the GROUND SELECTION RING half is
    /// UI/Target/TargetRingView.cs (TargetRingMath + TargetRingView, this same
    /// pass). This file is the piece both of those explicitly left out:
    /// register/unregister (target.js:68-85), pickAt (target.js:102-123),
    /// cycle (target.js:127-147), recOf (target.js:152-165), and damagePlane/
    /// _kill (target.js:333-375).
    ///
    /// WHY A NEW FILE INSTEAD OF EXTENDING Core/GameLoop.cs's OWN MINIMAL
    /// TargetRegistry: GameLoop.cs is being edited concurrently by another
    /// agent (coordinator instruction) and is on this port's DO-NOT-EDIT list.
    /// Its nested `GameLoop.TargetRegistry` (Core/GameLoop.cs:917-983) is a
    /// deliberately minimal stand-in — register/unregister/a round-robin Cycle
    /// (no range/cone filter)/a single untargeted PickAt (no pointer-lock
    /// centring, no PICK_RANGE clamp) — with no selection ring at all. This
    /// class is the REAL thing target.js:31-147 actually specifies (range-
    /// gated, forward-cone Tab cycle sorted nearest-first; a pointer-lock-aware
    /// pick; a WoW ground ring). It does not touch or replace GameLoop's
    /// version — whoever next edits GameLoop.cs can swap `_targets`'s type
    /// over to this one; until then both compile and run side by side (they
    /// are unrelated types in different namespaces:
    /// `TbilisiFly.Core.GameLoop.TargetRegistry` vs `TbilisiFly.Combat.
    /// TargetRegistry`).
    ///
    /// DECOUPLING FROM LIVE PHYSICS/CITY STATE (so this is unit-probable
    /// without a running scene): <see cref="ResolveHits"/> takes an already-
    /// gathered <c>RaycastHit[]</c> (the caller does the actual
    /// Physics.Raycast/RaycastAll against whatever spatial broadphase City/
    /// Flight expose — target.js:115-118's own `g.world.collidablesNear`) and
    /// only resolves WHICH registered record, if any, a hit belongs to —
    /// target.js:157-165 `_recOf`'s own job, kept separate from the raycast
    /// itself exactly as the JS keeps them separate methods. Likewise
    /// <see cref="BuildRingSnapshot"/> takes an injected floor-probe delegate
    /// rather than calling Physics.Raycast itself.
    ///
    /// "WHAT THE MACHINE IS MADE OF" (target.js:216-229 stamina/top speed/ram
    /// damage/mass) is planes.js DATA this file has no access to (Flight
    /// territory, out of scope here, same as GameLoop's own minimal registry
    /// leaves those 4 TargetSnapshot fields at their zero default) — Register's
    /// optional parameters let a Flight-side caller fill them in instead of
    /// leaving them unset, but nothing here invents plane data.
    ///
    /// HANDEDNESS: no cross products anywhere in this file — picking, cycling,
    /// sorting and damage math are all plain distances/dot-products/positions,
    /// the same "handedness-safe as-is" category CharacterRig.cs's header
    /// describes (nothing here builds a tangent frame).
    ///
    /// FLOAT PRECISION: every distance/position here is LOCAL (camera to a
    /// target a few hundred metres away, PickRange/CycleRange-bounded) — never
    /// an accumulation over the whole city's absolute coordinate range (the
    /// class of bug a sibling City/ pass just fixed, float32 over |x| up to
    /// 264 km). Vector3.Distance/Dot on two nearby Unity Transforms is exactly
    /// what the engine's own float Transform system already does; there is no
    /// separate accumulation here to promote to double.
    /// </summary>
    public sealed class TargetRegistry
    {
        public const float PickRange = 700f;  // target.js:31 PICK_RANGE
        public const float CycleRange = 500f; // target.js:32 CYCLE_RANGE
        public const float CycleDot = 0.25f;  // target.js:33 CYCLE_DOT

        public sealed class Rec
        {
            public Transform Wrapper;
            public Collider Collider;
            public string Id, Name, Sub;
            public Texture2D Portrait;
            public bool RoleIsTank;
            public bool IsOwn;
            public int MaxHp = 1;
            public int Hp = 1;
            public bool Dead;
            public float? HalfSpan;  // rec.wrapper.userData.halfSpan, target.js:317
            public float? RingY;     // target.js:286 rec.ringY — cached floor height, once
            // "what the machine is made of" — target.js:216-229, Flight-owned data,
            // left at 0 unless the caller (Register's optional params) supplies it.
            public int TopSpeedKmh, RamDamage;
            public float MassKg;
        }

        readonly List<Rec> _list = new List<Rec>();
        readonly Dictionary<Collider, Rec> _byCollider = new Dictionary<Collider, Rec>();
        public IReadOnlyList<Rec> List => _list;
        public Rec Current { get; private set; }
        float _hitT; // target.js:48 this._hitT

        /// <summary>target.js:372 `g.fx?.explosion/plume`, `g.world?.onShake` — the
        /// killing shot's FX, Fx/City territory this file does not own (see
        /// Combat/BreakingGlass.cs's OnGlassBreakSound for the identical
        /// event-hookup convention). Position, radius.</summary>
        public event Action<Vector3, float> OnKillFx;

        // ---- the registry — target.js:68-85 ----

        public Rec Register(Transform wrapper, Collider collider, string id, string name, string sub,
            Texture2D portrait, bool roleIsTank, int maxHp, float? halfSpan = null,
            int topSpeedKmh = 0, int ramDamage = 0, float massKg = 0f)
        {
            int hp = Mathf.Max(1, maxHp);
            var rec = new Rec
            {
                Wrapper = wrapper, Collider = collider, Id = id, Name = name, Sub = sub,
                Portrait = portrait, RoleIsTank = roleIsTank, MaxHp = hp, Hp = hp, HalfSpan = halfSpan,
                TopSpeedKmh = topSpeedKmh, RamDamage = ramDamage, MassKg = massKg,
            };
            _list.Add(rec);
            if (collider != null) _byCollider[collider] = rec;
            return rec;
        }

        /// <summary>target.js:79-85 unregister — drops the record AND anything
        /// targeting it (a card left showing a machine you just climbed into is a
        /// card showing yourself).</summary>
        public bool Unregister(Rec rec)
        {
            if (rec == null) return false;
            bool removed = _list.Remove(rec);
            if (rec.Collider != null) _byCollider.Remove(rec.Collider);
            if (Current == rec) Clear();
            return removed;
        }

        public void Clear() => Current = null; // target.js:177-182 (DOM/ring side lives in the view)
        public void Set(Rec rec) { if (rec == null) Clear(); else Current = rec; } // target.js:169-175

        // ---- the player's own machine as a target — target.js:88-94 ----
        Rec _own;
        public Rec OwnRecord(Transform ownWrapper, int hp, int maxHp, string id = "own",
            string name = null, string sub = null, Texture2D portrait = null, bool roleIsTank = false)
        {
            if (_own == null || _own.Wrapper != ownWrapper)
                _own = new Rec { Wrapper = ownWrapper, IsOwn = true, Id = id, Name = name, Sub = sub, Portrait = portrait, RoleIsTank = roleIsTank };
            _own.MaxHp = Mathf.Max(1, maxHp);
            _own.Hp = Mathf.Max(0, hp);
            return _own;
        }

        // ---- picking — target.js:102-123, 152-165 ----

        /// <summary>target.js:106-112 — pointer-locked, the pick comes off the
        /// screen centre (wherever the lock caught the cursor); otherwise wherever
        /// the pointer is. Unity's own Input already hands over client-area screen
        /// coordinates (no DOM canvas-rect reconstruction needed — see class
        /// header on why that part of target.js has no Unity analogue).</summary>
        public static Ray ComputePickRay(Camera cam, Vector2 screenPos, bool pointerLocked)
        {
            Vector2 p = pointerLocked ? new Vector2(cam.pixelWidth * 0.5f, cam.pixelHeight * 0.5f) : screenPos;
            return cam.ScreenPointToRay(p);
        }

        /// <summary>target.js:157-165 `_recOf` — walks the hit's parent chain: a
        /// registered collider anywhere up the chain wins; failing that, the
        /// player's own plane root (if `ownRec` was built this frame via
        /// <see cref="OwnRecord"/>) wins; otherwise null.</summary>
        public Rec RecOf(Transform hit, Transform ownPlaneRoot, Rec ownRec)
        {
            for (Transform o = hit; o != null; o = o.parent)
            {
                var col = o.GetComponent<Collider>();
                if (col != null && _byCollider.TryGetValue(col, out var rec)) return rec;
                if (ownPlaneRoot != null && o == ownPlaneRoot) return ownRec;
            }
            return null;
        }

        /// <summary>target.js:102-123 pickAt, split at the raycast: hand this the
        /// RaycastHits your own broadphase already produced (any order — sorted
        /// here), within <see cref="PickRange"/>. Returns the record it landed on,
        /// or null (the caller carries on either way — a click on the sky is still
        /// a camera drag, same as the JS).</summary>
        public Rec ResolveHits(IReadOnlyList<RaycastHit> hits, Transform ownPlaneRoot, Rec ownRec)
        {
            if (hits == null || hits.Count == 0) return null;
            var sorted = new List<RaycastHit>(hits);
            sorted.Sort((a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in sorted)
            {
                if (h.distance > PickRange) continue;
                var rec = RecOf(h.transform, ownPlaneRoot, ownRec);
                if (rec != null) { Set(rec); return rec; }
            }
            return null;
        }

        /// <summary>target.js:127-147 cycle — Tab: the nearest machine roughly in
        /// front of the camera, and again for the next one out. Nothing in front
        /// means nothing targeted (as in WoW).</summary>
        public Rec Cycle(Vector3 eyePos, Vector3 eyeForward)
        {
            var scored = new List<(Rec rec, float d)>();
            Vector3 fwd = eyeForward.sqrMagnitude > 1e-8f ? eyeForward.normalized : Vector3.forward;
            foreach (var rec in _list)
            {
                if (rec.Wrapper == null || rec.Wrapper.parent == null) continue; // target.js:133 "not in the world (yet)"
                Vector3 wp = rec.Wrapper.position;
                float d = Vector3.Distance(wp, eyePos);
                if (d > CycleRange) continue;
                Vector3 dir = wp - eyePos;
                if (dir.sqrMagnitude < 1e-8f) continue;
                if (Vector3.Dot(dir.normalized, fwd) < CycleDot) continue;
                scored.Add((rec, d));
            }
            scored.Sort((a, b) => a.d.CompareTo(b.d));
            if (scored.Count == 0) { Clear(); return null; }
            int idx = scored.FindIndex(e => ReferenceEquals(e.rec, Current));
            var next = scored[(idx + 1) % scored.Count].rec;
            Set(next);
            return next;
        }

        // ---- the card, per frame — target.js:184-250 _paint (card data only;
        // the ring is BuildRingSnapshot below) ----

        /// <summary>target.js:300-306 update(dt)'s card half. Call once per frame;
        /// cheap when nothing changed (TargetCardView.Paint no-ops on an unchanged
        /// key, same as the JS).</summary>
        public void Tick(float dt, TargetCardView view, Vector3 observerPos)
        {
            if (_hitT > 0f) _hitT = Mathf.Max(0f, _hitT - dt);
            var rec = Current;
            if (rec == null) { view?.Hide(); return; }
            // target.js:304-305 — the machine went away (menu, fresh aeroplane).
            if (!rec.IsOwn && rec.Wrapper != null && rec.Wrapper.parent == null) { Clear(); view?.Hide(); return; }
            float dist = rec.Wrapper != null ? Vector3.Distance(rec.Wrapper.position, observerPos) : 0f;
            var snap = new TargetSnapshot
            {
                Id = rec.Id, Name = rec.Name, Sub = rec.Sub, Portrait = rec.Portrait,
                RoleIsTank = rec.RoleIsTank, IsOwn = rec.IsOwn,
                Hp = rec.Hp, MaxHp = rec.MaxHp, Dead = rec.Dead,
                DistanceM = dist, HitFlash = _hitT > 0f,
                StaminaPool = rec.MaxHp, TopSpeedKmh = rec.TopSpeedKmh, RamDamage = rec.RamDamage, MassKg = rec.MassKg,
            };
            view?.Show(snap);
            view?.Paint(snap);
        }

        /// <summary>target.js:309-321's ring half, reduced to the snapshot
        /// UI/Target/TargetRingView.Update wants. `floorProbe` is only ever
        /// called ONCE per record (cached on the Rec exactly like the JS's own
        /// `rec.ringY !== undefined` guard, target.js:286) — a parked machine's
        /// floor never moves.</summary>
        public RingSnapshot BuildRingSnapshot(Func<Vector3, float?> floorProbe)
        {
            var rec = Current;
            if (rec == null || rec.Wrapper == null) return new RingSnapshot { IsOwn = true }; // -> view hides
            bool fleetVisible = true;
            for (Transform o = rec.Wrapper; o != null; o = o.parent)
                if (!o.gameObject.activeInHierarchy) { fleetVisible = false; break; } // target.js:314 visibility walk
            if (!rec.IsOwn && fleetVisible && !rec.RingY.HasValue && floorProbe != null)
                rec.RingY = floorProbe(rec.Wrapper.position);
            return new RingSnapshot
            {
                IsOwn = rec.IsOwn,
                FleetVisible = fleetVisible,
                WorldPos = rec.Wrapper.position,
                HalfSpan = rec.HalfSpan,
                ProbedFloorY = rec.RingY,
                Dead = rec.Dead,
            };
        }

        // ---- damage — target.js:333-375 ----

        /// <summary>target.js:333-361 damagePlane. `ownHp`/`setOwnHp` read/write
        /// the hero's own live hull (target.js:344 `this.game.health`) when `rec`
        /// is the player's own machine — this file does not own that field.
        /// Shooting a machine IS targeting it (target.js:356): the bar that just
        /// moved is the bar that comes up.</summary>
        public Rec DamagePlane(Rec rec, float dmg, Func<int> ownHp = null, Action<int> setOwnHp = null, bool fx = true)
        {
            if (rec == null || rec.Dead) return null;
            if (rec.IsOwn)
            {
                int cur = ownHp != null ? ownHp() : rec.Hp;
                int nu = Mathf.Max(1, cur - Mathf.RoundToInt(dmg));
                setOwnHp?.Invoke(nu);
                rec.Hp = nu;
            }
            else
            {
                rec.Hp = Mathf.Max(0, Mathf.RoundToInt(rec.Hp - dmg));
            }
            _hitT = 0.3f; // target.js:349
            if (!ReferenceEquals(Current, rec)) Set(rec); // target.js:356 — being shot targets you
            if (!rec.IsOwn && rec.Hp <= 0) Kill(rec, fx);  // target.js:358
            return rec;
        }

        /// <summary>target.js:366-375 _kill — the airframe stays parked (a museum
        /// piece, not a flying machine that falls) but goes up; the card reads
        /// განადგურებული (handled by TargetCardView.Paint's Dead branch) from
        /// here on.</summary>
        void Kill(Rec rec, bool fx)
        {
            rec.Dead = true;
            if (!fx || rec.Wrapper == null) return;
            float r = Mathf.Max(4f, (rec.HalfSpan ?? 6f) * 0.8f);
            OnKillFx?.Invoke(rec.Wrapper.position, r);
        }

        /// <summary>target.js:377-386 dispose.</summary>
        public void Dispose()
        {
            Clear();
            _list.Clear();
            _byCollider.Clear();
        }
    }
}

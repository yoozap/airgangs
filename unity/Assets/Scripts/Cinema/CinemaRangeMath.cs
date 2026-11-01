using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Props;

namespace TbilisiFly.Cinema
{
    // =========================================================================
    // Port of the PURE-MATH slice of src/cinema.js's `_rangeStage` (cinema.js:
    // 1582-1643) and `_crateStage` (cinema.js:1655-1725) — narrowing
    // CinemaReel.cs's and CinemaTypes.cs's own "NOT PORTED: _rangeStage/
    // _crateStage, needs loaded scene geometry" notes to exactly the raycasts
    // themselves. Everything AROUND those raycasts — picking which lane is
    // "his" lane, clamping the measured distances into the stage's own
    // numbers, and (for the crates) turning three measured crate positions
    // into the row's whole frame — is plain float/Vector3 math and is ported
    // here, exercised with synthetic inputs, no lane/collider/GLB required.
    //
    // NOT PORTED, and why (unchanged from CinemaReel.cs's own boundary):
    //   * finding the lanes/booths/crate colliders themselves (scene.traverse,
    //     cinema.js:1584, 1658-1663) and every raycast against them
    //     (cinema.js:1595-1637, 1690, `_floorAt`) — City/'s loaded colliders,
    //     a caller's job, same as CraneHookFinder.cs's "a caller flattens the
    //     vertices first" boundary.
    //   * _warmCity, _loadProps/_buildStage/_teardown, the overlay DOM — all
    //     already called out as out of scope in CinemaReel.cs's header; not
    //     revisited here.
    //
    // ============================== HANDEDNESS ==============================
    // One hand-rolled "rotate 90°" vector, cinema.js:1687 `n = new Vector3
    // (-u.z, 0, u.x)` — the EXACT shape WarehouseCrane.RightOf() already
    // substitutes for (WarehouseCrane.cs's own header: "-nose.z, 0, nose.x"
    // -> RightOf(nose)). Reused verbatim as RightOf(u) below rather than
    // re-derived — the task brief's own warning ("a previous agent wrote
    // `-RightOf(away)` and found on cross-check that the sign was backwards")
    // is about exactly this mistake: do NOT negate it going in, only the
    // JS's OWN explicit `if (n.dot(fwd) > 0) n.negate()` conditional stays,
    // unchanged, as a second, separate step.
    // The `yawIn`/`yawOut`/`yawAlong` atan2 forms (cinema.js:1699-1701) need
    // NO handedness fix of their own: they are this project's established
    // "yaw from forward" inverse (`yaw = atan2(-fwd.x, -fwd.z)`, used
    // unchanged throughout this port, e.g. CinemaFlight.cs's own seat-facing
    // calculation) applied to vectors (`n`, `u`) that are ALREADY correctly
    // mirrored by the time they reach this formula — so the formula itself
    // is copied from the JS line for line.
    // =========================================================================
    public static class RangeStageMath
    {
        public const float Behind = 6.0f;      // m, cinema.js:73
        public const float BehindMin = 2.5f;   // m, cinema.js:74

        /// <summary>cinema.js:1586-1588 — nearest lane by its stand's XZ
        /// distance from `origin`, or (past 40 m) the fifth lane / the last
        /// one, whichever is fewer. `laneStands` must be non-empty.</summary>
        public static int ChooseLaneIndex(IReadOnlyList<Vector3> laneStands, Vector3 origin)
        {
            int best = 0;
            float bestD2 = Dist2(laneStands[0], origin);
            for (int i = 1; i < laneStands.Count; i++)
            {
                float d2 = Dist2(laneStands[i], origin);
                if (d2 < bestD2) { bestD2 = d2; best = i; }
            }
            if (Mathf.Sqrt(bestD2) > 40f) best = Mathf.Min(laneStands.Count - 1, 4);
            return best;
        }
        static float Dist2(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        /// <summary>cinema.js:1616 — how far back the open floor runs, before
        /// the booths start, clamped between BehindMin and Behind.</summary>
        public static float ComputeBehind(float clearDistance)
            => Mathf.Clamp(clearDistance - 4.2f, BehindMin, Behind);

        /// <summary>cinema.js:1632-1633 — the booth dividers' own depth
        /// (clamped) and the stage-z past which it is "the booths" (a body's
        /// width of margin short of the counter).</summary>
        public static (float wall, float cubicle) ComputeWallAndCubicle(float behind, float wallRaw)
        {
            float wall = Mathf.Clamp(wallRaw, 0.8f, 3.0f);
            return (wall, behind - wall - 0.4f);
        }

        /// <summary>cinema.js:1637-1639 — the dividers' own height (clamped;
        /// 2.2 m when no hit came back) and where the lane starts past the
        /// booths.</summary>
        public static (float wallTop, float counter) ComputeWallTopAndCounter(
            float behind, float? wallTopHitY, float floorY)
        {
            float raw = wallTopHitY.HasValue ? wallTopHitY.Value - floorY : 2.2f;
            return (Mathf.Clamp(raw, 1.2f, 3.5f), behind + 1.0f);
        }

        // =====================================================================
        // NEW (this pass): three more of CinemaReel.cs's small Tick-time
        // helpers, narrowed to their pure cores exactly the way the methods
        // above already narrow _rangeStage/_crateStage. None of these three
        // were claimed by any earlier unit.
        // =====================================================================

        /// <summary>cinema.js:1744-1754 `_laneAt(x)` — which lane's firing
        /// point sits at stage-x `x` (the booth two over, say), among the
        /// lanes that run the SAME way as the stage (`rail.dot(fwd) &gt;=
        /// 0.98`, cinema.js:1749). `acrossOffset[i]` is that lane's own
        /// `(stand[i] - s.lane.stand).dot(s.right)` — a caller's one dot
        /// product per lane (needs the live lane list, same seam as
        /// ChooseLaneIndex above); `railDotFwd[i]` is `lanes[i].rail.dot(s.
        /// fwd)`. Returns the matching index, or null exactly where the JS
        /// returns `null` (cinema.js:1746's `best = null`).</summary>
        public static int? NearestLaneAt(IReadOnlyList<float> acrossOffset,
            IReadOnlyList<float> railDotFwd, float x, float tolerance = 0.9f)
        {
            int? best = null;
            float bestD = tolerance;
            for (int i = 0; i < acrossOffset.Count; i++)
            {
                if (railDotFwd[i] < 0.98f) continue;
                float d = Mathf.Abs(acrossOffset[i] - x);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>cinema.js:1770-1777 `_runTo(lane, dist, at)`'s pure half:
        /// the clamp that picks the carrier's own rail offset (`lane.at`)
        /// when no explicit `at` override is given, and the distance from
        /// the firing line that offset puts the sheet at (`lane.range +
        /// lane.at`, the method's own return value). Moving `lane.obj`
        /// along `lane.rail` by the returned `at` is a caller's job (a live
        /// Transform), same seam as everywhere else in this file.</summary>
        public static float TrolleyRunTo(float dist, float range, float min, float max,
            float? atOverride, out float at)
        {
            at = atOverride ?? Mathf.Clamp(dist - range, min, max);
            return range + at;
        }

        /// <summary>cinema.js:1783-1795 `_aimPoint(t)` — where on the sheet
        /// the round is actually sent: the B-27's scored ring (`B27.cu`/
        /// `B27.cv`, papertargets.js) sits where a chest sits, not in the
        /// middle of the paper. `centre`/`rightAxis`/`upAxis` are the live
        /// mesh's own matrixWorld (cinema.js:1788-1790's `localToWorld`/
        /// `setFromMatrixColumn` reads — a caller's job, same seam as
        /// everywhere else here); `upAxis` need not be pre-flipped — this
        /// reproduces cinema.js:1791's `if (uy.y &lt; 0) uy.negate()` itself,
        /// so the sheet reads right-side-up however its carrier is rigged.
        /// NO caller falls back to the mesh's bare position when there is no
        /// scored card (cinema.js:1787) — that branch needs nothing but a
        /// position and is not worth a second method.
        ///
        /// HANDEDNESS: none needed. `centre`/`rightAxis`/`upAxis` are matrix
        /// COLUMN reads off an ALREADY-live (and therefore already correctly
        /// handed, whichever engine built it) Transform — not a hand-rolled
        /// cross product this file would have to swap operands on — and the
        /// combination below is pure scalar-weighted vector addition, which
        /// carries whatever handedness its three inputs already have.</summary>
        public static Vector3 AimPointOnCard(Vector3 centre, Vector3 rightAxis, Vector3 upAxis,
            float cardW, float cardH, float cu, float cv)
        {
            if (upAxis.y < 0f) upAxis = -upAxis;
            return centre
                + rightAxis.normalized * ((cu - 0.5f) * cardW)
                + upAxis.normalized * ((0.5f - cv) * cardH);
        }
    }

    // =========================================================================
    // FOUND UNPORTED in a later pass: `_buildStage`'s own decision logic over
    // one `SceneDef.Cards` entry (cinema.js:1818-1863, specifically the three
    // lines at cinema.js:1836-1837,1844) — which lane a card belongs to and
    // how far down it the sheet is run out to. CinemaTypes.CardSpec (this
    // pass) finally carries the DATA these decisions are made over; this
    // class carries the decisions themselves. Running the actual carrier
    // (`_runTo`'s Transform write) and picking a DIFFERENT lane
    // (`_laneAt`/NearestLaneAt above) both still need the live lane list —
    // unchanged, still a caller's job.
    // =========================================================================
    public static class CardPlacement
    {
        /// <summary>cinema.js:1836 `const off = c.at ?? c.x ?? 0;` — the
        /// across-the-booths offset this card is found at, whichever shape
        /// (range's `x` or crates' `at`) this particular entry uses.</summary>
        public static float AcrossOffset(CardSpec c) => c.At ?? c.X ?? 0f;

        /// <summary>cinema.js:1837's own condition, `(!c.at && !c.x ? s.lane
        /// : this._laneAt(off))` — true when this card has neither `at` nor
        /// `x` set, meaning it is HIS own lane (the range scene's implicit
        /// default) rather than one found by `NearestLaneAt(AcrossOffset(c))`.
        /// Split out so a caller never has to re-derive the JS's own
        /// double-negative.</summary>
        public static bool UsesOwnLane(CardSpec c) => !c.At.HasValue && !c.X.HasValue;

        /// <summary>cinema.js:1844 `c.dist ?? (c.z - s.behind)` — how far down
        /// the lane, from the FIRING LINE, this card's sheet is run out to.</summary>
        public static float Distance(CardSpec c, float behindStageOrigin)
            => c.Dist ?? ((c.Z ?? 0f) - behindStageOrigin);
    }

    // =========================================================================
    // Port of src/cinema.js:1655-1725 `_crateStage`'s own ROW-FRAME math —
    // everything after the three ammo-crate shells' world positions are
    // already in hand (`CraneHookFinder.cs`'s own boundary: a caller supplies
    // already-measured points, not a scene.traverse). See file header for
    // the HANDEDNESS note on `RightOf(u)`.
    // =========================================================================
    public static class CrateRowBuilder
    {
        public const float CornerIn = 0.55f;          // S2_CORNER_IN, cinema.js:249
        public static readonly Vector2 StartAB = new Vector2(1.3f, 2.9f); // S2_START, cinema.js:248

        /// <summary>Everything `_crateStage` already has measured BEFORE it
        /// needs a floor raycast (cinema.js:1655-1690): the three crate
        /// shells' world positions (`one`/`two`/`top`'s matrixWorld origins —
        /// `c1`,`c2`,`c3`), the first crate's own height and depth (measured
        /// off its Box3/geometry — asset-dependent, a caller's number), and
        /// the RANGE stage's own frame (from `_rangeStage(c3)`, already
        /// built by the time `_crateStage` calls this) that the row sits
        /// inside and measures its across-the-booths offsets against.</summary>
        public struct Input
        {
            public Vector3 C1, C2, C3;
            public float CrateHeight;   // crane.js-style measured constant: box size.y of `one`
            public float Depth;         // measured: matrixWorld column-2 length * geometry.parameters.depth
            public Vector3 StageFwd, StageRight;   // s.fwd / s.right, from _rangeStage(c3)
            public Vector3 LaneStand;              // s.lane.stand
            /// <summary>cinema.js:1690 `this._floorAt(o.x, o.z, c1.y+1.5) ??
            /// c1.y` — a seam, exactly like WindRider.SkinAt/PlaneSling's
            /// underRay: a caller supplies the real raycast, called here
            /// with the row origin's OWN x/z (which this method computes
            /// first, same order the JS does it in) and `c1.y + 1.5` as the
            /// cast's start height. Null (no FloorAt at all, or no hit)
            /// falls back to `c1.y`, exactly as `?? c1.y` does.</summary>
            public System.Func<float, float, float, float?> FloorAt;
        }

        public struct Output
        {
            public RowFrame Row;
            /// <summary>cinema.js:1715 `shift` — how far the stage origin
            /// moved backward once the row became the stage's origin; the
            /// caller subtracts this from the range stage's own Cubicle/
            /// Counter (cinema.js:1716-1717) exactly as `_crateStage` does.</summary>
            public float Shift;
        }

        /// <summary>cinema.js:1680-1719, in full except the floor raycast
        /// itself (see Input.FloorAt). `oldStageO` is the range stage's own
        /// origin BEFORE the row replaces it (`s.o` going into
        /// `_crateStage`, cinema.js:1715's `s.o` read before the
        /// reassignment three lines later).</summary>
        public static Output Compute(in Input inp, Vector3 oldStageO)
        {
            Vector3 u = inp.C2 - inp.C1; u.y = 0f; u.Normalize();            // cinema.js:1681
            float cl = Vector3.Distance(inp.C1, inp.C2);                     // cinema.js:1682, one crate end to end
            float len = 2f * cl;                                            // cinema.js:1683, the row is two of them
            float half = cl * 0.5f;                                         // cinema.js:1684

            // cinema.js:1687-1688 — see class/file header for why this is
            // RightOf(u), not a re-derived or re-negated formula.
            Vector3 n = WarehouseCrane.RightOf(u);
            if (Vector3.Dot(n, inp.StageFwd) > 0f) n = -n;                   // AWAY from the range

            Vector3 o = inp.C1 - u * half + n * (inp.Depth * 0.5f);         // cinema.js:1689
            float? floorY = inp.FloorAt?.Invoke(o.x, o.z, inp.C1.y + 1.5f);  // cinema.js:1690
            o.y = floorY ?? inp.C1.y;

            var row = new RowFrame
            {
                O = o, U = u, N = n,
                Len = len, Depth = inp.Depth, CrateH = inp.CrateHeight,
                Lid = inp.CrateHeight,                                       // cinema.js:1693
                Top = (inp.C3.y - o.y) + inp.CrateHeight,                    // cinema.js:1694
                Stack0 = Vector3.Dot(inp.C3 - o, u) - half,                  // cinema.js:1695
                YawIn = Mathf.Atan2(n.x, n.z),                               // cinema.js:1699
                YawOut = Mathf.Atan2(-n.x, -n.z),                            // cinema.js:1700
                YawAlong = Mathf.Atan2(-u.x, -u.z),                          // cinema.js:1701
            };

            // cinema.js:1708-1713 — the far corner (where the cover-slam
            // lands) and the shoot-from spot up on the first crate, both as
            // across-the-booths offsets from HIS lane.
            Vector3 corner = row.At(len - CornerIn, 0f, 0f);
            row.CardX = Vector3.Dot(corner - inp.LaneStand, inp.StageRight);
            Vector3 shootFrom = row.At(StartAB.x, 0f, 0f);
            row.BoxX = Vector3.Dot(shootFrom - inp.LaneStand, inp.StageRight);

            float shift = Vector3.Dot(o - oldStageO, inp.StageFwd);         // cinema.js:1715
            return new Output { Row = row, Shift = shift };
        }

        /// <summary>cinema.js:1716-1720 — the final assembly step, folding
        /// Compute's Output back onto the range stage it replaces: the
        /// booths' own Cubicle/Counter move back by Shift (the row's origin
        /// is further from the firing line than the range stage's plain
        /// origin was), and Stage.O/Y/Row become the row's own. FOUND
        /// UNPORTED in a later pass: Compute above already returns Shift
        /// (cinema.js:1715 itself), but nothing applied it — this is plain
        /// struct-field arithmetic, no scene dependency at all, unlike
        /// everything else `_crateStage` needs a live scene for.</summary>
        public static void Apply(ref StageSetup stage, Output output)
        {
            stage.Cubicle -= output.Shift;
            stage.Counter -= output.Shift;
            stage.O = output.Row.O;
            stage.Y = output.Row.O.y;
            stage.Row = output.Row;
        }
    }
}

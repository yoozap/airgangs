using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/airport.js:861-906 — the lane gate / paper-sheet swing,
    /// driven every frame from the game loop with the PERSONAGE's own
    /// position (never the camera's — the chase camera trails 6 m behind and
    /// would open a gate two lanes early, airport.js's own comment at :862-864).
    ///
    /// PURE MATH CLASS, same shape as Combat/RangeLane.cs's own RangeLanes —
    /// no MonoBehaviour, no Update(): a caller (the game-loop orchestration
    /// file, outside this unit's scope) is expected to build one <see
    /// cref="Door"/> per "range_gate" pivot and one <see cref="Paper"/> per
    /// "range_paper" pivot that AirportDetail.BuildGatePivots/BuildPaperPivots
    /// create, then call <see cref="Tick"/> once per frame with the
    /// personage's world position. This is the documented integration seam —
    /// nothing wires it yet, matching this port's own established convention
    /// for hooks nobody has built the host for (see GameLoop.cs's own header,
    /// "WHAT IS STUBBED").
    ///
    /// SWAY COMPOSITION (airport.js:895-900): the carrier's own swing is
    /// worked out in src/rangelane.js (RangeLanes.cs's `Sway`, Combat-owned)
    /// and written onto the SAME pivot the JS shares via `userData.sway`; this
    /// file's `Tick` ADDS whatever the caller reports through `swayOf`, rather
    /// than fighting over the one rotation — exactly the JS's own "composed
    /// rather than fought over: two writers on one rotation means the last one
    /// each frame wins and the other may as well not exist." `swayOf` may be
    /// null (airport.js's `?? 0`), in which case the sheet still swings from a
    /// shove, it just never fights a moving carrier.
    ///
    /// HANDEDNESS: every quantity here is a LOCAL hinge rotation about the
    /// pivot's own Y (gate) or X (paper) axis, and `At` is a measured world
    /// position used only for a DISTANCE test (`sqrMagnitude`/`Abs`) — no
    /// cross product anywhere, so nothing needs the project's operand swap.
    /// </summary>
    public static class RangeDoorsPaper
    {
        public const float GateOpenRad = 1.35f;  // airport.js:865 GATE_OPEN
        public const float GateNearM = 2.0f;      // airport.js:866 GATE_NEAR
        public const float PaperReachM = 0.75f;   // airport.js:871 PAPER_REACH
        public const float PaperSwingRad = 1.0f;  // airport.js:872 PAPER_SWING

        public sealed class Door
        {
            public Transform Pivot;
            public Vector3 At;   // captured once, airport.js's doors never move
            public float T;
        }

        public sealed class Paper
        {
            public Transform Pivot;
            public Vector3 At;   // re-measured every tick — the carrier moves
            public float T;
        }

        /// <summary>airport.js:855-856 — `at` captured once, from the pivot's
        /// current world position, at the moment it is adopted.</summary>
        public static Door MakeDoor(Transform pivot) => new Door { Pivot = pivot, At = pivot.position };

        /// <summary>Same capture for a paper pivot (airport.js:855-856 covers
        /// both — doors and sheets are pushed from the same `rec` shape).</summary>
        public static Paper MakePaper(Transform pivot) => new Paper { Pivot = pivot, At = pivot.position };

        /// <summary>
        /// airport.js:873-906, run once per frame. `actor` is the personage's
        /// world position (game.js's own `actor` — a plain point, not a
        /// Transform, matching the JS). `swayOf`, if given, returns the
        /// current carrier sway (radians) for a paper's pivot — see class
        /// header.
        /// </summary>
        public static void Tick(IReadOnlyList<Door> doors, IReadOnlyList<Paper> sheets,
            Vector3 actor, float dt, System.Func<Transform, float> swayOf = null)
        {
            if (doors != null)
            {
                foreach (var d in doors)
                {
                    float want = (d.At - actor).sqrMagnitude < GateNearM * GateNearM ? 1f : 0f; // airport.js:875
                    if (want == d.T) continue;
                    d.T += (want - d.T) * Mathf.Min(1f, 7f * dt); // airport.js:877
                    if (Mathf.Abs(want - d.T) < 0.002f) d.T = want; // airport.js:878
                    d.Pivot.localRotation = Quaternion.Euler(0f, -GateOpenRad * d.T * Mathf.Rad2Deg, 0f); // airport.js:879
                }
            }
            if (sheets == null) return;
            foreach (var p in sheets)
            {
                // airport.js:887-888 — WHERE THE SHEET IS NOW, not where it was
                // built: it rides a carrier that can be eleven metres down the
                // lane by the time someone touches it.
                p.At = p.Pivot.position;
                float dist = Mathf.Sqrt((p.At.x - actor.x) * (p.At.x - actor.x) + (p.At.z - actor.z) * (p.At.z - actor.z)); // airport.js:889
                float want = 0f;
                if (dist < PaperReachM) // airport.js:891
                    want = ((PaperReachM - dist) / PaperReachM) * (actor.z < p.At.z ? -1f : 1f); // airport.js:893

                float sway = swayOf != null ? swayOf(p.Pivot) : 0f; // airport.js:900 `?? 0`
                // airport.js:901 — nothing to do and nothing already applied.
                // The JS also tests `p.pivot.rotation.x === 0` there as a belt-
                // and-suspenders check against an externally-rotated pivot;
                // skipped here because this class is the SOLE writer of
                // Pivot.localRotation once Tick runs (set only a few lines
                // below), so p.T/sway alone already capture that state exactly.
                if (want == 0f && p.T == 0f && sway == 0f) continue;

                p.T += (want - p.T) * Mathf.Min(1f, 9f * dt); // airport.js:902
                if (want == 0f && Mathf.Abs(p.T) < 0.004f) p.T = 0f; // airport.js:903
                float xRad = PaperSwingRad * p.T + sway; // airport.js:904
                p.Pivot.localRotation = Quaternion.Euler(xRad * Mathf.Rad2Deg, 0f, 0f); // airport.js:904, THREE's default XYZ order == a pure local-X rotation here
            }
        }
    }
}

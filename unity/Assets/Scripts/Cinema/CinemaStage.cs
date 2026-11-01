using UnityEngine;

namespace TbilisiFly.Cinema
{
    // =========================================================================
    // Port of src/cinema.js's `_stageApi()` (cinema.js:1330-1443) — "the stage
    // frame, handed to every shot in the cut list." Every SHOTS1/SHOTS2/
    // FLIGHT_SHOTS `cam`/`look` closure is written against exactly these
    // methods (`S.p`, `S.actor`, `S.card`, ...), so this class is a direct,
    // literal translation — the method names below ARE the JS method names.
    //
    // ============================== HANDEDNESS ==============================
    // Three call sites build a "right-angled off a forward vector" offset by
    // hand rather than through a named cross product:
    //   cinema.js:1234  `right: new Vector3(-rwFwd.z, 0, rwFwd.x)`   (_flightStage)
    //   cinema.js:1376  `new Vector3(-r.fwd.z, 0, r.fwd.x)`          (ground())
    //   cinema.js:1385  `new Vector3(-fwd.z, 0, fwd.x)`              (mark())
    // Each is algebraically `cross(forward, worldUp)` in Three's right-handed
    // frame. Per FlightModel.cs's rule, a cross product's PHYSICAL result
    // mirrors between Three's frame and Unity's left-handed one for the SAME
    // input vectors, so producing the same physical "right" here needs the
    // operands swapped: `cross(worldUp, forward)` — which, worked out
    // component-wise for forward=(fx,0,fz), up=(0,1,0), is simply the NEGATION
    // of the JS formula: `(fwd.z, 0, -fwd.x)`. Verified by hand against a
    // concrete case (RightOfForward's own doc comment) rather than taken on
    // faith — see that method.
    // =========================================================================
    public sealed class StageFrame
    {
        readonly CinemaDirector _d;
        public StageFrame(CinemaDirector d) { _d = d; }

        /// <summary>The one derived formula this port's cinema files need.
        /// WORKED EXAMPLE (yaw=0 under this project's forward-from-yaw
        /// convention, `fwd = (-sin(yaw), 0, -cos(yaw))`, unchanged from the
        /// JS — see FootController.cs's own use of the identical formula):
        /// fwd(0) = (0,0,-1). Facing -Z (south, in this project's compass),
        /// your anatomical right hand points toward GeoFrame's +X (west) —
        /// the game's own X=west convention survives the port unmirrored in
        /// MEANING, only the stored sign flips. The JS formula
        /// `(-fwd.z,0,fwd.x)` evaluated at fwd=(0,0,-1) gives (1,0,0) = +X =
        /// west = right: correct IN THE GAME FRAME. Evaluated on the SAME
        /// fwd vector reused unmodified in Unity's mirrored frame (where +X
        /// is now EAST), that same formula gives +X = EAST = wrong (your
        /// right hand facing south is toward the WEST, i.e. Unity's -X).
        /// The negated formula below gives (-1,0,0) = -X = west = correct.</summary>
        public static Vector3 RightOfForward(Vector3 fwd) => new Vector3(fwd.z, 0f, -fwd.x);

        // ---- src/cinema.js:1330-1339 P (the stage's own x-right/y-up/z-fwd frame) ----
        public Vector3 P(float x, float y, float z)
        {
            var s = _d.Stage;
            return s.O + s.Right * x + s.Fwd * z + Vector3.up * (s.Y + y);
        }

        // ---- src/cinema.js:1394-1400 actor (carried by the hero) ----
        public Vector3 Actor(float x, float y, float z)
        {
            var s = _d.Stage;
            Vector3 h = _d.Actor.Position;
            return new Vector3(h.x, h.y + y, h.z) + s.Right * x + s.Fwd * z;
        }

        // ---- src/cinema.js:1411-1417 orbit: r metres from the hero at stage
        // angle a (0 = down range, + = to his right), y above his feet ----
        public Vector3 Orbit(float a, float r, float y)
        {
            var s = _d.Stage;
            Vector3 h = _d.Actor.Position;
            return new Vector3(h.x, h.y + y, h.z)
                + s.Fwd * (Mathf.Cos(a) * r) + s.Right * (Mathf.Sin(a) * r);
        }

        // ---- src/cinema.js:1418-1425 card ----
        public Vector3 Card(int i, float x, float y, float z)
        {
            var targets = _d.Targets;
            CardTarget t = (targets != null && targets.Count > 0)
                ? targets[Mathf.Clamp(i, 0, targets.Count - 1)] : default;
            var s = _d.Stage;
            return t.AimAt + s.Right * x + s.Fwd * z + Vector3.up * y;
        }

        // ---- src/cinema.js:1426-1429 round: the live bullet, while one is in the air ----
        public Vector3? Round() => _d.RoundProvider != null ? _d.RoundProvider() : (Vector3?)null;

        // ---- src/cinema.js:1430-1440 hip: the holster on his belt ----
        public Vector3 Hip()
        {
            if (_d.HipProvider != null) return _d.HipProvider();
            var s = _d.Stage;
            Vector3 h = _d.Actor.Position;
            return new Vector3(h.x, h.y + 0.9f, h.z) + s.Right * 0.2f;
        }

        // ---- src/cinema.js:1403 row: the crate row's own frame (scene 2) ----
        public Vector3 Row(float a, float b, float y)
        {
            var r = _d.Stage.Row ?? default;
            return r.At(a, b, y);
        }
        public Vector3 RowU() => (_d.Stage.Row ?? default).U;
        public float HeroA()
        {
            var r = _d.Stage.Row ?? default;
            return Vector3.Dot(_d.Actor.Position - r.O, r.U);
        }

        public Vector3 Mix(Vector3 a, Vector3 b, float k) => Vector3.Lerp(a, b, k);

        // ========================= scene 3 (the flypast) ========================
        // src/cinema.js:1343-1393. Everything is written in the AEROPLANE's own
        // space: +x right wing, +y up, -z nose (THREE convention; this port's
        // IReelPlane.LocalToWorld carries the same local-space meaning as the
        // Unity transform it wraps — local forward is +Z there, see
        // FlightModel.cs's header, so "forward of the nose" below is -z exactly
        // as the JS writes it: the LOCAL AXIS LABELS did not change, only the
        // Transform that interprets them).
        public Vector3 Plane(float x, float y, float z) => _d.Plane?.LocalToWorld(new Vector3(x, y, z)) ?? Vector3.zero;
        public float Span() => _d.Plane?.HalfSpan ?? 5f;
        public Vector3 FwdVec() => _d.Plane != null ? _d.Plane.Rotation * Vector3.forward : Vector3.forward;
        public Vector3 Seat(float x, float y, float z)
        {
            Vector3 o = _d.Plane != null && _d.Plane.HasCockpit ? _d.Plane.SeatOffsetLocal : new Vector3(0f, 0.6f, 0f);
            return _d.Plane?.LocalToWorld(new Vector3(o.x + x, o.y + y, o.z + z)) ?? Vector3.zero;
        }
        public Vector3 Ahead(float d, float y = 0f) => _d.Plane?.LocalToWorld(new Vector3(0f, y, -d)) ?? Vector3.zero;

        /// <summary>src/cinema.js:1363-1367 nose — simplified to a measured
        /// constant (IReelPlane.NoseHalfLength) instead of a per-call Box3:
        /// the model's own extent does not change frame to frame, so
        /// recomputing it every shot is pure waste, not behaviour. See
        /// CinemaTypes.cs's IReelPlane doc comment.</summary>
        public Vector3 Nose(float x, float y, float z)
        {
            float half = _d.Plane?.NoseHalfLength ?? 2.5f;
            return _d.Plane?.LocalToWorld(new Vector3(x, y, -half - z)) ?? Vector3.zero;
        }

        /// <summary>src/cinema.js:1372-1377 ground — a point PLANTED ON THE
        /// GROUND the aeroplane goes past; it does not move. HANDEDNESS: see
        /// this class's header — RightOfForward replaces the JS's hand-rolled
        /// `(-fwd.z,0,fwd.x)`.</summary>
        public Vector3 Ground(float x, float y, float z)
        {
            var r = _d.FlightStage.RunwayAt;
            var fwd = _d.FlightStage.RunwayFwd;
            return r + Vector3.up * y + fwd * z + RightOfForward(fwd) * x;
        }

        /// <summary>src/cinema.js:1378-1387 mark — a point fixed in the WORLD,
        /// off the line the aeroplane flies, from the stage origin. Same
        /// handedness fix as Ground().</summary>
        public Vector3 Mark(float x, float y, float z)
        {
            var f = _d.FlightState;
            Vector3 fwd = new Vector3(-Mathf.Sin(f.Yaw), 0f, -Mathf.Cos(f.Yaw));
            return new Vector3(f.Pos.x, f.Y, f.Pos.z) + fwd * z + RightOfForward(fwd) * x + Vector3.up * y;
        }

        /// <summary>src/cinema.js:1390-1393 below — straight down from the
        /// aeroplane, d metres.</summary>
        public Vector3 Below(float d)
        {
            Vector3 p = _d.Plane != null ? _d.Plane.Position : Vector3.zero;
            return new Vector3(p.x, p.y - d, p.z);
        }
    }
}

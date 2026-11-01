using System;
using UnityEngine;

namespace TbilisiFly.Cinema
{
    // =========================================================================
    // Port of src/cinema.js's supporting data shapes — "THE REEL" (cinema.js:
    // 1-55 is the full design note: a scripted camera cut-list playing over the
    // game's OWN locomotion/shooting/climbing, not a separate animation).
    //
    // SCOPE OF THIS PORT (see CinemaReel.cs's class header for the full
    // breakdown). This file holds the small data types CinemaReel.cs's director
    // and camera math are built from, split out only because CinemaReel.cs is
    // already long.
    // =========================================================================

    /// <summary>Which keys the reel is "holding" this frame — cinema.js's
    /// `hold(...codes)` (cinema.js:795-799), which clears every key and holds
    /// exactly the ones named. A caller's input-reader ORs this onto whatever
    /// it would normally produce, the same way game.js's `keys` Set is read by
    /// everything downstream of it (FootController.MoveInput, mainly).</summary>
    [Flags]
    public enum ReelKeys
    {
        None = 0,
        W = 1 << 0,
        A = 1 << 1,
        S = 1 << 2,
        D = 1 << 3,
        Shift = 1 << 4,
        Ctrl = 1 << 5,
        Space = 1 << 6,
    }

    /// <summary>The narrow surface CinemaReel.cs needs from the hero's own
    /// on-foot/shooting state. A real integrator (GameLoop, once cinema is
    /// wired in — see CinemaReel.cs's header) implements this over
    /// FootController + CharacterRig + Pistol/Weapons; a probe implements it
    /// over a plain fake. Kept deliberately narrow: this port does not reach
    /// into Character/ or Combat/ internals (hard rule), it only calls through
    /// here.</summary>
    public interface IReelActor
    {
        Vector3 Position { get; }          // foot.pos
        float Yaw { get; }                 // foot.yaw — facing
        float CamYaw { get; set; }         // foot.camYaw — what the body's own A/D turn chases
        bool Grounded { get; }
        bool HasWeaponDrawn { get; }        // foot.held != null
        bool Climbing { get; }              // foot._climb
        bool Flipping { get; }              // foot._flip
        bool InCover { get; }

        void SetKeys(ReelKeys keys);         // game.keys.clear() + add(...) — cinema.js hold()
        void SetLookYawRate(float radPerSec); // g.look.yaw — servo turn while a weapon is held
        void SetLookPitch(float rad);         // g.look.pitch
        void SetDirectorAim(Vector3? worldDir, Vector3? worldPoint); // cinema.aim/aimPoint —
                                                                       // shooting.js reads THIS instead
                                                                       // of the crosshair while the reel runs
        void Draw(bool snap);                // foot.drawWeapon + optional _finishDraw — cinema.js draw()
        bool Fire();                         // pistol.fire(), cooldown/bloom pre-zeroed — cinema.js fire()
        void Roll(float targetYaw);          // foot._startRoll at an absolute yaw — cinema.js roll()
        void Slide();                        // foot._startSlide — cinema.js slide()
        bool TryClimb();                     // foot._tryClimb() — cinema.js climb()
        void StartFlip(float fallMeters, float yaw); // foot._startFlip — cinema.js flip()
        bool ToggleCover();                  // foot.toggleCover() — cinema.js cover()
        void SetAiming(bool on);             // pistol.setAiming — cinema.js sight()
        bool Reload();                       // pistol.ammo=0; pistol.reload() — cinema.js reload()
        void ArmRollLand();                  // foot._rollBuf = 0.3 — the AC-style landing roll buffer
        bool StartRollIfNotRolling(float yaw);
    }

    /// <summary>The narrow surface CinemaReel.cs needs from the aeroplane, for
    /// scene 3 (the flypast). A real integrator wraps Flight.PlaneController's
    /// Transform + cockpit data; this port does not reach into Flight/
    /// internals directly (hard rule).</summary>
    public interface IReelPlane
    {
        Vector3 Position { get; set; }
        Quaternion Rotation { get; set; }
        Vector3 LocalToWorld(Vector3 local);
        float HalfSpan { get; }             // plane.userData.halfSpan, cinema.js:1347
        float NoseHalfLength { get; }        // half the model's own Z extent — cinema.js:1363-1367's
                                              // per-call Box3 collapses to this one measured constant
        bool HasCockpit { get; }
        Vector3 SeatOffsetLocal { get; }     // cockpit.seat (default 0,0.6,0) — cinema.js:1354-1358
        float CockpitHalfWidth { get; }      // cockpit.halfW, default 0.7 — cinema.js:1509
        float CockpitDeck { get; }           // cockpit.deck, default 0.8 — cinema.js:1510
        void SetVisible(bool v);
    }

    /// <summary>One sheet of paper the reel shoots at — cinema.js's `_targets`
    /// entries (cinema.js:1846-1850). `AimAt` is the X-ring point on the card's
    /// own swinging/rail-mounted frame, refreshed every steer tick
    /// (cinema.js:1783-1795 `_aimPoint`) by whatever owns the real sheet
    /// (PaperTargets.cs/RangeLane.cs, outside this port's scope — a caller
    /// refreshes CardTarget.AimAt each frame and hands the array in).</summary>
    public struct CardTarget
    {
        public Vector3 AimAt;
    }

    /// <summary>One entry of a SceneDef's `Cards` — cinema.js's CARDS1 entries
    /// (`{x,z}`, cinema.js:62-66) and the crates scene's `cards` function
    /// (`{at,dist}`, cinema.js:534). FOUND UNPORTED in a later pass: unlike
    /// every other scripted constant in this file (BEATS/SHOTS), this data
    /// was never carried into the C# port at all — `SceneDef` had no `Cards`
    /// field, even though `CinemaRangeMath.NearestLaneAt`/`TrolleyRunTo`
    /// (crane4.json) already port the exact math this data feeds. Both
    /// shapes are unified into one struct: `X`/`Z` is the range scene's own
    /// (an offset from HIS lane, a distance from the stage origin); `At`/
    /// `Dist` is the crates scene's (an absolute across-the-booths offset —
    /// `s.row.boxX`/`s.row.cardX`, CrateRowBuilder's own output — and a
    /// distance from the FIRING LINE, not the stage origin). See
    /// CinemaRangeMath.CardPlacement for cinema.js:1836-1844's own decision
    /// logic over these two shapes.</summary>
    public struct CardSpec
    {
        public float? X, Z, At, Dist;
        public CardSpec(float x, float z) { X = x; Z = z; At = null; Dist = null; }
        public static CardSpec FromAt(float at, float dist) => new CardSpec { At = at, Dist = dist };
    }

    /// <summary>The crate row's own frame — cinema.js:1691-1707's `row` object,
    /// built by `_crateStage` (cinema.js:1655-1725) from the three ammo-crate
    /// colliders' measured positions. The pure math that turns three measured
    /// crate positions into this struct is NOW built here — see
    /// Cinema/CinemaRangeMath.cs's `CrateRowBuilder.Compute`; only measuring
    /// the three crates themselves (scene.traverse for their colliders) and
    /// the one floor raycast inside it stay a caller's job (CrateRowBuilder.
    /// Input.FloorAt is that one remaining seam).</summary>
    public struct RowFrame
    {
        public Vector3 O, U, N;     // origin; along the row (toward the tower); out of the hide face
        public float Len, Depth, CrateH;
        public float Lid;           // the first crate's top, cinema.js:1693
        public float Top;           // the tower's top, cinema.js:1694
        public float Stack0;        // where the tower starts along the row, cinema.js:1695
        public float YawIn, YawOut, YawAlong;
        public float CardX, BoxX;   // across-the-booths offsets for the two cards this scene shoots at

        public Vector3 At(float a, float b, float y) => O + U * a + N * b + Vector3.up * y;
    }

    /// <summary>The range/crates stage frame — cinema.js's `_stage` object for
    /// the non-flight scenes (built by `_rangeStage`/`_crateStage`,
    /// cinema.js:1582-1725). Measured from the real range/crates at runtime by
    /// a caller (raycasts against loaded colliders — out of this port's scope,
    /// same reasoning as ShedLayout.cs/PlaneHallLayout.cs); this struct is the
    /// pure, already-measured result the camera math (StageFrame, below) reads.</summary>
    public struct StageSetup
    {
        public Vector3 O, Fwd, Right;
        public float Y;
        public float Yaw;          // cinema.js _rangeStage's own `yaw` — frame-one facing (SceneDef.Place)
        public float Behind, Wall, WallTop, Cubicle, Counter, Pitch;
        public bool HasCubicle;    // false only for the flight scene, which has no booths
        public RowFrame? Row;      // set only for the 'crates' scene
    }

    /// <summary>The flight scene's two stands — the real runway, and a fixed
    /// point out over town — cinema.js's `_flightStage` (cinema.js:1221-1244).
    /// HANDEDNESS: `Right` here is the one hand-derived "rotate 90°" vector in
    /// this port that needs the cross-product swap — see CinemaReel.cs's
    /// RightOfForward() and its header.</summary>
    public struct FlightStageSetup
    {
        public Vector3 RunwayAt, RunwayFwd, RunwayRight;
        public float RunwayYaw;
        public float RunwayGroundY;
        public Vector3 CityAt, CityFwd;
        public float CityYaw;
        public float CityBaseY;
    }

    /// <summary>One frame of the scripted flypast — cinema.js's `st.fly`
    /// object, stepped by `_flyStep` (cinema.js:1250-1328). Ported in
    /// CinemaFlight.cs.</summary>
    public struct FlightReelState
    {
        public Vector3 Pos;
        public float Yaw, Y, Roll, Pitch, V, Rpm;
        public bool Air, InCity;
    }

    /// <summary>One shot in the cut list — cinema.js's SHOTS1/SHOTS2/
    /// FLIGHT_SHOTS entries (cinema.js:136-213, 314-403, 457-519). `Cam`/`Look`
    /// are pure functions of (k in 0..1, the stage frame) — exactly the JS
    /// closures, just not closing over `this` directly.</summary>
    public struct Shot
    {
        public float T, D;              // start time, duration, REAL seconds
        public Vector2 Fov;              // degrees, vertical — same convention as THREE.PerspectiveCamera.fov
        public Vector2 Roll;             // radians, dutch angle at k=0 / k=1
        public float Shake;
        public Func<float, StageFrame, Vector3> Cam;
        public Func<float, StageFrame, Vector3> Look;
        public Func<float, StageFrame, Vector3> Up; // null = world up (cinema.js:1137-1138)
    }

    /// <summary>One scripted beat — cinema.js's BEATS1/BEATS2 entries
    /// (cinema.js:89-126, 252-309). `Go` is the beat's `go(c)` closure, written
    /// against the director directly (cinema.js's own `c` IS the Cinema
    /// instance) so every beat below reads exactly like its JS original.</summary>
    public struct Beat
    {
        public float T;
        public Action<CinemaDirector> Go;
        public Beat(float t, Action<CinemaDirector> go) { T = t; Go = go; }
    }

    /// <summary>One of SCENES (cinema.js:525-542).</summary>
    public sealed class SceneDef
    {
        public string Id, Name;
        public float Dur;
        public string Gun;           // weapon id, or null (the flypast carries no weapon)
        public bool Flight;
        public Beat[] Beats;
        public Shot[] Shots;
        /// <summary>cinema.js's `place(c,s)` — frame one's mark, yaw and
        /// starting speed (cinema.js:530, 536). Not used for the flight scene
        /// (it has its own `_reset` path, CinemaReel.cs `ResetFlight`).</summary>
        public Func<CinemaDirector, StageSetup, (Vector3 at, float yaw, float speed)> Place;
        /// <summary>cinema.js's own `cards` (CARDS1 for 'range', a `(s)=>...`
        /// function for 'crates', `[]` for 'flight' — cinema.js:528, 534,
        /// 541). See CardSpec's own doc comment for why this was missing.
        /// A function of the StageSetup even for the range scene (which
        /// ignores it) for symmetry with Place above, and because the
        /// crates scene genuinely needs `s.Row`'s BoxX/CardX.</summary>
        public Func<StageSetup, CardSpec[]> Cards;
    }
}

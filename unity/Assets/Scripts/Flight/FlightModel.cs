using UnityEngine;

namespace TbilisiFly.Flight
{
    /// <summary>Stick state. Mirrors the JS `stick` object. throttle &lt; 0 is the air brake.</summary>
    public struct StickInput
    {
        public float Pitch, Roll, Rudder, Throttle;
    }

    /// <summary>
    /// Per-part integrity, 0..1. Mirrors the JS `game.struct`. A wing at 0 is GONE.
    /// </summary>
    public struct Airframe
    {
        public float WingL, WingR, Tail;
        public static Airframe Intact => new Airframe { WingL = 1f, WingR = 1f, Tail = 1f };
    }

    /// <summary>The fields aero.js reads off a plane definition (src/planes.js).
    /// Named AeroPlaneDef (not PlaneDef) to avoid colliding with the full plane-catalog
    /// row of the same name in PlaneCatalog.cs — this is only the narrow subset aero.js
    /// touches, not the whole PLANES table entry.</summary>
    public struct AeroPlaneDef
    {
        public float Mass;       // 0 = derive from size, as the JS does
        public bool  Prop;       // propeller rather than jet
        public float ClMax;      // 0 -> 1.5
        public float GBreak;     // 0 -> 9
        public float Tw;         // thrust-to-weight
        public float MaxSpeed;
    }

    /// <summary>
    /// Port of src/aero.js. The plane's motion is a world-space velocity VECTOR —
    /// the nose and the flight path are separate things, and everything interesting
    /// (stalls, sideslip, mushing, energy) lives in the gap between them.
    ///
    /// PORTED AS-IS. The constants here were tuned against the Three.js build and
    /// the user's stated feel ("hot arcade thrust, realistic breaking only"). Do not
    /// replace any of this with Rigidbody drag or Unity's physics — it integrates by
    /// hand on purpose.
    ///
    /// ======================= HANDEDNESS, read before editing =======================
    /// THIS HEADER IS ABOUT THE *FLIGHT FRAME*, NOT ABOUT THE ART. The two are
    /// different frames and conflating them has already cost a pass; read the
    /// second block below before touching any sign in a sibling file.
    ///
    /// Three.js body axes: forward = -Z, up = +Y, right = +X   (right-handed)
    /// Unity    body axes: forward = +Z, up = +Y, right = +X   (left-handed)
    ///
    /// Right and up are the SAME in both. Only forward's label flips. Consequences,
    /// each derived and marked at its use site below:
    ///
    ///  * Local airflow: the JS reads -vLocal.z as "airspeed along the nose";
    ///    here that is +vLocal.z. So alpha/beta drop the JS's minus on z.
    ///  * Rotation about a local axis is COMPONENT-IDENTICAL between the two (both
    ///    coordinate handedness and rotation handedness flip together, cancelling).
    ///    But because forward flips, the physical result on the NOSE inverts for
    ///    pitch and yaw, and does NOT for roll:
    ///        pitch (local X): NEGATE     yaw (local Y): NEGATE     roll (local Z): KEEP
    ///    Roll survives because the wing effect is +X -> +Y in both frames.
    ///    The banked-turn rotation about WORLD up is a yaw, so it negates too.
    ///  * Cross products are component-identical but geometrically mirrored. The JS
    ///    lift direction cross(right, velDir) gives +Y in Three and -Y in Unity, so
    ///    the operands are SWAPPED here: cross(velDir, right).
    ///
    /// `rates` and `target` are kept in the JS SIGN CONVENTION so every tuning
    /// constant below can be read straight against src/aero.js. The pitch/yaw
    /// negation happens once, at the moment the rotation is applied.
    ///
    /// =============== THE ASSETS DO NOT OBEY THIS. MEASURED, 2026-10-30 ==============
    /// "Unity forward = +Z" above is a statement about `plane` — the Transform
    /// this model thrusts, pitches and rolls, whose +Z this file DEFINES as the
    /// nose. It is NOT a statement about the aeroplane MODELS, and every one of
    /// the 35 shipped airframes contradicts it.
    ///
    /// Measured on all 35 real Assets/Resources/Art/Planes/*.fbx at their natural import
    /// transform (Editor/Probes/PlanePropsProbe.Axis, `AXIS`/`AXISDIAG`/
    /// `AXISCEN` lines; the discriminator is the horizontal stabiliser, which
    /// is 25-45% of the wingspan and sits in the last tenth of the length, so
    /// the WIDEST end slab of the bounding box is the tail and the opposite end
    /// is the nose):
    ///
    ///     27 defs with no orientation fix   nose along -Z   (p51, c130, f15, ...)
    ///      4 defs with yaw = PI             nose along +Z   (hsp75, javelin, h6k, il76)
    ///      2 defs with yaw = -PI/2          nose along +X   (cessna310, f4f)
    ///      2 defs with an off-axis yaw      37.7 / 40.2 deg off -Z (tornado, tu144)
    ///
    /// which is EXACTLY the set src/planes.js:826-836 straightens, with the
    /// straightened nose still at -Z. In other words the glTF -> Blender -> FBX
    /// -> Unity bridge (tools/glb_batch_fbx.py, axis_forward='-Z', axis_up='Y')
    /// spends its one required handedness flip on **X**, not Z: a model-local
    /// (x, y, z) in the GLB arrives here as (-x, y, z), the forward-flip rule
    /// does not apply to these files, and the JS's straightened nose (0, 0, -1)
    /// is still (0, 0, -1) in Unity.
    ///
    /// The proof that does not depend on a tolerance: src/planes.js:239 authors
    /// the P-51's propeller hub at (r, u, f) = (0, 1.57, 4.95) on the GLB, and
    /// the model ships a bare spinner there. The P-51's narrow end slab comes
    /// back centred on that station with z NEGATIVE, and its wide end slab
    /// measures 3.88 m across against the aircraft's published 3.9 m tailplane
    /// span. A number from the authoring pipeline that the runtime had no way
    /// to know, on the end the measurement says is the nose.
    ///
    /// SO, THE BOUNDARY, and it is the thing to get right:
    ///   * this file and everything it drives (thrust along `plane.forward`,
    ///     :190/:301) own the FLIGHT frame, nose = +Z. No sign in here is
    ///     affected by the measurement above and none of them should be touched
    ///     on account of it.
    ///   * Flight/PlaneCockpitMeasurement.cs (its search window is
    ///     src/planes.js:1019-1020's `air.min.z + len*0.05 .. 0.78`, measured
    ///     from the -Z end), Flight/PlaneProps.FileNose, Flight/PlaneHallFill's
    ///     `Span = size.x, Len = size.z`, and PlanePhysics.cs:573's nose sample
    ///     point at `-NoseLen * 0.85` all own the MODEL frame, nose = -Z. They
    ///     are right, and they agree with each other.
    ///   * the ONE place the two frames meet is the model child's
    ///     localRotation under the flight Transform, which must carry
    ///     src/planes.js:834's per-def fix (PlaneDef.YawUnity / RollUnity)
    ///     composed with the single 180-degree yaw that turns a straightened
    ///     -Z nose into this file's +Z forward.
    ///
    /// CLOSED, 2026-10-30. That rotation is Flight/PlaneModelFrame.cs — read
    /// its header for the chain. The model child is no longer parented at
    /// identity anywhere (Editor/BuildPlayableScene.cs, BuildFlyableScene.cs,
    /// Flight/PlaneHallFill.cs and Flight/PlaneController.Init all go through
    /// PlaneModelFrame.Attach/Ensure), and the -Z-anchored sites are HANDED
    /// that frame rather than assuming the flight root is it:
    ///
    ///   PlaneCockpitBaker.Bake      measures in `model_frame`, and converts
    ///                               the seat back to the flight root through
    ///                               the frame's real orientation
    ///   PlaneCockpitKit (kit/shell/hood)  built as children of `model_frame`
    ///   PlaneProps / PropellerSystem      run in `model_file`, the FILE frame,
    ///                               byte-identical to the frame they ran in
    ///                               before (PROPSUM unchanged at 31/31)
    ///   PlaneHallFill               measures the STRAIGHTENED box, so
    ///                               `Span = size.x, Len = size.z` is finally
    ///                               the real span and length (the Cessna 310
    ///                               packed 9.88 x 11.20; it is 11.25 x 9.74)
    ///   PlanePhysics (:573 nose/tail contact, TearAt's tail line),
    ///   Combat/AirframeBreakup's four regions, Combat/PlaneDamage's _partAt /
    ///   cut-line / smoke plume   — all now read z in THIS file's frame, with
    ///                               the JS comparison reversed once, inline.
    ///
    /// The belly offset is untouched by any of it: a 180-degree yaw about Y
    /// cannot change `min.y`, which is the whole of WHEEL_HEIGHT's input
    /// (PlaneController.BellyOffset).
    /// ===============================================================================
    /// </summary>
    public class FlightModel
    {
        const float G = 9.81f;
        const float Rho0 = 1.225f; // sea-level air density, kg/m^3

        readonly AeroPlaneDef _def;

        public float Mass, Span, S, KInduced, Cd0, ClMax, AlphaStall, GBreak;
        public float Thrust0, VMax, Vne, VStall, Vr;
        public float RollMax, PitchMax, YawMax, Tau, NAuth;
        const float ClSlope = 5.5f; // per radian

        // Live readouts the game reacts to.
        public float Alpha, Beta;
        public float N = 1f;        // load factor
        public bool  Stalled;
        public float Overspeed;     // 0..1+, fraction past VNE onset

        public FlightModel(AeroPlaneDef def, float size, float halfSpan)
        {
            _def = def;
            if (size <= 0f) size = 11f;
            float span = 2f * (halfSpan > 0f ? halfSpan : 4.5f);

            Mass = def.Mass > 0f ? def.Mass : 2200f * Mathf.Pow(size / 11f, 2.4f);
            Span = span;
            // Wing area from real span x an estimated mean chord. Cross-checked
            // against the real types: Spitfire ~21 m2 (real 22.5), F-104 ~19 (18.2).
            S = span * Mathf.Max(1.1f, size * 0.17f);
            float ar = (span * span) / S;
            KInduced = 1f / (Mathf.PI * ar * 0.8f);
            Cd0 = def.Prop ? 0.026f : 0.018f;
            ClMax = def.ClMax > 0f ? def.ClMax : 1.5f;
            AlphaStall = ClMax / ClSlope; // ~0.23-0.42 rad
            GBreak = def.GBreak > 0f ? def.GBreak : 9f;

            Thrust0 = def.Tw * Mass * G;
            VMax = def.MaxSpeed;
            Vne = def.MaxSpeed * 1.28f; // past this the airframe flutters

            // True 1g stall speed and rotation speed — the game shows these.
            VStall = Mathf.Sqrt((2f * Mass * G) / (Rho0 * S * ClMax));
            Vr = VStall * 1.12f;

            // Control rates. Short wings roll fast (F-104), long wings don't (C-17).
            RollMax  = Mathf.Clamp(14f / (span / 2f), 0.7f, 3.4f);
            PitchMax = Mathf.Clamp(18f / size, 0.45f, 1.5f);
            YawMax   = Mathf.Clamp(8f / size, 0.25f, 0.6f);
            Tau = 0.18f + size * 0.004f; // control response lag, s

            // Elevator authority in g — enough to BREAK the airframe if you insist.
            NAuth = GBreak * 1.25f;
        }

        public float Rho(float y) => Rho0 * Mathf.Exp(-Mathf.Max(0f, y) / 8500f);

        public float ThrustAt(float v, float y)
        {
            // Nearly flat altitude lapse — arcade engines don't gasp for air.
            float t = Thrust0 * Mathf.Pow(Rho(y) / Rho0, 0.3f);
            // A propeller converts constant power: thrust falls as speed builds.
            if (_def.Prop) t *= Mathf.Clamp(1.25f - 0.6f * (v / VMax), 0.55f, 1.25f);
            return t;
        }

        /// <summary>
        /// Altitude overboost: high above the ground the speed ceiling opens up to
        /// ~+55% for crossing the country, GTA style.
        /// </summary>
        public float VCapAt(float groundDist)
            => VMax * (1f + 0.55f * Mathf.Clamp((groundDist - 800f) / 1500f, 0f, 1f));

        /// <summary>
        /// One physics step while airborne. Mutates <paramref name="vel"/>,
        /// <paramref name="rates"/> and the transform's rotation.
        /// `power` is how much of the engine is actually turning — a turbine still
        /// spooling makes a fraction of its thrust, a dead engine none at all, which
        /// is what turns an aeroplane into a glider.
        /// </summary>
        public void Update(float dt, StickInput stick, Transform plane,
                           ref Vector3 vel, ref Vector3 rates, Airframe str,
                           float groundDist, float power = 1f)
        {
            float rho = Rho(plane.position.y);
            float v = Mathf.Max(vel.magnitude, 0.5f);
            Vector3 velDir = vel / v;

            // Airflow in body axes. HANDEDNESS: forward is +Z here, so the JS's
            // -vLocal.z ("along the nose") becomes +vLocal.z. x and y are unchanged.
            Vector3 vLocal = plane.InverseTransformDirection(velDir);
            float alpha = Mathf.Atan2(-vLocal.y, vLocal.z); // + = wind from below
            float beta  = Mathf.Atan2(vLocal.x, vLocal.z);  // + = wind from the right
            Alpha = alpha;
            Beta = beta;

            float q = 0.5f * rho * v * v; // dynamic pressure
            // Authority: controls bite with airspeed, saturating near cruise.
            float qRef = 0.5f * Rho0 * VStall * VStall;
            float auth = q / (q + qRef);

            // ---- lift ----
            // Linear below the critical AoA, collapsing past it. Wings that are
            // damaged (0.65) or GONE (0) each carry their half of the total.
            float aAbs = Mathf.Abs(alpha);
            float cl;
            if (aAbs < AlphaStall)
            {
                cl = ClSlope * alpha;
                Stalled = false;
            }
            else
            {
                // Post-stall: lift falls off hard but not to zero (flat-plate regime).
                float past = aAbs - AlphaStall;
                cl = Mathf.Sign(alpha) * Mathf.Max(0.35f, ClMax - past * 2.6f);
                Stalled = true;
            }
            float wingFactor = (str.WingL + str.WingR) / 2f;
            float liftN = q * S * cl * wingFactor;
            N = liftN / (Mass * G);

            Vector3 fwd = plane.forward;
            Vector3 rightB = plane.right;
            // HANDEDNESS: the JS is cross(right, velDir); in Unity's left-handed
            // cross that points DOWN, so the operands are swapped to get lift up.
            Vector3 liftDir = Vector3.Cross(velDir, rightB).normalized;

            // ---- drag ----
            float kInd = KInduced;
            // Ground effect: induced drag shrinks within a wingspan of the ground —
            // that floaty cushion right before touchdown.
            if (groundDist < Span) kInd *= 0.55f + 0.45f * (groundDist / Span);
            float cd = Cd0 + kInd * cl * cl;
            // Compressibility stand-in: a steep drag wall just past the ceiling, so
            // even monster thrust settles near it; the ceiling itself rises with
            // altitude and a dive still punches through into flutter territory.
            float vCap = VCapAt(groundDist);
            float over = Mathf.Max(0f, v / (vCap * 1.03f) - 1f);
            cd += 14f * over * over;
            // Air brake: holding S in the air bleeds speed hard.
            if (stick.Throttle < 0f) cd += 0.3f;
            // Missing parts are draggy torn metal.
            cd += 0.02f * (2f - str.WingL - str.WingR) + 0.015f * (1f - str.Tail);
            float dragN = q * S * cd;

            // Sideslip produces a side force (what makes a slip actually slide).
            float sideN = -q * S * 0.75f * beta;

            // ---- sum forces, integrate velocity ----
            Vector3 f = new Vector3(0f, -G * Mass, 0f)
                      + fwd     * (ThrustAt(v, plane.position.y) * Mathf.Max(0f, stick.Throttle) * power)
                      + liftDir * liftN
                      + velDir  * -dragN
                      + rightB  * sideN;
            vel += f * (dt / Mass);

            // VNE rides the boosted ceiling: level flight never flutters on its own,
            // but a full-power dive past ~128% of the cap starts tearing things.
            float vneNow = vCap * 1.28f;
            Overspeed = Mathf.Max(0f, (v - vneNow) / (vneNow * 0.18f));

            // ---- rotation: GTA-style snap, realistic consequences ----
            // Arcade authority curve: controls wake up fast and stay crisp. High
            // floor — the stick answers even at ZERO airspeed, so a braked, falling
            // plane can still point its nose where it wants.
            // NOTE: built in the JS sign convention; negated at application.
            float authA = Mathf.Clamp(v / 70f, 0.9f, 1.15f);
            Vector3 target = new Vector3(
                stick.Pitch * 1.25f * authA,
                stick.Rudder * 0.7f,
                stick.Roll * 2.3f * authA);

            // Auto-level when the stick is free (GTA does this too). Roll keeps its
            // sign, and rightB.y is frame-independent, so this is a straight copy.
            if (stick.Roll == 0f) target.z += -rightB.y * 0.9f;

            // Damaged aerodynamics bite but stay flyable (user rule: a one-winged —
            // even wingless — plane must still answer the stick): a steady pull
            // toward the stump plus a wobble you keep correcting, not a death roll.
            float asym = str.WingR - str.WingL;
            float wingsLost = 2f - str.WingL - str.WingR;
            target.z += asym * 1.4f * auth;
            if (wingsLost > 0.3f)
                target.z += Mathf.Sin(Time.time * 6.3f) * 0.25f * wingsLost;

            if (str.Tail < 1f)
            {
                float tailFac = str.Tail > 0f ? str.Tail : -0.6f;
                target.x += -(alpha - 0.03f) * 3.2f * auth * tailFac;
                target.y += -beta * 2.4f * auth * tailFac;
            }

            // Stall: buffet + an uncommanded wing drop; the nose wants to come down —
            // but active stick input overrides it (the player is always the boss).
            if (Stalled)
            {
                float t = Time.time;
                target.z += (Mathf.Sin(t * 13.7f) * 0.9f + Mathf.Sin(t * 5.3f) * 0.5f)
                            * (1f - Mathf.Abs(stick.Roll));
                target.x -= 0.5f * (1f - Mathf.Abs(stick.Pitch));
            }
            if (str.Tail <= 0f)
            {
                float t = Time.time;
                target.x += Mathf.Sin(t * 7.1f) * 2.2f;
                target.y += Mathf.Sin(t * 4.3f) * 1.4f;
            }

            float k = 1f - Mathf.Exp(-dt / 0.11f); // snappy
            rates = Vector3.Lerp(rates, target, k);

            // HANDEDNESS: pitch and yaw invert, roll does not. See the class header.
            // UNITS: `rates` is rad/s, exactly aero.js's — three.js's rotateX/Y/Z
            // take RADIANS, Transform.Rotate takes DEGREES. Without Rad2Deg every
            // stick command landed 57.3x too weak: a 2.07 rad/s roll (118 deg/s)
            // became 2 deg/s, 1.25 rad/s of pitch became 1.3 deg/s, and the
            // banked turn never turned. The user's sessions read as "left right
            // not works, not arrows not wasd in plane ... down up too"
            // (2026-10-31); the census showed roll=+-1 and pitch=+-1 reaching
            // this very method with the bank never moving. No probe asserted a
            // rate in play: FlightProbe pairs the CONSTANTS and ORIENTFLY only
            // checks that the nose leads the velocity.
            plane.Rotate(-rates.x * Mathf.Rad2Deg * dt, 0f, 0f, Space.Self);
            plane.Rotate(0f, -rates.y * Mathf.Rad2Deg * dt, 0f, Space.Self);
            plane.Rotate(0f, 0f,  rates.z * Mathf.Rad2Deg * dt, Space.Self);

            // Banked turn — the GTA soul: bank angle drives yaw around world up, so
            // rolling in IS turning, tight at any speed. A yaw, so it negates.
            // aero.js:219 rotateOnWorldAxis(up, rightB.y * 1.35 * dt) — radians.
            plane.Rotate(Vector3.up, -rightB.y * 1.35f * Mathf.Rad2Deg * dt, Space.World);

            // GTA air brake: S hauls the speed down hard at ANY airspeed — all the
            // way to zero if you hold it; then you're a falling leaf.
            if (stick.Throttle < 0f)
            {
                float vb = vel.magnitude;
                if (vb > 0.01f) vel *= Mathf.Max(vb - 55f * dt, 0f) / vb;
            }

            // Glue the flight path to the nose (arcade handling) — but only as much
            // as air is actually flowing ALONG the nose. Nose-up with no forward
            // speed bends nothing: the plane keeps falling, exactly like it should.
            // Point the nose into the fall (or power up) and the dive becomes flight.
            Vector3 fwd2 = plane.forward;
            float v2 = vel.magnitude;
            float uFwd = Mathf.Max(0f, Vector3.Dot(vel, fwd2)); // airspeed along the nose
            float glue = 1f - Mathf.Exp(
                -dt * (Stalled ? 1.2f : 6.5f) * Mathf.Clamp(uFwd / 40f, 0.04f, 1.5f));
            vel = Vector3.Lerp(vel, fwd2 * v2, glue);

            // Structural G: whichever is worse — what the wing is lifting, or the
            // turn the elevator is actually forcing at this speed.
            float nPitch = (rates.x * v2) / G;
            if (Mathf.Abs(nPitch) > Mathf.Abs(N)) N = nPitch;
        }
    }
}

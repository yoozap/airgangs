using UnityEngine;

namespace TbilisiFly.Props
{
    // =========================================================================
    // Port of src/crane.js:868-1943 — "THE WIND TUNNEL": the aerodynamic
    // force balance that makes the slung Mustang ride back, buffet and (at
    // 2000 km/h) get hauled up under the block, plus the moving-air sheets'
    // visual parameters. Full design note at crane.js:868-963; read it first.
    //
    // ================================ SCOPE =================================
    // PORTED, as pure data + pure math:
    //   * Balance() — the force balance off the real aerodynamics (lift slope,
    //     parasite/induced drag) crane.js:1126-1133 `balance(kmh)`.
    //   * Step() — the ride-back spring-damper, haul-up fraction, low-passed
    //     buffet, and the two composed quaternions (the swinging pivot and
    //     the plane's own, decoupled, attitude) — crane.js:1849-1930.
    //   * BuildHazeTexture()/HazeAt() — the tiling fractal-noise alpha ramp
    //     the moving-air sheets are painted with — crane.js:1945-1993.
    //
    // NOT PORTED, and why:
    //   * the seven moving-air SHEETS' mesh (crane.js:1015-1087) is now
    //     ported too, in Props/WindFlowMesh.cs (same folder) — see that
    //     file's own header; it needed only WindRider.PlaneBox (plain
    //     floats, already required by this file and WindRider.cs), not the
    //     loaded aeroplane this header used to say it needed.
    //   * "whoever is standing on it" (crane.js:1146-1845) — the rider
    //     locomotion that lets the hero stand on, be blown off, and fly
    //     beside the wing is now ported too, in Props/WindRider.cs, NOT this
    //     file: an earlier pass of this port named CharacterRig for 5
    //     missing methods and found them genuinely absent there, but a
    //     correction identified them in Character/FootPoses.cs instead
    //     (ClimbMetrics/HandsOnEdge/SetWindBrace/SetFlyPose/SetWingGrip),
    //     which unblocked that whole system. See WindRider.cs's own header
    //     for what it covers (the state machine, cornerAt's longest-run
    //     search, measureBelly's real ray-against-baked-skin) and its one
    //     remaining gap (the gun-arm preservation, which needs Combat
    //     weapon-hand-bone tracking FootController does not expose).
    //
    // ============================== HANDEDNESS ==============================
    // One true cross product: crane.js:1096 `axis = UP.cross(wind)`. Per
    // FlightModel.cs's rule the operands are SWAPPED for Unity's left-
    // handedness — AxisOf() below is `Cross(wind, up)`, not `Cross(up, wind)`.
    // Everything else (the spring-damper scalars, the AxisAngle/Euler
    // composition) is either a scalar or a rotation about an axis that is
    // itself handedness-correct by construction (the swapped `axis`, and
    // local-X for the lift pitch, which per FlightModel.cs's header is
    // "pitch: NEGATE" for a LOCAL AXIS rotation driving a physical nose
    // angle — see LiftRotation below for where that negation is applied).
    // =========================================================================
    public static class WindTunnel
    {
        public const int Sheets = 7;              // crane.js:894
        public const int SheetX = 26;               // crane.js:895
        public const int SheetZ = 30;               // crane.js:896
        public const float FlowScroll = 3.4f;        // crane.js:900
        public const float FlowHaze = 0.26f;         // crane.js:901
        public const float SwayOsc = 0.05f;          // rad, crane.js:911
        public const float SwayLimit = 1.40f;        // rad, crane.js:912
        public const float HaulUp = 0.46f;           // crane.js:913
        public const float SwayK = 4.2f;              // crane.js:914
        public const float SwayC = 3.4f;              // crane.js:915
        public const float BuffetLag = 7f;            // 1/s, crane.js:917
        public const float Buffet = 0.038f;           // rad at AirLo, crane.js:919
        public const float LiftPitchMax = 0.07f;      // rad, crane.js:920 LIFT_PITCH
        public const float PropRpmWindmill = 1400f;   // crane.js:921
        public const float WingIncidence = 0.018f;    // rad, crane.js:922 WING_INC

        /// <summary>Mirrors Flight/FlightModel.cs's own PRIVATE `ClSlope`
        /// (5.5f per radian) and `Rho0` (1.225 kg/m^3 at sea level) — not
        /// readable from here (that file's rule, documented in its own
        /// header, is to keep them private), so restated with the same
        /// value and the same citation (src/aero.js) rather than reached
        /// into.</summary>
        const float ClSlope = 5.5f;
        const float Rho0 = 1.225f;

        /// <summary>crane.js:1096. See class header.</summary>
        public static Vector3 AxisOf(Vector3 wind) => Vector3.Cross(wind, Vector3.up).normalized;

        public struct Balance { public float Lift, Drag, Q; }

        /// <summary>crane.js:1126-1133 `balance(kmh)`. `clMax` defaults to
        /// the Mustang's own (src/planes.js p51 entry, 1.6f) since this
        /// rig has only ever hung that one airframe — pass the real value
        /// if a caller slings something else.</summary>
        public static Balance ComputeBalance(float kmh, float wingArea, float cd0, float kInduced, float clMax = 1.6f)
        {
            float v = kmh / 3.6f;
            float q = 0.5f * Rho0 * v * v;
            float cl = Mathf.Min(clMax, ClSlope * WingIncidence);
            float cd = cd0 + kInduced * cl * cl;
            return new Balance { Lift = q * wingArea * cl, Drag = q * wingArea * cd, Q = q };
        }

        /// <summary>Between-frame state — crane.js's own closed-over `a`, `v`,
        /// `t` (ride-back angle/rate/clock) and the low-passed buffet Euler
        /// `e` (crane.js:1142-1144, 1204).</summary>
        public struct State
        {
            public float A, V, T;
            public Vector3 BuffetEuler;
        }

        /// <summary>One frame — crane.js:1849-1930, minus the flow-mesh/prop-
        /// spin/shell-reindex/rider calls (see class header). `axis` is
        /// AxisOf(wind) (computed once, the wind direction does not change
        /// frame to frame); `baseRotation` is the plane's RESTING local
        /// rotation (`base` in the JS, cinema.js:1135, built once from the
        /// plane's own rotation.y). Returns whether the rig is "still"
        /// (crane.js:1854) — a caller stops ticking anything downstream
        /// (flow visibility, prop spin, shell reindex) when true, same as
        /// the JS's own early-outs.</summary>
        public static bool Step(ref State st, float dt, float airspeedKmh, float weightN,
            float wingArea, float cd0, float kInduced, Vector3 axis, Quaternion baseRotation,
            out Quaternion swingRotation, out Quaternion planeLocalRotation,
            out float haulFraction, out float flowOpacity, out float scrollDx, out float scrollDy,
            out float propRpm, float clMax = 1.6f)
        {
            float p = airspeedKmh / WarehouseCrane.AirLo; // crane.js:1853

            if (p <= 0f && Mathf.Abs(st.A) < 2e-3f && Mathf.Abs(st.V) < 2e-3f)
            {
                // STILL — crane.js:1911-1919 (reached here on the frame it
                // settles, rather than the JS's separate early-out for every
                // frame AFTER that one; behaviourally identical steady state).
                st.A = 0f; st.V = 0f; st.BuffetEuler = Vector3.zero;
                swingRotation = Quaternion.identity;
                planeLocalRotation = baseRotation;
                haulFraction = 0f; flowOpacity = 0f; scrollDx = 0f; scrollDy = 0f; propRpm = 0f;
                return true;
            }

            st.T += dt;
            var bal = ComputeBalance(airspeedKmh, wingArea, cd0, kInduced, clMax);
            float up = Mathf.Max(weightN * 0.06f, weightN - bal.Lift);
            float want = Mathf.Min(SwayLimit, Mathf.Atan2(bal.Drag, up) + SwayOsc * Mathf.Sin(st.T * 1.15f) * p);
            st.V += ((want - st.A) * SwayK - st.V * SwayC) * dt;
            st.A += st.V * dt;

            haulFraction = Mathf.Clamp01(Mathf.Max(0f, bal.Lift / weightN - 1f) / 6f);

            float b = Buffet * Mathf.Pow(p, 1.35f);
            Vector3 eWant = new Vector3(
                b * Mathf.Sin(st.T * 3.1f) * 0.8f + b * Mathf.Sin(st.T * 5.9f) * 0.3f,
                b * Mathf.Sin(st.T * 2.3f) * 0.6f,
                b * Mathf.Sin(st.T * 4.3f) + b * Mathf.Sin(st.T * 1.7f) * 0.35f);
            float k = Mathf.Min(1f, BuffetLag * dt);
            st.BuffetEuler += (eWant - st.BuffetEuler) * k;

            // crane.js:1907 `swing.quaternion = AxisAngle(axis,-a) * Euler(e)`.
            // Three's default Euler composition order ('XYZ') is approximated
            // with Quaternion.Euler for this — a several-degree, low-passed
            // TREMOR, not a controls-grade rotation, so an exact order match
            // is not worth re-deriving (unlike the flight scene's orientation,
            // which gets the full basis-construction treatment in
            // Cinema/CinemaFlight.cs because THAT one is the whole picture).
            swingRotation = Quaternion.AngleAxis(-st.A * Mathf.Rad2Deg, axis)
                          * Quaternion.Euler(st.BuffetEuler * Mathf.Rad2Deg);

            planeLocalRotation = Quaternion.Inverse(swingRotation) * baseRotation * LiftRotation(p);

            flowOpacity = Mathf.Min(0.62f, FlowHaze * Mathf.Pow(p, 0.75f));
            scrollDx = -dt * FlowScroll * (0.3f + 0.7f * p);
            scrollDy = dt * 0.06f * p;
            propRpm = PropRpmWindmill * p;
            return false;
        }

        /// <summary>crane.js:1908 `lift.setFromAxisAngle(X, min(0.30,
        /// LIFT_PITCH*p))` — a rotation about the plane's own LOCAL X (pitch)
        /// axis. Per FlightModel.cs's header, a LOCAL-X rotation's PHYSICAL
        /// effect on the nose inverts between Three's forward=-Z and Unity's
        /// forward=+Z body axes ("pitch: NEGATE") — so unlike the swing/
        /// buffet terms above (which rotate the PIVOT, not the fuselage,
        /// about a world axis), this one needs that negation to keep the
        /// nose tipping the same physical way (up, into the stream).</summary>
        public static Quaternion LiftRotation(float p)
            => Quaternion.AngleAxis(-Mathf.Min(0.30f, LiftPitchMax * p) * Mathf.Rad2Deg, Vector3.right);

        // =====================================================================
        // ---- the haze texture — crane.js:1945-1993 -------------------------
        // =====================================================================
        /// <summary>crane.js:1948-1993 `hazeTexture()`, the per-pixel alpha
        /// formula only (mirrors PropellerSystem.cs's BlurDiscAlphaAt split-
        /// out-for-probing pattern). UNLIKE that disc, this formula samples
        /// from `octaves` — four independently RANDOM lattices (the JS calls
        /// `Math.random()` per build too, crane.js:1958) — so there is no
        /// cross-engine pixel to prove bit-exact; what is checked is the
        /// FORMULA's shape (non-negative, bounded, the stretch factor).
        /// `octaves` is 4 lattices of side {4,8,16,32} (crane.js:1968).</summary>
        public static float HazeAlphaAt(float u, float v, System.Func<float, float, float>[] octaves)
        {
            int[] per = { 4, 8, 16, 32 };
            float n = 0f, amp = 1f, sum = 0f;
            for (int o = 0; o < octaves.Length; o++)
            {
                n += amp * octaves[o](u * per[o] * 0.28f, v * per[o]);
                sum += amp;
                amp *= 0.55f;
            }
            n /= Mathf.Max(1e-6f, sum);
            float val = Mathf.Max(0f, n - 0.42f) / 0.58f;
            return Mathf.Min(1f, val * val * 1.6f);
        }

        /// <summary>Bilinear value-noise lattice, crane.js:1956-1966
        /// `octave(per)`, wrapped (tiles seamlessly). `lattice` is `per*per`
        /// random values in 0..1 — a caller supplies them (UnityEngine.Random
        /// or a seeded System.Random; the JS itself is unseeded, so exact
        /// reproduction is not the point — see HazeAlphaAt's doc comment).</summary>
        public static float SampleOctave(int per, float[] lattice, float x, float y)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float xf = Smooth(x - xi), yf = Smooth(y - yi);
            int i0 = ((xi % per) + per) % per, i1 = (i0 + 1) % per;
            int j0 = ((yi % per) + per) % per, j1 = (j0 + 1) % per;
            float a = lattice[j0 * per + i0] * (1 - xf) + lattice[j0 * per + i1] * xf;
            float bRow = lattice[j1 * per + i0] * (1 - xf) + lattice[j1 * per + i1] * xf;
            return a * (1 - yf) + bRow * yf;
        }
        static float Smooth(float t) => t * t * (3f - 2f * t);

        /// <summary>Builds a real, usable tiling Texture2D the same shape as
        /// crane.js's canvas haze (RepeatWrapping both axes, white RGB,
        /// alpha from HazeAlphaAt). A caller assigns it to the moving-air
        /// sheets' material (not built here — see class header).</summary>
        public static Texture2D BuildHazeTexture(int size = 256, int seed = 0)
        {
            var rng = new System.Random(seed);
            int[] per = { 4, 8, 16, 32 };
            var lattices = new float[per.Length][];
            for (int o = 0; o < per.Length; o++)
            {
                lattices[o] = new float[per[o] * per[o]];
                for (int i = 0; i < lattices[o].Length; i++) lattices[o][i] = (float)rng.NextDouble();
            }
            var octaves = new System.Func<float, float, float>[per.Length];
            for (int o = 0; o < per.Length; o++)
            {
                int capturedO = o;
                octaves[o] = (x, y) => SampleOctave(per[capturedO], lattices[capturedO], x, y);
            }

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float a = HazeAlphaAt(x / (float)size, y / (float)size, octaves);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255));
                }
            tex.SetPixels32(px);
            tex.Apply(false);
            tex.wrapMode = TextureWrapMode.Repeat;
            return tex;
        }
    }
}

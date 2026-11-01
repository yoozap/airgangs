using UnityEngine;

namespace TbilisiFly.Core
{
    /// <summary>
    /// Port of src/wind.js (111 lines) — ONE shared breeze over the whole city.
    ///
    /// Everything that should move in it reads the same clock and the same
    /// direction: the trees on Rustaveli lean the way the windsock at the
    /// airport is pointing. A slow gust envelope rides on top of the steady
    /// flow so a wind that blows at one strength forever doesn't read as a
    /// machine.
    ///
    /// ======================= HANDEDNESS =======================
    /// `wind.dir` in the JS is a THREE.Vector2 whose two components are the
    /// mesh-space (x, z) pair — .x is the game frame's X (WEST), .y is the
    /// game frame's Z (NORTH, unchanged by the mirror). That is a DIRECTION,
    /// not a position, but GeoFrame's X-mirror applies to it the same way:
    /// only the X component negates.
    ///
    ///   dirUnity = ( -dirGame.x,  dirGame.y )     // (east-component, north)
    ///
    /// `heading = atan2(dir.x, dir.y)` is then computed FROM that already-
    /// mirrored vector, so the angle SELF-CORRECTS exactly the way a
    /// footprint-derived yaw does in GeoFrame's own notes — there is no
    /// second, separate sign flip to apply to the heading itself. The
    /// reasoning, spelled out because it is easy to get backwards:
    ///
    ///   Both engines rotate a vector about world-up with the SAME formula
    ///   (x' = x·cosθ + z·sinθ, z' = -x·sinθ + z·cosθ) — verified against
    ///   FlightModel.cs's handedness notes, which establish that a rotation
    ///   about a local/world axis is component-identical between the two
    ///   engines (coordinate handedness and rotation handedness flip
    ///   together and cancel). Since `heading` was produced by feeding the
    ///   mirrored `dir` through that SAME atan2/sin/cos pair, the output
    ///   already points the correct (mirrored) way in Unity. Negating it
    ///   again would un-mirror it.
    ///
    /// This is why GeoFrame.cs splits its warning in two: hand-tuned yaw
    /// CONSTANTS need a manual negate; yaws DERIVED from already-mirrored
    /// vectors do not. `wind.heading` is the second kind.
    /// ============================================================
    /// </summary>
    public static class Wind
    {
        // src/wind.js:19 — Tbilisi's prevailing wind, up the valley from the
        // north-west. GAME-FRAME value, kept for documentation; UnityDir0 is
        // what everything else should read.
        public static readonly Vector2 GameDir = new Vector2(0.80f, 0.60f);

        /// <summary>Unity-frame wind direction: (east-component, north-component), mirrored per the header.</summary>
        public static readonly Vector2 UnityDir0 = new Vector2(-GameDir.x, GameDir.y).normalized;

        public static float Time;   // src/wind.js:20
        public static float Gust = 1f;     // src/wind.js:21 — 0.55..1.3, the slow breathing
        public static float Heading;       // src/wind.js:22 — rad, where the wind is blowing TO

        /// <summary>Current direction unit vector, (east-component, north-component).</summary>
        public static Vector2 Dir { get; private set; } = UnityDir0;

        // Shared globals every windy material (grass, trees, foliage) reads.
        // Three's `uniforms` object becomes URP global shader properties —
        // one write a frame moves every instanced blade/leaf in the city,
        // same as the JS's single shared uniform set.
        static readonly int WindTimeId = Shader.PropertyToID("_WindTime");
        static readonly int WindDirId = Shader.PropertyToID("_WindDir");
        static readonly int WindAmpId = Shader.PropertyToID("_WindAmp");

        /// <summary>One frame. Mirrors updateWind(dt) — src/wind.js:32-44.</summary>
        public static void Update(float dt)
        {
            Time += dt;
            float t = Time;
            // Two slow sines beating against each other: gusts that come and
            // go without ever repeating on a count you can hear.
            Gust = 0.92f + 0.26f * Mathf.Sin(t * 0.19f) * Mathf.Sin(t * 0.073f + 1.3f);
            // The direction wanders a few degrees, the way a real breeze does.
            float wander = 0.13f * Mathf.Sin(t * 0.11f + 0.7f) + 0.06f * Mathf.Sin(t * 0.37f);
            // atan2 on the ALREADY-MIRRORED direction — see the class header.
            Heading = Mathf.Atan2(UnityDir0.x, UnityDir0.y) + wander;
            Dir = new Vector2(Mathf.Sin(Heading), Mathf.Cos(Heading));

            Shader.SetGlobalFloat(WindTimeId, t);
            Shader.SetGlobalVector(WindDirId, Dir);
            Shader.SetGlobalFloat(WindAmpId, Gust);
        }
    }

    /// <summary>
    /// The windsock: "it points where the wind is going, and it never sits
    /// still." Ported out of src/airport.js's propUpdater closure (the part
    /// that actually reads src/wind.js's state) because airport.js itself is
    /// City-owned and out of this port's scope — this is a reusable, City-
    /// agnostic function City code can call once it builds the prop.
    ///
    /// Pure rotation-chase math: frame-independent (it is an angle-wrap
    /// smoothing filter, not a vector transform), so nothing here needs a
    /// handedness fix beyond feeding it Wind.Heading, which is already
    /// Unity-correct per Wind's header.
    /// </summary>
    public static class WindsockDriver
    {
        /// <summary>
        /// One frame. `yaw`/`tilt` are the prop's current local Y/Z rotation in
        /// radians (src/airport.js:1046-1055: g.rotation.y / g.rotation.z).
        /// Returns the next (yaw, tilt).
        /// </summary>
        public static (float yaw, float tilt) Step(float yaw, float tilt, float dt, float windHeading, float gust, float time)
        {
            // The sock lags the wind — fabric on a swivel, not a weathervane.
            // It streams AWAY from the wind, hence the +PI.
            float want = windHeading + Mathf.PI;
            float d = (want - yaw) % (Mathf.PI * 2f);
            if (d > Mathf.PI) d -= Mathf.PI * 2f;
            if (d < -Mathf.PI) d += Mathf.PI * 2f;
            yaw += d * Mathf.Min(1f, 1.6f * dt);

            // Lifts as the gust builds: limp at a breath, straight out in a blow.
            float lift = Mathf.Clamp((gust - 0.7f) * 1.25f, 0f, 1f);
            tilt = -0.42f * (1f - lift) + 0.05f * Mathf.Sin(time * 2.3f);
            return (yaw, tilt);
        }
    }
}

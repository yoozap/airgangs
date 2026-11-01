using UnityEngine;

namespace TbilisiFly.Data
{
    /// <summary>
    /// The one conversion everything else stands on.
    ///
    /// THE GAME FRAME (Three.js, tools/build_map.py to_local):
    ///     X = WEST, Y = UP, Z = NORTH, metres, RIGHT-handed.
    ///     to_local(lat, lon) = ( -(lon - lon0) * M_LON, (lat - lat0) * M_LAT )
    ///
    /// THE UNITY FRAME (chosen here):
    ///     X = EAST, Y = UP, Z = NORTH, metres, LEFT-handed.
    ///     This is the convention every GIS package for Unity uses (Cesium for
    ///     Unity, Mapbox), and it puts north on +Z — Unity's "forward" — so a
    ///     compass heading of 0 is +Z and headings run clockwise like a compass.
    ///
    /// So the port MIRRORS X:  x_unity = -x_game,  y and z unchanged.
    ///
    /// Mirroring one axis is not free. It has three consequences, and missing
    /// any one of them produces a city that looks almost right:
    ///
    ///   1. Every stored X flips sign. Done at load (ToUnity below) rather than
    ///      by regenerating city.json, so the Three.js build keeps running off
    ///      the same data file for as long as the port takes.
    ///   2. Every YAW flips sign. Mirroring reverses the sense of rotation about
    ///      Y. Footprint-derived orientations self-correct (their vertices are
    ///      mirrored too), but hand-tuned constants do NOT — negate those:
    ///        src/planes.js      yaw -0.658 (orientation fix), yaw -0.702
    ///        src/characters.js  anime yaw 180, ghost yaw -90
    ///        public/tbilisi/metro.json  23 stored "yaw" values
    ///   3. Every generated mesh's TRIANGLE WINDING reverses, or the normals
    ///      point inward and backface culling eats the whole city. Any mesh
    ///      built in C# from game-frame data must reverse its index triples.
    ///      Use FlipWinding below; do not hand-roll it per generator.
    ///
    /// VERIFIED 2026-10-26 against glTFast 6.20.0, with
    /// Assets/Resources/Art/Debug/axis_probe.glb and Assets/Editor/Probes/AxisProbe.cs:
    /// **glTFast negates X**, the same axis this file mirrors, and leaves Y and Z
    /// alone. Every X-offset node flipped sign (the 4 m arm authored at +2 landed
    /// at -2; the chiral "F" at -3/-2.4/-2.6 landed at +3/+2.4/+2.6) while the
    /// Y and Z arms passed through untouched.
    ///
    /// So imported props and the procedurally generated city AGREE. An authored
    /// +X still points west in Unity, exactly as it did in the Three.js build.
    /// Re-run AxisProbe if glTFast is ever upgraded — this is the assumption the
    /// whole world's placement rests on.
    /// </summary>
    public static class GeoFrame
    {
        // tools/build_map.py ORIGIN — Delisi, Vazha-Pshavela Ave (the spawn)
        public const double Lat0 = 41.7252;
        public const double Lon0 = 44.7458;

        // Metres per degree AT THIS ORIGIN. These are build_map.py's own series
        //   M_LAT = 111132.92 - 559.82*cos(2f) + 1.175*cos(4f)
        //   M_LON = 111412.84*cos(f)  - 93.5*cos(3f)      f = radians(Lat0)
        // evaluated once and pinned, so there is no second place to get it wrong.
        // Re-derive these if ORIGIN ever moves.
        //
        // DO NOT "fix" these to match city.json's meta.mlat/mlon (111067.92 /
        // 83206.34). Those are ROUNDED for the JSON; the footprints themselves
        // were computed with the full-precision series, so these full values are
        // the correct ones to invert with. The difference is sub-millimetre, but
        // the rounded pair looks more authoritative than it is.
        public const double MetresPerLat = 111067.92066361613;
        public const double MetresPerLon = 83206.33860686835;

        /// <summary>lat/lon -> Unity world metres. X=east, Y=up(0), Z=north.</summary>
        public static Vector3 FromLatLon(double lat, double lon, float y = 0f)
            => new Vector3(
                (float)((lon - Lon0) * MetresPerLon),   // EAST (game frame had west)
                y,
                (float)((lat - Lat0) * MetresPerLat));  // NORTH

        public static (double lat, double lon) ToLatLon(Vector3 p)
            => (Lat0 + p.z / MetresPerLat, Lon0 + p.x / MetresPerLon);

        /// <summary>A position straight out of city.json (game frame) -> Unity.</summary>
        public static Vector3 ToUnity(float gameX, float gameY, float gameZ)
            => new Vector3(-gameX, gameY, gameZ);

        /// <summary>A yaw straight out of the game data or the JS source -> Unity.</summary>
        public static float YawToUnity(float gameYawRadians) => -gameYawRadians;

        public static float YawDegToUnity(float gameYawDegrees) => -gameYawDegrees;

        /// <summary>
        /// Reverse index triples. MUST be called on every mesh built in C# from
        /// game-frame coordinates, because mirroring X reverses winding.
        /// </summary>
        public static void FlipWinding(int[] tris)
        {
            for (int i = 0; i < tris.Length; i += 3)
                (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
        }
    }
}

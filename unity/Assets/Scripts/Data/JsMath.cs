namespace TbilisiFly.Data
{
    /// <summary>
    /// JavaScript's numeric semantics where they differ from C#'s, so a port of
    /// a line in src/*.js can keep behaving the way the shipped game behaves.
    ///
    /// ================================ WHY THIS EXISTS =====================
    /// `Mathf.RoundToInt` is NOT `Math.round`. Unity's rounds a .5 tie to the
    /// nearest EVEN number (banker's rounding, inherited from
    /// System.Math.Round's default MidpointRounding.ToEven); JavaScript's
    /// always rounds a tie toward +infinity. So:
    ///
    ///     Math.round(2.5)        === 3     // JS
    ///     Mathf.RoundToInt(2.5f)  == 2     // Unity
    ///     Math.round(-2.5)       === -2    // JS — toward +inf, NOT away from 0
    ///     Mathf.RoundToInt(-2.5f) == -2    // agrees here, by luck
    ///
    /// A probe caught this for real, which is the only reason it is known:
    /// DebrisField.MoundAnchors computes `3 + round(span / 8)` to decide how
    /// many smoulder anchors a collapsed building gets. On a 20 m building the
    /// division is exactly 2.5, so the JS places 6 anchors and the C# placed 5
    /// — a visibly thinner column of smoke on a whole class of buildings, and
    /// the last anchor landed in a different place too (the angle is k/n).
    /// Nobody would have found that by reading the code; both lines look right.
    ///
    /// ================================ WHERE TO USE IT =====================
    /// Use Round wherever the JS line being ported says `Math.round` AND the
    /// result is load-bearing: a count, a loop bound, a grid index, a seed, an
    /// array length. Ties there change geometry or placement.
    ///
    /// Do NOT bother for display text (`RNG 437`, a tonnage label, a heading).
    /// A tie is invisible to the reader, and churning those would be noise that
    /// hides the real fixes.
    /// </summary>
    public static class JsMath
    {
        /// <summary>
        /// JavaScript `Math.round`: a .5 tie goes toward +infinity.
        /// Equivalent to floor(x + 0.5), which is how the spec defines it.
        /// </summary>
        public static int Round(float x) => (int)System.Math.Floor(x + 0.5f);


        /// <summary>
        /// JavaScript's `x >>> 0` — ToUint32, i.e. wrap to 32 bits.
        ///
        /// This is the other half of the port's integer trouble, and a probe
        /// caught it the same way. src/citymap.js seeds a building's lean axis
        /// with `((bi * 2654435761) >>> 0) % 1000`. Written in C# as
        /// `(bi * 2654435761u) % 1000u` it gives a DIFFERENT answer, because
        /// C# promotes `int * uint` to **long** and never wraps: for bi = 7 the
        /// product stays 18581050327, so `% 1000` yields 327 where JS — having
        /// truncated to 32 bits first — yields 143. The building then leaned on
        /// a completely different axis (cos 0.6228 vs -0.4652) and dragged
        /// every damaged vertex sideways with it.
        ///
        /// Knuth's 2654435761 is above int.MaxValue, so any hash using it is
        /// exposed to this. Pass the product as a long and let this wrap it.
        ///
        /// Caveat worth knowing: JS numbers are doubles, so if the true product
        /// exceeds 2^53 JS itself rounds BEFORE truncating, and this will not
        /// agree. Building indices here are ~193k, giving products near 5e14,
        /// comfortably exact — but do not reuse this on a much larger operand
        /// without re-checking that.
        /// </summary>
        public static uint ToUint32(long x) => unchecked((uint)x);

        /// <summary>double overload — same rule, for lat/lon-grade values that
        /// must not be narrowed to float first (see GeoFrame on why those stay
        /// double).</summary>
        public static int Round(double x) => (int)System.Math.Floor(x + 0.5);

        /// <summary>JS <c>Number.prototype.toFixed</c>: rounds the EXACT IEEE value,
        /// ties to the LARGER n (ECMA-262 21.1.3.3). Neither .NET format reproduces
        /// it. <c>"0.0"</c> formats the shortest round-trippable decimal and rounds
        /// THAT half away from zero: 6.35 (really 6.3499999999999996) gives "6.4"
        /// where JS gives "6.3" -- the f104/aerobatic/mig21nk tonnage strings.
        /// <c>"F1"</c> is IEEE-exact but rounds an exactly-representable midpoint
        /// half to EVEN: 0.25 gives "0.2" where JS gives "0.3" -- the next plane
        /// with a mass ending in 250 or 750 kg. Doing the arithmetic in double does
        /// NOT fix this one; it is a formatting rule, not a precision loss. G17
        /// always carries enough digits to decide the rounding, and a true tie at
        /// G17 is an exactly-representable midpoint, where half-away-from-zero is
        /// what ECMA asks for. Verified 72/72 against JS semantics over every real
        /// empty/mtow value in src/planes.js plus synthetic midpoints.</summary>
        public static string ToFixed(double x, int digits)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            decimal d = decimal.Parse(x.ToString("G17", inv),
                                      System.Globalization.NumberStyles.Float, inv);
            return System.Math.Round(d, digits, System.MidpointRounding.AwayFromZero)
                         .ToString("F" + digits, inv);
        }
    }
}

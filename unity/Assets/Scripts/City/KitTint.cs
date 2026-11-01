using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Per-instance hash tint for the five real-model kit placements (panel
    /// blocks, houses, apartments, sheds, outbuildings) — src/citymap.js
    /// `_placeOut`/`_placeApts`/`_placeSheds`/`_placeHouses`/`_placePanelBlocks`,
    /// each one's own `(c, it) => { const h = ((it.bi * 2654435761) % 1024) /
    /// 1024; c.setHSL(...); }` callback passed to `_placeChunkedInstanced`.
    ///
    /// citymap.js's `instanceColor` MULTIPLIES the material's own base colour
    /// (see `_loadAptKit`'s "NO multiplyScalar here" comment — dimming the
    /// material AND tinting per instance double-dims). CityRenderer.cs is
    /// where that multiply actually happens (a per-bucket cloned Material,
    /// since Graphics.RenderMeshInstanced has no verified-safe per-instance
    /// custom-property path on this project's stock URP/Lit materials without
    /// new shader work this port cannot compile-check blind); this file only
    /// computes the EXACT colour citymap.js would — the number, not how it
    /// gets onto the GPU.
    ///
    /// HASH NOTE: `bi * 2654435761` here has NO `&gt;&gt;&gt; 0` in the JS
    /// (contrast BuildingDamage's lean-axis hash, which does and therefore
    /// goes through JsMath.ToUint32 — see .claude/skills/unity/SKILL.md). `bi`
    /// tops out at ~193,146, so the product stays under 2^53 and is EXACT in a
    /// JS double; wrapping it to 32 bits here would silently diverge from the
    /// JS value instead of matching it. Plain `long` multiplication is correct
    /// and sufficient. Every multiplier below (2654435761, 193, 97, 40503) is
    /// copied from its OWN call site — per the skill doc, "the hashes in
    /// citymap.js are not interchangeable between call sites".
    /// </summary>
    public static class KitTint
    {
        const long Knuth = 2654435761L; // citymap.js: every tint call site's own hash multiplier

        /// <summary>((bi * mult) % mod) / mod — the exact shape of every hash
        /// in citymap.js's tint callbacks. `mult`/`mod` stay `long` so the
        /// multiplication cannot overflow for any `bi` this city ever produces
        /// (max observed ~193,146; 193146 * 2654435761 ~ 5.13e14, far inside a
        /// signed 64-bit range and inside the 2^53 a JS double still holds
        /// exactly).</summary>
        static float Hash(int bi, long mult, int mod)
        {
            long prod = (long)bi * mult;
            long r = prod % mod;
            if (r < 0) r += mod; // bi is never negative in this port, but stay exact if that changes
            return r / (float)mod;
        }

        /// <summary>
        /// three.js `Color.setHSL`, reproduced term-for-term (the same p/q +
        /// hue2rgb construction as three/src/math/Color.js's own
        /// setHSL/hue2rgb), so the two engines land on the same RGB for the
        /// same (h, s, l) — this is NOT Unity's Color.HSVToRGB, which is a
        /// different model (HSV, not HSL) and would not agree at the same
        /// inputs.
        /// </summary>
        public static Color Hsl(float h, float s, float l)
        {
            h -= Mathf.Floor(h);         // THREE.MathUtils.euclideanModulo(h, 1)
            s = Mathf.Clamp01(s);
            l = Mathf.Clamp01(l);
            if (s == 0f) return new Color(l, l, l);
            float p = l <= 0.5f ? l * (1f + s) : l + s - l * s;
            float q = 2f * l - p;
            return new Color(Hue2Rgb(p, q, h + 1f / 3f), Hue2Rgb(p, q, h), Hue2Rgb(p, q, h - 1f / 3f));
        }

        static float Hue2Rgb(float p, float q, float t)
        {
            if (t < 0f) t += 1f;
            if (t > 1f) t -= 1f;
            if (t < 1f / 6f) return p + (q - p) * 6f * t;
            if (t < 1f / 2f) return q;
            if (t < 2f / 3f) return p + (q - p) * 6f * (2f / 3f - t);
            return p;
        }

        /// <summary>citymap.js:5649-5650 (_placeOut).</summary>
        public static Color Out(int bi)
        {
            float h = Hash(bi, Knuth, 1024);
            return Hsl(0.08f + 0.3f * h, 0.03f + 0.06f * h, 0.58f + 0.12f * h);
        }

        /// <summary>citymap.js:5735-5737 (_placeApts).</summary>
        public static Color Apt(int bi)
        {
            float h = Hash(bi, Knuth, 1024);
            float v = Hash(bi, 193L, 256);
            return Hsl(0.05f + 0.12f * h, 0.05f + 0.12f * v, 0.58f + 0.13f * (1f - v));
        }

        /// <summary>citymap.js:5833-5834 (_placeSheds).</summary>
        public static Color Shed(int bi)
        {
            float h = Hash(bi, Knuth, 1024);
            return Hsl(0.45f + 0.16f * h, 0.03f + 0.07f * h, 0.60f + 0.1f * (1f - h));
        }

        /// <summary>citymap.js:5932-5934 (_placeHouses).</summary>
        public static Color House(int bi)
        {
            float h = Hash(bi, Knuth, 1024);
            float v = Hash(bi, 97L, 256);
            return Hsl(0.03f + 0.22f * h, 0.07f + 0.14f * v, 0.60f + 0.12f * (1f - v));
        }

        /// <summary>citymap.js:6044-6046 (_placePanelBlocks).</summary>
        public static Color Panel(int bi)
        {
            float hv = Hash(bi, Knuth, 1024);
            float warm = Hash(bi, 40503L, 512);
            return Hsl(0.07f + 0.10f * hv, 0.05f + 0.12f * warm, 0.56f + 0.14f * (1f - warm));
        }
    }
}

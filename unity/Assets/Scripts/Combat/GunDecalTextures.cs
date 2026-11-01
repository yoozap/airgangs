using UnityEngine;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// Port of src/guns.js:90-171's 2 procedural decal textures (makeHoleTexture,
    /// makeCrackTexture) — the gap Combat/Guns.cs's own header names ("the
    /// painted canvas hole/crack textures ... art, same category Combat/
    /// ImpactFx.cs's own 5 canvas textures were deferred under"). Combat/
    /// Guns.cs's `Cracks`/`Holes` fields are Combat/ImpactFx.cs's DecalLayer —
    /// pose/size data only; this file is the surface detail those stamps
    /// project, same split as Combat/ImpactDecalTextures.cs and
    /// Combat/DamageDecals.cs (see DamageTextures.cs's header for the shared
    /// per-pixel primitives and RNG convention).
    /// </summary>
    public static class GunDecalTextures
    {
        public static Texture2D BuildHole(uint seed = 2001)
        {
            var buf = new Color[64 * 64];
            DrawHole(buf, new DamageTextures.Rng32(seed));
            return DamageTextures.ToTexture(buf, 64, 64, "gun_hole", 4); // :121 t.anisotropy = 4
        }

        public static Texture2D BuildCrack(uint seed = 2002)
        {
            var buf = new Color[96 * 96];
            DrawCrack(buf, new DamageTextures.Rng32(seed));
            return DamageTextures.ToTexture(buf, 96, 96, "gun_crack", 4); // :169 t.anisotropy = 4
        }

        static void Blend(Color[] buf, int w, int h, int x, int y, Color c)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
            int i = y * w + x;
            buf[i] = DamageTextures.Over(buf[i], c);
        }

        // ====================================================================
        // src/guns.js:90-124 makeHoleTexture — chipped-plaster ring, a dark
        // irregular pit, a few hairline cracks. 64x64, NOT translated: every
        // coordinate in the JS is written as an absolute `32 + cos(a)*r` etc.
        // ====================================================================
        static void DrawHole(Color[] buf, DamageTextures.Rng32 rng)
        {
            var center = new Vector2(32f, 32f);

            // :95-102 — 26 chipped-plaster dabs, flat-filled small circles
            const int dabCount = 26;
            var dabPos = new Vector2[dabCount]; var dabR = new float[dabCount]; var dabColor = new Color[dabCount];
            for (int k = 0; k < dabCount; k++)
            {
                float a = rng.NextFloat() * Mathf.PI * 2f;          // :96
                float r = 10f + rng.NextFloat() * 14f;               // :97
                dabColor[k] = DamageTextures.Rgba(232, 226, 214, 0.3f + rng.NextFloat() * 0.3f); // :98
                dabPos[k] = center + new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
                dabR[k] = 2f + rng.NextFloat() * 3.5f;                // :100
            }

            // :104-111 — the dark irregular pit, a radial gradient circle
            var pitStops = new (float, Color)[]
            {
                (0f, DamageTextures.Rgba(12, 10, 9, 0.95f)), (0.55f, DamageTextures.Rgba(28, 24, 22, 0.75f)), (1f, DamageTextures.Rgba(40, 36, 32, 0f)),
            };
            const float pitR0 = 1f, pitR1 = 13f; // :104 createRadialGradient(32,32,1, 32,32,13) — same centre, exact
            const float pitFillR = 13f; // :110 arc(32,32,13,0,7)

            // :112-120 — 7 hairline cracks running out from r=6 to r~14-22
            const int crackCount = 7;
            var crackA = new Vector2[crackCount]; var crackB = new Vector2[crackCount];
            for (int k = 0; k < crackCount; k++)
            {
                float a = rng.NextFloat() * Mathf.PI * 2f;           // :113 — draw order preserved: a, then lineWidth, then r1
                _crackWidth[k] = 1f + rng.NextFloat();                // :115 g.lineWidth = 1 + Math.random()
                float r1 = 14f + rng.NextFloat() * 8f;                // :118
                crackA[k] = center + new Vector2(Mathf.Cos(a) * 6f, Mathf.Sin(a) * 6f);
                crackB[k] = center + new Vector2(Mathf.Cos(a) * r1, Mathf.Sin(a) * r1);
            }
            Color crackColor = DamageTextures.Rgba(20, 17, 15, 0.55f);

            for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                Color c = default;
                for (int k = 0; k < dabCount; k++)
                    if (DamageTextures.InCircle(p, dabPos[k], dabR[k])) c = DamageTextures.Over(c, dabColor[k]);
                float r = Vector2.Distance(p, center);
                if (r <= pitFillR) c = DamageTextures.Over(c, DamageTextures.Lerp3((r - pitR0) / Mathf.Max(1e-5f, pitR1 - pitR0), pitStops));
                for (int k = 0; k < crackCount; k++)
                    if (DamageTextures.DistToSegment(p, crackA[k], crackB[k]) < _crackWidth[k] * 0.5f) c = DamageTextures.Over(c, crackColor);
                Blend(buf, 64, 64, x, y, c);
            }
        }
        // scratch for DrawHole's 7 hairline-crack stroke widths (crackCount is
        // the JS literal :112 `k < 7`, fixed).
        static readonly float[] _crackWidth = new float[7];

        // ====================================================================
        // src/guns.js:126-171 makeCrackTexture — radial shatter rays with one
        // branch each, 2 concentric stress rings, a dark impact centre. 96x96,
        // translated to (48,48).
        // ====================================================================
        static void DrawCrack(Color[] buf, DamageTextures.Rng32 rng)
        {
            const int rays = 9;
            var rayA = new Vector2[rays]; var rayMid = new Vector2[rays]; var rayB = new Vector2[rays];
            var branchA = new Vector2[rays]; var branchB = new Vector2[rays];
            var rayColor = new Color[rays];
            for (int k = 0; k < rays; k++)
            {
                float a = (float)k / rays * Mathf.PI * 2f + rng.NextFloat() * 0.5f; // :134
                float len = 26f + rng.NextFloat() * 18f;                             // :135
                rayColor[k] = DamageTextures.Rgba(235, 245, 255, 0.5f + rng.NextFloat() * 0.3f); // :136
                float x = Mathf.Cos(a) * len, y = Mathf.Sin(a) * len;
                Vector2 jitter = new Vector2((rng.NextFloat() - 0.5f) * 6f, (rng.NextFloat() - 0.5f) * 6f); // :142
                rayA[k] = new Vector2(Mathf.Cos(a) * 3f, Mathf.Sin(a) * 3f);
                rayMid[k] = new Vector2(x * 0.55f, y * 0.55f) + jitter;
                rayB[k] = new Vector2(x, y);
                // :146-150 branch, from the (un-jittered) 0.55 point
                branchA[k] = new Vector2(x * 0.55f, y * 0.55f);
                branchB[k] = branchA[k] + new Vector2(Mathf.Cos(a + 0.9f) * 9f, Mathf.Sin(a + 0.9f) * 9f);
            }
            const float rayHalfW = 1.4f * 0.5f;    // :137 lineWidth 1.4
            const float branchHalfW = 0.8f * 0.5f; // :146 lineWidth 0.8

            // :153-158 — 2 concentric stress rings at r=8 and r=15 (+ jitter)
            const int ringCount = 2;
            var ringR = new float[ringCount];
            float[] baseR = { 8f, 15f };
            for (int k = 0; k < ringCount; k++) ringR[k] = baseR[k] + rng.NextFloat() * 2f; // :157
            Color ringColor = DamageTextures.Rgba(225, 238, 250, 0.4f);
            const float ringLineWidth = 0.9f; // :155 lineWidth 0.9 — OnRing halves this itself

            // :161-167 — dark impact centre, a small radial gradient disc
            var centerStops = new (float, Color)[] { (0f, DamageTextures.Rgba(10, 12, 14, 0.9f)), (1f, DamageTextures.Rgba(10, 12, 14, 0f)) };
            const float centerR0 = 0f, centerR1 = 5f; // :161 same centre, exact

            for (int y = 0; y < 96; y++)
            for (int x = 0; x < 96; x++)
            {
                float fx = x - 48f + 0.5f, fy = y - 48f + 0.5f;
                var p = new Vector2(fx, fy);
                Color c = default;
                for (int k = 0; k < rays; k++)
                {
                    // the ray is drawn as TWO joined segments (moveTo->mid->lineTo),
                    // matching the JS's own two lineTo calls on one open path.
                    if (DamageTextures.DistToSegment(p, rayA[k], rayMid[k]) < rayHalfW ||
                        DamageTextures.DistToSegment(p, rayMid[k], rayB[k]) < rayHalfW) c = DamageTextures.Over(c, rayColor[k]);
                    if (DamageTextures.DistToSegment(p, branchA[k], branchB[k]) < branchHalfW) c = DamageTextures.Over(c, rayColor[k]);
                }
                for (int k = 0; k < ringCount; k++)
                    if (DamageTextures.OnRing(p, Vector2.zero, ringR[k], ringLineWidth)) c = DamageTextures.Over(c, ringColor);
                float r = p.magnitude;
                if (r <= centerR1) c = DamageTextures.Over(c, DamageTextures.Lerp3((r - centerR0) / Mathf.Max(1e-5f, centerR1 - centerR0), centerStops));
                Blend(buf, 96, 96, x, y, c);
            }
        }
    }
}

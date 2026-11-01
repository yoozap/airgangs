using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Closes the real, documented gap in LandmarkPlacer.cs's 10 procedural
    /// landmarks (narikala, funicularStation, metekhi, svetitskhoveli, jvari,
    /// baths, marriott, biltmore, sheratonMetechi, radissonRed): "real massing,
    /// not a placeholder" was already true, but 8 of the 10 ALSO carry a real
    /// Commons photo in the JS (src/landmarks.js's own `photoMat(file, rx, ry,
    /// tint)` helper, line-cited below) and that photo was never applied —
    /// LandmarkPlacer.cs's ported box()/cyl() calls kept only the numeric
    /// FALLBACK tint `photoMat` draws before its photo loads (every one of the
    /// hex constants in Narikala()/Metekhi()/etc. is that literal fallback,
    /// confirmed by grep: 0x9a6f52/0xd9c8a4/0xd9cdb4/0xd3c3a2/0xcdbb97/0xc4a982/
    /// 0x9c7355/0xa87f5e/0xdfdad0/0x51707f all match photoMat's 4th argument
    /// exactly). sheratonMetechi and radissonRed are NOT in this table on
    /// purpose — landmarks.js:1185-1209 builds both from plain
    /// MeshStandardMaterial colour, no photoMat call either.
    ///
    /// DESIGN: keyed by (landmarkKey, fallbackTintHex) rather than by mesh,
    /// because LandmarkPlacer.BuildProcedural's return type
    /// (List&lt;(Mesh,Color32)&gt;) is load-bearing for LandmarkProbe.cs (an
    /// Editor probe this task may not touch) and already carries that exact
    /// tint as a Color32 — a caller can look a bucket's tint straight up here
    /// without LandmarkPlacer.cs needing a new return shape. Every tint key is
    /// unique WITHIN one landmark's own Dictionary&lt;uint,Buf&gt; (BuildProcedural
    /// buckets one mesh per distinct colour), even though two different
    /// landmarks sometimes reuse the same fallback hex for a different photo
    /// scale (narikala's and svetitskhoveli's own sameba_stone fallback is the
    /// same 0xd9c8a4 at two different (rx,ry) — scoping by landmarkKey as well
    /// as tint is exactly why that is safe here).
    ///
    /// REPEAT VALUES ARE LITERAL, NOT bays/floors. This is a SEPARATE mechanism
    /// from RealFacadeTextures.cs (which ports citymap.js's generic ordinary-
    /// building wall binder, `TilingFor = 1/bays x 1/floors`). landmarks.js's
    /// own photoMat() instead calls `texture.repeat.set(rx, ry)` with a
    /// literal pair hand-chosen per call site — reproduced here as literals,
    /// not derived from facades.json's bays/floors fields, because the two
    /// source functions disagree on them for the SAME file (e.g. sameba_stone
    /// is bays=2/floors=1 in facades.json for the generic wall binder, but
    /// narikala's own photoMat call asks for rx=3,ry=2 — a different, hand-
    /// tuned repeat for this specific surface). Unity equivalent of
    /// `texture.repeat.set` on a 0..1-UV'd mesh is `Material.mainTextureScale`.
    ///
    /// ANISOTROPY is a flat 8 for every landmark photo tile — landmarks.js:996
    /// `t.anisotropy = 8;`, unconditional, unlike RealFacadeTextures' 8/16
    /// split (that split belongs to _loadRealFacades, not photoMat).
    ///
    /// FILE LOOKUP reuses RealFacadeTextures.LoadManifest() purely to resolve
    /// the on-disk filename and licence credit (facades.json's `file`/`credit`
    /// fields) for the SAME shared manifest/texture folder — not its
    /// TilingFor/AnisoFor/Apply methods, which encode the other mechanism's
    /// own rules as documented above.
    ///
    /// NOT DONE HERE, by design (see LandmarkPlacer.cs's own header): loading
    /// the Texture2D and assigning `Material.mainTexture`/`mainTextureScale`/
    /// `anisoLevel` to an actual GameObject's renderer. That needs a
    /// GameObject/Instantiate step LandmarkPlacer.cs's "pure data, no
    /// GameObject" house style deliberately stays out of, and CityLoader.cs's
    /// own comment ("mesh/tint pairs — consumed by a rendering layer, not
    /// stored here") confirms no such layer exists yet for procedural
    /// landmarks. <see cref="TryGetFacade"/> is the one call a future
    /// rendering pass needs: given the landmark key and the Color32 tint
    /// BuildProcedural already returns per mesh bucket, decide whether (and
    /// how) to texture it.
    /// </summary>
    public static class LandmarkFacades
    {
        public struct FacadeUse
        {
            public string Kind;   // facades.json key, e.g. "sameba_stone"
            public float Rx, Ry;  // literal texture.repeat.set(rx,ry) — see header
        }

        // landmarkKey -> { fallbackTintHex -> use }. Every entry line-cited to
        // its photoMat() call in src/landmarks.js.
        static readonly Dictionary<string, Dictionary<uint, FacadeUse>> Table = new()
        {
            ["narikala"] = new Dictionary<uint, FacadeUse>
            {
                [0x9a6f52] = new FacadeUse { Kind = "oldbrick",     Rx = 6f,      Ry = 4f },    // landmarks.js:1024
                [0xd9c8a4] = new FacadeUse { Kind = "sameba_stone", Rx = 3f,      Ry = 2f },    // landmarks.js:1043
            },
            ["funicularStation"] = new Dictionary<uint, FacadeUse>
            {
                [0xd9cdb4] = new FacadeUse { Kind = "funicular_arcade", Rx = 1f, Ry = 1f },      // landmarks.js:1056
            },
            ["metekhi"] = new Dictionary<uint, FacadeUse>
            {
                [0xd3c3a2] = new FacadeUse { Kind = "sameba_stone",  Rx = 3f,   Ry = 2f },       // landmarks.js:1072
                [0xcdbb97] = new FacadeUse { Kind = "sameba_arches", Rx = 2.2f, Ry = 1f },       // landmarks.js:1075
            },
            ["svetitskhoveli"] = new Dictionary<uint, FacadeUse>
            {
                [0xd9c8a4] = new FacadeUse { Kind = "sameba_stone",  Rx = 5f,   Ry = 2.4f },     // landmarks.js:1085
                [0xcdbb97] = new FacadeUse { Kind = "sameba_arches", Rx = 2.6f, Ry = 1f },       // landmarks.js:1086/1098
            },
            ["jvari"] = new Dictionary<uint, FacadeUse>
            {
                [0xc4a982] = new FacadeUse { Kind = "jvari_stone", Rx = 3f, Ry = 2f },           // landmarks.js:1119
            },
            ["baths"] = new Dictionary<uint, FacadeUse>
            {
                [0x9c7355] = new FacadeUse { Kind = "oldbrick", Rx = 4f, Ry = 2f },              // landmarks.js:1137
                [0xa87f5e] = new FacadeUse { Kind = "oldbrick", Rx = 4f, Ry = 2f },              // landmarks.js:1138
            },
            ["marriott"] = new Dictionary<uint, FacadeUse>
            {
                [0xdfdad0] = new FacadeUse { Kind = "marriott_facade", Rx = 4.5f, Ry = 3.2f },   // landmarks.js:1155
            },
            ["biltmore"] = new Dictionary<uint, FacadeUse>
            {
                // landmarks.js:1175 — 34/21 bays-wide, 104/24 floors-tall curtain wall.
                [0x51707f] = new FacadeUse { Kind = "glass", Rx = 34f / 21f, Ry = 104f / 24f },
            },
            // sheratonMetechi, radissonRed: intentionally absent — see header.
        };

        public const int Anisotropy = 8; // landmarks.js:996, flat for every photoMat() tile.

        /// <summary>Looks up whether a given (landmarkKey, bucketTint) pair —
        /// exactly what BuildProcedural's `List&lt;(Mesh,Color32)&gt;` hands a
        /// caller, one tuple per tint bucket — should carry a real photo
        /// facade, and if so which file/tiling.</summary>
        public static bool TryGetFacade(string landmarkKey, Color32 bucketTint, out FacadeUse use)
        {
            use = default;
            if (!Table.TryGetValue(landmarkKey, out var byTint)) return false;
            uint hex = ((uint)bucketTint.r << 16) | ((uint)bucketTint.g << 8) | bucketTint.b;
            return byTint.TryGetValue(hex, out use);
        }

        /// <summary>Convenience for a future rendering pass: resolves a
        /// <see cref="FacadeUse"/> straight to the manifest Entry (file path +
        /// licence credit) via RealFacadeTextures' shared reader, so nothing
        /// re-parses facades.json a second time. Returns null if the manifest
        /// (or this kind within it) is absent — same offline-safe posture
        /// RealFacadeTextures.LoadManifest documents.</summary>
        public static RealFacadeTextures.Entry ResolveEntry(FacadeUse use, Dictionary<string, RealFacadeTextures.Entry> manifest)
            => manifest != null && manifest.TryGetValue(use.Kind, out var e) ? e : null;
    }
}

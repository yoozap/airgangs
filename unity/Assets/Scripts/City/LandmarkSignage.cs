using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/landmarks.js:44-60 <c>label(text, w, h, bg, fg, size)</c> — the
    /// canvas-texture brand signage LandmarkPlacer.cs's own header documents as
    /// dropped ("minus the photo-facade textures... same granularity
    /// BuildingBuilder.cs is already at"). This file closes exactly that one gap:
    /// the two real BRAND TEXT signs the JS paints with a 2D canvas context —
    /// "TBILISI MARRIOTT" (landmarks.js:1160-1165, inside <c>marriott()</c>) and
    /// "RADISSON RED" (landmarks.js:1202-1207, inside <c>radissonRed()</c>).
    ///
    /// WHY A GENERATED Texture2D RATHER THAN System.Drawing OR A LIVE CANVAS:
    /// Three.js's <c>label()</c> draws into an HTML <c>&lt;canvas&gt;</c> 2D
    /// context (fillRect for the background, then fillText for the brand name)
    /// and wraps the result in a CanvasTexture. Unity has no DOM canvas, and
    /// System.Drawing is not safely available under Mono/IL2CPP across every
    /// build target this project aims at (desktop + mobile + console, per
    /// docs/UNITY_PORT.md's stated goals). Rather than depend on a live
    /// World-Space-Canvas-plus-RenderTexture bake (which only produces a result
    /// when something actually renders a frame — brittle for a `-batchmode
    /// -nographics` bake pass, and overkill for two short, fixed brand strings),
    /// this ports the SAME visual result — an opaque background rectangle with
    /// a centred brand name — as a small bitmap font rasterised directly into a
    /// Texture2D's pixel buffer with plain array writes. No GPU rendering, no
    /// Canvas, no editor UI required to run: it is pure C# and works identically
    /// in batch mode. This is the same "real geometry instead of a canvas
    /// texture" trade AirportBuilder.cs already made for the runway designators
    /// and helipad H's (its Glyph5x7 table, airport.js:102-118's `numTex`) — the
    /// task brief explicitly asks for that approach to be kept for "any
    /// remaining markings"; this is its Texture2D counterpart for the two signs
    /// that are genuinely textures (applied to a quad with a real photo-style
    /// background colour), not flat-shaded 3D geometry like a runway number.
    ///
    /// THE FONT ITSELF IS NOT AirportBuilder's Glyph5x7 — that table only
    /// defines '0','1','3','L','R','H' (everything a runway designator or a
    /// helipad needed). The brand strings need a full alphabet subset
    /// (A,B,D,E,I,L,M,N,O,R,S,T + space for "TBILISI MARRIOTT" and "RADISSON
    /// RED"), so this file carries its OWN 5x7 glyph table rather than widening
    /// AirportBuilder.cs (which other agents are not touching, but the project
    /// rule is "prefer new files" and the two tables serve different alphabets
    /// for different reasons — duplication here is the lower-risk choice).
    /// </summary>
    public static class LandmarkSignage
    {
        // =====================================================================
        // 5x7 dot-matrix font — same bit convention as AirportBuilder.Glyph5x7
        // ('1' = lit, 7 rows top-to-bottom, 5 columns left-to-right), covering
        // only the letters the two brand strings need.
        // =====================================================================
        static readonly Dictionary<char, string[]> Glyph5x7 = new()
        {
            [' '] = new[] { "00000", "00000", "00000", "00000", "00000", "00000", "00000" },
            ['A'] = new[] { "01110", "10001", "10001", "11111", "10001", "10001", "10001" },
            ['B'] = new[] { "11110", "10001", "10001", "11110", "10001", "10001", "11110" },
            ['D'] = new[] { "11110", "10001", "10001", "10001", "10001", "10001", "11110" },
            ['E'] = new[] { "11111", "10000", "10000", "11110", "10000", "10000", "11111" },
            ['I'] = new[] { "11111", "00100", "00100", "00100", "00100", "00100", "11111" },
            ['L'] = new[] { "10000", "10000", "10000", "10000", "10000", "10000", "11111" },
            ['M'] = new[] { "10001", "11011", "10101", "10001", "10001", "10001", "10001" },
            ['N'] = new[] { "10001", "11001", "10101", "10011", "10001", "10001", "10001" },
            ['O'] = new[] { "01110", "10001", "10001", "10001", "10001", "10001", "01110" },
            ['R'] = new[] { "11110", "10001", "10001", "11110", "10100", "10010", "10001" },
            ['S'] = new[] { "01111", "10000", "10000", "01110", "00001", "00001", "11110" },
            ['T'] = new[] { "11111", "00100", "00100", "00100", "00100", "00100", "00100" },
        };

        /// <summary>
        /// Port of src/landmarks.js:44-60 <c>label(text, w, h, bg, fg, size)</c>.
        /// `size` (the JS's canvas font-px) has no direct meaning for a bitmap
        /// font sized to fill the texture, so it is dropped — the glyphs are
        /// scaled to fill ~70% of the texture height and centred horizontally,
        /// which is what the JS's `textAlign/textBaseline = center/middle`
        /// achieves for a single line of text.
        /// </summary>
        public static Texture2D BuildLabelTexture(string text, int w, int h, Color32 bg, Color32 fg)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
            {
                name = $"sign_{text.Replace(' ', '_')}",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = bg; // landmarks.js:49-50 fillRect

            // Scale: 7 glyph rows fill 70% of the texture height; width per cell
            // is 5 cols + 1 gap col, scaled isotropically with height so glyphs
            // stay square-pixelled, then shrunk further only if the string would
            // overrun 94% of the width (matches canvas text never being asked
            // to wrap — this project's two brand strings are short enough that
            // the height-driven scale already fits in practice).
            float scale = (h * 0.7f) / 7f;
            int n = text.Length;
            float totalW = n * 6f * scale - scale; // no trailing gap after the last glyph
            if (totalW > w * 0.94f) scale *= (w * 0.94f) / totalW;
            totalW = n * 6f * scale - scale;

            float x0 = (w - totalW) / 2f;
            float y0 = (h - scale * 7f) / 2f;

            for (int ci = 0; ci < n; ci++)
            {
                char c = char.ToUpperInvariant(text[ci]);
                if (!Glyph5x7.TryGetValue(c, out var rows)) continue; // unknown glyph: leave as background
                float cellX = x0 + ci * 6f * scale;
                for (int row = 0; row < 7; row++)
                {
                    string bits = rows[row];
                    for (int col = 0; col < 5; col++)
                    {
                        if (bits[col] != '1') continue;
                        int px0 = Mathf.FloorToInt(cellX + col * scale);
                        int px1 = Mathf.CeilToInt(cellX + (col + 1) * scale);
                        // texture row 0 is the BOTTOM in Unity's Texture2D pixel
                        // convention; the glyph's row 0 is its TOP (same as the
                        // JS's canvas, where y grows downward) — flip here once.
                        int py0 = Mathf.FloorToInt(h - (y0 + (row + 1) * scale));
                        int py1 = Mathf.CeilToInt(h - (y0 + row * scale));
                        for (int py = Mathf.Max(0, py0); py < Mathf.Min(h, py1); py++)
                            for (int pxi = Mathf.Max(0, px0); pxi < Mathf.Min(w, px1); pxi++)
                                px[py * w + pxi] = fg;
                    }
                }
            }

            tex.SetPixels32(px);
            // makeNoLongerReadable IN A PLAYER ONLY. A readable Texture2D keeps a
            // full CPU copy as well as the GPU one — measured, a 2048x240 RGBA32
            // plate reports 3.75 MB, exactly twice its pixels, and the 425
            // runtime-baked readable textures were costing 182 MB. These are
            // one-shot bakes, so the copy is pure cost in the game; it has to
            // stay in the EDITOR because Editor/Probes/SignTextInkProbe and
            // LandmarkSignageProbe prove the Georgian text by reading the pixels
            // back (GetPixels32), and a non-readable texture would make those
            // probes throw instead of verify.
            tex.Apply(false, !Application.isEditor);
            return tex;
        }

        static Color32 Hex(string rrggbb)
        {
            uint v = System.Convert.ToUInt32(rrggbb, 16);
            return new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);
        }

        /// <summary>landmarks.js:1160-1163 — the Marriott frontage sign:
        /// `label('TBILISI MARRIOTT', 512, 54, '#efece4', '#4a4a4a')`.</summary>
        // Exposed as consts, not inlined, so LandmarkSignageProbe can PRINT the
        // very values the texture is built from. A probe that retyped these would
        // only prove the probe agrees with itself; reading them here is what
        // actually proves the C# constants match landmarks.js's label() args.
        public const string MarriottText = "TBILISI MARRIOTT";
        public const int MarriottW = 512, MarriottH = 54;
        public const string MarriottBg = "efece4", MarriottFg = "4a4a4a";

        public static Texture2D MarriottSignTexture() =>
            BuildLabelTexture(MarriottText, MarriottW, MarriottH, Hex(MarriottBg), Hex(MarriottFg));

        /// <summary>landmarks.js:1202-1204 — the Radisson RED brand bar:
        /// `label('RADISSON RED', 384, 60, '#c8102e', '#ffffff')`.</summary>
        public const string RadissonRedText = "RADISSON RED";
        public const int RadissonRedW = 384, RadissonRedH = 60;
        public const string RadissonRedBg = "c8102e", RadissonRedFg = "ffffff";

        public static Texture2D RadissonRedSignTexture() =>
            BuildLabelTexture(RadissonRedText, RadissonRedW, RadissonRedH, Hex(RadissonRedBg), Hex(RadissonRedFg));

        // =====================================================================
        // The sign QUADS themselves — landmarks.js:1160-1165 (Marriott) and
        // :1202-1206 (Radisson RED). Both are `THREE.PlaneGeometry(w,h)` with
        // no rotation, added as a child of the landmark's own group at a fixed
        // LOCAL offset, material `side: THREE.DoubleSide` (visible from both
        // faces — exactly what this port gives it via Cull Off, see
        // SignMaterial below, so winding direction is immaterial here: there is
        // no backface to cull).
        //
        // HANDEDNESS: LandmarkPlacer.cs's procedural builders (Marriott(),
        // RadissonRed()) author every primitive in UNMIRRORED game-frame local
        // coordinates and mirror X exactly once at the very end (MakeMesh).
        // These sign quads are built ALREADY in the final, mirrored LOCAL frame
        // (the group's own transform, after PlaceWorld applies GeoFrame.
        // FromLatLon/YawToUnity) so they drop straight onto the same parent
        // GameObject MakeMesh's output does — the local offset is therefore
        // `(-x_js, y_js, z_js)`, the one mirror MakeMesh would have applied had
        // this quad been part of its own Buf. Because the quad is symmetric
        // about its own centre and the material culls neither face, there is no
        // winding to get wrong and no separate FlipWinding call is needed (see
        // the class header's note: double-sided removes the concern entirely).
        // </summary>
        public struct SignDef
        {
            public string Name;
            public Mesh Quad;
            public Texture2D Tex;
        }

        static Mesh BuildSignQuad(string name, float w, float h, Vector3 localCenterMirrored)
        {
            float hw = w / 2f, hh = h / 2f;
            var verts = new[]
            {
                localCenterMirrored + new Vector3(-hw, -hh, 0f),
                localCenterMirrored + new Vector3( hw, -hh, 0f),
                localCenterMirrored + new Vector3( hw,  hh, 0f),
                localCenterMirrored + new Vector3(-hw,  hh, 0f),
            };
            // U MIRRORED. The handedness note above is right that the quad is
            // symmetric about its own centre and so needs no FlipWinding — but
            // symmetry is exactly why the mirror this frame change owes the
            // quad moves no VERTEX and still has to swap the two U ends. A
            // three.js-layout texture quad (u 0..1 with local +X) reads
            // right-to-left in Unity's left-handed frame whatever its yaw, so
            // without this the hotel name is drawn backwards. Same defect and
            // same fix as ShopfrontRenderer.QuadMesh (shop-sign-text-mirrored);
            // its header has the derivation.
            float u0 = ShopfrontRenderer.MirrorSignUv ? 1f : 0f, u1 = ShopfrontRenderer.MirrorSignUv ? 0f : 1f;
            var uv = new[] { new Vector2(u0, 0), new Vector2(u1, 0), new Vector2(u1, 1), new Vector2(u0, 1) };
            var tris = new[] { 0, 1, 2, 0, 2, 3 };
            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uv);
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>landmarks.js:1160-1165. Sign local offset in the JS is
        /// (24, 15.5, 18.2) relative to the Marriott group's own origin; mirror
        /// X per the class header.</summary>
        public static SignDef MarriottSign() => new SignDef
        {
            Name = "marriott_sign",
            Quad = BuildSignQuad("marriott_sign", 17f, 1.8f, new Vector3(-24f, 15.5f, 18.2f)),
            Tex = MarriottSignTexture(),
        };

        /// <summary>landmarks.js:1202-1206. Sign local offset (0, 32, 9.2) —
        /// x is already 0, so mirroring changes nothing here, but the rule is
        /// applied uniformly rather than special-cased.</summary>
        public static SignDef RadissonRedSign() => new SignDef
        {
            Name = "radissonRed_sign",
            Quad = BuildSignQuad("radissonRed_sign", 12f, 2.2f, new Vector3(-0f, 32f, 9.2f)),
            Tex = RadissonRedSignTexture(),
        };

        /// <summary>Unlit, double-sided (Cull Off), opaque — the Unity
        /// equivalent of `new THREE.MeshBasicMaterial({ map, side:
        /// THREE.DoubleSide })` with no `transparent: true` (landmarks.js never
        /// sets it for these two signs — the background rectangle is opaque,
        /// unlike airport.js's `numTex`, which IS transparent). Built fresh per
        /// call since Texture2D assignment is the only per-sign state; callers
        /// may share one Shader lookup across both signs.</summary>
        public static Material BuildSignMaterial(Texture2D tex)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            var mat = new Material(shader) { name = tex.name + "_mat" };
            mat.SetTexture("_BaseMap", tex);
            mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            return mat;
        }
    }
}

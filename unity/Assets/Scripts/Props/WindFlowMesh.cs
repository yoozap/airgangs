using UnityEngine;

namespace TbilisiFly.Props
{
    // =========================================================================
    // Port of src/crane.js:1003-1066 — the seven moving-air SHEETS' mesh
    // generation. WindTunnel.cs's header explicitly left this unported
    // ("needs a loaded aeroplane's measured box to bend around"); that need
    // is exactly WindRider.PlaneBox, already ported and populated from plain
    // floats (WindRider.cs) — no GLB load, Mesh, or live scene object is
    // needed to exercise this file.
    //
    // PORTED, in full: the stream's upstream/downstream extent (crane.js:
    // 1003-1004), and the per-sheet position/UV/vertex-colour generation
    // loop (crane.js:1017-1066) including Blockage(x) (crane.js:1008-1013,
    // not re-defined here — see BlockageAt, which forwards to WindRider.
    // Geometry.Blockage, the SAME formula already ported there; keeping one
    // copy rather than two was a deliberate choice, not an oversight).
    //
    // NOT PORTED: hazeTexture()'s assignment and the flow mesh's placement
    // in the crane's own frame (crane.js:1079-1087) — trivial transform-
    // copy glue that needs a live Transform; and the per-frame visibility/
    // opacity/scroll driving, which is WindTunnel.Step's own output
    // (flowOpacity/scrollDx/scrollDy), already ported there — this file only
    // builds the STATIC mesh those per-frame values are applied to (the UV
    // scroll is a material offset, not a vertex rebuild, crane.js:1069 's
    // `map: tex` with the per-frame scrollDx/Dy — mesh data here never
    // changes frame to frame, exactly like the JS's own `mergeGeometries`
    // built once in `windTunnel()`).
    //
    // ============================== HANDEDNESS ==============================
    // No cross product anywhere in this file — every value is a scalar
    // formula or a direct linear combination of x/y/z. Nothing here needs
    // an operand swap; a caller mirrors X only at the point these vertices
    // are placed into a real Mesh under a GeoFrame-converted parent, same
    // as every other pure-math file in this folder.
    // =========================================================================
    public static class WindFlowMesh
    {
        public const int Sheets = WindTunnel.Sheets;   // crane.js:894
        public const int SheetX = WindTunnel.SheetX;    // crane.js:895
        public const int SheetZ = WindTunnel.SheetZ;    // crane.js:896

        /// <summary>crane.js:1003-1004 — how far upstream/downstream the
        /// sheets extend, off the aeroplane's own measured length.</summary>
        public static void StreamExtent(WindRider.PlaneBox box, out float z0, out float z1)
        {
            z0 = box.MinZ - box.Len * 0.8f;
            z1 = box.MaxZ + box.Len * 1.1f;
        }

        /// <summary>crane.js:1008-1013 `blockage(x)`, restated here only to
        /// keep this file self-contained for a reader — it FORWARDS to
        /// WindRider.Geometry.Blockage rather than redefining the formula a
        /// second time.</summary>
        public static (float y, float h) BlockageAt(WindRider.Geometry geo, float x) => geo.Blockage(x);

        /// <summary>One sheet of Sheets (k = 0..Sheets-1), crane.js:1017-1066
        /// in full. `positions`/`uvs`/`colors` are each (SheetX+1)*(SheetZ+1)
        /// long, row-major by z then x exactly as the JS builds them
        /// (crane.js:1023's outer loop over j/z, 1029's inner loop over
        /// i/x) — index = j*(SheetX+1)+i, matching BuildGridIndices below.</summary>
        public static void BuildSheet(WindRider.PlaneBox box, int k, float z0, float z1,
            out Vector3[] positions, out Vector2[] uvs, out Color[] colors)
        {
            var geo = WindRider.Geometry.From(box);
            // crane.js:1018 `lift = (k/(SHEETS-1) - 0.5) * hgt * 1.75`
            float lift = (k / (float)(Sheets - 1) - 0.5f) * geo.Hgt * 1.75f;
            int w = SheetX + 1;
            int rows = SheetZ + 1;
            positions = new Vector3[w * rows];
            uvs = new Vector2[w * rows];
            colors = new Color[w * rows];

            for (int j = 0; j <= SheetZ; j++)
            {
                float tz = j / (float)SheetZ;
                float z = z0 + (z1 - z0) * tz;
                float u = (z - geo.Zc) / (geo.Len * 0.42f);
                float bump = Mathf.Exp(-u * u);
                float wake = z > geo.Zc ? (1f - Mathf.Exp(-(z - geo.Zc) / (geo.Len * 0.5f))) : 0f;
                for (int i = 0; i <= SheetX; i++)
                {
                    float tx = i / (float)SheetX;
                    float x = (tx - 0.5f) * geo.Span * 1.34f;
                    var bl = BlockageAt(geo, x);
                    float sy = geo.Yc + lift;
                    float over = sy >= bl.y ? 1f : -1f;
                    float miss = Mathf.Abs(sy - bl.y);
                    float clear = bl.h + geo.Hgt * 0.13f;
                    float push = bl.h <= 0f ? 0f
                        : Mathf.Max(0f, clear - miss) * (0.9f + 0.5f * Mathf.Exp(-miss / (geo.Hgt * 0.4f)));

                    int idx = j * w + i;
                    positions[idx] = new Vector3(
                        x,
                        sy + over * push * bump - over * push * wake * 0.35f,
                        z);
                    // the haze is stretched LONG down the stream (crane.js:1040-1042)
                    uvs[idx] = new Vector2(tz * 3.1f + k * 0.37f, tx * 0.9f + k * 0.21f);
                    // brightens where it is being squeezed, fades at the sheet's own edges (crane.js:1044-1049)
                    float edge = Mathf.Sin(Mathf.PI * tx) * Mathf.Min(1f, Mathf.Sin(Mathf.PI * tz) * 2.4f);
                    float squeeze = 0.45f + 1.5f * (push / (geo.Hgt * 0.5f)) * bump;
                    float c = Mathf.Max(0f, edge) * Mathf.Min(1.8f, squeeze);
                    colors[idx] = new Color(c, c, c, 1f);
                }
            }
        }

        /// <summary>crane.js:1052-1058 — the shared quad-grid index buffer,
        /// IDENTICAL for every sheet (same SheetX/SheetZ every time), so a
        /// caller builds it once rather than per sheet.</summary>
        public static int[] BuildGridIndices()
        {
            int w = SheetX + 1;
            var idx = new int[SheetZ * SheetX * 6];
            int n = 0;
            for (int j = 0; j < SheetZ; j++)
                for (int i = 0; i < SheetX; i++)
                {
                    int a = j * w + i;
                    idx[n++] = a; idx[n++] = a + 1; idx[n++] = a + w;
                    idx[n++] = a + 1; idx[n++] = a + w + 1; idx[n++] = a + w;
                }
            return idx;
        }
    }
}

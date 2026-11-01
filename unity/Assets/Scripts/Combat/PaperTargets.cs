using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// Port of src/papertargets.js — the ISSF 25 m precision pistol card, the
    /// hole a bullet actually leaves in it, and the scoring rule. Cited
    /// `// src/papertargets.js:NNN` throughout.
    ///
    /// ================================ SPLIT INTO TWO HALVES ===================
    /// `IssfTarget`/`B27Target` below are PURE scoring math — no Transform, no
    /// Texture, every input a plain float — lifted out of `hit()`'s body
    /// (papertargets.js:297-398, where it is inlined, not its own function) so
    /// it can be probed in isolation (RangeProbe.cs calls it directly) exactly
    /// as tools/_probe_range.mjs transcribes the same arithmetic on the JS side
    /// (that script's own header explains why it transcribes rather than
    /// imports: the JS version is inlined inside a DOM-constructing class).
    /// `PaperTargets`/`Card` below are the Unity-side mesh/texture plumbing —
    /// behaviourally the same shape as the JS class, Texture2D standing in for
    /// the JS's canvas.
    ///
    /// ================================ THE WORLD-SCALE GOTCHA ===================
    /// src/papertargets.js:312-318's own comment: a sheet hangs from a pivot at
    /// its top edge and the range model comes in at 1/100 scale, so the card's
    /// CENTRE has to be read through the mesh's FULL world matrix (position AND
    /// scale), never `mesh.position` or a bare rotated offset — the recorded
    /// project-wide gotcha ("the target centre must be in WORLD scale, not
    /// local"). Unity's `Transform.TransformPoint` already folds in
    /// `lossyScale` by construction, so `Card.WorldCentre()` below is the
    /// direct, correct equivalent of the JS's `mesh.localToWorld(bbox.center)`
    /// — AS LONG AS this file calls `TransformPoint`, never
    /// `mesh.position + rotated-offset`. Flagged here because it is exactly
    /// the kind of one-line regression the original JS gotcha was.
    ///
    /// ================================ HANDEDNESS =============================
    /// One cross product: `nrm = rx × uy` (the card's face normal,
    /// papertargets.js:335) — SWAPPED below to `Cross(uy, rx)` per the project
    /// rule. It is immaterial in practice: the only consumer is
    /// `cosI = |dot(nrm, dir)|`, and `|dot(n,d)| == |dot(-n,d)|`, so a
    /// sign-flipped normal scores identically either way — ported correctly
    /// anyway rather than relying on that cancellation. Every other vector
    /// here is a dot product, a basis-column read (`Transform` rotation
    /// columns, handedness-safe the same way RifleHolds.cs's bore-direction
    /// reads are), or `(-sin(yaw), 0, -cos(yaw))`-style, none of which need a
    /// swap (same precedent as FootController.cs's own header).
    ///
    /// ================================ NOT PAINTED =============================
    /// The ring NUMBERS printed in the four white rings (papertargets.js:280-
    /// 293, pure decoration — scoring reads real-world millimetres off the
    /// mesh, never the pixel under a shot) are not rasterised: no bitmap font
    /// is implemented here. Flagged, not hidden, same spirit as this port's
    /// other documented reduced-fidelity notes (e.g. FootPoses.RifleShoulder's
    /// own header). Everything that affects what a shot SCORES — the ring
    /// lines, the black aiming mark, the hole itself, the B-27 printed face —
    /// is painted.
    /// </summary>
    public static class IssfTarget
    {
        public const float CardMm = 500f;   // papertargets.js:33
        public const float RingMm = 50f;    // papertargets.js:34
        public const int BlackRing = 7;     // papertargets.js:35
        public const int Rings = 10;        // papertargets.js:47
        public const float InnerTenMm = 25f; // papertargets.js:44
        public const int Px = 1024;         // papertargets.js:36

        public static float RingRadiusMm(int ring) => RingMm / 2f + (Rings - ring) * (RingMm / 2f);

        public struct Shot { public float XMm, YMm, RMm, EdgeMm; public int Ring; public bool Inner; }

        /// <summary>src/papertargets.js:375-382 — the open-ring (ISSF) rule: a
        /// shot takes the HIGHER ring if the hole BREAKS THE LINE, so it is
        /// scored on the hole's inner edge (r - bullet radius), not its centre
        /// — a plug-gauge rule, not "nearest ring to the centre point".</summary>
        public static Shot ScoreOpenRing(float xmm, float ymm, float bulletMm, float cardMm = CardMm)
        {
            float r = Mathf.Sqrt(xmm * xmm + ymm * ymm) * (CardMm / cardMm);
            float edge = Mathf.Max(0f, r - bulletMm / 2f);
            int ring = Mathf.Clamp(Rings + 1 - Mathf.CeilToInt(edge / (RingMm / 2f)), 0, Rings);
            bool inner = edge <= InnerTenMm / 2f;
            return new Shot { XMm = xmm, YMm = ymm, RMm = r, EdgeMm = edge, Ring = ring, Inner = inner };
        }
    }

    /// <summary>src/papertargets.js:61-66 B27 — the printed police-silhouette
    /// face's own measured ring geometry (an oval, off-centre: it sits where a
    /// chest sits), read off the texture along four diagonals.</summary>
    public static class B27Target
    {
        public const float Cu = 0.4993f, Cv = 0.3590f;
        public const float Au = 0.0469f, Av = 0.0703f;
        public static readonly (float Lim, int Val)[] Rings = { (1.16f, 10), (2.33f, 9), (3.50f, 8), (4.70f, 7) };
        public const int Body = 5;

        /// <summary>src/papertargets.js:354-374. `u,v` texture coordinates (v
        /// already flip-corrected by the caller, matching the JS's own
        /// `flip ? 1-uv.y : uv.y`), `cardWm` the sheet's real width in metres,
        /// `underDark` whatever was sampled under the hole before it was
        /// punched (the silhouette vs. bare paper).</summary>
        public static (int Ring, bool Inner, float EdgeMm) Score(float u, float v, float bulletMm, float cardWm, bool underDark)
        {
            float k = Mathf.Sqrt(Mathf.Pow((u - Cu) / Au, 2f) + Mathf.Pow((v - Cv) / Av, 2f));
            float bite = (bulletMm / 2f / 1000f) / (Au * cardWm);
            float edge = Mathf.Max(0f, k - bite);
            int ring = 0;
            foreach (var (lim, val) in Rings) { if (edge <= lim) { ring = val; break; } }
            if (ring == 0) ring = underDark ? Body : 0;
            return (ring, ring == 10, edge);
        }
    }

    /// <summary>
    /// Port of src/papertargets.js's `PaperTargets` class — the sheet-as-mesh
    /// bookkeeping, the Texture2D standing in for the JS's canvas, and the
    /// live-card memory pool (papertargets.js:43 LIVE_CARDS — a few most-
    /// recently-shot cards keep their own texture, the rest share one blank).
    /// </summary>
    public sealed class PaperTargets
    {
        const int LiveCards = 6;     // papertargets.js:43
        const int MaxShots = 400;    // papertargets.js:45

        public sealed class Shot { public float X, Y, R; public int Ring; public bool Inner; }

        public sealed class Card
        {
            public Transform MeshT;
            public MeshFilter Filter;
            public Renderer Rend;
            public int FlatAxis, UpAxis, RightAxis;
            public float W, H;           // world metres
            public float CardMm;
            public int Cw, Ch;
            public float PxPerMm;

            public Texture2D Tex;        // this card's OWN texture, only once live
            public Material LiveMat;
            public Material OriginalMat; // the sheet's printed material, if any (card.printed)
            public Texture2D Face;       // the printed face's pixels, if readable (card.face)
            public Color32[] Buf;        // canvas-convention buffer: row 0 = TOP (see header)
            public bool Flip;            // mirrors the JS's `tex.flipY !== false` test

            public readonly List<Shot> Shots = new List<Shot>();
            public float Score, Spread; public int Tens;

            public Vector3 LocalBoundsCenter;
            public bool IsLive => Tex != null;
        }

        readonly Dictionary<Transform, Card> _sheets = new Dictionary<Transform, Card>();
        readonly List<Card> _live = new List<Card>();
        static readonly Dictionary<(int cw, int ch, int cardMm), (Texture2D tex, Material mat)> _blanks = new Dictionary<(int, int, int), (Texture2D, Material)>();

        public Card Active { get; private set; }

        // =====================================================================
        // src/papertargets.js:83-139 adopt — measure the sheet's real size and
        // which of its own three axes is flat/up/right, then give it a blank
        // ISSF face it owns outright (no canvas allocated yet — see header).
        // =====================================================================
        public Card Adopt(Transform meshT)
        {
            if (meshT == null || _sheets.ContainsKey(meshT)) return _sheets.TryGetValue(meshT, out var ex) ? ex : null;
            var mf = meshT.GetComponent<MeshFilter>();
            var rend = meshT.GetComponent<Renderer>();
            if (mf == null || mf.sharedMesh == null || rend == null) return null;

            Bounds lb = mf.sharedMesh.bounds; // LOCAL bounds
            Vector3 size = lb.size;
            // the thinnest local axis is the paper's thickness; the other two are width/height
            int[] axes = { 0, 1, 2 };
            System.Array.Sort(axes, (a, b) => size[a].CompareTo(size[b]));
            int flat = axes[0];
            int planeA = axes[1], planeB = axes[2];

            Vector3 ColumnOf(int axis) => axis switch
            {
                0 => meshT.right,
                1 => meshT.up,
                _ => meshT.forward,
            };
            // which in-plane axis hangs more vertically in the WORLD decides "up"
            int vert = Mathf.Abs(ColumnOf(planeA).y) > Mathf.Abs(ColumnOf(planeB).y) ? planeA : planeB;
            int right = (vert == planeA) ? planeB : planeA;

            float WorldExtent(int axis) => Mathf.Abs(size[axis] * meshT.lossyScale[axis]);
            float wM = WorldExtent(right); if (wM < 1e-5f) wM = 0.5f;
            float hM = WorldExtent(vert); if (hM < 1e-5f) hM = 0.5f;

            var card = new Card
            {
                MeshT = meshT, Filter = mf, Rend = rend,
                FlatAxis = flat, UpAxis = vert, RightAxis = right,
                W = wM, H = hM,
                LocalBoundsCenter = lb.center,
            };
            card.CardMm = Mathf.Min(IssfTarget.CardMm, wM * 1000f, hM * 1000f);
            card.Cw = IssfTarget.Px;
            card.Ch = Mathf.Max(64, Mathf.RoundToInt(IssfTarget.Px * hM / wM));
            card.PxPerMm = card.Cw / (wM * 1000f);

            // the printed face, if the sheet's current material has a READABLE
            // texture (import-time "Read/Write Enabled" — cannot be forced from
            // here without the Editor; falls back to the painted blank, exactly
            // the JS's own `!printed` branch, if it is not).
            card.Face = TryReadableTexture(rend.sharedMaterial);
            card.OriginalMat = rend.sharedMaterial;
            if (card.Face == null) rend.sharedMaterial = BlankMaterialFor(card);

            _sheets[meshT] = card;
            return card;
        }

        static Texture2D TryReadableTexture(Material mat)
        {
            if (mat == null || mat.mainTexture == null) return null;
            var tex = mat.mainTexture as Texture2D;
            if (tex == null) return null;
            try { tex.GetPixel(0, 0); return tex; } // throws if not marked readable
            catch { return null; }
        }

        Material BlankMaterialFor(Card card)
        {
            var key = (card.Cw, card.Ch, Mathf.RoundToInt(card.CardMm));
            if (_blanks.TryGetValue(key, out var had))
                return had.mat;
            var tex = new Texture2D(card.Cw, card.Ch, TextureFormat.RGBA32, false, false) { name = $"target_blank_{key.Item1}x{key.Item2}" };
            var buf = new Color32[card.Cw * card.Ch];
            Paint(card, buf);
            PushBuffer(tex, buf, card.Cw, card.Ch);
            var mat = card.OriginalMat != null ? new Material(card.OriginalMat) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.mainTexture = tex;
            _blanks[key] = (tex, mat);
            return mat;
        }

        // the first round through this card: it gets a canvas (texture) of its
        // own — src/papertargets.js:177-222 _goLive
        Card GoLive(Card card)
        {
            if (card.IsLive) return card;
            card.Buf = new Color32[card.Cw * card.Ch];
            if (card.Face != null)
            {
                // resampled into this card's own working resolution (a simplification
                // of the JS, which keeps the printed face's native resolution —
                // see header's "not painted" note for the other simplification).
                var src = card.Face.GetPixels32();
                int sw = card.Face.width, sh = card.Face.height;
                for (int y = 0; y < card.Ch; y++)
                {
                    int sy = Mathf.Clamp(sh - 1 - (y * sh / card.Ch), 0, sh - 1); // canvas row0=top -> texture row0=bottom
                    for (int x = 0; x < card.Cw; x++)
                    {
                        int sx = Mathf.Clamp(x * sw / card.Cw, 0, sw - 1);
                        card.Buf[y * card.Cw + x] = src[sy * sw + sx];
                    }
                }
            }
            else
            {
                Paint(card, card.Buf);
            }
            card.Tex = new Texture2D(card.Cw, card.Ch, TextureFormat.RGBA32, false, false) { name = $"target_live_{card.MeshT.GetEntityId()}" };
            PushBuffer(card.Tex, card.Buf, card.Cw, card.Ch);
            card.LiveMat = card.OriginalMat != null ? new Material(card.OriginalMat) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            card.LiveMat.mainTexture = card.Tex;
            card.Rend.sharedMaterial = card.LiveMat;

            _live.Add(card);
            while (_live.Count > LiveCards) Retire(_live[0]);
            return card;
        }

        void Retire(Card card)
        {
            _live.Remove(card);
            card.Rend.sharedMaterial = card.OriginalMat != null ? card.OriginalMat : BlankMaterialFor(card);
            if (card.Tex != null) Object.Destroy(card.Tex);
            if (card.LiveMat != null) Object.Destroy(card.LiveMat);
            card.Tex = null; card.LiveMat = null; card.Buf = null;
            card.Shots.Clear(); card.Score = 0f; card.Tens = 0; card.Spread = 0f;
            if (Active == card) Active = null;
        }

        // =====================================================================
        // src/papertargets.js:301-398 hit — a round goes through. `point` world
        // space, `dir` the round's own direction, `uv` the raycast's texture
        // coordinate if the collider provided one (RaycastHit.textureCoord).
        // =====================================================================
        public Shot Hit(Transform meshT, Vector3 point, Vector3 dir, float bulletMm, Vector2? uv = null)
        {
            if (!_sheets.TryGetValue(meshT, out var card)) return null;
            GoLive(card);

            Vector3 rx = AxisWorld(card.MeshT, card.RightAxis).normalized;
            Vector3 uy = AxisWorld(card.MeshT, card.UpAxis).normalized;
            if (uy.y < 0f) uy = -uy;

            // THE WORLD-SCALE CENTRE — see this file's header. TransformPoint
            // folds in lossyScale; a bare `meshT.position` does not.
            Vector3 centre = card.MeshT.TransformPoint(card.LocalBoundsCenter);
            Vector3 off = point - centre;
            float xm = Vector3.Dot(off, rx);
            float ym = Vector3.Dot(off, uy);

            int cw = card.Cw, ch = card.Ch;
            bool flip = card.Flip;
            float px = uv.HasValue ? uv.Value.x * cw : cw / 2f + xm * 1000f * card.PxPerMm;
            float py = uv.HasValue ? (flip ? 1f - uv.Value.y : uv.Value.y) * ch : ch / 2f - ym * 1000f * card.PxPerMm;
            if (px < -40f || py < -40f || px > cw + 40f || py > ch + 40f) return null;

            // the bullet's own diameter, stretched by the angle it arrived at
            Vector3 nrm = Vector3.Cross(uy, rx).normalized; // SWAPPED (this file's header) — sign is a don't-care here (abs())
            float cosI = Mathf.Max(0.2f, Mathf.Abs(Vector3.Dot(nrm, dir)));
            Vector3 dirFlat = dir - nrm * Vector3.Dot(dir, nrm);
            float ang = Mathf.Atan2(Vector3.Dot(dirFlat, uy), Vector3.Dot(dirFlat, rx));
            float pxPerMmLocal = cw / (card.W * 1000f);

            bool underDark = SampleDark(card, (int)px, (int)py);
            Punch(card, px, py, bulletMm * pxPerMmLocal, 1f / cosI, ang);

            float xmm = xm * 1000f, ymm = ym * 1000f;
            int ring; bool inner; float rOut;
            if (card.Face != null && uv.HasValue)
            {
                float u = uv.Value.x;
                float v = flip ? 1f - uv.Value.y : uv.Value.y;
                var (bRing, bInner, _) = B27Target.Score(u, v, bulletMm, card.W, underDark);
                ring = bRing; inner = bInner;
                rOut = Mathf.Sqrt(Mathf.Pow((u - B27Target.Cu) * card.W, 2f) + Mathf.Pow((v - B27Target.Cv) * card.H, 2f)) * 1000f;
            }
            else
            {
                var s = IssfTarget.ScoreOpenRing(xmm, ymm, bulletMm, card.CardMm);
                ring = s.Ring; inner = s.Inner; rOut = Mathf.Sqrt(xmm * xmm + ymm * ymm);
            }

            var shot = new Shot { X = xmm, Y = ymm, R = rOut, Ring = ring, Inner = inner };
            card.Shots.Add(shot);
            if (card.Shots.Count > MaxShots) card.Shots.RemoveAt(0);
            card.Score += ring;
            if (inner) card.Tens++;
            foreach (var s in card.Shots)
            {
                float d = Mathf.Sqrt(Mathf.Pow(s.X - shot.X, 2f) + Mathf.Pow(s.Y - shot.Y, 2f));
                if (d > card.Spread) card.Spread = d;
            }
            Active = card;
            return shot;
        }

        static Vector3 AxisWorld(Transform t, int axis) => axis switch { 0 => t.right, 1 => t.up, _ => t.forward };

        bool SampleDark(Card card, int x, int y)
        {
            x = Mathf.Clamp(x, 0, card.Cw - 1); y = Mathf.Clamp(y, 0, card.Ch - 1);
            Color32 c = card.Buf[y * card.Cw + x];
            float lum = (c.r * 0.3f + c.g * 0.6f + c.b * 0.1f) / 255f;
            return lum < 0.5f;
        }

        // =====================================================================
        // src/papertargets.js:241-295 the blank face — paper colour, grain, the
        // black aiming mark, the ring lines, the inner ten. Numbers NOT painted
        // (header).
        // =====================================================================
        static readonly Color32 Paper = new Color32(0xef, 0xec, 0xe4, 255);
        static readonly Color32 Black = new Color32(0x15, 0x16, 0x1a, 255);

        static void Paint(Card card, Color32[] buf)
        {
            int cw = card.Cw, ch = card.Ch;
            for (int i = 0; i < buf.Length; i++) buf[i] = Paper;

            float cx = cw / 2f, cy = ch / 2f;
            float k = card.PxPerMm * (card.CardMm / IssfTarget.CardMm);
            var rnd = new System.Random(12345); // deterministic grain, unlike the JS's Math.random()
            for (int i = 0; i < 2600; i++)
            {
                int x = rnd.Next(cw), y = rnd.Next(ch);
                byte v = (byte)(rnd.NextDouble() < 0.5 ? 0 : 255);
                BlendPx(buf, cw, ch, x, y, new Color32(v, v, v, 255), 0.05f);
            }

            float blackR = IssfTarget.RingRadiusMm(IssfTarget.BlackRing) * k;
            FillCircle(buf, cw, ch, cx, cy, blackR, Black, 1f);

            for (int ring = 1; ring <= IssfTarget.Rings; ring++)
            {
                float r = IssfTarget.RingRadiusMm(ring) * k;
                if (r > Mathf.Max(cx, cy)) continue;
                var col = ring >= IssfTarget.BlackRing ? new Color32(255, 255, 255, 217) : new Color32(20, 20, 24, 230);
                StrokeCircle(buf, cw, ch, cx, cy, r, Mathf.Max(1.5f, 0.6f * k), col);
            }
            StrokeCircle(buf, cw, ch, cx, cy, (IssfTarget.InnerTenMm / 2f) * k, Mathf.Max(1f, 0.4f * k), new Color32(255, 255, 255, 191));
        }

        static void BlendPx(Color32[] buf, int cw, int ch, int x, int y, Color32 c, float a)
        {
            if (x < 0 || y < 0 || x >= cw || y >= ch) return;
            int i = y * cw + x;
            buf[i] = Color32.Lerp(buf[i], c, a);
        }

        static void FillCircle(Color32[] buf, int cw, int ch, float cx, float cy, float r, Color32 c, float a)
        {
            int x0 = Mathf.Max(0, Mathf.FloorToInt(cx - r)), x1 = Mathf.Min(cw - 1, Mathf.CeilToInt(cx + r));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(cy - r)), y1 = Mathf.Min(ch - 1, Mathf.CeilToInt(cy + r));
            float r2 = r * r;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    if (dx * dx + dy * dy <= r2) BlendPx(buf, cw, ch, x, y, c, a);
                }
        }

        static void StrokeCircle(Color32[] buf, int cw, int ch, float cx, float cy, float r, float lineW, Color32 c)
        {
            int x0 = Mathf.Max(0, Mathf.FloorToInt(cx - r - lineW)), x1 = Mathf.Min(cw - 1, Mathf.CeilToInt(cx + r + lineW));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(cy - r - lineW)), y1 = Mathf.Min(ch - 1, Mathf.CeilToInt(cy + r + lineW));
            float half = lineW / 2f;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    float d = Mathf.Abs(Mathf.Sqrt(dx * dx + dy * dy) - r);
                    if (d <= half) BlendPx(buf, cw, ch, x, y, c, c.a / 255f);
                }
        }

        // src/papertargets.js:414-458 _punch — the wipe ring, the torn-fibre
        // hole, a few fibres pushed through the back. `elong` stretches the
        // hole along `ang` (a round through the card at an angle cuts an oval).
        static void Punch(Card card, float px, float py, float dpx, float elong, float ang)
        {
            float r = dpx / 2f;
            float cosA = Mathf.Cos(ang), sinA = Mathf.Sin(ang);
            // local (rotated, then x-scaled by elong) <- world delta
            Vector2 ToLocal(float dx, float dy)
            {
                float rx = dx * cosA + dy * sinA;
                float ry = -dx * sinA + dy * cosA;
                return new Vector2(rx / elong, ry);
            }
            float reach = r * 1.9f * Mathf.Max(elong, 1f);
            int x0 = Mathf.Max(0, Mathf.FloorToInt(px - reach)), x1 = Mathf.Min(card.Cw - 1, Mathf.CeilToInt(px + reach));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(py - reach)), y1 = Mathf.Min(card.Ch - 1, Mathf.CeilToInt(py + reach));
            var rnd = new System.Random((int)(px * 73856093) ^ (int)(py * 19349663));
            // a jagged rim, sampled at fixed angles once per punch (matches the
            // JS's own fixed `steps=18` polygon, papertargets.js:435-443)
            const int steps = 18;
            var rim = new float[steps + 1];
            for (int i = 0; i <= steps; i++) rim[i] = r * (0.9f + (float)rnd.NextDouble() * 0.2f);

            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    Vector2 lp = ToLocal(x + 0.5f - px, y + 0.5f - py);
                    float d = lp.magnitude;
                    if (d <= r * 1.75f)
                    {
                        float wipeA = d <= r * 0.7f ? 0.85f : Mathf.Lerp(0.85f, 0f, Mathf.InverseLerp(r * 0.7f, r * 1.75f, d));
                        if (wipeA > 0f) BlendPx(card.Buf, card.Cw, card.Ch, x, y, new Color32(80, 78, 76, 255), wipeA * 0.6f);
                    }
                    float a = Mathf.Atan2(lp.y, lp.x);
                    if (a < 0f) a += Mathf.PI * 2f;
                    float frac = a / (Mathf.PI * 2f) * steps;
                    int i0 = Mathf.FloorToInt(frac) % steps;
                    float rr = Mathf.Lerp(rim[i0], rim[(i0 + 1) % (steps + 1)], frac - Mathf.Floor(frac));
                    if (d <= rr) BlendPx(card.Buf, card.Cw, card.Ch, x, y, new Color32(11, 11, 13, 255), 1f);
                }
            card.Tex?.SetPixels32(FlipRows(card.Buf, card.Cw, card.Ch));
            card.Tex?.Apply(false);
        }

        static Color32[] FlipRows(Color32[] src, int w, int h)
        {
            var o = new Color32[src.Length];
            for (int y = 0; y < h; y++)
                System.Array.Copy(src, y * w, o, (h - 1 - y) * w, w);
            return o;
        }

        static void PushBuffer(Texture2D tex, Color32[] buf, int w, int h)
        {
            tex.SetPixels32(FlipRows(buf, w, h));
            tex.Apply(false);
        }

        // src/papertargets.js:461-476 reset — a fresh card
        public bool Reset(Card card = null)
        {
            card ??= Active;
            if (card == null || !card.IsLive) return false;
            card.Shots.Clear(); card.Score = 0f; card.Tens = 0; card.Spread = 0f;
            if (card.Face != null)
            {
                var src = card.Face.GetPixels32();
                int sw = card.Face.width, sh = card.Face.height;
                for (int y = 0; y < card.Ch; y++)
                {
                    int sy = Mathf.Clamp(sh - 1 - (y * sh / card.Ch), 0, sh - 1);
                    for (int x = 0; x < card.Cw; x++)
                    {
                        int sx = Mathf.Clamp(x * sw / card.Cw, 0, sw - 1);
                        card.Buf[y * card.Cw + x] = src[sy * sw + sx];
                    }
                }
            }
            else Paint(card, card.Buf);
            PushBuffer(card.Tex, card.Buf, card.Cw, card.Ch);
            return true;
        }

        /// <summary>A plain snapshot of the score card's own fields for a UI to
        /// render however it likes — src/papertargets.js's `#target-card` DOM
        /// element (:479-536) is not ported; building/binding a uGUI/UI Toolkit
        /// panel is /UI/'s territory, out of this port's scope (hard rule).
        /// ClockHour/RMm ARE real logic lifted out of that DOM tail
        /// (papertargets.js:525-528 — "where on the clock face it landed, which
        /// is how a shot is called") rather than left for a UI author to
        /// reinvent: ClockHour is 0 when the shot is inside 8mm of centre (no
        /// readout, matching `shot.r > 8 ? ... : ''`), else 1-12 with 0
        /// wrapped to 12 exactly as JS's `|| 12` does.</summary>
        public readonly struct ScoreSnapshot
        {
            public readonly int Ring, Sum, N; public readonly bool Inner; public readonly float Avg, GroupMm;
            public readonly int ClockHour; public readonly float RMm;
            public ScoreSnapshot(int ring, bool inner, int sum, int n, float avg, float groupMm, int clockHour, float rMm)
            { Ring = ring; Inner = inner; Sum = sum; N = n; Avg = avg; GroupMm = groupMm; ClockHour = clockHour; RMm = rMm; }
        }

        public ScoreSnapshot Snapshot(Card card, Shot last)
        {
            int n = card.Shots.Count;
            // src/papertargets.js:525-528 clock — atan2(x,y), not the usual
            // atan2(y,x): angle measured clockwise from north, which is what a
            // clock face wants. 6/PI turns radians into hours (2*PI rad = 12 h).
            int clockHour = 0;
            float rMm = last?.R ?? 0f;
            if (last != null && last.R > 8f)
            {
                int h = TbilisiFly.Data.JsMath.Round(((Mathf.Atan2(last.X, last.Y) / Mathf.PI * 6f) + 12f) % 12f);
                clockHour = h == 0 ? 12 : h;
            }
            return new ScoreSnapshot(last?.Ring ?? 0, last?.Inner ?? false, Mathf.RoundToInt(card.Score), n,
                n > 0 ? card.Score / n : 0f, card.Spread, clockHour, rMm);
        }

        public void Dispose()
        {
            foreach (var card in _sheets.Values) { if (card.Tex != null) Object.Destroy(card.Tex); if (card.LiveMat != null) Object.Destroy(card.LiveMat); }
            foreach (var (tex, mat) in _blanks.Values) { Object.Destroy(tex); Object.Destroy(mat); }
            _blanks.Clear();
            _sheets.Clear();
            _live.Clear();
        }
    }
}

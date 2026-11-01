using System;
using UnityEngine;
using TbilisiFly.Combat;

namespace TbilisiFly.Fx
{
    /// <summary>
    /// Draws plane damage. Combat/PlaneDamage.cs builds five
    /// <see cref="PlaneMarkLayer"/> ring buffers — Holes 3200, Tears 1600,
    /// Exits 1600, Scrapes 800, Craze 800 (src/planedamage.js:60-63, :133) —
    /// and, in its own words, leaves them as "pure data: Pose/Tint/Live drive
    /// whatever draws them". Nothing drew them. Combat/DamageDecals.cs bakes
    /// the matching 2x2 atlas art (src/planedamage.js:1678-1960) and was
    /// constructed only by an Editor probe. So the shipped state was the exact
    /// inverse of the rule src/planedamage.js states for itself: the metal went
    /// transparent inside every tear (Shaders/PlaneHull.shader's clip(), fed by
    /// PlaneDamage.cs:289's _TearInv) with NO HOLE ART AROUND IT. This class is
    /// the missing half — the renderer, on exactly the model of
    /// Fx/ImpactDecalFx.cs (the same thing for impactfx.js's DecalLayer).
    ///
    /// It is the port of what src/planedamage.js's MarkLayer constructor does
    /// and PlaneMarkLayer deliberately does not (src/planedamage.js:417-456):
    /// one InstancedMesh of unit quads per layer, one material per layer with
    /// its own polygon offset and render order, the 2x2 atlas uv slide, and the
    /// per-instance colour. Built ONCE (EnsureBuilt, below) — never one Material
    /// per shot (Fx/InstancedSpriteBatch.cs's header for why that rule exists
    /// here).
    ///
    /// WHAT IT MUST NOT DO, and does not: re-derive any damage. Tear radius
    /// growth (area adds up, so radius goes as the root of the round count),
    /// the energy-scaled join distance that saws a wing off along a connected
    /// run (pistol 12 cm, belly gun 38 cm), tears opening at THREE CALIBRES
    /// because a 9 mm hole drawn at 9 mm is invisible in play, and
    /// DecalGeometry.SurfaceFit's four-corner probe that keeps a mark from
    /// hanging over a thin edge — all of that is already decided by
    /// PlaneDamage.Feed/TearRadius/BridgeOf/Joined/Stamp and is baked into the
    /// pose this reads. This class draws the record and nothing else.
    ///
    /// FRAME: PlaneMarkLayer.Pose holds a WORLD matrix, not a local one.
    /// PlaneDamage.Stamp stores `mark.Local = owner.worldToLocalMatrix * world`
    /// (the airframe's OWN frame — that is what makes a mark fly with the
    /// machine) and PlaneDamage.Update re-derives `world = w.localToWorldMatrix
    /// * mk.Local` into the layer whenever the wrapper's matrix changes, for
    /// severed pieces too (their PieceRider does the same off the piece). So
    /// there is nothing for this renderer to transform and no handedness
    /// boundary to cross here: it submits Pose verbatim. The one cross product
    /// in that chain (Stamp's tangent frame) is already operand-swapped for
    /// Unity in PlaneDamage.cs.
    ///
    /// FLEET-OFF HIDING is honoured by respecting `Live` and nothing else.
    /// Flight/PlaneHallVisibility.cs switches the whole parked fleet with one
    /// `Fleet.SetActive(near)` (PlaneHallVisibility.cs:127), PlaneDamage.Update
    /// reads `w.gameObject.activeInHierarchy` and calls `Layer.Hide(index)` on
    /// the flip, and Hide clears `Live` while keeping the slot's owner. A slot
    /// with Live == false is skipped here, so the marks vanish with their
    /// aeroplane and come back with it. (That is the "dots in air" bug: the JS
    /// has to park a zero-scale matrix in the instance buffer for the same
    /// reason — src/planedamage.js:500 hide() — because an InstancedMesh cannot
    /// skip an instance; immediate-mode instancing just omits it.)
    ///
    /// Marks do not fade — same one-shot RESUBMIT as Fx/ImpactDecalFx.cs.
    /// src/planedamage.js's MarkLayer has no life field at all: a mark stays
    /// until something else takes its ring slot. There is no per-frame
    /// simulation of its own to run, so Render() takes no dt.
    /// </summary>
    public sealed class PlaneMarkFx
    {
        // One material per layer, in src/planedamage.js:523-528's own order,
        // which is also the `order` that sets each one's polygon offset and
        // render queue.
        public Material HoleMat { get; private set; }   // :523 atlasTexture(drawSkinHole), order 0
        public Material TearMat { get; private set; }   // :525 atlasTexture(drawSkinTear), order 1
        public Material ExitMat { get; private set; }   // :526 atlasTexture(drawSkinExit), order 2
        public Material ScrapeMat { get; private set; } // :527 skinScrapeTexture(),        order 3, NOT an atlas
        public Material CrazeMat { get; private set; }  // :528 atlasTexture(drawCraze),    order 4

        /// <summary>Tiles per axis per layer, matching the `tiles` argument each
        /// `new MarkLayer(...)` passes — 2 for the four atlases, 1 for the
        /// scrape strip (src/planedamage.js:527 passes no tiles at all, so it
        /// defaults to 1 and gets no uvOffset attribute).</summary>
        public const int AtlasTiles = 2; // src/planedamage.js:1665 TILES
        public const int StripTiles = 1;

        /// <summary>True once all five materials and their art exist.</summary>
        public bool Baked { get; private set; }
        /// <summary>What the bake has cost in total, in ms — probe-visible.</summary>
        public long BakeMs { get; private set; }

        /// <summary>
        /// The atlases are painted PER PIXEL on the CPU (Combat/DamageDecals.cs
        /// has no canvas to rasterize with, which is what the JS gets for free
        /// from THREE.CanvasTexture), and the four 512x512 sheets plus the
        /// scrape strip measure 5.29 s on this machine. So the bake is
        /// deliberately NOT in the constructor and NOT all in one frame:
        ///   * it starts on the first Render(), i.e. the first ticked combat
        ///     frame, which keeps a bare `new PlaneDamage()` — five Editor
        ///     probes do exactly that — free, and
        ///   * it paints ONE layer per call, so the start of a session is five
        ///     ~1 s steps instead of one 5.3 s hang.
        /// Nothing is lost by the stagger: a layer has no marks to draw until
        /// something has been shot, which is many frames later. Pass all=true to
        /// force the whole bake in one go (what a probe wants).
        ///
        /// This is a lazy init with NO fallback path — the documented
        /// first-hit-only hazard in this project is an accessor that quietly
        /// returns a default when its EnsureInit has not run yet. Render() calls
        /// this first and unconditionally, and nothing else reads the materials.
        ///
        /// The 5.3 s itself is a real port cost and belongs to whoever owns
        /// Combat/DamageDecals.cs, not to this renderer: the fix is painting the
        /// Color[] on a worker thread (only the Texture2D upload needs the main
        /// thread) or baking the five sheets once into imported assets.
        /// </summary>
        public void EnsureBuilt(bool all = false)
        {
            if (Baked) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (_sh == null) _sh = Shader.Find("TbilisiFly/PlaneMark");
            if (_sh == null) { Baked = true; return; } // nothing to build onto
            while (true)
            {
                if (HoleMat == null) HoleMat = BuildMat(_sh, DamageDecals.BuildHoleAtlas(), AtlasTiles, 0);
                else if (TearMat == null) TearMat = BuildMat(_sh, DamageDecals.BuildTearAtlas(), AtlasTiles, 1);
                else if (ExitMat == null) ExitMat = BuildMat(_sh, DamageDecals.BuildExitAtlas(), AtlasTiles, 2);
                else if (ScrapeMat == null) ScrapeMat = BuildMat(_sh, DamageDecals.BuildScrapeTexture(), StripTiles, 3);
                else if (CrazeMat == null) CrazeMat = BuildMat(_sh, DamageDecals.BuildCrazeAtlas(), AtlasTiles, 4);
                else Baked = true;
                if (Baked || !all) break;
            }
            sw.Stop();
            BakeMs += sw.ElapsedMilliseconds;
        }
        Shader _sh;

        /// <summary>src/planedamage.js:424-456 — the MeshBasicMaterial and the one
        /// injected shader line, as material state.</summary>
        static Material BuildMat(Shader sh, Texture2D tex, int tiles, int order)
        {
            if (sh == null) return null;
            var m = new Material(sh) { enableInstancing = true };
            m.SetTexture("_MainTex", tex);
            m.SetFloat("_UvScale", 1f / tiles);
            // src/planedamage.js:429-430 polygonOffsetFactor/polygonOffsetUnits
            float po = -8f - order * 2f;
            m.SetFloat("_OffsetFactor", po);
            m.SetFloat("_OffsetUnits", po);
            // src/planedamage.js:453 mesh.renderOrder = 6 + order. Three.js
            // renderOrder sorts within the transparent pass; Unity's equivalent
            // knob is the material's render queue (3000 == Transparent).
            m.renderQueue = 3000 + 6 + order;
            return m;
        }

        // ------------------------------------------------------------------
        // Which of the four drawings this instance is.
        //
        // src/planedamage.js:468-478 picks it inside MarkLayer.alloc with
        // Math.random() and stores it in a per-instance `uvOffset` buffer
        // attribute. Combat/PlaneDamage.cs's PlaneMarkLayer carries no such
        // field (it is the rendering half, which that class left out), and
        // widening that ring buffer's data layout was out of scope for this
        // pass — so the variant is derived HERE, from the slot index, which
        // gives the same two properties the JS relies on:
        //   * it is pseudo-random across neighbouring marks, so a burst into
        //     one wing does not repeat a drawing (the whole reason the atlas
        //     exists, src/planedamage.js:1617-1623), and
        //   * it is STABLE for as long as the slot lives, which is what the JS
        //     gets by writing the attribute once at alloc.
        // The one behavioural difference: a ring slot reused after 3,200 marks
        // shows the same drawing it did the first time round, where the JS
        // re-rolls. Documented rather than hidden; at 3,200 slots it is not
        // observable.
        //
        // The hash is this project's own `fract(sin(n) * 43758.5453)`, computed in
        // DOUBLE including its argument, per this port's numerics rule (float32
        // cannot resolve the fractional part that IS the hash — see
        // City/BuildingDamage.Fr).
        // ------------------------------------------------------------------
        internal static Vector4 TileOffset(int slot, int tiles)
        {
            if (tiles <= 1) return Vector4.zero;
            double s = Math.Sin(slot * 12.9898 + 78.233) * 43758.5453;
            double h = s - Math.Floor(s);
            int n = tiles * tiles;
            int t = (int)(h * n);            // JS `| 0` on a non-negative value
            if (t >= n) t = n - 1;
            int tx = t % tiles;
            int ty = t / tiles;              // JS `(t / tiles) | 0`
            // src/planedamage.js:476 off.setXY(i, tx / tiles, 1 - (ty + 1) / tiles)
            return new Vector4(tx / (float)tiles, 1f - (ty + 1) / (float)tiles, 0f, 0f);
        }

        static readonly Vector4 WhiteTint = new Vector4(1f, 1f, 1f, 1f);
        Matrix4x4[] _mScratch = new Matrix4x4[InstancedSpriteBatch.MaxPerBatch];
        Vector4[] _tScratch = new Vector4[InstancedSpriteBatch.MaxPerBatch];
        Vector4[] _pScratch = new Vector4[InstancedSpriteBatch.MaxPerBatch];

        /// <summary>Live instances submitted on the last Render() call, per layer —
        /// the "a count you computed is not evidence anything was drawn" check,
        /// read by Editor/Probes/PlaneMarkProbe.cs.</summary>
        public int DrawnHoles, DrawnTears, DrawnExits, DrawnScrapes, DrawnCraze;
        public int DrawnTotal => DrawnHoles + DrawnTears + DrawnExits + DrawnScrapes + DrawnCraze;

        int DrawLayer(PlaneMarkLayer layer, Material mat, int tiles)
        {
            if (mat == null || layer == null) return 0;
            int count = layer.Count;          // high-water mark of the ring, not Max
            if (count <= 0) return 0;
            if (_mScratch.Length < count)
            {
                _mScratch = new Matrix4x4[count];
                _tScratch = new Vector4[count];
                _pScratch = new Vector4[count];
            }
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                // Live is false for a slot that was never filled, one whose
                // owning tear merged away (Clear), AND one whose aeroplane has
                // been switched off (Hide) — see this class's header.
                if (!layer.Live[i]) continue;
                _mScratch[n] = layer.Pose[i];
                var tint = layer.Tint[i];
                _tScratch[n] = tint.HasValue
                    ? new Vector4(tint.Value.r / 255f, tint.Value.g / 255f, tint.Value.b / 255f, 1f)
                    : WhiteTint;
                _pScratch[n] = TileOffset(i, tiles);
                n++;
            }
            InstancedSpriteBatch.Draw(InstancedSpriteBatch.UnitQuad, mat, _mScratch, _tScratch, _pScratch, n);
            return n;
        }

        /// <summary>Call once per frame. Owned and called by Combat/PlaneDamage.cs
        /// itself — the same self-contained-field pattern Combat/ImpactFx.cs uses
        /// for Fx/ImpactDecalFx.cs and Combat/Rockets.cs for Fx/RocketTrailFx.cs —
        /// because in the JS it is PlaneDamage's own constructor that builds the
        /// MarkLayers' meshes and materials (src/planedamage.js:523-528).</summary>
        public void Render(PlaneDamage pd)
        {
            if (pd == null) return;
            EnsureBuilt();
            DrawnHoles = DrawLayer(pd.Holes, HoleMat, AtlasTiles);
            DrawnTears = DrawLayer(pd.Tears, TearMat, AtlasTiles);
            DrawnExits = DrawLayer(pd.Exits, ExitMat, AtlasTiles);
            DrawnScrapes = DrawLayer(pd.Scrapes, ScrapeMat, StripTiles);
            DrawnCraze = DrawLayer(pd.Craze, CrazeMat, AtlasTiles);
        }

        /// <summary>src/planedamage.js:1595-1596 `for (const l of this.layers)
        /// l.dispose()` — the one thing in PlaneDamage's own dispose() that had
        /// nothing to release on the C# side, because the layers owned no mesh,
        /// material or texture. They do now, through this class.</summary>
        public void Dispose()
        {
            Baked = false;
            BakeMs = 0;
            Kill(HoleMat);
            Kill(TearMat);
            Kill(ExitMat);
            Kill(ScrapeMat);
            Kill(CrazeMat);
            HoleMat = TearMat = ExitMat = ScrapeMat = CrazeMat = null;
        }

        static void Kill(Material m)
        {
            if (m == null) return;
            if (m.mainTexture != null) UnityEngine.Object.DestroyImmediate(m.mainTexture);
            UnityEngine.Object.DestroyImmediate(m);
        }
    }
}

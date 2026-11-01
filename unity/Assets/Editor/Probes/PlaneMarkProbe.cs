using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using TbilisiFly.Combat;
using TbilisiFly.Fx;
using Debug = UnityEngine.Debug;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves Fx/PlaneMarkFx.cs actually DRAWS plane damage — the renderer the
    /// five PlaneMarkLayers (Holes/Tears/Exits/Scrapes/Craze) never had.
    ///
    /// WHAT THIS CANNOT DO: -nographics batch mode has no GPU, so it cannot
    /// reproduce tools/_probe_realholes.mjs, which proves a hole by reading
    /// RENDERED PIXELS before and after on all 35 planes. That remains the bar
    /// and still needs a GPU run. What this proves instead, end to end through
    /// the real PlaneDamage -> Refine -> Trace -> Stamp -> Update -> Render
    /// path on a real Collider:
    ///   SHADER   the shader compiles and reports zero messages
    ///   MAT      5 materials exist, each with its baked atlas bound, and the
    ///            per-layer uv scale / polygon offset / render queue the JS's
    ///            MarkLayer constructor sets
    ///   TILE     the 2x2 atlas: all four tiles are reachable and every uv
    ///            offset is on the tile grid
    ///   DRAWN    instances SUBMITTED per layer after a real round hits a real
    ///            airframe part — not a count this probe computed itself
    ///   FRAME    the quad rides the aeroplane: moving the wrapper moves the
    ///            submitted pose by exactly as much, and the mark's position in
    ///            the airframe's OWN frame does not change
    ///   FLEETOFF the marks vanish when the fleet GameObject is switched off
    ///            (Flight/PlaneHallVisibility.cs's one SetActive) and come back
    ///            with it — the "dots in air" bug
    ///   BAKE     what the four atlases + the strip cost to build, once
    /// </summary>
    public static class PlaneMarkProbe
    {
        public static void Run()
        {
            try
            {
                ShaderFacts();
                MaterialFacts();
                TileFacts();
                LiveDrawFacts();
            }
            catch (Exception e)
            {
                Debug.Log($"PLANEMARK id=exception msg={e.Message}");
                Debug.LogException(e);
            }
        }

        // ---- the shader half: it has to exist and it has to compile ----
        static void ShaderFacts()
        {
            var sh = Shader.Find("TbilisiFly/PlaneMark");
            int msgs = sh != null ? ShaderUtil.GetShaderMessageCount(sh) : -1;
            Debug.Log($"SHADER id=planemark found={(sh != null).ToString().ToLowerInvariant()} " +
                      $"supported={(sh != null && sh.isSupported).ToString().ToLowerInvariant()} " +
                      $"messages={msgs} " +
                      $"hasErrors={(sh != null && ShaderUtil.ShaderHasError(sh)).ToString().ToLowerInvariant()}");
            if (sh != null && msgs > 0)
                foreach (var m in ShaderUtil.GetShaderMessages(sh))
                    Debug.Log($"SHADERMSG id=planemark severity={m.severity} line={m.line} msg={m.message}");
        }

        // ---- src/planedamage.js:417-456 MarkLayer's material, per layer ----
        static void MaterialFacts()
        {
            var sw = Stopwatch.StartNew();
            var fx = new PlaneMarkFx();
            long ctorMs = sw.ElapsedMilliseconds;
            fx.EnsureBuilt(true);
            sw.Stop();
            // The art is painted per pixel on the CPU, so it is deliberately NOT
            // baked in the constructor — see PlaneMarkFx.EnsureBuilt.
            Debug.Log($"BAKE id=atlases ctorMs={ctorMs} bakeMs={fx.BakeMs} totalMs={sw.ElapsedMilliseconds}");

            var rows = new (string id, Material m, int tiles, int order)[]
            {
                ("hole", fx.HoleMat, PlaneMarkFx.AtlasTiles, 0),
                ("tear", fx.TearMat, PlaneMarkFx.AtlasTiles, 1),
                ("exit", fx.ExitMat, PlaneMarkFx.AtlasTiles, 2),
                ("scrape", fx.ScrapeMat, PlaneMarkFx.StripTiles, 3),
                ("craze", fx.CrazeMat, PlaneMarkFx.AtlasTiles, 4),
            };
            foreach (var r in rows)
            {
                var tex = r.m != null ? r.m.GetTexture("_MainTex") as Texture2D : null;
                float want = -8f - r.order * 2f; // :429-430
                Debug.Log($"MAT id={r.id} mat={(r.m != null).ToString().ToLowerInvariant()} " +
                          $"tex={(tex != null).ToString().ToLowerInvariant()} " +
                          $"texW={(tex != null ? tex.width : 0)} texH={(tex != null ? tex.height : 0)} " +
                          $"uvScale={(r.m != null ? r.m.GetFloat("_UvScale") : 0f):F3} " +
                          $"uvScaleWant={1f / r.tiles:F3} " +
                          $"offFactor={(r.m != null ? r.m.GetFloat("_OffsetFactor") : 0f):F1} " +
                          $"offWant={want:F1} " +
                          $"queue={(r.m != null ? r.m.renderQueue : 0)} queueWant={3000 + 6 + r.order} " +
                          $"instancing={(r.m != null && r.m.enableInstancing).ToString().ToLowerInvariant()}");
            }
            fx.Dispose();

            // ONE layer per call, so a session's start is five ~1 s steps and
            // not one 5.3 s hang (PlaneMarkFx.EnsureBuilt). Six calls: five
            // layers, then Baked.
            var st = new PlaneMarkFx();
            for (int i = 1; i <= 6; i++)
            {
                st.EnsureBuilt();
                int built = (st.HoleMat != null ? 1 : 0) + (st.TearMat != null ? 1 : 0) +
                            (st.ExitMat != null ? 1 : 0) + (st.ScrapeMat != null ? 1 : 0) +
                            (st.CrazeMat != null ? 1 : 0);
                Debug.Log($"BAKE id=stagger call={i} built={built} baked={st.Baked.ToString().ToLowerInvariant()} cumMs={st.BakeMs}");
            }
            st.Dispose();
        }

        // ---- FOUR OF EVERYTHING (src/planedamage.js:468-478 + :1617-1623) ----
        static void TileFacts()
        {
            const int tiles = PlaneMarkFx.AtlasTiles;
            var seen = new Dictionary<string, int>();
            bool onGrid = true;
            const int n = 3200; // the Holes layer's whole ring
            for (int i = 0; i < n; i++)
            {
                var o = PlaneMarkFx.TileOffset(i, tiles);
                // every offset must be k/tiles for an integer k, or the quad
                // would straddle two drawings
                float sx = o.x * tiles, sy = o.y * tiles;
                if (Mathf.Abs(sx - Mathf.Round(sx)) > 1e-5f || Mathf.Abs(sy - Mathf.Round(sy)) > 1e-5f) onGrid = false;
                string k = $"{o.x:F2},{o.y:F2}";
                seen.TryGetValue(k, out int c);
                seen[k] = c + 1;
            }
            int min = int.MaxValue, max = 0;
            foreach (var kv in seen) { if (kv.Value < min) min = kv.Value; if (kv.Value > max) max = kv.Value; }
            Debug.Log($"TILE id=atlas tiles={tiles} variants={seen.Count} variantsWant={tiles * tiles} " +
                      $"onGrid={onGrid.ToString().ToLowerInvariant()} slots={n} minShare={min} maxShare={max}");
            // tiles=1 (the scrape strip, src/planedamage.js:527) must take the
            // whole texture, offset (0,0)
            var s = PlaneMarkFx.TileOffset(7, PlaneMarkFx.StripTiles);
            Debug.Log($"TILE id=strip tiles={PlaneMarkFx.StripTiles} offX={s.x:F3} offY={s.y:F3}");
        }

        // =====================================================================
        // The live path: a real round into a real airframe part, then the real
        // per-frame Update() that submits the instances.
        // =====================================================================
        static void LiveDrawFacts()
        {
            // The fleet switch Flight/PlaneHallVisibility.cs toggles with one
            // GameObject.SetActive — the marks' visibility hangs off exactly this.
            var fleet = new GameObject("probe_fleet");
            var wrapper = new GameObject("probe_plane");
            wrapper.transform.SetParent(fleet.transform, false);
            wrapper.transform.position = new Vector3(0f, 20f, 0f);
            var rec = wrapper.AddComponent<PlaneDamageRecord>();
            rec.DisplayName = "probe";
            rec.MaxHullPoints = 1000f;
            rec.HullPoints = 1000f;

            // one skin panel (a 4 m slab of "wing") and one perspex canopy
            var wing = Panel("wingL", wrapper.transform, new Vector3(0f, 0f, 0f), false);
            // The lower skin, 0.6 m under the upper one. A through-shot's exit
            // needs a FRONT face to land on: Unity's mesh raycasts ignore
            // backfaces by default (Physics.queriesHitBackfaces), so the far
            // side of the SAME slab is invisible to Exit()'s ray — see this
            // probe's report. A real airframe is modelled as separate panels,
            // which is what this reproduces.
            Panel("wingL_lower", wrapper.transform, new Vector3(0f, -0.6f, 0f), false);
            var canopy = Panel("canopy_glass", wrapper.transform, new Vector3(6f, 0f, 0f), true);

            var pd = new PlaneDamage { CameraPosition = () => new Vector3(0f, 25f, -10f) };
            pd.Watch(rec);
            // Steady state: past a session's first five frames every layer has
            // its art, so force the whole bake before firing. Without this the
            // staggered build shifts each DRAWN line one layer behind.
            pd.MarkFx.EnsureBuilt(true);

            var cart = Cartridges.Para9x19;
            Vector3 dir = Vector3.down;

            // 1st round into the wing -> a single opening: the Holes layer
            Fire(pd, wing, new Vector3(0f, 0f, 0f), dir, cart);
            pd.Update(0.016f);
            Report("round1", pd);

            // 2nd round into the SAME SPOT -> the opening grows (n=2), so the
            // mark moves from Holes to Tears. "Damage is TEARS that grow when
            // you hit the same place, not one decal per round."
            Fire(pd, wing, new Vector3(0.002f, 0f, 0.002f), dir, cart);
            pd.Update(0.016f);
            Report("round2_samespot", pd);

            // a ricochet off the wing -> the Scrapes layer (the one non-atlas
            // layer, src/planedamage.js:527)
            FireRicochet(pd, wing, new Vector3(1.2f, 0f, 0.4f), dir, cart);
            pd.Update(0.016f);
            Report("ricochet", pd);

            // into the perspex -> the Craze layer ("crazes, then goes")
            Fire(pd, canopy, new Vector3(6f, 0f, 0f), dir, cart);
            pd.Update(0.016f);
            Report("canopy", pd);

            // ---- the airframe's OWN frame: it rides the aeroplane ----
            var layer = pd.Holes.Count > 0 && AnyLive(pd.Holes) ? pd.Holes : pd.Tears;
            int slot = FirstLive(layer);
            Vector3 before = layer.Pose[slot].GetColumn(3);
            Vector3 localBefore = wrapper.transform.InverseTransformPoint(before);
            wrapper.transform.position += new Vector3(137f, 11f, -42f);
            pd.Update(0.016f);
            Vector3 after = layer.Pose[slot].GetColumn(3);
            Vector3 localAfter = wrapper.transform.InverseTransformPoint(after);
            Debug.Log($"FRAME id=rides moved={(after - before - new Vector3(137f, 11f, -42f)).magnitude:F6} " +
                      $"localDrift={(localAfter - localBefore).magnitude:F6} " +
                      $"localX={localAfter.x:F4} localY={localAfter.y:F4} localZ={localAfter.z:F4}");

            // ---- fleet off: the "dots in air" bug ----
            // Render() runs at the TOP of Update(), so the frame that notices
            // the flip still submits the old state; the next one submits none.
            int liveBefore = pd.MarkFx.DrawnTotal;
            fleet.SetActive(false);
            pd.Update(0.016f);
            pd.Update(0.016f);
            int liveOff = pd.MarkFx.DrawnTotal;
            fleet.SetActive(true);
            pd.Update(0.016f);
            pd.Update(0.016f);
            int liveBack = pd.MarkFx.DrawnTotal;
            Debug.Log($"FLEETOFF id=switch on={liveBefore} off={liveOff} back={liveBack}");

            pd.Dispose();
            UnityEngine.Object.DestroyImmediate(fleet);
        }

        static void Report(string id, PlaneDamage pd)
        {
            var fx = pd.MarkFx;
            Debug.Log($"DRAWN id={id} holes={fx.DrawnHoles} tears={fx.DrawnTears} exits={fx.DrawnExits} " +
                      $"scrapes={fx.DrawnScrapes} craze={fx.DrawnCraze} total={fx.DrawnTotal}");
        }

        static bool AnyLive(PlaneMarkLayer l) => FirstLive(l) >= 0;

        static int FirstLive(PlaneMarkLayer l)
        {
            for (int i = 0; i < l.Count; i++) if (l.Live[i]) return i;
            return -1;
        }

        /// <summary>A 4x0.2x2 m panel with a real MeshCollider — Collider.Raycast
        /// works in edit mode, which is what lets this probe feed PlaneDamage a
        /// genuine RaycastHit (RaycastHit.collider has no setter, so a synthesised
        /// hit could never reach Mark()'s collider lookup).</summary>
        static MeshCollider Panel(string name, Transform parent, Vector3 localPos, bool clear)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(4f, 0.2f, 2f);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var mr = go.AddComponent<MeshRenderer>();
            var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(sh) { name = clear ? "probe_perspex" : "probe_skin" };
            // PlaneDamage classifies "perspex" by renderQueue >= Transparent AND
            // alpha < 0.95 (PartsOf/IsTransparent/Alpha).
            if (clear)
            {
                // PlaneDamage.IsTransparent asks _Surface FIRST on a URP Lit
                // material (0 opaque / 1 transparent) and only falls back to the
                // render queue, so setting the queue alone leaves the panel
                // classified as bare metal.
                if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(1f, 1f, 1f, 0.3f));
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", new Color(1f, 1f, 1f, 0.3f));
            }
            mr.sharedMaterial = mat;
            var col = go.AddComponent<MeshCollider>();
            col.sharedMesh = mf.sharedMesh;
            return col;
        }

        static bool Aim(MeshCollider col, Vector3 worldAt, Vector3 dir, out RaycastHit hit)
        {
            var ray = new Ray(worldAt - dir * 8f, dir);
            return col.Raycast(ray, out hit, 40f);
        }

        /// <summary>The real path a belly-gun round takes: Refine (off the crude
        /// shell onto the real skin) then Trace.</summary>
        static void Fire(PlaneDamage pd, MeshCollider col, Vector3 localAt, Vector3 dir, Cartridge cart)
        {
            Vector3 worldAt = col.transform.parent.TransformPoint(localAt);
            if (!Aim(col, worldAt, dir, out var first)) { Debug.Log("DRAWN id=aimfail"); return; }
            var refined = pd.Refine(first, dir) ?? first;
            string surf = pd.Trace(refined, refined.point, refined.normal, dir, cart);
            Debug.Log($"HIT id={col.name} surface={surf ?? "null"} part={refined.collider.name}");
        }

        /// <summary>A graze. Only the ImpactInfo path can set Ricochet, and it is
        /// the only thing that stamps the Scrapes layer (src/planedamage.js's
        /// _skin ricochet branch).</summary>
        static void FireRicochet(PlaneDamage pd, MeshCollider col, Vector3 localAt, Vector3 dir, Cartridge cart)
        {
            Vector3 worldAt = col.transform.parent.TransformPoint(localAt);
            if (!Aim(col, worldAt, dir, out var first)) { Debug.Log("DRAWN id=aimfail_ric"); return; }
            var refined = pd.Refine(first, dir) ?? first;
            float cal = cart.BulletMm / 1000f;
            pd.Mark(new ImpactInfo
            {
                Hit = refined, Point = refined.point, Normal = refined.normal, Dir = dir, Cart = cart,
                Energy = cart.Energy, Graze = 0.2f, Ricochet = true, Through = false,
                HoleW = cal * 1.18f, HoleL = cal * 1.18f,
            });
        }
    }
}

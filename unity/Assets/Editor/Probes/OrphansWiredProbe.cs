using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Combat;
using TbilisiFly.Core;
using TbilisiFly.Fx;
using TbilisiFly.UI.Settings;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// The "it is REACHED" half for the three units wired on 2026-10-31 out of
    /// docs/open_items.json's `orphans-4` and `quality-changed-dead`. Every one
    /// of them was already probe-verified as a CALCULATION; what nothing proved
    /// was that anything called it (.claude/skills/unity/SKILL.md: "a probe
    /// proves a CALCULATION, not that the calculation is REACHED").
    ///
    /// So this probe never calls the ported maths directly. It drives the
    /// REAL publisher in each case and reads the consumer's own counters back:
    ///
    ///   DEBRIS    — damage a synthetic CombatBuildingChunk to stage 6 and then
    ///               to death, with Core/CollapseDebris.cs behind the live
    ///               CollapseFxHooks, and read the pieces/scatters that came out
    ///               of City/DebrisField.cs + Fx/RubbleFx.cs.
    ///   GUNDECAL  — stamp through MachineGun's own impact path and tick
    ///               MachineGun.Update, then read Fx/GunDecalFx's submit count,
    ///               which is what proves Combat/GunDecalTextures.cs's painted
    ///               textures are on a material in a draw.
    ///   QUALITY   — call GameLoop.ApplyQuality (what SettingsView.QualityChanged
    ///               now reaches) for all four presets and read URP's own
    ///               renderScale plus GameLoop.Hq back.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.OrphansWiredProbe.Run
    /// </summary>
    public static class OrphansWiredProbe
    {
        static string L(bool b) => b.ToString().ToLowerInvariant(); // SKILL.md: JS prints lower-case

        public static void Run()
        {
            Debris();
            GunDecals();
            Quality();
            ProbeExit.Exit(0);
        }

        // =====================================================================
        // DEBRIS — citymap.js:1553/2405/2473/2505/2521
        // =====================================================================

        // One 24 x 16 m building, 30 m tall, GAME frame — the same synthetic
        // footprint CollapseStageProbe uses, so the two are directly comparable.
        static readonly float[] Fp = { 0f, 0f, 24f, 0f, 24f, 16f, 0f, 16f };
        const float BaseY = 0f, Top = 30f;

        static Mesh BuildMesh()
        {
            int vc = Fp.Length / 2;
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int v = 0; v < vc; v++)
            {
                int w = (v + 1) % vc;
                float ax = -Fp[v * 2], az = Fp[v * 2 + 1]; // mirror X into Unity space
                float bx = -Fp[w * 2], bz = Fp[w * 2 + 1];
                int b = verts.Count;
                verts.Add(new Vector3(ax, BaseY, az));
                verts.Add(new Vector3(bx, BaseY, bz));
                verts.Add(new Vector3(bx, Top, bz));
                verts.Add(new Vector3(ax, Top, az));
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
            }
            float ccx = 0f, ccz = 0f;
            for (int v = 0; v < vc; v++) { ccx += -Fp[v * 2]; ccz += Fp[v * 2 + 1]; }
            ccx /= vc; ccz /= vc;
            int centre = verts.Count;
            verts.Add(new Vector3(ccx, Top, ccz));
            int ring = verts.Count;
            for (int v = 0; v < vc; v++) verts.Add(new Vector3(-Fp[v * 2], Top, Fp[v * 2 + 1]));
            for (int v = 0; v < vc; v++) { tris.Add(centre); tris.Add(ring + v); tris.Add(ring + (v + 1) % vc); }
            var mesh = new Mesh { name = "probe_debris_building" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.triangles = tris.ToArray();
            var cols = new Color32[verts.Count];
            for (int i = 0; i < cols.Length; i++) cols[i] = new Color32(200, 200, 200, 255);
            mesh.SetColors(cols);
            mesh.RecalculateBounds();
            return mesh;
        }

        static CombatBuildingChunk MakeChunk(string name)
        {
            int vc = Fp.Length / 2;
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = BuildMesh();
            var chunk = go.AddComponent<CombatBuildingChunk>();
            chunk.RangeStart = new[] { 0 };
            chunk.RangeCount = new[] { vc * 4 + vc + 1 };
            chunk.Heights = new[] { Top - BaseY };
            chunk.BuildingIndex = new[] { 0 };
            chunk.WallBase = new[] { BaseY };
            chunk.WallTop = new[] { Top };
            chunk.ShellTint = new[] { new Color32(190, 185, 178, 255) };
            chunk.ShellWallBucket = new[] { -1 };
            chunk.FootprintFlat = (float[])Fp.Clone();
            chunk.FootprintOffset = new[] { 0 };
            chunk.FootprintCount = new[] { vc };
            return chunk;
        }

        static void Debris()
        {
            var debris = new CollapseDebris { GroundAt = (x, z) => 0f };
            bool loaded = debris.EnsureLoaded();
            Debug.Log($"DEBRISWIRE id=kit loaded={L(loaded)} layers={debris.Rubble.LayerCount} " +
                      $"vertexColoured={L(debris.Rubble.VertexColoured)} path={RubbleFx.KitPath}");

            // The footprint bbox the publishers hand over, from the chunk's own
            // baked ring (not from SpanOf, which is max(w,d) and cannot be unpacked).
            var chunk = MakeChunk("probe_debris");
            bool box = chunk.FootprintBox(0, out float x0, out float x1, out float z0, out float z1);
            Debug.Log($"DEBRISWIRE id=bbox ok={L(box)} w={(x1 - x0):F4} d={(z1 - z0):F4}");

            int debrisCalls = 0, rubbleCalls = 0, shedCalls = 0, launched = 0, scattered = 0;
            CombatBuildingChunk.Fx = new CollapseFxHooks
            {
                GroundAt = (x, z) => (float?)0f,
                OnDebris = (cx, cz, span, fromY, n) => { debrisCalls++; launched += debris.Launch(cx, cz, span, fromY, n); },
                OnRubble = (a, b, c, d, e, f) => { rubbleCalls++; scattered += debris.SpawnRubble(a, b, c, d, e, f); },
                OnShed = (a, b, c, d) => { shedCalls++; scattered += debris.ShedDebris(a, b, c, d); },
            };

            float maxHp = chunk.MaxHpOf(0);
            // citymap.js:1553 — stage >= 6 sheds 4 chunks off a wall, ONCE per stage
            // (rec.shedAt). Walk 6, 7, 8 and the count must be 3 calls, not 3 x N.
            for (int s = 6; s <= 8; s++)
            {
                float wantHp = maxHp * (1f - s / 9f) - 0.5f;
                float cur = maxHp * chunk.HpFrac(0);
                chunk.ApplyDamage(0, Mathf.Max(0f, cur - wantHp));
                Debug.Log($"DEBRISWIRE id=shed_s{s} stage={chunk.StageOf(0)} shedCalls={shedCalls} scattered={scattered}");
            }

            int beforeKill = launched;
            chunk.ApplyDamage(0, maxHp * 2f); // the killing shot -> _startCollapse
            Debug.Log($"DEBRISWIRE id=ignite debrisCalls={debrisCalls} launchedNew={launched - beforeKill} " +
                      $"live={debris.LiveCount}");

            int atIgnition = launched;
            bool threw = false;
            try { for (int i = 0; i < 400; i++) { chunk.TickCollapses(0.02f); debris.Tick(0.02f); } }
            catch (System.Exception e) { threw = true; Debug.Log($"DEBRISWIRE id=tick threw={e.GetType().Name}: {e.Message}"); }
            Debug.Log($"DEBRISWIRE id=fall threw={L(threw)} debrisCalls={debrisCalls} launchedTotal={launched} " +
                      $"secondariesAndSlam={launched - atIgnition} rubbleCalls={rubbleCalls} " +
                      $"scattered={scattered} settled={debris.SettledCount} live={debris.LiveCount} " +
                      $"refused={debris.RefusedCount}");
            Debug.Log($"DEBRISWIRE id=draw reserved={debris.Rubble.Reserved} " +
                      $"drawnInstances={debris.Rubble.DrawnInstances} drawCalls={debris.Rubble.DrawCalls}");

            // src/rockets.js:309 g.world.blastDebris — the IDamageWorld entry that
            // used to be an empty method body.
            var adapter = new CombatWorldAdapter(null, null) { Debris = debris };
            int beforeBlast = debris.Rubble.Reserved;
            adapter.BlastDebris(0f, 12f, 0f, new Vector3(1f, 0.2f, 0f), 9);
            Debug.Log($"DEBRISWIRE id=blastdebris newSlots={debris.Rubble.Reserved - beforeBlast} live={debris.LiveCount}");

            CombatBuildingChunk.Fx = null;
            debris.Dispose();
        }

        // =====================================================================
        // GUNDECAL — src/guns.js:388-389 + :90-171
        // =====================================================================
        static void GunDecals()
        {
            var gun = new MachineGun { Rng = () => 0.5f };
            var fx = gun.DecalFx;
            Debug.Log($"GUNDECALWIRE id=mats holeMat={L(fx.HoleMat != null)} crackMat={L(fx.CrackMat != null)} " +
                      $"holeTex={L(fx.HoleMat != null && fx.HoleMat.mainTexture != null)} " +
                      $"crackTex={L(fx.CrackMat != null && fx.CrackMat.mainTexture != null)} " +
                      $"holeTexSize={(fx.HoleMat != null && fx.HoleMat.mainTexture != null ? fx.HoleMat.mainTexture.width : 0)} " +
                      $"crackTexSize={(fx.CrackMat != null && fx.CrackMat.mainTexture != null ? fx.CrackMat.mainTexture.width : 0)}");

            // src/guns.js:603 this.cracks.stamp(p, n) — the painted-on-window branch.
            gun.Cracks.Stamp(new Vector3(3f, 4f, 5f), Vector3.back,
                MachineGun.CracksBaseSize, MachineGun.CracksBaseSize, rng: gun.Rng);
            Debug.Log($"GUNDECALWIRE id=stamp cracksCount={gun.Cracks.Count} holesCount={gun.Holes.Count}");

            bool threw = false;
            try { gun.Update(0.02f, firing: false, muzzleWorld: Vector3.zero, forward: Vector3.forward, rng: gun.Rng); }
            catch (System.Exception e) { threw = true; Debug.Log($"GUNDECALWIRE id=update threw={e.GetType().Name}: {e.Message}"); }
            Debug.Log($"GUNDECALWIRE id=render threw={L(threw)} drawnCracks={fx.DrawnCracks} drawnHoles={fx.DrawnHoles}");
            gun.Dispose();
        }

        // =====================================================================
        // QUALITY — src/settings.js:6-10 QUALITY + :163-173 _applyQuality
        // =====================================================================
        static void Quality()
        {
            var go = new GameObject("probe_quality");
            var game = go.AddComponent<GameLoop>();
            var urp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
            float before = urp != null ? urp.renderScale : -1f;
            foreach (var key in new[] { "low", "med", "high", "ultra" })
            {
                var q = QualityPreset.For(key);
                game.ApplyQuality(q);
                float scale = urp != null ? urp.renderScale : -1f;
                Debug.Log($"QUALITYWIRE id={key} presetPixelRatio={q.PixelRatio:F2} presetHq={L(q.Hq)} " +
                          $"renderScale={scale:F2} hq={L(game.Hq)} applied={L(game.QualityApplied.HasValue)}");
            }
            // settings.js:167 — a HAND-PICKED preset pins the scale against the
            // auto-drop valve (GameLoop.AdaptResolution / `_resDropped`).
            var g2 = new GameObject("probe_quality_manual").AddComponent<GameLoop>();
            Debug.Log($"QUALITYWIRE id=pin beforeAuto={L(g2.RenderScalePinned)}");
            g2.ApplyQuality(QualityPreset.For("high"));
            Debug.Log($"QUALITYWIRE id=pin_auto pinned={L(g2.RenderScalePinned)}");
            g2.ApplyQuality(QualityPreset.For("high"), manual: true);
            Debug.Log($"QUALITYWIRE id=pin_manual pinned={L(g2.RenderScalePinned)}");
            Object.DestroyImmediate(g2.gameObject);

            if (urp != null) urp.renderScale = before; // leave the asset as we found it
            Object.DestroyImmediate(go);
        }
    }
}

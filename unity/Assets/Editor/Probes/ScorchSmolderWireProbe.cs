using System;
using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Combat;
using TbilisiFly.Core;
using TbilisiFly.Fx;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// docs/open_items.json `smolder-scorch-unported`: citymap.js:2514
    /// `fx.stampScorch(...)` and :2517-2519 `fx.setSmolder/setBurning(...)` had
    /// no counterpart, City/DebrisField.MoundAnchors was computed and
    /// unconsumed, and — found while porting them — Fx/FireSystem.Update was
    /// never called by anything, so the whole fire/smoke layer was constructed,
    /// handed to five consumers and never stepped.
    ///
    /// Two halves, in the order the skill asks for:
    ///
    ///   PUBLISHED — drive a real CombatBuildingChunk to death through
    ///               ApplyDamage/TickCollapses with a REAL FireSystem behind the
    ///               live CollapseFxHooks (via the real Core/CombatWorldAdapter.
    ///               FxSink, not a probe stub), then read FireSystem's own
    ///               registries back: the scorch decal, its size against
    ///               citymap.js:2514's `span * 2.1`, the smolder anchors against
    ///               DebrisField.MoundAnchors, the mound fire's 2.4x flame
    ///               quads, the smoke columns those emitters actually spawn and
    ///               the fact they FADE out again.
    ///   REACHED   — load Assets/Scenes/Game.unity, run the real boot, tick the
    ///               real GameLoop, and prove Install()'s own FireSystem is
    ///               STEPPED by game.WorldTick (the gate that was shut) and that
    ///               the live CombatBuildingChunk.Fx.Fire carries the four new
    ///               IFireFx methods.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.ScorchSmolderWireProbe.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class ScorchSmolderWireProbe
    {
        static string L(bool b) => b.ToString().ToLowerInvariant(); // SKILL.md: JS prints lower-case
        static bool _allPass = true;

        static void Check(string key, bool cond, string note = null)
        {
            string line = $"{(cond ? "PASS" : "FAIL")} {key}";
            if (!cond && note != null) line += $" ({note})";
            Debug.Log(line);
            if (!cond) _allPass = false;
        }

        public static void Run()
        {
            Published();
            Reached();
            Debug.Log(_allPass ? "SCORCHSMOLDER RESULT=PASS" : "SCORCHSMOLDER RESULT=FAIL");
            ProbeExit.Exit(_allPass ? 0 : 1);
        }

        // =====================================================================
        // The same synthetic 24 x 16 m, 30 m building OrphansWiredProbe and
        // CollapseStageProbe use, in the GAME frame, so all three are directly
        // comparable. span = max(24, 16) = 24 m.
        // =====================================================================
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
            var mesh = new Mesh { name = "probe_scorch_building" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.triangles = tris.ToArray();
            var cols = new Color32[verts.Count];
            for (int i = 0; i < cols.Length; i++) cols[i] = new Color32(200, 200, 200, 255);
            mesh.SetColors(cols);
            mesh.RecalculateBounds();
            return mesh;
        }

        static CombatBuildingChunk MakeChunk(string name, int origBi)
        {
            int vc = Fp.Length / 2;
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = BuildMesh();
            var chunk = go.AddComponent<CombatBuildingChunk>();
            chunk.RangeStart = new[] { 0 };
            chunk.RangeCount = new[] { vc * 4 + vc + 1 };
            chunk.Heights = new[] { Top - BaseY };
            chunk.BuildingIndex = new[] { origBi };   // the JS's rec.bi
            chunk.WallBase = new[] { BaseY };
            chunk.WallTop = new[] { Top };
            chunk.ShellTint = new[] { new Color32(190, 185, 178, 255) };
            chunk.ShellWallBucket = new[] { -1 };
            chunk.FootprintFlat = (float[])Fp.Clone();
            chunk.FootprintOffset = new[] { 0 };
            chunk.FootprintCount = new[] { vc };
            return chunk;
        }

        // =====================================================================
        // PUBLISHED
        // =====================================================================
        static void Published()
        {
            const int OrigBi = 4242;   // the JS's rec.bi — also the anchor-ring seed
            var fire = new FireSystem();
            var gameGo = new GameObject("probe_scorch_game");
            var game = gameGo.AddComponent<GameLoop>();
            // The REAL IFireFx implementation the game wires (CombatWorldAdapter.
            // Install's `new FxSink(fireSystem, impactFx, game)`), so this proves
            // the delegation chain and not a probe stub.
            var sink = new FxSink(fire, new ImpactFx(), game);

            var chunk = MakeChunk("probe_scorch", OrigBi);
            Check("burnKeyIsOrigBi", chunk.BurnKeyOf(0) == OrigBi,
                $"BurnKeyOf={chunk.BurnKeyOf(0)} — the registry key must be the city-wide rec.bi");
            Check("fireKeyDisjoint", chunk.FireKeyOf(0) != chunk.BurnKeyOf(0) && chunk.FireKeyOf(0) > 0,
                "citymap.js:2519's `${bi}:fire` must not share the smolder key");

            int scorchCalls = 0, smolderCalls = 0, burningCalls = 0, clearCalls = 0;
            var recorder = new CountingFire(sink,
                () => scorchCalls++, () => smolderCalls++, () => burningCalls++, () => clearCalls++);

            CombatBuildingChunk.Fx = new CollapseFxHooks
            {
                Fire = recorder,
                GroundAt = (x, z) => (float?)0f,     // citymap.js:2509's heightAt
            };

            float maxHp = chunk.MaxHpOf(0);
            chunk.ApplyDamage(0, maxHp * 2f);        // the killing shot -> _startCollapse
            bool threw = false;
            try { for (int i = 0; i < 400; i++) chunk.TickCollapses(0.02f); }
            catch (Exception e) { threw = true; Debug.Log($"SCORCHWIRE id=tick threw={e.GetType().Name}: {e.Message}"); }
            Check("collapseCompletes", !threw && chunk.StageOf(0) == 9);

            // ---- the scorch -------------------------------------------------
            float span = chunk.SpanOf(0);
            float wantSize = span * 2.1f;            // citymap.js:2514
            bool gotScorch = fire.TryGetScorch(0, out Vector3 sPos, out float sSize);
            Debug.Log($"SCORCHWIRE stamped={fire.ScorchCount} size={sSize:F4} wantSize={wantSize:F4} " +
                      $"span={span:F3} pos=({sPos.x:F2},{sPos.y:F2},{sPos.z:F2}) groundY=0.00 " +
                      $"jsYOffset=0.22 calls={scorchCalls}");
            Check("scorchStamped", fire.ScorchCount == 1 && scorchCalls == 1 && gotScorch,
                "citymap.js:2514 fx.stampScorch never reached FireSystem");
            Check("scorchSizeIsSpanTimes2p1", gotScorch && Mathf.Abs(sSize - wantSize) < 0.001f,
                $"want {wantSize:F3} got {sSize:F3}");
            Check("scorchOnTheGround", gotScorch && Mathf.Abs(sPos.y - 0.22f) < 0.001f,
                "src/fire.js:523 puts the decal at groundY + 0.22");

            // ---- the smoulder + the mound fire -------------------------------
            var want = DebrisField.MoundAnchors(-24f, 0f, 0f, 16f, BaseY, Top, OrigBi);
            bool gotSm = fire.TryGetSmolder(chunk.BurnKeyOf(0), out Vector3[] anchors);
            bool gotBurn = fire.TryGetBurning(chunk.FireKeyOf(0), out Vector3[] fAnchors, out float inten, out float scale);
            float anchorErr = 0f;
            if (gotSm && anchors != null && anchors.Length == want.Length)
                for (int i = 0; i < want.Length; i++) anchorErr = Mathf.Max(anchorErr, Vector3.Distance(anchors[i], want[i]));
            Debug.Log($"SMOLDERWIRE anchors={(anchors != null ? anchors.Length : 0)} wantAnchors={want.Length} " +
                      $"maxAnchorErr={anchorErr:F6} smolderEmitters={fire.SmolderCount} " +
                      $"burningEmitters={fire.BurningCount} intensity={inten:F2} scale={scale:F2} " +
                      $"flameQuads={fire.FlameQuadCount} setSmolderCalls={smolderCalls} " +
                      $"setBurningCalls={burningCalls} clearBurningCalls={clearCalls}");
            Check("smolderRegistered", gotSm && anchors != null && anchors.Length == want.Length && smolderCalls == 1,
                "citymap.js:2517 fx.setSmolder never reached FireSystem");
            Check("smolderAnchorsAreMoundAnchors", anchorErr < 1e-4f,
                "the anchors are not City/DebrisField.MoundAnchors' ring for this rec.bi");
            Check("moundFireRegistered", gotBurn && fAnchors != null && fAnchors.Length == want.Length
                  && Mathf.Abs(inten - 1f) < 1e-4f && Mathf.Abs(scale - 2.4f) < 1e-4f,
                "citymap.js:2519 fx.setBurning(`${bi}:fire`, mound, 1, 2.4)");
            Check("facadeFireCleared", clearCalls == 1, "citymap.js:2508 fx.clearBurning(rec.bi)");
            Check("flameQuadsBuilt", fire.FlameQuadCount == want.Length,
                "src/fire.js:379-409 — one persistent flame quad per anchor");

            // ---- THE COLUMNS: step the real emitters and watch them smoke ----
            // The collapse has just thrown ~1,350 dust puffs (fx.plume + the
            // per-floor collar) and they sit all over the same mound, so counting
            // live smoke right now would prove nothing about the SMOULDER. So:
            // drain first with the camera outside fire.js:588's 1400 m near field
            // (no emitter runs, everything ages out), and take the mound fire off
            // for the window so every puff counted comes from setSmolder alone.
            var farPos = new Vector3(-12f, 2f, 9000f);
            var camPos = new Vector3(-12f, 2f, 8f);  // at the mound
            int drained = -1, smokeAfter = 0, smokeFaded = -1;
            float seconds = 0f;
            bool stepThrew = false;
            var covered = new HashSet<int>();
            float worstOffset = 0f;
            int spawned = 0;
            try
            {
                fire.ClearBurning(chunk.FireKeyOf(0));
                for (int i = 0; i < 1200; i++) fire.Update(1f / 60f, farPos);  // 20 s, nothing emitting
                drained = fire.LiveSmokeCount;

                // 10 s at 6 anchors x 1.1 puffs/s/anchor (fire.js:618) is ~66
                // draws over 6 anchors, so a missed column is a real fault and
                // not the sampling (P(miss) ~ 6e-6), where 3 s left a 3% hole.
                //
                // Coverage is accumulated PER FRAME off the smoke ring cursor:
                // the slots between last frame's cursor and this frame's are
                // exactly the puffs just spawned, and the smolder is the only
                // emitter running, so each one is a column's own puff read at
                // its spawn point. (A snapshot of live puffs cannot do this: a
                // smolder puff rises at vy 1.6 for up to 4.5 s — fire.js:620-624
                // — so by the end of its life it is metres above its anchor, and
                // only ~9 are young at any instant.)
                int prevC = fire.SCursor;
                for (int f = 0; f < 600; f++)
                {
                    fire.Update(1f / 60f, camPos);
                    seconds += 1f / 60f;
                    int c = fire.SCursor;
                    for (int i = prevC; i != c; i = (i + 1) % FireSystem.SMax)
                    {
                        if (!fire.TryGetSmoke(i, out Vector3 p, out float _)) continue;
                        spawned++;
                        int best = -1; float bd = float.MaxValue;
                        for (int k = 0; k < want.Length; k++)
                        {
                            float d = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(want[k].x, want[k].z));
                            if (d < bd) { bd = d; best = k; }
                        }
                        if (best < 0) continue;
                        covered.Add(best);
                        if (bd > worstOffset) worstOffset = bd;
                    }
                    prevC = c;
                }
                smokeAfter = fire.LiveSmokeCount;
            }
            catch (Exception e) { stepThrew = true; Debug.Log($"SMOLDERWIRE id=step threw={e.GetType().Name}: {e.Message}"); }
            Check("fireStepRuns", !stepThrew, "FireSystem.Update threw — see the SMOLDERWIRE id=step line");
            Check("collapseDustDrains", drained == 0,
                $"{drained} puffs still alive after 20 s with nothing emitting — the column count below would be collapse dust");

            // The mound fire goes back on (it is live in the game alongside the
            // smoulder) and must rebuild its flame quads.
            fire.SetBurning(chunk.FireKeyOf(0), anchors, 1f, 2.4f);
            Check("moundFireRelights", fire.BurningCount == 1 && fire.FlameQuadCount == want.Length);

            // ...and the columns FADE: clear both emitters, step past the longest
            // dur (src/fire.js:623 `dur: 4.5`, x the 0.8-1.2 jitter) with the
            // camera back at the mound, and the live count must return to zero.
            fire.SetSmolder(chunk.BurnKeyOf(0), null);
            fire.ClearBurning(chunk.FireKeyOf(0));
            try
            {
                for (int i = 0; i < 600; i++) fire.Update(1f / 60f, camPos);
                smokeFaded = fire.LiveSmokeCount;
            }
            catch (Exception e) { Debug.Log($"SMOLDERWIRE id=fade threw={e.GetType().Name}: {e.Message}"); }

            Debug.Log($"SMOLDERWIRE anchors={want.Length} columns={covered.Count} seconds={seconds:F2} " +
                      $"puffsAfterDrain={drained} puffsSpawned={spawned} wantPuffs={(int)(want.Length * 1.1f * seconds)} " +
                      $"puffsLive={smokeAfter} maxOffsetXzM={worstOffset:F2} " +
                      $"afterFadeS=10.00 puffsAfterFade={smokeFaded} " +
                      $"fades={L(smokeAfter > 0 && smokeFaded == 0)}");
            Check("smolderEmitsColumns", smokeAfter > 0 && covered.Count == want.Length,
                $"{covered.Count}/{want.Length} anchors smoked — the emitter is registered but not emitting");
            // fire.js:624's `spread: 2.5` jitters X and Z INDEPENDENTLY by
            // (rand-0.5)*2.5, so the furthest a puff can spawn from its own
            // anchor in plan is the diagonal, sqrt(2) * 1.25 = 1.7678 m. A
            // bigger offset would mean the puffs are not on the anchors at all.
            Check("columnsStandOnTheAnchors", worstOffset <= 1.7678f,
                $"a puff spawned {worstOffset:F2} m (XZ) from the nearest mound anchor; " +
                "fire.js:624's spread allows 1.7678 m at the diagonal and no more");
            Check("smolderColumnsFade", smokeAfter > 0 && smokeFaded == 0,
                "the puffs never aged out, so the mound would smoke in frozen geometry");

            CombatBuildingChunk.Fx = null;
            fire.Dispose();
            UnityEngine.Object.DestroyImmediate(chunk.gameObject);
            UnityEngine.Object.DestroyImmediate(gameGo);
        }

        /// <summary>Pass-through IFireFx that counts the four calls this item is
        /// about and forwards every one of them to the REAL FxSink, so the
        /// counters and the FireSystem state below them come from the same
        /// delegation chain the game uses.</summary>
        sealed class CountingFire : IFireFx
        {
            readonly IFireFx _inner;
            readonly Action _scorch, _smolder, _burning, _clear;
            public CountingFire(IFireFx inner, Action scorch, Action smolder, Action burning, Action clear)
            { _inner = inner; _scorch = scorch; _smolder = smolder; _burning = burning; _clear = clear; }
            public void Wave(float x, float y, float z, float r) => _inner.Wave(x, y, z, r);
            public void SpawnEmber(float x, float y, float z) => _inner.SpawnEmber(x, y, z);
            public void SpawnSmoke(float x, float y, float z, float size, float dark, float dur,
                float vx, float vy, float vz, float spread)
                => _inner.SpawnSmoke(x, y, z, size, dark, dur, vx, vy, vz, spread);
            public void Plume(float x, float y, float z, float r, float s) => _inner.Plume(x, y, z, r, s);
            public void StampScorch(float x, float gy, float z, float size)
            { _scorch(); _inner.StampScorch(x, gy, z, size); }
            public void SetSmolder(int key, Vector3[] a) { _smolder(); _inner.SetSmolder(key, a); }
            public void SetBurning(int key, Vector3[] a, float i, float s) { _burning(); _inner.SetBurning(key, a, i, s); }
            public void ClearBurning(int key) { _clear(); _inner.ClearBurning(key); }
        }

        // =====================================================================
        // REACHED — the gate, on the real shipped scene
        // =====================================================================
        const string ScenePath = "Assets/Scenes/Game.unity";

        static void Reached()
        {
            UnityEngine.SceneManagement.Scene scene = default;
            bool openOk = true;
            try { scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single); }
            catch (Exception e) { openOk = false; Debug.LogException(e); }
            Check("sceneLoads", openOk && scene.IsValid());
            if (!openOk || !scene.IsValid()) return;

            var game = UnityEngine.Object.FindFirstObjectByType<GameLoop>();
            var boot = UnityEngine.Object.FindFirstObjectByType<PlayableSceneBoot>();
            Check("gameLoopExists", game != null);
            if (game == null) return;
            try { boot?.Run(); } catch (Exception e) { Debug.LogException(e); }
            for (int i = 0; i < 3; i++) game.Tick(1f / 60f);

            // game.BreakupWorld IS the adapter Install built (CombatWorldAdapter.
            // cs: "the adapter IS the IBreakupWorld"), and it carries FireRef.
            var adapter = game.BreakupWorld as CombatWorldAdapter;
            var fire = adapter != null ? adapter.FireRef : null;
            var hooks = CombatBuildingChunk.Fx;
            Debug.Log($"SMOLDERWIRE id=wiring adapter={L(adapter != null)} fireSystem={L(fire != null)} " +
                      $"hooks={L(hooks != null)} hooksFire={L(hooks != null && hooks.Fire != null)} " +
                      $"fireIsFxSink={L(hooks != null && hooks.Fire is FxSink)} started={L(game.Started)} " +
                      $"worldTick={L(game.WorldTick != null)}");
            Check("liveHooksCarryFire", hooks != null && hooks.Fire is FxSink,
                "CombatBuildingChunk.Fx.Fire is not the real FxSink, so no collapse can publish fire FX");
            Check("fireSystemInstalled", fire != null, "Install() built no FireSystem");
            if (fire == null || hooks == null || hooks.Fire == null) return;

            // THE GATE: publish through the LIVE hooks, then tick the REAL
            // GameLoop. If FireSystem.Update is not reached from game.WorldTick,
            // nothing ages and no puff is ever spawned — which is exactly the
            // state this file found it in.
            hooks.Fire.StampScorch(100f, 5f, 200f, 42f);
            var mound = DebrisField.MoundAnchors(96f, 112f, 196f, 212f, 5f, 25f, 7);
            hooks.Fire.SetSmolder(7, mound);
            int scorchNow = fire.ScorchCount;
            int smolderNow = fire.SmolderCount;
            // Stand the camera at the mound so fire.js:588's near-field test passes.
            if (game.MainCamera != null) game.MainCamera.transform.position = new Vector3(104f, 8f, 204f);
            int puffsBefore = fire.LiveSmokeCount;
            for (int i = 0; i < 120; i++) game.Tick(1f / 60f);
            int puffsAfter = fire.LiveSmokeCount;
            Debug.Log($"SMOLDERWIRE id=ticked scorch={scorchNow} smolderEmitters={smolderNow} " +
                      $"puffsBefore={puffsBefore} puffsAfter={puffsAfter} " +
                      $"stepped={L(puffsAfter > 0)}");
            Check("fireSystemIsStepped", puffsAfter > 0,
                "game.WorldTick does not step FireSystem — every ember, puff, flame and scorch is frozen/undrawn");
            Check("scorchReachedLiveSystem", scorchNow > 0 && smolderNow > 0,
                "the live hooks' StampScorch/SetSmolder did not land in Install()'s FireSystem");
            fire.SetSmolder(7, null);
        }
    }
}

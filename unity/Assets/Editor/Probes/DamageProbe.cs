using System.Collections.Generic;
using TbilisiFly.Combat;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the destruction/damage port (Combat/PlaneDamage.cs, AirframeBreakup.cs,
    /// MetalDents.cs, BreakWall.cs, BreakingGlass.cs) reproduces src/planedamage.js,
    /// src/breakup.js, src/dents.js, src/breakwall.js and src/glass.js. Prints one
    /// KEY=value line per fact; tools/_probe_damage.mjs prints the same facts from the
    /// real JS modules and the two are diffed with tools/unity_port_check.py.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.DamageProbe.Run
    /// </summary>
    public static class DamageProbe
    {
        sealed class NullBreakupWorld : IBreakupWorld
        {
            public float? GroundAt(float x, float z) => 0f;
            public void SpawnEmber(Vector3 at) { }
            public void SpawnSmoke(Vector3 at, float size, float dark, float dur, float vy, float spread) { }
            public void SetBurning(string key, Vector3[] points, float intensity) { }
            public void ClearBurning(string key) { }
            public void Plume(Vector3 at, float radius, float strength) { }
            public Vector3 HeroVelocity() => Vector3.zero;
            public bool HeroAirborne() => false;
        }

        sealed class NullDentsWorld : MetalDents.IWorld
        {
            public bool CinemaActive() => false;
        }

        public static void Run()
        {
            Print();
            ProbeExit.Exit(0);
        }

        // Identical generator to tools/_probe_ballistics.mjs's mulberry32(seed) and
        // BallisticsProbe.cs's own copy (duplicated here, not shared, per this
        // project's established "transcribe the tiny PRNG per probe" convention).
        // GLASS below uses it to seed BreakingGlass.Rng so the shard sim's
        // "fires, falls, rests" step count becomes a deterministic, comparable
        // fact instead of two unsynchronised Math.random()/UnityEngine.Random
        // streams that can never agree regardless of correctness.
        static System.Func<float> Mulberry32(uint seed)
        {
            uint a = seed;
            return () =>
            {
                unchecked
                {
                    a = a + 0x6D2B79F5u;
                    uint t = a;
                    t = (t ^ (t >> 15)) * (1u | t);
                    t = (t + (t ^ (t >> 7)) * (61u | t)) ^ t;
                    return (float)(((t ^ (t >> 14))) / 4294967296.0);
                }
            };
        }

        public static void Print()
        {
            TearRadiusFacts();
            BridgeAndJoinFacts();
            FeedGrowthFacts();
            HoleSetFacts();
            SurfaceFitFacts();
            DentDepthFacts();
            BreakWallFacts();
            BreakingGlassFacts();
            AirframeBreakupFacts();
        }

        // ---- tear radius for a given calibre (src/planedamage.js:1648 tearRadius) ----
        static void TearRadiusFacts()
        {
            float[] cals = { 0.009f, 0.00762f, 0.0127f }; // 9x19, 7.62x39, 12.7x108 bullet diameters
            string[] names = { "9x19", "762x39", "127x108" };
            int[] ns = { 1, 2, 4, 16 };
            for (int c = 0; c < cals.Length; c++)
                foreach (int n in ns)
                {
                    float r = PlaneDamage.TearRadius(cals[c], n);
                    Debug.Log($"TEARRADIUS id={names[c]}_n{n} cal={cals[c]:F5} n={n} r={r:F6} diameter={r * 2f:F6}");
                }
        }

        // ---- the join distance for pistol vs belly-gun energy (src/planedamage.js:1655 bridgeOf) ----
        static void BridgeAndJoinFacts()
        {
            var pistol = Cartridges.Para9x19;
            var akm = Cartridges.M43_762x39;
            var belly = Cartridges.Heavy127x108;

            float bPistol = PlaneDamage.BridgeOf(pistol);
            float bAkm = PlaneDamage.BridgeOf(akm);
            float bBelly = PlaneDamage.BridgeOf(belly);
            Debug.Log($"BRIDGE id=pistol bulletMm={pistol.BulletMm:F3} energy={pistol.Energy:F1} bridge={bPistol:F5}");
            Debug.Log($"BRIDGE id=akm bulletMm={akm.BulletMm:F3} energy={akm.Energy:F1} bridge={bAkm:F5}");
            Debug.Log($"BRIDGE id=belly bulletMm={belly.BulletMm:F3} energy={belly.Energy:F1} bridge={bBelly:F5}");

            // two single-round tears 0.25 m apart: does EACH cartridge's own bridge reach
            // join them? This is the literal "join distance is the round's ENERGY" claim.
            float gapM = 0.25f;
            foreach (var (name, cart) in new[] { ("pistol", pistol), ("akm", akm), ("belly", belly) })
            {
                float cal = cart.BulletMm / 1000f;
                var a = new PlaneTear { X = 0, Y = 0, Z = 0, N = 1, Cal = cal, R = PlaneDamage.TearRadius(cal, 1), Bridge = PlaneDamage.BridgeOf(cart) };
                var b = new PlaneTear { X = gapM, Y = 0, Z = 0, N = 1, Cal = cal, R = PlaneDamage.TearRadius(cal, 1), Bridge = PlaneDamage.BridgeOf(cart) };
                bool joined = PlaneDamage.Joined(a, b);
                Debug.Log($"JOIN id={name} gap={gapM:F3} joined={(joined ? 1 : 0)}");
            }
        }

        // ---- N hits at one spot produce ONE grown tear, not N decals (src/planedamage.js:903 _feed) ----
        static void FeedGrowthFacts()
        {
            var pd = new PlaneDamage();
            var cart = Cartridges.Para9x19;
            float cal = cart.BulletMm / 1000f;
            var list = new List<PlaneTear>();

            var t1 = pd.Feed(list, new Vector3(0, 0, 0), Vector3.up, cal, cart, "skin");
            Debug.Log($"FEED id=round1 count={list.Count} n={t1.N} r={t1.R:F5}");
            // same spot, well within merge reach (MERGE_CAL=3.5 bullet diameters)
            var t2 = pd.Feed(list, new Vector3(0.002f, 0, 0), Vector3.up, cal, cart, "skin");
            Debug.Log($"FEED id=round2_samespot count={list.Count} n={t2.N} r={t2.R:F5} sameTear={(ReferenceEquals(t1, t2) ? 1 : 0)}");
            // far away — outside any reasonable merge reach — must start a NEW tear
            var t3 = pd.Feed(list, new Vector3(5f, 0, 0), Vector3.up, cal, cart, "skin");
            Debug.Log($"FEED id=round3_faraway count={list.Count} n={t3.N} r={t3.R:F5} newTear={(!ReferenceEquals(t1, t3) ? 1 : 0)}");
        }

        // ---- every drawn hole needs a shader slot (src/planedamage.js:352 HoleSet.write) ----
        static void HoleSetFacts()
        {
            var set = new HoleSet();
            var all = new List<PlaneTear>();
            for (int i = 0; i < 10; i++)
                all.Add(new PlaneTear { X = i * 0.1f, Y = 0, Z = 0, R = 0.01f + i * 0.001f });
            set.Write(all, Matrix4x4.identity);
            Debug.Log($"HOLESET id=novranges count=10 tearAll={set.TearAll} tearCount={set.TearCount} " +
                      $"texW={HoleSet.TearW} texH={HoleSet.TearH} slots={HoleSet.HoleSlots}");
            // Separate, differently-shaped line so the JS side's constant-only
            // claim (tools/_probe_damage.mjs, HoleSet is module-private there)
            // actually pairs with something instead of silently matching zero
            // lines against the behavioural HOLESET line above.
            Debug.Log($"HOLESETCONST id=novranges texW={HoleSet.TearW} texH={HoleSet.TearH} slots={HoleSet.HoleSlots}");
            set.Dispose();
        }

        // ---- decals FITTED to what they hit — surfaceFit's 4-corner probe (Combat/ImpactFx.cs) ----
        static void SurfaceFitFacts()
        {
            var thin = new GameObject("thin_strut");
            var thinCol = thin.AddComponent<BoxCollider>();
            // a 2 cm x 2 cm strut running along Z: thin in BOTH cross-section axes, long
            // in Z. The decal basis below probes normal=+X, in-plane=(Z along the strut's
            // length, Y across its OTHER thin side) — the Y corners at full size (±0.5 m)
            // fall outside the strut's 1 cm half-extent and must shrink to land on it.
            thinCol.size = new Vector3(0.02f, 0.02f, 2f);

            var wide = new GameObject("wide_panel");
            wide.transform.position = new Vector3(10f, 0f, 0f);
            var wideCol = wide.AddComponent<BoxCollider>();
            wideCol.size = new Vector3(5f, 5f, 5f);

            float kThin = DecalGeometry.SurfaceFit(thinCol, thin.transform.position, Vector3.forward, Vector3.up, Vector3.right, 1f, 1f);
            float kWide = DecalGeometry.SurfaceFit(wideCol, wide.transform.position, Vector3.forward, Vector3.up, Vector3.right, 1f, 1f);
            Debug.Log($"SURFACEFIT id=thin_strut k={kThin:F4} shrunk={(kThin < 1f ? 1 : 0)}");
            Debug.Log($"SURFACEFIT id=wide_panel k={kWide:F4} fullsize={(Mathf.Approximately(kWide, 1f) ? 1 : 0)}");

            Object.DestroyImmediate(thin);
            Object.DestroyImmediate(wide);
        }

        // ---- dent depth for a given impact energy (src/dents.js:84-91) ----
        static void DentDepthFacts()
        {
            // the three calibration arrivals from src/dents.js's own header comment
            var apron = MetalDents.DentDepthFor(20000f);
            var belly = MetalDents.DentDepthFor(100000f);
            var crash = MetalDents.DentDepthFor(1100000f);
            Debug.Log($"DENTDEPTH id=apron_20kJ depth={apron.depth:F4} radius={apron.radius:F4}");
            Debug.Log($"DENTDEPTH id=belly_100kJ depth={belly.depth:F4} radius={belly.radius:F4}");
            Debug.Log($"DENTDEPTH id=crash_1100kJ depth={crash.depth:F4} radius={crash.radius:F4}");
        }

        // ---- the carved hole follows the real shape of the hit (src/breakwall.js) ----
        static void BreakWallFacts()
        {
            // a flat 10x10 m square footprint, one storey
            float[] fp = { -5f, -5f, 5f, -5f, 5f, 5f, -5f, 5f }; // game-frame x,z pairs
            var input = new BreakWall.BuildingWallInput
            {
                FootprintFlatGameFrame = fp, Base = 0f, Top = 10f,
                Tint = new Color32(190, 185, 178, 255), CxGame = 0f, CzGame = 0f,
            };
            var dummyMat = new Material(Shader.Find("Hidden/InternalErrorShader") ?? Shader.Find("Standard"));
            var shell = BreakWall.BuildShell(input, dummyMat, dummyMat);
            if (shell == null) { Debug.Log("BREAKWALL id=build failed=1"); return; }
            int before = shell.Live;
            var holes = new List<BreakWall.BlastHole> { new BreakWall.BlastHole { X = 5f, Y = 5f, Z = 0f, R = 2f } };
            BreakWall.Carve(shell, holes);
            int after = shell.Live;
            Debug.Log($"BREAKWALL id=carve cellsTotal={shell.Cells.Count} liveBefore={before} liveAfterBlast={after} " +
                      $"ateCells={(after < before ? 1 : 0)}");

            float origMaxY = 0f;
            for (int v = 0; v < shell.Count; v++) origMaxY = Mathf.Max(origMaxY, shell.OrigP[v * 3 + 1]);
            BreakWall.WriteShellVerts(shell, 0f, kh: 0.3f, jit: 0.1f, dark: 0.4f, leanX: 0.05f, leanZ: 0f, phase: 1);
            var posA = shell.Mesh.vertices;
            float newMaxY = 0f;
            foreach (var p in posA) newMaxY = Mathf.Max(newMaxY, p.y);
            // kh=0.3 squashes the whole height to 30% — robust regardless of the small
            // per-vertex jitter noise (src/breakwall.js:274-297's own formula).
            Debug.Log($"BREAKWALL id=crumble origMaxY={origMaxY:F3} squashedMaxY={newMaxY:F3} " +
                      $"squashed={(newMaxY < origMaxY * 0.5f ? 1 : 0)}");
            Object.DestroyImmediate(dummyMat);
        }

        // ---- shard ballistic sim: fires, falls, bounces, rests (src/glass.js) ----
        // GLASS_SEED must match tools/_probe_damage.mjs's GLASS_SEED exactly — both
        // sides seed the same mulberry32 stream through the injectable Rng hook
        // (BreakingGlass.Rng already existed for this; no production change needed),
        // consumed in the identical 15-per-shard/8-per-glint source order, so the
        // shard arrays — and therefore the rest step and the resting shard's own
        // position — are now a deterministic, byte-comparable fact instead of two
        // independent random walks that can never agree.
        const uint GlassSeed = 1337;
        static void BreakingGlassFacts()
        {
            var glass = new BreakingGlass { HeightAt = (x, z) => 0f, Rng = Mulberry32(GlassSeed) };
            glass.Shatter(0f, 3f, 0f, 1f, 0f, count: 20, power: 1f);
            int steps = 0;
            bool anyRested = false;
            int restIdx = -1;
            for (int i = 0; i < 600 && steps < 600; i++)
            {
                glass.Update(1f / 60f);
                steps++;
                for (int s = 0; s < BreakingGlass.ShardMax; s++)
                {
                    var sh = glass.ShardAt(s);
                    if (sh != null && sh.Rest) { anyRested = true; restIdx = s; break; }
                }
                if (anyRested) break;
            }
            Debug.Log($"GLASS id=shatter steps={steps} anyRested={(anyRested ? 1 : 0)}");
            if (anyRested)
            {
                var sh = glass.ShardAt(restIdx);
                Debug.Log($"GLASS id=restshard idx={restIdx} x={sh.X:F6} y={sh.Y:F6} z={sh.Z:F6} rx={sh.Rx:F6} rz={sh.Rz:F6} vx={sh.Vx:F6} vz={sh.Vz:F6}");
            }
        }

        // ---- structural cut: a region predicate really splits the render geometry (src/breakup.js) ----
        static void AirframeBreakupFacts()
        {
            var planeGo = new GameObject("probe_plane");
            var meshGo = new GameObject("probe_quad");
            meshGo.transform.SetParent(planeGo.transform, false);
            var mf = meshGo.AddComponent<MeshFilter>();
            var mr = meshGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(Shader.Find("Standard") ?? Shader.Find("Hidden/InternalErrorShader"));
            var mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            mf.sharedMesh = mesh;

            var breakup = new AirframeBreakup(new NullBreakupWorld(), planeGo.transform, null, false) { SizeHint = 2f };
            // a CUSTOM region (not the default wingL/tailZ, which this degenerate flat
            // probe quad's z=0 bounding box would trivially fail to intersect) — the
            // same "a cut nobody drew on the airframe ahead of time" path
            // src/planedamage.js's _sever() actually uses via Cut().
            var piece0 = breakup.Cut("probe_cut", p => p.x < 0f,
                impulse: new Vector3(1, 1, 0), spin: 2f, burn: false, flutter: true);
            bool ok = piece0 != null;
            Debug.Log($"BREAKUP id=detach ok={(ok ? 1 : 0)} partsCount={breakup.Parts.Count} goneHasCut={(!breakup.Has("probe_cut") ? 1 : 0)}");
            if (ok)
            {
                var piece = breakup.Parts[0];
                breakup.Update(1f / 60f);
                Debug.Log($"BREAKUP id=piece radius={piece.Radius:F4} dragK={piece.DragK:F4} velY={piece.Vel.y:F4}");
            }

            breakup.Dispose();
            Object.DestroyImmediate(planeGo);
        }
    }
}

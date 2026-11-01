using System.Collections;
using System.Linq;
using TbilisiFly.City;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves CityLoader.cs (the load ORDER), FacadeProps.cs (the AC/drain/
    /// entrance/dish/parapet/roof-clutter item counts), ChunkedInstancer.cs
    /// (the spatial partition) and CityCulling.cs (the per-frame visibility
    /// sweep) against real Tbilisi data — no running game, no scene, exactly
    /// the "proof not a claim" discipline the project's probes already use.
    ///
    /// The DECISIVE check is `cullAtFar15km`: CityRenderer currently draws
    /// every batch every frame with no culling at all; this probe builds the
    /// same kind of per-chunk partition CityRenderer's batches would need and
    /// proves a camera 15 km from the spawn draws ZERO of them, while a
    /// camera AT the spawn draws the lot.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CityLoadProbe.Run
    /// </summary>
    // KEY PREFIX IS "CL", NOT "CLP" — it must match tools/_probe_cityload.py
    // exactly. It printed CLP for a while, so none of its 35 lines could pair
    // with the Python side and tools/unity_port_check.py compared ZERO values
    // while the unit was being recorded as verified. One character.
    public static class CityLoadProbe
    {
        public static void Run()
        {
            var ctx = new CityLoader.Context { MaxBuildings = 20000 }; // cap — see class doc; order/shape proofs don't need all 193k

            DrainCoroutine(CityLoader.RunCritical(ctx, msg => Debug.Log($"CL progress={msg}")));
            Debug.Log($"CL id=criticalStageOrder order={string.Join(">", ctx.StageOrder)}");
            Debug.Log($"CL id=criticalStageOrderMatchesSpec matches={ctx.StageOrder.SequenceEqual(CityLoader.CriticalStageOrder)}");
            Debug.Log($"CL id=readyFlag ready={ctx.Ready}");
            foreach (var kv in ctx.StageErrors) Debug.Log($"CL STAGE_ERROR stage={kv.Key} msg={kv.Value}");

            int criticalCount = ctx.StageOrder.Count;
            DrainCoroutine(CityLoader.RunScenery(ctx));
            var sceneryOrder = ctx.StageOrder.Skip(criticalCount).ToList();
            Debug.Log($"CL id=sceneryStageOrder order={string.Join(">", sceneryOrder)}");
            Debug.Log($"CL id=sceneryStageOrderMatchesSpec matches={sceneryOrder.SequenceEqual(CityLoader.SceneryStageOrder)}");
            Debug.Log($"CL id=unportedStages list={string.Join(",", CityLoader.UnportedStages)}");

            // ---- A: load produced real data ----
            Debug.Log($"CL id=cityLoaded buildings={ctx.City?.Buildings.Count ?? 0} " +
                      $"terrainTiles={(ctx.City?.Terrain != null ? 1 : 0)}");
            int kitClaims = ctx.Rich != null
                ? ctx.Rich.Panels.Count + ctx.Rich.Houses.Count + ctx.Rich.Outs.Count + ctx.Rich.Apts.Count + ctx.Rich.Sheds.Count
                : 0;
            Debug.Log($"CL id=buildingsBuilt chunks={ctx.Rich?.Chunks.Count ?? 0} kitClaims={kitClaims} " +
                      $"reliefBalconies={ctx.Rich?.Relief.Balconies.Count ?? 0} reliefWindows={ctx.Rich?.Relief.Windows.Count ?? 0}");

            // ---- B: FacadeProps counts ----
            Debug.Log($"CL id=facadePropsBuildings processed={ctx.FacadePropsBuildingsProcessed} failed={ctx.FacadePropsBuildingsFailed} " +
                      $"kitClaimsSkipped={ctx.FacadePropsKitClaimsSkipped}");
            var p = ctx.PropsItems;
            Debug.Log($"CL id=facadePropCounts ac={p.Acs.Count} dish={p.Dishes.Count} drainpipe={p.Drainpipes.Count} " +
                      $"entrance={p.Entrances.Count} parapet={p.Parapets.Count} roofProp={p.RoofProps.Count}");
            int entOld = p.Entrances.Count(e => e.Variant == "ent_old");
            Debug.Log($"CL id=entranceVariants ent_old={entOld} entrance={p.Entrances.Count - entOld}");
            foreach (var g in p.RoofProps.GroupBy(r => r.Variant))
                Debug.Log($"CL id=roofPropVariant name={g.Key} count={g.Count()}");
            // budgets are 50k/30k/80k/60k/70k/50k (FacadeProps.*Budget) — proving
            // every pool stayed under its own cap, same discipline the JS's own
            // `.length < N` push-site guards enforce.
            Debug.Log($"CL id=facadePropBudgetsRespected " +
                      $"ac={p.Acs.Count <= FacadeProps.AcBudget} dish={p.Dishes.Count <= FacadeProps.DishBudget} " +
                      $"drainpipe={p.Drainpipes.Count <= FacadeProps.DrainBudget} entrance={p.Entrances.Count <= FacadeProps.EntranceBudget} " +
                      $"parapet={p.Parapets.Count <= FacadeProps.ParapetBudget} roofProp={p.RoofProps.Count <= FacadeProps.RoofPropBudget}");

            // ---- C + D: chunk every facade-prop layer, then cull it ----
            Matrix4x4 Mat(Vector3 pos, float angUnity, Vector3 scale) =>
                Matrix4x4.TRS(pos, Quaternion.AngleAxis(angUnity * Mathf.Rad2Deg, Vector3.up), scale);

            var acChunks = ChunkedInstancer.Place(p.Acs, it => it.Pos, it => Mat(it.Pos, it.AngUnity, Vector3.one),
                FacadeProps.Ac.ChunkM, false, FacadeProps.Ac.MaxDist);
            var dishChunks = ChunkedInstancer.Place(p.Dishes, it => it.Pos, it => Mat(it.Pos, it.AngUnity, Vector3.one),
                FacadeProps.Dish.ChunkM, false, FacadeProps.Dish.MaxDist);
            var drainChunks = ChunkedInstancer.Place(p.Drainpipes, it => it.Pos, it => Mat(it.Pos, it.AngUnity, new Vector3(1f, it.Sy, 1f)),
                FacadeProps.Drainpipe.ChunkM, false, FacadeProps.Drainpipe.MaxDist);
            var entranceChunks = ChunkedInstancer.Place(p.Entrances, it => it.Pos, it => Mat(it.Pos, it.AngUnity, Vector3.one),
                FacadeProps.Entrance.ChunkM, false, FacadeProps.Entrance.MaxDist);
            var parapetChunks = ChunkedInstancer.Place(p.Parapets, it => it.Pos, it => Mat(it.Pos, it.AngUnity, new Vector3(it.Sx, 1f, 1f)),
                FacadeProps.Parapet.ChunkM, false, FacadeProps.Parapet.MaxDist);
            var roofChunks = ChunkedInstancer.Place(p.RoofProps, it => it.Pos, it => Mat(it.Pos, it.AngUnity, Vector3.one),
                FacadeProps.RoofVent.ChunkM, false, FacadeProps.RoofVent.MaxDist);

            Debug.Log($"CL id=chunkedPartition acChunks={acChunks.Count} dishChunks={dishChunks.Count} " +
                      $"drainChunks={drainChunks.Count} entranceChunks={entranceChunks.Count} parapetChunks={parapetChunks.Count} roofChunks={roofChunks.Count}");

            var culling = new CityCulling { Terrain = ctx.City?.Terrain };
            culling.AddRange(acChunks);
            culling.AddRange(dishChunks);
            culling.AddRange(drainChunks);
            culling.AddRange(entranceChunks);
            culling.AddRange(parapetChunks);
            culling.AddRange(roofChunks);
            int totalChunks = culling.Chunks.Count;
            Debug.Log($"CL id=totalCullingChunks count={totalChunks}");
            // The uncontested baseline CityRenderer ships today: every chunk
            // drawn, every frame, regardless of camera position.
            Debug.Log($"CL id=drawnWithCullingOff count={totalChunks}");

            Vector3 spawn = ctx.Spawn; // "spawn" stage's own fallback is the city origin (Delisi) — see CityLoader's doc on _computeSpawn
            culling.Tick(spawn, 0f); // first-ever Tick is always a full, unsliced sweep (CullDue's "teleport from infinity")
            Debug.Log($"CL id=cullAtSpawn chunks={totalChunks} hiOn={culling.VisibleHiCount} loOn={culling.VisibleLoCount} " +
                      $"shadowOn={culling.VisibleShadowCount} sweepRan={culling.LastSweepRan} sweepFull={culling.LastSweepWasFull}");

            // 15 km beyond the spawn — past any real Tbilisi facade prop given
            // the biggest cull radius here is 1500 m (FacadeProps.Parapet/RoofVent).
            Vector3 far = spawn + new Vector3(0f, 0f, 15000f);
            culling.Tick(far, 0f);
            Debug.Log($"CL id=cullAtFar15km chunks={totalChunks} hiOn={culling.VisibleHiCount} loOn={culling.VisibleLoCount} " +
                      $"shadowOn={culling.VisibleShadowCount} sweepFull={culling.LastSweepWasFull}");
            Debug.Log($"CL id=decisiveCullCheck pass={culling.VisibleHiCount == 0 && culling.VisibleLoCount == 0}");

            // back at the spawn — proves the far jump didn't leave the sweep
            // permanently degraded (another teleport, another full sweep, hi
            // comes back on).
            culling.Tick(spawn, 0f);
            Debug.Log($"CL id=cullBackAtSpawn chunks={totalChunks} hiOn={culling.VisibleHiCount} sweepFull={culling.LastSweepWasFull}");

            // standing still (<2 m) must cost nothing — CullDue returns false,
            // no sweep, state unchanged. citymap.js:1118's own claim.
            int hiBeforeStill = culling.VisibleHiCount;
            bool dueWhenStill = culling.CullDue(spawn + new Vector3(0.1f, 0f, 0f));
            Debug.Log($"CL id=cullDueStandingStill due={dueWhenStill} hiUnchanged={culling.VisibleHiCount == hiBeforeStill}");

            // ---- E: collapse animation kinematics at fixed times ----
            var inst = new CollapseState { Dur = 4f, FromKh = 1f, IsInstOrLandmark = true };
            var shell = new CollapseState { Dur = 4f, FromKh = 1f, FromDark = 0f, FromLean = 0f, IsInstOrLandmark = false };
            for (int t = 1; t <= 5; t++)
            {
                var fi = CollapseAnim.Step(ref inst, 1f);
                var fs = CollapseAnim.Step(ref shell, 1f);
                Debug.Log($"CL id=collapseAtT{t} instP={fi.P:F4} instKh={fi.Kh:F4} instTilt={fi.Tilt:F4} instWobble={fi.Wobble:F4} instDone={fi.Done} " +
                          $"shellP={fs.P:F4} shellKh={fs.Kh:F4} shellBuckle={fs.Buckle:F4} shellDark={fs.Dark:F4} shellLean={fs.Lean:F4} shellDone={fs.Done}");
            }

            Debug.Log("CL id=done");
        }

        static void DrainCoroutine(IEnumerator e) { while (e.MoveNext()) { } }
    }
}

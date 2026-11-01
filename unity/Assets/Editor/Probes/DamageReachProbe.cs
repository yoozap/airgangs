using System;
using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Combat;
using TbilisiFly.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// QUALITY_BAR.md's "damage" row asks whether a bullet or a rocket hitting a
    /// building IN THE SHIPPED GAME actually lowers its HP, advances a real
    /// crumble stage (vertex range deformation visible), carves a blast hole at
    /// a facade corner, and breaks glass. Combat/BreakWall.cs, City/
    /// BuildingDamage.cs and Combat/BreakingGlass.cs are already probe-verified
    /// as CALCULATIONS (DamageProbe/CollapseStageProbe/GlassHoleProbe) — every
    /// one of those builds its OWN synthetic chunk and calls
    /// ApplyDamage/PunchHole/BlastWindows directly. None of them drives the
    /// production call chain a real round takes: MachineGun.Update/
    /// RocketLauncher.Update (what CombatWorldAdapter.GunTickable/
    /// RocketTickable call every frame) -> their private Fire/Impact ->
    /// Combat/CombatWorldHooks.cs's IDamageWorld, answered by the ONE live
    /// CombatWorldAdapter instance CombatWorldAdapter.Install wires onto
    /// GameLoop.Guns/GameLoop.Rockets.
    ///
    /// This probe opens the real Assets/Scenes/Game.unity, runs the same
    /// PlayableSceneBoot.Run() -> CombatWorldAdapter.Install(game) boot
    /// BootSceneCityProbe/CoreSpawnProbe use, picks the two real CombatBuildingChunk
    /// buildings nearest City/AirportBuilder.Get31LSpawn (the real airport spawn
    /// reference point LandmarkProbe already proves), and drives:
    ///
    ///   - game.Guns.Update(...)    (MachineGun)    -- the FIRST hit on a pristine
    ///     building, exactly the EnsureInit "first-hit-only" shape the unity
    ///     skill warns about, fired repeatedly (still through the same public
    ///     Update entry point) until a crumble stage actually advances.
    ///   - game.Rockets.Update(...) (RocketLauncher) -- one blast aimed at a real
    ///     footprint CORNER (the production plane transform is moved/aimed so
    ///     the rocket hardpoint offset + plane.forward lands exactly there),
    ///     read back through the real CombatBuildingChunk.EnsureShell cache the
    ///     real PunchHole call populated.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity \
    ///         -executeMethod TbilisiFly.Probes.DamageReachProbe.Run
    /// </summary>
    public static class DamageReachProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";
        static bool _allPass = true;

        public static void Run()
        {
            try { Body(); }
            catch (Exception e)
            {
                _allPass = false;
                Debug.Log($"DAMAGEREACH id=exception msg={e.Message}");
                Debug.LogException(e);
            }
            Debug.Log(_allPass ? "DAMAGEREACHPROBE RESULT=PASS" : "DAMAGEREACHPROBE RESULT=FAIL");
            ProbeExit.Exit(_allPass ? 0 : 1);
        }

        static void Check(string key, bool cond, string note = null)
        {
            string line = (cond ? "PASS " : "FAIL ") + key + (note != null ? $" ({note})" : "");
            Debug.Log(line);
            if (!cond) _allPass = false;
        }

        static void Body()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Check("sceneLoads", scene.IsValid());
            if (!scene.IsValid()) return;

            var game = UnityEngine.Object.FindFirstObjectByType<GameLoop>();
            var boot = UnityEngine.Object.FindFirstObjectByType<PlayableSceneBoot>();
            Check("gameLoopInScene", game != null);
            Check("playableSceneBootInScene", boot != null);
            if (game == null || boot == null) return;

            // The same boot BootSceneCityProbe/CoreSpawnProbe drive: PlayableSceneBoot.Run()
            // -> Game.ActivePlane.Init() -> CombatWorldAdapter.Install(game) -> hero spawn
            // -> Game.BeginSession(). After this, game.Guns/game.Rockets are the REAL,
            // live-wired MachineGun/RocketLauncher instances (Install():541-542).
            try { boot.Run(); } catch (Exception e) { Debug.Log($"DAMAGEREACH id=bootthrew msg={e.Message}"); }

            Check("install", game.CombatSystems.Count > 0, "CombatWorldAdapter.Install never ran");
            Check("gunsWired", game.Guns != null);
            Check("rocketsWired", game.Rockets != null);
            Check("activePlane", game.ActivePlane != null);
            if (game.Guns == null || game.Rockets == null || game.ActivePlane == null) return;

            // ---- the real airport spawn reference point (City/AirportBuilder.cs,
            // LandmarkProbe's own "AIRPORT spawn_31L" line) ----
            Vector3 refPoint;
            try
            {
                var ap = AirportBuilder.Load();
                var spawn = AirportBuilder.Get31LSpawn(ap, game.Terrain);
                refPoint = spawn.Pos;
            }
            catch (Exception e)
            {
                Debug.Log($"DAMAGEREACH id=airportspawnfailed msg={e.Message}");
                refPoint = game.ActivePlane.transform.position;
            }
            Debug.Log($"DAMAGEREACH airportSpawn=({refPoint.x:F1},{refPoint.y:F1},{refPoint.z:F1})");

            var chunks = UnityEngine.Object.FindObjectsByType<CombatBuildingChunk>(FindObjectsSortMode.None);
            Check("chunksInScene", chunks != null && chunks.Length > 0);
            if (chunks == null || chunks.Length == 0) return;

            var candidates = new List<(CombatBuildingChunk chunk, int bi, float d2)>();
            foreach (var c in chunks)
            {
                if (c == null) continue;
                int n = c.BuildingCount;
                for (int bi = 0; bi < n; bi++)
                {
                    float dx = c.CxOf(bi) - refPoint.x, dz = c.CzOf(bi) - refPoint.z;
                    candidates.Add((c, bi, dx * dx + dz * dz));
                }
            }
            candidates.Sort((a, b) => a.d2.CompareTo(b.d2));
            Check("foundCandidateBuildings", candidates.Count >= 2);
            if (candidates.Count < 2) return;

            // ---- pick a clear-line-of-sight WALL target for the gun, and a
            // clear-line-of-sight CORNER target (a different building) for the
            // rocket, walking outward from the airport spawn until each is found. ----
            const int SearchCap = 60;
            CombatBuildingChunk gunChunk = null; int gunBi = -1;
            Vector3 gunMuzzle = default, gunFwd = default, gunTarget = default;
            for (int k = 0; k < candidates.Count && k < SearchCap; k++)
            {
                var (c, bi, d2) = candidates[k];
                if (c.StageOf(bi) != 0 || c.HpFrac(bi) < 0.999f) continue; // must be pristine
                if (TryFindClearWallAim(c, bi, out gunMuzzle, out gunFwd, out gunTarget))
                { gunChunk = c; gunBi = bi; Debug.Log($"DAMAGEREACH gunPick dist={Mathf.Sqrt(d2):F1}m bi={bi}"); break; }
            }
            Check("gunAimFound", gunChunk != null);

            CombatBuildingChunk rkChunk = null; int rkBi = -1;
            Vector3 cornerPt = default, approachDir = default;
            Vector2 edgeAStart = default, edgeBEnd = default;
            float rkStandoff = 0f;
            for (int k = 0; k < candidates.Count && k < SearchCap; k++)
            {
                var (c, bi, d2) = candidates[k];
                if (c == gunChunk && bi == gunBi) continue; // a different building than the gun's
                if (c.StageOf(bi) != 0 || c.HpFrac(bi) < 0.999f) continue;
                // RocketLauncher.Damage=260 must NOT push this building to stage 9
                // (collapsed) on the first hit, or CombatBuildingChunk.PunchHole's
                // own `StageOf(bi) >= Stages-1` guard would refuse the hole —
                // comfortable margin so the wall is still standing afterward.
                if (c.MaxHpOf(bi) < 420f) continue;
                // City/CombatWorldAdapter.ComputeWindowAnchors only emits a tier
                // for f in [1, nF) with nF = floor(height/3.2) — a building under
                // ~2 storeys has NO window anchors at all (that is a real building
                // fact, not a bug), so panesBroken would be a legitimate 0 and
                // prove nothing about BlastWindows/StripPropsAt being reached.
                if (c.WallTopOf(bi) - c.WallBaseOf(bi) < 10f) continue;
                if (TryFindClearCornerAim(c, bi, out cornerPt, out approachDir, out edgeAStart, out edgeBEnd, out rkStandoff))
                { rkChunk = c; rkBi = bi; Debug.Log($"DAMAGEREACH rocketPick dist={Mathf.Sqrt(d2):F1}m bi={bi} standoff={rkStandoff:F0}"); break; }
            }
            Check("rocketAimFound", rkChunk != null);

            if (gunChunk != null) GunReach(game, gunChunk, gunBi, gunMuzzle, gunFwd);
            if (rkChunk != null) RocketReach(game, rkChunk, rkBi, cornerPt, approachDir, edgeAStart, edgeBEnd, rkStandoff);

            Debug.Log("DMG_PATH gun=MachineGun.Update->Fire->UpdateTracers->Impact->CombatWorldAdapter.DamageHit->CombatBuildingChunk.ApplyDamage");
            Debug.Log("DMG_PATH rocket=RocketLauncher.Update->Fire->UpdateRockets->Impact->CombatWorldAdapter.{DamageHit,PunchHole,StripPropsAt,BlastWindows}->CombatBuildingChunk.{ApplyDamage,PunchHole,EnsureShell}");
        }

        // =====================================================================
        // ---- aim-finding (measurement scaffolding only — the damage itself is
        // always driven through game.Guns.Update / game.Rockets.Update) --------
        // =====================================================================

        static readonly float[] WallStandoffs = { 12f, 20f, 30f };
        static readonly float[] CornerStandoffs = { 20f, 35f, 50f };

        static bool TryFindClearWallAim(CombatBuildingChunk chunk, int bi, out Vector3 muzzle, out Vector3 fwd, out Vector3 target)
        {
            muzzle = fwd = target = default;
            var ring = chunk.FootprintRingUnityOf(bi);
            if (ring == null) return false;
            int n = ring.Length / 2;
            if (n < 3) return false;
            float baseY = chunk.WallBaseOf(bi), top = chunk.WallTopOf(bi);
            float midY = baseY + (top - baseY) * 0.5f;
            float ccx = chunk.CxOf(bi), ccz = chunk.CzOf(bi);
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                float x0 = ring[i * 2], z0 = ring[i * 2 + 1], x1 = ring[j * 2], z1 = ring[j * 2 + 1];
                float ex = x1 - x0, ez = z1 - z0;
                float len = Mathf.Sqrt(ex * ex + ez * ez);
                if (len < 2f) continue;
                float nx = -ez / len, nz = ex / len;
                float mx = (x0 + x1) * 0.5f, mz = (z0 + z1) * 0.5f;
                if ((mx - ccx) * nx + (mz - ccz) * nz < 0f) { nx = -nx; nz = -nz; }
                Vector3 t = new Vector3(mx, midY, mz);
                Vector3 outward = new Vector3(nx, 0f, nz);
                foreach (float d in WallStandoffs)
                {
                    Vector3 m = t + outward * d;
                    Vector3 f = -outward;
                    if (Physics.Raycast(m, f, out RaycastHit hit, d + 2f) && hit.collider != null
                        && hit.collider.gameObject == chunk.gameObject)
                    { muzzle = m; fwd = f; target = t; return true; }
                }
            }
            return false;
        }

        static bool TryFindClearCornerAim(CombatBuildingChunk chunk, int bi, out Vector3 cornerPt, out Vector3 approachDir,
            out Vector2 edgeAStart, out Vector2 edgeBEnd, out float standoff)
        {
            cornerPt = approachDir = default; edgeAStart = edgeBEnd = default; standoff = 0f;
            var ring = chunk.FootprintRingUnityOf(bi);
            if (ring == null) return false;
            int n = ring.Length / 2;
            if (n < 3) return false;

            // Core/CombatWorldAdapter.ComputeWindowAnchors skips an edge under
            // 4 m outright and needs floor(len/3.4) >= 1 bay — a building whose
            // LONGEST edge is still short (a thin tower/chimney/kiosk footprint;
            // real OSM oddities with height but no facade) has ZERO window
            // anchors anywhere on it, so BlastWindows/StripPropsAt would have
            // nothing to break and "breaks glass" would prove nothing. Skip it
            // here so Body()'s candidate loop moves on to a building that can
            // actually exercise glass.
            float maxEdgeLen = 0f;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                float dxE = ring[j * 2] - ring[i * 2], dzE = ring[j * 2 + 1] - ring[i * 2 + 1];
                maxEdgeLen = Mathf.Max(maxEdgeLen, Mathf.Sqrt(dxE * dxE + dzE * dzE));
            }
            if (maxEdgeLen < 8f) return false;

            float baseY = chunk.WallBaseOf(bi), top = chunk.WallTopOf(bi);
            float midY = baseY + (top - baseY) * 0.5f;
            float ccx = chunk.CxOf(bi), ccz = chunk.CzOf(bi);
            for (int c = 0; c < n; c++)
            {
                float cx = ring[c * 2], cz = ring[c * 2 + 1];
                Vector3 away = new Vector3(cx - ccx, 0f, cz - ccz);
                if (away.sqrMagnitude < 1e-4f) continue;
                away.Normalize();
                Vector3 corner3 = new Vector3(cx, midY, cz);
                Vector3 aDir = -away; // from outside, toward the corner
                foreach (float d in CornerStandoffs)
                {
                    Vector3 start = corner3 - aDir * d;
                    if (Physics.Raycast(start, aDir, out RaycastHit hit, d + 2f) && hit.collider != null
                        && hit.collider.gameObject == chunk.gameObject)
                    {
                        int pi = (c - 1 + n) % n, ni = (c + 1) % n;
                        cornerPt = corner3; approachDir = aDir; standoff = d;
                        edgeAStart = new Vector2(ring[pi * 2], ring[pi * 2 + 1]);
                        edgeBEnd = new Vector2(ring[ni * 2], ring[ni * 2 + 1]);
                        return true;
                    }
                }
            }
            return false;
        }

        // =====================================================================
        // ---- the gun: MachineGun.Update, the real GunTickable entry point ----
        // =====================================================================
        static void GunReach(GameLoop game, CombatBuildingChunk chunk, int bi, Vector3 muzzle, Vector3 fwd)
        {
            Check("gunChunkPristine", chunk.StageOf(bi) == 0 && chunk.HpFrac(bi) >= 0.999f);

            float maxHp = chunk.MaxHpOf(bi);
            float hpBefore = maxHp * chunk.HpFrac(bi);
            int stageBefore = chunk.StageOf(bi);

            var mesh = chunk.GetComponent<MeshFilter>().sharedMesh;
            int start = chunk.RangeStart[bi], count = chunk.RangeCount[bi];
            var beforeAll = mesh.vertices;
            var snapBefore = new Vector3[count];
            Array.Copy(beforeAll, start, snapBefore, 0, count);

            int panesBroken = 0;
            Action<float[], float, float> onBurst = (flat, power, maxShards) => panesBroken += flat.Length / 5;
            game.Guns.OnGlassBurst += onBurst;

            float hpAfterFirst = hpBefore;
            int rounds = 0;
            const int MaxRounds = 80;
            int stageAfter = stageBefore;
            for (; rounds < MaxRounds; rounds++)
            {
                // THE REAL ENTRY POINT — the same MachineGun.Update CombatWorldAdapter.
                // GunTickable calls every frame while Space is held. dt=1s guarantees
                // the tracer (850 m/s) resolves to Impact() within this same call, so
                // Fire -> UpdateTracers -> Impact -> World.DamageHit all run here.
                game.Guns.Update(1f, true, muzzle, fwd);
                if (rounds == 0) hpAfterFirst = maxHp * chunk.HpFrac(bi);
                stageAfter = chunk.StageOf(bi);
                if (stageAfter != stageBefore) { rounds++; break; }
            }
            game.Guns.OnGlassBurst -= onBurst;

            float hpAfter = maxHp * chunk.HpFrac(bi);
            var afterAll = mesh.vertices;
            int vertsMoved = 0;
            for (int i = 0; i < count; i++) if (afterAll[start + i] != snapBefore[i]) vertsMoved++;

            Debug.Log($"DAMAGEREACH gunRounds={rounds} hpAfterFirstRound={hpAfterFirst:F2}");
            Check("gunFirstHitLowersHp", hpAfterFirst < hpBefore - 0.01f,
                  $"hpBefore={hpBefore:F2} hpAfterFirstRound={hpAfterFirst:F2} — the EnsureInit first-hit trap");
            Check("gunLowersHp", hpAfter < hpBefore);
            Check("gunAdvancesStage", stageAfter != stageBefore, $"{rounds} rounds, maxHp={maxHp:F1}");
            Check("gunDeformsVerts", vertsMoved > 0);

            Debug.Log($"DMG weapon=gun building={bi} hpBefore={hpBefore:F2} hpAfter={hpAfter:F2} " +
                      $"stageBefore={stageBefore} stageAfter={stageAfter} vertsMoved={vertsMoved} " +
                      $"holeCarved=false cornerTaken=false panesBroken={panesBroken}");
        }

        // =====================================================================
        // ---- the rocket: RocketLauncher.Update, the real RocketTickable entry
        // point, aimed by moving the REAL active plane so the production
        // hardpoint-offset math lands exactly on a footprint corner. ----------
        // =====================================================================
        static void RocketReach(GameLoop game, CombatBuildingChunk chunk, int bi, Vector3 cornerPt, Vector3 approachDir,
            Vector2 edgeAStart, Vector2 edgeBEnd, float standoffFound)
        {
            Check("rocketChunkPristine", chunk.StageOf(bi) == 0 && chunk.HpFrac(bi) >= 0.999f);

            var planeT = game.ActivePlane.transform;
            Quaternion savedRot = planeT.rotation;
            Vector3 savedPos = planeT.position;

            // RocketLauncher._side starts at 1 and FLIPS before the first Fire(),
            // so the first shot's hardpoint offset is (-1*1.7, -0.55, 0.9) —
            // Combat/Rockets.cs:Fire(). Force the max blast radius
            // (HoleR * (0.85 + Rng()*0.3)) via the gun's own public Rng hook —
            // same injectable-Rng convention DamageProbe's Mulberry32 uses on
            // BreakingGlass — so a corner hit has the best real chance of
            // catching cells on BOTH adjacent walls, not a weak one that
            // happens to miss.
            game.Rockets.Rng = () => 1f;
            // SAME standoff the line-of-sight probe actually verified — firing
            // from a different distance than the one checked risks a miss the
            // LOS check never saw.
            float standoff = standoffFound > 0f ? standoffFound : 45f;
            Vector3 hardpointWorld = cornerPt - approachDir * standoff;
            Vector3 localOffset = new Vector3(-1.7f, -0.55f, 0.9f);
            Quaternion rot = Quaternion.LookRotation(approachDir, Vector3.up);
            planeT.rotation = rot;
            // Combat/Rockets.cs:Fire() reads `plane.TransformPoint(localOffset)`,
            // which applies the plane's own lossyScale (re-skinned airframes are
            // NOT guaranteed unit scale — see the unity skill's "never reset an
            // FBX's natural import transform" lesson) — composing `rot *
            // localOffset` by hand silently dropped that scale and put the
            // rocket's real spawn point off the intended corner by the scale
            // error. TransformVector (position-independent) gives the SAME
            // rotation+scale composition Fire() will apply, so solving
            // `position = hardpointWorld - TransformVector(localOffset)` lands
            // Fire()'s own TransformPoint exactly on hardpointWorld.
            Vector3 offsetWorld = planeT.TransformVector(localOffset);
            planeT.position = hardpointWorld - offsetWorld;

            float maxHp = chunk.MaxHpOf(bi);
            float hpBefore = maxHp * chunk.HpFrac(bi);
            int stageBefore = chunk.StageOf(bi);

            var mesh = chunk.GetComponent<MeshFilter>().sharedMesh;
            int start = chunk.RangeStart[bi], count = chunk.RangeCount[bi];
            var beforeAll = mesh.vertices;
            var snapBefore = new Vector3[count];
            Array.Copy(beforeAll, start, snapBefore, 0, count);

            int panesBroken = 0;
            Action<float[], float, float> onBurst = (flat, power, maxShards) => panesBroken += flat.Length / 5;
            game.Rockets.OnGlassBurst += onBurst;

            // THE REAL ENTRY POINT — the same RocketLauncher.Update
            // CombatWorldAdapter.RocketTickable calls every frame while Space is
            // held with "rocket" selected. dt=1s guarantees the swept raycast's
            // step (>=130 m/s accelerating burn) clears the 45 m standoff within
            // this one call, so Fire -> UpdateRockets -> Impact -> World.{DamageHit,
            // PunchHole,StripPropsAt,BlastWindows} all run here.
            game.Rockets.Update(1f, true, planeT, 0f);

            game.Rockets.OnGlassBurst -= onBurst;

            // Diagnostic only — WindowDataOf is a cache GETTER after the real
            // call already populated it (CombatBuildingChunk.cs:407-414: "if
            // (_winPos.TryGetValue(bi, out var cachedPos)) return cachedPos"),
            // so this cannot corrupt what BlastWindows/StripPropsAt already
            // consumed; it just answers "did this building even HAVE window
            // anchors to break" so panesBroken=0 isn't mistaken for a bug.
            var (winPos, _, winBroken) = chunk.WindowDataOf(bi, (x, z) => null);
            int winBrokenCount = 0;
            if (winBroken != null) foreach (var b in winBroken) if (b) winBrokenCount++;
            Debug.Log($"DAMAGEREACH windowAnchors building={bi} total={(winPos?.Count ?? 0)} brokenMask={winBrokenCount}");

            float hpAfter = maxHp * chunk.HpFrac(bi);
            int stageAfter = chunk.StageOf(bi);
            var afterAll = mesh.vertices;
            int vertsMoved = 0;
            for (int i = 0; i < count; i++) if (afterAll[start + i] != snapBefore[i]) vertsMoved++;

            // The real shell the real PunchHole call built+carved —
            // CombatBuildingChunk.EnsureShell is a cache getter after the first
            // call (CombatBuildingChunk.cs:432-434: `if (_shells.TryGetValue(bi,
            // out var existing)) return existing;`), so this fetches the SAME
            // Shell instance, not a new build.
            var fallbackMat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            var liningMat = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Standard"));
            var shell = chunk.EnsureShell(bi, fallbackMat, liningMat);
            bool holeCarved = shell != null && shell.Holes.Count > 0 && shell.Live < shell.Cells.Count;

            bool cornerTaken = false;
            if (shell != null)
            {
                Vector2 corner2 = new Vector2(cornerPt.x, cornerPt.z);
                // 3D distance (height included) to the ACTUAL blast centre
                // (cornerPt.y == midY, the hole's own Y), not 2D X/Z: every row
                // in a wall column shares the same (X,Z) (BreakWall.BuildShell's
                // `x = x0 + (x1-x0)*fx` depends only on the column fraction, not
                // the row), so a pure-XZ nearest-cell search is ambiguous across
                // height and can resolve to the GROUND row while the rocket hit
                // mid-wall — a measurement bug, not a BreakWall one.
                int idxA = NearestCellOnSegment(shell, edgeAStart, corner2, cornerPt, 5f);
                int idxB = NearestCellOnSegment(shell, corner2, edgeBEnd, cornerPt, 5f);
                bool deadA = idxA >= 0 && IsCellDead(shell, idxA);
                bool deadB = idxB >= 0 && IsCellDead(shell, idxB);
                cornerTaken = deadA && deadB;
                var ca = idxA >= 0 ? shell.Cells[idxA] : default;
                var cb = idxB >= 0 ? shell.Cells[idxB] : default;
                Debug.Log($"DAMAGEREACH cornerCells idxA={idxA} posA=({ca.X:F2},{ca.Y:F2},{ca.Z:F2}) deadA={deadA.ToString().ToLowerInvariant()} " +
                          $"idxB={idxB} posB=({cb.X:F2},{cb.Y:F2},{cb.Z:F2}) deadB={deadB.ToString().ToLowerInvariant()} " +
                          $"holeXYZ=({cornerPt.x:F2},{cornerPt.y:F2},{cornerPt.z:F2}) cellsTotal={shell.Cells.Count} live={shell.Live}");
            }

            Check("rocketLowersHp", hpAfter < hpBefore);
            Check("rocketCarvesHole", holeCarved);
            Check("rocketCornerTaken", cornerTaken, "cells flanking the corner on BOTH adjacent walls must degenerate");
            Check("rocketBreaksGlass", panesBroken > 0, "BlastWindows/StripPropsAt via the real IDamageWorld call");

            Debug.Log($"DMG weapon=rocket building={bi} hpBefore={hpBefore:F2} hpAfter={hpAfter:F2} " +
                      $"stageBefore={stageBefore} stageAfter={stageAfter} vertsMoved={vertsMoved} " +
                      $"holeCarved={holeCarved.ToString().ToLowerInvariant()} cornerTaken={cornerTaken.ToString().ToLowerInvariant()} " +
                      $"panesBroken={panesBroken}");

            planeT.SetPositionAndRotation(savedPos, savedRot);
        }

        /// <summary>Index into shell.Cells of the cell nearest <paramref name="near"/>
        /// among cells that sit on the (segA,segB) edge (within 0.9 m of that
        /// segment's line) — i.e. "the real cell this building's own wall-shell
        /// put nearest the corner, on this specific edge", read off the REAL
        /// Shell the real PunchHole call built. Not a recomputation of the
        /// carve — purely a lookup by position into Shell.Cells, which BreakWall.
        /// BuildShell (called by the production CombatBuildingChunk.EnsureShell)
        /// already populates with each cell's own X/Z.</summary>
        static int NearestCellOnSegment(BreakWall.Shell shell, Vector2 segA, Vector2 segB, Vector3 near, float maxDist)
        {
            int best = -1; float bestD = float.MaxValue;
            Vector2 dir = segB - segA;
            float lenSq = dir.sqrMagnitude;
            for (int k = 0; k < shell.Cells.Count; k++)
            {
                var c = shell.Cells[k];
                Vector2 p = new Vector2(c.X, c.Z);
                float t = lenSq > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - segA, dir) / lenSq) : 0f;
                Vector2 proj = segA + dir * t;
                float distToLine = Vector2.Distance(p, proj);
                if (distToLine > 0.9f) continue; // must sit on THIS edge's line, any row
                float distToNear = Vector3.Distance(new Vector3(c.X, c.Y, c.Z), near); // 3D — row matters
                if (distToNear > maxDist) continue;
                if (distToNear < bestD) { bestD = distToNear; best = k; }
            }
            return best;
        }

        /// <summary>Combat/BreakWall.cs's own degenerate marker — Carve() sets all
        /// SIX indices of a dead cell's two triangles to c.A (BreakWall.cs:262),
        /// which survives GeoFrame.FlipWinding unconditionally (three equal
        /// indices flip to three equal indices).</summary>
        static bool IsCellDead(BreakWall.Shell shell, int k)
        {
            if (k < 0) return false;
            var tris = shell.Mesh.triangles;
            int o = k * 6;
            return o + 2 < tris.Length && tris[o] == tris[o + 1] && tris[o + 1] == tris[o + 2];
        }
    }
}

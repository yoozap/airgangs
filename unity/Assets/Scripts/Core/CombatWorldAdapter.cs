using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Combat;
using TbilisiFly.Data;
using TbilisiFly.Flight;
using TbilisiFly.Fx;
using TbilisiFly.UI.Common;
using TbilisiFly.UI.Credits;
using TbilisiFly.UI.Hud;
using TbilisiFly.UI.Settings;
using TbilisiFly.UI.SkillBar;
using TbilisiFly.UI.Target;
using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.Core
{
    // =========================================================================
    // CombatWorldAdapter.cs — the ONE place that answers Combat/CombatWorldHooks.cs's
    // IDamageWorld / IBallisticsWorld / IBreakupWorld against the REAL, live
    // systems (TerrainSampler for ground, the building chunks City/BuildingBuilder.cs
    // already extrudes for collidables, Core/AudioFx.cs + Fx/* for sound/visuals).
    //
    // GLASS + HOLE-CARVING ARE NOW REAL (src/citymap.js's blastWindows/
    // stripPropsAt/nearWindow/punchHole parity). City/BuildingBuilder.cs's Chunk
    // now carries, per extruded building, the ORIGINAL CityData.Buildings index
    // and the raw footprint ring it was extruded from (BuildingIndex/WallBase/
    // WallTop/ShellTint/ShellWallBucket/FootprintFlat+Offset+Count — baked onto
    // CombatBuildingChunk below by Editor/BuildPlayableScene.cs). That is what
    // was missing: NearWindow/BlastWindows/PunchHole/StripPropsAt could not
    // get from a RaycastHit back to a footprint before. Now:
    //   - NearWindow/BlastWindows/StripPropsAt read a per-building synthetic
    //     window-anchor grid (City/BuildingDamage.cs's WindowAnchors/NearWindow,
    //     cached per building — ComputeWindowAnchors below duplicates the
    //     position math to additionally keep the facade NORMAL per anchor,
    //     since BuildingDamage.cs may not be edited from this file and its own
    //     WindowAnchors discards the normal it computes internally). There is
    //     no instanced glass-pane prop layer wired to the chunk data yet (that
    //     is src/citymap.js's `propMeshes`, a City/FacadeRelief-owned concern
    //     out of this file's remit) — the window-anchor grid is the real,
    //     addressed, per-building substitute, not a stub.
    //   - PunchHole builds (and caches) a Combat/BreakWall.cs Shell for the hit
    //     building on first use, degenerates the ORIGINAL flat wall quad in the
    //     chunk's own mesh (so the hole doesn't show a solid wall behind it —
    //     same "step aside, never delete" trick src/citymap.js's _ensureShell
    //     uses), and carves the roof deck too when the hole reaches the
    //     roofline (City/BuildingDamage.cs's CarveRoof). The chunk's
    //     MeshCollider is deliberately NOT recooked per hole, same documented,
    //     bounded gap CombatBuildingChunk.PaintStage already accepts for the
    //     crumble stages (a round can clip a few cm of "solid" air at the rim
    //     of a very fresh hole).
    //   - Non-glass bolt-on props (balconies, AC units, shopfront signs) are
    //     NOT stripped by StripPropsAt — City/FacadeRelief's instances aren't
    //     addressable from a chunk/building index here either, a real,
    //     separate gap from the window glass above.
    //   - Structural/road/landmark TAGS (IsDestructible/IsLandmark/IsRoadSurface)
    //     — nothing in City/ attaches such tags to a collider today, so these
    //     answer false. SurfaceClassifier already falls through to its
    //     material-NAME heuristics first (CityMaterials names submeshes things
    //     like "wall:brick_red"/"asphalt"), which carries most real cases
    //     without any of these tags — see Ballistics.cs's own classifier order.
    // =========================================================================


    /// <summary>
    /// Forwards Combat's IDamageWorld/IImpactSink "make a noise / make a light"
    /// calls onto the real systems: Fx/FireSystem.cs for embers/smoke/plumes,
    /// Core/AudioFx.cs (FxAudio) for the sample library, Combat/ImpactFx.cs's own
    /// shared Debris/Sparks pools for particles (so a plane's own debris and a
    /// building's share one set of ring buffers, same consolidation ImpactFx's
    /// own header already does for guns vs rockets).
    /// </summary>
    public sealed class FxSink : IFireFx, IImpactSink
    {
        public readonly FireSystem Fire;
        readonly ImpactFx _impacts;
        readonly GameLoop _game;

        public FxSink(FireSystem fire, ImpactFx impacts, GameLoop game)
        {
            Fire = fire; _impacts = impacts; _game = game;
        }

        // ---- IFireFx ----
        public void Wave(float x, float y, float z, float radius) => Fire?.Wave(x, y, z, radius);
        public void SpawnEmber(float x, float y, float z) => Fire?.SpawnEmber(x, y, z);
        public void SpawnSmoke(float x, float y, float z, float size, float dark, float dur,
            float vx, float vy, float vz, float spread) => Fire?.SpawnSmoke(x, y, z, size, dark, dur, vx, vy, vz, spread);
        public void Plume(float x, float y, float z, float radius, float strength) => Fire?.Plume(x, y, z, radius, strength);
        // src/fire.js:518/434/415/429 — the collapse's scorch + mound smoulder/
        // fire. Straight delegation: FireSystem already owns both registries and
        // the decal ring (see IFireFx's own docs on why these four were missing).
        public void StampScorch(float x, float groundY, float z, float size) => Fire?.StampScorch(x, groundY, z, size);
        public void SetSmolder(int key, Vector3[] anchors) => Fire?.SetSmolder(key, anchors);
        public void SetBurning(int key, Vector3[] anchors, float intensity, float scale) => Fire?.SetBurning(key, anchors, intensity, scale);
        public void ClearBurning(int key) => Fire?.ClearBurning(key);

        // ---- IImpactSink (src/planedamage.js's debris/sound surface) ----
        public void Debris(Vector3 p, int n, Color32 tint, Vector3 dir, float cone, float speed, float size, float up, float life, float grav, float spread = 0f)
            => _impacts?.Debris.Emit(p, n, tint, speed: speed, spread: spread, up: up, size: size, life: life, grav: grav, dir: dir, cone: cone);
        public void Sparks(Vector3 p, int n, Color32 tint, Vector3 dir, float cone, float speed, float size, float life, float grav, float drag)
            => _impacts?.Sparks.Emit(p, n, tint, speed: speed, size: size, life: life, grav: grav, drag: drag, dir: dir, cone: cone);
        public void SpawnEmber(Vector3 p) => Fire?.SpawnEmber(p.x, p.y, p.z);
        public void SpawnSmokeWide(Vector3 p, float size, float dark, float dur, float vy, float spread)
            => Fire?.SpawnSmoke(p.x, p.y, p.z, size, dark, dur, 0f, vy, 0f, spread);
        public void ShatterGlass(Vector3 at, float nx, float nz, int count, float power) { /* building glass not modelled — see class header */ }
        public void HitSkinSound(float dist, float vol) => FxAudio.HitSkin(dist, vol);
        public void HitPerspexSound(float dist, float vol) => FxAudio.HitPerspex(dist, vol);
        public void HitSoftSound(float dist, float vol) => FxAudio.HitSoft(dist, vol);
        public void RicochetWhine(float dist, float vol, float energy) => FxAudio.RicochetWhine(dist, vol, energy);
        public void GlassBreakSound(float dist, float vol, bool big) => FxAudio.GlassBreak(dist, vol, big);
        public void MetalTearSound(float dist, float vol, float half) => FxAudio.MetalTear(dist, vol, half);
        public void ShakeGround(float amount, float x, float z) { /* no camera-shake system in scope yet */ }
        public void SetHint(string text) => _game.OnHint?.Invoke(text);
        public void DamagePlane(PlaneDamageRecord rec, int amount)
        {
            if (rec == null) return;
            rec.HullPoints = Mathf.Max(0f, rec.HullPoints - amount);
            if (rec.HullPoints <= 0f) rec.Dead = true;
        }
    }

    /// <summary>
    /// The IDamageWorld / IBallisticsWorld / IBreakupWorld implementation — one
    /// instance, handed to MachineGun/RocketLauncher/ImpactFx/BulletSim/PlaneDamage
    /// alike (see CombatBootstrap.cs's wiring). See this file's class header for
    /// what is and is not real here.
    /// </summary>
    public sealed class CombatWorldAdapter : IDamageWorld, IBallisticsWorld, IBreakupWorld
    {
        readonly TerrainSampler _terrain;
        readonly GameLoop _game;
        List<CombatBuildingChunk> _allChunks; // every chunk in the scene is built once and never changes
        Material _fallbackWallMat, _shellLiningMat;

        public CombatWorldAdapter(TerrainSampler terrain, GameLoop game)
        {
            _terrain = terrain;
            _game = game;
        }

        static CombatBuildingChunk ChunkOf(Collider c) => c != null ? c.GetComponent<CombatBuildingChunk>() : null;

        /// <summary>DamageHit boxes (chunk, bi) into DamageResult.Rec — unbox it the
        /// same way everywhere NearWindow/PunchHole are handed that opaque object.</summary>
        static bool TryUnbox(object rec, out CombatBuildingChunk chunk, out int bi)
        {
            chunk = null; bi = -1;
            if (rec is System.ValueTuple<CombatBuildingChunk, int> t) { chunk = t.Item1; bi = t.Item2; }
            return chunk != null && bi >= 0;
        }

        List<CombatBuildingChunk> AllChunks()
        {
            _allChunks ??= new List<CombatBuildingChunk>(Object.FindObjectsByType<CombatBuildingChunk>(FindObjectsSortMode.None));
            return _allChunks;
        }

        /// <summary>heightAt for WindowDataOf's buried-on-a-slope check — same
        /// Contains(-x,z)/HeightAt(x,z) convention GroundOrNull below already uses
        /// (Contains wants the GAME-frame x, HeightAt wants the already-mirrored
        /// Unity x the window anchor itself lives in).</summary>
        internal float? WindowHeightAt(float ux, float uz)
            => (_terrain != null && _terrain.Contains(-ux, uz)) ? (float?)_terrain.HeightAt(ux, uz) : null;

        void EnsureShellMaterials()
        {
            if (_shellLiningMat != null) return;
            var shader = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null
                ? Shader.Find("Universal Render Pipeline/Lit") : Shader.Find("Standard");
            _fallbackWallMat = new Material(shader) { name = "ShellWallFallback" };
            _fallbackWallMat.SetColor("_BaseColor", new Color(0.74f, 0.72f, 0.70f));
            // Unlit on purpose — src/citymap.js:2265's own _linMat is a
            // MeshBasicMaterial precisely so the gutted interior reads as a dark
            // room from every angle instead of catching the sun like the facade.
            var unlitShader = Shader.Find("Universal Render Pipeline/Unlit") ?? shader;
            _shellLiningMat = new Material(unlitShader) { name = "ShellLining" };
            _shellLiningMat.SetColor("_BaseColor", Color.black);
        }

        /// <summary>City/BuildingDamage.WindowAnchors' exact position math
        /// (citymap.js:2027-2060 _windowAnchors), reimplemented here (not called)
        /// ONLY to also keep the per-anchor facade normal that method computes
        /// then discards — BlastWindows/StripPropsAt need it for the pane's
        /// (x,y,z,nx,nz) return shape; NearWindow/WindowAt only ever need the
        /// positions, which this keeps byte-for-byte identical to that method so
        /// the 1.7 m/0.82 m radius checks land on the exact same points.
        /// `ringUnityXZ` and `cx`/`cz` must already be mirrored into Unity/mesh
        /// space (see CombatBuildingChunk.FootprintRingUnityOf/CxOf/CzOf).</summary>
        internal static void ComputeWindowAnchors(float[] ringUnityXZ, int n, float baseY, float top, float cx, float cz,
            System.Func<float, float, float?> heightAt, List<Vector3> outPos, List<float> outNormal)
        {
            float h = top - baseY;
            int nF = Mathf.Max(1, Mathf.FloorToInt(h / 3.2f));
            const int CAP = 3 * 64;
            for (int i = 0; i < n && outPos.Count < CAP; i++)
            {
                int j = (i + 1) % n;
                float x1 = ringUnityXZ[i * 2], z1 = ringUnityXZ[i * 2 + 1];
                float ex = ringUnityXZ[j * 2] - x1, ez = ringUnityXZ[j * 2 + 1] - z1;
                float len = Mathf.Sqrt(ex * ex + ez * ez);
                if (len < 4f) continue;
                float nx = -ez / len, nz = ex / len;
                float mx = x1 + ex / 2f, mz = z1 + ez / 2f;
                if ((mx - cx) * nx + (mz - cz) * nz < 0f) { nx = -nx; nz = -nz; }
                int bays = Mathf.FloorToInt(len / 3.4f);
                if (bays <= 0) continue;
                for (int k = 0; k < bays && outPos.Count < CAP; k++)
                {
                    float t = (k + 0.5f) / bays;
                    float wx = x1 + ex * t + nx * 0.5f;
                    float wz = z1 + ez * t + nz * 0.5f;
                    float? gy = heightAt(wx, wz);
                    for (int f = 1; f < nF && outPos.Count < CAP; f++)
                    {
                        float wy = baseY + 2f + f * 3.2f - 0.4f;
                        if (gy.HasValue && wy < gy.Value + 0.8f) continue; // buried on the slope
                        outPos.Add(new Vector3(wx, wy, wz));
                        outNormal.Add(nx); outNormal.Add(nz);
                    }
                }
            }
        }

        /// <summary>Shared engine behind BlastWindows and StripPropsAt — both read
        /// the same per-building window-anchor grid (see this file's class header
        /// on why StripPropsAt has no separate non-glass prop layer to strip
        /// here) and both PERMANENTLY consume the panes they return (the `broken`
        /// mask), so calling both on the same blast — exactly what src/rockets.js
        /// does, punchHole's stripPropsAt first then blastWindows — never returns
        /// the same pane twice. Flat, five-wide, nearest-first — see
        /// IDamageWorld.BlastWindows's own doc for why that shape is load-bearing.</summary>
        float[] CollectPanes(float px, float py, float pz, float radius)
        {
            var found = new List<(float x, float y, float z, float nx, float nz, float d2)>();
            float r2 = radius * radius;
            foreach (var chunk in AllChunks())
            {
                if (chunk == null) continue;
                int n = chunk.BuildingCount;
                for (int bi = 0; bi < n; bi++)
                {
                    float cx = chunk.CxOf(bi), cz = chunk.CzOf(bi), span = chunk.SpanOf(bi);
                    float dx = cx - px, dz = cz - pz;
                    float gate = span * 0.75f + radius;
                    if (dx * dx + dz * dz > gate * gate) continue;

                    var (pos, nrm, broken) = chunk.WindowDataOf(bi, WindowHeightAt);
                    if (pos == null || pos.Count == 0) continue;
                    float baseY = chunk.WallBaseOf(bi);
                    float kh = BuildingDamage.StageH[chunk.StageOf(bi)];
                    for (int k = 0; k < pos.Count; k++)
                    {
                        if (broken[k]) continue;
                        var a = pos[k];
                        float wy = baseY + (a.y - baseY) * kh; // anchors are pristine; walls crumble
                        float ddx = a.x - px, ddy = wy - py, ddz = a.z - pz;
                        float d2 = ddx * ddx + ddy * ddy + ddz * ddz;
                        if (d2 > r2) continue;
                        broken[k] = true;
                        found.Add((a.x, wy, a.z, nrm[k * 2], nrm[k * 2 + 1], d2));
                    }
                }
            }
            if (found.Count == 0) return System.Array.Empty<float>();
            found.Sort((a, b) => a.d2.CompareTo(b.d2));
            var flat = new float[found.Count * 5];
            for (int i = 0; i < found.Count; i++)
            {
                int o = i * 5; var f = found[i];
                flat[o] = f.x; flat[o + 1] = f.y; flat[o + 2] = f.z; flat[o + 3] = f.nx; flat[o + 4] = f.nz;
            }
            return flat;
        }

        // ---- IDamageWorld ----
        public DamageResult? DamageHit(RaycastHit hit, float damage)
        {
            var chunk = ChunkOf(hit.collider);
            if (chunk == null) return null; // terrain / kit prop / statue — nothing with HP here
            int bi = chunk.BuildingAt(hit.triangleIndex);
            if (bi < 0) return null;
            var (changed, destroyed) = chunk.ApplyDamage(bi, damage);
            return new DamageResult
            {
                Changed = changed,
                Destroyed = destroyed,
                Addr = $"შენობა #{bi}",
                HpFrac = chunk.HpFrac(bi),
                Cx = chunk.CxOf(bi), TopNow = chunk.TopOf(bi), Cz = chunk.CzOf(bi), Span = chunk.SpanOf(bi),
                Rec = (chunk, bi),
            };
        }

        /// <summary>src/citymap.js:2102 nearWindow — did this point land within the
        /// 1.7 m ball of a (still-intact) window cell on this building.</summary>
        public bool NearWindow(Collider hitObject, object rec, Vector3 point)
        {
            if (!TryUnbox(rec, out var chunk, out int bi)) return false;
            var (pos, _, broken) = chunk.WindowDataOf(bi, WindowHeightAt);
            if (pos == null || pos.Count == 0) return false;
            var live = pos;
            if (System.Array.IndexOf(broken, true) >= 0)
            {
                live = new List<Vector3>(pos.Count);
                for (int i = 0; i < pos.Count; i++) if (!broken[i]) live.Add(pos[i]);
                if (live.Count == 0) return false;
            }
            return BuildingDamage.NearWindow(live, chunk.StageOf(bi), chunk.WallBaseOf(bi), point);
        }

        /// <summary>src/citymap.js:2839 blastWindows.</summary>
        public float[] BlastWindows(float x, float y, float z, float radius) => CollectPanes(x, y, z, radius);

        /// <summary>src/citymap.js:2294 punchHole.</summary>
        public object PunchHole(object rec, Vector3 point, float radius)
        {
            if (!TryUnbox(rec, out var chunk, out int bi)) return null;
            EnsureShellMaterials();
            var hole = chunk.PunchHole(bi, point, radius, _fallbackWallMat, _shellLiningMat);
            return hole.HasValue ? (object)hole.Value : null;
        }

        /// <summary>src/citymap.js:2356 stripPropsAt — see this file's class header
        /// on why only the glass half of that method (not balconies/AC/signs) is
        /// real here.</summary>
        public float[] StripPropsAt(float x, float y, float z, float radius) => CollectPanes(x, y, z, radius);
        public void IgniteBlast(DamageResult res, Vector3 point, float radius) { }

        /// <summary>The shared collapse/blast debris field (Core/CollapseDebris.cs),
        /// assigned by <see cref="Install"/>. Null in a scene with no combat
        /// runtime, which is what <see cref="BlastDebris"/>'s guard is for.</summary>
        public CollapseDebris Debris;

        /// <summary>src/citymap.js:2561 `blastDebris(x, y, z, dir, n = 9)` — the
        /// chunks a warhead tears out of a wall, a third of them burning on the
        /// way down. Reached from src/rockets.js:309
        /// `g.world.blastDebris?.(p.x, p.y, p.z, n, res ? 12 : 7)` (ported as
        /// Combat/Rockets.cs). This was an EMPTY method body: the rocket called
        /// it, City/DebrisField.LaunchBlast implemented it, and the two were
        /// never joined, so no rocket ever threw concrete.</summary>
        public void BlastDebris(float x, float y, float z, Vector3 normal, int count)
            => Debris?.LaunchBlast(new Vector3(x, y, z), normal, count);
        /// <summary>src/game.js:430-435 `this.world.onShake` — rocket/ram-impact
        /// camera shake, scaled down with distance from the HERO's own plane
        /// (full strength inside 1.15 m, gone past 550 m). Real callers: src/
        /// rockets.js:287 (Combat/Rockets.cs:199) and src/planephys.js's ram
        /// (Flight/PlanePhysics.cs:754,814,1000) — this was a no-op stub so every
        /// one of those camera kicks was silently dropped.</summary>
        public void OnShake(float power, float x, float z)
        {
            var plane = _game.ActivePlane;
            if (plane == null) return;
            var pos = plane.transform.position;
            float dx = x - pos.x, dz = z - pos.z;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            float falloff = Mathf.Clamp01(1.15f - d / 550f);
            _game.AddShake(power * falloff, 1.6f);
        }

        // ---- IBallisticsWorld ----
        public string ExplicitSurfaceTag(Collider hit) => null;
        public bool TryBuildingFacade(Collider hit, Vector3 point, out bool isWindow)
        {
            isWindow = false;
            return ChunkOf(hit) != null;
        }
        public bool IsDestructible(Collider hit) => false;
        // citymap.js:1828 — a landmark takes no damage. Was literally `=> false`
        // with its own comment admitting nothing in City/ tagged a collider.
        // City/LandmarkTag.cs is that tag now. NOTE the tag is still attached by
        // nothing at runtime (landmark GameObjects are only built by an Editor
        // probe today), so this returns false in practice until that lands — but
        // the mechanism is in place and correct rather than hardcoded shut.
        public bool IsLandmark(Collider hit) => hit != null && hit.GetComponentInParent<City.LandmarkTag>() != null;
        public bool IsTerrain(Collider hit) => hit != null && hit.GetComponent<CombatTerrainTag>() != null;
        /// <summary>
        /// src/ballistics.js:420 `if (mat && world?.roadMat && mat === world.roadMat) return 'asphalt'` —
        /// the test that tells a ROAD DECK from bare ground once something has
        /// already been found to be terrain. Road tiles are tagged terrain too
        /// (the wheels rest on them, citymap.js:6808 `{collidable:true,
        /// ground:true}`), so without this a round landing on Rustaveli is
        /// classified off the terrain CLASS GRID and comes back `soil` or
        /// `grass`.
        ///
        /// It was hardcoded `=> false` — docs/open_items.json
        /// `collidable-flags`, the one place where the JS really does consult
        /// the road flag and the port did not. Material IDENTITY, not a name
        /// match, exactly as the JS does it: City/GroundMaterials.Asphalt() is
        /// the single shared instance citymap.js:6806 describes ("shared by
        /// junction aprons, bridge decks, tunnel floors"), so this answers true
        /// for all four of RoadNetworkRenderer's `collidable: true` surfaces
        /// and for nothing else.
        ///
        /// Note the asphalt Material is also NAMED "asphalt"
        /// (RoadBuilder.cs:694, "ballistics.js reads this name"), and
        /// SurfaceClassifier's name regex fires before IsTerrain is consulted,
        /// so today both routes agree. That is a reason to make this correct,
        /// not a reason to leave it shut: the name test is what breaks the
        /// moment a material is renamed or a road surface is drawn with an
        /// unnamed instance, and this is the branch the JS actually relies on.
        /// </summary>
        public bool IsRoadSurface(Collider hit)
        {
            if (hit == null) return false;
            var r = hit.GetComponent<Renderer>();
            return r != null && r.sharedMaterial != null && r.sharedMaterial == City.GroundMaterials.Asphalt();
        }
        public int GroundClassAt(float x, float z) => _terrain != null ? _terrain.ClassAt(x, z) : 0;

        // ---- IBreakupWorld ----
        public float? GroundAt(float x, float z)
            => (_terrain != null && _terrain.Contains(-x, z)) ? (float?)_terrain.HeightAt(x, z) : null;
        readonly System.Collections.Generic.Dictionary<string, int> _burnKeys = new System.Collections.Generic.Dictionary<string, int>();
        /// <summary>A stable int for a breakup part's string key. NEGATIVE on
        /// purpose: Fx/FireSystem's burning/smolder maps are ONE registry shared
        /// with the city, and a building publishes under its own building index
        /// (Core/CombatBuildingChunk.BurnKeyOf, >= 0, plus FireKeyBias), so
        /// handing out 0, 1, 2... here made the first torn-off wing evict
        /// building 0's mound fire and vice versa.</summary>
        int BurnKeyId(string key)
        {
            if (!_burnKeys.TryGetValue(key, out int id)) { id = -1 - _burnKeys.Count; _burnKeys[key] = id; }
            return id;
        }
        public void SpawnEmber(Vector3 at) => FireRef?.SpawnEmber(at.x, at.y, at.z);
        public void SpawnSmoke(Vector3 at, float size, float dark, float dur, float vy, float spread)
            => FireRef?.SpawnSmoke(at.x, at.y, at.z, size, dark, dur, 0f, vy, 0f, spread);
        public void SetBurning(string key, Vector3[] points, float intensity) => FireRef?.SetBurning(BurnKeyId(key), points, intensity);
        public void ClearBurning(string key) => FireRef?.ClearBurning(BurnKeyId(key));
        public void Plume(Vector3 at, float radius, float strength) => FireRef?.Plume(at.x, at.y, at.z, radius, strength);
        public Vector3 HeroVelocity() => _game != null && _game.ActivePlane != null ? _game.ActivePlane.Velocity : Vector3.zero;
        public bool HeroAirborne() => _game != null && _game.Mode == GameMode.Flying;

        /// <summary>Assigned once by Install() — kept settable rather than a
        /// constructor arg because the FireSystem and this adapter are built in the
        /// same breath and whichever comes first would otherwise need the other.</summary>
        public FireSystem FireRef;

        // =====================================================================
        // ---- Install() — the single wiring entry point ---------------------
        // Called from Boot.cs's StartFlightAsync (the real login->roster->fly
        // flow) AND from Editor/BuildPlayableScene.cs's PlayableSceneBoot (no
        // login/server available yet) AND from Editor/Probes/PlayableProbe.cs
        // directly — same call, three callers, by design (plain static method,
        // no MonoBehaviour lifecycle dependency, so it works identically
        // in-Play and in a batchmode editor script). Idempotent: a second call
        // on an already-wired GameLoop is a no-op.
        // =====================================================================
        public static void Install(GameLoop game)
        {
            if (game == null || game.CombatSystems.Count > 0) return;

            // Every phase below is independently try/caught and logged. A probe
            // run showed all four of CombatSystems/CrashFactory/body-spawn/UI
            // failing TOGETHER from one scene, which is the signature of ONE
            // early, uncaught exception aborting everything downstream of it
            // (this method used to run start-to-finish with no isolation) --
            // never confirmed exactly which line, because an all-or-nothing
            // method gives no way to tell. Isolating each phase means a failure
            // in, say, BoundaryWall.Load() no longer takes CombatSystems, the
            // UI panel and the hero body down with it, and whatever DOES throw
            // next time logs on its own line instead of vanishing into one
            // swallowed exception at the call site (Core/CombatWorldAdapter.cs's
            // own PlayableSceneBoot / Boot.cs's StartFlightAsync / the probe all
            // wrap THEIR call to Install() in a single try/catch too).
            TerrainSampler terrain = null;
            try
            {
                var city = Data.CityData.Load(null, buildings: false);
                terrain = city.Terrain;
                game.Terrain = terrain;
            }
            catch (System.Exception e) { Debug.LogException(e); }

            try
            {
                // GameLoop.Footsteps is a public field that NOTHING assigned, so
                // every step was silent: the port of footsteps.js existed in full
                // and was simply never constructed. It needs the ground sampler
                // for its surface-class fallback (footsteps.js:76
                // groundClassAt) — that is the one dependency it cannot default.
                //
                // BoreFloorAt is deliberately left null here. It wants
                // TunnelBuilder.BoreField.FloorAt so that a step inside a
                // flyable bore reads as concrete (footsteps.js:66), but the
                // BoreField is produced by CityLoader's tunnel stage and this
                // runtime path loads city data WITHOUT building tunnels
                // (buildings: false above, no tunnel plan at all). Wiring it
                // properly means making CityLoader's build result reachable at
                // runtime rather than living only inside its coroutine — a real
                // refactor, not a line. Until then a step in a bore falls back
                // to the terrain class under it, which is wrong but audible,
                // where before there was no step sound at all.
                if (game.Footsteps == null && terrain != null)
                    game.Footsteps = new Character.Footsteps { Terrain = terrain };
            }
            catch (System.Exception e) { Debug.LogException(e); }

            try { game.Boundary = BoundaryWall.Load(); }
            catch (System.Exception e) { Debug.LogException(e); }

            try { game.Zones = ZoneBuilder.Load(); }
            catch (System.Exception e) { Debug.LogException(e); }

            FireSystem fireSystem = null;
            // Hoisted out of the combat try-block below for the same reason
            // fireSystem is: game.WorldTick (further down, in its own try) has to
            // step the shared debris field every frame — src/citymap.js:2593
            // `_updateDebris(dt)`.
            CollapseDebris debris = null;
            try
            {
                var runtimeRoot = new GameObject("CombatRuntime").transform;

                fireSystem = new FireSystem();
                var impactFx = new ImpactFx();
                var glass = new BreakingGlass { HeightAt = (x, z) => GroundOrNull(game, x, z) };

                var adapter = new CombatWorldAdapter(terrain, game) { FireRef = fireSystem };
                // The adapter IS the IBreakupWorld; it was a local, so GameLoop had no
                // way to reach it when wiring a plane's airframe breakup later.
                game.BreakupWorld = adapter;
                var fxSink = new FxSink(fireSystem, impactFx, game);
                impactFx.World = adapter;
                // Combat/CoverShootBridge.cs — the purchased Cover+Shoot asset's
                // ShootBehaviour.ShootWeapon reaches this same adapter through the
                // bridge so an on-foot round damages buildings exactly like the
                // belly gun/rockets do (see that file's header; DamageReachProbe's
                // DMG_PATH is the production chain this now joins).
                TbilisiFly.Combat.CoverShootBridge.DamageWorld = adapter;

                var planeDamage = new PlaneDamage
                {
                    Impacts = fxSink,
                    BreakupWorld = adapter,
                    CameraPosition = () => game.MainCamera != null ? game.MainCamera.transform.position : Vector3.zero,
                };
                impactFx.PlaneDamage = planeDamage;

                var machineGun = new MachineGun { World = adapter, PlaneDamage = planeDamage, Impacts = impactFx };
                var blast = new BlastFx { Fx = fxSink, HeightAt = (x, z) => GroundOrNull(game, x, z) };
                var rockets = new RocketLauncher { World = adapter, Impacts = impactFx, Blast = blast };
                // src/game.js:206-207 keeps both ON the game object; this port
                // left them as locals, so nothing outside this method could
                // observe a shot. Same instances, published — see GameLoop.Guns.
                game.Guns = machineGun;
                game.Rockets = rockets;
                var bullets = new BulletSim { World = adapter, PlaneRefine = (hit, dir) => planeDamage.Refine(hit, dir) };
                // src/game.js:175-192 — THE IMPACT FENCE, and it is not
                // defensive boilerplate: the JS comment on these exact lines
                // says a throw inside any impact handler "used to abandon the
                // rest of that frame's rounds mid-flight: they were never
                // stepped and never retired, so they piled up against the live
                // cap and every frame after did more work than the last. That
                // is what a stutter while shooting turned out to be." Resolve()
                // is called from inside BulletSim's own step loop (Combat/
                // Ballistics.cs:598/620/625) and runs decals, glass, building
                // damage and the range's scoring faces, so the same exposure
                // exists here. One handler's failure loses its own effect and
                // nothing else, and says so exactly once (game.js:187-190).
                bool impactFailed = false;
                bullets.OnImpact = info =>
                {
                    try { impactFx.Resolve(info); }
                    catch (System.Exception e)
                    {
                        if (impactFailed) return;
                        impactFailed = true;
                        Debug.LogError($"impact handler failed (further ones are silent): {e}");
                    }
                };

                // src/guns.js:600 / src/rockets.js:302,306 g.glass.burstList — the
                // one place a pane blown out by BlastWindows/StripPropsAt actually
                // becomes a shatter (Combat/BreakingGlass.cs's own data, rendered by
                // whoever wires the VFX graph — see that file's class header).
                // `max<=0` is the JS call sites that pass no third argument
                // (rockets.js's own two calls) — BurstList's own default (46) applies.
                impactFx.OnGlassBurst += (flat, power, max) => glass.BurstList(flat, power, max > 0f ? Mathf.RoundToInt(max) : 46);
                rockets.OnGlassBurst += (flat, power, max) => glass.BurstList(flat, power, max > 0f ? Mathf.RoundToInt(max) : 46);
                // The third source: a machine-gun round through a window. guns.js:600
                // `g.glass.burstList(blown, 0.8, 3)` — a tighter budget than a rocket's
                // because one bullet takes one pane, not a facade's worth. This was a
                // stale stub in Guns.cs: its comment still said BlastWindows returned
                // object[], which had since become float[], so the panes it found were
                // being dropped on the floor.
                machineGun.OnGlassBurst += (flat, power, max) => glass.BurstList(flat, power, max > 0f ? Mathf.RoundToInt(max) : 46);

                // ---- src/game.js:425-433 — the COLLAPSE publishers ------------
                // The three OnGlassBurst subscriptions above are the GUN/ROCKET
                // glass path (guns.js:600, rockets.js:302/306). These are the
                // other, separate pipe the JS sets up on the CityMap itself, and
                // until now the C# collapse path raised nothing at all:
                //
                //   game.js:427  world.onWindowsBlown = list => glass.burstList(list, 1.3, 60)
                //   game.js:429  world.onBlast = (x,y,z,r) => blast.explode({x,y,z}, null, {r, marks:false})
                //   game.js:431  world.onShake = (s,x,z) => ... distance falloff
                //
                // Publishers: citymap.js:2833 (every pane off a building that just
                // dropped a crumble stage, or went down) and :2402/:2472/:2515
                // (collapse ignition, the secondaries as it pancakes, the ground
                // slam) — see Core/CombatBuildingChunk.cs's collapse section.
                //
                // ASSIGNED, not `+=`: one hooks object replaces the previous one,
                // so an editor domain reload that runs Install() twice does not
                // stack a second fireball on every collapse (this file's own
                // :536 comment flags that same re-Install hazard for the UI).
                // `marks: false` is load-bearing — a collapsing block must not
                // stamp a dozen rocket craters across the pavement.
                // The debris field: ONE instance, as src/citymap.js keeps ONE
                // `this._debris` array on the single CityMap. Fx/RubbleFx.cs's six
                // instanced layers come off the imported rubble_kit, and the step
                // runs from game.WorldTick below (a tick per chunk would step the
                // shared field 604 times a frame). The adapter holds the reference
                // so IDamageWorld.BlastDebris — src/rockets.js:309's
                // `g.world.blastDebris?.(...)`, a NO-OP stub until now — can reach it.
                debris = new CollapseDebris
                {
                    Fire = fxSink,
                    // DebrisField's own header: pass TerrainSampler.HeightAt (the
                    // mirrored-X overload), since debris spawns off chunk-mesh
                    // positions that are already in Unity space. The JS's
                    // `this.heightAt(d.x, d.z) ?? 0`.
                    GroundAt = (x, z) => GroundOrNull(game, x, z) ?? 0f,
                };
                adapter.Debris = debris;

                CombatBuildingChunk.Fx = new CollapseFxHooks
                {
                    OnWindowsBlown = list => glass.BurstList(list, 1.3f, 60),
                    OnBlast = (x, y, z, r) => blast.Explode(new Vector3(x, y, z), null, r, false, false),
                    OnShake = (s, x, z) => adapter.OnShake(s, x, z),
                    Fire = fxSink,
                    // citymap.js:2405/2473/2521 _launchDebris, :2505 _spawnRubble,
                    // :1553 _shedDebris — see CollapseFxHooks' own docs.
                    OnDebris = (cx, cz, span, fromY, n) => debris.Launch(cx, cz, span, fromY, n),
                    OnRubble = (x0, x1, z0, z1, b, t) => debris.SpawnRubble(x0, x1, z0, z1, b, t),
                    OnShed = (x0, x1, z0, z1) => debris.ShedDebris(x0, x1, z0, z1),
                    // The SAME ground query CollectPanes hands WindowDataOf — the
                    // anchor grid is cached on first call, so a second, subtly
                    // different heightAt would bake a different window set
                    // depending on whether a blast or a collapse touched the
                    // building first.
                    GroundAt = adapter.WindowHeightAt,
                };

                // src/guns.js:624-637/610 `_showToast` — the real-address hit toast.
                // All three publishers (machine gun, rockets, the plane-on-plane/
                // building impact path through ImpactFx) share ONE view, same as
                // the JS's single `this.toast` element. Each raises the SAME
                // Combat.DamageResult shape; GunFx.cs's GunToast.Format already
                // does the pure formatting (address/pct/bar colour) — this is
                // just the previously-missing subscribe side (GameLoop.OnHitToast
                // had zero subscribers on all three: docs/dead_events_baseline.txt).
                machineGun.OnHitToast += res => game.HitToast?.Show(GunToast.Format(res));
                rockets.OnHitToast += res => game.HitToast?.Show(GunToast.Format(res));
                impactFx.OnHitToast += res => game.HitToast?.Show(GunToast.Format(res));

                // src/papertargets.js:393-396 — the scored-target card, pushed
                // from the place a round actually scores (Combat/PaperTargets.
                // Hit, via Combat/RangeBootstrap.TryRouteHit, which raises
                // OnPaperScore for exactly this). Same previously-missing
                // subscribe side as the three OnHitToast lines above, with one
                // difference that matters: OnPaperScore is a STATIC event, so
                // unlike `machineGun`'s instance events it survives a domain
                // reload and a second Install() and would then paint the card
                // twice per round. Hence `-=` first, and a NAMED STATIC method
                // — a lambda cannot be unsubscribed, and an INSTANCE method
                // group would not compare equal to the one a destroyed
                // GameLoop left behind, so the stale subscription would stay.
                // _paperUiGame is how the static handler still reaches the
                // live GameLoop.
                _paperUiGame = game;
                RangeBootstrap.OnPaperScore -= OnPaperScored;
                RangeBootstrap.OnPaperScore += OnPaperScored;

                var held = new HeldWeapon(game, bullets, impactFx, runtimeRoot);

                game.CombatSystems.Add(new DelegateTickable(dt => bullets.Update(dt)));

                // src/ballistics.js:671-687 `_draw()` — the tapered round-streak
                // instances. Fx/BulletTrailFx.cs ports it (plus a handwritten
                // Shaders/BulletStreak.shader) and had ZERO callers: one of five
                // complete, correct Fx systems that nothing constructed, so no
                // tracer ever drew. Ticked right after the sim that owns `Live`,
                // so the streaks are always the frame's real bullet positions.
                var bulletTrail = new Fx.BulletTrailFx();
                game.CombatSystems.Add(new DelegateTickable(_ =>
                    bulletTrail.Update(bullets.Live,
                        game.MainCamera != null ? game.MainCamera.transform.position : Vector3.zero)));
                game.CombatSystems.Add(new DelegateTickable(dt => impactFx.Update(dt)));
                game.CombatSystems.Add(GunTickable(machineGun, game));
                game.CombatSystems.Add(RocketTickable(rockets, game));
                game.CombatSystems.Add(new DelegateTickable(dt => planeDamage.Update(dt)));
                game.CombatSystems.Add(new DelegateTickable(dt => blast.Update(dt)));
                game.CombatSystems.Add(new DelegateTickable(dt => glass.Update(dt)));
                game.CombatSystems.Add(held);

                // Every known plane becomes BOTH a PlaneDamage.Watch() subject AND a
                // GameLoop.Targets entry — Register() (GameLoop.cs:962, the nested
                // minimal registry Tab/click/HUD already drive) had nobody calling
                // it anywhere in the project; the parked fleet and the plane the
                // hero is flying were simply never fed in. "შენი მანქანა" (own
                // vehicle) is the Sub label the JS's target.js puts on the hero's
                // own machine — TargetSnapshot.Sub was empty for exactly that reason.
                foreach (var rec in Object.FindObjectsByType<PlaneDamageRecord>(FindObjectsSortMode.None))
                {
                    planeDamage.Watch(rec);
                    var planeRec = rec;
                    game.Targets.Register(new GameLoop.TargetRegistry.Entry
                    {
                        Wrapper = planeRec.transform,
                        Id = planeRec.gameObject.name,
                        Name = planeRec.DisplayName,
                        Sub = planeRec.IsPlayerOwn ? "შენი მანქანა" : "",
                        // src/target.js:212-213 `const tank = planeRole(def) === 'TANK'`
                        // -> `🛡 TANK` / `⚔ DPS`. TANK/DPS is the deliberate,
                        // correct label on PLANE UI (it is the hero/personage
                        // screen it is banned from — src/characters.js:23-25), and
                        // hardcoding false showed all 15 tank machines as DPS.
                        // The GameObject's name IS the PlaneDef.Id for every
                        // parked machine (Flight/PlaneHallFill.cs:149
                        // `go.name = def.Id`); an unknown name falls back through
                        // Planes.PlaneById to All[0] (spitfire, DPS), which is
                        // the JS's own `planeById(id) ?? PLANES[0]`.
                        RoleIsTank = TbilisiFly.Flight.Planes.IsTankPlane(planeRec.gameObject.name),
                        IsOwn = planeRec.IsPlayerOwn,
                        MaxHp = Mathf.Max(1, Mathf.RoundToInt(planeRec.MaxHullPoints)),
                        HpGetter = () => Mathf.RoundToInt(planeRec.HullPoints),
                        // src/target.js:317 — the selection ring's radius is
                        // `max(3, halfSpan * 1.06)`. PlaneDamageRecord already
                        // carries the real half wingspan (planes.js:1574
                        // `wrapper.userData.halfSpan`); without it every machine
                        // from a Spitfire to an An-225 drew the same 6.36 m circle.
                        HalfSpan = planeRec.HalfSpan,
                    });
                }

                // src/game.js:1777/1879 `this._shakeAmp = Math.min(CAP, this._shakeAmp +
                // addend)` — PlaneGroundRoll.cs/PlaneWallContact.cs already compute the
                // real addend and raise OnShakeAdd for it (hard touchdowns, wall/wingtip
                // strikes); nothing ever assigned the delegate, so every camera kick from
                // these two was silently dropped. The CAP is each call site's own, same
                // as the JS (1.5 for a touchdown, 1.6 for a wall/wingtip strike).
                foreach (var pc in Object.FindObjectsByType<PlaneController>(FindObjectsSortMode.None))
                {
                    if (pc.GroundRoll != null) pc.GroundRoll.OnShakeAdd = a => game.AddShake(a, 1.5f);
                    if (pc.WallContact != null) pc.WallContact.OnShakeAdd = a => game.AddShake(a, 1.6f);

                    // Core/PlaneRaycastWorld.cs's `Damage` was never assigned either
                    // (Flight/PlaneController.cs:168 constructs the World but has no
                    // reason to know about the building-damage system), so a plane
                    // flying square into a building (PlaneWallContact.cs:143
                    // `_world.Damage?.DamageHit(...)`) silently did nothing — no HP
                    // loss, no toast, no blown windows. Wiring both here is what
                    // makes `ShowBuildingToastHook` (below) ever fire for real.
                    if (pc.World != null)
                    {
                        pc.World.Damage = adapter;
                        pc.World.ShowBuildingToastHook = res => game.HitToast?.Show(GunToast.Format(res));
                    }
                }

                // game.js:408-414/2460-2492 crash-warning grid (Core/WarnGrid.cs).
                var warnGridGo = new GameObject("WarnGrid");
                warnGridGo.transform.SetParent(runtimeRoot, false);
                game.WarnGridRef = warnGridGo.AddComponent<WarnGrid>();
                game.WarnGridRef.Plane = game.ActivePlane;

                game.CrashFactory = plane => BuildCrash(plane, game, fireSystem);
                game.IsArmedProvider = () => held.Armed;
                game.OnToggleAim = held.ToggleAim;
                game.OnSetAiming = held.Gun.SetAiming;
                game.IsAimingProvider = () => held.Gun.Aiming;
                game.OnTakePickup = held.TakeNearest;
                game.OnSelectWeaponSlot = held.Select; // the action bar's draw/holster — see GameLoop's field doc
                ShootBehaviour.StowHook = held.StowPack;   // pack weapon leaving the hand → belt/back (HeldWeapon's own doc)
                ShootBehaviour.DrawHook = held.DrawPack;
                // src/game.js:521-524 — the R key's `this.pistol.reload()`.
                // Same shape as OnTakePickup; see HeldWeapon.Reload's own doc
                // for why hand reloading never worked before this line.
                game.OnReload = () => held.Reload();
                // src/game.js:1015-1027 `_loadout()` — what the hero OWNS, for
                // SaveState's snapshot. Same "narrow Func provider, wired here"
                // shape as IsArmedProvider/IsAimingProvider just above.
                game.LoadoutProvider = () => (held.CarriedLoadout(), held.WornGearId);

                // The weapon-reach half of foot.js's stance layer. FootController
                // computes gunTwist/gunDip/gunPitch from these two and feeds them
                // into CharacterRig.SwingBody; until they were wired the terms were
                // correctly computed and permanently zero, so a held weapon never
                // twisted the torso toward the shot.
                //
                // WHY HeldWeapon AND NOT FootWeapons: there are two independent
                // orchestrators over the same arm bones (see the skill's own
                // section on it). `Character/FootWeapons.cs` + `FootWeaponFrame.cs`
                // are constructed NOWHERE — `grep -rn "new FootWeapons"` returns
                // nothing — while HeldWeapon is the live owner that this adapter
                // builds and that GunAnim/RifleHolds are driven from. So the live
                // path supplies these, and `game.Foot.Weapons` is deliberately
                // left null rather than wiring the inert path and having both
                // drive the same bones in one frame.
                game.Foot.GunRockProvider = () => held.Gun.Rock;          // shooting.js: rad the torso is still rocked back
                game.Foot.AimDirProvider = () =>                           // foot.js's aim vector, only while actually aiming
                    held.Gun.Aiming && game.MainCamera != null ? (Vector3?)game.MainCamera.transform.forward : null;

                // THE BELT IS STANDARD ISSUE — src/foot.js:628-635, including its
                // own comment on why: the belt used to be loot on the warehouse
                // bench, so a hero who had not found it yet "put a pistol away
                // into thin air -- owned, invisible, and nowhere on the body."
                // Everyone wears one from the start; the bench one still exists
                // and simply does nothing the second time.
                //
                // Nothing in this port did this. Combat/Pickups.cs's
                // RestoreLoadout only wears gear ids that are already in the SAVED
                // kit, so a brand-new hero — every hero, since GameLoop.SaveState
                // does not write `gear` yet (see its header) — reached the
                // warehouse with no holster at all. Found by the port audit, not
                // by playing, because the failure is an ABSENT object: the stow
                // animation still runs, the pistol just vanishes at the end of it.
                game.OnBodySpawned += () =>
                {
                    var belt = Combat.GearDefs.GearById("belt");
                    if (belt == null) return;
                    // obj: null is the holster-only fallback Combat/Pickups.cs:317
                    // already uses — Character/FootBelt.cs skins the strap off the
                    // body's own weights and does not need a loaded belt.glb to
                    // give HeldWeapon somewhere to seat the pistol.
                    held.SetWornGear(belt, null);
                    if (!held.WearGear(belt, null))
                        Debug.LogWarning("belt did not go on — the hero will stow into thin air");
                };
            }
            catch (System.Exception e) { Debug.LogException(e); }

            game.OnGoAirport = () => GoAirportOrShed(game);

            try
            {
                var culling = new CityCulling { Terrain = terrain };
                // src/citymap.js:2593 `_updateDebris(dt)` runs from the CityMap's
                // own per-frame update alongside the culling sweep, so it rides
                // the same hook here. `debris` stays null if the combat block
                // above threw, and the null-conditional is what keeps this from
                // throwing on every frame of a scene with no combat runtime.
                game.WorldTick = (camPos, dt) =>
                {
                    culling.Tick(camPos, dt);
                    debris?.Tick(dt);
                    // src/game.js:2455 `this.fx.update(fdt, this.camera.position)`
                    // — THE WHOLE FIRE/SMOKE LAYER'S ONLY TICK. FireSystem was
                    // constructed (above) and handed to five consumers, but
                    // nothing ever stepped it: no ember moved, no smoke puff
                    // aged, the burning/smolder emitters never emitted and
                    // Render() — which is also where the flame quads, the dust
                    // rings and the scorch decals are DRAWN — never ran. It
                    // rides this hook because camPos is what the emitters' own
                    // 1400 m near-field test needs (src/fire.js:588) and it is
                    // the same per-frame world step the debris field uses.
                    fireSystem?.Update(dt, camPos);
                };
            }
            catch (System.Exception e) { Debug.LogException(e); }

            try { InstallUi(game); }
            catch (System.Exception e) { Debug.LogException(e); }

            Debug.Log($"COMBATWORLDADAPTER installed systems={game.CombatSystems.Count} " +
                      $"crashFactory={game.CrashFactory != null} terrain={terrain != null} " +
                      $"hud={game.Hud != null} skillBar={game.SkillBar != null} settings={game.Settings != null}");
        }

        static float? GroundOrNull(GameLoop game, float x, float z)
            => (game.Terrain != null && game.Terrain.Contains(-x, z)) ? (float?)game.Terrain.HeightAt(x, z) : null;

        static ITickable GunTickable(MachineGun gun, GameLoop game) => new DelegateTickable(dt =>
        {
            bool flying = game.Started && !game.Crashed && game.Mode == GameMode.Flying && game.ActivePlane != null;
            bool rocketSelected = game.SkillBar != null && game.SkillBar.Current == "rocket";
            var kb = UnityEngine.InputSystem.Keyboard.current;
            bool fire = flying && !rocketSelected && kb != null && kb.spaceKey.isPressed;
            // src/guns.js:417 `this.muzzleLocal = new THREE.Vector3(0, -0.95, -3.65)`
            // — the gun's own measured muzzle point, NOT "nose length + 1.5 m
            // clearance". That approximation is gone; Combat.GunPod.MuzzleWorld is
            // the real local offset (ported 1:1, z negated for the handedness flip)
            // applied to whichever plane is currently flying. GunPod.Build (wired
            // in GameLoop.WirePlaneCombat) guarantees the pod — and therefore this
            // offset — exists on every plane that can be flown.
            Vector3 muzzle = Vector3.zero;
            Vector3 fwd = Vector3.forward;
            if (game.ActivePlane != null)
            {
                fwd = game.ActivePlane.transform.forward;
                muzzle = GunPod.MuzzleWorld(game.ActivePlane.transform);
            }
            gun.Update(dt, fire, muzzle, fwd);
        });

        static ITickable RocketTickable(RocketLauncher rockets, GameLoop game) => new DelegateTickable(dt =>
        {
            bool flying = game.Started && !game.Crashed && game.Mode == GameMode.Flying && game.ActivePlane != null;
            bool rocketSelected = game.SkillBar != null && game.SkillBar.Current == "rocket";
            var kb = UnityEngine.InputSystem.Keyboard.current;
            bool fire = flying && rocketSelected && kb != null && kb.spaceKey.isPressed;
            float speed = game.ActivePlane != null ? game.ActivePlane.Velocity.magnitude : 0f;
            rockets.Update(dt, fire, game.ActivePlane != null ? game.ActivePlane.transform : null, speed);
        });

        /// <summary>A crash fire's key in the shared Fx/FireSystem registry,
        /// deterministic per string and parked in its own negative band
        /// (-1,000,000 and down) so it can collide with neither a building index
        /// nor a breakup part's BurnKeyId.</summary>
        static int CrashBurnKey(string key) => -1_000_000 - (key == null ? 0 : (key.GetHashCode() & 0xFFFF));

        static CrashSequenceHandle BuildCrash(PlaneController plane, GameLoop game, FireSystem fireSystem)
        {
            if (plane == null) return null;
            var rec = plane.GetComponent<PlaneDamageRecord>();
            float size = (rec != null && rec.Size > 0f) ? rec.Size : 11f;
            // src/game.js:2269-2273 `_showWasted()`'s own `if (this._wastedShown)
            // return; this._wastedShown = true;` guard — scoped per crash here
            // (a fresh closure every BuildCrash call) rather than per-Game, which
            // is the same observable behaviour: the JS resets `_wastedShown` in
            // resetPlane() (game.js:730), i.e. exactly once per crash lifetime.
            bool wastedShown = false;
            var hooks = new CrashHooks
            {
                GroundAt = (x, z) => GroundOrNull(game, x, z),
                SpawnEmber = p => fireSystem.SpawnEmber(p.x, p.y, p.z),
                SpawnSmoke = (p, o) => fireSystem.SpawnSmoke(p.x, p.y, p.z, o.Size, o.Dark, o.Dur, o.Vx, o.Vy, o.Vz, o.Spread),
                Plume = (x, y, z, r, h) => fireSystem.Plume(x, y, z, r, h),
                // CrashBurnKey, not key.GetHashCode(): the string hash is an
                // arbitrary 32-bit value that can land anywhere, including
                // straight on a building's own key in the ONE shared burning
                // registry (see BurnKeyId's note) — so "crash:plane" could evict
                // a collapsed block's mound fire. CrashBurnKey keeps crash fires
                // in their own negative band.
                SetBurning = (key, pts, inten) => fireSystem.SetBurning(CrashBurnKey(key), pts, inten),
                ClearBurning = key => fireSystem.ClearBurning(CrashBurnKey(key)),
                // game.js:2654-2670 takeFxLight/freeFxLight — previously
                // unassigned here, the ONE real BuildCrash call site, so every
                // crash fireball fell through CrashSequence.TakeLight's fallback
                // (`new GameObject("crash_fx_light"); AddComponent<Light>()`)
                // and re-triggered the exact "a new PointLight recompiles EVERY
                // material" shader-recompile freeze that Fx/FxLightPool.cs and
                // GameLoop.TakeFxLight/FreeFxLight exist to prevent. The pool was
                // fully built and reachable; this was the one missing wire.
                TakeFxLight = (c, inten, range, decay) => game.TakeFxLight(c, inten, range, decay),
                FreeFxLight = l => game.FreeFxLight(l),
                // src/crash.js's `_showWasted()` calls — previously unassigned
                // (CrashHooks.ShowWasted invoked at CrashSequence.cs:457/459,
                // nothing ever set it). Same destination as GameLoop.cs:402/740's
                // OnShowWasted/death-branch route (PlaneHullDamage's own crash
                // path) — both are the single death screen, so both go through
                // the SAME OnHint hook rather than inventing a second display path.
                ShowWasted = () =>
                {
                    if (wastedShown) return;
                    wastedShown = true;
                    game.OnHint?.Invoke(DeathScreenText.Title);
                },
            };
            var camTr = game.MainCamera != null ? game.MainCamera.transform : plane.transform;
            var seq = new CrashSequence(plane.transform, camTr, size, 1f, plane.Velocity, true,
                plane.Velocity.magnitude, plane.AltitudeAGL, hooks);
            return new CrashSequenceHandle(dt => seq.Update(dt));
        }

        /// <summary>src/airport.js's own warehouse buildShed() call: park=(0,0,-46),
        /// parkYawLocal=0, scale=(3,3,3) — the ONLY warehouse in this project, so
        /// kept here as real measured constants rather than re-deriving them from
        /// a ShedFill.ShedBuildResult that is never retained in a saved scene
        /// (ShedFill.cs's own header: "Park ... inert ... NOT consumed here, just
        /// carried" — this is that consumer).</summary>
        static readonly Vector3 ShedParkLocalGame = new Vector3(0f, 0f, -46f);
        const float ShedParkYawLocalGame = 0f;
        static readonly Vector3 ShedBuildScale = new Vector3(3f, 3f, 3f);

        /// <summary>src/game.js:884-898 `_parkInShed`'s fit check and park pose,
        /// against whatever GameObject named "warehouse" is actually in the live
        /// scene (same `this.scene.traverse` convention the JS itself uses to find
        /// `userData.shed` — "the airport has not built yet" degrades to `false`
        /// exactly as it does there). Game.unity carries no warehouse today
        /// (Editor/BuildPlayableScene.cs's own header calls it "a separate, larger
        /// porting unit" — PlaneHall.unity's is a different scene); this activates
        /// for real the moment one is merged in, with no further code change.</summary>
        static bool TryParkInShed(GameLoop game, PlaneController plane, GameObject warehouse)
        {
            var rec = plane.GetComponent<PlaneDamageRecord>();
            // src/game.js:895-896 — off the real airframe facts, never a fresh
            // Box3/bounds over the wrapper (that would also measure the climb
            // shell and cockpit kit, which is the exact trap the JS comment
            // there warns about).
            float span = rec != null ? Mathf.Max(rec.HalfSpan * 2f, rec.Size) : 0f;
            if (span <= 0f)
            {
                Debug.Log($"TRYPARKINSHED fail=noSpan hasRec={rec != null} halfSpan={(rec != null ? rec.HalfSpan : 0f):F2} size={(rec != null ? rec.Size : 0f):F2}");
                return false;
            }
            float tall = AirframeHeight(plane, rec);

            Vector3 door = ShedLayout.ScaledDoor(ShedBuildScale); // src/shed.js:82-86, real Door0 at this shed's real scale
            if (span > door.x * 2f - 2f || tall > door.y - 0.6f)
            {
                Debug.Log($"TRYPARKINSHED fail=fit span={span:F2} tall={tall:F2} doorX2m2={door.x * 2f - 2f:F2} doorYm06={door.y - 0.6f:F2} hasCockpitKit={(rec != null && rec.CockpitKit != null)}");
                return false; // src/game.js:898
            }

            // src/shed.js's park point is in shed-local game-frame metres —
            // mirror x (GeoFrame rule #3, ShedFill.cs's own header) before
            // handing it to a Unity Transform.
            Vector3 parkLocalUnity = new Vector3(-ShedParkLocalGame.x, ShedParkLocalGame.y, ShedParkLocalGame.z);
            Vector3 at = warehouse.transform.TransformPoint(parkLocalUnity);
            // src/game.js:900-906 — probe from INSIDE the shed, just over the
            // slab, not from the sky and not off the analytic terrain: "the
            // slab is a collidable, so the usual ground probe finds its top --
            // but probe from INSIDE the shed (just under the roof), or the ray
            // starting over the building reads the roof as the floor." The
            // 2 m maxAbove is what rejects that roof. Falling back to the
            // shed's own Y is the JS's own `?? slab`. This used to read
            // Terrain.HeightAt, which is the ground UNDER the shed rather than
            // the floor the machine stands on.
            float slab = warehouse.transform.position.y;
            float gy = plane.GroundProbe.GroundAt(at.x, at.z, slab + 1.5f, 2f) ?? slab;
            float yawDeg = warehouse.transform.eulerAngles.y
                + GeoFrame.YawToUnity(ShedParkYawLocalGame) * Mathf.Rad2Deg; // src/game.js:915-917

            float wheelH = plane.GroundProbe.WheelHeightFor(plane.Hull?.Struct.Gear ?? 1f); // per-airframe belly, see GameLoop.StageGroundProbe
            plane.transform.SetPositionAndRotation(
                new Vector3(at.x, gy + wheelH, at.z), Quaternion.Euler(0f, yawDeg, 0f));
            plane.Velocity = Vector3.zero;
            plane.Rates = Vector3.zero;
            // src/game.js:908-909 `this.airborne = false; this.speed = 0;` — the
            // two readout fields _parkInShed itself zeroes alongside vel/rates.
            // Missing before: Speed/Airborne kept whatever the plane had the
            // instant before 🛬 was pressed, so SyncReadouts (and the HUD speed
            // chip it feeds) could show a parked machine as still airborne/moving
            // for one frame until something else touched these fields.
            plane.Airborne = false;
            plane.Speed = 0f;
            Debug.Log($"TRYPARKINSHED ok span={span:F2} tall={tall:F2} at={at} gy={gy:F2} wheelH={wheelH:F2}");
            return true;
        }

        /// <summary>Unity-native stand-in for src/planes.js's measured
        /// `wrapper.userData.height` (no runtime mesh-measurement system is ported
        /// — PlaneCatalog.cs's own header). The cockpit kit (if any) is excluded
        /// explicitly, same exclusion the JS comment at game.js:892-894 calls
        /// for, and the climb-shell is a bare BoxCollider with no renderer so it
        /// is excluded for free.
        ///
        /// FOUND BY Assets/Tests/PlayMode/UserFlowsRun.cs's FLOW_AIRPORT
        /// scenario (2026-11-01), through three wrong turns before the real
        /// bug — kept here because the SAME mistake (starting from
        /// `Renderer.bounds` for a size/fit decision) is worth recognising
        /// elsewhere in this project:
        ///   1. "world-space bounds is wrong for a banked plane" — true, but
        ///      re-rotating `Renderer.bounds`' OWN corners into body space
        ///      made it WORSE (13.28 -> 13.65 -> 18.39 across three runs for
        ///      the identical fuselage mesh).
        ///   2. "the mesh geometry itself must be bad" (a stray submesh) —
        ///      disproved by the vertex cross-check: `mesh.vertices` through
        ///      the SAME localToWorldMatrix read a sane 2.50 m for the
        ///      fuselage while `Renderer.bounds` read 17.08 m for the exact
        ///      same frame — EVERY child disagreed with its own bounds the
        ///      same way (prop disc 3.78 vs 6.05, cockpit_kit 0.78 vs 1.93).
        ///   3. THE ACTUAL CAUSE: `Renderer.bounds` is already a WORLD-SPACE
        ///      axis-aligned box of the mesh AFTER its current rotation —
        ///      re-reading ITS corners and rotating them AGAIN is an AABB of
        ///      an AABB, which only ever grows (never shrinks back to the
        ///      true size) and grows differently depending on the plane's
        ///      current attitude at measurement time, exactly matching the
        ///      run-to-run drift.
        /// FIX: never start from `Renderer.bounds`. Take the mesh's own LOCAL
        /// bounds (`sharedMesh.bounds` — always available, even for an
        /// `isReadable:0` mesh, unlike `.vertices`), transform its 8 corners
        /// through the renderer's CURRENT `localToWorldMatrix` exactly once
        /// (this already encodes the renderer's position/rotation/scale, so
        /// there is only one AABB-widening step, not two), then un-rotate
        /// those world corners into the plane root's own body frame. Result:
        /// tall reads ~3.8 m for the Spitfire (matches its real ~3.9 m) and
        /// is stable run to run, because it no longer depends on which
        /// attitude the plane happened to be measured in.</summary>
        static float AirframeHeight(PlaneController plane, PlaneDamageRecord rec)
        {
            Transform root = plane.transform;
            Quaternion invRot = Quaternion.Inverse(root.rotation);
            Vector3 origin = root.position;
            float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
            foreach (var r in plane.GetComponentsInChildren<Renderer>())
            {
                if (rec != null && rec.CockpitKit != null && r.transform.IsChildOf(rec.CockpitKit)) continue;
                Bounds? lb = LocalMeshBounds(r);
                if (lb == null) continue;
                Matrix4x4 m = r.transform.localToWorldMatrix;
                Vector3 c = lb.Value.center, e = lb.Value.extents;
                for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 localCorner = c + new Vector3(sx * e.x, sy * e.y, sz * e.z);
                    Vector3 world = m.MultiplyPoint3x4(localCorner); // the ONE AABB-widening step
                    float localY = (invRot * (world - origin)).y;   // un-rotate into the plane's OWN up axis
                    if (localY < minY) minY = localY;
                    if (localY > maxY) maxY = localY;
                }
            }
            float tall = float.IsPositiveInfinity(minY) ? 0f : maxY - minY;
            LogAirframeHeightChildren(plane, rec); // DIAGNOSTIC — kept to confirm this fix on the fleet; see that method's header
            // SANITY CEILING — "a plane taller than its own wingspan is not a
            // plane." Never silently used to gate TryParkInShed's real
            // decision (a legitimately tall airframe like a C-17 would trip
            // it), just a loud signal that the measurement path is lying
            // again if this ever fires.
            float span = rec != null ? Mathf.Max(rec.HalfSpan * 2f, rec.Size) : 0f;
            if (span > 0f && tall > span)
                Debug.LogWarning($"AIRFRAMEHEIGHT sanity-fail tall={tall:F2} > span={span:F2} for {plane.PlaneId} — a real aeroplane is not taller than its own wingspan; the measurement path is still wrong");
            return tall;
        }

        /// <summary>The mesh's own LOCAL bounds — `sharedMesh.bounds`, never
        /// `Renderer.bounds` (see AirframeHeight's header). Works for a
        /// non-readable mesh (`mesh.vertices` would be empty/throw in a
        /// player build, per this project's own isReadable:0 meshes), and
        /// for a SkinnedMeshRenderer this is bind-pose local bounds — treated
        /// as rigid via the renderer's own transform, same approximation the
        /// coordinator asked for (this project's aeroplanes are not deeply
        /// skinned; a hinge like the canopy is its own separate Transform).</summary>
        static Bounds? LocalMeshBounds(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr)
                return smr.sharedMesh != null ? smr.sharedMesh.bounds : (Bounds?)null;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds : (Bounds?)null;
        }

        /// <summary>DIAGNOSTIC (2026-11-01): logs the widest-Y-span renderers
        /// under the plane (top 8) using the SAME local-bounds-through-
        /// localToWorldMatrix method AirframeHeight now uses, plus a vertex
        /// cross-check (only when the mesh is readable — `mesh.isReadable`,
        /// since this project ships isReadable:0 meshes where `.vertices` is
        /// empty) so a future regression back to raw `Renderer.bounds` shows
        /// up as a logged disagreement instead of a silent wrong number.</summary>
        static void LogAirframeHeightChildren(PlaneController plane, PlaneDamageRecord rec)
        {
            Transform root = plane.transform;
            Quaternion invRot = Quaternion.Inverse(root.rotation);
            Vector3 origin = root.position;
            var rows = new List<(string path, float yMin, float yMax, Vector3 scale, bool excluded)>();
            foreach (var r in plane.GetComponentsInChildren<Renderer>(true))
            {
                bool excluded = rec != null && rec.CockpitKit != null && r.transform.IsChildOf(rec.CockpitKit);
                Bounds? lb = LocalMeshBounds(r);
                float lMin = float.PositiveInfinity, lMax = float.NegativeInfinity;
                if (lb != null)
                {
                    Matrix4x4 m = r.transform.localToWorldMatrix;
                    Vector3 c = lb.Value.center, e = lb.Value.extents;
                    for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        Vector3 world = m.MultiplyPoint3x4(c + new Vector3(sx * e.x, sy * e.y, sz * e.z));
                        float localY = (invRot * (world - origin)).y;
                        if (localY < lMin) lMin = localY;
                        if (localY > lMax) lMax = localY;
                    }
                }
                string path = r.transform.name;
                for (var t = r.transform.parent; t != null && t != root; t = t.parent) path = t.name + "/" + path;
                rows.Add((path, lMin, lMax, r.transform.lossyScale, excluded));
            }
            rows.Sort((a, b) => (b.yMax - b.yMin).CompareTo(a.yMax - a.yMin));
            for (int i = 0; i < Mathf.Min(8, rows.Count); i++)
            {
                var row = rows[i];
                Debug.Log($"TRYPARKINSHED child={row.path} yMin={row.yMin:F2} yMax={row.yMax:F2} "
                    + $"span={(row.yMax - row.yMin):F2} scale=({row.scale.x:F2},{row.scale.y:F2},{row.scale.z:F2}) "
                    + $"excludedAsCockpitKit={row.excluded.ToString().ToLowerInvariant()}");
                var rTarget = root.Find(row.path);
                var mf = rTarget?.GetComponent<MeshFilter>();
                Mesh mesh = mf != null ? mf.sharedMesh : null; // SkinnedMeshRenderer vertices are bind-pose — not a fair cross-check, skipped
                if (mesh != null && mesh.isReadable)
                {
                    var verts = mesh.vertices;
                    Matrix4x4 m = mf.transform.localToWorldMatrix;
                    float vMin = float.PositiveInfinity, vMax = float.NegativeInfinity;
                    for (int v = 0; v < verts.Length; v++)
                    {
                        float localY = (invRot * (m.MultiplyPoint3x4(verts[v]) - origin)).y;
                        if (localY < vMin) vMin = localY;
                        if (localY > vMax) vMax = localY;
                    }
                    Debug.Log($"TRYPARKINSHED child={row.path} VERTEXCHECK vMin={vMin:F2} vMax={vMax:F2} "
                        + $"vSpan={(vMax - vMin):F2} vertCount={verts.Length} boundsSpan={(row.yMax - row.yMin):F2} "
                        + $"agree={(Mathf.Abs((vMax - vMin) - (row.yMax - row.yMin)) < 0.5f).ToString().ToLowerInvariant()}");
                }
            }
            Debug.Log($"TRYPARKINSHED totalRenderers={rows.Count} rootScale=({root.lossyScale.x:F2},{root.lossyScale.y:F2},{root.lossyScale.z:F2}) "
                + $"hasCockpitKit={(rec != null && rec.CockpitKit != null)} cockpitKitName={(rec != null && rec.CockpitKit != null ? rec.CockpitKit.name : "<null>")}");
        }

        /// <summary>src/game.js:955-974 — on foot, returning to the airport stands
        /// you at the warehouse's first bench (the pistols), facing them, inward
        /// off the shed's own -X wall. Same scene-traversal-by-name convention as
        /// TryParkInShed; returns false (no-op) when the warehouse/bench do not
        /// exist in the live scene yet.</summary>
        static bool TryStandAtWarehouseBench(GameLoop game, GameObject warehouse)
        {
            Transform table = null;
            foreach (var t in warehouse.GetComponentsInChildren<Transform>(true))
                if (t.name == "warehouse_table") { table = t; break; }
            if (table == null) return false;

            Vector3 tp = table.position;
            // the benches run along the shed's own -X wall: inside is its +X —
            // already in Unity's own correctly-mirrored local space (the FBX
            // importer did that once, at import time; ShedFill.cs's own header,
            // point 1), so no manual GeoFrame mirroring applies to this direction.
            Vector3 inward = warehouse.transform.TransformDirection(Vector3.right);
            inward.y = 0f;
            inward = inward.sqrMagnitude > 1e-8f ? inward.normalized : Vector3.forward;
            float px = tp.x + inward.x * 2.4f, pz = tp.z + inward.z * 2.4f;
            float gy = game.Terrain != null ? game.Terrain.HeightAt(px, pz) : tp.y;
            float yaw = Mathf.Atan2(inward.x, inward.z); // radians — FootController.Yaw convention

            game.OnSetAiming?.Invoke(false); // src/game.js:968 `this.pistol?.setAiming(false)`
            Vector3 standPos = new Vector3(px, gy + 0.02f, pz);
            if (game.Rig != null)
                game.Rig.Root.SetPositionAndRotation(standPos, Quaternion.Euler(0f, yaw * Mathf.Rad2Deg, 0f));
            game.Foot.Pos = standPos;
            game.Foot.Yaw = game.Foot.CamYaw = yaw;
            return true;
        }

        /// <summary>H key — goAirport()/_parkInShed() folded into one toggle (src/
        /// game.js's own table only ever bound ONE key here): near the shed spawn,
        /// go stand on the real runway (AirportBuilder's own OSM geometry); anywhere
        /// else, come back to the shed — now via TryParkInShed's real fit check
        /// when a "warehouse" GameObject exists in the scene, falling back to
        /// GameLoop.ShedSpawnPos/Rot (filled in once by Editor/BuildPlayableScene.cs
        /// at scene-build time) exactly like the JS falls back to the outside
        /// stand when `_parkInShed` returns false. On foot either way, the hero is
        /// placed at the warehouse's own bench and the pistol's aim is dropped
        /// (src/game.js:955-974) when that warehouse exists.</summary>
        static void GoAirportOrShed(GameLoop game)
        {
            if (game.ActivePlane == null || game.Terrain == null) return;
            var plane = game.ActivePlane;
            bool onFoot = game.Mode == GameMode.OnFoot; // src/game.js:933 `const onFoot = !!this.foot?.active;`
            GameObject warehouse = GameObject.Find("warehouse"); // src/game.js:886/958 `o.userData?.shed` / `o.name === 'warehouse'`
            Debug.Log($"GOAIRPORTORSHED warehouseFound={warehouse != null} planePos={plane.transform.position} shedSpawnPos={game.ShedSpawnPos}");

            // FOUND BY Assets/Tests/PlayMode/UserFlowsRun.cs's FLOW_AIRPORT
            // scenario (2026-11-01): this used to branch on `distToShed < 250f`
            // (measured against game.ShedSpawnPos — the plane's own EDITOR-
            // BAKE-TIME spawn pose, Editor/BuildPlayableScene.cs:401, which
            // Editor/BuildPlayableScene.cs's own comment already flags as
            // stale) into a SECOND behaviour with no counterpart anywhere in
            // src/game.js: "near the shed spawn, teleport to the real OSM
            // runway instead of parking". src/game.js:929-984 `goAirport()`
            // (and the identical game.js:536 `KeyH` binding) has exactly ONE
            // behaviour — always try to park the machine home — regardless of
            // where the hero currently is. Because the hero SPAWNS near home,
            // every press of 🛬 right after boarding took the invented branch
            // and sent the plane to the real airport runway, tens of
            // kilometres from the warehouse (measured: dist=19681 m) — the
            // opposite of "🛬 back to the airport / the warehouse"
            // (HudButtonsView.cs's own tooltip). Removed; the one behaviour
            // below is src/game.js:938-950 ported directly (try the shed,
            // else the outside stand), with no toggle.
            bool inside = warehouse != null && TryParkInShed(game, plane, warehouse);
            if (!inside)
            {
                plane.transform.SetPositionAndRotation(game.ShedSpawnPos, game.ShedSpawnRot);
                plane.Velocity = Vector3.zero;
                plane.Rates = Vector3.zero;
                // src/game.js:942-943 (the outside-the-bay fallback stand) —
                // same two fields TryParkInShed zeroes above for its own branch.
                plane.Airborne = false;
                plane.Speed = 0f;
            }
            game.OnHint?.Invoke(inside ? "🛬 ანგარში დაბრუნება · თვითმფრინავი საწყობშია"
                : "🛬 ანგარში დაბრუნება · თვითმფრინავი სადგომზეა (კარში ვერ ეტევა)"); // src/game.js:978-981
            // game.js:951-954 `if (!onFoot) { this._setHint(...); this._snapCamera(); }`
            if (!onFoot) game.FlightCam?.SnapToPlane();
            if (onFoot && warehouse != null) TryStandAtWarehouseBench(game, warehouse);
            game.SaveState(true); // src/game.js:982 `this.saveState(true, 'airport')`
        }

        /// <summary>The live GameLoop, for <see cref="OnPaperScored"/> — which
        /// has to be static (see the subscription's own comment in Install)
        /// and therefore cannot close over `game`.</summary>
        static GameLoop _paperUiGame;

        /// <summary>src/papertargets.js:396 `this._refresh(shot)`. `targets`/
        /// `card`/`shot` are never null: RangeBootstrap only invokes this when
        /// PaperTargets.Hit returned a real Shot (an off-card hit returns
        /// null), so there is no "did it score" test to repeat here.</summary>
        static void OnPaperScored(PaperTargets targets, PaperTargets.Card card, PaperTargets.Shot shot)
            => _paperUiGame?.ShowPaperScore(targets, card, shot);

        static void InstallUi(GameLoop game)
        {
            var uiGo = new GameObject("UI Panel");
            var doc = uiGo.AddComponent<UIDocument>();
            UiBootstrap.Apply(doc);
            var hud = new HudView();
            var chips = new HudChipsView(); // src/index.html:162-165 — speed/alt, see its own class header
            var skillBar = new SkillBarView();
            var weaponBar = new WeaponBarView();
            doc.rootVisualElement.Add(hud);
            doc.rootVisualElement.Add(chips);
            doc.rootVisualElement.Add(skillBar);
            doc.rootVisualElement.Add(weaponBar);
            // src/index.html:213-237 — the ⚙ 🛬 🚪 chip column (main.js:758/816).
            // Added AFTER the HUD layers so it draws over them, like the JS's
            // absolute-positioned buttons over #hud.
            var hudButtons = new TbilisiFly.UI.Hud.HudButtonsView(
                () => game.ToggleSettingsChip(),
                () => game.OnGoAirport?.Invoke(),
                () => UnityEngine.Object.FindFirstObjectByType<Boot>()?.Logout());
            doc.rootVisualElement.Add(hudButtons);
            game.HudButtons = hudButtons;
            game.Hud = hud;
            game.HudChips = chips;
            game.SkillBar = skillBar;
            game.WeaponBar = weaponBar;
            // THE BAR'S OWN EVENTS HAD NO SUBSCRIBERS (2026-11-02). WeaponBarView
            // raises Drawn(i)/Holstered/Hint exactly where skillbar.js:309-316
            // calls foot.drawWeapon/foot.holster/_setHint — and nothing listened,
            // so clicking a slot or pressing 1-8 highlighted it and did nothing.
            weaponBar.Drawn += i => game.OnSelectWeaponSlot?.Invoke(i);
            weaponBar.Holstered += () => game.OnSelectWeaponSlot?.Invoke(0);
            weaponBar.Hint += t => game.OnHint?.Invoke(t);

            // =================================================================
            // src/index.html:154-159 `#loading-overlay` — the text shown between
            // "▶ ამ გმირით შესვლა" and the world being ready. It did not exist
            // anywhere in this port (`grep -rn "loading-overlay\|LoadingOverlay"`
            // -> 0), which is exactly why Boot.HintChanged was baselined as a
            // DEAD DELEGATE: main.js:671-673 is the only writer of
            // `#loading-text` and nothing was listening.
            //
            // It goes on THIS panel, not the shell's: the hint fires after
            // Boot.SetScreen(BootScreen.Game), which has already disposed the
            // shell UIDocument (UI/Shell/ShellUi.cs's one-live-panel rule). It
            // is added AFTER the four HUD layers above so it draws over them,
            // matching src/style.css:196's `z-index: 10` against #hud's 5.
            //
            // The FIRST hint has already fired by the time Install() runs, so
            // Boot.LastHint is replayed rather than lost — see that property.
            // `-=` before `+=` because Boot outlives this panel and Install() is
            // reachable twice in a session (Boot.StartFlightAsync and
            // PlayableSceneBoot), and the method group makes the unsubscribe
            // actually match (a lambda would not).
            var loading = new TbilisiFly.UI.Common.LoadingOverlayView();
            doc.rootVisualElement.Add(loading);
            game.LoadingOverlay = loading;
            Boot boot = Object.FindAnyObjectByType<Boot>();
            if (boot != null)
            {
                boot.HintChanged -= loading.SetText;
                boot.HintChanged += loading.SetText;   // main.js:671-673
                loading.SetText(boot.LastHint);        // replay, see above
            }
            // main.js:668 `$('loading-overlay').classList.remove('hidden')`.
            // GameLoop.BeginSession hides it again at src/game.js:1390, which in
            // the usual flow is a few statements later — the overlay is what
            // covers the frames the city/body/resume work takes, exactly as in
            // the JS, and it is NOT left up if the session never begins.
            if (!game.Started) loading.Show(); else loading.Hide();

            // SettingsView/TargetCardView/CreditsView all clone a real UXML shell
            // (Assets/Resources/UI/{Settings,Target,Credits}/*.uxml, already imported) —
            // same "never wired into any scene" gap UiBootstrap.cs's own header
            // flags, now closed here. CreditsView carries the CC BY/BY-SA
            // attribution for 609 photographs by 95 photographers plus
            // OpenStreetMap's ODbL — docs/QUALITY_BAR.md calls out that this must
            // not be "only logged". Editor-only load (AssetDatabase) — same
            // established pattern as City/CityRenderer.cs's own #if UNITY_EDITOR
            // kit-mesh resolution, since this project only ever runs inside the
            // Editor today (no Resources/Addressables pipeline exists yet).
            var settingsUxml = ArtAssets.Load<VisualTreeAsset>("Assets/Resources/UI/Settings/Settings.uxml");
            var targetUxml = ArtAssets.Load<VisualTreeAsset>("Assets/Resources/UI/Target/TargetCard.uxml");
            var creditsUxml = ArtAssets.Load<VisualTreeAsset>("Assets/Resources/UI/Credits/Credits.uxml");
            if (settingsUxml != null)
            {
                var settings = new SettingsView(settingsUxml);
                doc.rootVisualElement.Add(settings);
                game.Settings = settings;

                // src/settings.js:175-186 `_applyEngine`/`_applyVolume` — the
                // engine toggle and the volume slider. SettingsView raises both
                // correctly and NOTHING subscribed to either
                // (`grep -rn "EngineToggled\|VolumeChanged"` outside that file:
                // zero), so the toggle and the slider did nothing at all. Now
                // they do exactly what the JS does: on -> start the rig and
                // re-apply the volume, off -> dispose it ("else if (s.ctx)
                // s.dispose()", i.e. only when one actually exists).
                settings.EngineToggled += on =>
                {
                    if (on)
                    {
                        game.Engine ??= new EngineSound();
                        game.Engine.Start();
                        game.Engine.SetVolume(settings.State.Volume / 100f); // settings.js:180 `this._applyVolume()`
                    }
                    else game.Engine?.Dispose(); // settings.js:181
                };
                settings.VolumeChanged += v => game.Engine?.SetVolume(v); // settings.js:184-186 (already /100 by ApplyVolume)

                // src/settings.js:163-173 `_applyQuality` — the SAME shape of
                // gap as the two above: SettingsView.QualityChanged had zero
                // subscribers (`grep -rn QualityChanged` outside SettingsView.cs:
                // zero), so the low/med/high/ultra segmented control saved a
                // string and changed nothing on screen, while the T key really
                // did switch shadows and post. GameLoop.ApplyQuality drives the
                // same knobs the T key does plus URP's renderScale (the JS's
                // `renderer.setPixelRatio`) — see its doc for the per-line map
                // and for why settings.js:167's `manual` flag has no subject here.
                settings.QualityChanged += (q, manual) => game.ApplyQuality(q, manual);
            }
            else Debug.LogWarning("CombatWorldAdapter: missing Assets/Resources/UI/Settings/Settings.uxml");
            if (targetUxml != null)
            {
                var targetCard = new TargetCardView(targetUxml);
                doc.rootVisualElement.Add(targetCard);
                game.TargetCard = targetCard;
            }
            else Debug.LogWarning("CombatWorldAdapter: missing Assets/Resources/UI/Target/TargetCard.uxml");
            if (creditsUxml != null)
            {
                // Standalone legal screen — no GameLoop field drives it per-frame
                // (it is opened on demand from Settings, not fed live data), but
                // it must exist in the scene, same bar as Settings/TargetCard.
                var credits = new CreditsView(creditsUxml);
                doc.rootVisualElement.Add(credits);
            }
            else Debug.LogWarning("CombatWorldAdapter: missing Assets/Resources/UI/Credits/Credits.uxml");
            // MapUiRoot needs map textures + district/subzone polygons on top of a
            // UXML — a bigger asset dependency this pass did not chase down.
            // GameLoop.MapUi stays null, which `MapUi?.Update`/`MapUi?.Toggle`
            // already guard.
        }

    }

}

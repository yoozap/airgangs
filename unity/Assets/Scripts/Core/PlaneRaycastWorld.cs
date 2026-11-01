using System;
using TbilisiFly.Combat;
using TbilisiFly.Data;
using TbilisiFly.Flight;
using UnityEngine;

namespace TbilisiFly.Core
{
    // =========================================================================
    // PlaneRaycastWorld.cs — the previous pass's own "WIRING NEEDED" item:
    // PlaneWallContact.cs/PlaneGroundProbe.cs/Flight/PlanePhysics.cs each take a
    // narrow World interface (IPlaneWallWorld / IGroundProbeWorld /
    // IPlanePhysicsWorld) so they stay engine-call-site-agnostic, but nothing in
    // the project ever implemented one — PlaneWallContact.cs's own header asks
    // for exactly this: "a thin Physics.RaycastAll wrapper forwarding to the
    // existing IDamageWorld" rather than a second building-damage system.
    //
    // ============================ DESIGN ============================
    // Deliberately THIN. Raycasts and terrain sampling are REAL (Physics.
    // RaycastAll / Physics.Raycast / TerrainSampler.HeightAt) because those
    // need nothing this file doesn't already have. FX/audio/building-damage
    // are Action/Func HOOKS, default no-op, exactly the pattern GameLoop.cs
    // already uses for every system with no Unity counterpart yet:
    //   - `Damage` (IDamageWorld) is forwarded, not constructed — Core/
    //     CombatWorldAdapter.cs (locked this session) owns the real building-
    //     damage system and assigns it here once it exists; until then every
    //     building-hit path degrades to "FX only, no damage", which is exactly
    //     PlaneWallContact.cs's own documented behaviour for a null collider.
    //   - SpawnEmber/SpawnSmoke/DustBurst/BurstGlass/ShowBuildingToast have no
    //     Unity FX system wired to them yet either (Fx/FireSystem.cs exists
    //     but needs its own camera-facing material set, which belongs to
    //     whoever constructs it — CombatWorldAdapter's own header lists
    //     "FireSystem" as one of ITS constructions) — same honest-hook
    //     treatment, not a silent gap.
    //   - HitConcreteSound/CollapseRumbleSound ARE wired for real: Core/
    //     AudioFx.cs's static HitConcrete/CollapseRumble calls need nothing
    //     else constructed first.
    //
    // IsTerrain uses the SAME CombatTerrainTag marker Core/CombatWorldAdapter.cs
    // already defines (a bare MonoBehaviour) rather than inventing a second
    // one. IsPlaneCollider follows IPlaneWallWorld's own doc comment exactly:
    // GetComponentInParent<PlaneDamageRecord>() != null.
    // =========================================================================
    public sealed class PlaneRaycastWorld : IPlaneWallWorld, IGroundProbeWorld, IPlanePhysicsWorld, MetalDents.IWorld
    {
        public TerrainSampler Terrain;
        public LayerMask Mask = ~0;

        /// <summary>Forwarded, not owned — see class header. Null until
        /// CombatWorldAdapter.cs (or a future pass) assigns the real one.</summary>
        public IDamageWorld Damage { get; set; }

        // ---- FX/audio hooks, every one optional (default no-op) ----
        public Action<Vector3> SpawnEmberHook = _ => { };
        public Action<Vector3, float, float, float, float, float> SpawnSmokeHook = (p, sz, dk, dur, vy, sp) => { };
        public Action<Vector3, int, float> DustBurstHook = (p, n, r) => { };
        public Action<float[], float, float> BurstGlassHook = (blown, power, count) => { };
        public Action<DamageResult> ShowBuildingToastHook = _ => { };
        public Func<float> SoundVolumeHook = () => 0.7f;
        public Action<Vector3, float> ExplosionHook = (p, r) => { };
        public Action<Vector3, float, float> PlumeHook = (p, r, strength) => { };
        public Action<Vector3, float> WaveHook = (p, r) => { };
        public Action<Vector3, float> StampScorchHook = (p, size) => { };
        public Action<float, float, float> OnShakeHook = (power, x, z) => { };

        /// <summary>THE MACHINE BEING FLOWN, excluded from every cast this
        /// class makes. src/game.js:834-836, verbatim: "It is deliberately NOT
        /// in the world's collidables: that set is what the aeroplane's own
        /// ground probe reads, and a machine that can find its own shell under
        /// its wheels sits on itself". `world.collidablesNear` is the ONLY set
        /// the JS's _touchWall / _sampleStreetY / _topSurfaceBelow /
        /// _checkCollision ever sweep, so leaving the active wrapper out of it
        /// there leaves it out of all of them — this field is that one
        /// exclusion, in the one place the port turns a cast into hits.
        ///
        /// FOUND BY THE SECOND PLAY-MODE RUN (2026-10-30): Unity's broadphase
        /// has no such notion, so `PlaneWallContact.TouchWall`'s nose sweep hit
        ///     col=plane_shell dist=0.003 headOn=1
        /// — the Spitfire's OWN box shell, 3 mm in front of its own origin —
        /// every time the 0.4 s hit cooldown expired. Each one cost
        /// `speed *= 0.3` (game.js:2008) plus `sp * (1.2 + 5.5 * headOn)` hull
        /// damage, so a taxiing aeroplane beat itself to death at 25 m/s and
        /// never reached Vr 44.8 m/s. The JS cannot express this bug.</summary>
        public Transform OwnPlane;

        bool IsOwn(Collider c) => OwnPlane != null && c != null && c.transform.IsChildOf(OwnPlane);

        /// <summary>Drop the own-plane hits in place (no second allocation on
        /// the common zero-own-hits path).</summary>
        RaycastHit[] DropOwn(RaycastHit[] hits)
        {
            if (OwnPlane == null || hits.Length == 0) return hits;
            int keep = 0;
            for (int i = 0; i < hits.Length; i++)
                if (!IsOwn(hits[i].collider)) hits[keep++] = hits[i];
            if (keep == hits.Length) return hits;
            var outp = new RaycastHit[keep];
            Array.Copy(hits, outp, keep);
            return outp;
        }

        // ---- real: raycasts (Unity's own broadphase stands in for
        // src/citymap.js's collidablesNear spatial hash, see each interface's
        // own doc comment) ----
        public RaycastHit[] RaycastAllSorted(Vector3 origin, Vector3 dir, float maxDist)
        {
            if (maxDist <= 0f || dir.sqrMagnitude < 1e-10f) return Array.Empty<RaycastHit>();
            var hits = Physics.RaycastAll(origin, dir.normalized, maxDist, Mask, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            return DropOwn(hits);
        }

        public bool RaycastWorld(Vector3 origin, Vector3 dir, float maxDist, out RaycastHit hit)
        {
            if (dir.sqrMagnitude < 1e-10f) { hit = default; return false; }
            if (OwnPlane == null)
                return Physics.Raycast(origin, dir.normalized, out hit, maxDist, Mask, QueryTriggerInteraction.Ignore);
            // The own plane is not a collidable, so the FIRST non-own hit is
            // what the JS's single-object sweep would have found.
            var hits = RaycastAllSorted(origin, dir, maxDist);
            if (hits.Length > 0) { hit = hits[0]; return true; }
            hit = default;
            return false;
        }

        public bool IsTerrain(Collider c) => c != null && c.GetComponentInParent<CombatTerrainTag>() != null;

        public bool IsPlaneCollider(Collider c) => c != null && c.GetComponentInParent<PlaneDamageRecord>() != null;

        // ---- real: terrain ----
        public float? HeightAt(float x, float z) => Terrain != null ? Terrain.HeightAt(x, z) : (float?)null;

        public float? BoreFloorAt(float x, float z, float refY) => null; // no tunnel-bore system in City/ yet (documented gap, matches IGroundProbeWorld's own note)

        public System.Collections.Generic.IReadOnlyList<Vector3> DownwardHits(float x, float z, float originY)
        {
            var hits = Physics.RaycastAll(new Vector3(x, originY, z), Vector3.down, 20000f, Mask, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance)); // nearest-first == highest point first, same order three.js's intersectObjects already returns
            hits = DropOwn(hits); // game.js:834-836 — see OwnPlane: the plane must not find its own shell under its wheels
            var pts = new Vector3[hits.Length];
            for (int i = 0; i < hits.Length; i++) pts[i] = hits[i].point;
            return pts;
        }

        float? IPlanePhysicsWorld.GroundAt(float x, float z, float refY, float maxDist) => HeightAt(x, z);

        public void SetCollidersEnabled(Transform wrapper, bool enabled_)
        {
            if (wrapper == null) return;
            foreach (var c in wrapper.GetComponentsInChildren<Collider>(true)) c.enabled = enabled_;
        }

        /// <summary>planephys.js's narrower `g.world.damageHit` forward. A
        /// best-effort synthetic RaycastHit (point/normal only, no collider —
        /// RaycastHit's point/normal setters are public, its collider is not)
        /// since IPlanePhysicsWorld only hands this a point+normal, not a real
        /// hit. Honest limitation: whatever IDamageWorld.DamageHit does with a
        /// collider-less hit is that implementation's own call, not verified
        /// here (no editor access this session — see port brief).</summary>
        public bool DamageBuilding(Vector3 point, Vector3 normal, float roundsEquivalent, out bool destroyed)
        {
            destroyed = false;
            if (Damage == null) return false;
            var hit = new RaycastHit { point = point, normal = normal };
            var res = Damage.DamageHit(hit, roundsEquivalent);
            if (!res.HasValue) return false;
            destroyed = res.Value.Destroyed;
            return res.Value.Changed;
        }

        // ---- shared FX surface (IPlaneWallWorld + IPlanePhysicsWorld both need these) ----
        public void SpawnEmber(Vector3 at) => SpawnEmberHook?.Invoke(at);
        public void SpawnSmoke(Vector3 at, float size, float dark, float dur, float vy, float spread) =>
            SpawnSmokeHook?.Invoke(at, size, dark, dur, vy, spread);
        public void DustBurst(Vector3 at, int count, float radius) => DustBurstHook?.Invoke(at, count, radius);
        public void BurstGlass(float[] blown, float power, float count) => BurstGlassHook?.Invoke(blown, power, count);
        public void ShowBuildingToast(DamageResult res) => ShowBuildingToastHook?.Invoke(res);
        public float SoundVolume() => SoundVolumeHook != null ? SoundVolumeHook() : 0.7f;
        public float CameraDistanceTo(Vector3 point) =>
            Camera.main != null ? Vector3.Distance(Camera.main.transform.position, point) : 50f;

        public void HitConcreteSound(float dist, float vol) => FxAudio.HitConcrete(dist, vol); // real — no further construction needed
        public void CollapseRumbleSound(float dist, float vol, bool destroyed) => FxAudio.CollapseRumble(dist, vol, destroyed);

        public void Explosion(Vector3 at, float radius) => ExplosionHook?.Invoke(at, radius);
        public void Plume(Vector3 at, float radius, float strength) => PlumeHook?.Invoke(at, radius, strength);
        public void Wave(Vector3 at, float radius) => WaveHook?.Invoke(at, radius);
        public void StampScorch(Vector3 at, float radius) => StampScorchHook?.Invoke(at, radius);
        public void OnShake(float power, float x, float z) => OnShakeHook?.Invoke(power, x, z);

        // MetalDents.IWorld — cinema.js has no ported counterpart anywhere in
        // this project (GameLoop.cs's own class header says so), so dents are
        // never suppressed for it.
        public bool CinemaActive() => false;
    }
}

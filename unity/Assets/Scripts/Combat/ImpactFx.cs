using System;
using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Fx;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// Port of src/impactfx.js's surfaceFit — a decal must never mark something
    /// nobody can see. The four corners of the quad are probed against the one
    /// collider that was actually hit; the quad is shrunk until every corner
    /// lands on it, so a round into a 2 cm cable leaves a 2 cm scratch instead
    /// of a palm-sized hole hanging in mid-air over a thin strut.
    ///
    /// Unity has an exact primitive for "raycast against exactly one object,
    /// ignoring everything else in the scene": Collider.Raycast. That is a
    /// closer match to THREE.Raycaster.intersectObject(obj) than
    /// Physics.Raycast would be (which could hit a DIFFERENT, occluding
    /// collider and falsely read as "off the edge").
    /// </summary>
    public static class DecalGeometry
    {
        const int FitTries = 3;
        static readonly (float sx, float sy)[] FitCorners =
        {
            (0.5f, 0.5f), (-0.5f, 0.5f), (0.5f, -0.5f), (-0.5f, -0.5f),
        };

        /// <summary>Returns the shrink factor (1 = full size). `obj` may be null (nothing
        /// to probe against — e.g. a hit with no backing collider at all).</summary>
        public static float SurfaceFit(Collider obj, Vector3 point, Vector3 x, Vector3 y, Vector3 z, float w, float h)
        {
            if (obj == null) return 1f;
            Vector3 dir = -z;
            float k = 1f;
            for (int t = 0; t < FitTries; t++)
            {
                int off = 0;
                foreach (var (sx, sy) in FitCorners)
                {
                    Vector3 from = point + x * (sx * w * k) + y * (sy * h * k) + z * 0.3f;
                    if (!obj.Raycast(new Ray(from, dir), out _, 0.62f)) off++;
                }
                if (off == 0) break;
                k *= 0.55f;
            }
            return k;
        }
    }

    /// <summary>One decal's pose+size, exactly as src/impactfx.js's DecalLayer.stamp()
    /// composes it. The pit/spall/exit/scuff/star TEXTURES themselves are now built
    /// by Combat/ImpactDecalTextures.cs (src/impactfx.js:168-418); wiring one onto an
    /// InstancedMesh / DecalProjector / Shader Graph material per layer remains a
    /// Unity VFX concern left to whoever wires the renderer — this struct is the
    /// pose/size data that drives it.</summary>
    public struct DecalStamp
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale; // (w, h, 1) — non-uniform, so a hole can be an ellipse
        public Color32? Tint;
        public bool Live;
    }

    public sealed class DecalLayer
    {
        public readonly DecalStamp[] Slots;
        public readonly float Lift;
        int _cursor;
        public int Count { get; private set; }

        public DecalLayer(int max, float lift = 0.006f)
        {
            Slots = new DecalStamp[max];
            Lift = lift;
        }

        /// <summary>`along` is the in-plane stretch direction (the flight path flattened
        /// onto the surface) — omit for a square-on hit, which spins randomly instead
        /// (src/impactfx.js:117). `fit` is the collider actually hit, for SurfaceFit.</summary>
        public DecalStamp Stamp(Vector3 point, Vector3 normal, float w, float h,
            Vector3? along = null, Color32? tint = null, float slide = 0f, Collider fit = null,
            Func<float> rng = null)
        {
            rng ??= () => UnityEngine.Random.value;
            Vector3 z = normal.normalized;
            Vector3 x;
            if (along.HasValue && along.Value.sqrMagnitude > 1e-8f)
            {
                Vector3 a = along.Value;
                x = a - z * Vector3.Dot(a, z); // project the flight path into the surface plane
                if (x.sqrMagnitude < 1e-8f) x = Vector3.right;
                x.Normalize();
            }
            else
            {
                float ang = rng() * Mathf.PI * 2f;
                x = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f);
                x -= z * Vector3.Dot(x, z);
                if (x.sqrMagnitude < 1e-8f) x = new Vector3(z.y, -z.x, 0f);
                x.Normalize();
            }
            // HANDEDNESS: the JS builds y = cross(z, x) to feed THREE.Matrix4.makeBasis
            // (x, y, z) as a proper right-handed frame. Unity's Cross is the same
            // component formula under a left-handed convention, so — per GeoFrame.cs's
            // rule — the operands are SWAPPED to keep (x, y, z) a proper, unmirrored
            // frame here too: y = Cross(x, z), not Cross(z, x).
            Vector3 y = Vector3.Cross(x, z).normalized;

            float k = fit != null ? DecalGeometry.SurfaceFit(fit, point, x, y, z, w, h) : 1f;
            w *= k; h *= k;

            Vector3 pos = point + z * Lift;
            if (slide != 0f) pos += x * (slide * w);
            // model +Z -> surface normal, +Y -> the in-plane "up" (x,y,z already orthonormal
            // and Unity-handed-consistent, so LookRotation reconstructs the same frame).
            Quaternion rot = Quaternion.LookRotation(z, y);

            var stamp = new DecalStamp { Position = pos, Rotation = rot, Scale = new Vector3(w, h, 1f), Tint = tint, Live = true };
            int i = _cursor;
            _cursor = (_cursor + 1) % Slots.Length;
            Slots[i] = stamp;
            Count = Mathf.Max(Count, i + 1);
            return stamp;
        }
    }

    /// <summary>Port of src/impactfx.js's ParticleCloud — the simulation only (position/
    /// velocity/drag/gravity integration and the emit() cone math). Rendering (one
    /// shared point-sprite shader per layer in the JS) is Unity VFX Graph territory.</summary>
    public sealed class ParticleCloud
    {
        public readonly int Max;
        public readonly Vector3[] Pos;
        public readonly Vector3[] Vel;
        public readonly Color32[] Tint;
        public readonly float[] Life;   // >= 1 means free/dead
        public readonly float[] Size;
        public readonly float[] Grav;
        public readonly float[] Drag;
        public readonly float[] Span;
        int _cursor;

        public ParticleCloud(int max)
        {
            Max = max;
            Pos = new Vector3[max];
            Vel = new Vector3[max];
            Tint = new Color32[max];
            Life = new float[max];
            for (int i = 0; i < max; i++) Life[i] = 1f;
            Size = new float[max];
            Grav = new float[max];
            Drag = new float[max];
            Span = new float[max];
            for (int i = 0; i < max; i++) Span[i] = 1f;
        }

        // src/impactfx.js:489 emit
        public void Emit(Vector3 p, int n, Color32 tint, float speed = 3f, float spread = 0.1f,
            float up = 1f, float size = 0.05f, float life = 0.8f, float grav = 6f, float drag = 2.5f,
            Vector3? dir = null, float cone = 1f, Func<float> rng = null)
        {
            rng ??= () => UnityEngine.Random.value;
            for (int k = 0; k < n; k++)
            {
                int i = _cursor;
                _cursor = (_cursor + 1) % Max;
                Pos[i] = p + new Vector3((rng() - 0.5f) * spread, (rng() - 0.5f) * spread, (rng() - 0.5f) * spread);
                float vx = (rng() - 0.5f) * 2f, vy = (rng() - 0.5f) * 2f, vz = (rng() - 0.5f) * 2f;
                if (dir.HasValue)
                {
                    vx = dir.Value.x * cone + vx * 0.8f;
                    vy = dir.Value.y * cone + vy * 0.8f;
                    vz = dir.Value.z * cone + vz * 0.8f;
                }
                float s = speed * (0.35f + rng() * 0.9f);
                float l = Mathf.Sqrt(vx * vx + vy * vy + vz * vz);
                if (l < 1e-9f) l = 1f;
                Vel[i] = new Vector3((vx / l) * s, (vy / l) * s + up * (0.4f + rng() * 0.8f), (vz / l) * s);
                Tint[i] = tint;
                Size[i] = size * (0.5f + rng());
                Grav[i] = grav;
                Drag[i] = drag;
                Span[i] = life * (0.7f + rng() * 0.6f);
                Life[i] = 0.001f;
            }
        }

        // src/impactfx.js:525 update
        public void Update(float dt)
        {
            for (int i = 0; i < Max; i++)
            {
                if (Life[i] >= 1f) continue;
                Life[i] = Mathf.Min(1f, Life[i] + dt / Span[i]);
                float d = Mathf.Exp(-Drag[i] * dt);
                Vector3 v = Vel[i];
                v.x *= d;
                v.y = v.y * d - Grav[i] * dt;
                v.z *= d;
                Vel[i] = v;
                Pos[i] += v * dt;
            }
        }
    }

    /// <summary>Port of src/impactfx.js's Casings — a spent case ejected from the port,
    /// spinning, bouncing off the ground, settling, then recycled. Pure physics, no
    /// shader; rendering (one InstancedMesh per cylinder in the JS) is left to Unity.</summary>
    public sealed class Casings
    {
        public sealed class Case
        {
            public float? Gy;      // last-sampled ground height under the case
            public float Px, Pz;   // where it was last sampled, to re-probe only after moving
            public Vector3 P;
            public Vector3 V;
            public Vector3 RotEuler;
            public Vector3 Spin;
            public float LenM;
            public float Rest;
            public int Bounces;
            public float T;
        }

        public const int Max = 36;
        public const float RestTime = 18f; // s a case lies on the ground before recycling
        public readonly List<Case> List = new List<Case>();

        /// <summary>src/impactfx.js:599 game._groundAt(x, z, fromY, maxDrop) -> groundY or null.</summary>
        public Func<float, float, float, float, float?> GroundAt;
        public Func<float> Rng = () => UnityEngine.Random.value;
        /// <summary>Fires once per case on its first bounce — audiofx.js's casingDrop,
        /// out of Combat's scope; wire distance/volume on the far side.</summary>
        public event Action FirstBounce;

        public void Eject(Vector3 pos, Vector3 vel, float lengthMm)
        {
            if (List.Count >= Max) List.RemoveAt(0);
            float? gy = GroundAt?.Invoke(pos.x, pos.z, pos.y + 1.6f, 1.8f);
            List.Add(new Case
            {
                Gy = gy, Px = pos.x, Pz = pos.z, P = pos, V = vel,
                RotEuler = new Vector3(Rng() * 6.28f, Rng() * 6.28f, Rng() * 6.28f),
                Spin = new Vector3((Rng() - 0.5f) * 34f, (Rng() - 0.5f) * 24f, (Rng() - 0.5f) * 34f),
                LenM = lengthMm / 1000f, Rest = 0f, Bounces = 0, T = 0f,
            });
        }

        public void Update(float dt)
        {
            for (int i = List.Count - 1; i >= 0; i--)
            {
                var c = List[i];
                c.T += dt;
                if (c.Rest > 0f)
                {
                    c.Rest -= dt;
                    if (c.Rest <= 0f) List.RemoveAt(i);
                    continue;
                }
                c.V.y -= 19f * dt;
                c.P += c.V * dt;
                if ((c.P.x - c.Px) * (c.P.x - c.Px) + (c.P.z - c.Pz) * (c.P.z - c.Pz) > 0.36f)
                {
                    c.Px = c.P.x; c.Pz = c.P.z;
                    float? gy = GroundAt?.Invoke(c.P.x, c.P.z, c.P.y + 1.6f, 1.8f);
                    if (gy.HasValue) c.Gy = gy;
                }
                c.RotEuler += c.Spin * dt;
                if (c.Gy.HasValue && c.P.y <= c.Gy.Value + 0.006f)
                {
                    c.P.y = c.Gy.Value + 0.006f;
                    if (c.Bounces == 0) FirstBounce?.Invoke();
                    c.Bounces++;
                    if (c.Bounces > 2 || c.V.magnitude < 0.7f)
                    {
                        c.RotEuler = new Vector3(Mathf.PI / 2f, Rng() * 6.28f, 0f);
                        c.Spin = Vector3.zero;
                        c.V = Vector3.zero;
                        c.Rest = RestTime;
                    }
                    else
                    {
                        c.V = new Vector3(c.V.x * 0.6f, Mathf.Abs(c.V.y) * 0.34f, c.V.z * 0.6f);
                        c.Spin *= 0.5f;
                    }
                }
                else if (c.T > 6f)
                {
                    List.RemoveAt(i); // thrown off a roof — gone
                }
            }
        }
    }

    /// <summary>
    /// Port of src/impactfx.js's ImpactFX — the one place a resolved ballistic
    /// event (from Ballistics.cs's BulletSim, or a hitscan round like the belly
    /// gun's) becomes damage, marks, debris and (eventually, elsewhere) a noise.
    /// Decals are LAYERED exactly as the JS composes them: a soft tinted spall
    /// halo first, THE HOLE on top of it at the bullet's own diameter (stretched
    /// into an ellipse on an oblique hit), a ragged exit hole on anything the
    /// round went through, a long ploughed scuff for a ricochet, a cracked star
    /// for glass. Nothing here is a 50 cm decal — a 9 mm hole really is drawn at
    /// 9 mm; that fidelity is the entire point of this file.
    /// </summary>
    public sealed class ImpactFx
    {
        public readonly DecalLayer Spall = new DecalLayer(1400, 0.005f);
        public readonly DecalLayer Pits = new DecalLayer(1400, 0.008f);
        public readonly DecalLayer Exits = new DecalLayer(500, 0.006f);
        public readonly DecalLayer Scuffs = new DecalLayer(400, 0.006f);
        public readonly DecalLayer Stars = new DecalLayer(500, 0.006f);
        public readonly ParticleCloud Debris = new ParticleCloud(1400);
        public readonly ParticleCloud Sparks = new ParticleCloud(420);
        public readonly Casings Casings = new Casings();

        // The renderer for Spall/Pits/Exits/Scuffs/Stars above (Fx/ImpactDecalFx.cs)
        // — owned here the same way Combat/Rockets.cs owns its own
        // Fx.RocketTrailFx and Combat/Blast.cs its own Fx.BlastRenderer, so this
        // class is wire-ready with no Core/* edit required.
        readonly ImpactDecalFx _decalFx = new ImpactDecalFx();
        // The renderer for Debris/Sparks above (Fx/ImpactParticleFx.cs) — same
        // self-contained-field pattern as _decalFx. See that class's header for
        // why it draws through Shaders/ImpactSprite.shader rather than the
        // orphaned, property-name-mismatched Shaders/ImpactParticle.shader.
        readonly ImpactParticleFx _particleFx = new ImpactParticleFx();

        public IDamageWorld World;
        public IPlaneDamageHooks PlaneDamage = new NullPlaneDamageHooks();
        public Func<float> Rng = () => UnityEngine.Random.value;

        // Hooks for systems explicitly out of this port's scope — kept as events
        // so the control flow (who gets called, with what, and when) is complete
        // and wire-ready even though the receiving systems are not written yet.
        public event Action<Collider, Vector3, Vector3, Cartridge> OnPaperHit;      // src/papertargets.js
        public event Action<Collider, Vector3, float> OnLaneHit;                   // src/rangelane.js
        public event Action<float[], float, float> OnGlassBurst;                   // src/glass.js burstList(flat5, power, max)
        public event Action<DamageResult> OnHitToast;                              // UI, shared with Guns.cs

        readonly Dictionary<(int, int, int), int> _paneHits = new Dictionary<(int, int, int), int>();

        static bool IsPlane(RaycastHit hit)
            => hit.collider != null && hit.collider.GetComponent<PlaneSurfaceTag>() != null;

        // Combat/RangeBootstrap.cs item 3 — same shape as IsPlane/PlaneSurfaceTag
        // directly above: a tag component the range's own adoption step
        // (RangeBootstrap.AdoptBaked/Build) stamps onto the sheet's collider, so
        // this file never needs to guess from a material/name heuristic the way
        // Ballistics.cs's SurfaceClassifier does for everything else. `surface ==
        // "paper"` (below) never actually fires on its own — nothing in
        // SurfaceClassifier.Classify ever returns "paper" — so this tag check is
        // what makes src/papertargets.js's "PAPER OWNS ITS OWN HOLE" branch (and
        // its MarkHit equivalent, added below) reachable at all.
        static bool IsPaper(RaycastHit hit)
            => hit.collider != null && hit.collider.GetComponent<PaperCardTag>() != null;

        /// <summary>Routes a round into whichever adopted Range owns this
        /// collider (RangeBootstrap.TryRouteHitAll — scores the paper AND kicks
        /// the carrier's pendulum via Combat/RangeLane.cs), then fires the same
        /// OnPaperHit/OnLaneHit events the (now unreachable without this call)
        /// `surface == "paper"` branches used to, for any other listener
        /// (UI/audio) that might still want them, plus a small paper-dust puff.
        /// Shared by Resolve/MarkHit below so the two don't duplicate the
        /// momentum-from-cartridge math.</summary>
        void RoutePaperHit(RaycastHit hit, Vector3 point, Vector3 dir, Cartridge cart)
        {
            float cal = (cart?.BulletMm ?? 9f) / 1000f;
            float power = cart != null ? (cart.MassKg * cart.V0) / 2f : 1f;
            RangeBootstrap.TryRouteHitAll(hit, dir, cart?.BulletMm ?? 9f, power);
            OnPaperHit?.Invoke(hit.collider, point, dir, cart);
            OnLaneHit?.Invoke(hit.collider, point, power);
            Debris.Emit(point, 4, new Color32(0xe8, 0xe4, 0xda, 255), dir: dir, cone: 1.1f,
                speed: 1.8f, size: cal * 1.6f, up: 0.4f, life: 0.5f, grav: 4f, rng: Rng);
        }

        /// <summary>src/impactfx.js:707 resolve — the entry point for a round that went
        /// through BulletSim's full projectile sim (Ballistics.cs).</summary>
        public DamageResult? Resolve(ImpactInfo info)
        {
            // PAPER OWNS ITS OWN HOLE (src/papertargets.js) — checked first, like
            // IsPlane below, so a paper hit never reaches DamageHit/surface marks.
            if (IsPaper(info.Hit)) { RoutePaperHit(info.Hit, info.Point, info.Dir, info.Cart); return null; }

            var S = info.S;
            string surface = info.Surface;
            Vector3 point = info.Point, normal = info.Normal, dir = info.Dir;
            float cal = info.Cart.BulletMm / 1000f;

            DamageResult? res = World?.DamageHit(info.Hit, info.Damage);

            // AN AEROPLANE ANSWERS FOR ITSELF (src/planedamage.js, out of scope).
            if (IsPlane(info.Hit) && PlaneDamage.Mark(info)) return null;

            if (surface == "glass") Glass(info, res, cal);
            else if (S.Liquid) Water(info);
            else if (info.Ricochet) Ricochet(info, cal);
            else Strike(info, cal);

            // the far side of anything it went through gets its own, rougher hole
            if (info.Through && info.Exit.HasValue && !S.NoHole && surface != "glass")
            {
                Exits.Stamp(info.Exit.Value, -normal, cal * 1.7f, cal * 1.7f,
                    tint: S.Dust.Tint, fit: info.Hit.collider, rng: Rng);
                Debris.Emit(info.Exit.Value, Mathf.CeilToInt(S.Dust.N * 0.6f), S.Dust.Tint,
                    dir: dir, cone: 1.4f, speed: S.Dust.Speed * 1.2f, size: cal * 1.4f, up: 0.3f,
                    life: 0.5f, grav: 9f, rng: Rng);
            }

            if (res != null)
            {
                OnHitToast?.Invoke(res.Value);
                if (res.Value.Changed && !res.Value.Destroyed)
                {
                    Debris.Emit(new Vector3(res.Value.Cx, res.Value.TopNow, res.Value.Cz), 16,
                        new Color32(0x9a, 0x93, 0x88, 255), speed: 4f,
                        spread: Mathf.Max(2f, res.Value.Span * 0.4f), size: 0.9f, life: 1.8f,
                        grav: 3f, up: 1.5f, rng: Rng);
                }
            }
            return res;
        }

        /// <summary>src/impactfx.js:777 markHit — the hitscan path (the belly gun):
        /// marks/debris/the right noise only, no sim, no damage (the caller already
        /// applied damage — see Guns.cs). Returns the classified surface id.</summary>
        public string MarkHit(RaycastHit hit, Vector3 point, Vector3 normal, Vector3 dir, Cartridge cart)
        {
            // Combat/RangeBootstrap.cs item 3 — this is specifically the path the
            // range's pistol takes (Guns.cs's own `Impacts?.MarkHit(...)`), so
            // without this branch EVERY shot fired at the range's own paper
            // sheets fell through to the generic Strike() decal below instead of
            // scoring — exactly the bug this port's brief named.
            if (IsPaper(hit)) { RoutePaperHit(hit, point, dir, cart); return "paper"; }
            if (IsPlane(hit))
            {
                string plane = PlaneDamage.Trace(hit, point, normal, dir, cart);
                if (plane != null) return plane;
            }
            string surface = SurfaceClassifier.Classify(hit, World as IBallisticsWorld, point);
            var S = Surfaces.Of(surface);
            float cal = cart.BulletMm / 1000f;
            float cosI = Mathf.Clamp(Mathf.Abs(Vector3.Dot(normal, dir)), 0.05f, 1f);
            var info = new ImpactInfo
            {
                Hit = hit, S = S, Surface = surface, Point = point, Normal = normal, Dir = dir,
                Cart = cart, Energy = cart.Energy, Graze = Mathf.Asin(cosI), Through = false, Exit = null,
                HoleW = cal * S.Hole, HoleL = (cal * S.Hole) / cosI,
            };
            if (S.Liquid) Water(info); else Strike(info, cal);
            return surface;
        }

        // src/impactfx.js:799 _strike — a round that bit and stayed, or punched through.
        void Strike(ImpactInfo info, float cal)
        {
            var S = info.S;
            Collider on = info.Hit.collider;
            if (!S.NoHole)
            {
                float halo = cal * S.Spall * (0.8f + Rng() * 0.45f);
                Spall.Stamp(info.Point, info.Normal, halo, halo * (0.8f + Rng() * 0.4f),
                    tint: S.Dust.Tint, fit: on, rng: Rng);
                Color32 holeTint = info.Surface == "metal" ? new Color32(0xc9, 0xcc, 0xd2, 255) : new Color32(255, 255, 255, 255);
                Pits.Stamp(info.Point, info.Normal, info.HoleL * 1.7f, info.HoleW * 1.7f,
                    along: info.Dir, tint: holeTint, fit: on, rng: Rng);
            }
            var d = S.Dust;
            Debris.Emit(info.Point, d.N, d.Tint, dir: info.Normal, cone: 0.9f, speed: d.Speed,
                size: cal * (S.Splinter ? 2.6f : 1.8f), up: d.Up != 0f ? d.Up : 0.6f,
                life: S.Splinter ? 0.7f : 0.85f, grav: S.Splinter ? 11f : 6f, spread: cal * 2f, rng: Rng);
            if (S.Sparks > 0f)
            {
                Sparks.Emit(info.Point, Mathf.RoundToInt(S.Sparks), new Color32(0xff, 0xcf, 0x7a, 255),
                    dir: info.Normal, cone: 0.7f, speed: 7f, size: 0.012f, life: 0.3f, grav: 16f,
                    drag: 1.2f, up: 0.4f, rng: Rng);
            }
        }

        // src/impactfx.js:829 _ricochet — skipped off: a long ploughed smear.
        void Ricochet(ImpactInfo info, float cal)
        {
            var S = info.S;
            float len = cal * (14f + 26f * (1f - info.Graze / Mathf.Max(0.05f, S.Graze)));
            Scuffs.Stamp(info.Point, info.Normal, len, cal * 2.6f, along: info.Dir, tint: S.Dust.Tint,
                slide: 0.29f, fit: info.Hit.collider, rng: Rng);
            Vector3 bounceDir = Vector3.Reflect(info.Dir, info.Normal);
            Debris.Emit(info.Point, Mathf.CeilToInt(S.Dust.N * 0.8f), S.Dust.Tint, dir: bounceDir,
                cone: 1.6f, speed: S.Dust.Speed * 1.5f, size: cal * 1.6f, up: 0.3f, life: 0.6f, grav: 7f, rng: Rng);
            if (S.Sparks > 0f || S.Sound == "concrete")
            {
                Sparks.Emit(info.Point, S.Sparks > 0f ? Mathf.RoundToInt(S.Sparks) : 5,
                    new Color32(0xff, 0xd0, 0x89, 255), dir: bounceDir, cone: 1.8f, speed: 9f,
                    size: 0.01f, life: 0.28f, grav: 16f, drag: 1f, rng: Rng);
            }
        }

        // src/impactfx.js:852 _glass — a pane: hole + crack star, then after three
        // hits on the same spot the whole sheet leaves the frame (src/glass.js, out
        // of scope for the actual departure, but the counting logic ports exactly).
        void Glass(ImpactInfo info, DamageResult? res, float cal)
        {
            float web = cal * (11f + Rng() * 5f);
            Stars.Stamp(info.Point, info.Normal, web, web * (0.85f + Rng() * 0.3f),
                tint: new Color32(255, 255, 255, 255), rng: Rng);
            Debris.Emit(info.Point, 7, new Color32(0xd8, 0xee, 0xf8, 255), dir: info.Dir, cone: 1.3f,
                speed: 3.4f, size: cal * 1.2f, up: 0.2f, life: 0.55f, grav: 15f, rng: Rng);
            Sparks.Emit(info.Point, 8, new Color32(0xbf, 0xe6, 0xff, 255), dir: info.Dir, cone: 1.2f,
                speed: 4.5f, size: 0.008f, life: 0.45f, grav: 17f, drag: 0.8f, rng: Rng);

            var key = (Mathf.RoundToInt(info.Point.x * 1.6f), Mathf.RoundToInt(info.Point.y * 1.6f), Mathf.RoundToInt(info.Point.z * 1.6f));
            int n = (_paneHits.TryGetValue(key, out int existing) ? existing : 0) + 1;
            _paneHits[key] = n;
            if (_paneHits.Count > 400) _paneHits.Clear();
            if (n >= 3)
            {
                var blown = World?.BlastWindows(info.Point.x, info.Point.y, info.Point.z, 0.9f);
                if (blown != null && blown.Length > 0) OnGlassBurst?.Invoke(blown, 0.7f, 2f);
                _paneHits.Remove(key);
            }
        }

        // src/impactfx.js:881 _water — a column of droplets, no hole, no bounce.
        void Water(ImpactInfo info)
        {
            var S = info.S;
            Debris.Emit(info.Point, S.Dust.N, S.Dust.Tint, dir: Vector3.up, cone: 1.4f, speed: 3.5f,
                size: 0.05f, up: S.Dust.Up != 0f ? S.Dust.Up : 4f, life: 0.75f, grav: 15f, drag: 0.6f,
                spread: 0.08f, rng: Rng);
            Sparks.Emit(info.Point, 6, new Color32(0xdf, 0xf2, 0xff, 255), dir: Vector3.up, cone: 1.6f,
                speed: 4f, size: 0.02f, life: 0.5f, grav: 16f, drag: 0.6f, rng: Rng);
        }

        public void Update(float dt)
        {
            Debris.Update(dt);
            Sparks.Update(dt);
            Casings.Update(dt);
            // Decals carry no dt-driven state of their own (src/impactfx.js's
            // DecalLayer never ages a mark out) — this is a straight resubmit of
            // whatever Spall/Pits/Exits/Scuffs/Stars hold right now.
            _decalFx.Render(this);
            // Debris/Sparks already stepped above — render them the same frame.
            _particleFx.Render(this);
        }

        public void Dispose()
        {
            _decalFx.Dispose();
            _particleFx.Dispose();
        }
    }
}

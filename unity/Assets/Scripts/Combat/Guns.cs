using System;
using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Core;
using TbilisiFly.Fx;

namespace TbilisiFly.Combat
{

    /// <summary>
    /// Port of src/guns.js's MachineGun — the belly-mounted gun (Space to fire).
    /// Hitscan with a VISUAL tracer: the raycast happens at the muzzle, the
    /// damage lands only once the tracer has visually travelled to the wall
    /// (_fire / UpdateTracers / Impact below, mirroring the JS 1:1).
    ///
    /// The gun's OWN local VFX/audio (src/guns.js:37-377, kept separate from
    /// Combat/ImpactFx.cs's general pipeline by the JS itself — see that
    /// file's header comment) now lives in Combat/GunFx.cs: GunFx.Shoot (the
    /// dry muzzle report), ShardBurst/ImpactDust (the two particle sims) and
    /// Combat/ImpactFx.cs's DecalLayer, reused here for Cracks exactly as
    /// src/guns.js:389 reuses its own class for holes/cracks.
    ///
    /// STILL NOT PORTED HERE (shader/VFX/UI geometry, not math — see
    /// docs/UNITY_PORT.md's "the math ports; the shaders do not"): the
    /// muzzle-flash sprite + tracer InstancedMesh (already covered by
    /// Shaders/MuzzleFlash.shader + Scripts/Fx/MuzzleFlashFx.cs). The painted
    /// canvas hole/crack textures (src/guns.js:90-171 makeHoleTexture/
    /// makeCrackTexture) are built by Combat/GunDecalTextures.cs, same
    /// split as Combat/ImpactDecalTextures.cs's 5 textures for
    /// Combat/ImpactFx.cs, and are now DRAWN: Fx/GunDecalFx.cs owns the two
    /// Materials and resubmits Cracks/Holes every frame, owned by this class
    /// (_decalFx below) exactly as _muzzleFx/_particleFx are. The modelled gun-pod geometry
    /// (src/guns.js:393-417) IS now ported — see GunPod below; it was
    /// previously filed as asset-blocked, which a direct grep for a
    /// GLTFLoader/loadAsync call in src/guns.js refuted (zero hits — it is
    /// bare primitive geometry, same as Fx/RocketBodyFx.cs's rocket body).
    /// Also still not ported: the crosshair/hit-toast DOM elements (the pure
    /// logic behind them — src/guns.js:624-656 — is ported as GunToast/
    /// GunCrosshair in Combat/GunFx.cs; only the DOM creation itself is UI's
    /// job). The glass-departure hook (src/guns.js:598 g.glass.burstList) WAS
    /// a real stub here (IDamageWorld.BlastWindows used to return an opaque
    /// object[] while Combat/BreakingGlass.cs's BurstList wants a flat
    /// float[]) — Combat/CombatWorldHooks.cs's IDamageWorld.BlastWindows was
    /// changed to `float[]` since (see that file's own doc comment), so this
    /// file now raises OnGlassBurst exactly like Combat/ImpactFx.cs and
    /// Combat/Rockets.cs do. ONE WIRING LINE STILL NEEDED, in Core/ (off
    /// limits to this agent): Core/CombatWorldAdapter.cs:900-901 wires
    /// impactFx.OnGlassBurst/rockets.OnGlassBurst to glass.BurstList — it
    /// needs a third line, `machineGun.OnGlassBurst += (flat, power, max) =&gt;
    /// glass.BurstList(flat, power, max &gt; 0f ? Mathf.RoundToInt(max) : 46);`,
    /// or a belly-gun hit still finds its blown panes and drops them on the
    /// floor. OnHitToast below is the hook UI should subscribe to.
    /// </summary>
    public sealed class MachineGun
    {
        // src/guns.js:13-30
        public const float Rate = 12f;         // rounds per second
        public const float Range = 1100f;      // m
        public const float BulletSpeed = 850f; // m/s — the visible tracer's travel speed
        public const float Damage = 14f;       // hp/round vs a building
        // Named PlaneDamageAmount (not PlaneDamage) to avoid colliding with the
        // PlaneDamage hooks field below — same name convention collision as
        // ImpactFx.cs's IPlaneDamageHooks field, just inside one class here.
        public const float PlaneDamageAmount = 48f;  // hp/round vs an airframe — the real AA weapon
        public const float Spread = 0.003f;    // rad — the gun's cone
        public const int MaxTracers = 48;
        public static readonly Cartridge Cart = Cartridges.ById["12.7x108"]; // src/guns.js:32

        public LayerMask TargetMask = ~0;
        public IDamageWorld World;
        public IPlaneDamageHooks PlaneDamage = new NullPlaneDamageHooks();
        /// <summary>Wired once Combat/ImpactFx.cs's ImpactFx is instantiated — the shared
        /// marks/debris/damage pipeline src/guns.js:609 calls for every non-pane hit.</summary>
        public ImpactFx Impacts;

        // src/guns.js:386-389 — the gun's own local effects (simulation only;
        // rendering is Shaders/ImpactParticle.shader's job, same precedent as
        // ImpactFx.cs's ParticleCloud/DecalLayer).
        public readonly ShardBurst Shards = new ShardBurst();
        public readonly ImpactDust Dust = new ImpactDust();
        // src/guns.js:388-389 — DecalLayer construction args (max slots, base size).
        internal const int CracksMax = 400;
        internal const float CracksBaseSize = 1.25f;
        internal const int HolesMax = 1600;
        internal const float HolesBaseSize = 0.52f;
        internal const float DecalLift = 0.06f;   // src/guns.js:72 addScaledVector(normal, 0.06)
        internal const float StampScaleMin = 0.8f; // src/guns.js:69 scale*(0.8+random()*0.5)
        internal const float StampScaleRange = 0.5f;
        // src/guns.js:600 g.glass.burstList(blown, 0.8, 3) — the belly gun's own
        // blown-pane burst power / max shards-this-tick (named per project rule:
        // "name magic numbers internal const").
        internal const float GlassBurstPower = 0.8f;
        internal const float GlassBurstMax = 3f;

        /// <summary>src/guns.js:389 cracks — reuses Combat/ImpactFx.cs's DecalLayer
        /// exactly as the JS's own DecalLayer class is reused for both holes and
        /// cracks (same stamp() shape, just a different texture/size).</summary>
        public readonly DecalLayer Cracks = new DecalLayer(CracksMax, DecalLift);
        /// <summary>src/guns.js:388 holes — constructed in the JS too, but never
        /// actually stamped (every generic mark goes through Impacts.MarkHit
        /// instead — see this.holes' absence from every call site in
        /// src/guns.js's _impact). Kept here only for field-for-field parity;
        /// intentionally dead, exactly as upstream.</summary>
        public readonly DecalLayer Holes = new DecalLayer(HolesMax, DecalLift);

        /// <summary>Probe handle for the owned decal renderer (DrawnCracks /
        /// DrawnHoles) — the gate-is-open assertion the SKILL's "a probe proves a
        /// CALCULATION, not that the calculation is REACHED" rule asks for.</summary>
        internal GunDecalFx DecalFx => _decalFx;
        public Func<float> Rng = () => UnityEngine.Random.value;

        public event Action<DamageResult> OnHitToast; // src/guns.js:_showToast — UI subscribes
        /// <summary>src/guns.js:600 g.glass.burstList(blown, 0.8, 3) — every pane the
        /// belt gun's blast radius knocked out, for the consumer to hand to
        /// Combat/BreakingGlass.cs's BurstList (flat, 5-wide, same convention as
        /// Combat/ImpactFx.cs's and Combat/Rockets.cs's own OnGlassBurst).</summary>
        public event Action<float[], float, float> OnGlassBurst;

        public sealed class Tracer
        {
            public Vector3 Pos;
            public Vector3 Dir;
            public float Dist;
            public float MaxDist;
            public RaycastHit? Hit;
        }

        public readonly List<Tracer> Live = new List<Tracer>();

        /// <summary>Rounds this gun has sent since it was built. Pure
        /// instrumentation: `Live` is NOT a usable "did it fire?" signal,
        /// because UpdateTracers runs in the same Update() as Fire() and
        /// retires any tracer whose target is nearer than one frame of travel
        /// (850 m/s x 6 ms = 5.1 m) — nose-in to a building, every round was
        /// created and gone before the caller could look. The first play-mode
        /// run read `Live.Count == 0` across 40 frames of held Space and
        /// wrongly concluded the belly gun never fired.</summary>
        public int ShotCount { get; private set; }

        float _cool;

        /// <summary>Owned renderer for the muzzle flash sprite + tracer rods
        /// (src/guns.js:420-456,501-550) — constructed once, fed from Live every
        /// Update() below. WIRING: MuzzleFlashFx previously had zero callers
        /// project-wide (grep confirmed); it is now self-sufficient — MachineGun
        /// is already ticked every frame via Core/CombatWorldAdapter.cs's own
        /// `GunTickable(machineGun, game)`, so no Core/* edit is needed to draw.</summary>
        readonly MuzzleFlashFx _muzzleFx = new MuzzleFlashFx();

        /// <summary>Owned renderer for Shards/Dust above (Fx/ImpactParticleFx.cs) —
        /// same self-contained-field pattern as _muzzleFx. Closes guns.js's own
        /// still-unwired ShardBurst/ImpactDust rendering gap (see that class's
        /// header for the Shaders/ImpactParticle.shader property-name mismatch
        /// this resolves by using Shaders/ImpactSprite.shader instead).</summary>
        readonly ImpactParticleFx _particleFx = new ImpactParticleFx();

        /// <summary>Owned renderer for Cracks/Holes below (Fx/GunDecalFx.cs) —
        /// same self-contained-field pattern as _muzzleFx/_particleFx. Closes
        /// src/guns.js:388-389's own gap: Combat/GunDecalTextures.cs painted both
        /// textures and this class stamped the poses, and nothing drew them, so
        /// the crack star a round puts through a painted-on window was invisible.</summary>
        readonly GunDecalFx _decalFx = new GunDecalFx();

        /// <summary>Fire-rate gate + the per-frame tracer sweep. `firing` is whatever the
        /// caller's gating decided (started/not crashed/not in placement/on foot/skillbar
        /// slot — all Core/UI territory, src/guns.js:480); this method only knows the gun.</summary>
        public void Update(float dt, bool firing, Vector3 muzzleWorld, Vector3 forward, Func<float> rng = null,
            float vol = 0.7f, Vector3? listenerPos = null)
        {
            rng ??= () => UnityEngine.Random.value;
            _cool -= dt;
            if (firing && _cool <= 0f)
            {
                _cool = 1f / Rate;
                Fire(muzzleWorld, forward, rng, vol);
            }
            UpdateTracers(dt, listenerPos, vol);
            // src/guns.js:492-493
            Dust.Update(dt);
            Shards.Update(dt);
            _muzzleFx.Rng = rng;
            _muzzleFx.Update(dt, Live); // src/guns.js:420-456,525-550 flash + tracer draw
            _particleFx.Render(Shards, Dust); // Dust/Shards already stepped above
            _decalFx.Render(this); // src/guns.js:388-389 holes/cracks — resubmit, no sim step
        }

        /// <summary>Releases the owned flash/tracer/particle Materials and mesh.
        /// Call when this gun itself is torn down (matches Fx.MuzzleFlashFx's and
        /// Fx.ImpactParticleFx's own Dispose).</summary>
        public void Dispose()
        {
            _muzzleFx.Dispose();
            _particleFx.Dispose();
            _decalFx.Dispose();
        }

        // src/guns.js:501 _fire
        void Fire(Vector3 muzzle, Vector3 forward, Func<float> rng, float vol)
        {
            Vector3 dir = forward;
            dir.x += (rng() - 0.5f) * Spread * 2f;
            dir.y += (rng() - 0.5f) * Spread * 2f;
            dir.z += (rng() - 0.5f) * Spread * 2f;
            dir.Normalize();

            ShotCount++;
            bool hasHit = Physics.Raycast(new Ray(muzzle, dir), out RaycastHit hit, Range, TargetMask);
            if (Live.Count >= MaxTracers) Live.RemoveAt(0);
            Live.Add(new Tracer
            {
                Pos = muzzle, Dir = dir, Dist = 0f,
                MaxDist = hasHit ? hit.distance : Range,
                Hit = hasHit ? hit : (RaycastHit?)null,
            });
            // src/guns.js:529 this.sound.shoot(vol) — the muzzle flash sprite +
            // tracer trigger + skillbar pulse are Fx/UI's job (MuzzleFlashFx.cs
            // already covers the flash/tracer half; skillbar is UI, out of scope).
            GunFx.Shoot(vol, rng);
        }

        // src/guns.js:533 _updateTracers
        void UpdateTracers(float dt, Vector3? listenerPos, float vol)
        {
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                var tr = Live[i];
                tr.Dist += BulletSpeed * dt;
                if (tr.Dist >= tr.MaxDist)
                {
                    Impact(tr, listenerPos, vol);
                    Live.RemoveAt(i);
                }
            }
        }

        // src/guns.js:569 _impact. `listenerPos` is src/guns.js:590's
        // g.camera.position (how far the sound has to travel back to the
        // cockpit — audiofx turns this into delay/dark/wet, same as every
        // other impact sound in the project); null skips the two audio calls
        // rather than guessing a distance.
        public DamageResult? Impact(Tracer tr, Vector3? listenerPos = null, float vol = 0.7f)
        {
            if (tr.Hit == null) return null; // flew out to max range

            // AN AEROPLANE'S / A STAND-IN'S COLLIDER IS A CRATE. Re-cast onto the real
            // airframe panel, then (whatever that lands on) onto any invisible prop
            // stand-in holding real geometry (the crane fan guards, etc).
            RaycastHit? real = PlaneDamage.Refine(tr.Hit.Value, tr.Dir);
            RaycastHit? land = real == null ? (RaycastHit?)null : ProxyRefine.Refine(real.Value, tr.Dir);
            if (land == null) return null; // the round went through a gap in the crate
            RaycastHit hit = land.Value;

            bool isPlane = hit.collider != null && hit.collider.GetComponent<PlaneSurfaceTag>() != null;
            DamageResult? res = World?.DamageHit(hit, isPlane ? PlaneDamageAmount : Damage);
            Vector3 n = hit.normal;
            if (Vector3.Dot(n, tr.Dir) > 0f) n = -n;

            // window pane hit -> glass shatters (a real 3D pane is knocked out for good;
            // a painted-on window just takes the crack star — VFX, stubbed here).
            bool pane = res != null && res.Value.Rec != null && (World?.NearWindow(hit.collider, res.Value.Rec, hit.point) ?? false);
            float dist = listenerPos.HasValue ? Vector3.Distance(listenerPos.Value, hit.point) : 0f;
            if (pane)
            {
                var blown = World?.BlastWindows(hit.point.x, hit.point.y, hit.point.z, 1.9f);
                if (blown != null && blown.Length > 0)
                {
                    // src/guns.js:600 g.glass.burstList(blown, 0.8, 3) — GlassFX plays the
                    // shatter itself; the consumer (Core/CombatWorldAdapter.cs, same as its
                    // existing impactFx.OnGlassBurst/rockets.OnGlassBurst wiring) hands this
                    // straight to Combat/BreakingGlass.cs's BurstList.
                    OnGlassBurst?.Invoke(blown, GlassBurstPower, GlassBurstMax);
                }
                else
                {
                    // src/guns.js:602-604 — painted-on window: local shard burst + crack
                    // star decal + the gun's own (non-distance-modelled-at-source) glass
                    // break audio, which DOES still travel back to the cockpit as sound.
                    Shards.Burst(hit.point, n, Rng);
                    float m = StampScaleMin + Rng() * StampScaleRange;
                    Cracks.Stamp(hit.point, n, CracksBaseSize * m, CracksBaseSize * m, rng: Rng);
                    FxAudio.GlassBreak(dist, vol);
                }
            }
            else
            {
                // the shared marks/debris/damage pipeline — src/guns.js:609
                Impacts?.MarkHit(hit, hit.point, n, tr.Dir, Cart);
            }

            if (res == null) return null;
            OnHitToast?.Invoke(res.Value);
            if (res.Value.Changed)
            {
                // src/guns.js:613-620 — collapse dust/rumble is FireFX's job now;
                // guns only adds the stage puff while the building still stands.
                if (!res.Value.Destroyed)
                {
                    Dust.Burst(new Vector3(res.Value.Cx, res.Value.TopNow, res.Value.Cz), 24,
                        Mathf.Max(3f, res.Value.Span * 0.55f), Rng);
                }
                FxAudio.CollapseRumble(dist, vol, res.Value.Destroyed);
            }
            return res;
        }
    }

    /// <summary>
    /// Port of src/guns.js:393-417 — the belly-gun pod itself: the metal
    /// housing+barrel+brake+struts slung under the fuselage, and the muzzle
    /// point `MachineGun.Update`'s caller fires from. PREVIOUSLY filed as
    /// "parked for a missing asset" (see Combat/Rockets.cs's own corrected
    /// header for the identical mistake on the rocket body) — wrong: grepped
    /// src/guns.js for any GLTFLoader/loadAsync call, zero hits. It is bare
    /// THREE.CylinderGeometry/BoxGeometry primitives parented straight onto
    /// `game.plane`, so this is a direct geometry port via
    /// Combat/ProceduralPropMesh.cs, not an asset stand-in. ONE real
    /// GameObject per DPS plane (not per-frame/per-instance), so — unlike
    /// Fx/RocketBodyFx.cs's live rockets — a persistent GameObject hierarchy
    /// is the right call here, matching the JS's own literal
    /// `game.plane.add(pod)` scene-graph child.
    ///
    /// HANDEDNESS: every local offset below is `-(the JS's own value)` — see
    /// Combat/Rockets.cs's Fire() comment for the established rule (Three's
    /// -Z-forward is Unity's +Z-forward) this applies uniformly to.
    ///
    /// THE TAPER RUNS THE OTHER WAY — this header used to say "front/muzzle
    /// end narrower at 0.16, rear/mount end wider at 0.2", and the code agreed
    /// with the comment and BOTH disagreed with the JS. Work it through:
    /// `THREE.CylinderGeometry(radiusTop, radiusBottom, …)` puts radiusTop at
    /// +Y, and src/guns.js:396/400/404's `rotation.x = Math.PI / 2` maps +Y ->
    /// +Z (R_x(+90) sends (0,1,0) to (0,0,1)). Three's forward is -Z, so the
    /// TOP radius ends up AFT and the BOTTOM radius faces the MUZZLE. The JS
    /// housing is therefore `CylinderGeometry(0.16, 0.2, …)` = 0.16 aft, WIDE
    /// (0.2) AT THE MUZZLE, and the barrel `CylinderGeometry(0.05, 0.055, …)`
    /// = 0.05 aft, 0.055 at the muzzle. Odd-looking, measured, and the spec.
    /// tools/_probe_pass4extras.mjs <-> Editor/Probes/Pass4ExtrasProbe.cs print
    /// both sides as rMuzzle/rAft rather than top/bottom for exactly this
    /// reason, and that pair is what caught it.
    /// </summary>
    public static class GunPod
    {
        // src/guns.js:417 muzzleLocal, relative to the PLANE (not the pod) —
        // z=-3.65 (JS) -> +3.65 (Unity).
        public static readonly Vector3 MuzzleLocal = new Vector3(0f, -0.95f, 3.65f);
        public static Vector3 MuzzleWorld(Transform plane) => plane.TransformPoint(MuzzleLocal);

        // src/guns.js:409 pod.position, relative to the plane — z=-1.55 (JS) -> +1.55.
        static readonly Vector3 PodLocalPos = new Vector3(0f, -0.95f, 1.55f);

        const int Sides = 12;
        // :397 CylinderGeometry(0.16, 0.2, 1.5, 12) + rotation.x=+PI/2 — top
        // (0.16) lands AFT, bottom (0.2) at the MUZZLE (see the class header).
        const float BodyRAft = 0.16f, BodyRMuzzle = 0.2f, BodyLen = 1.5f;
        // :399 CylinderGeometry(0.05, 0.055, 1.35, 10) — the barrel is TAPERED
        // too; this was collapsed to a single 0.0525 (the mean) and is restored
        // here, aft/muzzle by the same rule as the housing.
        const float BarrelRAft = 0.05f, BarrelRMuzzle = 0.055f;
        const float BarrelLen = 1.35f, BarrelZ = 1.3f;                       // :399-401 (-1.3 -> +1.3)
        const float BrakeR = 0.09f, BrakeLen = 0.32f, BrakeZ = 1.92f;       // :403-405 (-1.92 -> +1.92)
        const float StrutW = 0.09f, StrutH = 0.42f, StrutD = 0.22f;         // :407
        static readonly float[] StrutZ = { 0.35f, -0.45f };                 // :406 (-0.35,0.45) -> (0.35,-0.45)
        const float StrutY = 0.32f;

        /// <summary>Builds the pod hierarchy as a child of `plane` and returns its
        /// root (named "gun_pod" — the Unity equivalent of the JS's `this.pod`
        /// THREE.Group). Call once per DPS airframe; Flight/Core owns deciding
        /// WHICH planes get one (planes.js's own `planeRole` gating, out of this
        /// port's scope).</summary>
        public static GameObject Build(Transform plane)
        {
            var metal = MakeMetal(new Color(0x22 / 255f, 0x25 / 255f, 0x2a / 255f), 0.75f, 0.4f); // :398
            var root = new GameObject("gun_pod");
            root.transform.SetParent(plane, false);
            root.transform.localPosition = PodLocalPos;

            // Cylinder(rNear, rFar, …) puts rNear on the -Z (aft) ring and rFar
            // on the +Z (muzzle) ring — see ProceduralPropMesh.Cylinder.
            AddPart(root.transform, ProceduralPropMesh.Combine(
                new[] { ProceduralPropMesh.Cylinder(BodyRAft, BodyRMuzzle, BodyLen, Sides) }, "pod_body"), metal, Vector3.zero);
            AddPart(root.transform, ProceduralPropMesh.Combine(
                new[] { ProceduralPropMesh.Cylinder(BarrelRAft, BarrelRMuzzle, BarrelLen, 10) }, "pod_barrel"), metal, new Vector3(0, 0, BarrelZ));
            AddPart(root.transform, ProceduralPropMesh.Combine(
                new[] { ProceduralPropMesh.Cylinder(BrakeR, BrakeR, BrakeLen, 10) }, "pod_brake"), metal, new Vector3(0, 0, BrakeZ));
            foreach (float z in StrutZ)
                AddPart(root.transform, ProceduralPropMesh.Combine(
                    new[] { ProceduralPropMesh.Box(Vector3.zero, StrutW, StrutH, StrutD) }, "pod_strut"),
                    metal, new Vector3(0, StrutY, z));
            return root;
        }

        static void AddPart(Transform parent, Mesh mesh, Material mat, Vector3 localPos)
        {
            var go = new GameObject(mesh.name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; // :411-413 castShadow = true
        }

        static Material MakeMetal(Color c, float metallic, float roughness)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var m = new Material(shader) { name = "Combat_GunPodMetal" };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 1f - roughness);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 1f - roughness);
            return m;
        }
    }
}

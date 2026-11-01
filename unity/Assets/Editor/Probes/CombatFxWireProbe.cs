using System.Reflection;
using TbilisiFly.Character;
using TbilisiFly.Combat;
using TbilisiFly.Fx;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves two things from this pass:
    ///   1. The three src/weapons.js pose functions FootCover.cs's own header
    ///      flagged as missing — ApplyRifleHold/ApplyCoverArms/ApplyRifleSupport —
    ///      exist (Combat/RifleHolds.cs) and return true against a REAL humanoid
    ///      body's own bones (not a fixture), same discipline
    ///      Editor/Probes/CharCombatProbe.cs already uses for the shoulder pocket.
    ///   2. The four previously-zero-caller Fx consumers this pass wired directly
    ///      into their owning Combat/* class (RocketTrailFx into RocketLauncher,
    ///      MuzzleFlashFx into MachineGun, BlastRenderer into BlastFx, SmokeTrailFx
    ///      into PlaneAirframeHost) are actually REACHED and mutate real state —
    ///      not just constructed. Reads their private fields via reflection since
    ///      none of that is meant to be public API for an outside caller.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CombatFxWireProbe.Run
    /// </summary>
    public static class CombatFxWireProbe
    {
        static readonly string[] Bodies = { "banana", "eggplant", "toad" };
        const BindingFlags NI = BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags PI = BindingFlags.Public | BindingFlags.Instance;

        public static void Run()
        {
            RifleHoldFacts();
            RocketTrailFacts();
            MuzzleFlashFacts();
            BlastRendererFacts();
            SmokeTrailFacts();
            ImpactDecalFacts();
            ImpactParticleFacts();
            OnFootFlashFacts();
            ProbeExit.Exit(0);
        }

        static string B(bool b) => b.ToString().ToLowerInvariant();

        // =====================================================================
        // 1. foot.js:3624-3732's blocker — the pose functions it needs.
        // =====================================================================
        static void RifleHoldFacts()
        {
            var akm = WeaponDefs.ById["akm"];
            foreach (var id in Bodies)
            {
                string path = $"Assets/Resources/Art/Characters/{id}.fbx";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) { Debug.Log($"RIFLEHOLD id={id} missing=1"); continue; }

                var instance = Object.Instantiate(prefab);
                CharacterRig rig = null;
                try
                {
                    rig = new CharacterRig(instance, id);
                    Transform root = rig.Root;
                    Transform hand = Weapons.FindHandBone(root);
                    bool haveHand = hand != null;
                    Vector3 aim = new Vector3(0f, 0f, 1f);

                    bool holdOk = haveHand && RifleHolds.ApplyRifleHold(rig, root, hand, aim, akm, "aim", "R");
                    var armRes = haveHand ? RifleHolds.ApplyCoverArms(rig, root, hand, true) : default;

                    bool supOk = false;
                    if (holdOk)
                    {
                        var dummyWeapon = new GameObject("dummyWeapon").transform;
                        dummyWeapon.SetParent(hand, false);
                        supOk = RifleHolds.ApplyRifleSupport(rig, root, dummyWeapon, akm, 1f, aim, "R");
                        Object.DestroyImmediate(dummyWeapon.gameObject);
                    }

                    Debug.Log($"RIFLEHOLD id={id} humanoid={B(rig.IsHumanoid)} hand={B(haveHand)} " +
                              $"holdOk={B(holdOk)} coverArmsOk={B(armRes.Ok)} supportOk={B(supOk)}");
                }
                finally
                {
                    if (rig != null) Object.DestroyImmediate(rig.Root.gameObject);
                    else Object.DestroyImmediate(instance);
                }
            }
        }

        // =====================================================================
        // 2a. Fx/RocketTrailFx.cs, wired directly inside Combat/Rockets.cs.
        // =====================================================================
        static void RocketTrailFacts()
        {
            var planeGo = new GameObject("probePlane");
            try
            {
                var launcher = new RocketLauncher();
                launcher.Update(0.016f, true, planeGo.transform, 120f); // _cool starts at 0 -> fires this frame
                int liveAfterFire = launcher.Live.Count;
                for (int i = 0; i < 6; i++) launcher.Update(0.02f, false, planeGo.transform, 120f); // let puffs accumulate

                var trail = typeof(RocketLauncher).GetField("_trail", NI).GetValue(launcher);
                var mat = (Material)typeof(RocketTrailFx).GetField("Mat", PI).GetValue(trail);
                var life = (float[])typeof(RocketTrailFx).GetField("_life", NI).GetValue(trail);
                int spawned = 0;
                foreach (var l in life) if (l < 1f) spawned++;

                Debug.Log($"ROCKETTRAIL liveRocketsAfterFire={liveAfterFire} matFound={B(mat != null)} spawnedPuffs={spawned}");
                launcher.Dispose();
            }
            finally { Object.DestroyImmediate(planeGo); }
        }

        // =====================================================================
        // 2b. Fx/MuzzleFlashFx.cs, wired directly inside Combat/Guns.cs.
        // =====================================================================
        static void MuzzleFlashFacts()
        {
            var gun = new MachineGun { TargetMask = 0 };
            gun.Update(0.02f, true, Vector3.zero, Vector3.forward); // _cool starts at 0 -> fires this frame
            int liveTracers = gun.Live.Count;

            var fx = typeof(MachineGun).GetField("_muzzleFx", NI).GetValue(gun);
            var flashMat = (Material)typeof(MuzzleFlashFx).GetField("FlashMat", PI).GetValue(fx);
            var tracerMat = (Material)typeof(MuzzleFlashFx).GetField("TracerMat", PI).GetValue(fx);
            float flashT = (float)typeof(MuzzleFlashFx).GetField("_flashT", NI).GetValue(fx);

            Debug.Log($"MUZZLEFLASH liveTracers={liveTracers} flashMatFound={B(flashMat != null)} " +
                      $"tracerMatFound={B(tracerMat != null)} flashTAfterShot={flashT:F4}");
            gun.Dispose();
        }

        // =====================================================================
        // 2c. Fx/BlastRenderer.cs, wired directly inside Combat/Blast.cs.
        // =====================================================================
        static void BlastRendererFacts()
        {
            var blast = new BlastFx();
            blast.Explode(Vector3.zero, Vector3.up, 5f);
            blast.Update(0.1f); // this call also runs _renderer.Render() internally now

            var renderer = typeof(BlastFx).GetField("_renderer", NI).GetValue(blast);
            var ballMats = (Material[])typeof(BlastRenderer).GetField("_ballMats", NI).GetValue(renderer);
            int matsFound = 0;
            foreach (var m in ballMats) if (m != null) matsFound++;
            int liveBalls = 0;
            foreach (var b in blast.Balls) if (b.Live) liveBalls++;

            Debug.Log($"BLASTRENDER liveBallsAfterExplode={liveBalls} ballMatsFound={matsFound}/{ballMats.Length}");
            blast.Dispose();
        }

        // =====================================================================
        // 2d. Fx/SmokeTrailFx.cs, wired directly inside Combat/PlaneAirframeHost.cs.
        // =====================================================================
        static void SmokeTrailFacts()
        {
            var planeGo = new GameObject("probeSmokePlane");
            try
            {
                var host = new PlaneAirframeHost(null, planeGo.transform, null, true);
                host.Tick(0.06f, crashed: true, leftDamaged: false, rightDamaged: false,
                    leftDetached: false, rightDetached: false); // > 0.05s cadence -> one spawn at Plane.position

                var smoke = typeof(PlaneAirframeHost).GetField("_smoke", NI).GetValue(host);
                var life = (float[])typeof(SmokeTrailFx).GetField("_life", NI).GetValue(smoke);
                var mat = (Material)typeof(SmokeTrailFx).GetField("Mat", PI).GetValue(smoke);
                int spawned = 0;
                foreach (var l in life) if (l < 1f) spawned++;

                Debug.Log($"SMOKETRAIL spawned={spawned} matFound={B(mat != null)}");
                host.Dispose();
            }
            finally { Object.DestroyImmediate(planeGo); }
        }

        // =====================================================================
        // 2e. Fx/ImpactDecalFx.cs, wired directly inside Combat/ImpactFx.cs's
        // ImpactFx class (src/impactfx.js's DecalLayer renderer — previously the
        // one impactfx.js gap with no Unity consumer at all: DecalLayer/
        // DecalStamp/DecalGeometry were pure pose/size data with nothing
        // submitting them to a GPU instanced draw).
        // =====================================================================
        static void ImpactDecalFacts()
        {
            var fx = new ImpactFx();
            fx.Spall.Stamp(Vector3.zero, Vector3.up, 0.1f, 0.1f, tint: new Color32(200, 190, 170, 255));
            fx.Pits.Stamp(Vector3.zero, Vector3.up, 0.02f, 0.02f);
            fx.Update(0.02f); // calls the owned ImpactDecalFx.Render(fx) internally

            var decalFx = typeof(ImpactFx).GetField("_decalFx", NI).GetValue(fx);
            var spallMat = (Material)typeof(ImpactDecalFx).GetField("SpallMat", PI).GetValue(decalFx);
            var pitMat = (Material)typeof(ImpactDecalFx).GetField("PitMat", PI).GetValue(decalFx);
            bool spallTexFound = spallMat != null && spallMat.mainTexture != null;
            bool pitTexFound = pitMat != null && pitMat.mainTexture != null;

            Debug.Log($"IMPACTDECAL spallCount={fx.Spall.Count} pitCount={fx.Pits.Count} " +
                      $"spallMatFound={B(spallMat != null)} pitMatFound={B(pitMat != null)} " +
                      $"spallTexFound={B(spallTexFound)} pitTexFound={B(pitTexFound)}");
            fx.Dispose();
        }

        // =====================================================================
        // 2f. Fx/ImpactParticleFx.cs — the ParticleCloud (Combat/ImpactFx.cs's
        // Debris/Sparks) AND ShardBurst/ImpactDust (Combat/Guns.cs's MachineGun)
        // renderer this pass built to close the handed-over _TintLife/_Tint
        // mismatch (see Fx/ImpactParticleFx.cs's own header for the mechanism
        // chosen — Shaders/ImpactSprite.shader instead of the orphaned
        // Shaders/ImpactParticle.shader). Proves a live particle reaches EACH of
        // the four Materials, Render() runs (via each owner's own Update(dt))
        // without throwing, and every Material resolved to a real Shader.
        // =====================================================================
        static void ImpactParticleFacts()
        {
            var fx = new ImpactFx();
            fx.Debris.Emit(Vector3.zero, 5, new Color32(150, 140, 120, 255));
            fx.Sparks.Emit(Vector3.zero, 3, new Color32(255, 200, 120, 255));
            fx.Update(0.02f); // calls the owned ImpactParticleFx.Render(fx) internally

            var particleFx = typeof(ImpactFx).GetField("_particleFx", NI).GetValue(fx);
            var debrisMat = (Material)typeof(ImpactParticleFx).GetField("DebrisMat", PI).GetValue(particleFx);
            var sparksMat = (Material)typeof(ImpactParticleFx).GetField("SparksMat", PI).GetValue(particleFx);
            int debrisLive = 0; foreach (var l in fx.Debris.Life) if (l < 1f) debrisLive++;
            int sparksLive = 0; foreach (var l in fx.Sparks.Life) if (l < 1f) sparksLive++;

            var gun = new MachineGun { TargetMask = 0 };
            gun.Shards.Burst(Vector3.zero, Vector3.up, () => 0.5f);
            gun.Dust.Burst(Vector3.zero, 6, 0.4f, () => 0.5f);
            gun.Update(0.02f, false, Vector3.zero, Vector3.forward); // calls _particleFx.Render(Shards, Dust) internally

            var gunParticleFx = typeof(MachineGun).GetField("_particleFx", NI).GetValue(gun);
            var shardMat = (Material)typeof(ImpactParticleFx).GetField("ShardMat", PI).GetValue(gunParticleFx);
            var dustMat = (Material)typeof(ImpactParticleFx).GetField("DustMat", PI).GetValue(gunParticleFx);
            int shardsLive = 0; foreach (var l in gun.Shards.Life) if (l < 1f) shardsLive++;
            int dustLive = 0; foreach (var l in gun.Dust.Life) if (l < 1f) dustLive++;

            Debug.Log($"IMPACTPARTICLE debrisLive={debrisLive} sparksLive={sparksLive} " +
                      $"shardsLive={shardsLive} dustLive={dustLive} debrisMatFound={B(debrisMat != null)} " +
                      $"sparksMatFound={B(sparksMat != null)} shardMatFound={B(shardMat != null)} " +
                      $"dustMatFound={B(dustMat != null)}");
            fx.Dispose();
            gun.Dispose();
        }

        // =====================================================================
        // 2g. Fx/OnFootMuzzleFlashFx.cs — the on-foot pistol/rifle flash
        // (Combat/Shooting.cs's Pistol.FlashVisible/FlashScale/FlashRotation),
        // previously computed with zero consumers. Proves Fire() hands the held
        // weapon's own Transform through (no Character/Core edit needed — see
        // Fx/OnFootMuzzleFlashFx.cs's header), Update()'s own Render(this) call
        // runs without throwing, and the flash Material resolved to a real Shader.
        // =====================================================================
        static void OnFootFlashFacts()
        {
            var heldGo = new GameObject("probeHeldWeapon");
            try
            {
                var p = new Pistol();
                var def = WeaponDefs.ById["makarov"];
                float pitch = 0f, yaw = 0f;
                // src/shooting.js:541 _magazine — the caller sets the held cartridge
                // and ticks Update() once BEFORE ever calling Fire(), same as the
                // real per-frame caller does; skipping this leaves Ammo null, which
                // Fire() reads as an empty mag (dry-fire branch, returns false).
                p.SetHeldCartridge(def.CartridgeId);
                p.Update(0.0001f, true, 0f, 5f, false, ref pitch);
                var req = new Pistol.FireRequest
                {
                    WeaponHeld = true,
                    CartridgeId = def.CartridgeId,
                    Muzzle = Vector3.zero,
                    AimDir = Vector3.forward,
                    HeldWeapon = heldGo.transform,
                    HeldWeaponDef = def,
                    ClipShotAvailable = false,
                };
                bool fired = p.Fire(req, ref pitch, ref yaw);
                p.Update(0.01f, true, 0f, 5f, false, ref pitch); // calls the owned _flashFx.Render(this) internally

                var flashFx = typeof(Pistol).GetField("_flashFx", NI).GetValue(p);
                var flashMat = (Material)typeof(OnFootMuzzleFlashFx).GetField("FlashMat", PI).GetValue(flashFx);

                Debug.Log($"ONFOOTFLASH fired={B(fired)} visible={B(p.FlashVisible)} " +
                          $"heldFound={B(p.FlashHeldWeapon != null)} flashMatFound={B(flashMat != null)} " +
                          $"scale={p.FlashScale:F4}");
                p.Dispose();
            }
            finally { Object.DestroyImmediate(heldGo); }
        }
    }
}

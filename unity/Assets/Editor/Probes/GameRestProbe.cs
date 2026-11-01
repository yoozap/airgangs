using TbilisiFly.Core;
using TbilisiFly.Flight;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the "game-rest" port (Core/PlaneHullDamage.cs, Core/PlaneGroundProbe.cs,
    /// Core/PlaneGroundRoll.cs, Core/PlaneWallContact.cs, Core/CharacterPreviewRig.cs)
    /// against tools/_probe_gamerest.mjs, which pulls the same constants out of
    /// the real src/game.js / src/planephys.js / src/main.js with regexes.
    ///
    /// Every KEY=value line here either (a) reads a const/static field straight
    /// off the ported class (no re-typing), or (b) RUNS the real method with
    /// synthetic input chosen to land on one side or the other of a threshold,
    /// and reports the outcome — the same "exercise the real code" standard
    /// Flight/PlanePhysicsProbe.cs already sets for this port.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.GameRestProbe.Run
    /// </summary>
    public static class GameRestProbe
    {
        public static void Run()
        {
            // ---- constants, read directly off the ported classes ----
            // All 47 keys tools/_probe_gamerest.mjs prints, same names/shapes,
            // so tools/unity_port_check.py's line-by-line skeleton match pairs
            // every one of them (not just the 7 this probe used to print).
            Debug.Log($"MAX_HEALTH={PlaneHullDamage.MaxHealth}");
            Debug.Log($"WHEEL_HEIGHT={PlaneGroundProbe.WheelHeight}");
            Debug.Log($"REVERSE_SPEED={PlaneGroundRoll.ReverseSpeed}");
            Debug.Log($"G={PlanePhysicsConst.G}"); // reused from Flight/PlanePhysics.cs, not re-declared
            Debug.Log($"RAM_TEARSPEC={PlanePhysicsConst.TearSpec}");
            Debug.Log($"RAM_DEADZONE={PlanePhysicsConst.Deadzone}");
            Debug.Log($"RAM_KPLANE={PlanePhysicsConst.KPlane}");

            Debug.Log($"HEALTH_BAND_GOOD={PlaneHullDamage.HealthBandGood}");
            Debug.Log($"HEALTH_BAND_WARN={PlaneHullDamage.HealthBandWarn}");
            Debug.Log($"COLLAPSE_GEAR_DMG={PlaneHullDamage.CollapseGearDmg}");
            Debug.Log($"SHEAR_WING_DMG={PlaneHullDamage.ShearWingDmg}");
            Debug.Log($"SHEAR_TAIL_DMG={PlaneHullDamage.ShearTailDmg}");
            Debug.Log($"WING_DAMAGE_CAP={PlaneHullDamage.WingDamageCap}");
            Debug.Log($"WING_DROOP_RAD={PlaneHullDamage.WingDroopRad}");

            Debug.Log($"TOUCHDOWN_FATAL_FWDY={PlaneGroundRoll.TouchdownFatalFwdY}");
            Debug.Log($"TOUCHDOWN_BANK_RIGHTY={PlaneGroundRoll.TouchdownBankRightY}");
            Debug.Log($"TOUCHDOWN_SHEAR_SPEED={PlaneGroundRoll.TouchdownShearSpeed}");
            Debug.Log($"TOUCHDOWN_DAMAGEWING_DMG={PlaneGroundRoll.TouchdownDamageWingDmg}");
            Debug.Log($"TOUCHDOWN_HARD_SINK={PlaneGroundRoll.TouchdownHardSink}");
            Debug.Log($"TOUCHDOWN_MED_SINK={PlaneGroundRoll.TouchdownMedSink}");
            Debug.Log($"COLLAPSE_GEAR_SINK_LEVEL={PlaneGroundRoll.CollapseGearSinkLevel}");
            Debug.Log($"COLLAPSE_GEAR_SINK_UNLEVEL={PlaneGroundRoll.CollapseGearSinkUnlevel}");

            Debug.Log($"ROLLING_FRICTION_GEAR={PlaneGroundRoll.RollingFrictionGear}");
            Debug.Log($"ROLLING_FRICTION_BELLY={PlaneGroundRoll.RollingFrictionBelly}");
            Debug.Log($"BRAKE_G={PlaneGroundRoll.BrakeG}");
            Debug.Log($"REVERSE_ACCEL={PlaneGroundRoll.ReverseAccel}");
            Debug.Log($"ROTATE_VR_FRAC={PlaneGroundRoll.RotateVrFrac}");
            Debug.Log($"LIFTOFF_HEIGHT={PlaneGroundRoll.LiftoffHeight}");
            Debug.Log($"LIFTOFF_EDGE_DROP={PlaneGroundRoll.LiftoffEdgeDrop}");
            Debug.Log($"BELLY_SLIDE_DMG_PER_S={PlaneGroundRoll.BellySlideDmgPerS}");

            Debug.Log($"LAND_PITCH_CLAMP={PlaneGroundRoll.LandPitchClamp}");

            Debug.Log($"TOUCHWALL_MIN_SPEED={PlaneWallContact.TouchWallMinSpeed}");
            Debug.Log($"CEILING_HITCOOLDOWN={PlaneWallContact.HitCooldownDuration}");
            Debug.Log($"BUILDINGSMASH_HEADON_SPEED={PlaneWallContact.BuildingSmashHeadOnSpeed}");
            Debug.Log($"BUILDINGSMASH_TOTAL_MULT={PlaneWallContact.BuildingSmashTotalMult}");

            Debug.Log($"WINGHIT_COOLDOWN_TICK={PlaneWallContact.WingHitCooldownTick}");
            Debug.Log($"WINGHIT_MIN_SPEED={PlaneWallContact.WingHitMinSpeed}");
            Debug.Log($"WINGWALL_SHEAR_SPEED={PlaneWallContact.WingWallShearSpeed}");
            Debug.Log($"WINGWALL_DAMAGEWING_BASE={PlaneWallContact.WingWallDamageWingBase}");
            Debug.Log($"WINGDRAG_MIN_SPEED={PlaneWallContact.WingDragMinSpeed}");
            Debug.Log($"WINGDRAG_SHEAR_SPEED={PlaneWallContact.WingDragShearSpeed}");
            Debug.Log($"WINGDRAG_DAMAGEWING_DMG={PlaneWallContact.WingDragDamageWingDmg}");

            Debug.Log($"RAM_SPECUS_GATE={PlaneWallContact.RamSpecUsGate}");

            Debug.Log($"PREVIEW_FOV={CharacterPreviewRig.PreviewFov}");
            Debug.Log($"PREVIEW_SUN_INTENSITY={CharacterPreviewRig.PreviewSunIntensity}");
            Debug.Log($"PREVIEW_RIM_INTENSITY={CharacterPreviewRig.PreviewRimIntensity}");
            Debug.Log($"PREVIEW_DRAG_SCALE={CharacterPreviewRig.PreviewDragScale}");

            // ---- PlaneGroundProbe.WheelH / NoseLenOf ----
            Debug.Log($"WheelH_gear={PlaneGroundProbe.WheelH(1f)} WheelH_belly={PlaneGroundProbe.WheelH(0f)}");
            Debug.Log($"NoseLenOf_explicit={PlaneGroundProbe.NoseLenOf(5.5f, 11f)} " +
                      $"NoseLenOf_fallback={PlaneGroundProbe.NoseLenOf(null, 11f)} " +
                      $"NoseLenOf_floor={PlaneGroundProbe.NoseLenOf(null, 1f)}"); // 1*0.35=0.35 -> floors to 3

            // ---- PlaneHullDamage: health bands + damage-method amounts ----
            RunHullDamage();

            // ---- PlaneGroundRoll.Touchdown: threshold behaviour ----
            RunTouchdown();

            // ---- PlaneGroundRoll.TakeoffHint formatting ----
            Debug.Log($"TakeoffHint_sample={PlaneGroundRoll.TakeoffHint(41.7f)}"); // 41.7*3.6=150.12 -> 150

            // ---- CharacterPreviewRig: camera/light setup ----
            RunCharacterPreview();
        }

        static void RunHullDamage()
        {
            var hull = new PlaneHullDamage();
            HealthBand lastBand = default;
            float lastFrac = -1f;
            hull.OnHealthChanged = (frac, band) => { lastFrac = frac; lastBand = band; };

            hull.Health = PlaneHullDamage.MaxHealth * 0.56f;
            hull.UpdateHealthBar();
            Debug.Log($"band_at_0.56={lastBand}"); // expect Good (>0.55)

            hull.Health = PlaneHullDamage.MaxHealth * 0.30f;
            hull.UpdateHealthBar();
            Debug.Log($"band_at_0.30={lastBand}"); // expect Warn (>0.25)

            hull.Health = PlaneHullDamage.MaxHealth * 0.10f;
            hull.UpdateHealthBar();
            Debug.Log($"band_at_0.10={lastBand}"); // expect Critical

            // CollapseGear: -90 health, Gear -> 0
            hull = new PlaneHullDamage();
            hull.OnHealthChanged = (f, b) => { };
            float before = hull.Health;
            hull.CollapseGear();
            Debug.Log($"collapseGear_dmg={before - hull.Health} gear={hull.Struct.Gear}");

            // ShearWing: -280 health, WingL -> 0, WingDmgLeft -> true
            hull = new PlaneHullDamage();
            before = hull.Health;
            hull.ShearWing("left", "test");
            Debug.Log($"shearWing_dmg={before - hull.Health} wingL={hull.Struct.Air.WingL} dmgFlag={hull.WingDmgLeft}");

            // ShearTail: -240 health, Tail -> 0
            hull = new PlaneHullDamage();
            before = hull.Health;
            hull.ShearTail("test");
            Debug.Log($"shearTail_dmg={before - hull.Health} tail={hull.Struct.Air.Tail}");

            // DamageWing: CrashDamage(dmg) with ColMult=1, WingR capped to 0.65
            hull = new PlaneHullDamage();
            hull.ColMult = 1f;
            before = hull.Health;
            hull.DamageWing("right", 250f);
            Debug.Log($"damageWing_dmg={before - hull.Health} wingR={hull.Struct.Air.WingR} cap=0.65");

            // Explode: health clamps to 0, Crashed true, wing/tail zeroed
            hull = new PlaneHullDamage();
            hull.ApplyDamage(PlaneHullDamage.MaxHealth + 500f);
            Debug.Log($"explode_health={hull.Health} crashed={hull.Crashed} " +
                      $"wingL={hull.Struct.Air.WingL} wingR={hull.Struct.Air.WingR} tail={hull.Struct.Air.Tail}");
        }

        static void RunTouchdown()
        {
            var probeWorld = new NullGroundWorld();
            var ground = new PlaneGroundProbe(probeWorld);

            // Case 1: level, moderate sink (7 m/s) — hard-landing branch
            // (sink>6.2), NOT past the level collapse threshold (9) — gear
            // survives, lands (no bounce since level && sink<=12).
            var hull1 = new PlaneHullDamage();
            var roll1 = new PlaneGroundRoll(ground, hull1);
            var go1 = new GameObject("probe_plane_1");
            go1.transform.position = new Vector3(0f, 10f, 0f);
            go1.transform.rotation = Quaternion.identity; // level
            var aero = MakeAero();
            float speed1 = 0f;
            Vector3 vel1 = new Vector3(0f, -7f, 40f); // sink=7
            Vector3 rates1 = Vector3.zero;
            float healthBefore1 = hull1.Health;
            bool grounded1 = roll1.Touchdown(go1.transform, ref speed1, ref vel1, ref rates1, 10f, aero);
            Debug.Log($"touchdown_case1 grounded={grounded1} crashed={hull1.Crashed} " +
                      $"dmg={healthBefore1 - hull1.Health} gear={hull1.Struct.Gear} speedAfter={speed1}");
            Object.DestroyImmediate(go1);

            // Case 2: level, severe sink (10 m/s) — past the level collapse
            // threshold (9) -> gear collapses; still not >12 so it lands.
            var hull2 = new PlaneHullDamage();
            var roll2 = new PlaneGroundRoll(ground, hull2);
            var go2 = new GameObject("probe_plane_2");
            go2.transform.position = new Vector3(0f, 10f, 0f);
            go2.transform.rotation = Quaternion.identity;
            float speed2 = 0f;
            Vector3 vel2 = new Vector3(0f, -10f, 40f);
            Vector3 rates2 = Vector3.zero;
            bool grounded2 = roll2.Touchdown(go2.transform, ref speed2, ref vel2, ref rates2, 10f, aero);
            Debug.Log($"touchdown_case2 grounded={grounded2} crashed={hull2.Crashed} gear={hull2.Struct.Gear}");
            Object.DestroyImmediate(go2);

            // Case 3: nose-down past -0.45 fwd.y at >60 m/s -> instant Explode.
            var hull3 = new PlaneHullDamage();
            var roll3 = new PlaneGroundRoll(ground, hull3);
            var go3 = new GameObject("probe_plane_3");
            go3.transform.position = new Vector3(0f, 10f, 0f);
            go3.transform.rotation = Quaternion.Euler(60f, 0f, 0f); // steep nose-down-ish attitude
            float speed3 = 65f;
            Vector3 vel3 = new Vector3(0f, -2f, 65f);
            Vector3 rates3 = Vector3.zero;
            bool grounded3 = roll3.Touchdown(go3.transform, ref speed3, ref vel3, ref rates3, 10f, aero);
            Debug.Log($"touchdown_case3 grounded={grounded3} crashed={hull3.Crashed} fwdY={go3.transform.forward.y:F2}");
            Object.DestroyImmediate(go3);
        }

        static FlightModel MakeAero()
        {
            var def = new AeroPlaneDef { Mass = 2200f, Prop = true, ClMax = 1.5f, GBreak = 7f, Tw = 0.38f, MaxSpeed = 150f };
            return new FlightModel(def, 11f, 4.5f);
        }

        static void RunCharacterPreview()
        {
            var root = new GameObject("probe_preview_root");
            GameObject SpawnStub(string id)
            {
                var g = new GameObject($"stub_{id}");
                return g;
            }
            var preview = new CharacterPreviewRig(root.transform, SpawnStub);
            preview.Start(256, 256, "banana");
            Debug.Log($"preview_target_w={preview.Target.width} h={preview.Target.height}");
            preview.Drag(10f);
            preview.Stop();
            Object.DestroyImmediate(root);
        }

        /// <summary>Degenerate ground world — Touchdown doesn't call into
        /// IGroundProbeWorld at all (it only needs the PRE-computed gY this
        /// probe hands it directly), so every member here can be a stub; it
        /// exists only to satisfy PlaneGroundProbe's constructor.</summary>
        sealed class NullGroundWorld : IGroundProbeWorld
        {
            public float? HeightAt(float x, float z) => null;
            public float? BoreFloorAt(float x, float z, float refY) => null;
            public System.Collections.Generic.IReadOnlyList<Vector3> DownwardHits(float x, float z, float originY) => null;
        }
    }
}

using TbilisiFly.Combat;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves this session's guns.js/impactfx.js gap-closing work (Combat/GunFx.cs's
    /// GunFx/ShardBurst/ImpactDust/GunToast/GunCrosshair, plus the named consts added
    /// to Combat/Guns.cs's MachineGun for its DecalLayer reuse) carries the SAME
    /// numbers as the real src/guns.js literals. tools/_probe_combatrest.mjs prints
    /// the JS side by regex straight off src/guns.js; this prints the C# side
    /// straight off the internal consts those literals were ported into — no
    /// retyped numbers on either side. KEY names here must match that script's
    /// EXACTLY (no prefix, no extra fields per line, one file's worth of facts only).
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CombatRestProbe.Run
    /// </summary>
    public static class CombatRestProbe
    {
        public static void Run()
        {
            Debug.Log($"RATE={F(MachineGun.Rate)}");
            Debug.Log($"RANGE={F(MachineGun.Range)}");
            Debug.Log($"BULLET_SPEED={F(MachineGun.BulletSpeed)}");
            Debug.Log($"DAMAGE={F(MachineGun.Damage)}");
            Debug.Log($"PLANE_DAMAGE={F(MachineGun.PlaneDamageAmount)}");
            Debug.Log($"SPREAD={F(MachineGun.Spread)}");
            Debug.Log($"MAX_TRACERS={MachineGun.MaxTracers}");

            Debug.Log($"SHARD_MAX={ShardBurst.Max}");
            Debug.Log($"SHARD_PER_BURST={ShardBurst.PerBurst}");
            Debug.Log($"SHARD_LIFE={F(ShardBurst.LifeSpan)}");
            Debug.Log($"SHARD_GRAV={F(ShardBurst.Gravity)}");

            Debug.Log($"DUST_MAX={ImpactDust.Max}");
            Debug.Log($"DUST_LIFE={F(ImpactDust.LifeSpan)}");
            Debug.Log($"DUST_GRAV={F(ImpactDust.Gravity)}");

            Debug.Log($"GUNSOUND_BREECH_FREQ_BASE={F(GunFx.BreechFreqBase)}");
            Debug.Log($"GUNSOUND_BREECH_FREQ_RANGE={F(GunFx.BreechFreqRange)}");
            Debug.Log($"GUNSOUND_BREECH_Q={F(GunFx.BreechQ)}");
            Debug.Log($"GUNSOUND_BREECH_GAIN={F(GunFx.BreechGain)}");
            Debug.Log($"GUNSOUND_BREECH_DUR={F(GunFx.BreechDur)}");
            Debug.Log($"GUNSOUND_THUMP_F0={F(GunFx.ThumpF0)}");
            Debug.Log($"GUNSOUND_THUMP_F1={F(GunFx.ThumpF1)}");
            Debug.Log($"GUNSOUND_THUMP_GAIN={F(GunFx.ThumpGain)}");
            Debug.Log($"GUNSOUND_THUMP_DUR={F(GunFx.ThumpDur)}");

            Debug.Log($"HOLES_BASE_SIZE={F(MachineGun.HolesBaseSize)}");
            Debug.Log($"HOLES_MAX={MachineGun.HolesMax}");
            Debug.Log($"CRACKS_BASE_SIZE={F(MachineGun.CracksBaseSize)}");
            Debug.Log($"CRACKS_MAX={MachineGun.CracksMax}");
            Debug.Log($"DECAL_LIFT={F(MachineGun.DecalLift)}");
            Debug.Log($"STAMP_SCALE_MIN={F(MachineGun.StampScaleMin)}");
            Debug.Log($"STAMP_SCALE_RANGE={F(MachineGun.StampScaleRange)}");

            Debug.Log($"TOAST_PCT_HIGH={GunToast.PctHigh}");
            Debug.Log($"TOAST_PCT_MID={GunToast.PctMid}");
            Debug.Log($"TOAST_DURATION={F(GunToast.DurationSeconds)}");
            Debug.Log($"CROSSHAIR_AIM_DIST={F(GunCrosshair.AimDistance)}");
        }

        // Trims trailing zeros the same way the hand-written JS literals read
        // (12, not 12.0000) so the two sides are eyeballable side by side.
        static string F(float v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
    }
}

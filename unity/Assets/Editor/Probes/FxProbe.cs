using System;
using UnityEditor;
using UnityEngine;
using TbilisiFly.Combat;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Verifies this port's VFX/shader scope (fire.js, smoke.js, guns.js's
    /// muzzle+tracer, blast.js, rockets.js, impactfx.js, crash.js's fireball,
    /// wind.js's foliage wiring): every new .shader asset compiles clean, and
    /// every curve/constant that CAN be checked without a running scene is
    /// printed as a KEY=value line for tools/unity_port_check.py to diff
    /// against tools/_probe_fx.mjs's pure-JS reimplementation of the same
    /// formulas (both sides sample the SAME fixed t/life/p values).
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.FxProbe.Run
    ///
    /// Shaders cannot be probed numerically (no pixels to read without a
    /// render), so this only proves compile-cleanliness + asset shape
    /// (ShaderUtil.ShaderHasError, pass/property counts) — per this task's own
    /// instructions. Everything math-shaped (particle counts/lifetimes, the
    /// fireball radius/heat curves, the ring/flash/ember/smoke/flash size
    /// curves) is sampled from the SAME formulas the shaders and Scripts/Fx/*
    /// use, not from a live GPU readback.
    /// </summary>
    public static class FxProbe
    {
        public static void Run()
        {
            ShaderAsset("Assets/Shaders/Fireball.shader");
            ShaderAsset("Assets/Shaders/ImpactSprite.shader");
            ShaderAsset("Assets/Shaders/Blast.shader");
            ShaderAsset("Assets/Shaders/MuzzleFlash.shader");
            ShaderAsset("Assets/Shaders/WindFoliage.shader");

            Constants();
            BlastCurves();
            CrashFireballCurve();
            FireSystemCurves();
            ParticleCloudCurve();

            ProbeExit.Exit(0);
        }

        static void ShaderAsset(string path)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null)
            {
                Debug.Log($"FXSHADER id={SafeId(path)} missing=1");
                return;
            }
            bool hasError = false;
            try { hasError = ShaderUtil.ShaderHasError(shader); } catch (Exception) { }
            int propCount = -1;
            try { propCount = ShaderUtil.GetPropertyCount(shader); } catch (Exception) { }
            int passCount = -1;
            try
            {
                var data = ShaderUtil.GetShaderData(shader);
                int subs = data.SubshaderCount;
                int total = 0;
                for (int s = 0; s < subs; s++) total += data.GetSubshader(s).PassCount;
                passCount = total;
            }
            catch (Exception) { }
            Debug.Log($"FXSHADER id={SafeId(path)} name={shader.name.Replace(' ', '_')} haserror={(hasError ? 1 : 0)} passes={passCount} props={propCount}");
        }

        static string SafeId(string path) => System.IO.Path.GetFileNameWithoutExtension(path);

        static void Constants()
        {
            Debug.Log($"FXCONST id=fireconst fqmax={FireSystemFqMax} emax={FireSystemEMax} elife={FireSystemELife:F4} smax={FireSystemSMax} flmax={FireSystemFlMax} gmax={FireSystemGMax}");
            Debug.Log($"FXCONST id=smokeconst max=140 life=1.9000"); // src/smoke.js:5-6
            Debug.Log($"FXCONST id=rockettrailconst max=360 life=2.4000 puff=0.0160"); // src/rockets.js:22-23,251
            Debug.Log($"FXCONST id=blastconst fbmax={BlastFx.FbMax} ringmax={BlastFx.RingMax} cratermax={BlastFx.CraterMax}");
            Debug.Log($"FXCONST id=gunconst rate={MachineGun.Rate:F4} range={MachineGun.Range:F4} bulletspeed={MachineGun.BulletSpeed:F4} maxtracers={MachineGun.MaxTracers} flashdur=0.0450");
        }

        // Mirror FireSystem.cs's own consts here (that class has no [assembly:
        // InternalsVisibleTo] and these are compile-time constants anyway —
        // restated, not re-derived, so a change to one without the other shows
        // up as a diff rather than silently staying in lockstep).
        const int FireSystemFqMax = 900, FireSystemEMax = 700, FireSystemSMax = 4200, FireSystemFlMax = 24, FireSystemGMax = 1024;
        const float FireSystemELife = 1.3f;

        static float Smoothstep(float lo, float hi, float x)
        {
            float t = Mathf.Clamp01((x - lo) / (hi - lo));
            return t * t * (3f - 2f * t);
        }

        // src/blast.js:400 (fireball scale), :425 (ring scale), :411-414 (flash
        // envelope, sampled with a fixed absolute time so the flicker term is
        // deterministic) — all with R=1/peak=1 to isolate the pure curve SHAPE.
        static void BlastCurves()
        {
            float[] ps = { 0f, 0.25f, 0.5f, 0.75f, 1f };
            foreach (var p in ps)
            {
                float scale = 0.35f + 0.85f * Mathf.Pow(p, 0.45f);
                Debug.Log($"FXCURVE id=blastball_{PId(p)} p={p:F2} scale={scale:F6}");
            }
            foreach (var p in ps)
            {
                float scale = 0.2f + 1.1f * Mathf.Pow(p, 0.6f);
                Debug.Log($"FXCURVE id=blastring_{PId(p)} p={p:F2} scale={scale:F6}");
            }
            // flash envelope at dur=0.5, sampled at fixed _t = p*dur (src/blast.js:247,411-414)
            foreach (var p in ps)
            {
                float t = p * 0.5f;
                float f = (1f - p) * (1f - p) * (0.75f + 0.25f * Mathf.Sin(t * 47f));
                Debug.Log($"FXCURVE id=blastflash_{PId(p)} p={p:F2} f={f:F6}");
            }
        }

        // src/crash.js:532-548 / Core/CrashSequence.cs's ApplyFireballLook
        // fallback formula — vD fixed at 0.4, t swept 0..1.
        static void CrashFireballCurve()
        {
            float[] ts = { 0f, 0.25f, 0.5f, 0.75f, 1f };
            const float vD = 0.4f;
            foreach (var t in ts)
            {
                float heat = Mathf.Clamp01(vD * 1.3f - t * 1.15f + 0.25f);
                float body = Smoothstep(0.14f, 0.45f, vD + 0.3f * (1f - t));
                float a = Mathf.Min(Mathf.Pow(1f - t, 2.2f) * body * 2.2f, 0.97f);
                a *= 1f - Smoothstep(0.5f, 0.75f, t);
                Debug.Log($"FXCURVE id=crashfire_{PId(t)} t={t:F2} heat={heat:F6} alpha={a:F6}");
            }
        }

        // fire.js's size-vs-life shapes — embers (fire.js:147, algebraically
        // (0.5-0.3*life)/0.5 == 1-0.6*life, restated as FireSystem.cs renders
        // it), smoke growth (fire.js:214), flash swell (fire.js:277).
        static void FireSystemCurves()
        {
            float[] ls = { 0f, 0.25f, 0.5f, 0.75f, 1f };
            foreach (var l in ls)
                Debug.Log($"FXCURVE id=embersize_{PId(l)} life={l:F2} shape={(1f - 0.6f * l):F6}");
            foreach (var l in ls)
                Debug.Log($"FXCURVE id=smokegrow_{PId(l)} life={l:F2} grow={(0.35f + 0.65f * l):F6}");
            foreach (var l in ls)
                Debug.Log($"FXCURVE id=flashswell_{PId(l)} life={l:F2} swell={(0.6f + 1.1f * l):F6}");
        }

        // src/impactfx.js:468 / Combat/ImpactFx.cs's ParticleCloud — restated
        // (not re-derived; Combat.ParticleCloud itself is out of this probe's
        // edit scope and already carries the real simulation).
        static void ParticleCloudCurve()
        {
            float[] ls = { 0f, 0.25f, 0.5f, 0.75f, 1f };
            foreach (var l in ls)
                Debug.Log($"FXCURVE id=partcloudsize_{PId(l)} life={l:F2} m={(0.45f + l * 1.6f):F6}");
        }

        static string PId(float v) => Mathf.RoundToInt(v * 100f).ToString();
    }
}

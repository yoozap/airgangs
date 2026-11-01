using System;
using UnityEngine;
using TbilisiFly.Core;
using TbilisiFly.Data;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// The rest of src/guns.js that Combat/Guns.cs's MachineGun left as VFX-hook
    /// comments: the gun's own dry muzzle report, its two local particle systems
    /// (glass shards off a knocked-out pane, dust puffs at a bullet strike / a
    /// crumbling stage), and the pure logic behind the hit toast and crosshair —
    /// all genuinely separate from Combat/ImpactFx.cs's general-purpose pipeline
    /// (src/guns.js:10-12 says so explicitly: "What it LEAVES is shared... [but]
    /// ...keeps its OWN copies" of the muzzle-local effects). Rendering (the
    /// InstancedMesh / Points layer) is left to the consumer, same precedent as
    /// Combat/ImpactFx.cs's ParticleCloud/DecalLayer; Shaders/ImpactParticle.shader
    /// is the shader slot both this file's particle sims and ImpactFx's
    /// ParticleCloud are meant to be drawn with.
    /// </summary>
    public static class GunFx
    {
        // src/guns.js:270-271 — breech report: bandpass noise burst.
        internal const float BreechFreqBase = 850f;
        internal const float BreechFreqRange = 400f;
        internal const float BreechQ = 0.7f;
        internal const float BreechGain = 0.55f;
        internal const float BreechDur = 0.09f; // :275 ramp to 0.001 by t+0.09
        // src/guns.js:280-286 — the mechanical thump of the action.
        internal const float ThumpF0 = 185f;
        internal const float ThumpF1 = 65f;
        internal const float ThumpGain = 0.3f;
        internal const float ThumpDur = 0.07f; // :286 stop at t+0.07

        /// <summary>src/guns.js:261-293 GunSound — the muzzle report ON the plane:
        /// fires dry and immediate, no propagation delay or reverb send (that is
        /// DOWNRANGE-only modelling, src/audiofx.js, used for impacts/collapses —
        /// src/guns.js:258-260). Reuses Core/AudioFx.cs's already-ported Burst/Tone
        /// synthesis primitives at dist=1 (inside DistanceModel.NearRef, so the
        /// loud/dark curves are within rounding of "no attenuation at all" — see
        /// this method's call sites for why a dedicated dry bypass isn't needed).</summary>
        public static void Shoot(float vol, Func<float> rng = null)
        {
            rng ??= () => UnityEngine.Random.value;
            // breech report: a hard noise crack...
            FxAudio.Burst(1f, vol, dur: BreechDur, freq: BreechFreqBase + rng() * BreechFreqRange, q: BreechQ,
                type: FilterKind.Bandpass, gain: BreechGain);
            // ...over the mechanical thump of the action
            FxAudio.Tone(1f, vol, dur: ThumpDur, f0: ThumpF0, f1: ThumpF1, gain: ThumpGain, type: OscKind.Square);
        }
    }

    /// <summary>Port of src/guns.js:176-256 ShardBurst — glinting glass shards
    /// sprayed from a broken window, falling fast under hard gravity. Simulation
    /// only (Pos/Life/Vel ring buffers + Update); the JS's additive point-sprite
    /// shader (src/guns.js:189-211) lives in Shaders/ImpactParticle.shader.</summary>
    public sealed class ShardBurst
    {
        public const int Max = 260;
        public const int PerBurst = 16;
        internal const float LifeSpan = 0.8f;   // src/guns.js:238
        internal const float Gravity = 16f;     // src/guns.js:239 — glass falls hard

        public readonly Vector3[] Pos = new Vector3[Max];
        public readonly Vector3[] Vel = new Vector3[Max];
        public readonly float[] Life = new float[Max];
        int _cursor;

        public ShardBurst()
        {
            for (int i = 0; i < Max; i++) Life[i] = 1f;
        }

        // src/guns.js:218 burst
        public void Burst(Vector3 pos, Vector3 normal, Func<float> rng = null)
        {
            rng ??= () => UnityEngine.Random.value;
            for (int k = 0; k < PerBurst; k++)
            {
                int i = _cursor;
                _cursor = (_cursor + 1) % Max;
                Pos[i] = pos;
                Life[i] = 0.001f;
                // spray out of the pane, then gravity takes them
                Vel[i] = new Vector3(
                    normal.x * (2f + rng() * 4f) + (rng() - 0.5f) * 3f,
                    1f + rng() * 2.5f,
                    normal.z * (2f + rng() * 4f) + (rng() - 0.5f) * 3f);
            }
        }

        // src/guns.js:233 update
        public void Update(float dt)
        {
            for (int i = 0; i < Max; i++)
            {
                if (Life[i] >= 1f) continue;
                Life[i] = Mathf.Min(1f, Life[i] + dt / LifeSpan);
                Vector3 v = Vel[i];
                v.y -= Gravity * dt; // glass falls hard
                Vel[i] = v;
                Pos[i] += v * dt;
            }
        }
    }

    /// <summary>Port of src/guns.js:300-377 ImpactDust — concrete puffs at bullet
    /// strikes, bigger plumes when a damage stage crumbles. Simulation only, same
    /// ring-buffer trick as ShardBurst; rendering shares Shaders/ImpactParticle.shader.</summary>
    public sealed class ImpactDust
    {
        public const int Max = 420;
        public const float LifeSpan = 0.9f;
        internal const float Gravity = 5f; // src/guns.js:360 — dust settles

        public readonly Vector3[] Pos = new Vector3[Max];
        public readonly Vector3[] Vel = new Vector3[Max];
        public readonly float[] Life = new float[Max];
        int _cursor;

        public ImpactDust()
        {
            for (int i = 0; i < Max; i++) Life[i] = 1f;
        }

        // src/guns.js:340 burst
        public void Burst(Vector3 pos, int n, float spread, Func<float> rng = null)
        {
            rng ??= () => UnityEngine.Random.value;
            for (int k = 0; k < n; k++)
            {
                int i = _cursor;
                _cursor = (_cursor + 1) % Max;
                Pos[i] = new Vector3(
                    pos.x + (rng() - 0.5f) * spread,
                    pos.y + (rng() - 0.5f) * spread * 0.5f,
                    pos.z + (rng() - 0.5f) * spread);
                Life[i] = 0.001f + rng() * 0.25f;
                Vel[i] = new Vector3((rng() - 0.5f) * 6f, 1.5f + rng() * 4f, (rng() - 0.5f) * 6f);
            }
        }

        // src/guns.js:354 update
        public void Update(float dt)
        {
            for (int i = 0; i < Max; i++)
            {
                if (Life[i] >= 1f) continue;
                Life[i] = Mathf.Min(1f, Life[i] + dt / LifeSpan);
                Vector3 v = Vel[i];
                v.y -= Gravity * dt; // dust settles
                Vel[i] = v;
                Pos[i] += v * dt;
            }
        }
    }

    /// <summary>Port of src/guns.js:624-638 _showToast — pure formatting logic
    /// (the DOM element itself is UI's job). Georgian strings kept verbatim —
    /// there is no English fallback anywhere in this project.</summary>
    public static class GunToast
    {
        // src/guns.js:633-635 — the toast bar's two colour thresholds.
        internal const int PctHigh = 55;
        internal const int PctMid = 25;

        public struct Formatted
        {
            public string Line;     // the address line (or the "destroyed" line)
            public int Pct;         // 0..100, remaining HP
            public Color32 BarColor0, BarColor1; // the two-stop gradient the JS CSS used
        }

        public static Formatted Format(DamageResult res)
        {
            string addr = string.IsNullOrEmpty(res.Addr) ? "უცნობი შენობა" : res.Addr;
            int pct = JsMath.Round(res.HpFrac * 100f);
            string line = res.Destroyed ? $"\U0001F4A5 {addr} — დანგრეულია!" : $"\U0001F3E2 {addr}";
            Color32 c0, c1;
            if (pct > PctHigh) { c0 = new Color32(0x29, 0xc6, 0xff, 255); c1 = new Color32(0x55, 0xf2, 0xff, 255); }
            else if (pct > PctMid) { c0 = new Color32(0xff, 0xb0, 0x2e, 255); c1 = new Color32(0xff, 0xd2, 0x57, 255); }
            else { c0 = new Color32(0xff, 0x2e, 0x55, 255); c1 = new Color32(0xff, 0x5e, 0x8a, 255); }
            return new Formatted { Line = line, Pct = pct, BarColor0 = c0, BarColor1 = c1 };
        }

        /// <summary>src/guns.js:637 — how long the toast stays up once shown.</summary>
        public const float DurationSeconds = 2.2f;
    }

    /// <summary>Port of src/guns.js:640-656 _updateCrosshair — pure screen-space
    /// projection, camera/container plumbing left to whoever owns the HUD (the
    /// JS itself retires this in favour of the combat HUD's own reticle once
    /// that is active — src/guns.js:643).</summary>
    public static class GunCrosshair
    {
        public const float AimDistance = 420f; // m — src/guns.js:648

        /// <summary>Returns false when the aim point is behind the camera (JS's
        /// `p.z > 1` after project()) — caller should hide the crosshair then.
        /// `uv` is viewport fraction (0,0)=bottom-left exactly like THREE's NDC
        /// remap at src/guns.js:654-655; flip uv.y for a top-left pixel origin.
        /// Takes `forward` pre-resolved by the caller (same convention as this
        /// class's sibling MachineGun.Update/Fire, which already take a `forward`
        /// parameter rather than a raw quaternion) — src/guns.js:647's
        /// `(0,0,-1).applyQuaternion(...)` is a THREE-frame construction this
        /// class deliberately does not re-derive, to avoid guessing at the
        /// mirrored-X / forward-axis convention GeoFrame.cs owns.</summary>
        public static bool Project(Camera camera, Vector3 planePosition, Vector3 forward, out Vector2 uv)
        {
            Vector3 aimPoint = planePosition + forward * AimDistance;
            Vector3 vp = camera.WorldToViewportPoint(aimPoint);
            uv = new Vector2(vp.x, vp.y);
            return vp.z > 0f; // in front of the camera (Unity's viewport z is view-space depth, not THREE's clip.z)
        }
    }
}

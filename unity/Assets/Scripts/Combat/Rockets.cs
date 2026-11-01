using System;
using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Core;
using TbilisiFly.Fx;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// Port of src/rockets.js's RocketLauncher — wing-mounted rockets, skill 2 on
    /// the action bar. One heavy rocket per trigger pull, alternating left/right
    /// hardpoints, flown as a real swept projectile (not hitscan — a rocket is
    /// slow enough that tunnelling through a wall is a real risk, hence the
    /// raycast covering "this frame's travel + 1.5 m" every update).
    ///
    /// THE ROCKET BODY IS NOW BUILT AND WIRED TOO (Fx/RocketBodyFx.cs) — a
    /// prior pass filed src/rockets.js:147-187 as "parked for a missing
    /// asset"; that was a misdiagnosis this pass corrected directly (grepped
    /// both this file and src/rockets.js for any GLTFLoader/loadAsync call:
    /// zero hits — the body/nose/fins are bare THREE.CylinderGeometry/
    /// ConeGeometry/BoxGeometry primitives, the same kind of thing
    /// City/ParkBuilder.cs already ports with its own primitive builders).
    /// The propellant trail IS also built AND WIRED — this class owns its own Fx.RocketTrailFx instance
    /// (below) and feeds it from Live every frame at the same ~0.016s cadence
    /// src/rockets.js:250-254 spawns puffs, reading positions only, exactly
    /// per that file's own header; nothing outside Combat/* has to know the
    /// trail exists. The actual detonation VFX (fireball/flash/ring/
    /// crater/smoke/embers) IS ported — see Blast.cs — and is driven from here
    /// exactly as src/rockets.js:_impact composes it.
    ///
    /// AUDIO IS NOW WIRED (previously left as "// audio hook" comments, filed as
    /// "audiofx.js, out of scope" — that note predates Core/AudioFx.cs's
    /// ProceduralSfx/FxAudio actually landing). The launch whoosh
    /// (src/rockets.js:99-130 RocketSound.whoosh) is reconstructed from
    /// Core/AudioFx.cs's own generic Burst/Tone primitives at dist=1 (dry, at the
    /// wing — same convention as Combat/GunFx.cs's GunSound.Shoot), not a bespoke
    /// envelope — this is a documented simplification, not a skipped feature.
    /// Downrange detonation()/debrisRain()/collapseRumble() call straight into
    /// Core/AudioFx.cs's already-ported FxAudio.Detonation/DebrisRain/CollapseRumble
    /// (src/audiofx.js), which exist and were simply never called from here.
    ///
    /// Dust and the gun's own decal pool are explicitly CONSOLIDATED onto
    /// ImpactFx's shared Debris/Pits layers rather than re-created locally (the
    /// JS keeps guns.js's `dust`/`holes` around specifically because three.js
    /// pays a draw call per InstancedMesh; Unity's batching makes that
    /// consolidation free, and it is one fewer system to keep in sync).
    /// </summary>
    public sealed class RocketLauncher
    {
        public const float Cooldown = 1.5f;   // s between rockets
        public const float Damage = 260f;     // hp — buildings carry 100-800
        public const float BlastR = 6.5f;     // m — fireball / crater scale
        public const float HoleR = 3.1f;      // m — radius of wall actually torn out
        public const float GlassR = 22f;      // m — every window this close blows out
        public const float Range = 1600f;     // m before a dud self-destructs
        public const float Accel = 340f;      // m/s^2 motor burn
        public const float TopSpeed = 430f;   // m/s
        public const int MaxLive = 6;

        public sealed class Rocket
        {
            public Vector3 Pos;
            public Vector3 Dir;
            public float Speed;
            public float Dist;
            public float Puff; // s since the last trail particle — VFX-only bookkeeping
        }

        public readonly List<Rocket> Live = new List<Rocket>();
        float _cool;
        int _side = 1; // alternates wing hardpoints

        /// <summary>Owned renderer for the propellant trail (src/rockets.js's
        /// RocketTrail) — constructed once, fed from Live every Update() below.
        /// WIRING: previously this class existed with zero callers project-wide
        /// (grep confirmed); it is now self-sufficient and needs no Core/* edit
        /// to render — RocketLauncher itself is already ticked every frame via
        /// Core/CombatWorldAdapter.cs's `RocketTickable(rockets, game)`.</summary>
        readonly RocketTrailFx _trail = new RocketTrailFx();

        /// <summary>Owned renderer for the rocket's own visible body (src/rockets.js:147-187
        /// — see Fx/RocketBodyFx.cs's header for why this was NOT actually asset-blocked:
        /// neither this file nor anything it calls ever loads a model for it). Before this,
        /// a live rocket had no visible body at all — only the trail puffs above.</summary>
        readonly RocketBodyFx _bodyFx = new RocketBodyFx();

        public LayerMask TargetMask = ~0;
        public IDamageWorld World;
        /// <summary>The plane the hero is standing beside is deliberately left off the
        /// normal collidables (see planedamage.js's notes — it would sit on its own
        /// shell), but a rocket fired from it must still be able to hit it.</summary>
        public List<Collider> OwnPlaneColliders;
        public ImpactFx Impacts;
        public BlastFx Blast;
        public Func<float> Rng = () => UnityEngine.Random.value;
        /// <summary>src/rockets.js:290 `g.camera.position.distanceTo(p)` — the
        /// downrange audio's distance term. Null-safe default (0,0,0) so a caller
        /// that never wires this just gets dist-from-origin instead of throwing;
        /// same pattern as Combat/PlaneDamage.cs's own CameraPosition field.</summary>
        public Func<Vector3> CameraPosition = () => Vector3.zero;
        /// <summary>src/rockets.js:219,268 `g.sound.volume ?? 0.7`.</summary>
        public Func<float> MasterVolume = () => 0.7f;

        public event Action<DamageResult> OnHitToast;
        public event Action<float[], float, float> OnGlassBurst;  // flat, 5 per pane

        public float CooldownFrac => Mathf.Max(0f, _cool / Cooldown);

        /// <summary>`plane` is the firing aircraft's own Transform (read-only — Combat
        /// never writes to it). `firing`/`planeSpeed` are whatever Core/Flight's gating
        /// decided (src/rockets.js:197).</summary>
        public void Update(float dt, bool firing, Transform plane, float planeSpeed)
        {
            _cool -= dt;
            if (firing && _cool <= 0f && Live.Count < MaxLive)
            {
                _cool = Cooldown;
                Fire(plane, planeSpeed);
            }
            UpdateRockets(dt);
            // src/rockets.js:250-254 — trail puffs, position-only, cosmetic.
            _trail.FeedRockets(Live, dt);
            _trail.Update(dt);
            _bodyFx.Render(Live); // src/rockets.js:147-187 — the rocket's own visible body
        }

        /// <summary>Releases the owned trail's/body's Materials. Call when this
        /// launcher itself is torn down (matches Fx.RocketTrailFx's/
        /// Fx.RocketBodyFx's own Dispose).</summary>
        public void Dispose() { _trail.Dispose(); _bodyFx.Dispose(); }

        // src/rockets.js:208 _fire
        void Fire(Transform plane, float planeSpeed)
        {
            _side = -_side;
            // HANDEDNESS: forward flips sign (Three -Z -> Unity +Z), so the hardpoint's
            // local z (-0.9, slightly ahead of the wing root) becomes +0.9.
            Vector3 pos = plane.TransformPoint(new Vector3(_side * 1.7f, -0.55f, 0.9f));
            Vector3 dir = plane.forward; // local (0,0,1) through the plane's rotation
            Live.Add(new Rocket
            {
                Pos = pos, Dir = dir,
                Speed = Mathf.Max(planeSpeed + 50f, 130f), // never overtakes its own shot
                Dist = 0f, Puff = 0f,
            });
            Whoosh(MasterVolume());
            // VFX hook: model spawn, skillbar pulse — src/rockets.js:213,220 (rendering/UI)
        }

        // ---- src/rockets.js:99-130 RocketSound.whoosh, promoted to named consts
        // (tools/_probe_destrest.mjs / DestRestProbe.cs) ----
        internal const float WhooshBpFreq0 = 300f;    // :110 bp.frequency.setValueAtTime(300, t)
        internal const float WhooshBpFreq1 = 2400f;   // :111 exponentialRampToValueAtTime(2400, t+0.45)
        internal const float WhooshBpQ = 0.9f;        // :109 bp.Q.value
        internal const float WhooshBpRate = 0.8f;     // :106 src.playbackRate.value
        internal const float WhooshBpGain = 0.6f;     // :114 0.6 * vol
        internal const float WhooshBpDur = 0.75f;     // :118 src.stop(t + 0.75)
        internal const float WhooshToneF0 = 150f;     // :122 o.frequency.setValueAtTime(150, t)
        internal const float WhooshToneF1 = 48f;      // :123 exponentialRampToValueAtTime(48, t+0.12)
        internal const float WhooshToneGain = 0.35f;  // :125 0.35 * vol
        internal const float WhooshToneDur = 0.16f;   // :129 o.stop(t + 0.16)

        /// <summary>src/rockets.js:99-130 RocketSound.whoosh — plays dry (dist=1,
        /// no propagation delay/reverb: "this one happens at the wing"). Built from
        /// Core/AudioFx.cs's generic Burst/Tone primitives rather than a bespoke
        /// envelope — see this class's header for why that is a documented
        /// simplification, not a skipped feature: the motor-ignition bandpass sweep
        /// becomes BurstSweep; the launch-tube bang becomes Tone.</summary>
        static void Whoosh(float vol)
        {
            FxAudio.BurstSweep(1f, vol, dur: WhooshBpDur, freq0: WhooshBpFreq0, freq1: WhooshBpFreq1,
                q: WhooshBpQ, type: FilterKind.Bandpass, gain: WhooshBpGain, rate: WhooshBpRate);
            FxAudio.Tone(1f, vol, dur: WhooshToneDur, f0: WhooshToneF0, f1: WhooshToneF1,
                gain: WhooshToneGain, type: OscKind.Triangle);
        }

        bool RaycastTargets(Ray ray, float maxDist, out RaycastHit hit)
        {
            bool found = Physics.Raycast(ray, out hit, maxDist, TargetMask);
            if (OwnPlaneColliders != null)
            {
                foreach (var c in OwnPlaneColliders)
                {
                    if (c == null) continue;
                    if (c.Raycast(ray, out RaycastHit h2, maxDist) && (!found || h2.distance < hit.distance))
                    { hit = h2; found = true; }
                }
            }
            return found;
        }

        // src/rockets.js:233 _updateRockets
        void UpdateRockets(float dt)
        {
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                var rk = Live[i];
                rk.Speed = Mathf.Min(TopSpeed, rk.Speed + Accel * dt);
                float step = rk.Speed * dt;
                // swept hit test: nothing thinner than one frame's travel tunnels through
                if (RaycastTargets(new Ray(rk.Pos, rk.Dir), step + 1.5f, out RaycastHit hit))
                {
                    Impact(rk, hit);
                    Live.RemoveAt(i);
                    continue;
                }
                rk.Pos += rk.Dir * step;
                rk.Dist += step;
                rk.Puff += dt; // VFX hook: trail.spawn every ~0.016s — src/rockets.js:250-254
                if (rk.Dist > Range) Live.RemoveAt(i); // dud — burned out
            }
        }

        // src/rockets.js:265 _impact
        DamageResult? Impact(Rocket rk, RaycastHit hit)
        {
            Vector3 p = hit.point;
            Vector3 n = hit.normal;
            if (Vector3.Dot(n, rk.Dir) > 0f) n = -n;

            // the damage lands FIRST: if this is the killing shot, the collapse takes
            // over (it blows the whole facade's glass itself) — the blast below just
            // dresses the opening act.
            DamageResult? res = World?.DamageHit(hit, Damage);

            Blast?.Explode(p, n, BlastR);
            Impacts?.Debris.Emit(p, 54, new Color32(0x9a, 0x93, 0x88, 255), spread: 6.5f, size: 0.9f,
                life: 1.6f, grav: 4f, rng: Rng);
            Impacts?.Pits.Stamp(p, n, 6f, 6f, fit: hit.collider, rng: Rng);
            World?.OnShake(0.85f, p.x, p.z);
            // the bang travels: close up it's a crack you feel, from a kilometre out
            // it's a dull thump followed by echoes off the ridges — src/rockets.js:290-292
            float vol = MasterVolume();
            float dist = Vector3.Distance(CameraPosition(), p);
            FxAudio.Detonation(dist, vol, 1f);
            FxAudio.DebrisRain(dist, vol, res != null ? 1.4f : 0.8f); // masonry clattering down after

            // the wall itself: a real hole, right where it hit
            float holeR = HoleR * (0.85f + Rng() * 0.3f);
            object punched = (res?.Rec != null) ? World?.PunchHole(res.Value.Rec, p, holeR) : null;
            float[] ripped = punched != null ? World?.StripPropsAt(p.x, p.y, p.z, holeR * 1.1f) : null;
            if (ripped != null && ripped.Length > 0) OnGlassBurst?.Invoke(ripped, 1.9f, 0f);

            // every pane inside the blast sphere leaves its frame
            var blown = World?.BlastWindows(p.x, p.y, p.z, GlassR);
            if (blown != null && blown.Length > 0) OnGlassBurst?.Invoke(blown, 1.7f, 0f);

            World?.BlastDebris(p.x, p.y, p.z, n, res != null ? 12 : 7);
            if (res == null) return null; // ground / bridge / statue — bang, crater, dirt

            // a fire is left burning in the hole
            World?.IgniteBlast(res.Value, p, BlastR);
            OnHitToast?.Invoke(res.Value);
            if (res.Value.Changed)
            {
                FxAudio.CollapseRumble(dist, vol, res.Value.Destroyed); // src/rockets.js:316
                if (!res.Value.Destroyed)
                {
                    Vector3 plume = new Vector3(res.Value.Cx, res.Value.TopNow, res.Value.Cz);
                    Impacts?.Debris.Emit(plume, 34, new Color32(0x9a, 0x93, 0x88, 255),
                        spread: Mathf.Max(4f, res.Value.Span * 0.6f), size: 1f, life: 2f, grav: 3f, rng: Rng);
                    Blast?.Fx?.Plume(res.Value.Cx, res.Value.TopNow - 2f, res.Value.Cz, res.Value.Span * 0.35f, 4f);
                }
            }
            return res;
        }
    }
}

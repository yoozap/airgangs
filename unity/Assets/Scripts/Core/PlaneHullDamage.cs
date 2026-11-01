using System;
using UnityEngine;

namespace TbilisiFly.Core
{
    // =========================================================================
    // PlaneHullDamage.cs — port of the PLAYER'S OWN airframe damage/health
    // bookkeeping out of src/game.js's `Game` class. Everything here is state
    // that lived directly on `this` in the JS (health, struct, wingDmg) plus
    // the methods that mutate it:
    //   _updateHealthBar   game.js:2191-2215
    //   _crashDamage       game.js:2216-2218
    //   _dentHull          game.js:2231-2239
    //   _applyDamage       game.js:2240-2244
    //   _explode           game.js:2249-2267
    //   _showWasted        game.js:2269-2277
    //   _collapseGear      game.js:1817-1825
    //   _shearWing         game.js:1826-1837
    //   _shearTail         game.js:1838-1851
    //   _damageWing        game.js:2278-2299
    //   _repairWings       game.js:2301-2320
    //   _wingSmokePos      game.js:2321-2327
    //   _wingTip           game.js:2328-2335
    //
    // ============================ WHY THIS EXISTS ============================
    // Flight/PlanePhysics.cs (src/planephys.js, already ported) explicitly
    // documents that it computes a RamResult and "the caller applies dmgUs/
    // pilot wing-tear to the PLAYER's own state (g._applyDamage/_shearWing/
    // _damageWing are Core/Combat territory the player's own side, not this
    // file's)" — this class IS that territory. Likewise Combat/CrashDents.cs
    // (src/dents.js) is called FROM here (DentHull), not duplicated.
    //
    // ============================== DESIGN ==============================
    // This is a plain C# class, not a MonoBehaviour — no Transform, no scene
    // dependency beyond what is handed in per-call. Every externally-visible
    // effect the JS methods had (sound, hint text, breakup/detach, dents,
    // smoke-clear, the HUD bar) is an `Action` hook instead of a direct call,
    // because those systems belong to GameLoop.cs/CombatWorldAdapter.cs (both
    // locked for this port session) and whatever other agent owns Smoke/
    // Breakup. WIRING NEEDED (see this port's final report): construct one
    // instance per active plane, subscribe the hooks to FxAudio/AirframeBreakup/
    // SmokeTrail-equivalent/OnHint, and call ResetAll() from ResetPlane().
    //
    // Gear is NOT on Flight.Airframe (aero.js/FlightModel.cs never reads it —
    // only ground-roll/landing logic does), so PlaneStructState wraps the
    // existing Airframe (WingL/WingR/Tail, fed straight to FlightModel.Update)
    // plus a sibling Gear field, instead of duplicating WingL/WingR/Tail.
    // =========================================================================

    /// <summary>game.js:112 `this.struct` (`{ wingL, wingR, tail, gear }`).
    /// `Air` is handed straight to <see cref="TbilisiFly.Flight.FlightModel.Update"/>
    /// unchanged; `Gear` exists only for this file's and PlaneGroundRoll.cs's
    /// own ground-contact logic.</summary>
    public struct PlaneStructState
    {
        public TbilisiFly.Flight.Airframe Air;
        public float Gear; // 1 intact, 0 collapsed (JS stores it as a number, used truthily)
        public static PlaneStructState Intact =>
            new PlaneStructState { Air = TbilisiFly.Flight.Airframe.Intact, Gear = 1f };
    }

    /// <summary>game.js:2191-2206's three colour bands, carried as data instead
    /// of a DOM gradient string — a future HUD view picks the colour.</summary>
    public enum HealthBand { Good, Warn, Critical }

    public sealed class PlaneHullDamage
    {
        public const float MaxHealth = 1000f; // game.js:76 MAX_HEALTH

        // ---- promoted literals (tools/_probe_gamerest.mjs / GameRestProbe.cs) ----
        internal const float HealthBandGood = 0.55f;  // game.js:2203 `p > 0.55`
        internal const float HealthBandWarn = 0.25f;  // game.js:2205 `p > 0.25`
        internal const float CollapseGearDmg = 90f;   // game.js:1819 `_applyDamage(90)`
        internal const float ShearWingDmg = 280f;     // game.js:1836 `_applyDamage(280)`
        internal const float ShearTailDmg = 240f;     // game.js:1847 `_applyDamage(240)`
        internal const float WingDamageCap = 0.65f;   // game.js:2293 `Math.min(this.struct[part], 0.65)`
        /// <summary>game.js:2295 `wing.mesh.rotation.z += side === 'right' ? -0.16 : 0.16` —
        /// the wingtip-droop visual. Not wired here: bending the actual wing
        /// Mesh is OnWingBend's caller's job (this class owns no Mesh/Material,
        /// see the hook's own doc comment above), but the number is measured
        /// from the same source and belongs next to its siblings so a future
        /// OnWingBend implementation has it to hand rather than re-measuring.</summary>
        internal const float WingDroopRad = 0.16f;

        public float Health = MaxHealth;
        public bool Crashed;
        public bool WingDmgLeft, WingDmgRight; // game.js:117 this.wingDmg
        public PlaneStructState Struct = PlaneStructState.Intact;

        /// <summary>planeColMult(def) for the ACTIVE airframe (game.js:2217,
        /// planes.js planeColMult) — the caller refreshes this on
        /// ResetPlane()/swapToPlane() from PlaneCatalog, same as the JS reads
        /// `this.def` fresh each call.</summary>
        public float ColMult = 1f;

        // ---- hooks the caller wires; every one is optional (JS `?.()`) ----
        public Action<float, HealthBand> OnHealthChanged;         // -> HUD bar (game.js:2191-2215)
        public Action<string> OnHint;                              // game.js _setHint
        public Action<float> OnExplosionSound;                     // sound.explosion(vol)
        /// <summary>part: "wingL"/"wingR"/"tail", spin, burn — src/breakup.js detach().</summary>
        public Action<string, float, bool> OnDetach;
        public Action OnSmokeClear;                                 // smoke.clear() (game.js:2316)
        /// <summary>point, into, energy(J), mult — forwards straight to
        /// Combat/CrashDents.cs's MetalDents.Crush(plane, point, into, energy, mult).
        /// The caller supplies `plane` (the Transform) since this class holds none.</summary>
        public Action<Vector3, Vector3, float, float> OnDentHull;
        public Action<Vector3, float> OnCrashScatter;               // breakup.crashScatter(baseVel, severity)
        public Action OnEngineKill;                                 // sound.kill()
        public Action OnSaveDeath;                                  // saveState(true, 'death')
        public Action OnShowWasted;                                 // container.classList.add('wasted')
        /// <summary>side: "left"/"right" — droop the wingtip mesh + char the
        /// material (game.js:2285-2295). Visual-only; this class never touches
        /// a Mesh/Material.</summary>
        public Action<string> OnWingBend;
        public Action<string> OnWingRepair;                        // restore droop/colour (game.js:2307-2314)

        /// <summary>13th hook, added by the pass that finally CONSTRUCTS one of
        /// these per plane (see this file's own "WIRING NEEDED" note above):
        /// game.js:2260 `this.crash = new CrashSequence(...)` inside `_explode`
        /// is also where the JS's `this.crashed` flag (already mirrored by this
        /// class's own `Crashed` field) takes effect on the GAME's mode, not
        /// just this airframe's health state. GameLoop.cs owns mode switching
        /// (Flying -&gt; Crashed) and has no other way to learn that a wall hit,
        /// a ram, or a touchdown — not just CheckStructuralStress's G-break/VNE
        /// path — just destroyed the active plane. Fired once, right where
        /// CrashSequence construction happens in the JS (before OnEngineKill).</summary>
        public Action OnCrashed;

        /// <summary>game.js:2191-2215 `_updateHealthBar`.</summary>
        public void UpdateHealthBar()
        {
            float p = Mathf.Clamp01(Health / MaxHealth);
            var band = p > HealthBandGood ? HealthBand.Good : p > HealthBandWarn ? HealthBand.Warn : HealthBand.Critical;
            OnHealthChanged?.Invoke(p, band);
        }

        /// <summary>game.js:2216-2218 `_crashDamage` — the collision-multiplier
        /// path; gunfire/over-G go straight through ApplyDamage (game.js:2214's
        /// own comment).</summary>
        public void CrashDamage(float dmg, Vector3? hitNormal = null) => ApplyDamage(dmg * ColMult, hitNormal);

        /// <summary>game.js:2240-2244 `_applyDamage`.</summary>
        public void ApplyDamage(float dmg, Vector3? hitNormal = null)
        {
            Health = Mathf.Max(0f, Health - dmg);
            UpdateHealthBar();
            if (Health <= 0f) Explode(hitNormal);
        }

        /// <summary>game.js:2231-2239 `_dentHull`. `mass` is the caller's
        /// FlightModel.Mass (aero.mass in the JS) — this class does not hold a
        /// FlightModel reference on purpose (constructed before one may exist).</summary>
        public void DentHull(Vector3 point, Vector3 into, float speed, float headOn, float mass)
        {
            float v = Mathf.Abs(speed) * Mathf.Clamp01(headOn);
            if (v < 4f) return;
            float energy = 0.5f * mass * v * v;
            OnDentHull?.Invoke(point, into, energy, ColMult);
        }

        /// <summary>game.js:2249-2267 `_explode`. `airborne`/`vel`/`speed`/
        /// `forward` are read fresh off the caller each call, exactly as the JS
        /// reads `this.airborne`/`this.vel`/`this.speed` — this class owns no
        /// Transform or velocity of its own.</summary>
        public void Explode(Vector3? hitNormal, bool airborne = false, Vector3 vel = default,
                             float speed = 0f, Vector3 forward = default)
        {
            if (Crashed) return;
            Health = 0f;
            UpdateHealthBar();
            Crashed = true;
            Vector3 baseVel = airborne ? vel : forward * speed;
            float severity = Mathf.Clamp(baseVel.magnitude / 110f, 0.5f, 1.3f);
            OnCrashScatter?.Invoke(baseVel, severity);
            Struct.Air.WingL = Struct.Air.WingR = Struct.Air.Tail = 0f;
            // game.js:2260 `this.crash = new CrashSequence(...)` is GameLoop's
            // CrashFactory (already wired — CombatWorldAdapter note) — reached
            // via OnCrashed, since flipping GameLoop's own Mode is this hook's
            // whole job (see OnCrashed's own doc comment above).
            OnCrashed?.Invoke();
            OnEngineKill?.Invoke();
            OnExplosionSound?.Invoke(1f);
            OnSaveDeath?.Invoke();
        }

        bool _wastedShown;
        /// <summary>game.js:2269-2277 `_showWasted`. Idempotent per crash —
        /// call ResetAll() to arm it again for the next flight.</summary>
        public void ShowWasted()
        {
            if (_wastedShown) return;
            _wastedShown = true;
            OnShowWasted?.Invoke();
        }

        /// <summary>game.js:1817-1825 `_collapseGear`.</summary>
        public void CollapseGear()
        {
            Struct.Gear = 0f;
            ApplyDamage(CollapseGearDmg);
            OnExplosionSound?.Invoke(0.3f);
            OnHint?.Invoke("\U0001F4A5 შასი ჩაიმტვრა — მუცელზე ხარ, მაგრამ აფრენა მაინც შეგიძლია!");
        }

        /// <summary>game.js:1826-1837 `_shearWing`. `side` is "left"/"right".</summary>
        public void ShearWing(string side, string msg)
        {
            bool left = side == "left";
            float cur = left ? Struct.Air.WingL : Struct.Air.WingR;
            if (cur == 0f) return;
            if (left) Struct.Air.WingL = 0f; else Struct.Air.WingR = 0f;
            if (left) WingDmgLeft = true; else WingDmgRight = true;
            OnDetach?.Invoke(left ? "wingL" : "wingR", 8f, UnityEngine.Random.value < 0.6f);
            OnExplosionSound?.Invoke(0.5f);
            ApplyDamage(ShearWingDmg);
            if (!Crashed) OnHint?.Invoke($"\U0001F4A5 {msg}!");
        }

        /// <summary>game.js:1838-1846 `_shearTail`.</summary>
        public void ShearTail(string msg)
        {
            if (Struct.Air.Tail == 0f) return;
            Struct.Air.Tail = 0f;
            OnDetach?.Invoke("tail", 6f, false);
            OnExplosionSound?.Invoke(0.5f);
            ApplyDamage(ShearTailDmg);
            if (!Crashed) OnHint?.Invoke($"\U0001F4A5 {msg} — მართვა დაიკარგა!");
        }

        /// <summary>game.js:2278-2299 `_damageWing`. `side` is "left"/"right".</summary>
        public void DamageWing(string side, float dmg)
        {
            CrashDamage(dmg);
            bool left = side == "left";
            bool already = left ? WingDmgLeft : WingDmgRight;
            if (Crashed || already) return;
            if (left) WingDmgLeft = true; else WingDmgRight = true;
            // A bent wing carries less lift and drags (game.js:2284).
            if (left) Struct.Air.WingL = Mathf.Min(Struct.Air.WingL, WingDamageCap);
            else Struct.Air.WingR = Mathf.Min(Struct.Air.WingR, WingDamageCap);
            OnWingBend?.Invoke(side);
            OnHint?.Invoke(left
                ? "\U0001F4A5 მარცხენა ფრთა დაზიანდა — მართვა ჭირს!"
                : "\U0001F4A5 მარჯვენა ფრთა დაზიანდა — მართვა ჭირს!");
        }

        /// <summary>game.js:2301-2320 `_repairWings` (plus the smoke-clear tail
        /// of resetPlane that always follows it — game.js:744-745).</summary>
        public void RepairWings()
        {
            if (WingDmgLeft) { WingDmgLeft = false; OnWingRepair?.Invoke("left"); }
            if (WingDmgRight) { WingDmgRight = false; OnWingRepair?.Invoke("right"); }
            OnSmokeClear?.Invoke();
        }

        /// <summary>game.js:2321-2327 `_wingSmokePos`.</summary>
        public Vector3 WingSmokePos(string side, Transform plane, float halfSpan, Func<string, Transform> wingTipBone)
        {
            Vector3 tip = WingTip(side, plane, halfSpan, wingTipBone);
            bool gone = (side == "left" ? Struct.Air.WingL : Struct.Air.WingR) == 0f;
            return gone ? Vector3.Lerp(plane.position, tip, 0.3f) : tip;
        }

        /// <summary>game.js:2328-2335 `_wingTip`. `wingTipBone` resolves
        /// plane.userData.wings[side].tip — a named child Transform if the
        /// airframe prefab carries one, else the measured-halfSpan fallback
        /// (unaffected by the X-mirror: right/up are shared between the two
        /// frames, only forward's label flips — FlightModel.cs's header).</summary>
        public Vector3 WingTip(string side, Transform plane, float halfSpan, Func<string, Transform> wingTipBone)
        {
            Transform t = wingTipBone?.Invoke(side);
            if (t != null) return t.position;
            Vector3 right = plane.right;
            float d = halfSpan * 0.93f;
            return plane.position + right * (side == "right" ? d : -d);
        }

        /// <summary>The damage-side slice of game.js:724-760 `resetPlane` —
        /// airframe clean, full tank, wings un-bent. Position/camera/engine
        /// are GameLoop's own job.</summary>
        public void ResetAll()
        {
            Crashed = false;
            _wastedShown = false;
            Struct = PlaneStructState.Intact;
            RepairWings(); // clears WingDmgLeft/Right + fires OnWingRepair/OnSmokeClear
            Health = MaxHealth;
            UpdateHealthBar();
        }
    }
}

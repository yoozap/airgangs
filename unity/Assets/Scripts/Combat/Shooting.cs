using System;
using UnityEngine;
using TbilisiFly.Fx;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// Port of src/shooting.js's Pistol — the on-foot sidearm/rifle control:
    /// PUBG-style recoil control + cone + sway, the real magazine, the zero
    /// (sights laid a hair above the line of sight so point-of-aim meets
    /// point-of-impact at 25 m), and the burst pattern (rounds 2-5 kick
    /// hardest, a random-walk sideways drift, not jitter).
    ///
    /// SCOPE SEPARATION FROM THE JS: src/shooting.js reaches directly into
    /// `g.foot`, `g.camera`, `g.look.pitch/yaw` and `g.foot.held.obj` (a
    /// Transform it rotates/translates by hand every frame). None of those are
    /// Combat's to own. So:
    ///   * Fire()/Update() take `ref float lookPitch`/`ref float lookYaw` —
    ///     Combat computes the kick and writes it into whatever the caller
    ///     passed in, same effect as the JS's direct `g.look.pitch = ...`,
    ///     without Combat holding a reference to Core's camera state.
    ///   * The weapon-model kick-back/muzzle-kick that the JS applies with
    ///     `obj.translateX(...)`/`obj.rotateZ(...)` on the held weapon's own
    ///     Transform becomes two CONTINUOUSLY-MAINTAINED public fields
    ///     (KickBackOffset, KickRotationZ) for the Character system to apply —
    ///     Combat never touches a weapon's Transform directly.
    ///   * Everything genuinely about the shot's own ballistics is unchanged:
    ///     this class spawns real rounds through Ballistics.cs's BulletSim.
    /// Casing ejection visuals, WebAudio (gunReport/dryFire) and the DOM
    /// reticle (+ flashTexture()'s painted canvas gradient) are VFX/UI, out of
    /// scope — see OnEjectCase/OnPlayClip/OnDryFire, the hooks where those
    /// would plug in. The muzzle flash's OWN timing/size, however, is a real
    /// measured formula (src/shooting.js:366-371, scaled by the cartridge's
    /// muzzle energy, not art) and IS ported below as FlashVisible/FlashScale/
    /// FlashRotation — three more continuously-maintained public fields, same
    /// pattern as KickBackOffset/KickRotationZ, for a Fx consumer (e.g.
    /// Fx/MuzzleFlashFx.cs, already built for Combat/Guns.cs's belly gun) to
    /// read without Combat touching a Transform or a Sprite itself.
    /// </summary>
    /// <summary>src/shooting.js:162 aimDir + src/shooting.js:306-326's aimPoint
    /// resolution — WHERE THE CROSSHAIR IS, as a muzzle-to-target direction. Ported
    /// as a free function (not a Pistol method) since it only needs a Camera, the
    /// NDC crosshair position and the muzzle — none of which Pistol has to hold a
    /// reference to between calls.</summary>
    public static class AimResolver
    {
        public const float CamBack = 8f; // m — the chase camera sits this far behind the hero

        /// <summary>`cinemaAimPoint` — a director's exact aim point (src/cinema.js, out of
        /// scope), when one is playing. `blindAimDir` — foot.js's cover blind-fire
        /// override: straight down the arm's line, there is no sight picture.</summary>
        public static Vector3 ResolveFireDirection(Camera cam, float ndcX, float ndcY, float range,
            Vector3 muzzle, Vector3? cinemaAimPoint, Vector3? blindAimDir, LayerMask targetMask)
        {
            Vector3? aimPoint = cinemaAimPoint;
            if (!aimPoint.HasValue && blindAimDir.HasValue)
                aimPoint = muzzle + blindAimDir.Value.normalized * range;
            if (!aimPoint.HasValue)
            {
                Ray ray = cam.ViewportPointToRay(new Vector3(ndcX * 0.5f + 0.5f, ndcY * 0.5f + 0.5f, 0f));
                aimPoint = Physics.Raycast(ray, out RaycastHit hit, range + CamBack, targetMask)
                    ? hit.point
                    : ray.origin + ray.direction * (range + CamBack);
            }
            return (aimPoint.Value - muzzle).normalized;
        }
    }

    public sealed class Pistol
    {
        // src/shooting.js:25-60
        public const float Range = 220f;
        public const float HipSpread = 0.045f;
        public const float AimSpread = 0.004f;
        public const float BloomPerShotDefault = 0.011f;
        public const float BloomMax = 0.05f;
        public const float BloomCool = 0.055f;
        public const float AimToggleMs = 320f;
        public const float Zero = 25f;
        public const float RecoilBack = 14f;
        public const float KickBackDecay = 9f;
        public const float BurstGap = 0.32f;
        public const float MoveSpread = 0.032f;
        public const float CrouchCone = 0.7f;
        public const float SwayAmp = 0.0022f;
        public const float SwayMove = 2.4f;
        public const float ShakeAmp = 0.006f;
        public const float ShakeTau = 0.06f;
        public const float BlindSpread = 0.075f;
        public const float KickbBackDecay = 20f;
        public const float FlashDuration = 0.045f; // src/shooting.js:31 FLASH_T

        public BulletSim Bullets;             // Combat's own hand-rolled sim — real integration
        public Func<float> Rng = () => UnityEngine.Random.value;
        /// <summary>Clip duration lookup, once GunAnim.cs is wired (null -> JS's fallback
        /// constants 2.95s/1.98s empty/tactical reload, 0.5s shot).</summary>
        public Func<string, float> ClipTime;
        public Action<string> OnPlayClip;     // arms/gun clip name to play ("aimFire", "reload", ...)
        /// <summary>src/shooting.js:425 — the HELD WEAPON MODEL's own clip ("fire"/
        /// "fireLast"), played unconditionally on every shot regardless of whether the
        /// arms gunAnim system is ready (unlike OnPlayClip's "aimFire"/"hipFire"/
        /// "fireLast", which only fires when req.ClipShotAvailable is true). Distinct
        /// hook because the JS calls both `ga.play(arms,...)` AND, separately and always,
        /// `g.foot.held.obj?.userData?.anim?.play(...)` on the weapon's own object.</summary>
        public Action<string> OnPlayWeaponClip;
        public Action OnDryFire;              // trigger pulled on an empty mag
        /// <summary>Wired once ImpactFx.cs's Casings pool is available — EjectCase()
        /// below computes the real velocity and calls Impacts.Casings.Eject directly;
        /// this is for anything ELSE that wants to know a case was thrown (none yet).</summary>
        public Action<Cartridge> OnEjectCase;
        public ImpactFx Impacts;
        /// <summary>src/shooting.js:382 gunReport — the shot's own report, out of Combat's
        /// scope (WebAudio) but given a hook like OnDryFire so a real caller can wire it;
        /// previously the only audio cue in Fire() with no hook at all.</summary>
        public Action<Cartridge> OnGunReport;

        // ---- live state, 1:1 with the JS instance fields ----
        public float? Ammo;                   // null = weapon not drawn yet
        float _cool;
        float _bloom;
        float _recoilAccum;                   // rad of rise still owed back to the camera
        float _kick;                          // legacy (no-clip) muzzle-kick-on-the-model magnitude
        float _reload, _reloadFrom;
        public string ReloadName { get; private set; }
        float _fire, _fireFrom;
        bool _trigger;
        int _burst;
        float _lastShot = -1e9f;
        float _drift;
        float _kickB;
        public float Rock;                    // rad the torso is still rocked back (rifles)
        float _shake;
        float _t;
        public float SwayYaw, SwayPitch;
        public bool Aiming { get; private set; }
        float _toggledAt = -1e9f;
        string _lastCart;
        readonly System.Collections.Generic.List<(float at, string cue)> _cues = new();

        /// <summary>Continuously-maintained — Character applies these to the held
        /// weapon's local Transform every frame (src/shooting.js:489-497, 526-534).</summary>
        public float KickBackOffset { get; private set; } // m, along the weapon's own -bore
        public float KickRotationZ { get; private set; }  // rad, about the weapon's own bore

        // src/shooting.js:366-371, :474-476 — muzzle flash. A pure measured
        // formula (powder burnt = cartridge energy), not art: a 9x19 flash is
        // visibly smaller than a 12.7x108 one. FlashRotation is a one-shot
        // random spin (src/shooting.js:369 material.rotation = random()*2π) so
        // no two flashes look alike, same trick as guns.js's DecalLayer.stamp.
        float _flashT;
        public bool FlashVisible => _flashT > 0f;
        public float FlashScale { get; private set; }
        public float FlashRotation { get; private set; }
        /// <summary>The weapon Transform the flash was drawn against, captured
        /// straight off FireRequest.HeldWeapon/HeldWeaponDef at the moment of the
        /// shot that is still decaying — the same reference Fire() below already
        /// receives from whatever calls it (Character/Core's own FireRequest
        /// construction), so Fx/OnFootMuzzleFlashFx.cs needs no NEW Character/Core
        /// edit to read a live, moving weapon Transform every frame: this class
        /// already gets handed one on every shot, it previously just discarded it
        /// once EjectCase() returned.</summary>
        public Transform FlashHeldWeapon { get; private set; }
        public WeaponDef FlashHeldWeaponDef { get; private set; }
        /// <summary>Owned renderer for FlashVisible/FlashScale/FlashRotation above
        /// (Fx/OnFootMuzzleFlashFx.cs) — same self-contained-field pattern as
        /// Combat/Guns.cs's _muzzleFx. Ticked from this class's own Update(dt, ...)
        /// below, which Core already calls every frame, so this renders a real
        /// flash today with no Core/Character edit at all.</summary>
        readonly OnFootMuzzleFlashFx _flashFx = new OnFootMuzzleFlashFx();

        public static Cartridge Cart(string id) =>
            (id != null && Cartridges.ById.TryGetValue(id, out var c)) ? c : Cartridges.ById["9x19"];

        public static int MagSize(string cartId) => Cart(cartId).Mag;
        public bool Reloading => _reload > 0f;

        /// <summary>0..1 of the reload clip to show — fast in, slow back off (src/shooting.js:218).</summary>
        public float ReloadBlend
        {
            get
            {
                if (_reload <= 0f) return 0f;
                const float IN = 0.18f, OUT = 0.24f;
                return Mathf.Min(1f, Mathf.Min((_reloadFrom - _reload) / IN, _reload / OUT));
            }
        }

        /// <summary>0..1 of the shot's recoil clip to show — fast in, slow back off (src/shooting.js:228).</summary>
        public float FireBlend
        {
            get
            {
                if (_fire <= 0f) return 0f;
                const float IN = 0.03f, OUT = 0.16f;
                return Mathf.Min(1f, Mathf.Min((_fireFrom - _fire) / IN, _fire / OUT));
            }
        }

        public void SetAiming(bool on) => Aiming = on;

        /// <summary>src/shooting.js:172 setVisible(false)'s reset — holstering drops the
        /// aim, the bloom cone, and (src/shooting.js:188 `this.flash.visible = false`)
        /// snaps any muzzle flash still decaying out immediately rather than letting it
        /// finish its FlashDuration after the weapon is already put away. (The
        /// pointer-lock/cursor/DOM half of setVisible is UI/input territory, not ported
        /// here.)</summary>
        public void OnHolster()
        {
            Aiming = false;
            _bloom = 0f;
            _flashT = 0f;
        }

        public void ToggleAim(float nowMs)
        {
            if (nowMs - _toggledAt < AimToggleMs) return;
            _toggledAt = nowMs;
            SetAiming(!Aiming);
        }

        /// <summary>src/shooting.js:265 — the hint-bar text `reload()` shows when it
        /// was not an auto-reload (`g._setHint?.(...)`), by whether the magazine was
        /// run dry (ends with a slide release) or swapped with a round still
        /// chambered. Georgian only, byte-for-byte, same convention as
        /// Combat/RangeLane.cs's RangeLanes.HintText and Combat/GunFx.cs's
        /// GunToast.Format — pure text selection, no DOM, so a UI/HUD consumer calls
        /// this only when it actually needs the string.</summary>
        public static string ReloadHintText(bool dry) =>
            dry ? "🔄 გადატენვა — ჩამკეტი უკან" : "🔄 გადატენვა";

        // src/shooting.js:240 reload. `spec` is GunAnim.cs's own GunAnimSpec (its
        // `.Cues[name]` is src/shooting.js:258's `g.gunAnim?.spec?.cues?.[name]`) —
        // pass null when no GunAnim is wired yet (e.g. weapon has no animation file)
        // to get the hand-written fallback table (src/shooting.js:261), same as the
        // JS's `:` branch. Closes the gap this file's own prior note left open.
        public bool Reload(string cartId, bool auto = false, GunAnimSpec spec = null)
        {
            int mag = MagSize(cartId);
            if (mag == 0 || _reload > 0f || (Ammo ?? 0f) >= mag) return false;
            bool dry = (Ammo ?? 0f) <= 0f;
            string name = dry ? "reloadEmpty" : "reload";
            float t = ClipTime?.Invoke(name) ?? (dry ? 2.95f : 1.98f);
            if (t <= 0f) t = dry ? 2.95f : 1.98f;
            _reload = t; _reloadFrom = t; ReloadName = name;
            _cues.Clear();
            GunCue[] specCues = (spec?.Cues != null && spec.Cues.TryGetValue(name, out var sc)) ? sc : null;
            if (specCues != null && specCues.Length > 0)
            {
                // src/shooting.js:259-260 `cues.map(([at, fn]) => [at, CUE_FN[fn] ?? magIn])`
                foreach (var c in specCues) _cues.Add((c.At, c.Fn ?? "magIn"));
            }
            else if (dry) { _cues.Add((0.93f, "slide")); _cues.Add((0.60f, "magIn")); _cues.Add((0.15f, "magOut")); }
            else { _cues.Add((0.67f, "magIn")); _cues.Add((0.17f, "magOut")); }
            _fire = 0f;
            OnPlayClip?.Invoke(name);
            return true;
        }

        public void Trigger(bool down) => _trigger = down;

        /// <summary>src/shooting.js:471 — the automatic-weapon re-fire. Call this every
        /// frame (with the SAME FireRequest a manual shot would use — recomputed fresh,
        /// since the aim/camera may have moved); it is a no-op unless the trigger is
        /// down, the cartridge is auto, the action has cycled and nothing else is busy.</summary>
        public bool TryAutoFire(FireRequest req, ref float lookPitch, ref float lookYaw)
        {
            var cart = Cart(req.CartridgeId);
            if (_trigger && cart.Auto && _cool <= 0f && _reload <= 0f && req.WeaponHeld)
                return Fire(req, ref lookPitch, ref lookYaw);
            return false;
        }

        public struct FireRequest
        {
            public bool WeaponHeld;
            public string CartridgeId;
            public bool InCover;
            public Func<bool> CoverCanFire;
            public bool Blind;             // foot.js aimOverride is set
            public Vector3 Muzzle;         // world-space muzzle point
            public Vector3 AimDir;         // resolved aim ray direction (camera/cinema/blind)
            public float MoveFrac;         // |foot.speed| / runSpeed, clamped by the caller
            public bool Crouched;
            public bool ClipShotAvailable; // g.gunAnim?.ready
            /// <summary>The held weapon's own Transform + measured def, for EjectCase's
            /// real port-axis ejection velocity (src/shooting.js:431-444). Either may be
            /// left null (e.g. no model loaded yet) — the shot still fires, just silently.</summary>
            public Transform HeldWeapon;
            public WeaponDef HeldWeaponDef;
            /// <summary>GunAnim.cs's own spec (Pistol/Rifle), passed through to a dry-fire
            /// auto-reload so it gets the REAL per-weapon cue table, not the fallback
            /// (src/shooting.js:258). Null is fine — see Reload()'s own doc.</summary>
            public GunAnimSpec ReloadSpec;
        }

        // src/shooting.js:431 _ejectCase — the spent case, out of the ejection port,
        // up and back, at the weapon's OWN measured port and axes (so a 93 mm PM
        // barrel and a 125 mm 92FS throw brass from the right place on each).
        void EjectCase(Cartridge cart, Transform held, WeaponDef def)
        {
            if (Impacts == null) return;
            MeasureOnceIfNeeded(def, held);
            Vector3 port = held.TransformPoint(def.Port);
            Vector3 right = held.TransformDirection(new Vector3(0, 0, 1));
            Vector3 up = held.TransformDirection(new Vector3(0, 1, 0));
            Vector3 back = held.TransformDirection(new Vector3(-1, 0, 0));
            Vector3 vel = right * (2.2f + Rng() * 1.1f) + up * (1.9f + Rng() * 0.9f) + back * (0.5f + Rng() * 0.6f);
            Impacts.Casings.Eject(port, vel, cart.CaseMm);
        }

        static void MeasureOnceIfNeeded(WeaponDef def, Transform held)
        {
            if (def.Measured || held == null) return;
            Weapons.MeasureOnce(def, held.gameObject);
        }

        /// <summary>src/shooting.js:449-456 _muzzle — where the barrel ends, in
        /// world space, off the weapon's OWN measured bounding box (Weapons.
        /// MeasureOnce records it when the gun is first drawn), so each
        /// weapon's flash/bullet start at ITS muzzle rather than a guessed
        /// offset. Falls back to the camera position with no weapon held,
        /// same as the JS (`this.game.camera.position.clone()`).</summary>
        public static Vector3 Muzzle(Transform held, WeaponDef def, Camera fallbackCam)
        {
            if (held == null) return fallbackCam != null ? fallbackCam.transform.position : Vector3.zero;
            MeasureOnceIfNeeded(def, held);
            Vector3 m = def?.Muzzle ?? new Vector3(0.16f, 0f, 0f); // shooting.js:454 default
            return held.TransformPoint(m);
        }

        /// <summary>src/shooting.js:458-464 _port — the ejection port, same
        /// measured-offset pattern as <see cref="Muzzle"/>.</summary>
        public static Vector3 PortPoint(Transform held, WeaponDef def, Camera fallbackCam)
        {
            if (held == null) return fallbackCam != null ? fallbackCam.transform.position : Vector3.zero;
            Vector3 p = def?.Port ?? new Vector3(0.02f, 0.05f, 0.012f); // shooting.js:462 default
            return held.TransformPoint(p);
        }

        // src/shooting.js:275 fire — one trigger pull.
        public bool Fire(FireRequest req, ref float lookPitch, ref float lookYaw)
        {
            if (!req.WeaponHeld) return false;
            var cart = Cart(req.CartridgeId);
            if (_reload > 0f) return false;
            if (_cool > 0f) return false;
            if (req.InCover && req.CoverCanFire != null && !req.CoverCanFire()) return false;

            int mag = MagSize(req.CartridgeId);
            if (mag > 0 && (Ammo ?? 0f) <= 0f)
            {
                OnDryFire?.Invoke();
                _trigger = false; // an automatic does not auto-cycle a reload
                Reload(req.CartridgeId, true, req.ReloadSpec);
                return false;
            }
            _cool = cart.Rate;

            Vector3 dir = req.AimDir.normalized;
            // ZERO: the barrel is laid a hair above the line of sight so point-of-aim
            // meets point-of-impact at 25 m (without this every shot lands low).
            float drop = (9.81f * Zero) / (2f * cart.V0 * cart.V0);
            dir.y += drop;

            float moving = Mathf.Clamp01(req.MoveFrac);
            float stance = req.Crouched ? CrouchCone : 1f;
            float spread = ((Aiming ? AimSpread : HipSpread) + _bloom + MoveSpread * moving) * stance
                + (req.Blind ? BlindSpread : 0f);
            float a = Rng() * Mathf.PI * 2f;
            float r = Mathf.Sqrt(Rng()) * spread;
            // Arbitrary orthonormal frame around `dir` for isotropic disk sampling.
            // HANDEDNESS: the JS builds right = cross(dir, worldUp), up = cross(right, dir);
            // operands SWAPPED here per GeoFrame's rule. Functionally inert either way —
            // uniform sampling on a disk is rotation-symmetric — but audited anyway.
            Vector3 worldUp = Vector3.up;
            Vector3 right = Vector3.Cross(worldUp, dir).normalized;
            Vector3 up = Vector3.Cross(dir, right).normalized;
            dir += right * (Mathf.Cos(a) * r) + up * (Mathf.Sin(a) * r);
            dir.Normalize();
            _bloom = Mathf.Min(BloomMax, _bloom + (cart.Bloom > 0f ? cart.Bloom : BloomPerShotDefault));

            if (_t - _lastShot > BurstGap) { _burst = 0; _drift = 0f; }
            _lastShot = _t;
            _burst++;

            Bullets?.Spawn(cart, req.Muzzle, dir, "pistol");
            // src/shooting.js:373-379 — powder smoke drifting off the muzzle. Not
            // pure art: it rides the SAME hand-rolled particle integration
            // (grav/drag) as every other ImpactFx.Debris emission, so it is a direct
            // call into Combat's already-held Impacts field, same as EjectCase below,
            // rather than routed through an Fx hook.
            if (Impacts != null)
            {
                Impacts.Debris.Emit(req.Muzzle, 4, new Color32(0x8e, 0x8b, 0x86, 255),
                    speed: 1.6f, spread: 0.02f, up: 0.5f, size: 0.05f, life: 0.55f,
                    grav: -0.6f, drag: 3.4f, dir: dir, cone: 0.7f, rng: Rng);
            }
            if (req.HeldWeapon != null && req.HeldWeaponDef != null) EjectCase(cart, req.HeldWeapon, req.HeldWeaponDef);
            OnEjectCase?.Invoke(cart);
            OnGunReport?.Invoke(cart); // src/shooting.js:382 gunReport(cart, volume)

            // src/shooting.js:366-371 — muzzle flash, scaled by how much powder
            // the cartridge actually burns.
            FlashRotation = Rng() * Mathf.PI * 2f;
            FlashScale = Mathf.Min(0.8f, 0.26f + 0.2f * (cart.Energy / 580f)) * (0.8f + Rng() * 0.5f);
            _flashT = FlashDuration;
            FlashHeldWeapon = req.HeldWeapon;
            FlashHeldWeaponDef = req.HeldWeaponDef;

            // RECOIL. With a clip loaded, the clip IS the recoil visually — the
            // procedural weapon-kick and yaw jitter are switched off and only the
            // camera climb (a feel) remains; see src/shooting.js:384-410.
            bool clipShot = req.ClipShotAvailable;
            int n = _burst;
            float pat = (n >= 2 && n <= 5) ? cart.Climb : 1f;
            float kick = cart.Recoil * pat * (Aiming ? 0.85f : 1f) * (req.Crouched ? 0.8f : 1f);
            _drift = Mathf.Clamp(_drift + (Rng() - 0.5f) * cart.RecoilH * 1.6f, -cart.RecoilH * 1.5f, cart.RecoilH * 1.5f);
            float side = n == 1 ? 0f : _drift;
            _recoilAccum += kick * cart.Recover;
            lookPitch = Mathf.Clamp(lookPitch + kick, -1.2f, 1.2f);
            lookYaw += side;
            _kickB += cart.KickBack;
            Rock = Mathf.Min(0.14f, Rock + cart.Rock);
            _shake = 1f;
            if (!clipShot) _kick += kick * 2.6f;

            if (mag > 0) Ammo = Mathf.Max(0f, (Ammo ?? 0f) - 1f);
            bool last = mag > 0 && Ammo == 0f;
            string clip = last ? "fireLast" : Aiming ? "aimFire" : "hipFire";
            if (clipShot)
            {
                OnPlayClip?.Invoke(clip);
                _fire = _fireFrom = ClipTime?.Invoke(clip) is float ct && ct > 0f ? ct : 0.5f;
            }
            // src/shooting.js:425 — the weapon model's OWN clip, unconditional (not
            // gated on clipShot/ga.ready the way the arms clip above is).
            OnPlayWeaponClip?.Invoke(last ? "fireLast" : "fire");
            return true;
        }

        // src/shooting.js:466 update — everything that decays on its own.
        public void Update(float dt, bool weaponHeld, float footSpeed, float runSpeed, bool crouched,
            ref float lookPitch)
        {
            _t += dt;
            MagazineTick(dt, weaponHeld);
            if (_cool > 0f) _cool -= dt;
            if (_fire > 0f) _fire = Mathf.Max(0f, _fire - dt);
            if (_flashT > 0f) _flashT = Mathf.Max(0f, _flashT - dt); // src/shooting.js:474-476

            if (_bloom > 0f) _bloom = Mathf.Max(0f, _bloom - BloomCool * dt);
            if (_recoilAccum > 0.00002f)
            {
                // a rifle's sights come back slower than a pistol's: it is heavier
                bool auto = Cart(_lastCartHeld).Auto;
                float back = _recoilAccum * (1f - Mathf.Exp(-RecoilBack * (auto ? 0.6f : 1f) * dt));
                _recoilAccum -= back;
                lookPitch -= back;
            }
            else _recoilAccum = 0f;

            if (Rock > 0.0005f) Rock *= Mathf.Exp(-11f * dt); else Rock = 0f;
            if (_kickB > 0.0005f)
            {
                KickBackOffset = _kickB;
                _kickB *= Mathf.Exp(-KickbBackDecay * dt);
            }
            else { _kickB = 0f; KickBackOffset = 0f; }

            float moving = runSpeed > 0.0001f ? Mathf.Clamp01(Mathf.Abs(footSpeed) / runSpeed) : 0f;
            float amp = (weaponHeld && Aiming) ? SwayAmp * (1f + SwayMove * moving) * (crouched ? 0.55f : 1f) : 0f;
            float t = _t;
            float wantY = amp * (Mathf.Sin(t * 0.9f) * 0.7f + Mathf.Sin(t * 2.3f + 1.1f) * 0.3f);
            float wantP = amp * (Mathf.Sin(t * 1.4f + 0.6f) * 0.6f + Mathf.Sin(t * 3.1f) * 0.4f);
            float e = 1f - Mathf.Exp(-6f * dt);
            float sy = SwayYaw + (wantY - SwayYaw) * e;
            float sp = SwayPitch + (wantP - SwayPitch) * e;
            if (_shake > 0.01f)
            {
                sy += (Rng() - 0.5f) * ShakeAmp * _shake;
                sp += (Rng() - 0.5f) * ShakeAmp * _shake;
                _shake *= Mathf.Exp(-dt / ShakeTau);
            }
            else _shake = 0f;
            SwayYaw = sy;
            SwayPitch = sp;

            if (_kick > 0.00002f)
            {
                KickRotationZ = _kick;
                _kick *= Mathf.Exp(-KickBackDecay * dt);
            }
            else { _kick = 0f; KickRotationZ = 0f; }

            // src/shooting.js:520-525 — the crosshair opens with the cone: a real
            // measured formula off the current spread (same "formula, not art"
            // rationale as FlashScale above), continuously-maintained for a UI
            // consumer (e.g. UI/Hud/HudView.cs) to scale the reticle sprite by,
            // rather than Combat writing a DOM/UIElements transform itself.
            if (weaponHeld)
            {
                float spread = (Aiming ? AimSpread : HipSpread) + _bloom + MoveSpread * moving;
                CrosshairScale = (Aiming ? 0.6f : 1f) * (1f + Mathf.Min(1.6f, spread / 0.05f));
            }

            _flashFx.Render(this); // FlashVisible/_flashT already decayed above this frame
        }

        /// <summary>Releases the owned flash Material. Call when this pistol itself
        /// is torn down (matches Fx.OnFootMuzzleFlashFx's own Dispose).</summary>
        public void Dispose() => _flashFx.Dispose();

        /// <summary>src/shooting.js:523 `k` — see the comment in Update() above.</summary>
        public float CrosshairScale { get; private set; } = 1f;

        // src/shooting.js:541 _magazine
        void MagazineTick(float dt, bool weaponHeld)
        {
            string id = _lastCartHeld; // caller calls SetHeldCartridge(...) every frame
            if (id != _lastCart)
            {
                _lastCart = id;
                Ammo = id != null ? MagSize(id) : (float?)null;
                _reload = 0f;
                _fire = 0f;
                _cues.Clear();
            }
            if (_reload > 0f)
            {
                _reload = Mathf.Max(0f, _reload - dt);
                float done = 1f - _reload / _reloadFrom;
                for (int i = _cues.Count - 1; i >= 0; i--)
                {
                    if (done >= _cues[i].at)
                    {
                        OnPlayClip?.Invoke("cue:" + _cues[i].cue); // Core/audio maps "cue:<name>" to a sound
                        _cues.RemoveAt(i);
                    }
                }
                if (_reload <= 0f) Ammo = MagSize(id);
            }
        }

        // Caller sets this once per frame BEFORE Update() runs (mirrors reading
        // g.foot.held.cartridge fresh every frame in the JS's _magazine()).
        string _lastCartHeld;
        public void SetHeldCartridge(string cartId) => _lastCartHeld = cartId;
    }
}

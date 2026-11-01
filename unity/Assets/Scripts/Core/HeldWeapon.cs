using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TbilisiFly.Character;
using TbilisiFly.Combat;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace TbilisiFly.Core
{
    // =========================================================================
    // HeldWeapon.cs — "the weapon-held concept" GameLoop.cs's own class header
    // flags as owned by nobody: which slot the hero's hands are in right now,
    // what Transform/WeaponDef that is, and the per-frame job of posing the
    // arm around it (Combat/Weapons.cs) and firing it (Combat/Shooting.cs's
    // Pistol, over Combat/Ballistics.cs's BulletSim). Also the Pickups.IHost
    // implementation Combat/Pickups.cs was written against.
    //
    // NOW WIRED: Combat/GunAnim.cs (the Beretta/AKM arms clip — slide cycle,
    // lock-back, mag) and Combat/RifleHolds.cs (the AKM's two-handed shoulder
    // hold) were previously ported and never called from here — see this
    // project's recurring "ported but connected to nothing" failure mode.
    // Combat/WeaponAnimLoader.cs is the asset bridge that used to be missing
    // (the arms-skeleton FBX + each weapon's own parts clip were already
    // imported under Assets/Resources/Art/Props/ by an earlier pass; nothing referenced
    // them). EnsureArmsAnims()/ArmsFor()/PlayClip() below wire Pistol's own
    // ClipTime/OnPlayClip hooks (Combat/Shooting.cs's comment: "once GunAnim.cs
    // is wired") to real GunAnim instances, and Tick() now branches to
    // RifleHolds.ApplyRifleHold/ApplyRifleSupport for a rifle instead of
    // always running Weapons.ApplyPistolPose/ApplyAimPose. src/foot.js:6344-
    // 6378's blendReloadPose overlay (the shot jolt / reload pose laid over
    // the hold) is reproduced by GunPoseBlend.Apply, called every frame with
    // the same weight rule (reload: full pose; fire: an additive jolt off
    // RefAim/RefHip, half-strength and support-arm-only on a rifle).
    // DEVIATION (flagged, not silent): foot.js's reload overlay uses
    // chestForward() as the pose frame instead of the raw aim direction, and
    // gates on `!this._roll && !this._slide && !this._dive` — those states
    // live in FootController (Character/, off-limits to this pass). This file
    // uses the plain aim direction in both cases and does not gate on
    // roll/slide/dive, which is a defensible simplification (a reload or shot
    // jolt landing while rolling is a rare, brief visual glitch, not a
    // correctness bug) rather than a silent omission.
    //
    // !!! FOUND WHILE WIRING, FLAGGED FOR WHOEVER NEXT OWNS Character/ !!!
    // Character/FootWeaponFrame.cs is a SECOND, MORE COMPLETE port of this
    // exact overlay (foot.js:6275-6441 `_weaponFrame`, its own header says
    // so) — ApplyRifleHold/ApplyRifleSupport/ApplyRifleReloadHand/
    // ApplyTuckPose/ApplyCoverHold/ApplyCoverArmsBare/GunPoseBlend.Apply, all
    // already wired together — and it is ALSO never called from anything
    // (confirmed by grep: no reference to FootWeaponFrame outside its own
    // file). It depends on Character/FootWeapons.cs's OWN held-weapon model
    // (`Held`/`IsRifleHeld`/`HeldSide`/`GunAnimSource`/`PresentT`), which is a
    // SEPARATE tracking surface from this file's Pickups/HeldWeapon model —
    // the two were evidently built independently and never reconciled. This
    // pass wires the overlay through THIS file (Combat/HeldWeapon.cs, which
    // the port brief names explicitly) because it is the one that is
    // currently live (registered in GameLoop.CombatSystems) and self-
    // contained; FootWeaponFrame.cs sits inert today, so there is no runtime
    // double-application YET. But if a future Character/ pass wires
    // FootWeaponFrame.Tick() into FootController's LateUpdate without first
    // reconciling the two held-weapon models, BOTH will drive the same arm
    // bones/weapon Transform the same frame — a real, now-foreseeable bug.
    // Not fixed here: reconciling FootWeapons.cs's model with Pickups.IHost's
    // is a Character/-owning decision, out of this file's reach.
    // =========================================================================
    public sealed class HeldWeapon : Pickups.IHost, ITickable
    {
        readonly GameLoop _game;
        readonly Transform _sceneRoot;
        public readonly Pistol Gun = new Pistol();
        public readonly Pickups Pickups;

        public int CurrentSlot { get; private set; }

        /// <summary>src/pickups.js:~29-103 — the ground-loot card stack
        /// (UI/Combat/PickupCardsView.cs). Self-installed by
        /// <see cref="EnsurePickupUi"/>, same shape as Core/GameLoop.cs's
        /// Ensure*() views; this file owns it because <see cref="Pickups"/>
        /// (whose VisibleCards it renders) is built and ticked here.</summary>
        public TbilisiFly.UI.Combat.PickupCardsView PickupUi;

        /// <summary>src/shooting.js:~107-152 — the ON-FOOT pistol/rifle
        /// crosshair (UI/Combat/ReticleView.cs), not the flying combat HUD's
        /// reticle (that one is hud.js's CombatHUD, already wired via
        /// UI/Hud/HudView.cs). Fed from <see cref="Pistol.CrosshairScale"/>,
        /// which had zero readers in the whole project before this.</summary>
        public TbilisiFly.UI.Combat.ReticleView ReticleUi;
        readonly Dictionary<int, (WeaponDef def, Transform obj)> _carried = new Dictionary<int, (WeaponDef, Transform)>();
        Transform _beltObj;
        GearDef _wornGearDef; // SetWornGear used to drop `def` on the floor — needed for Core/GameLoop.cs's snapshot()/_loadout() port (game.js:1015-1025)

        // ---- GunAnim arms clips — one per kind, shared for the process's
        // lifetime, exactly as src/game.js:201 `this.gunAnims = {pistol:...,
        // rifle:...}` is built once at startup. Lazy because WeaponAnimLoader
        // touches AssetDatabase, which this constructor should not assume is
        // ready to call from (CombatWorldAdapter builds HeldWeapon early).
        GunAnim _armsPistol, _armsRifle;
        bool _armsLoaded;
        readonly ConditionalWeakTable<CharacterRig, Dictionary<string, Transform>> _bodySlotsCache = new ConditionalWeakTable<CharacterRig, Dictionary<string, Transform>>();

        public HeldWeapon(GameLoop game, BulletSim bullets, ImpactFx impacts, Transform sceneRoot)
        {
            _game = game;
            _sceneRoot = sceneRoot;
            Gun.Bullets = bullets;
            Gun.Impacts = impacts;
            Pickups = new Pickups(this);
            // src/shooting.js:247/258/262 g.gunAnim?.clipTime/.play — wired to
            // whichever arms clip matches the CURRENTLY held weapon, read fresh
            // every call (the lambda, not a snapshot) since the held slot can
            // change between one Fire()/Reload() call and the next.
            Gun.ClipTime = name => ArmsFor(CarriedInSlot(CurrentSlot))?.ClipTime(name) ?? 0f;
            Gun.OnPlayClip = PlayClip;
        }

        /// <summary>src/game.js:201-202 — both arms clips exist for the whole
        /// session, regardless of which one is currently held (so switching
        /// from pistol to rifle mid-reload doesn't need a fresh load).</summary>
        void EnsureArmsAnims()
        {
            if (_armsLoaded) return;
            _armsLoaded = true;
            _armsPistol = WeaponAnimLoader.CreateArmsAnim(GunAnimSpecs.Pistol);
            _armsRifle = WeaponAnimLoader.CreateArmsAnim(GunAnimSpecs.Rifle);
        }

        /// <summary>src/game.js:203-204 `get gunAnim()` — which arms clip
        /// applies to a given weapon kind (pistol-shaped weapons share one
        /// clip regardless of def.Anim being set — makarov has no clip file
        /// of its own and simply gets a GunAnim that never reports Ready).</summary>
        GunAnim ArmsFor(WeaponDef def) => WeaponDefs.IsRifle(def) ? _armsRifle : _armsPistol;

        // ---- probe-readable wiring facts (UiWireProbe.cs) — whether the
        // asset bridge actually found the FBX+clip, not just whether the
        // fields are non-null. Read-only; no gameplay code uses these.
        public bool ArmsPistolReady => _armsPistol?.Ready ?? false;
        public bool ArmsRifleReady => _armsRifle?.Ready ?? false;

        /// <summary>Pistol.OnPlayClip — plays the ARMS clip (hipFire/aimFire/
        /// fireLast/reload/reloadEmpty/draw/holster) and, for the weapon's own
        /// moving parts, the matching GUN clip (src/shooting.js:425's separate
        /// `obj?.userData?.anim?.play(last?'fireLast':'fire',...)` call —
        /// GUN_CLIPS only has "fire"/"fireLast"/"reload"/"reloadEmpty"/"draw",
        /// never the hip/aim split, since the gun's own slide doesn't care
        /// which stance fired it). "cue:"-prefixed names are audio cues
        /// (src/shooting.js:397 `OnPlayClip?.Invoke("cue:"+name)`) — no file in
        /// this port owns gun audio cues yet (Core/AudioFx.cs/Combat/GunFx.cs
        /// are other agents' territory this pass), so they are a deliberate
        /// no-op here, not a silent drop of something this file could play.</summary>
        void PlayClip(string clip)
        {
            if (string.IsNullOrEmpty(clip) || clip.StartsWith("cue:")) return;
            var def = CarriedInSlot(CurrentSlot);
            var obj = CarriedObjInSlot(CurrentSlot);
            ArmsFor(def)?.Play(clip, force: true, fade: (clip == "draw" || clip == "holster") ? 0.05f : 0.03f);
            if (obj == null || def == null) return;
            string partClip = clip switch
            {
                "hipFire" => "fire",
                "aimFire" => "fire",
                "fireLast" => "fireLast",
                _ => clip, // "reload" / "reloadEmpty" line up 1:1 with GUN_CLIPS
            };
            WeaponAnimLoader.GunPartsFor(obj, def)?.Play(partClip, force: true, fade: 0.02f);
        }

        /// <summary>src/gunanim.js's BoneRoles.SlotsOf applied to the HERO's
        /// own skeleton (not the clip's reference skeleton, which GunAnim.cs
        /// already builds its own copy of internally) — GunPoseBlend.Apply's
        /// `slots` parameter. Cached per rig; rebuilt only if the rig instance
        /// changes (a new body spawned).</summary>
        Dictionary<string, Transform> BodySlots(CharacterRig rig)
        {
            if (_bodySlotsCache.TryGetValue(rig, out var cached)) return cached;
            var slots = BoneRoles.SlotsOf(rig.Root, false);
            _bodySlotsCache.Add(rig, slots);
            return slots;
        }

        public bool Armed => CurrentSlot != 0 && CarriedObjInSlot(CurrentSlot) != null;

        // =====================================================================
        // ---- Pickups.IHost ------------------------------------------------
        // =====================================================================
        public WeaponDef CarriedInSlot(int slot) => _carried.TryGetValue(slot, out var v) ? v.def : null;
        public Transform CarriedObjInSlot(int slot) => _carried.TryGetValue(slot, out var v) ? v.obj : null;

        public void SetCarried(int slot, WeaponDef def, Transform obj)
        {
            _carried[slot] = (def, obj);
            if (def != null && obj != null && _game.Rig != null)
            {
                var hand = Weapons.FindHandBone(_game.Rig.Root);
                if (hand != null) Weapons.AttachToHand(hand, obj.gameObject, def, _game.Rig.Root);
            }
        }

        public void Select(int slot)
        {
            var prevSlot = CurrentSlot;
            var prevDef = CarriedInSlot(prevSlot);
            var prevObj = CarriedObjInSlot(prevSlot);
            CurrentSlot = slot;
            Gun.SetHeldCartridge(CarriedInSlot(slot)?.CartridgeId);
            if (prevSlot == slot) return;

            // src/foot.js:2585/2658 — draw/holster are ARMS-clip-only moves,
            // and only `if (!rifle)`: a rifle's equip/stow is the sling
            // system (Character/-owned, out of this file's reach), not a
            // GunAnim clip.
            EnsureArmsAnims();
            if (prevDef != null && !WeaponDefs.IsRifle(prevDef))
                ArmsFor(prevDef)?.Play("holster", force: true, fade: 0.05f);
            if (prevObj != null)
                WeaponAnimLoader.GunPartsFor(prevObj, prevDef)?.Reset(); // foot.js:2525 "slide forward, magazine in"

            var def = CarriedInSlot(slot);
            if (def != null && !WeaponDefs.IsRifle(def))
                ArmsFor(def)?.Play("draw", force: true, fade: 0.05f);
        }

        public void SetWornGear(GearDef def, Transform obj) { _beltObj = obj; _wornGearDef = def; }

        /// <summary>src/game.js:1015-1025 `_loadout()`'s `guns` array —
        /// `(this.weapons?.carried ?? []).forEach((w,i) => ...)` — every slot
        /// that actually holds a weapon (not a gear item; this model keeps
        /// gear in its own `_wornGearDef`, so every `_carried` entry here IS
        /// a weapon). Order is NOT guaranteed by a Dictionary — GameLoop's
        /// caller sorts by slot, matching the JS's own `guns.sort((a,b) =>
        /// a.s - b.s)`.</summary>
        public IEnumerable<(int Slot, string Id)> CarriedLoadout()
        {
            foreach (var kv in _carried)
                if (kv.Value.def != null) yield return (kv.Key, kv.Value.def.Id);
        }

        /// <summary>src/game.js:1026-1027 `new Set(this.gear?.keys() ?? [])` —
        /// this model carries at most one worn gear item (the belt), where the
        /// JS's `game.gear` is a Map that could in principle hold several;
        /// faithful to what is actually modeled, not an invented limit.</summary>
        public string WornGearId => _wornGearDef?.Id;

        public bool WearGear(GearDef def, Transform obj)
        {
            if (_game.Rig == null || def == null) return false;
            string want = def.Bone == "hips" ? "pelvis" : def.Bone; // 22-bone UE rig has no literal "hips"
            Transform bone = null;
            foreach (var t in _game.Rig.Root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.Equals(want, System.StringComparison.OrdinalIgnoreCase)) { bone = t; break; }
            }
            bone ??= _game.Rig.Root; // honest degraded fallback — belt rides the root, not buried
            if (obj != null) { obj.SetParent(bone, false); obj.localPosition = Vector3.zero; }
            return true;
        }

        public void SetHint(string text) => _game.OnHint?.Invoke(text);
        public void SaveState(bool immediate, string reason) => _game.SaveState(immediate);

        public float? GroundAt(float x, float z, float fromY, float probe)
            => (_game.Terrain != null && _game.Terrain.Contains(-x, z)) ? (float?)_game.Terrain.HeightAt(x, z) : null;

        public Transform SceneRoot => _sceneRoot;
        public Vector3 ActorPos => _game.Foot.Pos;
        public float ActorYaw => _game.Foot.Yaw;

        // =====================================================================
        // ---- GameLoop's own hooks — game.js:508-514/534 ----
        // =====================================================================
        public void ToggleAim() => Gun.ToggleAim(Time.realtimeSinceStartup * 1000f);

        public void TakeNearest()
        {
            if (Pickups.Near == null) return;
            Pickups.Take();
        }

        /// <summary>src/shooting.js:240-268 `reload(auto = false)`, as the R key
        /// reaches it (src/game.js:521-524). Combat/Shooting.cs's Pistol.Reload
        /// was only ever called by Fire()'s own dry-fire auto-reload, so a
        /// partly-spent magazine could never be topped up by hand — the only
        /// route to a full magazine was running the gun completely dry.
        ///
        /// The JS's own two gates come first (`if (!g.foot?.active ||
        /// !g.foot.held) return false`), then the hint, which is the one thing
        /// the auto path deliberately does NOT show (`if (!auto)
        /// g._setHint(...)`, shooting.js:265) — Pistol.ReloadHintText already
        /// holds both Georgian strings byte-for-byte.</summary>
        public bool Reload()
        {
            if (_game.Mode != GameMode.OnFoot) return false;
            var def = CarriedInSlot(CurrentSlot);
            if (def == null || CarriedObjInSlot(CurrentSlot) == null) return false;
            bool dry = (Gun.Ammo ?? 0f) <= 0f;
            if (!Gun.Reload(def.CartridgeId)) return false;
            SetHint(Pistol.ReloadHintText(dry)); // shooting.js:265 — manual only
            return true;
        }

        // =====================================================================
        // ---- the two on-foot combat views, self-installed ------------------
        // Same one-shot "find the UIDocument, build, parent" precedent as
        // Core/GameLoop.cs's EnsureMapUi()/EnsureHitToast()/EnsureRangeLaneUi():
        // Core/CombatWorldAdapter.cs's InstallUi() creates the UIDocument, and
        // this class is constructed before that runs, so construction has to be
        // retried from the per-frame tick rather than done in the constructor.
        // =====================================================================
        // A UIDocument that EXISTS is not a UIDocument whose panel exists — its
        // root is created by its own OnEnable off panelSettings — and the field
        // was assigned BEFORE the Add, so the NullReferenceException this used
        // to throw (HeldWeapon.cs:293, caught by GameLoop.RunStage) left
        // PickupUi non-null and unparented and the take-card stack could never
        // install again for the rest of the session. Both now install through
        // UiBootstrap.GameUiRoot() and assign the field only AFTER the Add, so
        // a too-early frame is simply retried. See that method's own header;
        // docs/open_items.json `playmode-nre-ui-installs`.
        void EnsurePickupUi()
        {
            if (PickupUi != null) return;
            var root = TbilisiFly.UI.Common.UiBootstrap.GameUiRoot();
            if (root == null) return; // InstallUi() hasn't run yet this session — retry next frame
            var view = new TbilisiFly.UI.Combat.PickupCardsView();
            root.Add(view);
            // src/pickups.js:86 `el.addEventListener('pointerdown', ... this.take(it))`
            // — a click takes THAT card's item, not merely the nearest one, so it
            // goes to Take(item) rather than this file's own TakeNearbyPickup().
            view.OnTakeClicked = it => Pickups.Take(it);
            PickupUi = view;
        }

        void EnsureReticleUi()
        {
            if (ReticleUi != null) return;
            var root = TbilisiFly.UI.Common.UiBootstrap.GameUiRoot();
            if (root == null) return; // InstallUi() hasn't run yet this session — retry next frame
            var view = new TbilisiFly.UI.Combat.ReticleView();
            root.Add(view);
            ReticleUi = view;
        }

        // =====================================================================
        // ---- per-frame — ITickable, registered on GameLoop.CombatSystems --
        // =====================================================================
        public void Tick(float dt)
        {
            EnsurePickupUi();
            EnsureReticleUi();
            if (_game.Rig == null)
            {
                // src/pickups.js:270 — setVisible(false) empties the stack, and
                // the card DOM goes with it; VisibleCards is already cleared by
                // the line above, so Render() just hides the view. Without this
                // the cards would freeze on screen for as long as there is no
                // body (a body swap re-spawns the rig).
                Pickups.SetVisible(false);
                PickupUi?.Render(Pickups.VisibleCards);
                ReticleUi?.Tick(false, false, 1f);
                return;
            }
            Pickups.Update(dt, _sceneRoot, _game.Foot.Pos);
            PickupUi?.Render(Pickups.VisibleCards); // src/pickups.js:93-103 `_render(list)`
            EnsureArmsAnims();

            var def = CarriedInSlot(CurrentSlot);
            var obj = CarriedObjInSlot(CurrentSlot);
            bool held = def != null && obj != null;
            bool rifle = WeaponDefs.IsRifle(def);
            var arms = ArmsFor(def);

            // src/game.js:2377/2385 — BOTH arms clips advance every frame
            // regardless of which is currently held (Update() is a no-op
            // while !Ready, so this is safe before the FBX is imported too).
            _armsPistol?.Update(dt);
            _armsRifle?.Update(dt);
            var parts = (obj != null && def != null) ? WeaponAnimLoader.GunPartsFor(obj, def) : null;
            parts?.Update(dt);

            Vector3? aim = _game.MainCamera != null ? _game.MainCamera.transform.forward : (Vector3?)null;

            if (held)
            {
                bool inCover = _game.Foot.Cover.Active;
                if (inCover)
                {
                    // foot.js:3623 _coverHold — the wall pose (rifle: high
                    // ready in its pocket; pistol: GTA high-carry) replaces
                    // the ordinary aim/carry solve outright.
                    RifleHolds.ApplyCoverHold(_game.Rig, _game.Rig.Root, Weapons.FindHandBone(_game.Rig.Root), def, obj, _game.Foot, "R", 1f);
                }
                else if (rifle)
                {
                    var hand = Weapons.FindHandBone(_game.Rig.Root);
                    if (hand != null)
                    {
                        string mode = Gun.Reloading ? "reload" : (Gun.Aiming ? "aim" : "carry");
                        RifleHolds.ApplyRifleHold(_game.Rig, _game.Rig.Root, hand, aim, def, mode, "R");
                        RifleHolds.ApplyRifleSupport(_game.Rig, _game.Rig.Root, obj, def, 1f, aim, "R");
                    }
                }
                else
                {
                    var chain = Weapons.FindArmChain(_game.Rig.Root);
                    var chainL = Weapons.FindArmChainL(_game.Rig.Root);
                    var hand = Weapons.FindHandBone(_game.Rig.Root);
                    if (chain.Valid && hand != null)
                    {
                        if (Gun.Aiming) Weapons.ApplyAimPose(_game.Rig.ActGroup, _game.Rig.Root, chain, chainL, hand, aim);
                        else Weapons.ApplyPistolPose(_game.Rig.ActGroup, _game.Rig.Root, chain, hand, aim);
                    }
                }

                // src/foot.js:6331-6378 — the clip's own recoil jolt/reload
                // pose, laid OVER the hold above: a reload is the full clip
                // pose (ref=null); a shot is an ADDITIVE jolt off whichever
                // idle (aiming ? RefAim : RefHip) the hold was already at. A
                // rifle only ever gets this at half strength and on the
                // support arm (foot.js's own comment: the Mixamo man's hands
                // jump 7cm on a shot, which on a stock already in the
                // shoulder would drive the gun INTO the flank).
                if (!inCover && arms != null && arms.Ready)
                {
                    float rw = rifle ? Gun.FireBlend * 0.45f : Mathf.Max(Gun.ReloadBlend, Gun.FireBlend);
                    if (rw > 0.001f)
                    {
                        var hand = Weapons.FindHandBone(_game.Rig.Root);
                        if (hand != null)
                        {
                            var refSnap = Gun.Reloading ? null : (Gun.Aiming ? arms.RefAim : arms.RefHip);
                            string only = (Gun.Reloading && rifle) ? "l"
                                : (Gun.Reloading || Gun.Aiming || rifle ? null : "r");
                            GunPoseBlend.Apply(_game.Rig.Root, BodySlots(_game.Rig), hand, aim,
                                arms.Dirs, arms.DirsOk, rw, arms, only, refSnap);
                        }
                    }
                }
            }

            float pitch = _game.FootLookPitch;
            float yaw = _game.Foot.CamYaw;
            Gun.Update(dt, held, _game.Foot.Speed, Mathf.Max(0.01f, _game.Foot.RunSpeed), _game.Foot.Crouched, ref pitch);
            // src/shooting.js:118 `setVisible(on)` + :135 `.aim` + :520-525 the
            // cone scale. The JS shows the reticle exactly while a weapon is
            // drawn on foot; CrosshairScale already folds the 0.6x aim factor in.
            ReticleUi?.Tick(held && _game.Mode == GameMode.OnFoot, Gun.Aiming, Gun.CrosshairScale);

            if (held && _game.Mode == GameMode.OnFoot && _game.MainCamera != null && def.Measured)
            {
                var mouse = Mouse.current;
                bool triggerDown = mouse != null && mouse.leftButton.isPressed;
                Gun.Trigger(triggerDown);
                Vector3 muzzle = obj.TransformPoint(def.Muzzle);
                Vector3 aimDir = AimResolver.ResolveFireDirection(_game.MainCamera, 0f, 0f, Pistol.Range,
                    muzzle, null, null, ~0);
                var cover = _game.Foot.Cover;
                var req = new Pistol.FireRequest
                {
                    WeaponHeld = true,
                    CartridgeId = def.CartridgeId,
                    InCover = cover.Active,
                    // src/foot.js:3215-3219 `coverCanFire` — the trigger does nothing
                    // behind the wall until peeked/popped/leaned/blind far enough out.
                    // Was hardcoded null here (= never gated, see the game.js walk in
                    // this port unit's own note), so cover never actually stopped a
                    // shot even though Shooting.cs:347 already had the check wired and
                    // waiting for a non-null delegate.
                    CoverCanFire = cover.Active ? (System.Func<bool>)cover.CanFire : null,
                    // src/shooting.js:316 `const blind = !!g.foot?.aimOverride` — the
                    // SAME vector FootController.cs:855 already reads as AimOverride
                    // for the arm pose; mirroring its presence here is the literal JS
                    // condition for widening the cone, not a new gate.
                    Blind = cover.BlindAimOverride.HasValue,
                    Muzzle = muzzle,
                    AimDir = aimDir,
                    MoveFrac = _game.Foot.RunSpeed > 0.01f ? Mathf.Clamp01(Mathf.Abs(_game.Foot.Speed) / _game.Foot.RunSpeed) : 0f,
                    Crouched = _game.Foot.Crouched,
                    ClipShotAvailable = arms?.Ready ?? false, // src/shooting.js:392 `!!g.gunAnim?.ready`
                    HeldWeapon = obj,
                    HeldWeaponDef = def,
                };
                // src/game.js:624-640 `_onPointerDown`'s armed-click branch: a trigger
                // pull that cover's own coverCanFire() refuses is not dropped — aiming,
                // it is BUFFERED (coverShot, foot.js:3221-3225 ShotBuf) and delivered
                // the instant the gun clears the wall; not aiming, the blind arm goes
                // out for as long as the button is held (coverBlind, foot.js:3227-3230).
                // Release (game.js:676-680 `_onPointerUp`) always clears the blind want.
                // These three calls were never made from anywhere in the project —
                // FootCover.cs's CanFire/Shot/Blind/ConsumeShotBuffer are a correct,
                // previously-unwired port (grep found zero call sites before this).
                if (mouse != null && mouse.leftButton.wasPressedThisFrame)
                {
                    if (cover.Active && !cover.CanFire())
                    {
                        if (Gun.Aiming) cover.Shot();
                        else cover.Blind(true);
                    }
                    else
                    {
                        Gun.Fire(req, ref pitch, ref yaw);
                    }
                }
                else
                {
                    Gun.TryAutoFire(req, ref pitch, ref yaw);
                }
                if (mouse != null && mouse.leftButton.wasReleasedThisFrame) cover.Blind(false);
                // src/foot.js:3447 `if (this.coverCanFire() && g.pistol?.fire())
                // c.shotBuf = 0` — the buffered shot delivered the moment cover allows
                // it (same frame or several later), exactly once.
                cover.ConsumeShotBuffer(() => Gun.Fire(req, ref pitch, ref yaw));
            }

            _game.FootLookPitch = pitch;
            _game.Foot.CamYaw = yaw;
        }
    }
}

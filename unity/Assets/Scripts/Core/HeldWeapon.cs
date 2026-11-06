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
    // CHESTFORWARD CLOSED (was flagged here as a deviation): the reload's
    // pose frame is now Weapons.ChestForward(_game.Rig.ActGroup, Root) ??
    // aim, matching foot.js:6358's `const frame = reloading ? (chestForward(
    // ...) ?? aim) : aim;` exactly (src/weapons.js:505-512). ONE DEVIATION
    // REMAINS, still flagged rather than silent: foot.js also gates the
    // whole reload-jolt branch on `!this._roll && !this._slide && !this.
    // _dive` — those states live in FootController (Character/, off-limits
    // to this pass). This file does not gate on roll/slide/dive, which is a
    // defensible simplification (a reload or shot jolt landing while rolling
    // is a rare, brief visual glitch, not a correctness bug) rather than a
    // silent omission.
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
    public sealed class HeldWeapon : Pickups.IHost, ITickable, FootBelt.IHolsterHost
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
        GearDef _wornGearDef;

        // ---- the JS stow/draw model, wired 2026-11-02 ----------------------
        // Character/FootWeapons.cs (src/foot.js:2125-3051: belt holster, back
        // sling, draw/holster) and Character/FootBelt.cs (the strap fitted and
        // skinned onto the body) were both finished, probe-verified and NEVER
        // INSTANTIATED — the skill file's "inert verified code" class. This
        // file owns the ARMS (the pistol/aim poses in Tick below); FootWeapons
        // runs with Active=false so it only ever REPARENTS the gun objects
        // (instant draw/stow, no animated reach), which is what keeps the two
        // orchestrators the skill warns about from driving the same bones.
        FootBelt _footBelt;
        FootBeltBridge _beltBridge;
        FootWeapons _fw;
        CharacterRig _fwRig;                   // the rig the above were built for; a body swap rebuilds them
        GameObject _beltInstance;
        readonly Dictionary<Transform, CarriedWeapon> _cwCache = new Dictionary<Transform, CarriedWeapon>();

        // ---- the purchased pack as the shooter (2026-11-02) ----------------
        // Under UsePurchasedShooter the pack's ShootBehaviour owns the hand:
        // a taken gun becomes one of ITS InteractiveWeapons (CoverShootPickups.
        // SpawnBridge), and the belt/back stow below only ever REPARENTS the
        // pack's object when ShootBehaviour.ChangeWeapon asks via StowHook/
        // DrawHook. _packDefs maps each pack weapon back to our WeaponDef
        // (rifle-or-pistol decides belt vs back; Slot decides the bar column).
        readonly Dictionary<InteractiveWeapon, WeaponDef> _packDefs = new Dictionary<InteractiveWeapon, WeaponDef>();
        static string PackSpecFor(WeaponDef def) => WeaponDefs.IsRifle(def) ? "ak" : "pistol";
        static int PackSlotFor(WeaponDef def) => WeaponDefs.IsRifle(def) ? 2 : 1;   // ShootBehaviour.slotMap: SHORT=1, LONG=2

        /// <summary>ShootBehaviour.StowHook — park the weapon leaving the hand on
        /// the belt (pistol) or the back (rifle). false = "no socket here", and
        /// the pack hides it the stock way.</summary>
        public bool StowPack(InteractiveWeapon iw)
        {
            if (_fw == null || _game.Rig == null || iw == null || !_packDefs.TryGetValue(iw, out var def)) return false;
            var cw = CW(def, iw.transform);
            if (_fw.Held == cw) _fw.Held = null;
            bool ok = WeaponDefs.IsRifle(def) ? _fw.ToBack(_game.Rig, cw) : _fw.ToHolster(_game.Rig);
            if (!ok) return false;
            // The pack's weapon models are authored 1:1 in world metres. The
            // JS stow scales by `Def.Scale / socket.lossyScale`, which is right
            // for OUR loot models and wrong for these — GunHandRun measured the
            // stowed AK at lossyScale 0.011 (1 cm, "no gun on my body"). Pin
            // the world scale explicitly, whatever the socket's scale chain is.
            EnforceWorldScale(iw.transform, Vector3.one);
            foreach (var r in iw.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
            Debug.Log($"PACK_STOW weapon={iw.label} parent={(iw.transform.parent != null ? iw.transform.parent.name : "<none>")} worldScale={iw.transform.lossyScale.ToString("F3")} distRoot={Vector3.Distance(iw.transform.position, _game.Rig.Root.position):F2}");
            PushOutsideBody(iw);
            FallbackStow();
            if (CoverShootHero.Instance?.Shoot?.ActiveWeapon == null) _game.WeaponBar?.SyncIndex(-1);
            return true;
        }

        /// <summary>A stowed pack weapon must sit OUTSIDE the body. The JS sockets put
        /// a slung rifle 2 cm INTO a slim back and a pistol in a pocket against the
        /// hip — on a 1.1 m-wide fruit that leaves the gun half-buried
        /// (GunHandRun 04/06: rifle at the back's surface, invisible). Measure the
        /// body's section at the gun's height (FootBelt.BodySection, body verts) and
        /// slide the gun radially until its own bounds clear that radius.</summary>
        void PushOutsideBody(InteractiveWeapon iw)
        {
            if (iw == null || _beltBridge == null || _game.Rig == null) return;
            var rs = iw.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return;
            Bounds b = rs[0].bounds; for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            // a section at the gun's height; a pistol on the hip sits below the fruit's
            // waist, so walk upward until the body has one
            TbilisiFly.Character.BodySection? sec = null; float y = b.center.y;
            for (int k = 0; k < 6 && !sec.HasValue; k++) sec = _beltBridge.BodySectionAt(_game.Rig, y + 0.08f * k);
            if (!sec.HasValue) { Debug.Log($"PACK_STOW_FIT weapon={iw.label} bodySection=<none>"); return; }
            // A rifle on the back goes straight BACK (its lateral offset is the
            // sling's business); a pistol on the hip goes radially out.
            bool rifle = _packDefs.TryGetValue(iw, out var pdef) && WeaponDefs.IsRifle(pdef);
            Vector3 d = b.center - sec.Value.C; d.y = 0f;
            // the hero faces root-local -Z, so its BACK is +Root.forward (FootWeapons.BackSocket's note)
            Vector3 dir = rifle ? _game.Rig.Root.forward : (d.sqrMagnitude > 1e-6f ? d.normalized : _game.Rig.Root.forward);
            dir.y = 0f; dir.Normalize();
            float dist = Vector3.Dot(d, dir);                       // how far out along the push axis it already is
            // Clearance is a CONSTANT, not a measured extent: GunHandRun round 7 read the
            // slung rifle's LENGTH as its "thickness" (0.36 m), pushed it 0.9 m and parked
            // it inside the hangar wall behind the bench — in frustum, renderers on, and
            // exactly 0 pixels drawn. A rifle slung across the back has its centre about
            // 0.12 m behind the skin; a pistol in a hip pocket about 0.08 m out.
            float half = rifle ? 0.12f : 0.08f;
            float need = sec.Value.R + half;
            float push = need - dist;
            if (push > 0f) iw.transform.position += dir * push;
            Debug.Log($"PACK_STOW_FIT weapon={iw.label} rifle={rifle.ToString().ToLowerInvariant()} bodyR={sec.Value.R:F3} alongBefore={dist:F3} half={half:F3} push={Mathf.Max(0f, push):F3} alongAfter={(dist + Mathf.Max(0f, push)):F3}");
        }

        static void EnforceWorldScale(Transform t, Vector3 world)
        {
            if (t == null) return;
            Vector3 p = t.parent != null ? t.parent.lossyScale : Vector3.one;
            t.localScale = new Vector3(
                Mathf.Abs(p.x) > 1e-6f ? world.x / p.x : 1f,
                Mathf.Abs(p.y) > 1e-6f ? world.y / p.y : 1f,
                Mathf.Abs(p.z) > 1e-6f ? world.z / p.z : 1f);
        }

        /// <summary>ShootBehaviour.DrawHook — the weapon is going back to the
        /// hand: let the stow release it and mark it held.</summary>
        public void DrawPack(InteractiveWeapon iw)
        {
            if (iw == null || !_packDefs.TryGetValue(iw, out var def)) return;
            if (_fw != null)
            {
                var cw = CW(def, iw.transform);
                _fw.FromBack(cw); _fw.FromHolster(cw);
                _fw.Held = cw;
            }
            foreach (var r in iw.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "PACK_DRAW weapon={0} slot={1} parent={2}", iw.label, def.Slot, iw.transform.parent != null ? iw.transform.parent.name : "<none>");
            _game.WeaponBar?.SyncIndex(def.Slot);
        }

        /// <summary>One stable FootWeapons.CarriedWeapon per weapon object —
        /// its _holstered/_onBack lists compare by reference.</summary>
        CarriedWeapon CW(WeaponDef def, Transform obj)
        {
            if (obj == null) return null;
            if (!_cwCache.TryGetValue(obj, out var cw))
            {
                cw = new CarriedWeapon { Def = def, Obj = obj, Anim = WeaponAnimLoader.GunPartsFor(obj, def) };
                _cwCache[obj] = cw;
            }
            cw.Def = def;
            return cw;
        }

        IEnumerable<CarriedWeapon> AllCarried()
        {
            foreach (var kv in _carried)
                if (kv.Value.def != null && kv.Value.obj != null) yield return CW(kv.Value.def, kv.Value.obj);
        }

        /// <summary>Anything owned-but-not-held that neither the belt nor the back
        /// could take (no socket on this body) must not be left standing in the
        /// world where the stow dropped it: park it on the root, hidden. The JS
        /// had the same hole ("put a pistol away into thin air") and solved it by
        /// making the belt standard issue; this is the backstop for a body whose
        /// Animator has no Chest/Spine for the back socket.</summary>
        void FallbackStow()
        {
            if (_game.Rig == null) return;
            foreach (var cw in AllCarried())
            {
                if (_fw != null && cw == _fw.Held) continue;
                if (cw.Obj.parent != null) continue;
                cw.Obj.SetParent(_game.Rig.Root, false);
                cw.Obj.localPosition = Vector3.zero;
                foreach (var r in cw.Obj.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                Debug.LogWarning($"PICKUP_STOW_FALLBACK {cw.Id}: no belt/back socket on {_game.Rig.Id} — parked hidden on the root");
            }
        }

        // ---- FootBelt.IHolsterHost --------------------------------------------
        public IEnumerable<Transform> HolsteredIn(WornGear gear)
        {
            if (gear?.Sockets == null) yield break;
            foreach (var cw in AllCarried())
                if (cw.Obj.parent != null && gear.Sockets.Contains(cw.Obj.parent)) yield return cw.Obj;
        }
        public void TakeOffGear(Transform heldObject) { if (heldObject != null) heldObject.SetParent(null, true); }
        public bool HasHeldWeapon() => _fw?.Held != null;
        public void ToHolster() { if (_fw != null && _game.Rig != null) { _fw.ToHolster(_game.Rig); FallbackStow(); } } // SetWornGear used to drop `def` on the floor — needed for Core/GameLoop.cs's snapshot()/_loadout() port (game.js:1015-1025)

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

            // THE PACK TAKES THE GUN (2026-11-02). The warehouse loot is a
            // PickupTag FBX; the pack can only aim/shoot/reload one of its own
            // InteractiveWeapons, in its own hand offsets. So: spawn the pack
            // weapon of the same class, retire the loot model, and hand it to
            // ShootBehaviour.AddWeapon (which draws it — the JS draws on
            // pickup too, pickups.js:201). Two pistols share the pack's one
            // SHORT slot: the second one just refills the first.
            var shoot = GameLoop.UsePurchasedShooter ? CoverShootHero.Instance?.Shoot : null;
            if (shoot != null && def != null && obj != null)
            {
              try
              {
                Debug.Log($"PICKUP_PACK_TRY def={def.Id} shootEnabled={shoot.enabled.ToString().ToLowerInvariant()} active={shoot.isActiveAndEnabled.ToString().ToLowerInvariant()} ready={shoot.Ready.ToString().ToLowerInvariant()}");
                int packSlot = PackSlotFor(def);
                var existing = shoot.WeaponInSlot(packSlot);
                if (existing != null && _packDefs.ContainsKey(existing))
                {
                    existing.ResetBullets();
                    UnityEngine.Object.Destroy(obj.gameObject);
                    _carried[slot] = (_packDefs[existing], existing.transform);
                    shoot.SelectSlot(packSlot);
                    Debug.Log($"PICKUP_PACK def={def.Id} refilled existing slot={packSlot}");
                    return;
                }
                var iw = CoverShootPickups.SpawnBridge(PackSpecFor(def), def.Id + "_pack", obj.position);
                if (iw != null)
                {
                    _packDefs[iw] = def;
                    iw.PrepareForInventory();
                    _cwCache.Remove(obj);
                    UnityEngine.Object.Destroy(obj.gameObject);
                    _carried[slot] = (def, iw.transform);
                    shoot.AddWeapon(iw);                       // → ChangeWeapon → DrawHook → bar index
                    _game.WeaponBar?.SetCarried(def.Slot, new TbilisiFly.UI.SkillBar.CarriedWeapon { Name = def.Name });
                    Debug.Log($"PICKUP_PACK def={def.Id} spec={PackSpecFor(def)} slot={packSlot} active={(shoot.ActiveWeapon == iw).ToString().ToLowerInvariant()}");
                    return;
                }
                Debug.LogWarning($"PICKUP_PACK def={def.Id}: pack model missing — falling back to the JS hand attach");
              }
              catch (System.Exception e)
              {
                // The take must never die inside the pack: fall through to the
                // JS attach so the gun is at least owned and in the hand.
                Debug.LogError($"PICKUP_PACK def={def.Id} FAILED in the pack bridge — falling back to the JS hand attach: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
              }
            }
            Transform handDiag = null;
            if (def != null && obj != null && _game.Rig != null)
            {
                var hand = Weapons.FindHandBone(_game.Rig.Root);
                handDiag = hand;
                if (hand != null) Weapons.AttachToHand(hand, obj.gameObject, def, _game.Rig.Root);
            }
            // One line that names which link of the pickup chain failed, instead
            // of inferring it from "the gun is not there".
            Debug.Log($"PICKUP_TAKE slot={slot} def={def?.Id ?? "<null>"} rig={(_game.Rig != null).ToString().ToLowerInvariant()} "
                + $"hand={(handDiag != null ? handDiag.name : "<NOT FOUND>")} obj={(obj != null ? obj.name : "<null>")} "
                + $"bar={(_game.WeaponBar != null).ToString().ToLowerInvariant()} "
                + $"objActive={(obj != null && obj.gameObject.activeInHierarchy).ToString().ToLowerInvariant()} "
                + $"objWorld={(obj != null ? obj.position.ToString("F2") : "-")} "
                + $"objScale={(obj != null ? obj.lossyScale.ToString("F3") : "-")}");

            // PUSH IT TO THE ON-FOOT ACTION BAR (2026-11-02).
            // WeaponBarView.SetCarried — the ONLY thing that fills a slot —
            // had zero callers anywhere in the project, so the bar was built,
            // added to the UI tree and (once its visibility was wired) shown
            // completely EMPTY. The user reported it twice as "my skills not
            // show" / "i dont see skills bar like in fucken js project".
            // WeaponDef.Slot IS the index into WeaponCategory.All: the two
            // pistols carry Slot = 1 (All[1] = "pistol") and the AKM carries
            // Slot = 4 (All[4] = "rifle"), which is also why
            // WeaponBarView.SelectByDigit does `i = digit - 1` — the toast's
            // "(სლოტი 2)" is the DIGIT, one above the index.
            // skillbar.js:330-346 setCarried is this same call.
            _game.WeaponBar?.SetCarried(def != null ? def.Slot : slot,
                def == null ? (TbilisiFly.UI.SkillBar.CarriedWeapon?)null
                            : new TbilisiFly.UI.SkillBar.CarriedWeapon { Name = def.Name });

            // A weapon put in a slot that is NOT the drawn one goes straight to
            // the belt/back (pickups.js restore: "the belt finds the pistol
            // already in its slot and the holster is filled on the way in").
            // Pickups.Take() follows SetCarried with Select(slot), which draws
            // it; anything else — a restored loadout, a second gun — stows.
            if (def != null && obj != null && _fw != null && _game.Rig != null && slot != CurrentSlot)
            {
                var cw = CW(def, obj);
                if (WeaponDefs.IsRifle(def)) _fw.ToBack(_game.Rig, cw); else _fw.ToHolster(_game.Rig);
                FallbackStow();
            }
        }

        public void Select(int slot)
        {
            if (GameLoop.UsePurchasedShooter)
            {
                // skillbar.js:309/312 by way of the pack: the bar's toggle is
                // already resolved by WeaponBarView.Select (slot 0 = holster).
                var d = CarriedInSlot(slot);
                CurrentSlot = d != null ? slot : 0;
                Gun.SetHeldCartridge(d?.CartridgeId);
                CoverShootHero.Instance?.Shoot?.SelectSlot(d != null ? PackSlotFor(d) : 0);
                _game.WeaponBar?.SyncIndex(d != null ? slot : -1);
                return;
            }
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

            // src/skillbar.js:309/312 — the slot is a draw/holster TOGGLE on the
            // weapon objects themselves: out of the holster or off the back and
            // into the hand (foot.js:2434 drawWeapon), or back onto the belt /
            // the back sling (foot.js:2491 holster). FootWeapons does exactly
            // that; Active=false makes it the instant branch (see the field doc).
            if (_fw != null && _game.Rig != null && !GameLoop.UsePurchasedShooter)
            {
                var obj = CarriedObjInSlot(slot);
                if (def != null && obj != null) _fw.DrawWeapon(_game.Rig, CW(def, obj));
                else _fw.Holster(_game.Rig);
                FallbackStow();
            }
            _game.WeaponBar?.SyncIndex(def != null ? slot : -1);
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
            if (def.Id == "belt") return WearBelt(def, obj);
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

        /// <summary>The belt is where a holstered pistol lives (game.js:159-161),
        /// and FootBelt.WearGear needs the strap's OWN object to find its
        /// `holster`/`holster2` sockets — it returns false on a null obj, which is
        /// exactly what CombatWorldAdapter's "standard issue" wear passed, so no
        /// hero ever had a socket. Load Art/Props/belt.fbx here (the same asset
        /// src/weapons.js:87-118 describes), fit it with FootBelt, and stand up
        /// the FootWeapons stow/draw model against it. Rebuilt on a body swap.</summary>
        bool WearBelt(GearDef def, Transform obj)
        {
            var rig = _game.Rig;
            if (_fwRig == rig && _fw != null) return true;
            if (_beltInstance != null) UnityEngine.Object.Destroy(_beltInstance);
            _cwCache.Clear();

            Transform beltObj = obj;
            if (beltObj == null)
            {
                var prefab = ArtAssets.Load<GameObject>("Assets/Resources/Art/Props/belt.fbx");
                if (prefab == null)
                {
                    Debug.LogWarning("BELT: Assets/Resources/Art/Props/belt.fbx missing — a holstered pistol has nowhere to live");
                    return false;
                }
                _beltInstance = UnityEngine.Object.Instantiate(prefab);
                _beltInstance.name = "belt";
                beltObj = _beltInstance.transform;
            }
            _beltObj = beltObj;

            _footBelt = new FootBelt(rig, this);
            _beltBridge = new FootBeltBridge(_footBelt, rig, def.HolsterScale > 0f ? def.HolsterScale : 0.54f);
            _fw = new FootWeapons(_beltBridge, _sceneRoot)
            {
                Active = false,                      // instant draw/stow — this file keeps the arms
                ModelId = rig.Id,
                Cam = _game.MainCamera,
                Cover = _game.Foot.Cover,
                CarriedWeapons = AllCarried,
                GunAnimSource = () => ArmsFor(CarriedInSlot(CurrentSlot)),
                AimDirSource = () => _game.MainCamera != null ? (Vector3?)_game.MainCamera.transform.forward : null,
                PistolAiming = () => Gun.Aiming,
                SetPistolAiming = on => Gun.SetAiming(on),   // foot.js:2480 _aimOnDraw — "on click we are in aim mode"
                SetReticleVisible = _ => { },                // Tick() already drives ReticleUi off `held`
            };
            _fwRig = rig;
            _game.Foot.Weapons = _fw;                        // FootController reads IsRifleHeld/Held/IsStowing off this

            bool ok = _footBelt.WearGear(def.Id, beltObj, def.Sockets, def.Fit, def.Width, def.Back);
            var worn = _footBelt.HolsterSocket;
            Debug.Log($"BELT worn={ok.ToString().ToLowerInvariant()} sockets={(worn?.Sockets?.Count ?? 0)} "
                + $"socket={(worn?.Socket != null ? worn.Socket.name : "<none>")} rig={rig.Id} humanoid={rig.IsHumanoid.ToString().ToLowerInvariant()}");
            // whatever is already owned finds its place now (WearGear only calls
            // ToHolster when nothing is held; a drawn gun stays drawn)
            if (ok && _fw.Held == null) ToHolster();
            return ok;
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
            // Character/CoverShootHero.cs: the purchased Cover+Shoot asset
            // owns on-foot aim/shoot/reload/weapon-draw while this flag is
            // set (user decision, 2026-11-01) — this is registered on
            // GameLoop.CombatSystems and ticks every frame regardless of
            // Mode, unlike FootCover (which only runs inside _foot.Update,
            // already skipped by StageOnFootPurchased), so it needs its own
            // early-return to stay disabled: the arms/holster draw, the
            // on-foot ReticleView toggle and the LMB fire/reload logic all
            // live below this line.
            // GROUND LOOT MUST RUN IN BOTH MODES (2026-11-02). The
            // UsePurchasedShooter early-return used to be the FIRST line of
            // this method, which also disabled Pickups.Update() — the ONLY
            // writer of Pickups.Near — so TakeNearest()'s own
            // `if (Pickups.Near == null) return;` made F a silent no-op under
            // the purchased shooter no matter what read the key. The user hit
            // this as "i cant take gun with f button", and ungating F in
            // GameLoop.ReadDiscreteKeys alone did NOT fix it: necessary, not
            // sufficient. It matters because the two loot systems are
            // disjoint — the akm/beretta/makarov at the warehouse carry
            // Combat/Pickups' PickupTag (WarehouseInteriorFill.cs:407) and
            // have no other taker, while the pack's own InteractiveWeapon
            // loot sits ~320 m away behind a 1 m trigger.
            // So the pickup SCAN, the cards and Take() run in BOTH modes;
            // everything BELOW the flag check (arms/holster draw, the on-foot
            // reticle, LMB fire and reload) stays owned by the purchased asset.
            EnsurePickupUi();
            if (_game.Rig == null)
            {
                // src/pickups.js:270 — setVisible(false) empties the stack, and
                // the card DOM goes with it; VisibleCards is already cleared by
                // the line above, so Render() just hides the view. Without this
                // the cards would freeze on screen for as long as there is no
                // body (a body swap re-spawns the rig).
                Pickups.SetVisible(false);
                PickupUi?.Render(Pickups.VisibleCards);
            }
            else
            {
                Pickups.Update(dt, _sceneRoot, _game.Foot.Pos);
                PickupUi?.Render(Pickups.VisibleCards); // src/pickups.js:93-103 `_render(list)`
            }

            _footBelt?.RideSockets();   // foot.js: the holster sockets ride the skinned strap every frame — in BOTH modes
            _fw?.TickSettle(dt);
            if (GameLoop.UsePurchasedShooter)
            {
                // skillbar.js:353-366 setAmmo off the pack's own magazine
                var sh = CoverShootHero.Instance?.Shoot;
                var aw = sh?.ActiveWeapon;
                if (aw != null) _game.WeaponBar?.SetAmmo(aw.Mag, aw.FullMag, sh.IsReloadingNow);
                // THE ON-FOOT RETICLE WAS NEVER SHOWN UNDER THE PACK (2026-11-02,
                // user: "i dont see aim"): this early return skipped the
                // ReticleView toggle below, and the pack's own crosshair
                // (AimBehaviour.OnGUI) was never given a texture — so a drawn gun
                // had no sight at all. Same view, same rule as the JS
                // (shooting.js:118 visible while a gun is drawn on foot, :135 red
                // while aiming, :520-525 the 0.6x aim cone), read off the pack's
                // own state instead of Gun's.
                EnsureReticleUi();
                bool packDrawn = aw != null && _game.Rig != null && _game.Mode == GameMode.OnFoot;
                bool packAiming = packDrawn && (CoverShootHero.Instance?.Aim?.IsAiming ?? false);
                ReticleUi?.Tick(packDrawn, packAiming, packAiming ? 0.6f : 1f);
                return;
            }
            EnsureReticleUi();
            if (_game.Rig == null)
            {
                ReticleUi?.Tick(false, false, 1f);
                return;
            }
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
                            // src/foot.js:6358 `const frame = reloading ? (chestForward(...)
                            // ?? aim) : aim;` — closes the DEVIATION flagged in this file's
                            // own header: the magazine change now lays out in front of the
                            // chest instead of down the crosshair, which is what keeps it
                            // off the torso in cover (body to the wall, camera along it).
                            Vector3? frame = Gun.Reloading
                                ? (Weapons.ChestForward(_game.Rig.ActGroup, _game.Rig.Root) ?? aim)
                                : aim;
                            GunPoseBlend.Apply(_game.Rig.Root, BodySlots(_game.Rig), hand, frame,
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
            // skillbar.js:353-366 setAmmo — rounds left on the drawn slot's icon.
            if (held && Gun.Ammo.HasValue)
                _game.WeaponBar?.SetAmmo(Mathf.RoundToInt(Gun.Ammo.Value), Pistol.MagSize(def.CartridgeId), Gun.Reloading);

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

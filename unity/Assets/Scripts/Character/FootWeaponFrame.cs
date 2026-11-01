using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using TbilisiFly.Combat;

namespace TbilisiFly.Character
{
    /// <summary>
    /// ================================ STATUS, DECIDED 2026-10-27 ================================
    /// `Core/HeldWeapon.cs` is now confirmed the LIVE held-weapon owner:
    /// registered via `game.CombatSystems.Add(held)`
    /// (`Core/CombatWorldAdapter.cs:498`), ticked every frame by
    /// `GameLoop.cs:889`, and it already calls `RifleHolds.ApplyRifleHold`/
    /// `ApplyRifleSupport`/`ApplyCoverHold` itself (`HeldWeapon.cs:277` etc.) —
    /// a probe run against the real banana/eggplant/toad rigs this session
    /// returned `holdOk=true coverArmsOk=true supportOk=true` on all three.
    /// This file (and the `Character/FootWeapons.cs` stateful model it is
    /// built on — `Held`/`IsRifleHeld`/`_draw`/`_stow`/`_holstered` etc.) has
    /// **zero live callers anywhere in the project**: `grep -rn "new
    /// FootWeapons("` and `grep -rn "FootWeaponFrame" (outside this file)`
    /// both return nothing. `Character/FootBelt.cs` — the fidelity-correct
    /// "skins the strap off the body's own weights" belt implementation
    /// `Core/HeldWeapon.cs`'s own header name-checks — is in the SAME state
    /// (`grep -rn "new FootBelt("` also empty); `HeldWeapon.WearGear` is a
    /// much simpler independent reimplementation (find a bone by name,
    /// parent the object) that does not call into it.
    ///
    /// RECOMMENDATION (not acted on unilaterally — this is a call for
    /// whoever next touches this seam): KEEP this file, do NOT wire it, do
    /// NOT delete it. Reasoning:
    ///   1. It is a complete, reviewed, line-cited port of foot.js:6275-6441
    ///      with real, verified manifest credit (covers=166). The SAME
    ///      foot.js behaviour it implements is already live, correctly,
    ///      through a different model (HeldWeapon.cs + RifleHolds.cs).
    ///      Deleting this file would not change what the shipped game does —
    ///      it would only destroy already-credited, already-reviewed port
    ///      work, and (per docs/port_units/README.md's "claim and an
    ///      exclusion are mutually exclusive" rule) would need a formal
    ///      exclusion filed in its place, for zero behavioural gain.
    ///   2. It is NOT a live double-posing risk today: its OWN dependency
    ///      (`FootWeapons.cs`'s stateful model) is never instantiated, so
    ///      there is no object for `Tick()` to even run against yet. The
    ///      risk this file's own header warns about (both orchestrators
    ///      driving the same bones the same frame) only materialises if a
    ///      future pass instantiates `FootWeapons` AND wires `Tick()` into
    ///      `FootController.LateUpdate` WITHOUT first reconciling with
    ///      `HeldWeapon.cs` — still true, still worth the warning, but not
    ///      an active bug.
    ///   3. The actually-redundant half, if any, is `Character/
    ///      FootWeapons.cs`'s stateful model + `FootBelt.cs` (both orphaned,
    ///      not just this orchestrator over them) — a bigger, riskier
    ///      deletion with its own manifest claims and other real callers
    ///      (`FootClimb.cs:436` uses `FootWeapons.HintMix`, a pure static
    ///      helper unrelated to the stateful model), so not a clean removal
    ///      either and NOT done here.
    /// If this project ever moves to a single held-weapon owner, retiring
    /// `FootWeapons`/`FootBelt`/`FootWeaponFrame` together — once someone
    /// confirms nothing else depends on their pure-function halves — is the
    /// real cleanup; keeping this file alone changes nothing either way.
    /// ================================================================================
    ///
    /// Port of src/foot.js:6275-6441 `_weaponFrame` — the weapon arm's per-frame
    /// hold: idle carry/aim, the roll's tuck-in, the clip's recoil/reload jolt
    /// laid over the hold (gunanim.js's blendReloadPose), the rifle's magazine-
    /// change support hand, and the cover hold / bare-arm fallback behind a
    /// wall. Called every frame the hero is on foot REGARDLESS of who owns the
    /// body — foot.js calls this from its own ordinary `update()` tail AND from
    /// the wind-tunnel's `heldByWind` early-return branch (foot.js:5535), which
    /// is exactly why it was lifted out of `update()` into its own method in
    /// the JS to begin with (foot.js:6265-6274's own comment says so).
    ///
    /// ================================ WHY A STANDALONE CLASS ================================
    /// FootController.cs's header explicitly cuts "_weaponFrame (foot.js:6275-
    /// 6440) — same owner [as the Guns/Weapons system]" from its own scope, and
    /// FootWeapons.cs's own `PresentT` field comment says the method is
    /// "itself out of this port unit's line range" — i.e. deliberately left for
    /// a later pass. This file is that pass. It is a thin ORCHESTRATOR only:
    /// every pose primitive it calls — ApplyRifleHold/ApplyRifleSupport/
    /// ApplyRifleReloadHand/ApplyTuckPose/ApplyCoverHold/ApplyCoverArmsBare
    /// (Combat/RifleHolds.cs), ApplyAimPose/ApplyPistolPose/SeatWeapon/
    /// AttachToHand (Combat/Weapons.cs), GunPoseBlend.Apply (Combat/GunAnim.cs)
    /// — already exists and is called here, never reimplemented (the port
    /// brief's own "680 lines, 15 of 17 duplicated" warning is why this file
    /// greps-before-writing every method name below).
    ///
    /// ================================ DOCUMENTED GAPS (not silently patched) ================
    /// 1. FootController's `_roll`/`_slide`/`_dive` are private with no public
    ///    exposure ("already shipped, not to be edited by this pass" per its
    ///    own header). The `!roll && !slide && !dive` gate on the reload/fire
    ///    blend IS fully portable without a new hook: it is the exact same
    ///    "roll || slide || dive" condition FootWeapons.AcrobaticActive already
    ///    exposes (its own doc comment says so), reused here as `!acro`. Only
    ///    the ROLL-SPECIFIC tuck (`this._roll.tuck`) has no such reuse — `InRoll`
    ///    /`RollTuck` below default to "never", the SAME documented reduced-
    ///    fidelity default FootWeapons.AcrobaticActive itself uses for this
    ///    identical boundary.
    /// 2. FootWeapons.cs's `_handBone` is private with no public getter. This
    ///    file never reads it — it resolves the SAME bone independently every
    ///    frame via `Weapons.FindHandBone`/`FindHandBoneL` off the held
    ///    weapon's side, exactly as foot.js's own `_gunHand` does (foot.js:
    ///    2472) — a pure function of the rig and the side, not stored state,
    ///    so it always agrees with whatever FootWeapons attached the gun to.
    /// 3. Combat/Shooting.cs's `Pistol` exposes `Aiming`/`ReloadBlend`/
    ///    `FireBlend` publicly but not an `AimDir()`, `_reload`/`_reloadFrom`'s
    ///    fraction, or the dry-reload name test. Rather than edit another port
    ///    unit's file, this class takes those as caller-supplied hooks
    ///    (`AimDir`/`Aiming`/`ReloadBlend`/`FireBlend`/`ReloadFrac`/
    ///    `ReloadIsDry`) — the same "constructor-injected hook" convention
    ///    FootWeapons.cs/FootCover.cs already use for every such boundary.
    ///    Defaults (false/0/null) make an unwired instance a safe no-op.
    /// 4. foot.js:6231 `if (this._stow || this._draw) this._moveArm(dt, rig)`
    ///    has no public gate to read (FootWeapons.cs exposes no `_draw`/`_stow`
    ///    flag), so `Tick()` below calls `FootWeapons.MoveArm` every frame
    ///    instead. Documented PERFORMANCE-ONLY deviation: MoveArm's own early
    ///    return when neither is in flight (FootWeapons.cs:1063-1067) makes an
    ///    unconditional call harmless, and THIS method's own hold
    ///    (ApplyRifleHold/ApplyAimPose/ApplyPistolPose, below) runs afterward
    ///    and reseats the gun regardless — exactly the order foot.js itself
    ///    uses (`_moveArm` then `_weaponFrame`, every frame `_weaponFrame`
    ///    runs). `TickSettle` is already self-gating (foot.js:6232's `if
    ///    (this._settle)` lives inside FootWeapons.TickSettle itself).
    /// 5. foot.js's own `else if (cw > 0.001 || this._coverArmsOn)` keeps a
    ///    sticky flag so the bare-arm release runs one more frame after `cw`
    ///    hits exactly 0. `RifleHolds.ApplyCoverArmsBare`'s own `w <= 0.001`
    ///    branch already performs that release unconditionally, so this file
    ///    calls it every frame the hero is bare-handed (not gated on `cw`),
    ///    which reaches the same steady state without the extra flag.
    ///
    /// ================================ HANDEDNESS ================================
    /// No cross product of this file's own — the one site that needs one
    /// (`ChestForward`, mirroring weapons.js:505's `chestForward`) reuses
    /// `Weapons.ChestAxis`'s already-audited `Cross(right, up)` construction
    /// rather than re-deriving it, adding back only the JS's null-on-degenerate
    /// fallback (which `ChestAxis` itself substitutes a safe default for
    /// instead of returning null).
    ///
    /// ================================ LATEUPDATE ================================
    /// Writes bones (every pose call below) — run from the owning
    /// MonoBehaviour's LateUpdate, AFTER FootController.Update has run this
    /// frame (so the body's final orientation — tumble, recline, strafe — is
    /// what the arm solves onto, exactly as foot.js:6219-6224's own comment
    /// explains for why `_weaponFrame` runs last). Do not call this while
    /// `foot.Climb.Active` — foot.js's own `update()` returns before reaching
    /// `_weaponFrame` during a climb (foot.js:5542-5545); gating that is the
    /// caller's job, same as every other mode switch in this port.
    /// </summary>
    public sealed class FootWeaponFrame
    {
        // ---- src/foot.js:435-436 DRAW_SETTLE/DRAW_SETTLE_AMP (verbatim) ----
        internal const float DrawSettle = 0.3f;
        internal const float DrawSettleAmp = 0.08f;

        // =====================================================================
        // ---- hooks this file needs from Combat/Shooting.cs's Pistol (gap 3) ----
        // =====================================================================
        public Func<Vector3?> AimDir = () => null;    // g.pistol?.aimDir()
        public Func<bool> Aiming = () => false;       // g.pistol?.aiming
        public Func<float> ReloadBlend = () => 0f;    // g.pistol?.reloadBlend
        public Func<float> FireBlend = () => 0f;      // g.pistol?.fireBlend
        public Func<float> ReloadFrac = () => 0f;     // 1 - p._reload/p._reloadFrom
        public Func<bool> ReloadIsDry = () => false;  // p._reloadName === 'reloadEmpty'

        // ---- hooks for FootController's private roll state (gap 1) ----
        public Func<bool> InRoll = () => false;
        public Func<float> RollTuck = () => 1f;

        readonly RifleHolds.TuckPoseResult _tuckScratch = new RifleHolds.TuckPoseResult();
        readonly ConditionalWeakTable<CharacterRig, Dictionary<string, Transform>> _slotCache
            = new ConditionalWeakTable<CharacterRig, Dictionary<string, Transform>>();

        static Transform Frame(CharacterRig rig) => rig.ActGroup != null ? rig.ActGroup : rig.Root;

        // src/foot.js:2472 _gunHand — a pure function of the rig/side, not
        // stored state (see class header, gap 2).
        static Transform GunHand(bool rifle, string side, Transform root)
            => rifle && side == "L"
                ? (Weapons.FindHandBoneL(root) ?? Weapons.FindHandBone(root))
                : Weapons.FindHandBone(root);

        // src/weapons.js:505 chestForward — reuses Weapons.ChestAxis's own
        // audited cross product, adding back only the null-on-degenerate case
        // (ChestAxis substitutes Vector3.forward there instead).
        static Vector3? ChestForward(CharacterRig rig, Transform root)
        {
            var chain = Weapons.FindArmChain(root);
            if (!chain.Valid) return null;
            var (up, right) = Weapons.BodyAxes(Frame(rig), root, chain.Upper);
            Vector3 cross = Vector3.Cross(right, up); cross.y = 0f; // weapons.js:509, SWAPPED per project rule
            return cross.sqrMagnitude > 1e-8f ? Weapons.ChestAxis(up, right) : (Vector3?)null;
        }

        Dictionary<string, Transform> SlotsOf(CharacterRig rig)
            => _slotCache.GetValue(rig, r => BoneRoles.SlotsOf(r.Root));

        /// <summary>
        /// One frame of the weapon arm. `coverWeight` is foot.js's `cw` (zero
        /// outside FootCover). See class header for what is and is not wired.
        /// </summary>
        public void Tick(float dt, FootController foot, CharacterRig rig, FootWeapons weapons, float coverWeight)
        {
            // foot.js:6231-6232 — see class header gap 4.
            weapons.MoveArm(dt, rig, foot.Pos);
            weapons.TickSettle(dt);

            var root = rig.Root;
            var held = weapons.Held;
            bool rifle = weapons.IsRifleHeld;
            string side = weapons.RifleSideProvider();
            Transform hand = held != null ? GunHand(rifle, side, root) : null;

            if (held == null || hand == null)
            {
                // foot.js:6437-6439 `else if (cw > 0.001 || this._coverArmsOn)` — see gap 5.
                RifleHolds.ApplyCoverArmsBare(rig, root, coverWeight);
                return;
            }

            bool aiming = Aiming();
            Vector3? raw = AimDir();
            // foot.js:6280 `this._roll || this._slide || this._dive` — identical
            // to FootWeapons.AcrobaticActive's own documented condition (gap 1).
            bool acro = weapons.AcrobaticActive?.Invoke() ?? false;
            Vector3? aim = acro ? (weapons.MoveAimClamp?.Invoke(raw) ?? raw) : raw;

            // foot.js:6287-6295 — the side was switched with the rifle up.
            if (rifle && side != weapons.HeldSide)
            {
                Weapons.AttachToHand(hand, held.Obj.gameObject, held.Def, root);
                weapons.HeldSide = side;
            }

            float reloadBlendVal = ReloadBlend();
            float fireBlendVal = FireBlend();
            bool rReload = rifle && reloadBlendVal > 0.001f;

            // foot.js:6296-6302 — the hold.
            if (rifle)
                RifleHolds.ApplyRifleHold(rig, root, hand, aim, held.Def,
                    rReload ? "reload" : aiming ? "aim" : "carry", side);
            else if (aiming)
                Weapons.ApplyAimPose(Frame(rig), root, Weapons.FindArmChain(root), Weapons.FindArmChainL(root), hand, aim);
            else
                Weapons.ApplyPistolPose(Frame(rig), root, Weapons.FindArmChain(root), hand, aim);

            // foot.js:6303-6321 — through a roll the pistol is pulled to the chest.
            float tuck = (InRoll() && !rifle) ? RollTuck() : 0f;
            Vector3? tuckAim = null;
            if (tuck > 0.001f)
            {
                var res = RifleHolds.ApplyTuckPose(rig, root, hand, _tuckScratch);
                if (res != null)
                {
                    for (int i = 0; i < res.Bones.Count; i++)
                        res.Bones[i].rotation = Quaternion.Slerp(res.Bones[i].rotation, res.From[i], 1f - tuck);
                    Vector3 baseAim = (aim.HasValue && aim.Value.sqrMagnitude > 1e-8f) ? aim.Value : res.Fwd;
                    tuckAim = Vector3.Lerp(baseAim, res.Fwd, tuck).normalized;
                }
            }

            // foot.js:6322-6379 — the clip's recoil/reload jolt, laid over the hold.
            bool reloading = reloadBlendVal > 0.001f;
            float rw = (rifle && side == "L") ? 0f
                : rifle ? fireBlendVal * 0.45f
                : Mathf.Max(reloadBlendVal, fireBlendVal);

            GunPoseBlend.Result gunDir = null;
            if (rw > 0.001f && !acro)
            {
                var gunAnim = weapons.GunAnimSource?.Invoke();
                if (gunAnim != null)
                {
                    var refSnap = reloading ? null : (aiming ? gunAnim.RefAim : gunAnim.RefHip);
                    Vector3? frame = reloading ? (ChestForward(rig, root) ?? aim) : aim;
                    string only = (reloading && rifle) ? (side == "L" ? "r" : "l")
                        : (reloading || aiming || rifle ? null : "r");
                    gunDir = GunPoseBlend.Apply(root, SlotsOf(rig), hand, frame,
                        gunAnim.Dirs, gunAnim.DirsOk, rw, gunAnim, only, refSnap);
                }
            }

            // foot.js:6380-6422 — seat the weapon: the clip's heading while it shows,
            // the reload hold's own line through a rifle's magazine change, else the aim.
            Vector3? seatAim = tuckAim ?? aim;
            Vector3? seatUp = null;
            float presentT = weapons.PresentT;
            float dipExtra = 0f;
            if (presentT >= 0f)
            {
                presentT += dt;
                if (presentT > DrawSettle) presentT = -1f;
                else dipExtra = -DrawSettleAmp * Mathf.Sin(2f * Mathf.PI * presentT / 0.24f) * Mathf.Exp(-presentT / 0.09f);
                weapons.PresentT = presentT;
            }
            float dipHold = aiming ? 0f : rifle ? FootWeapons.RifleDip : Weapons.MuzzleDip;
            float seatDip = dipHold + dipExtra;
            if (gunDir != null)
            {
                Vector3 baseAim = (aim.HasValue && aim.Value.sqrMagnitude > 1e-8f) ? aim.Value : gunDir.F;
                seatAim = Vector3.Lerp(baseAim, gunDir.F, rw).normalized;
                seatUp = Vector3.Lerp(Vector3.up, gunDir.U, rw).normalized;
                seatDip = dipHold * (1f - rw) + dipExtra;
            }
            if (rReload && RifleHolds.TryGetRifleFrame(rig, out _, out var rifleLine, out var rifleUp))
            {
                Vector3 baseAim = (aim.HasValue && aim.Value.sqrMagnitude > 1e-8f) ? aim.Value : rifleLine;
                seatAim = Vector3.Lerp(baseAim, rifleLine, reloadBlendVal).normalized;
                seatUp = Vector3.Lerp(Vector3.up, rifleUp, reloadBlendVal).normalized;
                seatDip = dipHold * (1f - reloadBlendVal) + dipExtra;
            }
            Weapons.SeatWeapon(hand, held.Obj, held.Def, root, seatAim, seatDip, seatUp);

            // foot.js:6423-6432 — the support hand: the reload's own move, or the handguard.
            if (rifle && rReload)
                RifleHolds.ApplyRifleReloadHand(rig, root, held.Obj, held.Def, ReloadFrac(), reloadBlendVal, aim, side, ReloadIsDry());
            else if (rifle)
                RifleHolds.ApplyRifleSupport(rig, root, held.Obj, held.Def, 1f - rw, aim, side);

            // foot.js:6433-6436 — behind the wall, the cover hold takes over.
            float cwHold = coverWeight * (1f - reloadBlendVal);
            if (cwHold > 0.001f) RifleHolds.ApplyCoverHold(rig, root, hand, held.Def, held.Obj, foot, side, cwHold);
        }
    }
}

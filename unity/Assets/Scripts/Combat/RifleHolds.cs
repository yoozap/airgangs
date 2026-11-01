using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using UnityEngine;
using TbilisiFly.Character;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// Port of src/weapons.js's RIFLE-HOLD cluster — the AKM's two-handed
    /// shoulder carry/aim, the magazine-change reload hand, the bladed cover
    /// hold, and the chest tuck used for a roll. Weapons.cs (already shipped)
    /// covers the pistol poses and the shared primitives (AimBone/AimHand/
    /// BodyAxes/AimLine/ApplyReachPose/SeatWeapon/FindArmChain*/FindHandBone*)
    /// this file calls into rather than re-implements.
    ///
    /// Covers: rifleChains (weapons.js:838), applyRifleHold (:848), aimHand is
    /// Weapons.AimHand (reused), applyRifleSupport (:966), boreReach (:1011,
    /// private — matches its non-exported status in the JS), magBone (:1060,
    /// private, same reason), applyRifleReloadHand (:1072), applyTuckPose
    /// (:1181), applyCoverArms (:1233).
    ///
    /// ================================ WIRING ================================
    /// UPDATE: Core/HeldWeapon.cs now IS that per-frame "what is the hero
    /// holding right now" loop and calls ApplyRifleHold/ApplyRifleSupport/
    /// ApplyCoverHold every Tick() while a rifle (or anything, for
    /// ApplyCoverHold) is held — see its own header. ApplyRifleReloadHand/
    /// ApplyTuckPose/ApplyCoverArmsBare/WireCoverGunSide/ShouldAutoFire are
    /// STILL not called from there (HeldWeapon.cs has no concept of a roll or
    /// a reload-support-hand walk — those live on FootController's side of
    /// the fence), same original caveat as below.
    ///
    /// FootController.Armed/AimOverride and FootCover.GunSideProvider/
    /// HipFireWindowOpen/BlindFireWindowOpen (the hooks named in the port
    /// brief) are CONSUMED below by ApplyCoverHold/ApplyCoverArmsBare (a port
    /// of foot.js's own _coverHold/_coverArmsBare, foot.js:3623-3732 — the
    /// glue that was left for "a weapon system" per FootCover.cs's own scope
    /// cut) and by WireCoverGunSide/ShouldAutoFire.
    ///
    /// ALSO FOUND WHILE WIRING: Character/FootWeaponFrame.cs is a second,
    /// independently-built orchestrator over THESE SAME methods (plus
    /// ApplyRifleReloadHand/ApplyTuckPose/ApplyCoverArmsBare), written
    /// against Character/FootWeapons.cs's own held-weapon model rather than
    /// HeldWeapon.cs's Pickups-based one. It is not called from anywhere
    /// either (confirmed by grep) — see Core/HeldWeapon.cs's header for the
    /// full note. The two held-weapon models need reconciling before BOTH
    /// get wired, or the same bones get driven twice.
    ///
    /// ================================ HANDEDNESS =============================
    /// Every `crossVectors(_up, _right)` in weapons.js's rifle-hold code is the
    /// SAME chest-forward construction Weapons.cs's ChestAxis already audited
    /// and swapped (its header's six) — it just appears here un-wrapped at five
    /// more call sites (rifleChains' `cf`, weapons.js:884; boreReach's `fwd0`,
    /// :1018; applyRifleReloadHand's pouch `fwd` and onMag `fwd2`, :1089/:1134;
    /// applyCoverArms' `_fwd`, :1238) instead of through the named helper,
    /// because none of them zero the Y component the way ChestAxis does. Each
    /// is ported through this file's own `ChestLine(up,right)` — `Cross(right,
    /// up)`, operands SWAPPED versus the JS's `cross(up,right)` — same rule,
    /// same five-site count the header promises. `Quaternion.FromToRotation`
    /// (inside the reused Weapons.AimHand) and `Quaternion.AngleAxis` (the
    /// reload roll below) need no swap, per Weapons.cs/CharacterRig.cs's own
    /// precedent: AimHand's FromToRotation is handedness-invariant, and
    /// AngleAxis(angle, physicalAxis) already encodes Unity's own rotation
    /// sense correctly when the axis is a measured real-world direction
    /// (not a cross-product stand-in) — the one case CharacterRig.cs's header
    /// calls out as needing a swap (_turnToward) is not this one.
    ///
    /// ================================ DEVIATIONS (flagged, not silent) =======
    /// 1. applyRifleSupport (weapons.js:966) reads `_up`/`_right` LEFT OVER from
    ///    whatever called bodyAxes earlier the SAME FRAME (normally
    ///    applyRifleHold, on the gun side — the comment there says "_right OUT
    ///    on the gun side"). That is a call-order coupling through module
    ///    globals the JS can get away with; C# has no such globals here, and a
    ///    silent one-call-order-only port is a worse bug than a visible
    ///    re-derivation. ApplyRifleSupport below recomputes BodyAxes itself off
    ///    the GUN chain's own upper arm — exactly the pattern
    ///    applyRifleReloadHand (weapons.js:1078) already uses standalone — so
    ///    this function no longer depends on being called after another one.
    /// 2. _coverHold's blind-arm reach (weapons.js:3691, foot.js) calls
    ///    `applyReachPose` with no `chainIn`, which falls back to
    ///    `rig._armChain` — always the LITERAL RIGHT chain, even when `hand`
    ///    is the LEFT hand (a rifle slung on the left shoulder). ApplyCoverHold
    ///    below passes the already-resolved GUN-side chain instead, so a
    ///    left-slung rifle's blind fire does not solve the wrong arm's IK
    ///    triangle. Flagged, not a silent fix: everywhere else in this file
    ///    the gun-side chain is already threaded through correctly.
    /// 3. bodyAxes (weapons.js:523) ALWAYS tries to fold in the LITERAL LEFT
    ///    chain's shoulder line (`rig._armChainL`), regardless of which `upper`
    ///    the caller passed — not "whichever arm is not the one posing", the
    ///    literal left one. Ported faithfully: every BodyAxes call below passes
    ///    the cached left-chain upper as `upperL`, so the two-shoulder line is
    ///    used exactly when the JS would use it (i.e. not when the call is
    ///    itself posing the left chain, since then upper==upperL and
    ///    Weapons.BodyAxes's own `!=` guard skips it — same as the JS's).
    /// </summary>
    public static class RifleHolds
    {
        // ---- src/weapons.js:753-812 rifle/reload/cover constants (verbatim) ----
        static readonly Vector3 RifleHintR = new Vector3(-0.85f, 0.4f, -0.2f);     // {up,right,fwd} — weapons.js:753
        static readonly Vector3 RifleHintL = new Vector3(-0.85f, 0.35f, 0.05f);    // weapons.js:754
        static readonly Vector3 RifleHintLow = new Vector3(-0.8f, 0.55f, -0.2f);   // weapons.js:761
        const float RiflePocketIn = 0.02f;     // weapons.js:759 RIFLE_POCKET.in
        const float RiflePocketDown = 0.05f;   // weapons.js:759 RIFLE_POCKET.down (`.proud` unused by this cluster)
        const float GripRake = 0.42f;          // weapons.js:760
        const float RifleCarryDip = 0.5f;      // weapons.js:762 (foot.js seats it the same)

        const float ReloadOut = 0.34f, ReloadDown = 0.12f, ReloadSide = 0.06f;    // weapons.js:793-795
        const float ReloadCant = 0.95f, ReloadLean = 0.18f, ReloadRoll = 0.80f;   // weapons.js:796-798
        static readonly Vector3 ReloadHint = new Vector3(-0.7f, 0.35f, 0.1f);     // weapons.js:799

        const float CoverOut = 0.36f, CoverDrop = 0.22f, CoverAcross = 0.08f;     // weapons.js:807-809
        const float CoverTilt = 0.30f;                                           // weapons.js:810
        static readonly Vector3 CoverHint = new Vector3(-0.5f, 0.5f, 0.2f);       // weapons.js:811

        const float BoltGrab = 0.012f;  // weapons.js:815 BOLT_GRAB
        const float MagGrab = 0.03f;    // weapons.js:1059 MAG_GRAB
        const float BoreClear = 0.26f;  // weapons.js:1007 BORE_CLEAR

        // weapons.js:816-826 RELOAD_PATH / RELOAD_PATH_DRY — (fraction, where)
        static readonly (float T, string Name)[] ReloadPath =
        {
            (0.00f, "guard"), (0.16f, "well"), (0.33f, "pouch"),
            (0.55f, "pouch"), (0.72f, "well"), (0.90f, "guard"),
        };
        static readonly (float T, string Name)[] ReloadPathDry =
        {
            (0.00f, "guard"), (0.10f, "well"), (0.30f, "pouch"),
            (0.44f, "well"), (0.58f, "bolt"), (0.92f, "bolt"), (0.97f, "guard"),
        };

        // weapons.js:1228-1232 applyTuckPose/applyCoverArms hints
        readonly struct ArmDir { public readonly float Up, Fwd, Right; public ArmDir(float u, float f, float r) { Up = u; Fwd = f; Right = r; } }
        static readonly ArmDir CoverUpperDir = new ArmDir(-0.80f, 0.32f, 0.24f);
        static readonly ArmDir CoverForeDir = new ArmDir(0.90f, 0.26f, -0.18f);
        static readonly ArmDir CoverSupDir = new ArmDir(-0.55f, 0.60f, -0.30f);
        static readonly ArmDir WallUpperDir = new ArmDir(-0.92f, -0.28f, 0.14f);
        static readonly ArmDir WallForeDir = new ArmDir(-0.88f, -0.30f, 0.12f);

        // foot.js:285/341/355/361/362/363/371/372 cover/blind-arm constants this
        // file's ApplyCoverHold needs (each file in this project keeps its own
        // copy of a shared constant rather than exposing it cross-file — same
        // precedent as FootCover.cs's own `StepUp` comment).
        const float CoverEdge = 0.34f;
        const float BlindReach = 0.3f, BlindReachOff = 0.12f;
        const float BlindHug = -0.12f, BlindHugOff = 0.06f;
        static readonly Vector3 HintBlind = new Vector3(0.3f, 0.5f, -0.6f);
        static readonly Vector3 HintBlindOff = new Vector3(-0.7f, 0.3f, -0.25f);
        static readonly Vector3 HintOver = new Vector3(0.9f, 0.5f, -0.2f);

        static float Smooth01(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        // =====================================================================
        // PER-RIG CACHE — src/weapons.js's `rig._armChain`/`_armChainL`/`_handR`/
        // `_handL`/`_rifleTarget`/`_rifleLine`/`_rifleUp` fields, mutated on the
        // CharacterRig instance in the JS. CharacterRig.cs cannot be edited to
        // add them (hard rule), so they live here, keyed by the rig instance —
        // behaviourally identical lazy-populate-once caching.
        // =====================================================================
        sealed class ChainCache
        {
            public bool Done;
            public Weapons.ArmChain ArmR, ArmL;
            public Transform HandR, HandL;
            public List<Transform> CoverBonesList;
            public Vector3 RifleTarget, RifleLine, RifleUp;
            public bool HasRifle;
        }

        static readonly ConditionalWeakTable<CharacterRig, ChainCache> _cache = new ConditionalWeakTable<CharacterRig, ChainCache>();
        static ChainCache Cache(CharacterRig rig) => _cache.GetOrCreateValue(rig);

        static void EnsureChains(ChainCache c, Transform root)
        {
            if (c.Done) return;
            c.ArmR = Weapons.FindArmChain(root);
            c.ArmL = Weapons.FindArmChainL(root);
            c.HandR = Weapons.FindHandBone(root);
            c.HandL = Weapons.FindHandBoneL(root);
            c.Done = true;
        }

        static Transform Frame(CharacterRig rig) => rig.ActGroup != null ? rig.ActGroup : rig.Root;

        /// <summary>The literal left chain's upper arm, for BodyAxes's `upperL` —
        /// see this file's header note 3 (bodyAxes always tries this one).</summary>
        static Transform LeftUpperOf(CharacterRig rig, Transform root)
        {
            var c = Cache(rig); EnsureChains(c, root);
            return c.ArmL.Valid ? c.ArmL.Upper : null;
        }

        /// <summary>src/weapons.js:505 chestForward / :884,1018,1089,1134,1238's
        /// inline `crossVectors(_up,_right).normalize()` — the SAME construction
        /// as Weapons.ChestAxis but without its y-zero. SWAPPED per this file's
        /// header (operands reversed vs. the JS's cross(up,right)).</summary>
        static Vector3 ChestLine(Vector3 up, Vector3 right) => Vector3.Cross(right, up).normalized;

        // =====================================================================
        // src/weapons.js:838 rifleChains — `side` is the shoulder the rifle is
        // on; that side's arm holds the grip, the other supports the handguard.
        // =====================================================================
        public struct RifleChainSet { public Weapons.ArmChain Gun, Sup; public Transform GunHand, SupHand; }

        public static RifleChainSet RifleChains(CharacterRig rig, Transform root, string side = "R")
        {
            var c = Cache(rig); EnsureChains(c, root);
            return side == "L"
                ? new RifleChainSet { Gun = c.ArmL, GunHand = c.HandL, Sup = c.ArmR, SupHand = c.HandR }
                : new RifleChainSet { Gun = c.ArmR, GunHand = c.HandR, Sup = c.ArmL, SupHand = c.HandL };
        }

        /// <summary>The rifle hold's own line/up, as last computed by
        /// ApplyRifleHold — src/weapons.js's `rig._rifleLine`/`rig._rifleUp`,
        /// which foot.js's `_coverHold`/`_weaponFrame` read so the seated weapon
        /// follows the HOLD through a reload instead of snapping back to the
        /// raw aim. Returns false (frame unset) before ApplyRifleHold has run
        /// at least once this rig.</summary>
        public static bool TryGetRifleFrame(CharacterRig rig, out Vector3 target, out Vector3 line, out Vector3 up)
        {
            var c = Cache(rig);
            target = c.RifleTarget; line = c.RifleLine; up = c.RifleUp;
            return c.HasRifle;
        }

        // =====================================================================
        // src/weapons.js:848 applyRifleHold
        // =====================================================================
        public static bool ApplyRifleHold(CharacterRig rig, Transform root, Transform hand, Vector3? aim,
            WeaponDef def, string mode = "aim", string side = "R")
        {
            var chains = RifleChains(rig, root, side);
            var chain = chains.Gun;
            if (!chain.Valid || hand == null) return false;

            Vector3 fwd = Weapons.AimLine(aim, root.rotation);
            var (up, right) = Weapons.BodyAxes(Frame(rig), root, chain.Upper, LeftUpperOf(rig, root));

            float sc = (def != null && def.Scale > 0f) ? def.Scale : 1f;
            float gripX = (def?.GripPos.x ?? 0f) * sc;
            float gripY = (def?.GripPos.y ?? 0f) * sc;

            bool reload = mode == "reload";
            bool cover = mode == "cover";
            Vector3 cf = (reload || cover) ? ChestLine(up, right) : fwd;
            Vector3 line = cf;
            if (cover)
            {
                line = (up + cf * Mathf.Tan(CoverTilt)).normalized;
            }
            else if (reload)
            {
                line = (up * Mathf.Cos(ReloadCant) - right * Mathf.Sin(ReloadCant) + cf * ReloadLean).normalized;
            }
            else if (mode != "aim")
            {
                line.y -= Mathf.Tan(RifleCarryDip);
                line = line.normalized;
            }

            float butt = sc * ((def != null && def.Measured) ? def.Butt : 0.26f);
            Vector3 upA = (up - line * Vector3.Dot(up, line)).normalized;

            Vector3 S;
            if (cover || reload)
            {
                S = chain.Upper.position
                    + cf * (cover ? CoverOut : ReloadOut)
                    - up * (cover ? CoverDrop : ReloadDown)
                    - right * (cover ? CoverAcross : ReloadSide);
            }
            else
            {
                Vector3? pocket = FootPoses.RifleShoulder(rig, side, fwd);
                S = pocket ?? (chain.Upper.position - right * RiflePocketIn - up * RiflePocketDown + fwd * 0.04f);
            }
            S += line * (butt - gripX) - upA * (0.02f + gripY);

            var c = Cache(rig);
            c.RifleTarget = S; c.RifleLine = line; c.RifleUp = upA; c.HasRifle = true;
            if (reload) c.RifleUp = (Quaternion.AngleAxis(ReloadRoll * Mathf.Rad2Deg * (side == "L" ? -1f : 1f), line) * upA).normalized;
            if (cover) c.RifleUp = cf;

            bool ok = Weapons.ApplyReachPose(Frame(rig), root, chain, hand, S, aim,
                mode == "aim" ? RifleHintR : reload ? ReloadHint : cover ? CoverHint : RifleHintLow);

            if (ok)
            {
                Vector3 dir = (upA * -Mathf.Cos(GripRake) - line * Mathf.Sin(GripRake)).normalized;
                Weapons.AimHand(hand, dir);
            }
            return ok;
        }

        // =====================================================================
        // src/weapons.js:1011 boreReach — private (unexported in the JS too).
        // Slides a point BACK ALONG THE BORE until the given arm (chain/hand)
        // can actually reach it, then forces it clear of the shoulder plane by
        // BORE_CLEAR. `up`/`right` are the caller's own current body axes (see
        // this file's header, deviation 1 — no shared mutable globals here).
        // =====================================================================
        static Vector3 BoreReach(Weapons.ArmChain chain, Transform hand, Transform weaponT,
            float x, float y, float z, Vector3 up, Vector3 right, float lim = 0.93f)
        {
            Vector3 S = chain.Upper.position;
            Vector3 E = chain.Fore.position;
            Vector3 H = hand.position;
            float max = (Vector3.Distance(S, E) + Vector3.Distance(E, H)) * lim;
            Vector3 fwd0 = ChestLine(up, right); // weapons.js:1018 — SWAPPED

            Vector3 Clear(Vector3 p)
            {
                float ahead = Vector3.Dot(p - S, fwd0);
                if (ahead < BoreClear) p += fwd0 * (BoreClear - ahead);
                return p;
            }

            Vector3 outp = weaponT.TransformPoint(new Vector3(x, y, z));
            if (Vector3.Distance(outp, S) <= max) return Clear(outp);

            float lo = -0.25f, hi = x; // as far back as the stock, no further
            for (int i = 0; i < 12; i++)
            {
                float mid = (lo + hi) * 0.5f;
                outp = weaponT.TransformPoint(new Vector3(mid, y, z));
                if (Vector3.Distance(outp, S) > max) hi = mid; else lo = mid;
            }
            outp = weaponT.TransformPoint(new Vector3(lo, y, z));
            return Clear(outp);
        }

        // =====================================================================
        // src/weapons.js:1060 magBone — private. CARGADOR (magazine) / CAMARA
        // (bolt carrier, excluding "...end") bones, found once per weapon
        // INSTANCE (not the shared WeaponDef catalogue entry — each spawned
        // AKM has its own bone instances, exactly as the JS's spread-copy
        // `{...it.def, obj}` gives every pickup its own `_magBone`/`_boltBone`).
        // No `isBone` filter: glTFast imports every node as a plain Transform
        // (same note as GunAnim.cs's BoneRoles.SlotsOf), so this matches by
        // name only.
        // =====================================================================
        sealed class MagCache { public Transform Mag, Bolt; public Vector3? MagHome, BoltHome; public bool Done; }
        static readonly ConditionalWeakTable<Transform, MagCache> _magCache = new ConditionalWeakTable<Transform, MagCache>();

        static MagCache MagBoneOf(Transform weaponT)
        {
            var c = _magCache.GetOrCreateValue(weaponT);
            if (!c.Done)
            {
                foreach (var t in weaponT.GetComponentsInChildren<Transform>(true))
                {
                    if (c.Mag == null && Regex.IsMatch(t.name, "CARGADOR", RegexOptions.IgnoreCase)) c.Mag = t;
                    if (c.Bolt == null && Regex.IsMatch(t.name, "CAMARA", RegexOptions.IgnoreCase) && !Regex.IsMatch(t.name, "end", RegexOptions.IgnoreCase)) c.Bolt = t;
                }
                if (c.Mag != null) c.MagHome = c.Mag.localPosition;
                if (c.Bolt != null) c.BoltHome = c.Bolt.localPosition;
                c.Done = true;
            }
            return c;
        }

        // =====================================================================
        // src/weapons.js:966 applyRifleSupport
        // =====================================================================
        public static bool ApplyRifleSupport(CharacterRig rig, Transform root, Transform weaponT, WeaponDef def,
            float w, Vector3? aim, string side = "R")
        {
            if (!(w > 0.001f) || weaponT == null) return false;
            var chains = RifleChains(rig, root, side);
            var left = chains.Sup;
            var lhand = chains.SupHand;
            if (!left.Valid || lhand == null) return false;

            // See this file's header, deviation 1: recomputed off the GUN chain
            // (falling back to the support chain itself, as the JS's own
            // `chain?.upper ?? left.upper` does in applyRifleReloadHand).
            Transform gunUpper = chains.Gun.Valid ? chains.Gun.Upper : left.Upper;
            var (up, right) = Weapons.BodyAxes(Frame(rig), root, gunUpper, LeftUpperOf(rig, root));

            float guardX = (def != null && def.Measured) ? def.Guard : 0.28f;
            Vector3 grab = BoreReach(left, lhand, weaponT, guardX, -0.035f, 0f, up, right);

            bool blend = w < 0.999f;
            Quaternion savedU = default, savedF = default, savedH = default;
            if (blend) { savedU = left.Upper.rotation; savedF = left.Fore.rotation; savedH = lhand.rotation; }

            bool ok = Weapons.ApplyReachPose(Frame(rig), root, left, lhand, grab, aim, RifleHintL);
            if (ok)
            {
                Vector3 bore = weaponT.TransformDirection(Vector3.right); // model +X -> bore
                Vector3 dir = (bore - right * 0.35f + up * 0.25f).normalized;
                Weapons.AimHand(lhand, dir);
            }
            if (ok && blend)
            {
                left.Upper.rotation = Quaternion.Slerp(left.Upper.rotation, savedU, 1f - w);
                left.Fore.rotation = Quaternion.Slerp(left.Fore.rotation, savedF, 1f - w);
                lhand.rotation = Quaternion.Slerp(lhand.rotation, savedH, 1f - w);
            }
            return ok;
        }

        // =====================================================================
        // src/weapons.js:1072 applyRifleReloadHand — the support hand through a
        // magazine change: handguard -> well -> belt pouch -> (dry: the
        // charging-handle rack) -> well -> handguard, with the hand riding the
        // magazine/bolt-carrier bones themselves once the clip starts moving
        // them. Only the support arm is driven from the clip's own motion — the
        // FPS reload clip's gun pose puts the rifle INSIDE the body, so the gun
        // itself keeps following the HOLD (ApplyRifleHold's own target),
        // never the clip (src/weapons.js's own framing of this, and this
        // project's recorded "reload clip puts the rifle in the body" gotcha).
        // =====================================================================
        public static bool ApplyRifleReloadHand(CharacterRig rig, Transform root, Transform weaponT, WeaponDef def,
            float u, float w, Vector3? aim, string side, bool dry)
        {
            if (!(w > 0.001f) || weaponT == null) return false;
            var chains = RifleChains(rig, root, side);
            var left = chains.Sup;
            var lhand = chains.SupHand;
            if (!left.Valid || lhand == null) return false;
            var chain = chains.Gun;

            Transform gunUpper = chain.Valid ? chain.Upper : left.Upper;
            var (up, right) = Weapons.BodyAxes(Frame(rig), root, gunUpper, LeftUpperOf(rig, root));
            float guardX = (def != null && def.Measured) ? def.Guard : 0.28f;
            Vector3 fwdChest = ChestLine(up, right); // weapons.js:1089/1134 — SWAPPED

            Vector3 At(string name)
            {
                if (name == "guard") return BoreReach(left, lhand, weaponT, guardX, -0.035f, 0f, up, right);
                if (name == "well") return BoreReach(left, lhand, weaponT, guardX * 0.30f, -0.165f, 0f, up, right);
                if (name == "bolt") return BoreReach(left, lhand, weaponT, guardX * 0.52f, 0.055f, 0.03f, up, right);
                // "pouch": the belt line, support side, a hand's width in front
                // of it. BASED AT THE GUN SHOULDER (weapons.js:1087-1090 — `out`
                // starts from `chain.upper`, the gun-side chain, NOT `left.upper`)
                // but CLAMPED to no further than the SUPPORT arm's own reach
                // (`S`/`Sh` in the JS is `left.upper` — a different bone than the
                // one the point is built from). Conflating the two (using
                // left.Upper for both) was this file's first draft bug.
                Vector3 o = gunUpper.position + fwdChest * 0.32f - right * 0.22f - up * 0.30f;
                Vector3 clampS = left.Upper.position;
                float maxR = (Vector3.Distance(clampS, left.Fore.position)
                    + Vector3.Distance(left.Fore.position, lhand.position)) * 0.93f;
                float d = Vector3.Distance(o, clampS);
                if (d > maxR) o = clampS + (o - clampS) * (maxR / d);
                float ahead = Vector3.Dot(o - clampS, fwdChest);
                if (ahead < BoreClear) o += fwdChest * (BoreClear - ahead);
                return o;
            }

            var path = dry ? ReloadPathDry : ReloadPath;
            int i = 0;
            while (i < path.Length - 1 && u >= path[i + 1].T) i++;
            var a = path[i];
            var b = path[Mathf.Min(i + 1, path.Length - 1)];
            float span = Mathf.Max(1e-4f, b.T - a.T);
            float k = Mathf.Clamp01((u - a.T) / span);
            float kk = k * k * (3f - 2f * k);
            Vector3 target = (a.Name == b.Name) ? At(a.Name) : Vector3.Lerp(At(a.Name), At(b.Name), kk);

            // the moment the magazine leaves the well, the hand is on the
            // magazine instead — it is the thing being moved, and the clip is
            // moving it (weapons.js:1114-1139).
            var mag = MagBoneOf(weaponT);
            float onMag = 0f;
            if (mag.Mag != null && mag.MagHome.HasValue)
            {
                onMag = Mathf.Clamp01(Vector3.Distance(mag.Mag.localPosition, mag.MagHome.Value) / MagGrab);
                if (onMag > 0.001f)
                {
                    Vector3 mp = mag.Mag.position + weaponT.TransformDirection(new Vector3(0f, -1f, 0f)) * 0.05f;
                    float maxR = (Vector3.Distance(left.Upper.position, left.Fore.position)
                        + Vector3.Distance(left.Fore.position, lhand.position)) * 0.93f;
                    float d = Vector3.Distance(mp, left.Upper.position);
                    if (d > maxR) mp = left.Upper.position + (mp - left.Upper.position) * (maxR / d);
                    float ahead = Vector3.Dot(mp - left.Upper.position, fwdChest);
                    if (ahead < BoreClear) mp += fwdChest * (BoreClear - ahead);
                    target = Vector3.Lerp(target, mp, onMag);
                }
            }
            // THE RACK (weapons.js:1142-1152)
            if (mag.Bolt != null && mag.BoltHome.HasValue)
            {
                float bt = Mathf.Clamp01(Vector3.Distance(mag.Bolt.localPosition, mag.BoltHome.Value) / BoltGrab);
                if (bt > 0.001f)
                {
                    Vector3 bp = mag.Bolt.position + weaponT.TransformDirection(new Vector3(0f, 1f, 0f)) * 0.03f;
                    target = Vector3.Lerp(target, bp, bt);
                }
            }

            bool blend = w < 0.999f;
            Quaternion savedU = default, savedF = default, savedH = default;
            if (blend) { savedU = left.Upper.rotation; savedF = left.Fore.rotation; savedH = lhand.rotation; }

            bool ok = Weapons.ApplyReachPose(Frame(rig), root, left, lhand, target, aim, RifleHintL);
            if (ok)
            {
                // on the handguard the hand lies along the barrel; at the well
                // and the pouch it comes up from underneath, as a magazine is held
                Vector3 bore = weaponT.TransformDirection(Vector3.right);
                float onGun = ((a.Name == "guard" ? 1f - kk : 0f) + (b.Name == "guard" ? kk : 0f)) * (1f - onMag);
                Vector3 d1 = up * -1f + bore * 0.35f;
                Vector3 d2 = bore - right * 0.35f + up * 0.25f;
                Vector3 dir = Vector3.Lerp(d1, d2, onGun).normalized;
                Weapons.AimHand(lhand, dir);
            }
            if (ok && blend)
            {
                left.Upper.rotation = Quaternion.Slerp(left.Upper.rotation, savedU, 1f - w);
                left.Fore.rotation = Quaternion.Slerp(left.Fore.rotation, savedF, 1f - w);
                lhand.rotation = Quaternion.Slerp(lhand.rotation, savedH, 1f - w);
            }
            return ok;
        }

        // =====================================================================
        // src/weapons.js:1181 applyTuckPose — pistol pulled to the chest, both
        // hands on it, for a roll. `result` is caller-owned and reused every
        // frame (no per-frame allocation), mirroring the JS's `out` parameter.
        // =====================================================================
        public sealed class TuckPoseResult
        {
            public readonly List<Transform> Bones = new List<Transform>();
            public readonly List<Quaternion> From = new List<Quaternion>();
            public Vector3 Fwd;
        }

        public static TuckPoseResult ApplyTuckPose(CharacterRig rig, Transform root, Transform hand, TuckPoseResult result)
        {
            var c = Cache(rig); EnsureChains(c, root);
            var right = c.ArmR; var left = c.ArmL;
            if (!right.Valid || hand == null) return null;

            var (up, bodyRight) = Weapons.BodyAxes(Frame(rig), root, right.Upper, left.Valid ? left.Upper : null);
            Vector3 fwd = (Frame(rig).rotation * new Vector3(0f, 0f, 1f)).normalized; // HANDEDNESS: Unity forward +Z

            result.Bones.Clear();
            result.From.Clear();
            void Save(Transform b) { result.Bones.Add(b); result.From.Add(b.rotation); }

            Save(right.Upper);
            Vector3 dir = (up * -0.85f + fwd * 0.30f + bodyRight * 0.20f).normalized;
            Weapons.AimBone(right.Upper, right.Fore, dir);
            Save(right.Fore);
            dir = (fwd * 0.55f + up * 0.70f + bodyRight * -0.30f).normalized;
            Weapons.AimBone(right.Fore, hand, dir);

            if (left.Valid)
            {
                Save(left.Upper);
                dir = (up * -0.6f + fwd * 0.45f + bodyRight * -0.35f).normalized;
                Weapons.AimBone(left.Upper, left.Fore, dir);
                Transform lhand = left.Fore.childCount > 0 ? left.Fore.GetChild(0) : null;
                Vector3 toHand = hand.position - left.Fore.position;
                if (lhand != null && toHand.sqrMagnitude > 1e-8f) { Save(left.Fore); Weapons.AimBone(left.Fore, lhand, toHand.normalized); }
            }
            result.Fwd = (fwd + up * 0.45f).normalized;
            return result;
        }

        // =====================================================================
        // src/weapons.js:1233 applyCoverArms — back to the wall: bare-handed,
        // both arms hang behind the body, palms to the brick; with a pistol,
        // GTA's high ready (up beside the shoulder, muzzle to the sky). The
        // rifle is NOT here — foot.js runs ApplyRifleHold at a steep aim for it.
        // =====================================================================
        public struct CoverArmsResult { public bool Ok; public Vector3 Aim, Up; }

        public static CoverArmsResult ApplyCoverArms(CharacterRig rig, Transform root, Transform hand, bool armed)
        {
            var c = Cache(rig); EnsureChains(c, root);
            var right = c.ArmR; var left = c.ArmL;
            if (!right.Valid) return default;

            var (up, bodyRight) = Weapons.BodyAxes(Frame(rig), root, right.Upper, left.Valid ? left.Upper : null);
            Vector3 fwd = ChestLine(up, bodyRight); // weapons.js:1238 — SWAPPED

            Vector3 DirOf(ArmDir hv, float mirror) => (up * hv.Up + fwd * hv.Fwd + bodyRight * (hv.Right * mirror)).normalized;
            Transform TipOf(Weapons.ArmChain ch) => ch.Fore.childCount > 0 ? ch.Fore.GetChild(0) : null;

            if (!armed)
            {
                Transform rh = hand != null ? hand : TipOf(right);
                Weapons.AimBone(right.Upper, right.Fore, DirOf(WallUpperDir, 1f));
                if (rh != null) Weapons.AimBone(right.Fore, rh, DirOf(WallForeDir, 1f));
                if (left.Valid)
                {
                    Transform lh = TipOf(left);
                    Weapons.AimBone(left.Upper, left.Fore, DirOf(WallUpperDir, -1f));
                    if (lh != null) Weapons.AimBone(left.Fore, lh, DirOf(WallForeDir, -1f));
                }
                return new CoverArmsResult { Ok = true };
            }

            if (hand == null) return default;
            Weapons.AimBone(right.Upper, right.Fore, DirOf(CoverUpperDir, 1f));
            Vector3 foreDir = DirOf(CoverForeDir, 1f);
            Weapons.AimBone(right.Fore, hand, foreDir);
            Vector3 aimOut = foreDir;                                   // the pistol stands up the forearm's line
            Vector3 upOut = (-fwd + up * 0.25f).normalized;             // slide toward the chest

            if (left.Valid)
            {
                Weapons.AimBone(left.Upper, left.Fore, DirOf(CoverSupDir, -1f));
                Transform lh = TipOf(left);
                Vector3 toHand = hand.position - left.Fore.position;
                if (lh != null && toHand.sqrMagnitude > 1e-8f) Weapons.AimBone(left.Fore, lh, toHand.normalized);
            }
            return new CoverArmsResult { Ok = true, Aim = aimOut, Up = upOut };
        }

        // =====================================================================
        // foot.js:3622 _coverBones — both arms' bones, gun side first per the
        // JS's OWN COMMENT, but the code it wrote always walks [R, L] regardless
        // of which side the rifle is slung — ported literally (the comment is
        // aspirational, the code is the spec).
        // =====================================================================
        static List<Transform> CoverBones(CharacterRig rig, Transform root)
        {
            var c = Cache(rig);
            if (c.CoverBonesList != null) return c.CoverBonesList;
            EnsureChains(c, root);
            var list = new List<Transform>();
            foreach (var ch in new[] { c.ArmR, c.ArmL })
            {
                if (!ch.Valid) continue;
                list.Add(ch.Upper);
                list.Add(ch.Fore);
                if (ch.Fore.childCount > 0) list.Add(ch.Fore.GetChild(0));
            }
            c.CoverBonesList = list;
            return list;
        }

        /// <summary>src/foot.js:3237 _gunSide — which side the gun hand is on,
        /// as a sign (not an index): a rifle slung left is the only case that
        /// flips it; pistols stay right-handed.</summary>
        static int GunSideOf(WeaponDef def, string side) => (WeaponDefs.IsRifle(def) && side == "L") ? -1 : 1;

        /// <summary>Wires FootCover.GunSideProvider (the hook it exposes for
        /// exactly this) to the rifle-side rule above. Call once when a hero's
        /// weapon/side settings are known; nothing in this build calls it yet
        /// (see this file's header — no owner exists for "what is held right
        /// now" as a per-frame concept). `heldProvider`/`sideProvider` let the
        /// caller supply that state however it ends up being tracked.</summary>
        public static void WireCoverGunSide(FootCover cover, System.Func<WeaponDef> heldProvider, System.Func<string> sideProvider)
        {
            if (cover == null) return;
            cover.GunSideProvider = () => GunSideOf(heldProvider?.Invoke(), sideProvider?.Invoke() ?? "R");
        }

        /// <summary>Consumes FootCover.HipFireWindowOpen/BlindFireWindowOpen —
        /// the gate the port brief named. A future Shooting.cs integration
        /// calls this once per frame while `foot.Armed` to decide whether cover
        /// is itself asking for a shot (auto hip-fire while held, or the
        /// buffered blind-fire trigger); nothing calls it yet, same caveat as
        /// WireCoverGunSide.</summary>
        public static bool ShouldAutoFire(FootController foot, bool aiming)
            => foot != null && (foot.Cover.HipFireWindowOpen(aiming) || foot.Cover.BlindFireWindowOpen());

        // =====================================================================
        // foot.js:3623 _coverHold — THE HOLD BEHIND THE WALL: the pistol up
        // beside the shoulder (ApplyCoverArms) or the rifle stood up in its
        // pocket (ApplyRifleHold 'cover' + ApplyRifleSupport), blended in by how
        // far behind the wall the hero is (`w`), with the blind arm laid over
        // either hold while `foot.Cover`'s blind-fire is out. This is the glue
        // that makes Armed/AimOverride/Cover.State actually move bones — see
        // this file's header WIRING note for why nothing calls it yet.
        // =====================================================================
        public static void ApplyCoverHold(CharacterRig rig, Transform root, Transform hand, WeaponDef heldDef,
            Transform weaponT, FootController foot, string side, float w)
        {
            if (heldDef == null || weaponT == null || hand == null || foot == null || !foot.Cover.Active) return;
            var c = foot.Cover.State;
            var bones = CoverBones(rig, root);
            if (bones.Count == 0) return;

            var savedQ = new Quaternion[bones.Count];
            for (int i = 0; i < bones.Count; i++) savedQ[i] = bones[i].rotation;
            Quaternion savedGunQ = weaponT.rotation;
            Vector3 savedGunP = weaponT.position;

            Transform frame = Frame(rig);
            Vector3 up = frame.rotation * Vector3.up;
            Vector3 fwd = frame.rotation * new Vector3(0f, 0f, 1f); // HANDEDNESS: Unity forward +Z

            bool rifle = WeaponDefs.IsRifle(heldDef);
            var chains = RifleChains(rig, root, side);

            if (rifle)
            {
                // high ready, in front of him — not the shoulder pocket, which on
                // a body this deep put both hands inside it (foot.js's own comment)
                Vector3 aim = (up * 0.95f + fwd * 0.3f).normalized;
                ApplyRifleHold(rig, root, hand, aim, heldDef, "cover", side);
                TryGetRifleFrame(rig, out _, out var line, out var rifleUp);
                Weapons.SeatWeapon(hand, weaponT, heldDef, root, line, 0f, rifleUp);
                ApplyRifleSupport(rig, root, weaponT, heldDef, 1f, aim, side);
            }
            else
            {
                var res = ApplyCoverArms(rig, root, hand, true);
                if (res.Ok) Weapons.SeatWeapon(hand, weaponT, heldDef, root, res.Aim, 0f, res.Up);
            }

            Vector3? aimOverride = foot.AimOverride;
            if (c != null && c.Blind > 0.01f && aimOverride.HasValue)
            {
                int take = Mathf.Min(3, bones.Count);
                var blindQ = new Quaternion[take];
                for (int i = 0; i < take; i++) blindQ[i] = bones[i].rotation;

                int bs = c.BlindSide;
                bool own = bs == GunSideOf(heldDef, side);
                Vector3 target = c.Base;
                if (bs != 0)
                {
                    // foot.js:3684 `bones[0]` — literally the FIRST cover bone,
                    // which _coverBones' own [R,L] order (this file's CoverBones,
                    // same quirk) makes the RIGHT upper arm regardless of which
                    // side is the gun — not necessarily chains.Gun. Matched here,
                    // not "corrected", since JS clearly intends `bones[0]`.
                    float shY = bones[0].position.y - foot.Pos.y;
                    target += c.T * (bs * (CoverEdge + (own ? BlindReach : BlindReachOff)));
                    target += c.N * (own ? BlindHug : BlindHugOff);
                    target.y += shY + 0.04f;
                }
                else
                {
                    target += -c.N * (c.Off + 0.12f);
                    target.y += c.Top + 0.22f;
                }
                // deviation 2 (this file's header): the gun-side chain, not the
                // JS's un-sided fallback.
                Vector3 hint = bs != 0 ? (own ? HintBlind : HintBlindOff) : HintOver;
                bool ok = Weapons.ApplyReachPose(frame, root, chains.Gun, hand, target, aimOverride, hint);
                if (ok)
                {
                    Weapons.SeatWeapon(hand, weaponT, heldDef, root, aimOverride, 0f);
                    if (rifle) ApplyRifleSupport(rig, root, weaponT, heldDef, 1f, aimOverride, side);
                }
                float k = 1f - Smooth01(c.Blind);
                for (int i = 0; i < take; i++) bones[i].rotation = Quaternion.Slerp(bones[i].rotation, blindQ[i], k);
            }

            if (w < 0.999f)
            {
                for (int i = 0; i < bones.Count; i++) bones[i].rotation = Quaternion.Slerp(bones[i].rotation, savedQ[i], 1f - w);
                weaponT.rotation = Quaternion.Slerp(weaponT.rotation, savedGunQ, 1f - w);
                weaponT.position = Vector3.Lerp(weaponT.position, savedGunP, 1f - w);
            }
        }

        // =====================================================================
        // foot.js:3709 _coverArmsBare — bare-handed behind the wall, booked
        // through CharacterRig.BoneSet so the clip gets its arms back the
        // moment the wall is left.
        // =====================================================================
        public static void ApplyCoverArmsBare(CharacterRig rig, Transform root, float w)
        {
            var bones = CoverBones(rig, root);
            if (bones.Count == 0) return;
            if (w <= 0.001f)
            {
                foreach (var b in bones) rig.ReleaseBone(b);
                return;
            }
            var clipQ = new Quaternion[bones.Count];
            for (int i = 0; i < bones.Count; i++) clipQ[i] = rig.ClipQ(bones[i]);
            ApplyCoverArms(rig, root, null, false);
            for (int i = 0; i < bones.Count; i++)
            {
                var b = bones[i];
                Quaternion q = Quaternion.Slerp(b.localRotation, clipQ[i], 1f - w);
                b.localRotation = clipQ[i];   // the clip's value back, so BoneSet books it as the base
                rig.BoneSet(b, q);
            }
        }
    }
}

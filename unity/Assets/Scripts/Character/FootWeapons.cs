using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using TbilisiFly.Combat;
using TbilisiFly.Core;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Port of src/foot.js:2125-3051 — the weapon DRAW / STOW / HOLSTER state
    /// machine and the arm that carries the gun while it moves. A standalone,
    /// constructor-injected class: nothing here is a MonoBehaviour and nothing
    /// here reaches for a concrete `GameLoop`/`Pistol`/settings type, because
    /// three other agents are building those concurrently (see WIRING below).
    ///
    /// Covers, cited by JS line number throughout: `_toHolster` (2125),
    /// `_seatInPocket` (2180), `_clearBody` (2295, DEAD CODE — see note at its
    /// own method), `_backSocket` (2322), `_toBack` (2389), `_fromBack` (2409),
    /// `_fromHolster` (2417), `drawWeapon` (2434), `_rifleSide` (2466),
    /// `_gunHand` (2472), `_aimOnDraw` (2480), `holster` (2491), `_startStow`
    /// (2542), `_startDraw` (2599), `_updateDraw` (2662), `_finishDraw` (2756),
    /// `_updateStow` (2772), `_updateSettle` (2850), `_moveArm` (2879),
    /// `_slingWaypoint` (2989), `_slingStand` (3009), `_gunReachEnv` (3026).
    /// Also ports the four small file-level helpers foot.js:2125-3051 calls
    /// into (bendThrough/muzzleUp/slerpPast, foot.js:459-514, and the
    /// HINT_*/DRAW_*/SLING_* constant table, foot.js:420-471) as private
    /// members of this class — they have no other owner and no other caller.
    ///
    /// ================================ SCOPE CUT ================================
    /// NOT here: the belt/gear-fitting measurement (foot.js:1093-2124 —
    /// `_ringOf`/`_gearMeshes`/`_bodySection`/`_bodyMeshes`/`_skinXform`/the
    /// `worn` map/`holsterSocket` getter) — FootBelt.cs's territory, being
    /// written concurrently. `IFootBelt` below is the exact, narrow contract
    /// this file needs from it (see its own doc comment). FootBelt.cs DID NOT
    /// EXIST YET at the time this file was written, so `IFootBelt` is this
    /// port's own best-effort specification of that boundary, not a read of
    /// the real class — whoever wires the two together may need to adapt
    /// FootBelt.cs's actual public surface to match it, or add an adapter.
    /// Also not here: `_moveAim` (foot.js:4594, the body-lean aim clamp during
    /// a roll/slide/dive — now ported as FootAcrobatics.MoveAim/
    /// MoveAimInBasis, unwired pending that file's own forward-axis probe) and
    /// the roll/slide/dive flags themselves (FootController's private
    /// `_roll`/`_slide`/`_dive`) — both consumed through the
    /// `MoveAimClamp`/`AcrobaticActive` hooks below because
    /// FootController.cs (already shipped, not to be edited by this pass)
    /// exposes neither publicly today. Until FootController adds that
    /// exposure, leave both hooks null: `AcrobaticActive` defaults to "false"
    /// (never clamps) and `MoveAimClamp` defaults to the identity passthrough
    /// — a documented reduced-fidelity default, not a crash.
    ///
    /// ================================ WIRING ========================
    /// This file is the one place `Armed`/`IsRifleHeld`/`RifleSideProvider`
    /// FootController.cs's own header asks for "FROM that system" are defined.
    /// Wire them (none of this happens automatically):
    ///   footController.Armed      = footWeapons.Armed;              // every frame
    ///   footCover.GunSideProvider = () => footWeapons.RifleSide() == "L" ? -1 : 1;
    /// `AimDirSource` below should ultimately point at the camera/reticle
    /// system (Combat/Shooting.cs's `Pistol`-equivalent) — this file only
    /// RELAYS it (through the pose solvers), it does not produce it.
    ///
    /// ================================ LATEUPDATE ================================
    /// `MoveArm` writes bones (the gun arm's reach pose, the carry/idle blend,
    /// the support hand) and must run from the owning MonoBehaviour's
    /// LateUpdate, after the Animator — same rule as every other file in this
    /// package. It assumes FootController's own Update already ran this frame
    /// (so SwingArms/SwingBody's idle arm pose is the "idle" this file blends
    /// away from/back to).
    ///
    /// ================================ HANDEDNESS ================================
    /// Three body axes are forward -Z, up +Y, right +X; Unity's are forward
    /// +Z, up +Y, right +X (right/up unchanged, forward's label flips — the
    /// project's own stated rule). This file's two genuine handedness sites:
    ///   1. `BackSocket`'s own basis construction (two cross products, swapped
    ///      per the project rule, plus the forward-seed literal and the
    ///      "which end of the body is the back" scan both flipped — see that
    ///      method's own comments for the exact reasoning).
    ///   2. `SeatInPocket`'s `sAx = cross(mAx, uAx)` (swapped).
    /// Gun-model-local axes (+X=muzzle, +Y=slide-top, +Z=right — the
    /// convention Combat/Weapons.cs's SeatWeapon/ApplyRifleSupport already use
    /// unchanged between engines) and the belt-socket-local axes
    /// import_belt.py authored the same way are NOT flipped: importing a
    /// standalone GLB is a different pipeline from the world/city data
    /// GeoFrame mirrors, and Combat/Weapons.cs already established "model +X
    /// -> forward (muzzle)" holds in both engines. `BackSocket`'s "which
    /// shoulder" sign test is the SAME pattern FootPoses.cs's own
    /// `AimOffFacing`/`RifleShoulder` already use UNFLIPPED (a Humanoid
    /// Avatar's `HumanBodyBones.RightUpperArm` is retargeting-normalised to
    /// the character's own +X right side, regardless of the source asset's
    /// raw numbers) — ported the same way here for the same reason, not
    /// re-derived. All quaternion ops (FromToRotation/AngleAxis/Slerp
    /// composition) are handedness-invariant per the project's established
    /// rule and need no swap.
    ///
    /// ================================ DEVIATION (flagged, not silent) ==========
    /// `_startDraw`/`_moveArm` resolve the arm CHAIN for the two-bone reach
    /// pose and for the carry-pose snapshot/restore by the GUN'S OWN SIDE
    /// (`RifleSide()`), not always the literal right chain the way foot.js's
    /// own `_moveArm` does (it never passes `chainIn` to `applyReachPose`,
    /// which then falls back to `rig._armChain` — always the right chain,
    /// even for a rifle slung on the left shoulder). This is the exact same
    /// mismatch Combat/RifleHolds.cs's own header already documents and fixes
    /// at its deviation #1/#2 for `applyRifleSupport`/`_coverHold`; this port
    /// makes the same call here for the same reason (a silently-wrong left
    /// shoulder is worse than a flagged, intentional fix) and changes nothing
    /// for the default right-handed case, which is unaffected.
    /// </summary>
    public sealed class FootWeapons
    {
        // ================= src/foot.js:430-471 timings/hints (verbatim) =================
        // `internal` (not private) so FootWeaponsProbe.cs — a different
        // assembly, Assembly-CSharp-Editor — can read these exact values via
        // AssemblyInfo.cs's InternalsVisibleTo, the same pattern DamageProbe.cs
        // established (see that file's own fix note).
        internal const float DrawReach = 0.17f, DrawGrip = 0.05f, DrawLift = 0.13f, DrawPresent = 0.22f;
        internal const float DrawDur = DrawReach + DrawGrip + DrawLift + DrawPresent; // 0.57s
        internal static readonly Vector3 HintReach = new Vector3(-0.25f, 0.75f, -0.6f);  // (up,right,fwd)
        internal static readonly Vector3 HintLift = new Vector3(0.6f, 0.65f, -0.5f);
        internal static readonly Vector3 HintCarry = new Vector3(-0.75f, 0.55f, -0.35f);
        internal const float SlingReach = 0.22f, SlingGrip = 0.05f, SlingLift = 0.2f, SlingPresent = 0.34f;
        internal const float SlingDur = SlingReach + SlingGrip + SlingLift + SlingPresent; // 0.81s
        internal const float SlingStow = 0.95f;
        internal const float SlingOut = 0.14f;
        internal const float SlingUp = 0.34f;
        internal const float SlingWayOut = 0.14f;
        internal const float SlingWayUp = 0.3f;
        internal const float SlingStandFwd = 0.3f;
        internal static readonly Vector3 HintSling = new Vector3(0.75f, 0.6f, -0.35f);
        internal static readonly Vector3 HintRifle = new Vector3(-0.85f, 0.4f, -0.2f);

        /// <summary>src/foot.js:478-483 `hintMix` — the named lerp every hand-
        /// hint blend in this ladder (and FootClimb.cs's hang→pull transition,
        /// foot.js:5292) goes through. JS reuses one scratch `{up,right,fwd}`
        /// object across calls to dodge an allocation every frame; `Vector3`
        /// is already a value type in C#, so the componentwise lerp is the
        /// whole port — no scratch object to carry. `internal` so FootClimb.cs
        /// (same assembly, same namespace) and this file's own call sites below
        /// share the one named helper instead of each inlining `Vector3.Lerp`
        /// under a different name — the gap a port-coverage audit flagged:
        /// the math was already right everywhere, just never given this name.</summary>
        internal static Vector3 HintMix(Vector3 a, Vector3 b, float k) => Vector3.Lerp(a, b, k);
        internal const float RifleDip = 0.5f; // weapons.js:761 RIFLE_CARRY_DIP == foot.js:471 RIFLE_DIP; RifleHolds.cs keeps its own private copy, this file keeps its own too (project convention — see FootController.StepUp's own comment)
        internal const float FootFov = 62f;   // src/foot.js:22 FOOT_FOV

        // =====================================================================
        // ---- dependencies (constructor-injected; see class header WIRING) ----
        // =====================================================================
        readonly IFootBelt _belt;
        readonly Transform _sceneRoot;

        /// <summary>src/foot.js `g.weapons.carried` — every weapon the hero owns
        /// (never includes gear; this port's CarriedWeapon type only models actual
        /// weapons, so the JS's own `w.kind !== 'gear'` filter is automatically
        /// satisfied and not re-checked here).</summary>
        public Func<IEnumerable<CarriedWeapon>> CarriedWeapons;
        /// <summary>Whichever GunAnim (pistol or rifle spec) applies to the
        /// CURRENTLY relevant weapon — the owner swaps this as the held kind
        /// changes, mirroring `g.gunAnim` being reassigned elsewhere in the JS
        /// (outside this port's own line range).</summary>
        public Func<GunAnim> GunAnimSource;
        /// <summary>The raw aim ray (camera/reticle-resolved) — src/foot.js's
        /// `g.pistol.aimDir()`. This file only relays/clamps it.</summary>
        public Func<Vector3?> AimDirSource;
        /// <summary>src/foot.js:4594 `_moveAim` — IS ported, as
        /// FootAcrobatics.MoveAim/MoveAimInBasis. Not wired to this hook yet:
        /// that file's own header leaves the body-forward axis unresolved
        /// ("DO NOT WIRE EITHER METHOD UNTIL A PROBE DECIDES IT") and nothing
        /// calls it today. Null = identity passthrough.</summary>
        public Func<Vector3?, Vector3?> MoveAimClamp;
        /// <summary>True while a roll/slide/dive is active — src/foot.js's
        /// `this._roll || this._slide || this._dive`. Not exposed by
        /// FootController.cs today; null/unset = always false (never clamps,
        /// a documented reduced-fidelity default — see class header).</summary>
        public Func<bool> AcrobaticActive;
        public Func<bool> PistolAiming;
        public Action<bool> SetPistolAiming;
        public Action<bool> SetReticleVisible;
        public Func<bool> AimOnDrawEnabled = () => true;     // g.settings?.state?.aimOnDraw !== false
        public Func<bool> RifleLeftShoulder = () => false;    // g.settings?.state?.side === 'left'
        public Func<float> SoundVolume = () => 0.7f;
        /// <summary>g.camera — optional; null skips the FOV-on-holster effect
        /// entirely rather than throwing.</summary>
        public Camera Cam;
        /// <summary>this._cover — read-only (FootCover.cs already shipped; not
        /// edited by this pass). Null = never in cover.</summary>
        public FootCover Cover;

        public FootWeapons(IFootBelt belt, Transform sceneRoot)
        {
            _belt = belt;
            _sceneRoot = sceneRoot;
        }

        // =====================================================================
        // ---- state — mirrors src/foot.js's OnFoot instance fields (this file's
        // own slice of them) directly ----
        // =====================================================================
        /// <summary>this.active — set every frame by the owner, same convention
        /// as FootController.Armed.</summary>
        public bool Active;
        /// <summary>this.held.</summary>
        public CarriedWeapon Held;
        /// <summary>this._modelId — debug-log only.</summary>
        public string ModelId;
        /// <summary>this._heldSide — which shoulder/hand the CURRENTLY HELD
        /// weapon is on, cached at draw time.</summary>
        public string HeldSide = "R";
        /// <summary>this._presentT — s since a draw landed the gun in the hand;
        /// consumed by foot.js:6392-6398 `_weaponFrame` (foot.js:6275-6440),
        /// itself out of this port unit's line range. Exposed so that future
        /// pass can read it without this file needing to know about it.</summary>
        public float PresentT = -1f;

        readonly List<CarriedWeapon> _holstered = new List<CarriedWeapon>();
        CarriedWeapon _inHolster;
        readonly List<CarriedWeapon> _onBack = new List<CarriedWeapon>();
        (CharacterRig rig, Transform sock) _backSock;
        Transform _handBone;

        sealed class DrawState
        {
            public CarriedWeapon Gun;
            public Transform Bone, Sock;
            public float T, Dur;
            public Matrix4x4 InSock;
            public bool Rifle;
            public Vector3 FromP; public Quaternion FromQ; public float FromS;
            public Vector3 LocalP; public Quaternion LocalQ; public Vector3 LocalS;
            public Vector3 MouthP; public Quaternion MouthQ; public float MouthS;
            public Matrix4x4 HandFromGun;
            public bool Leather;
        }
        sealed class StowState
        {
            public CarriedWeapon Gun;
            public float T, Dur;
            public bool Rifle;
            public Transform Sock;
            public Vector3 FromP; public Quaternion FromQ; public float FromS;
            public Matrix4x4 HandFromGun;
            public float ToS;
        }
        sealed class SettleState { public float T; public Quaternion Q; public CarriedWeapon Gun; }

        DrawState _draw;
        StowState _stow;
        SettleState _settle;

        // =====================================================================
        // ---- hooks this file EXPOSES for FootController/FootCover to consume
        // (see class header WIRING) ----
        // =====================================================================
        public bool Armed => Held != null;
        public bool IsRifleHeld => Held != null && WeaponDefs.IsRifle(Held.Def);
        public string RifleSideProvider() => RifleSide();

        /// <summary>src/foot.js:6164 `!this._stow` (gates the aim-twist want
        /// off while the weapon is on its way to the holster) — FootController
        /// needs this one bit without owning _stow itself.</summary>
        public bool IsStowing => _stow != null;

        /// <summary>src/foot.js:6203 `(this._draw ?? this._stow)?.rifle` — which
        /// shoulder-reach animation is live right now, read fresh every frame
        /// (so it flips the instant a draw finishes into nothing in flight).</summary>
        public bool DrawOrStowRifle => _draw != null ? _draw.Rifle : (_stow != null && _stow.Rifle);

        // =====================================================================
        // ---- small math helpers (src/foot.js:459-514, file-level, no other
        // owner) ----
        // =====================================================================

        /// <summary>src/foot.js:459 bendThrough — a quadratic Bezier that passes
        /// through `via` at its midpoint. Pure vector lerp arithmetic — no
        /// handedness concern.</summary>
        static Vector3 BendThrough(Vector3 a, Vector3 via, Vector3 b, float t)
        {
            Vector3 ctrl = via * 2f - a * 0.5f - b * 0.5f;
            float u = 1f - t;
            return a * (u * u) + ctrl * (2f * u * t) + b * (t * t);
        }

        /// <summary>src/foot.js:497 muzzleUp — the same orientation turned so its
        /// LOCAL +X (the muzzle) points straight up, by the smallest rotation
        /// that does it. Quaternion composition only — handedness-invariant.</summary>
        static Quaternion MuzzleUp(Quaternion q)
        {
            Vector3 a = q * Vector3.right;
            Quaternion toUp = Quaternion.FromToRotation(a, Vector3.up);
            return (toUp * q).normalized;
        }

        /// <summary>src/foot.js:505 slerpPast — a to b by t, where t may run past
        /// 1 (a slerp cannot overshoot; a presented pistol does). Quaternion
        /// axis-angle math only — handedness-invariant.</summary>
        static Quaternion SlerpPast(Quaternion a, Quaternion b, float t)
        {
            Quaternion d = b * Quaternion.Inverse(a);
            d.Normalize();
            if (d.w < 0f) d = new Quaternion(-d.x, -d.y, -d.z, -d.w);
            float w = Mathf.Min(1f, d.w);
            float sn = Mathf.Sqrt(Mathf.Max(1e-16f, 1f - w * w));
            float ang = 2f * Mathf.Acos(w);
            if (ang < 1e-6f) return b;
            Vector3 axis = new Vector3(d.x / sn, d.y / sn, d.z / sn);
            Quaternion scaled = Quaternion.AngleAxis(ang * t * Mathf.Rad2Deg, axis);
            return (scaled * a).normalized;
        }

        static float Smooth01(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        /// <summary>Decomposes a general TRS matrix without `Quaternion.LookRotation`
        /// (the skill's own warning: LookRotation's internal "right" uses the
        /// opposite operand order and silently mirrors local X) — uses
        /// Combat.Weapons.QuaternionFromBasis's Shepherd's-method construction
        /// from the matrix's own orthonormalised columns instead.</summary>
        static void DecomposeTRS(Matrix4x4 m, out Vector3 pos, out Quaternion rot, out Vector3 scale)
        {
            pos = m.GetColumn(3);
            Vector3 cx = m.GetColumn(0), cy = m.GetColumn(1), cz = m.GetColumn(2);
            scale = new Vector3(cx.magnitude, cy.magnitude, cz.magnitude);
            Vector3 x = scale.x > 1e-9f ? cx / scale.x : Vector3.right;
            Vector3 y = scale.y > 1e-9f ? cy / scale.y : Vector3.up;
            Vector3 z = scale.z > 1e-9f ? cz / scale.z : Vector3.forward;
            rot = Weapons.QuaternionFromBasis(x, y, z);
        }

        static void SetRenderersVisible(Transform t, bool visible)
        {
            if (t == null) return;
            foreach (var r in t.GetComponentsInChildren<Renderer>(true)) r.enabled = visible;
        }

        // =====================================================================
        // ---- src/foot.js:2125 _toHolster ----
        // Park the owned-but-not-drawn pistol(s) on the belt. Does nothing when
        // there is no belt on. A rifle is dealt with first and never counts for
        // a holster pocket.
        // =====================================================================
        public bool ToHolster(CharacterRig rig)
        {
            var belt = _belt?.HolsterSocket;
            foreach (var w in CarriedWeapons?.Invoke() ?? Array.Empty<CarriedWeapon>())
            {
                if (w?.Obj != null && WeaponDefs.IsRifle(w.Def) && w != Held && !_onBack.Contains(w))
                    ToBack(rig, w);
            }
            if (belt?.Sockets == null || belt.Sockets.Length == 0) return false;
            var owned = new List<CarriedWeapon>();
            foreach (var w in CarriedWeapons?.Invoke() ?? Array.Empty<CarriedWeapon>())
                if (w?.Obj != null && !WeaponDefs.IsRifle(w.Def) && w != Held) owned.Add(w);
            if (owned.Count == 0) return false;
            FromHolster(null);
            for (int i = 0; i < belt.Sockets.Length && i < owned.Count; i++)
            {
                var gun = owned[i];
                var obj = gun.Obj;
                var sock = belt.Sockets[i];
                // biasWorn: not ported — Weapons.cs's own documented decision
                // (Unity's ordinary depth test replaces the polygon-offset trick).
                SetRenderersVisible(obj, true); // frustumCulled=false has no direct Unity equivalent worth porting here; visibility is handled explicitly below regardless
                obj.SetParent(sock, false);
                obj.localPosition = Vector3.zero;
                obj.localRotation = Quaternion.identity;
                float sockScale = sock.lossyScale.x; if (Mathf.Abs(sockScale) < 1e-6f) sockScale = 1f;
                obj.localScale = Vector3.one * (gun.Scale * belt.HolsterScale / sockScale);
                SetRenderersVisible(obj, true);
                SeatInPocket(belt, sock, gun);
                _holstered.Add(gun);
            }
            _inHolster = _holstered.Count > 0 ? _holstered[0] : null;
            return _holstered.Count > 0;
        }

        // =====================================================================
        // ---- src/foot.js:2180 _seatInPocket ----
        // Seats a holster SOCKET (not the gun) so the part of the gun below the
        // mouth sits centred between the pocket's own walls, muzzle a few
        // millimetres off its bottom. Once per gun per socket.
        // =====================================================================
        void SeatInPocket(WornBelt belt, Transform sock, CarriedWeapon gun)
        {
            if (sock == null || gun?.Obj == null) return;
            var data = belt.DataFor(sock);
            if (data.SeatedForGunId == gun.Id) return;
            Transform root = belt.Obj;
            if (root == null) return;
            Matrix4x4 inv = root.worldToLocalMatrix;
            Vector3 sp = inv.MultiplyPoint3x4(sock.position);
            float az = Mathf.Atan2(sp.z, sp.x);
            Quaternion sq = Quaternion.Inverse(root.rotation) * sock.rotation; // belt-local
            Vector3 mAx = (sq * Vector3.right).normalized;     // muzzle: down the pocket (asset-local convention, no flip)
            Vector3 uAx = (sq * Vector3.up).normalized;        // slide top: forward
            Vector3 sAx = Vector3.Cross(uAx, mAx).normalized;  // HANDEDNESS: JS cross(mAx,uAx) -> swapped
            float Along(Vector3 p, Vector3 a) => Vector3.Dot(p - sp, a);

            var pocket = data.Pocket;
            if (!pocket.HasValue)
            {
                Vector3? bandCenterWorld = _belt?.RingCenterWorld(belt);
                if (!bandCenterWorld.HasValue) return;
                float bandY = inv.MultiplyPoint3x4(bandCenterWorld.Value).y;
                var ss = new List<float>(); var us = new List<float>();
                float mTop = float.PositiveInfinity, mBot = float.NegativeInfinity;
                foreach (var vWorld in _belt?.GearVertsWorld(belt) ?? Array.Empty<Vector3>())
                {
                    Vector3 v = inv.MultiplyPoint3x4(vWorld);
                    if (v.y > bandY - 0.03f) continue;                       // the band itself
                    float bearing = Mathf.Atan2(v.z, v.x);
                    float d = Mathf.Atan2(Mathf.Sin(bearing - az), Mathf.Cos(bearing - az));
                    if (Mathf.Abs(d) > 0.5f) continue;
                    ss.Add(Along(v, sAx)); us.Add(Along(v, uAx));
                    float mm = Along(v, mAx);
                    if (mm < mTop) mTop = mm;
                    if (mm > mBot) mBot = mm;
                }
                if (ss.Count < 40) return; // no pocket here
                ss.Sort(); us.Sort();
                float Q(List<float> arr, float f) => arr[Mathf.FloorToInt((arr.Count - 1) * f)];
                pocket = new PocketMetrics
                {
                    SLo = Q(ss, 0.05f), SHi = Q(ss, 0.95f), ULo = Q(us, 0.05f), UHi = Q(us, 0.95f),
                    MTop = mTop, MBot = mBot,
                };
                data.Pocket = pocket;
            }

            // the gun AS IT WOULD HANG in this socket
            float sockScale = sock.lossyScale.x; if (Mathf.Abs(sockScale) < 1e-6f) sockScale = 1f;
            float gs = gun.Scale * belt.HolsterScale / sockScale;
            Matrix4x4 gunM = sock.localToWorldMatrix * Matrix4x4.Scale(Vector3.one * gs);
            Matrix4x4 gunInv = gun.Obj.worldToLocalMatrix;
            int n = 0; float sSum = 0f, sMin = float.PositiveInfinity, mMax = float.NegativeInfinity;
            float uLo = float.PositiveInfinity, uHi = float.NegativeInfinity;
            foreach (var mf in gun.Obj.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Matrix4x4 m = inv * gunM * (gunInv * mf.transform.localToWorldMatrix);
                var verts = mf.sharedMesh.vertices;
                foreach (var vLocal in verts)
                {
                    Vector3 gv = m.MultiplyPoint3x4(vLocal);
                    float mm = Along(gv, mAx);
                    if (mm > mMax) mMax = mm;
                    float u = Along(gv, uAx);
                    if (u < uLo) uLo = u;
                    if (u > uHi) uHi = u;
                    if (mm < pocket.Value.MTop) continue;
                    float ssv = Along(gv, sAx);
                    sSum += ssv; n++;
                    if (ssv < sMin) sMin = ssv;
                }
            }
            foreach (var smr in gun.Obj.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                var baked = new Mesh();
                smr.BakeMesh(baked);
                Matrix4x4 m = inv * gunM * (gunInv * smr.transform.localToWorldMatrix);
                foreach (var vLocal in baked.vertices)
                {
                    Vector3 gv = m.MultiplyPoint3x4(vLocal);
                    float mm = Along(gv, mAx);
                    if (mm > mMax) mMax = mm;
                    float u = Along(gv, uAx);
                    if (u < uLo) uLo = u;
                    if (u > uHi) uHi = u;
                    if (mm < pocket.Value.MTop) continue;
                    float ssv = Along(gv, sAx);
                    sSum += ssv; n++;
                    if (ssv < sMin) sMin = ssv;
                }
                UnityEngine.Object.Destroy(baked);
            }
            if (n == 0) return;

            float extra = data.StandExtra;
            float ds = Mathf.Max((pocket.Value.SLo + pocket.Value.SHi) / 2f - sSum / n, (pocket.Value.SLo + extra) - sMin);
            float du = (pocket.Value.ULo + pocket.Value.UHi) / 2f - (uLo + uHi) / 2f;
            float dm = (pocket.Value.MBot - 0.004f) - mMax;

            Vector3 spNew = sp + sAx * ds + uAx * du + mAx * dm;
            sock.position = root.localToWorldMatrix.MultiplyPoint3x4(spNew);
            data.SeatedForGunId = gun.Id;
            _belt?.ResyncRide(belt, sock); // src/foot.js:1816 _skinXform + 2276-2280 — optional, see IFootBelt
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            float Mm(float x) => Mathf.Round(x * 1000f * (Mathf.Abs(root.lossyScale.x) > 1e-6f ? root.lossyScale.x : 1f));
            Debug.Log($"holster: {gun.Id} seated {Mm(du)}mm along, {Mm(ds)}mm out, {Mm(-dm)}mm down into the pocket "
                + $"(pocket s {Mm(pocket.Value.SLo)}..{Mm(pocket.Value.SHi)} u {Mm(pocket.Value.ULo)}..{Mm(pocket.Value.UHi)} "
                + $"m {Mm(pocket.Value.MTop)}..{Mm(pocket.Value.MBot)}; gun s {Mm(sSum / n)} u {Mm(uLo)}..{Mm(uHi)} m {Mm(mMax)}, n {n})");
#endif
        }

        // =====================================================================
        // ---- src/foot.js:2295 _clearBody ----
        // DEAD CODE IN THE JS: defined, never called anywhere in src/*.js (verified
        // by grep across the whole source tree). Ported anyway, faithfully, per
        // the port brief's exact line list — nothing wires it up on either side.
        // Pushes a held/holstered object's socket out far enough to clear the
        // body section at the object's own height.
        // =====================================================================
        public void ClearBody(Transform obj, Transform socket, CharacterRig rig, Vector3 heroPos)
        {
            if (obj == null || socket == null) return;
            Bounds box = WorldBoundsOf(obj);
            Vector3 c = box.center;
            BodySection? sect = _belt?.BodySectionAt(rig, c.y);
            if (!sect.HasValue) return;
            Vector3 axis = new Vector3(heroPos.x, c.y, heroPos.z);
            Vector3 outV = new Vector3(c.x - axis.x, 0f, c.z - axis.z);
            float r = outV.magnitude;
            if (r < 1e-4f) return;
            float need = sect.Value.R + Mathf.Max(box.size.x, box.size.z) * 0.35f;
            if (r >= need) return; // already clear
            outV *= (need - r) / r; // the shortfall, in world
            Vector3 before = socket.position;
            Vector3 after = socket.InverseTransformPoint(before + outV);
            obj.localPosition += after; // in the socket's own frame
        }

        static Bounds WorldBoundsOf(Transform obj)
        {
            Bounds? b = null;
            foreach (var r in obj.GetComponentsInChildren<Renderer>(true))
                b = b.HasValue ? Encapsulate(b.Value, r.bounds) : r.bounds;
            return b ?? new Bounds(obj.position, Vector3.zero);
        }
        static Bounds Encapsulate(Bounds a, Bounds b) { a.Encapsulate(b); return a; }

        // =====================================================================
        // ---- src/foot.js:2322 _backSocket ----
        // WHERE A RIFLE RIDES when put away: slung across the back, muzzle down
        // by the left hip, stock up at the right shoulder, the receiver flat
        // against the back. Measured once per rig off the back's own surface at
        // chest height and hung off the chest bone.
        // =====================================================================
        Transform BackSocket(CharacterRig rig)
        {
            if (rig == null) return null;
            if (_backSock.rig == rig && _backSock.sock != null) return _backSock.sock;
            Transform chest = rig.Anim != null
                ? (rig.Anim.GetBoneTransform(HumanBodyBones.Chest) ?? rig.Anim.GetBoneTransform(HumanBodyBones.Spine))
                : null;
            if (chest == null) return null;
            Matrix4x4 inv = rig.Root.worldToLocalMatrix;
            Vector3 cp = inv.MultiplyPoint3x4(chest.position);

            // the widest point of the back over the stretch the rifle lies
            // across — HANDEDNESS: Unity's rig-local "back" is -Z (the JS's is
            // +Z, since forward is -Z there and +Z here) — so the JS's "find
            // the MAX v.z" becomes "find the MIN v.z" here, and every additive
            // "push further into the back" below is negated to match.
            // THE BACK IS +Z ON THIS RIG (measured 2026-11-02, GunHandRun): the hero
            // FACES root-local -Z (FootController's heading is (-sin, 0, -cos);
            // CoverShootHero.facingSourceNegativeZ = true; the chase camera sits at
            // +Z and photographs the back). The comment above assumed the body faces
            // +Z and flipped the JS's "MAX v.z" to MIN — which put the sling on the
            // hero's FRONT: a slung rifle at fwd=-0.905 drawing 0 pixels from behind.
            // The JS convention (max z = the back) is the right one here, unflipped.
            float bestZ = float.NegativeInfinity;
            float backY = cp.y;
            foreach (var vWorld in _belt?.BodyVertsWorld(rig) ?? Array.Empty<Vector3>())
            {
                Vector3 v = inv.MultiplyPoint3x4(vWorld);
                if (Mathf.Abs(v.y - cp.y) >= 0.2f || Mathf.Abs(v.x) >= 0.12f) continue;
                if (v.z > bestZ) { bestZ = v.z; backY = v.y; }
            }
            if (float.IsNegativeInfinity(bestZ)) { bestZ = 0.15f; backY = cp.y; } // JS fallback backZ=+0.15 — same sign here (back is +Z)

            // which way is the body's right, in the rig frame — UNFLIPPED, same
            // reasoning FootPoses.AimOffFacing/RifleShoulder already use: a
            // Humanoid Avatar's RightUpperArm is retargeting-normalised to the
            // character's own +X right side, so this measurement needs no
            // GeoFrame/glTFast-import adjustment here.
            Transform upR = rig.IsHumanoid ? rig.Anim?.GetBoneTransform(HumanBodyBones.RightUpperArm) : null;
            if (upR == null)
            {
                foreach (var t in rig.Root.GetComponentsInChildren<Transform>(true))
                {
                    if (Regex.IsMatch(t.name, "^(upperarm_r|rightarm|upperarmr|bras1\\.D)$", RegexOptions.IgnoreCase)) { upR = t; break; }
                }
            }
            float rs = 1f;
            if (upR != null)
            {
                Vector3 p = inv.MultiplyPoint3x4(upR.position);
                rs = p.x < 0f ? -1f : 1f;
            }

            // the socket's own axes ARE the slung pose: +X the muzzle (down and
            // to the left), +Z the rifle's right side (against the back), +Y
            // what is left (asset/gun-local convention, unflipped per class header)
            Vector3 X = new Vector3(-rs * 0.45f, -0.89f, 0f).normalized;
            Vector3 fwdSeed = new Vector3(0f, 0f, -1f); // the hero FACES root-local -Z (see the back-search note above)
            Vector3 Y = Vector3.Cross(X, fwdSeed).normalized;  // HANDEDNESS: JS cross(Z,X) -> swapped
            Vector3 Z = Vector3.Cross(Y, X).normalized;        // HANDEDNESS: JS cross(X,Y) -> swapped
            float posY = backY + 0.02f;
            float posZ = bestZ + 0.02f; // JS: backZ + 0.02, into the +Z back — same here
            Vector3 pos = new Vector3(rs * 0.05f, posY, posZ);
            Matrix4x4 m = Matrix4x4.TRS(pos, Weapons.QuaternionFromBasis(X, Y, Z), Vector3.one);

            // THE CHEST'S WORLD MATRIX, READ AGAIN HERE (Unity's transforms stay
            // live, so no stale-matrix gotcha to guard against the way the JS
            // comment warns of — but the reasoning for parenting rigidly to the
            // chest below, rather than skin-binding, is the same one the JS
            // documents: a skinned bind's matrix only refreshes when something
            // walks the render graph, which does not happen mid-measurement).
            Matrix4x4 local = chest.worldToLocalMatrix * rig.Root.localToWorldMatrix * m;
            DecomposeTRS(local, out Vector3 sockPos, out Quaternion sockRot, out Vector3 sockScale);
            var sockGo = new GameObject("back_sling");
            sockGo.transform.SetParent(chest, false);
            sockGo.transform.localPosition = sockPos;
            sockGo.transform.localRotation = sockRot;
            sockGo.transform.localScale = sockScale;
            _backSock = (rig, sockGo.transform);
            return sockGo.transform;
        }

        // =====================================================================
        // ---- src/foot.js:2389 _toBack / 2409 _fromBack ----
        // =====================================================================
        public bool ToBack(CharacterRig rig, CarriedWeapon gun)
        {
            Transform sock = BackSocket(rig);
            if (sock == null || gun?.Obj == null) return false;
            Transform obj = gun.Obj;
            obj.SetParent(sock, false);
            obj.localPosition = Vector3.zero;
            obj.localRotation = Quaternion.identity;
            float ws = sock.lossyScale.x; if (Mathf.Abs(ws) < 1e-6f) ws = 1f;
            obj.localScale = Vector3.one * (gun.Scale / ws);
            SetRenderersVisible(obj, true);
            gun.BaseScaleY = null;
            gun.Anim?.Reset();
            if (!_onBack.Contains(gun)) _onBack.Add(gun);
            return true;
        }

        public bool FromBack(CarriedWeapon gun)
        {
            int i = gun == null ? -1 : _onBack.IndexOf(gun);
            if (i < 0) return false;
            _onBack.RemoveAt(i);
            if (gun.Obj != null) gun.Obj.SetParent(null, true);
            return true;
        }

        // =====================================================================
        // ---- src/foot.js:2417 _fromHolster ----
        // Take one weapon off the belt, or all of them when none is named.
        // =====================================================================
        public CarriedWeapon FromHolster(CarriedWeapon which)
        {
            var keep = new List<CarriedWeapon>();
            foreach (var gun in _holstered)
            {
                if (which != null && gun != which) { keep.Add(gun); continue; }
                if (gun?.Obj != null) gun.Obj.SetParent(null, true);
            }
            CarriedWeapon taken = which ?? (_holstered.Count > 0 ? _holstered[0] : null);
            _holstered.Clear(); _holstered.AddRange(keep);
            _inHolster = _holstered.Count > 0 ? _holstered[0] : null;
            return taken;
        }

        // =====================================================================
        // ---- src/foot.js:2434 drawWeapon ----
        // Hang the weapon's own object off the hero's hand bone. Out of the
        // holster it is a MOVE, not a teleport, when the belt/back is ready for
        // the animated sequence; otherwise it appears directly (unchanged).
        // =====================================================================
        public bool DrawWeapon(CharacterRig rig, CarriedWeapon weapon)
        {
            if (weapon?.Obj == null || rig == null) return false;
            Holster(rig);
            Transform bone = GunHand(rig, weapon);
            HeldSide = RifleSide();
            if (bone == null)
            {
                Debug.LogWarning($"no hand bone on {ModelId} — weapon not drawn");
                return false;
            }
            if (Active && _draw == null
                && ((_holstered.Contains(weapon) && _belt?.HolsterSocket != null && (GunAnimSource?.Invoke()?.Ready ?? false))
                    || _onBack.Contains(weapon)))
            {
                if (StartDraw(rig, weapon, bone)) return true;
            }
            FromHolster(weapon);
            FromBack(weapon);
            Weapons.AttachToHand(bone, weapon.Obj.gameObject, weapon.Def, rig.Root);
            _handBone = bone;
            SetRenderersVisible(weapon.Obj, true);
            Held = weapon;
            SetReticleVisible?.Invoke(true);
            AimOnDraw();
            FxAudio.DrawGun(SoundVolume());
            return true;
        }

        // =====================================================================
        // ---- src/foot.js:2466 _rifleSide / 2472 _gunHand / 2480 _aimOnDraw ----
        // =====================================================================
        public string RifleSide() => RifleLeftShoulder() ? "L" : "R";

        Transform GunHand(CharacterRig rig, CarriedWeapon weapon)
        {
            Transform root = rig.Root;
            return (WeaponDefs.IsRifle(weapon.Def) && RifleSide() == "L")
                ? (Weapons.FindHandBoneL(root) ?? Weapons.FindHandBone(root)) : Weapons.FindHandBone(root);
        }

        void AimOnDraw()
        {
            if (!AimOnDrawEnabled()) return;
            if (Cover != null && Cover.Active) return;
            SetPistolAiming?.Invoke(true);
        }

        // =====================================================================
        // ---- src/foot.js:2491 holster ----
        // =====================================================================
        public void Holster(CharacterRig rig)
        {
            if (_draw != null) FinishDraw(false);
            if (Held != null) FxAudio.DrawGun(SoundVolume());
            if (Held?.Obj != null && Active && _stow == null
                && (WeaponDefs.IsRifle(Held.Def) || (_belt?.HolsterSocket != null && (GunAnimSource?.Invoke()?.Ready ?? false))))
            {
                if (StartStow(rig)) return;
            }
            _handBone = null;
            SetReticleVisible?.Invoke(false);
            if (Cam != null && !Mathf.Approximately(Cam.fieldOfView, FootFov))
                Cam.fieldOfView = FootFov;
            var was = Held;
            if (was?.Obj == null)
            {
                Held = null;
                ToHolster(rig); // owned but never drawn — still goes on the belt
                return;
            }
            was.Obj.SetParent(null, true);
            was.Obj.localScale = Vector3.one;
            Held = null;
            was.BaseScaleY = null;
            was.Anim?.Reset();
            ToHolster(rig);
        }

        // =====================================================================
        // ---- src/foot.js:2542 _startStow ----
        // RED DEAD's holster: the arm carries the weapon down and across on a
        // sagging arc, muzzle-first into the pocket, then settles.
        // =====================================================================
        bool StartStow(CharacterRig rig)
        {
            var gun = Held;
            var belt = _belt?.HolsterSocket;
            bool rifle = WeaponDefs.IsRifle(gun.Def);
            Transform sock = rifle ? BackSocket(rig) : belt?.Sockets != null && belt.Sockets.Length > 0 ? belt.Sockets[0] : null;
            if (sock == null) return false;
            if (!rifle) SeatInPocket(belt, sock, gun);
            Matrix4x4 from = gun.Obj.localToWorldMatrix;
            Matrix4x4 handFromGun = Matrix4x4.TRS(gun.Obj.localPosition, gun.Obj.localRotation, gun.Obj.localScale).inverse;
            gun.Obj.SetParent(_sceneRoot, false);
            DecomposeTRS(from, out Vector3 fp, out Quaternion fq, out Vector3 fs);
            gun.Obj.position = fp; gun.Obj.rotation = fq; gun.Obj.localScale = fs;
            gun.BaseScaleY = null;
            Held = null;
            SetReticleVisible?.Invoke(false);
            if (Cam != null && !Mathf.Approximately(Cam.fieldOfView, FootFov)) Cam.fieldOfView = FootFov;
            var anim = GunAnimSource?.Invoke();
            float clipDur = anim != null ? anim.ClipTime("holster") : 0f;
            float dur = rifle ? SlingStow : (clipDur > 0f ? clipDur : 0.46f);
            _stow = new StowState
            {
                Gun = gun, T = 0f, Dur = Mathf.Max(0.34f, dur), Rifle = rifle, Sock = sock,
                FromP = gun.Obj.position, FromQ = gun.Obj.rotation, FromS = gun.Obj.localScale.x != 0f ? gun.Obj.localScale.x : 1f,
                HandFromGun = handFromGun,
                ToS = rifle ? gun.Scale : gun.Scale * belt.HolsterScale,
            };
            if (!rifle) anim?.Play("holster", force: true, fade: 0.05f);
            return true;
        }

        // =====================================================================
        // ---- src/foot.js:2599 _startDraw ----
        // The draw is the stow run backwards and NOT symmetric: slow against the
        // leather, then fast into the hand.
        // =====================================================================
        bool StartDraw(CharacterRig rig, CarriedWeapon weapon, Transform bone)
        {
            var belt = _belt?.HolsterSocket;
            bool rifle = WeaponDefs.IsRifle(weapon.Def);
            Transform sock = rifle ? BackSocket(rig) : belt?.Sockets != null && belt.Sockets.Length > 0 ? belt.Sockets[0] : null;
            if (sock == null) return false;
            Matrix4x4 from = weapon.Obj.localToWorldMatrix;
            FromHolster(weapon);
            FromBack(weapon);

            Vector3? raw = AimDirSource?.Invoke();
            Vector3? aim = (AcrobaticActive?.Invoke() ?? false) ? (MoveAimClamp?.Invoke(raw) ?? raw) : raw;
            string side = RifleSide();
            // DEVIATION (see class header): the gun-side chain, not always right.
            Weapons.ArmChain chain = side == "L" ? Weapons.FindArmChainL(rig.Root) : Weapons.FindArmChain(rig.Root);
            Quaternion savedUpper = default, savedFore = default;
            bool haveChain = chain.Valid;
            if (haveChain) { savedUpper = chain.Upper.localRotation; savedFore = chain.Fore.localRotation; }
            bool aiming = (PistolAiming?.Invoke() ?? false) || (rifle && AimOnDrawEnabled());
            if (rifle) RifleHolds.ApplyRifleHold(rig, rig.Root, bone, aim, weapon.Def, aiming ? "aim" : "carry", side);
            else Weapons.ApplyPistolPose(rig.ActGroup, rig.Root, chain, bone, aim);
            Weapons.AttachToHand(bone, weapon.Obj.gameObject, weapon.Def, rig.Root);
            Weapons.SeatWeapon(bone, weapon.Obj, weapon.Def, rig.Root, aim, aiming ? 0f : (rifle ? RifleDip : Weapons.MuzzleDip));
            Vector3 localP = weapon.Obj.localPosition;
            Quaternion localQ = weapon.Obj.localRotation;
            Vector3 localS = weapon.Obj.localScale;
            if (haveChain)
            {
                chain.Upper.localRotation = savedUpper;
                chain.Fore.localRotation = savedFore;
            }
            weapon.Obj.SetParent(_sceneRoot, false);
            DecomposeTRS(from, out Vector3 fp, out Quaternion fq, out Vector3 fs);
            weapon.Obj.position = fp; weapon.Obj.rotation = fq; weapon.Obj.localScale = fs;

            Matrix4x4 inSock = sock.worldToLocalMatrix * from;
            _draw = new DrawState
            {
                Gun = weapon, Bone = bone, Sock = sock, T = 0f, Dur = rifle ? SlingDur : DrawDur,
                InSock = inSock, Rifle = rifle,
                FromP = weapon.Obj.position, FromQ = weapon.Obj.rotation, FromS = weapon.Obj.localScale.x != 0f ? weapon.Obj.localScale.x : 1f,
                LocalP = localP, LocalQ = localQ, LocalS = localS,
                MouthP = Vector3.zero, MouthQ = Quaternion.identity, MouthS = 1f,
                HandFromGun = Matrix4x4.TRS(localP, localQ, localS).inverse,
                Leather = false,
            };
            _handBone = bone;
            HeldSide = side;
            PresentT = -1f;
            _settle = null; // a stow's settle must not outlive it (see foot.js's own comment at :2652)
            var anim = GunAnimSource?.Invoke();
            if (!rifle) anim?.Play("draw", force: true, fade: 0.05f);
            return true;
        }

        // =====================================================================
        // ---- src/foot.js:2662 _updateDraw ----
        // =====================================================================
        void UpdateDraw(float dt, CharacterRig rig, Vector3 heroPos)
        {
            var d = _draw;
            d.T += dt;
            float t = d.T;
            Transform gun = d.Gun.Obj;
            Matrix4x4 gm1 = d.Bone.localToWorldMatrix * Matrix4x4.TRS(d.LocalP, d.LocalQ, d.LocalS);
            DecomposeTRS(gm1, out Vector3 toP, out Quaternion toQ, out Vector3 toS);

            float reach = d.Rifle ? SlingReach : DrawReach;
            float grip = d.Rifle ? SlingGrip : DrawGrip;
            float lift = d.Rifle ? SlingLift : DrawLift;
            float present = d.Rifle ? SlingPresent : DrawPresent;
            float tLift = reach + grip;
            float tPres = tLift + lift;

            if (t < tPres)
            {
                Matrix4x4 gm2 = d.Sock.localToWorldMatrix * d.InSock;
                DecomposeTRS(gm2, out Vector3 fp, out Quaternion fq, out Vector3 fsv);
                d.FromP = fp; d.FromQ = fq;
                d.FromS = fsv.x != 0f ? fsv.x : 1f;
            }

            if (t < tLift)
            {
                gun.position = d.FromP; gun.rotation = d.FromQ; gun.localScale = Vector3.one * d.FromS;
            }
            else if (t < tPres)
            {
                if (!d.Leather) { d.Leather = true; FxAudio.DrawGun(SoundVolume()); }
                float c = (t - tLift) / lift;
                float e = c * c * (2f - c);
                Vector3 up;
                if (d.Rifle)
                {
                    up = (d.FromQ * new Vector3(0, 0, 1)) * (SlingOut * Mathf.Min(1f, e * 1.6f)) // HANDEDNESS: gun/socket-local "out from the back" is JS's -Z -> Unity's +Z (see SeatInPocket/BackSocket note on asset-local axes — this one is the SOCKET's own authored convention, which _backSocket built with Unity's forward already, so its own -Z-out-of-the-back convention carries through unflipped: +Z here is out)
                        + new Vector3(0, SlingUp * e, 0);
                }
                else
                {
                    up = (d.FromQ * new Vector3(-1, 0, 0)) * (0.135f * e);
                }
                gun.position = d.FromP + up;
                if (d.Rifle)
                {
                    Quaternion up2 = MuzzleUp(d.FromQ);
                    gun.rotation = Quaternion.Slerp(d.FromQ, up2, e);
                }
                else
                {
                    gun.rotation = Quaternion.Slerp(d.FromQ, toQ, 0.12f * c * c);
                }
                gun.localScale = Vector3.one * d.FromS;
                d.MouthP = gun.position; d.MouthQ = gun.rotation; d.MouthS = d.FromS;
            }
            else
            {
                float c = Mathf.Min(1f, (t - tPres) / present);
                float ep = 1f - Mathf.Pow(1f - c, 2.4f);
                float arc = Mathf.Sin(Mathf.PI * c);
                Vector3? way = d.Rifle ? SlingWaypoint(rig, RifleSide()) : null;
                Vector3 pos;
                if (way.HasValue)
                {
                    pos = BendThrough(d.MouthP, way.Value, toP, 1f - Mathf.Pow(1f - Mathf.Min(1f, c / 0.7f), 2.4f));
                }
                else
                {
                    pos = Vector3.Lerp(d.MouthP, toP, ep);
                    pos.y += arc * 0.06f;
                    Vector3 away = d.MouthP - heroPos; away.y = 0f;
                    if (away.sqrMagnitude > 1e-6f) pos += away.normalized * (arc * 0.04f);
                }
                gun.position = pos;
                float cr = d.Rifle ? Mathf.Clamp01((c - 0.5f) / 0.5f) : c;
                const float bk = 1.70158f; float u = cr - 1f;
                float er = 1f + (bk + 1f) * u * u * u + bk * u * u;
                gun.rotation = SlerpPast(d.MouthQ, toQ, er);
                gun.localScale = Vector3.one * Mathf.Lerp(d.MouthS, toS.x, ep);
                if (c >= 1f) FinishDraw(true);
            }
        }

        // =====================================================================
        // ---- src/foot.js:2756 _finishDraw ----
        // =====================================================================
        void FinishDraw(bool settle)
        {
            var d = _draw;
            if (d == null) return;
            _draw = null;
            if (settle) PresentT = 0f;
            d.Gun.Obj.SetParent(d.Bone, false);
            d.Gun.Obj.localPosition = d.LocalP;
            d.Gun.Obj.localRotation = d.LocalQ;
            d.Gun.Obj.localScale = d.LocalS;
            SetRenderersVisible(d.Gun.Obj, true);
            Held = d.Gun;
            _handBone = d.Bone;
            SetReticleVisible?.Invoke(true);
            AimOnDraw();
        }

        // =====================================================================
        // ---- src/foot.js:2772 _updateStow ----
        // Two moves, not one: the hand carries the gun down to the hip (sagging
        // below the line, coming back up into it), then it goes STRAIGHT DOWN
        // into the leather along the holster's own axis.
        // =====================================================================
        void UpdateStow(float dt, CharacterRig rig)
        {
            var s = _stow;
            Transform sock = s.Sock ?? (_belt?.HolsterSocket?.Sockets != null && _belt.HolsterSocket.Sockets.Length > 0 ? _belt.HolsterSocket.Sockets[0] : null);
            if (sock == null) { _stow = null; ToHolster(rig); return; }
            s.T += dt;
            float k = Mathf.Min(1f, s.T / s.Dur);
            Vector3 toP = sock.position; Quaternion toQ = sock.rotation;
            float want = s.ToS > 0f ? s.ToS : s.Gun.Scale;
            const float Carry = 0.68f;
            Vector3 up = s.Rifle
                ? (toQ * new Vector3(0, 0, 1)) * SlingOut + new Vector3(0, SlingUp, 0)
                : (toQ * new Vector3(-1, 0, 0)) * 0.135f;
            Vector3 mouth = toP + up;
            Vector3 pos;
            if (k < Carry)
            {
                float c = k / Carry;
                float e = c * c * (3f - 2f * c);
                Vector3? way = s.Rifle ? SlingWaypoint(rig, RifleSide()) : null;
                Vector3? stand = (way.HasValue) ? SlingStand(rig, RifleSide()) : null;
                if (way.HasValue && stand.HasValue)
                {
                    const float Stand = 0.35f;
                    Quaternion up2 = MuzzleUp(toQ);
                    if (c < Stand)
                    {
                        float u = c / Stand; float su = u * u * (3f - 2f * u);
                        pos = Vector3.Lerp(s.FromP, stand.Value, su);
                        s.Gun.Obj.rotation = Quaternion.Slerp(s.FromQ, up2, su);
                    }
                    else
                    {
                        float u = (c - Stand) / (1f - Stand); float su = u * u * (3f - 2f * u);
                        pos = BendThrough(stand.Value, way.Value, mouth, su);
                        s.Gun.Obj.rotation = up2;
                    }
                }
                else
                {
                    pos = Vector3.Lerp(s.FromP, mouth, e);
                    pos.y -= Mathf.Sin(Mathf.PI * c) * 0.085f;
                    s.Gun.Obj.rotation = Quaternion.Slerp(s.FromQ, toQ, e);
                }
                s.Gun.Obj.localScale = Vector3.one * Mathf.Lerp(s.FromS, want, e);
            }
            else
            {
                float c = (k - Carry) / (1f - Carry);
                float ei = 1f - (1f - c) * (1f - c);
                pos = Vector3.Lerp(mouth, toP, ei);
                if (s.Rifle) { Quaternion up2 = MuzzleUp(toQ); s.Gun.Obj.rotation = Quaternion.Slerp(up2, toQ, ei); }
                else s.Gun.Obj.rotation = toQ;
                s.Gun.Obj.localScale = Vector3.one * want;
            }
            // shared trailing step for BOTH branches, exactly mirroring the JS's
            // own `s.gun.obj.position.copy(this._stowV); if (k<1) return; ...`
            // which sits OUTSIDE the if/else, not nested in either branch.
            s.Gun.Obj.position = pos;
            if (k < 1f) return;
            _stow = null;
            ToHolster(rig);
            _settle = new SettleState { T = 0f, Q = s.Gun.Obj.rotation, Gun = s.Gun };
        }

        // =====================================================================
        // ---- src/foot.js:2850 _updateSettle ----
        // The last rock in the holster: a damped swing about the belt's own
        // forward, over in a third of a second.
        // =====================================================================
        void UpdateSettle(float dt)
        {
            var st = _settle;
            st.T += dt;
            float k = st.T / 0.34f;
            if (k >= 1f || st.Gun.Obj.parent == null)
            {
                if (st.Gun.Obj.parent != null) st.Gun.Obj.rotation = st.Q;
                _settle = null;
                return;
            }
            float a = Mathf.Sin(k * Mathf.PI * 3f) * (1f - k) * 0.17f;
            st.Gun.Obj.rotation = st.Q;
            st.Gun.Obj.Rotate(0f, 0f, a * Mathf.Rad2Deg, Space.Self);
        }

        /// <summary>Call once per frame from the owning MonoBehaviour's
        /// LateUpdate (after FootController.Update and the Animator) whenever a
        /// settle is pending — mirrors the JS's own per-frame dispatch (the JS
        /// calls this from its single big `update()`; this port exposes it
        /// separately since draw/stow/settle are mutually exclusive but not
        /// driven by the same call site in every frame type).</summary>
        public void TickSettle(float dt) { if (_settle != null) UpdateSettle(dt); }

        // =====================================================================
        // ---- src/foot.js:2879 _moveArm ----
        // The hand goes WITH the gun: while a draw/stow is in flight the arm is
        // SOLVED (a two-bone reach to wherever the grip is this frame), not
        // played, with the elbow placed by a hint that walks reach -> lift ->
        // carry (or carry -> sling for a rifle).
        // =====================================================================
        public void MoveArm(float dt, CharacterRig rig, Vector3 heroPos)
        {
            Transform hand = _handBone;
            string side = RifleSide();
            // DEVIATION (see class header): gun-side chain, not always right.
            Weapons.ArmChain chain = side == "L" ? Weapons.FindArmChainL(rig.Root) : Weapons.FindArmChain(rig.Root);
            Vector3? raw = AimDirSource?.Invoke();
            Vector3? aim = (AcrobaticActive?.Invoke() ?? false) ? (MoveAimClamp?.Invoke(raw) ?? raw) : raw;

            bool haveChain = chain.Valid;
            Quaternion mqa = default, mqb = default, mqe = default;
            if (haveChain) { mqa = chain.Upper.localRotation; mqb = chain.Fore.localRotation; }
            bool haveHand = hand != null;
            if (haveHand) mqe = hand.localRotation;

            // `mv` — read BEFORE ticking, src/foot.js's own `const mv = this._draw ?? this._stow;`
            bool mvIsDraw = _draw != null;
            bool mvRifle = mvIsDraw ? _draw.Rifle : (_stow != null && _stow.Rifle);
            WeaponDef mvDef = mvIsDraw ? _draw.Gun?.Def : _stow?.Gun?.Def;

            bool aiming = (PistolAiming?.Invoke() ?? false) || (mvRifle && mvIsDraw && AimOnDrawEnabled());
            bool carry = haveChain && haveHand && (mvRifle
                ? RifleHolds.ApplyRifleHold(rig, rig.Root, hand, aim, mvDef, aiming ? "aim" : "carry", side)
                : Weapons.ApplyPistolPose(rig.ActGroup, rig.Root, chain, hand, aim));
            Quaternion mqc = default, mqd = default;
            if (carry) { mqc = chain.Upper.localRotation; mqd = chain.Fore.localRotation; }

            if (_draw != null) UpdateDraw(dt, rig, heroPos);
            if (_stow != null) UpdateStow(dt, rig);
            TickSettle(dt);

            // `rec` — RE-READ after ticking (NOT the same as `mv` above): a draw
            // that just finished this frame zeroed `_draw` from inside
            // UpdateDraw's own FinishDraw call, exactly as src/foot.js's own
            // `const rec = this._draw ?? this._stow;` is a fresh read for this
            // reason, not a reuse of `mv`.
            bool haveRec = _draw != null || _stow != null;
            if (!haveRec)
            {
                if (Held == null) _handBone = null;
                return;
            }
            if (!haveChain || !haveHand) return;

            bool recIsDraw = _draw != null;
            bool recRifle = recIsDraw ? _draw.Rifle : _stow.Rifle;
            CarriedWeapon recGun = recIsDraw ? _draw.Gun : _stow.Gun;
            Matrix4x4 recHandFromGun = recIsDraw ? _draw.HandFromGun : _stow.HandFromGun;
            float t = recIsDraw ? _draw.T : _stow.T;
            float dur = recIsDraw ? _draw.Dur : _stow.Dur;

            Matrix4x4 mm = recGun.Obj.localToWorldMatrix * recHandFromGun;
            DecomposeTRS(mm, out Vector3 mmPos, out Quaternion mmRot, out _);

            Vector3 hint = recRifle ? HintRifle : HintCarry;
            float present = 0f;
            if (_draw != null && recRifle)
            {
                float tLift = SlingReach + SlingGrip, tPres = tLift + SlingLift;
                if (t < tPres) hint = HintSling;
                else
                {
                    present = Mathf.Min(1f, (t - tPres) / SlingPresent);
                    hint = HintMix(HintSling, HintRifle, present * present * (3f - 2f * present));
                }
            }
            else if (_draw != null)
            {
                float tLift = DrawReach + DrawGrip, tPres = tLift + DrawLift;
                if (t < tLift) hint = HintReach;
                else if (t < tPres) hint = HintMix(HintReach, HintLift, Mathf.Min(1f, 2f * (t - tLift) / DrawLift));
                else
                {
                    float c = Mathf.Min(1f, (t - tPres) / DrawPresent);
                    hint = HintMix(HintLift, HintCarry, c * c * (3f - 2f * c));
                }
            }
            else if (recRifle)
            {
                float c = Mathf.Min(1f, t / (dur * 0.68f));
                hint = HintMix(HintRifle, HintSling, c * c * (3f - 2f * c));
            }

            if (!Weapons.ApplyReachPose(rig.ActGroup, rig.Root, chain, hand, mmPos, aim, hint)) return;

            // THE GUN IS RIGID IN THE HAND — only a real hand bone (not e.g. a
            // forearm standing in for one) gets its exact orientation overwritten.
            bool wrist = Weapons.IsHandBone(hand.name);
            if (wrist) hand.rotation = mmRot; // Unity composes localRotation from parent automatically — see Weapons.cs's own precedent

            float left = dur - t;
            float ReachT = recRifle ? SlingReach : DrawReach;
            float toIdle = recIsDraw ? 1f - Smooth01(t / ReachT) : 1f - Smooth01(left / 0.12f);
            float toCarry = recIsDraw ? 1f - Smooth01(left / 0.08f) : 1f - Smooth01(t / 0.08f);
            if (toCarry > 0f && carry)
            {
                chain.Upper.localRotation = Quaternion.Slerp(chain.Upper.localRotation, mqc, toCarry);
                chain.Fore.localRotation = Quaternion.Slerp(chain.Fore.localRotation, mqd, toCarry);
            }
            if (toIdle > 0f)
            {
                chain.Upper.localRotation = Quaternion.Slerp(chain.Upper.localRotation, mqa, toIdle);
                chain.Fore.localRotation = Quaternion.Slerp(chain.Fore.localRotation, mqb, toIdle);
            }
            float back = Mathf.Max(toIdle, toCarry);
            if (back > 0f && wrist) hand.localRotation = Quaternion.Slerp(hand.localRotation, mqe, back);

            if (recRifle)
            {
                float w = recIsDraw ? present : 1f - Mathf.Min(1f, t / (dur * 0.5f));
                if (w > 0.001f) RifleHolds.ApplyRifleSupport(rig, rig.Root, recGun.Obj, recGun.Def, w, aim, side);
            }
        }

        // =====================================================================
        // ---- src/foot.js:2989 _slingWaypoint / 3009 _slingStand ----
        // THE WAY PAST THE SHOULDER for a slung rifle, and where it is stood on
        // end before going over the shoulder — both pure body-geometry reads,
        // no handedness concern (FootPoses.RifleShoulder already carries the
        // one cross product this needs, swapped there).
        // =====================================================================
        Vector3? SlingWaypoint(CharacterRig rig, string side)
        {
            Vector3? pocket = FootPoses.RifleShoulder(rig, side, null);
            if (!pocket.HasValue) return null;
            Vector3 outV = pocket.Value;
            Transform chest = rig?.Anim != null
                ? (rig.Anim.GetBoneTransform(HumanBodyBones.Chest) ?? rig.Anim.GetBoneTransform(HumanBodyBones.Spine))
                : null;
            if (chest != null)
            {
                Vector3 d = outV - chest.position; d.y = 0f;
                if (d.sqrMagnitude > 1e-6f) outV += d.normalized * SlingWayOut;
            }
            outV.y += SlingWayUp;
            return outV;
        }

        Vector3? SlingStand(CharacterRig rig, string side)
        {
            Vector3? pocket = FootPoses.RifleShoulder(rig, side, null);
            if (!pocket.HasValue) return null;
            Transform an = rig?.AnimGroup != null ? rig.AnimGroup : rig?.Root;
            if (an == null) return null;
            Vector3 facing = an.forward; facing.y = 0f; // HANDEDNESS: JS reads -column(2) (its own back->forward flip); Unity's .forward IS the rig's own forward already
            if (facing.sqrMagnitude < 1e-6f) facing = Vector3.forward;
            Vector3 outV = pocket.Value + facing.normalized * SlingStandFwd;
            outV.y += 0.05f;
            return outV;
        }

        // =====================================================================
        // ---- src/foot.js:3026 _gunReachEnv ----
        // How far into the reach-to-the-hip the body is — drives the shoulder
        // going back and down in CharacterRig.SwingBody.
        // =====================================================================
        public float GunReachEnv()
        {
            float x = -1f;
            if (_draw != null) x = _draw.T / (DrawReach + DrawGrip + DrawLift);
            else if (_stow != null) x = (_stow.T - _stow.Dur * 0.45f) / (_stow.Dur * 0.55f);
            if (x < 0f || x > 1f) return 0f;
            return 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * x));
        }
    }

    // =========================================================================
    // ---- supporting types ----
    // =========================================================================

    /// <summary>One weapon the hero owns — wraps Combat.WeaponDef (the shared,
    /// per-DEFINITION data Weapons.cs/RifleHolds.cs already measure onto) with
    /// the PER-INSTANCE state foot.js's own "gun" objects also carry: the live
    /// Transform, its own gun-model animator, and the crouch Y-stretch flag.
    /// Mirrors src/foot.js's "weapon" object shape exactly enough for this
    /// file's own math (`gun.obj`, `gun.scale`, `gun.kind` via Def.Kind,
    /// `gun.id`, `gun.userData.anim`/`baseScaleY`).</summary>
    public sealed class CarriedWeapon
    {
        public WeaponDef Def;
        public Transform Obj;
        public WeaponAnim Anim;
        public float? BaseScaleY;
        public string Id => Def?.Id;
        public float Scale => (Def != null && Def.Scale > 0f) ? Def.Scale : 1f;
    }

    /// <summary>The worn belt/holster gear, as FootWeapons needs it — mirrors
    /// one entry of src/foot.js's `this.worn` Map (the one with a `.socket`,
    /// i.e. what `holsterSocket` returns, foot.js:2118). Per-socket mutable
    /// state (JS's `sock.userData.*`) lives in the side-table `Data` below
    /// since a Unity Transform carries no arbitrary payload.</summary>
    public sealed class WornBelt
    {
        public Transform Obj;
        public Transform[] Sockets;
        public float HolsterScale = 1f;
        public List<(Transform Socket, object RideEntry)> Ride; // src/foot.js's `w.ride` — opaque to this file; handed back to IFootBelt.ResyncRide as-is
        public Transform RideBody;                               // src/foot.js's `w.rideBody`

        readonly Dictionary<Transform, SocketUserData> _data = new Dictionary<Transform, SocketUserData>();
        public SocketUserData DataFor(Transform sock)
        {
            if (sock == null) return new SocketUserData();
            if (!_data.TryGetValue(sock, out var d)) _data[sock] = d = new SocketUserData();
            return d;
        }
    }

    /// <summary>src/foot.js's `sock.userData.*` — per-socket scratch the holster
    /// seating measurement caches across frames (so it runs once per gun per
    /// socket, not every frame).</summary>
    public sealed class SocketUserData
    {
        public string SeatedForGunId;
        public PocketMetrics? Pocket;
        public float StandExtra;
    }

    public struct PocketMetrics { public float SLo, SHi, ULo, UHi, MTop, MBot; }

    /// <summary>src/foot.js's `_bodySection` return shape ({c, r}) — only `r`
    /// is read anywhere in this file's own range, but `c` is kept for parity
    /// with any future caller.</summary>
    public struct BodySection { public Vector3 C; public float R; }

    /// <summary>
    /// The exact, narrow contract FootWeapons needs from the belt/gear-fitting
    /// layer — src/foot.js:1093-2124 (`_ringOf`/`_gearMeshes`/`_bodySection`/
    /// `_bodyMeshes`/`_skinXform`/the `worn` map/`holsterSocket` getter), which
    /// is FootBelt.cs's territory and NOT ported by this file. FootBelt.cs did
    /// not exist yet when this was written (being authored concurrently); this
    /// interface is this port's own specification of the boundary, not a read
    /// of the real class. Whoever wires the two together should either make
    /// FootBelt.cs implement this directly or adapt its real public surface to
    /// match it with a thin wrapper.
    ///
    /// Every method hands back WORLD-SPACE data deliberately (not belt-local
    /// or rig-local) so this file can apply its own `inv` consistently and
    /// robustly, matching how the JS itself works (world position converted to
    /// a local frame via an explicit inverse at the point of use, never
    /// assumed from direct parentage) — see SeatInPocket/BackSocket's own
    /// comments for why that assumption would be unsafe here.
    /// </summary>
    public interface IFootBelt
    {
        /// <summary>src/foot.js:2118 holsterSocket getter — the worn belt/holster
        /// gear, or null if nothing worn has a socket.</summary>
        WornBelt HolsterSocket { get; }

        /// <summary>src/foot.js:1521 _ringOf(w) — the strap's own lowest even
        /// ring of vertices, as a WORLD-space centre (median Y, mid-range X/Z).
        /// Null if there are too few vertices to find one (fewer than 8).</summary>
        Vector3? RingCenterWorld(WornBelt w);

        /// <summary>src/foot.js:1884 _gearMeshes(w) folded with its own per-vertex
        /// walk (foot.js:1524-1531's `o.matrixWorld`-transformed stream) — EVERY
        /// vertex of the belt's own worn meshes, in WORLD space, unfiltered.</summary>
        IEnumerable<Vector3> GearVertsWorld(WornBelt w);

        /// <summary>src/foot.js:1985 _bodySection(y) — the body's own
        /// cross-section (centre + radius) at a WORLD Y, torso-filtered (the
        /// torso test itself, foot.js's `_torsoTest`, is also belt territory).
        /// Null if there are too few vertices at that height.</summary>
        BodySection? BodySectionAt(CharacterRig rig, float worldY);

        /// <summary>src/foot.js:1858 _bodyMeshes() folded with its per-vertex
        /// walk — EVERY vertex of the hero's OWN body (worn gear excluded), in
        /// WORLD space. FootWeapons does its own striding/filtering on top of
        /// this (BackSocket's `i+=2`, its own height/x window) — this just
        /// needs to hand back clean body geometry.</summary>
        IEnumerable<Vector3> BodyVertsWorld(CharacterRig rig);

        /// <summary>src/foot.js:1816 _skinXform + the ride-local resync tail of
        /// `_seatInPocket` (foot.js:2276-2280) — re-binds a belt holster's
        /// strap-ride entry after its socket moves. OPTIONAL: a no-op
        /// implementation is a valid, documented reduced-fidelity choice (the
        /// strap ride simply lags by the one seating move, the same category
        /// as this port's other documented degradations elsewhere).</summary>
        void ResyncRide(WornBelt w, Transform sock);
    }
}

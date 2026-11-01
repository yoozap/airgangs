using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Port of src/foot.js's GTA-style cover (V): raycast walls, corner stop,
    /// RMB peek (standing/crouched), LMB blind fire, low-wall pop. Cited
    /// `// src/foot.js:NNN` throughout — every method here is foot.js's OWN
    /// (the cover system has no characters.js counterpart; it is pure foot.js).
    ///
    /// ================================ SCOPE CUT (STALE — SEE CORRECTION) =========
    /// This header used to say `_coverHold`/`_coverArmsBare`/`_coverBones`
    /// (foot.js:3623-3732) — the weapon-arm BONE POSE while behind a wall
    /// (pistol up by the shoulder, the rifle stood in its pocket, the blind
    /// arm round the edge) — were NOT ported because `applyRifleHold`/
    /// `applyCoverArms`/`applyRifleSupport` did not exist in Combat/Weapons.cs
    /// yet. That is no longer true and misled a later pass into re-briefing
    /// the same gap as open. CORRECTED: all three (plus `ApplyCoverHold`/
    /// `ApplyCoverArmsBare`/`ApplyRifleReloadHand`/`ApplyTuckPose`) are ported
    /// in <c>Combat/RifleHolds.cs</c> (781 lines — `ApplyRifleHold` :228,
    /// `ApplyRifleSupport` :364, `ApplyCoverArms` :570, `ApplyCoverHold` :671,
    /// `ApplyCoverArmsBare` :760), already claimed in the manifest (foot.js
    /// 110 + weapons.js 516), and — more importantly — REACHABLE: `Core/
    /// HeldWeapon.cs:277` calls `RifleHolds.ApplyCoverHold` every frame it is
    /// relevant, `HeldWeapon` is registered via `game.CombatSystems.Add(held)`
    /// at `Core/CombatWorldAdapter.cs:498`, and `GameLoop.cs:889` ticks every
    /// `CombatSystems` entry each frame. A probe run against the real banana/
    /// eggplant/toad FBX rigs returned `holdOk=true coverArmsOk=true
    /// supportOk=true` on all three. This file's OWN scope (raycast walls,
    /// corner stop, peek, blind fire, pop — everything below) was never
    /// affected either way: it has no weapon-arm bone-pose code of its own,
    /// then or now, and still does not need any — `HeldWeapon.cs` reads
    /// `FootCover.Active`/`BlindAimOverride` (see below) to drive the pose it
    /// owns. Nothing in this file needs editing to close the gap; this
    /// comment exists only so the next reader does not re-open a closed
    /// question off a stale class header, the same way this one did. Do NOT
    /// wire a SECOND cover-pose caller from this file — see
    /// `Character/FootWeaponFrame.cs`'s header for why one already exists and
    /// is deliberately left unwired.
    ///
    /// ================================ HANDEDNESS =================================
    /// Every direction here is built from `(-sin(yaw), 0, -cos(yaw))`-style
    /// formulas or from a surface NORMAL already reported in world space by
    /// Unity's own Physics.Raycast (RaycastHit.normal) — never a cross product
    /// standing in for a rotation. Nothing to swap in this file.
    /// </summary>
    public sealed class FootCover
    {
        // ---- src/foot.js:266-374 cover constants ----
        const float CoverFind = 3.5f;
        const float CoverArrive = 0.3f;
        const float CoverRun = 0.85f;
        internal const float CoverSlam = -0.28f;   // internal: FootController.CoverLeanVector (foot.js:6160) also reads this
        const float CoverSlamT = 0.12f;
        internal const float CoverBreath = 0.012f; // internal: FootController.CoverStanceTerms (foot.js:6197)
        internal const float CoverSway = 0.018f;   // internal: FootController.CoverLeanVector (foot.js:6157)
        internal const float CoverHeadMax = 1.1f;  // internal: FootController.CoverStanceTerms (foot.js:6199)
        internal const float CoverMoveLean = 0.06f; // internal: FootController.CoverLeanVector (foot.js:6158)
        internal const float CoverAccLean = 0.012f; // internal: FootController.CoverLeanVector (foot.js:6159)
        const float CoverCamHz = 0.85f;
        const float PeekFollow = 16f;
        const float CoverPush = 2.2f;
        const float CoverGap = 0.05f;
        const float CoverIn = 0.26f;
        internal const float CoverSpeed = 1.8f;    // internal: FootController.CoverLeanVector (foot.js:6158)
        const float CoverCrouchSpeed = 1.25f;
        const float CoverHigh = 1.35f;
        const float StepUp = 0.65f; // also FootClimb.StepUp / FootController.StepUp — src/foot.js:43
        const float CoverLow = StepUp + 0.1f;
        const float CoverEdge = 0.34f;
        const float CoverNear = 0.7f;
        const float PeekSlide = 3.2f;
        const float CoverLook = 0.5f;
        const float CoverSeek = 1.7f;
        const float CoverLeave = 0.16f;
        internal const float CoverLean = 0.09f;    // internal: FootController.CoverLeanVector (foot.js:6160)
        const float CoverBaseTurn = 0.2f;
        const float CoverMoveTurn = 0.32f;
        const float CoverCornerTurn = 0.48f;
        const float CoverShoulder = 0.24f;
        internal const float CoverChest = 0.15f;   // internal: FootController.CoverStanceTerms (foot.js:6200)
        const float CoverShuffle = 0.15f;
        const float CoverLookMax = 1.05f;
        const float CoverCamSwing = 5f;
        const float CoverCamBack = 3.9f;
        const float CoverCamSide = 1.5f;
        const float CoverCamUp = 1.95f;
        const float CoverCamMin = 0.9f;
        const float CoverCamAhead = 1.6f;
        internal const float PeekCam = 30f;        // internal: FootController.TickChaseCamera (foot.js:5143 `PEEK_CAM`)
        internal const float CoverCamWall = 0.4f;  // internal: FootController.TickChaseCamera (foot.js:5127 `COVER_CAM_WALL`)
        static readonly float PeekOutGun = CoverEdge + 0.06f;
        static readonly float PeekOutOff = CoverEdge + 0.30f;
        const float LookIn = 0.25f;
        const float LookOut = 0.2f;
        const float LookTurn = 0.5f;
        internal const float LookBank = 0.15f;     // internal: FootController.CoverLeanVector (foot.js:6156)
        const float LookSwing = 0.55f;
        const float LookAhead = 1.2f;
        const float PeekIn = 0.2f;
        const float PeekMin = 1.35f;
        const float PeekMax = 2.95f;
        internal const float PeekBank = 0.3f;      // internal: FootController.CoverLeanVector (foot.js:6155)
        const float ShotBuffer = 0.4f;
        const float OutStep = 1.2f;
        const float OutIn = 0.22f;
        const float OutHalf = 1.3f;
        const float PopIn = 0.22f;
        const float PopCrouch = 0.15f;
        const float PopHalf = 1.45f;
        const float BlindIn = 0.2f;
        const float BlindOut = 0.25f;
        const float BlindAcross = 0.25f;
        const float BlindTurn = 0.48f;
        const float BlindTurnOff = 1.0f;
        const float AccelGround = 16f; // also FootController.AccelGround — src/foot.js:24

        const float Eye = 1.55f; // src/foot.js:264
        const float FootFov = 62f;

        public const string HintCover = "🧱 საფარი — ზურგით კედელთან · იარე კედლის გასწვრივ · RMB — დამიზნება (კუთხესთან — კუთხიდან) · LMB — სროლა · C — ჩაცუცქება · V / კედლისგან მოშორებით — გამოსვლა";
        public const string HintCoverLow = "🧱 დაბალი საფარი — RMB — ზემოდან დამიზნება · კუთხესთან RMB — კუთხიდან · LMB — ბრმა სროლა · Space — გადახტომა · V — გამოსვლა";
        public const string HintCorner = "📐 კუთხე — კუთხისკენ მიწოლა: გახედვა · RMB: კუთხიდან დამიზნება · LMB: ბრმა სროლა · V — გამოსვლა";

        static float Smooth01(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
        static float Wrap(float a) { while (a > Mathf.PI) a -= 2f * Mathf.PI; while (a < -Mathf.PI) a += 2f * Mathf.PI; return a; }

        /// <summary>Colliders cover may use as a wall. Mirrors Footsteps.cs's
        /// own `GroundMask` field convention.</summary>
        public LayerMask CoverMask = ~0;

        /// <summary>src/foot.js:3071-3076 `_coverNear`'s own candidate list,
        /// concatenated onto the general collidables set before a cover
        /// wall-ray is cast — set once per frame by FootController.Update
        /// from Character/FootPlaneProbe.cs (the nearby parked plane's
        /// shell/mesh colliders), same object every frame as the one
        /// ProbeGround reads, matching the JS's single `_planeTargets()`
        /// call reused by both. Null when there is no nearby plane.</summary>
        public IReadOnlyList<Collider> ExtraTargets;

        /// <summary>Which side the weapon hand is on (+1 right, -1 left) —
        /// src/foot.js:3237 `_gunSide`. Left null (defaults to the right-handed
        /// pistol carry, +1) until a weapon system is wired in; see class
        /// header's scope cut.</summary>
        public System.Func<int> GunSideProvider;
        int GunSide() => GunSideProvider != null ? GunSideProvider() : 1;

        public sealed class CoverState
        {
            public Vector3 N, T, Base;
            public float Off;
            public bool Low;
            public float Top;
            public Vector3 From; public float FromYaw;
            public string Phase; // "approach" | "settle"
            public float Time, Slam; public bool SlamCam;
            public float AimYaw;
            public float Blend;
            public float Peek, Pop, Out, Look, Blind; public bool BlindWant; public float BlindCd; public int BlindSide;
            public int Side;
            public int Corner;
            public float EdgeR = float.PositiveInfinity, EdgeL = float.PositiveInfinity;
            public bool BlockR, BlockL;
            public int CamSide = 1; public float CamS = 1f; public float LookYaw; public float PushOut; public float Lost;
            public int? HintCornerVal; // src/foot.js `c.hintCorner` — the SIGN last shown, or null
            public int MoveDir; public bool MoveOut; public bool HeldWas;
            public float Turn;
            public float ShotBuf;
            public bool WasCrouched; public bool Forced;
            public int NearSide;

            public float Edge(int s) => s > 0 ? EdgeR : EdgeL;
            public void SetEdge(int s, float v) { if (s > 0) EdgeR = v; else EdgeL = v; }
            public bool Block(int s) => s > 0 ? BlockR : BlockL;
            public void SetBlock(int s, bool v) { if (s > 0) BlockR = v; else BlockL = v; }
        }

        CoverState _c;
        public bool Active => _c != null;
        public CoverState State => _c;
        /// <summary>The blind-fire aim line while the arm is out round the
        /// edge — src/foot.js `this.aimOverride`. Null the rest of the time.</summary>
        public Vector3? BlindAimOverride;

        // =====================================================================
        // WALL PROBE — src/foot.js:3082-3091 _wallHit. One ray, every cover
        // question goes through it so they all agree on what a wall is.
        // =====================================================================
        public struct WallHit { public Vector3 Point, Normal; public float Dist; }

        WallHit? WallHitProbe(Vector3 origin, Vector3 dir, float far)
        {
            if (Physics.Raycast(origin, dir, out var hit, far, CoverMask))
            {
                Vector3 n = hit.normal;
                if (Vector3.Dot(n, dir) > 0f) n = -n;
                return new WallHit { Point = hit.point, Normal = n, Dist = hit.distance };
            }
            // src/foot.js:3071-3076/4787-4789 — the nearby parked plane's
            // shell/mesh, handed in every frame as an extra candidate the
            // general CoverMask query may not reach (see ProbeGround's
            // identical comment in FootController.cs for why this is a
            // policy port, not an observed-bug fix). Same ray, so "closest
            // of the two" would need a second compare; CoverMask already
            // wins whenever it hits, so this only resolves ties where
            // CoverMask found nothing at all.
            if (ExtraTargets != null)
            {
                Ray ray = new Ray(origin, dir);
                foreach (var c in ExtraTargets)
                {
                    if (c != null && c.Raycast(ray, out var h2, far))
                    {
                        Vector3 n2 = h2.normal;
                        if (Vector3.Dot(n2, dir) > 0f) n2 = -n2;
                        return new WallHit { Point = h2.point, Normal = n2, Dist = h2.distance };
                    }
                }
            }
            return null;
        }

        // =====================================================================
        // FIND COVER — src/foot.js:3098-3125 _findCover. `near` candidate
        // headings: facing first, then the way the legs are going, then round
        // the clock — GTA takes the nearest cover in the stick's general
        // direction.
        // =====================================================================
        public struct FindResult { public Vector3 Point; public Vector3 N; public bool Low; public float Dist; }

        public FindResult? FindCover(Vector3 pos, float yaw, bool strafing, float moveYaw, float speed)
        {
            var yaws = new List<float> { yaw };
            if (Mathf.Abs(speed) > 0.3f)
                yaws.Add(strafing ? moveYaw : yaw + (speed < 0f ? Mathf.PI : 0f));
            foreach (var d in new[] { 0.6f, -0.6f, 1.2f, -1.2f, 1.9f, -1.9f, 2.6f, -2.6f, Mathf.PI })
                yaws.Add(yaw + d);

            FindResult? best = null;
            float bestScore = float.PositiveInfinity;
            for (int i = 0; i < yaws.Count; i++)
            {
                Vector3 dir = new Vector3(-Mathf.Sin(yaws[i]), 0f, -Mathf.Cos(yaws[i]));
                WallHit? Wall(float h, float far)
                {
                    var w = WallHitProbe(pos + Vector3.up * h, dir, far);
                    return (w.HasValue && Mathf.Abs(w.Value.Normal.y) < 0.45f) ? w : null;
                }
                var chest = Wall(1.1f, CoverFind);
                var hit = chest ?? Wall(CoverLow, CoverFind);
                if (!hit.HasValue) continue;
                var high = Wall(CoverHigh, hit.Value.Dist + 0.6f);
                float score = hit.Value.Dist + 0.3f * Mathf.Min(i, 2);
                if (score < bestScore)
                {
                    bestScore = score;
                    Vector3 n = hit.Value.Normal; n.y = 0f; n.Normalize();
                    best = new FindResult { Point = hit.Value.Point, N = n, Low = !high.HasValue, Dist = hit.Value.Dist };
                }
            }
            return best;
        }

        // =====================================================================
        // BODY STANDOFF — src/foot.js:3127-3151 _coverOff. Half the body's own
        // depth (read off its local-space mesh bounds, NOT a hard-coded
        // number), so the back stands off the wall by the body's own
        // thickness, not a constant fitted to one of the 39 JS bodies.
        // =====================================================================
        float? _halfDepth;

        public float CoverOff(CharacterRig rig)
        {
            if (_halfDepth.HasValue) return _halfDepth.Value + CoverGap;
            Bounds? box = null;
            Transform root = rig.Root;
            foreach (var mf in rig.Model.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var b = LocalBounds(mf.transform, root, mf.sharedMesh.bounds);
                box = box.HasValue ? Union(box.Value, b) : b;
            }
            foreach (var smr in rig.Model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                var b = LocalBounds(smr.transform, root, smr.localBounds);
                box = box.HasValue ? Union(box.Value, b) : b;
            }
            float depth;
            if (box.HasValue && box.Value.size.y > 0.05f)
                depth = (box.Value.size.z / box.Value.size.y) * CharacterRoster.HeroHeight;
            else depth = 0.4f;
            _halfDepth = Mathf.Clamp(depth / 2f, 0.1f, 0.4f);
            return _halfDepth.Value + CoverGap;
        }

        static Bounds LocalBounds(Transform meshT, Transform rootT, Bounds localBounds)
        {
            Vector3 c = rootT.InverseTransformPoint(meshT.TransformPoint(localBounds.center));
            Vector3 e = rootT.InverseTransformVector(meshT.TransformVector(localBounds.extents));
            e = new Vector3(Mathf.Abs(e.x), Mathf.Abs(e.y), Mathf.Abs(e.z));
            return new Bounds(c, e * 2f);
        }
        static Bounds Union(Bounds a, Bounds b) { a.Encapsulate(b); return a; }

        // =====================================================================
        // ENTER / LEAVE — src/foot.js:3153-3211.
        // =====================================================================
        public struct EnterResult { public float SpeedFloor; public bool ForceCrouch; public string Hint; }

        public EnterResult Enter(CharacterRig rig, FindResult c, Vector3 pos, float yaw, bool wasCrouched)
        {
            Vector3 n = c.N;
            Vector3 t = new Vector3(-n.z, 0f, n.x);
            float off = CoverOff(rig);
            float d = Vector3.Dot(pos - c.Point, n);
            Vector3 b = pos + n * (off - d);
            b.y = pos.y;
            float top = CoverHigh;
            if (c.Low)
            {
                var w = WallHitProbe(new Vector3(b.x - n.x * 0.4f, b.y + CoverHigh + 0.4f, b.z - n.z * 0.4f), Vector3.down, CoverHigh + 0.4f);
                if (w.HasValue) top = Mathf.Clamp(w.Value.Point.y - b.y, CoverLow, CoverHigh);
            }
            _c = new CoverState
            {
                N = n, T = t, Base = b, Off = off, Low = c.Low, Top = top,
                From = pos, FromYaw = yaw, Phase = "approach", Time = 0f, Slam = 0f, SlamCam = false, AimYaw = yaw,
                Blend = 0f, WasCrouched = wasCrouched, Forced = false,
            };
            bool close = Mathf.Sqrt((b.x - pos.x) * (b.x - pos.x) + (b.z - pos.z) * (b.z - pos.z)) <= CoverArrive;
            if (close) _c.Phase = "settle";
            bool forceCrouch = c.Low && !wasCrouched;
            if (forceCrouch) _c.Forced = true;
            return new EnterResult { SpeedFloor = close ? 0f : 0f, ForceCrouch = forceCrouch, Hint = c.Low ? HintCoverLow : HintCover };
        }

        /// <summary>Returns (restoreCrouch, pushSpeed) — src/foot.js:3199-3211
        /// _leaveCover. `how`: "key"|"out"|"roll"|"jump"|"vault"|"lost"|"silent".</summary>
        public (bool restoreCrouch, bool wasCrouched, float pushSpeed) Leave(string how)
        {
            var c = _c;
            if (c == null) return (false, false, 0f);
            _c = null;
            BlindAimOverride = null;
            bool restore = c.Forced && how != "vault";
            float push = (how == "out" && c.Blend >= 1f) ? CoverPush : 0f;
            return (restore, c.WasCrouched, push);
        }

        public bool CanFire() => _c == null || _c.Peek > 0.5f || _c.Pop > 0.5f || _c.Out > 0.5f || _c.Blind > 0.7f;
        public void Shot() { if (_c != null) _c.ShotBuf = ShotBuffer; }
        public void Blind(bool down) { if (_c != null) _c.BlindWant = down; }

        /// <summary>src/foot.js:3445-3448 — call once a frame (after Tick) with
        /// a delegate that actually fires the weapon; fires at most once, and
        /// only once CanFire() says the gun is clear of the wall, exactly
        /// matching the JS's buffered-trigger semantics.</summary>
        public void ConsumeShotBuffer(System.Func<bool> tryFire)
        {
            if (_c == null || !(_c.ShotBuf > 0f)) return;
            if (CanFire() && tryFire != null && tryFire()) _c.ShotBuf = 0f;
        }

        /// <summary>src/foot.js:3438 `c.out > 0.7 && c.blindWant && !aiming` —
        /// the window where the hip-fire-while-stepped-out auto-cadence may
        /// fire. The weapon system owns the actual cadence (cartridge rate);
        /// this is only the gate.</summary>
        public bool HipFireWindowOpen(bool aiming) => _c != null && _c.Out > 0.7f && _c.BlindWant && !aiming;

        /// <summary>src/foot.js:3466 `c.blind > 0.7 && c.blindWant` — the
        /// window for the blind-fire-round-the-edge auto-cadence.</summary>
        public bool BlindFireWindowOpen() => _c != null && _c.Blind > 0.7f && _c.BlindWant;

        // =====================================================================
        // ONE FRAME — src/foot.js:3243-3573 _coverFrame. Mutates pos/yaw/speed/
        // camYaw/crouched directly, exactly as the climb does. `lookYawDelta`
        // is the mouse's unconsumed yaw this frame (src/foot.js's `g.look.yaw`)
        // — zeroed wherever the JS zeroes it, which the caller must then not
        // also apply elsewhere this frame.
        // =====================================================================
        public struct CoverInput
        {
            public float MoveX, MoveZ, StrafeX; // m.x, m.z, m.sx
            public bool RollEdge;                // X pressed this frame
            public bool JumpBuffered;             // this._jumpBuf > 0
            public bool Armed;
            public bool Aiming;
            public bool Reloading;
            public bool Crouched;                 // src/foot.js `this.crouched` — picks COVER_CROUCH_SPEED
            public float RunSpeed;
            public float AccelEstimate;           // this._accV, for the stop-rock lean
        }

        public struct CoverTickResult { public string Exit; public bool ForceCrouchOff; public string HintChanged; }

        public CoverTickResult Tick(float dt, CoverInput input, ref Vector3 pos, ref float yaw, ref float speed,
            ref float camYaw, ref float lookYawDelta)
        {
            var c = _c;
            var result = new CoverTickResult();
            if (c == null) { result.Exit = "none"; return result; }
            Vector3 n = c.N, t = c.T;
            c.Base = new Vector3(c.Base.x, pos.y, c.Base.z);
            c.Time += dt;

            if (c.Phase == "approach")
            {
                float dx = c.Base.x - pos.x, dz = c.Base.z - pos.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                if (dist <= CoverArrive || c.Time > 2.5f)
                {
                    c.Phase = "settle";
                    c.From = pos; c.FromYaw = yaw;
                }
                else
                {
                    float want = Mathf.Clamp(dist * 3f, 1.3f, input.RunSpeed * CoverRun);
                    speed += Mathf.Clamp(want - speed, -AccelGround * dt, AccelGround * dt);
                    float len = Mathf.Min(dist, speed * dt);
                    pos.x += (dx / dist) * len;
                    pos.z += (dz / dist) * len;
                    yaw += Wrap(Mathf.Atan2(-dx, -dz) - yaw) * Mathf.Min(1f, 12f * dt);
                    if (input.Armed) lookYawDelta = 0f;
                    camYaw = yaw + lookYawDelta;
                    result.Exit = null;
                    return result;
                }
            }

            bool wasSettled = c.Blend >= 1f;
            c.Blend = Mathf.Min(1f, c.Blend + dt / CoverIn);
            bool settled = c.Blend >= 1f;
            if (settled && !wasSettled) { c.Slam = CoverSlamT; c.SlamCam = true; }
            c.Slam = Mathf.Max(0f, c.Slam - dt);
            float yawN = Mathf.Atan2(-n.x, -n.z);
            float h = c.Low ? CoverLow - 0.2f : 1.0f;
            Vector3 back = -n;
            float far = c.Off + 0.7f;

            var w = WallHitProbe(new Vector3(c.Base.x + n.x * 0.1f, c.Base.y + h, c.Base.z + n.z * 0.1f), back, far);
            if (w.HasValue && Mathf.Abs(w.Value.Normal.y) < 0.45f)
            {
                Vector3 nn = w.Value.Normal; nn.y = 0f; nn.Normalize();
                if (Vector3.Angle(nn, n) * Mathf.Deg2Rad < 0.6f)
                {
                    n = Vector3.Slerp(n, nn, 1f - Mathf.Exp(-16f * dt)).normalized;
                    t = new Vector3(-n.z, 0f, n.x);
                    float by = c.Base.y;
                    c.Base = w.Value.Point + n * c.Off;
                    c.Base.y = by;
                }
                c.Lost = 0f;
            }
            else
            {
                c.Lost += dt;
                if (c.Lost > 0.3f) { result.Exit = "lost"; return result; }
            }
            c.N = n; c.T = t;

            float reach = CoverEdge + CoverLook;
            foreach (int s in new[] { 1, -1 })
            {
                WallHit? At(float d) => WallHitProbe(
                    new Vector3(c.Base.x + n.x * 0.1f + t.x * s * d, c.Base.y + h, c.Base.z + n.z * 0.1f + t.z * s * d), back, far);
                if (At(reach).HasValue)
                {
                    c.SetEdge(s, !At(reach + 0.45f).HasValue ? reach + 0.25f : !At(CoverSeek).HasValue ? CoverSeek - 0.2f : float.PositiveInfinity);
                }
                else
                {
                    float lo = 0f, hi = reach;
                    for (int i = 0; i < 6; i++) { float mid = (lo + hi) / 2f; if (At(mid).HasValue) lo = mid; else hi = mid; }
                    c.SetEdge(s, (lo + hi) / 2f);
                }
                c.SetBlock(s, WallHitProbe(new Vector3(c.Base.x + n.x * 0.12f, c.Base.y + h, c.Base.z + n.z * 0.12f), t * s, CoverEdge + 0.12f).HasValue);
            }
            float eR = c.EdgeR - CoverEdge, eL = c.EdgeL - CoverEdge;
            bool atR = eR <= 0.03f, atL = eL <= 0.03f;
            c.Corner = atR && atL ? (c.CamSide != 0 ? c.CamSide : 1) : atR ? 1 : atL ? -1 : 0;
            if (c.Corner != 0) c.CamSide = c.Corner;
            c.NearSide = c.Corner != 0 ? c.Corner : (Mathf.Min(eR, eL) <= CoverNear ? (eR <= eL ? 1 : -1) : 0);

            void ToEdge(int side)
            {
                float room = Mathf.Max(0f, c.Edge(side) - CoverEdge);
                if (room > 0.002f) c.Base += t * (side * Mathf.Min(room, PeekSlide * dt));
            }

            bool aiming = input.Armed && input.Aiming;
            bool held = input.MoveZ != 0f || input.MoveX != 0f || input.StrafeX != 0f;
            int along = 0, outDir = 0;
            if (settled && held)
            {
                if (!c.HeldWas)
                {
                    float my = camYaw + Mathf.Atan2(-(input.MoveX + input.StrafeX), -input.MoveZ);
                    Vector3 d = new Vector3(-Mathf.Sin(my), 0f, -Mathf.Cos(my));
                    float a = Vector3.Dot(d, t);
                    c.MoveDir = Mathf.Abs(a) > 0.35f ? (int)Mathf.Sign(a) : 0;
                    c.MoveOut = Vector3.Dot(d, n) > 0.7f;
                }
                along = c.MoveDir;
                outDir = c.MoveOut ? 1 : 0;
            }
            else { c.MoveDir = 0; c.MoveOut = false; }
            c.HeldWas = held && settled;
            float outOf = Mathf.Max(c.Peek, Mathf.Max(c.Pop, c.Out));
            c.PushOut = outDir > 0 && outOf < 0.02f ? c.PushOut + dt : 0f;
            if (c.PushOut > CoverLeave) { result.Exit = "out"; return result; }
            if (input.RollEdge && settled) { result.Exit = "roll"; return result; }
            if (input.JumpBuffered && settled)
            {
                if (c.Low) { yaw = yawN + Mathf.PI; camYaw = yaw; result.Exit = "vault"; }
                else result.Exit = "jump";
                result.ForceCrouchOff = true;
                return result;
            }

            float peekWant = 0f, popWant = 0f, outWant = 0f;
            // Takes camYaw/lookYawDelta as its OWN ref params: a local function may
            // not capture an enclosing `ref` parameter (CS1628). Passing them keeps
            // the write-through to Tick's caller intact.
            void StartOut(ref float camYawR, ref float lookYawDeltaR)
            {
                c.AimYaw = yawN - Mathf.Clamp(Wrap(yawN - camYawR), -OutHalf, OutHalf);
                camYawR = c.AimYaw;
                lookYawDeltaR = 0f;
            }
            bool hipOut = c.BlindWant && !input.Reloading && c.NearSide == 0 && !c.Low && c.Peek < 0.01f && c.Pop < 0.01f;
            if (settled && aiming && !input.Reloading)
            {
                if (c.Peek > 0.01f || (c.NearSide != 0 && c.Out < 0.01f))
                {
                    peekWant = 1f;
                    if (c.Peek <= 0.01f)
                    {
                        c.Side = c.NearSide != 0 ? c.NearSide : (c.Side != 0 ? c.Side : 1);
                        c.CamSide = c.Side;
                        float phi0 = Mathf.Clamp(Wrap(yawN - camYaw) * c.Side, PeekMin, PeekMax);
                        c.AimYaw = yawN - c.Side * phi0;
                        camYaw = c.AimYaw;
                        lookYawDelta = 0f;
                    }
                    ToEdge(c.Side);
                }
                else if (c.Pop > 0.01f || (c.Low && c.Out < 0.01f))
                {
                    popWant = 1f;
                    if (c.Pop <= 0.01f)
                    {
                        float yawW = yawN + Mathf.PI;
                        c.AimYaw = yawW + Mathf.Clamp(Wrap(camYaw - yawW), -PopHalf, PopHalf);
                        camYaw = c.AimYaw;
                        lookYawDelta = 0f;
                    }
                }
                else { outWant = 1f; if (c.Out <= 0.01f) StartOut(ref camYaw, ref lookYawDelta); }
            }
            else if (settled && hipOut) { outWant = 1f; if (c.Out <= 0.01f) StartOut(ref camYaw, ref lookYawDelta); }

            float dp = dt / PeekIn, dq = dt / PopIn, dout = dt / OutIn;
            c.Peek = Mathf.Clamp(c.Peek + Mathf.Clamp(peekWant - c.Peek, -dp, dp), 0f, 1f);
            c.Pop = Mathf.Clamp(c.Pop + Mathf.Clamp(popWant - c.Pop, -dq, dq), 0f, 1f);
            c.Out = Mathf.Clamp(c.Out + Mathf.Clamp(outWant - c.Out, -dout, dout), 0f, 1f);
            // src/foot.js:3438-3444 the hip-fire-while-held auto-cadence, and
            // :3445-3448 the buffered shot. Both NEED the weapon's own
            // cartridge rate/cooldown (src/foot.js `g.pistol.cart.rate`,
            // `.auto`), which this file does not have — see class header's
            // scope cut. HipFireWindowOpen below exposes exactly the gate the
            // JS computes (`c.out > 0.7 && c.blindWant && !aiming`) for a
            // weapon system to drive its own per-shot cooldown off; ShotBuf
            // is still tracked and decremented so ConsumeShotBuffer (below)
            // can hand a caller the identical buffered-trigger behaviour.
            if (c.ShotBuf > 0f) c.ShotBuf -= dt;

            bool canBlind = input.Armed && !aiming && settled && !input.Reloading
                && Mathf.Max(c.Peek, Mathf.Max(c.Pop, c.Out)) < 0.02f && (c.NearSide != 0 || c.Low);
            if (c.BlindWant && canBlind && c.Blind <= 0.01f) c.BlindSide = c.NearSide;
            if (c.Blind > 0.01f && c.BlindSide != 0) ToEdge(c.BlindSide);
            float bw = c.BlindWant && canBlind ? 1f : 0f;
            float db = dt / (bw > 0f ? BlindIn : BlindOut);
            c.Blind = Mathf.Clamp(c.Blind + Mathf.Clamp(bw - c.Blind, -db, db), 0f, 1f);
            if (c.Blind > 0.01f)
            {
                int bs = c.BlindSide;
                Vector3 ao = -n + t * (bs * BlindAcross);
                ao.y = bs != 0 ? 0f : -0.08f;
                BlindAimOverride = ao.normalized;
            }
            else BlindAimOverride = null;

            float want2 = 0f;
            if (settled && Mathf.Max(c.Peek, Mathf.Max(c.Pop, c.Out)) < 0.02f && c.Blind < 0.02f && along != 0)
                want2 = along * (input.Crouched ? CoverCrouchSpeed : CoverSpeed);
            int sgn = (int)Mathf.Sign(want2);
            if (sgn != 0 && (c.Block(sgn) || c.Edge(sgn) - CoverEdge <= 0.005f)) want2 = 0f;
            float dv = want2 - speed;
            speed += Mathf.Sign(dv) * Mathf.Min(Mathf.Abs(dv), AccelGround * dt);
            if (Mathf.Abs(speed) < 0.01f) speed = 0f;
            if (speed != 0f)
            {
                sgn = (int)Mathf.Sign(speed);
                float step = speed * dt;
                float room = Mathf.Max(0f, c.Edge(sgn) - CoverEdge);
                if (c.Block(sgn)) { step = 0f; speed = 0f; }
                else if (Mathf.Abs(step) > room) { step = sgn * room; speed = 0f; }
                c.Base += t * step;
                if (step != 0f) c.CamSide = sgn;
            }

            float lookWant = settled && c.Corner != 0 && c.MoveDir == c.Corner
                && Mathf.Max(c.Peek, Mathf.Max(c.Pop, c.Out)) < 0.02f && c.Blind < 0.02f ? 1f : 0f;
            float dl = dt / LookIn;
            c.Look = Mathf.Clamp(c.Look + Mathf.Clamp(lookWant - c.Look, -dl, dl), 0f, 1f);
            c.CamS += ((c.CamSide != 0 ? c.CamSide : 1) - c.CamS) * (1f - Mathf.Exp(-CoverCamSwing * dt));

            // Same CS1628 reason as StartOut: yaw/camYaw come in as ref params.
            void Follow(float target, ref float yawR, ref float camYawR)
            { yawR += Wrap(target - yawR) * Mathf.Min(1f, PeekFollow * dt); camYawR = target; }
            if (c.Peek > 0.01f && peekWant > 0f)
            {
                c.AimYaw += lookYawDelta; lookYawDelta = 0f;
                float phi = Mathf.Clamp(Wrap(yawN - c.AimYaw) * c.Side, PeekMin, PeekMax);
                Follow(c.AimYaw = yawN - c.Side * phi, ref yaw, ref camYaw);
            }
            else if (c.Pop > 0.01f && popWant > 0f)
            {
                c.AimYaw += lookYawDelta; lookYawDelta = 0f;
                float yawW = yawN + Mathf.PI;
                Follow(c.AimYaw = yawW + Mathf.Clamp(Wrap(c.AimYaw - yawW), -PopHalf, PopHalf), ref yaw, ref camYaw);
            }
            else if (c.Out > 0.01f && outWant > 0f)
            {
                c.AimYaw += lookYawDelta; lookYawDelta = 0f;
                Follow(c.AimYaw = yawN - Mathf.Clamp(Wrap(yawN - c.AimYaw), -OutHalf, OutHalf), ref yaw, ref camYaw);
            }
            else if (!settled)
            {
                yaw = c.FromYaw + Wrap(yawN - c.FromYaw) * Smooth01(c.Blend);
                if (input.Armed) lookYawDelta = 0f;
            }
            else
            {
                bool moving = Mathf.Abs(speed) > 0.3f;
                float turnWant = c.Corner != 0 ? c.Corner * (CoverCornerTurn + LookTurn * Smooth01(c.Look))
                    : moving ? Mathf.Sign(speed) * CoverMoveTurn
                    : (c.CamSide != 0 ? c.CamSide : 1) * CoverBaseTurn;
                if (c.Blind > 0.01f)
                {
                    float bt = c.BlindSide == GunSide() ? BlindTurn : BlindTurnOff;
                    turnWant += (c.BlindSide * bt - turnWant) * c.Blind;
                }
                c.Turn += (turnWant - c.Turn) * (1f - Mathf.Exp(-8f * dt));
                float wantYaw = yawN - c.Turn;
                yaw += Wrap(wantYaw - yaw) * (1f - Mathf.Exp(-14f * dt));
                c.LookYaw = Mathf.Clamp(c.LookYaw + lookYawDelta, -CoverLookMax, CoverLookMax);
                lookYawDelta = 0f;
            }

            if (!settled)
            {
                float k = Smooth01(c.Blend);
                pos.x = c.From.x + (c.Base.x - c.From.x) * k;
                pos.z = c.From.z + (c.Base.z - c.From.z) * k;
            }
            else
            {
                int gunSide = GunSide();
                float peekOut = c.Side == gunSide ? PeekOutGun : PeekOutOff;
                float lean = Smooth01(c.Peek) * peekOut * c.Side + Smooth01(c.Look) * LookOut * c.Corner
                    + (c.BlindSide != 0 && c.BlindSide != gunSide ? Smooth01(c.Blind) * 0.18f * c.BlindSide : 0f); // BLIND_STEP_OFF
                float room = CoverShoulder * Mathf.Sin(Mathf.Abs(c.Turn)) * (1f - Smooth01(c.Peek)) + OutStep * Smooth01(c.Out);
                pos.x = c.Base.x + t.x * lean + n.x * room;
                pos.z = c.Base.z + t.z * lean + n.z * room;
            }

            int hc = c.NearSide != 0 && Mathf.Max(c.Peek, Mathf.Max(c.Pop, c.Out)) < 0.02f && c.Blind < 0.02f ? c.NearSide : 0;
            if (hc != (c.HintCornerVal ?? 0) || !c.HintCornerVal.HasValue)
            {
                c.HintCornerVal = hc;
                result.HintChanged = hc != 0 ? HintCorner : (c.Low ? HintCoverLow : HintCover);
            }
            result.Exit = null;
            return result;
        }

        // =====================================================================
        // COVER CAMERA — src/foot.js:3575-3621 _coverCamera. Pure function:
        // returns the desired camera position + look-at point; the caller
        // (owning the actual Camera) applies its own damped spring the same
        // way foot.js's own `cs.v` does — exposed as CameraSpring below so the
        // SAME critically-damped integrator the JS uses is available, not just
        // the target.
        // =====================================================================
        public sealed class CameraSpring { public Vector3 V; public bool On; }
        public readonly CameraSpring Cam = new CameraSpring();

        /// <summary>`heroPos` is the hero's own current position (src/foot.js's
        /// `this.pos`) — only meaningfully different from `c.Base` while a
        /// peek/pop/out lean is live, and the caller (FootController) only
        /// invokes this once settled with none of those active, exactly
        /// mirroring foot.js:5073-5075's own gating in `_chaseCamera`.</summary>
        public void TickCamera(float dt, Transform camera, Vector3 heroPos, float crouchT, float pitch,
            System.Func<float, float, float, float?> groundAt)
        {
            var c = _c; if (c == null) return;
            float s = c.CamS;
            float lk = Smooth01(c.Look);
            float look = c.LookYaw - s * LookSwing * lk;
            Vector3 off = c.T * (-s * CoverCamBack * (1f - 0.2f * lk)) + c.N * (CoverCamSide + (1f - Mathf.Abs(s)) * CoverCamBack * 0.6f);
            off = Quaternion.AngleAxis(look * Mathf.Rad2Deg, Vector3.up) * off;
            float outD = Vector3.Dot(off, c.N);
            if (outD < CoverCamMin) off += c.N * (CoverCamMin - outD);
            float pit = Mathf.Clamp(pitch, -0.5f, 0.9f);
            Vector3 camTarget = heroPos + off;
            camTarget.y += CoverCamUp - 0.45f * crouchT + 1.8f * Mathf.Sin(Mathf.Max(0f, pit));
            float? camGY = groundAt?.Invoke(camTarget.x, camTarget.z, camTarget.y);
            if (camGY.HasValue && camTarget.y < camGY.Value + 1.0f) camTarget.y = camGY.Value + 1.0f;

            if (!Cam.On) { Cam.V = Vector3.zero; Cam.On = true; }
            if (c.SlamCam) { Cam.V.y -= 1.3f; c.SlamCam = false; }
            float w0 = 2f * Mathf.PI * CoverCamHz;
            float hh = Mathf.Min(dt, 1f / 30f);
            Vector3 accel = (camTarget - camera.position) * (w0 * w0) + Cam.V * (-2f * w0);
            Cam.V += accel * hh;
            camera.position += Cam.V * hh;
            Vector3 at = heroPos + c.T * (s * (CoverCamAhead + LookAhead * lk)) + c.N * (-0.6f * lk);
            at.y += Eye - 0.5f * crouchT - 1.5f * pit;
            camera.rotation = Quaternion.LookRotation((at - camera.position).normalized, Vector3.up);
        }
    }
}

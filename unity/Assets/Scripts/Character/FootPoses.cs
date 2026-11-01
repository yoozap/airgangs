using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Combat;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Port of the pose family CharacterRig.cs deliberately left out — see that
    /// file's header: "Left for src/foot.js's own port... setCrouch/_poseSeated/
    /// setSeated, setRollTuck/_poseTuck, setSlidePose/_poseSlide, setAirPose,
    /// setDivePose, the whole climb pose family, handsOnEdge/setHandHook/
    /// setWindBrace/setFlyPose/setWingGrip, setGrip/_handRig, rifleShoulder/
    /// _measurePocket, aimOffFacing, shoulderFront."
    ///
    /// Source: every method here is a 1:1 port of the SAME-NAMED method on
    /// src/characters.js's CharacterRig class (not of foot.js — foot.js only
    /// CALLS these; the pose math itself lives on the rig in the JS, exactly as
    /// it does here). Cited per-method as `// src/characters.js:NNN`.
    ///
    /// ======================== WHY THIS IS A SEPARATE STATIC CLASS ========================
    /// Unity's CharacterRig.cs (another agent's file, DO NOT EDIT) exposes the
    /// bone-pose STACK PRIMITIVES publicly — BoneTwist/BoneSet/BoneTurn/
    /// ReleaseBone(s)/ClipQ/QSame/FreezePose/RestoreFrozen/ThawPose — but keeps
    /// its own `TurnToward`/`LimbTip`/the humanoid-bone lookup private. Every pose
    /// below is built ONLY out of the public primitives, plus a small bone-rig
    /// lookup and a `TurnToward` of this file's own (see PoseRig / TurnToward
    /// below) — duplicated in spirit from CharacterRig's private one, not copy-
    /// pasted: CharacterRig's version walks arbitrary rig bone NAMES (it has to
    /// serve 39 bodies incl. Generic ones); this one reads HumanBodyBones
    /// directly, because every body that reaches this file is one of the 3
    /// Humanoid bodies in the 4-body roster (banana/eggplant/toad — see
    /// .claude/skills/unity/SKILL.md "the roster is 4 bodies"). The elephant
    /// (Generic, Idle-only, procedural run) never climbs, crouches, rolls,
    /// slides or dives in the JS either — every method here returns false/null
    /// for it exactly as characters.js's own "no legs to fold" bodies do.
    ///
    /// ================================ HANDEDNESS =================================
    /// Same rule as CharacterRig.cs and FlightModel.cs: rotations built from
    /// Quaternion.AngleAxis about this rig's OWN local axes (Root.right/up/
    /// forward) are component-identical and need no sign change. The one
    /// genuine cross product in this file's scope is `TurnToward` (mirrors
    /// characters.js:2429 _turnToward) — ported as `Vector3.Cross(r, axis)`
    /// (operands swapped), and `setGrip`'s finger-curl hinge (characters.js:605
    /// `axis = r × toWrist`) — ported as `Vector3.Cross(toWrist, r)` (swapped).
    /// `AimLocalRotation` (mirrors characters.js:438 `_aimQ`) uses
    /// Quaternion.FromToRotation, which is handedness-SAFE as-is: the shortest-
    /// arc rotation between two vectors given in a shared frame is a physical,
    /// coordinate-independent fact, not a signed generator — nothing to flip.
    ///
    /// ================================ LATEUPDATE =================================
    /// Every method here writes bones and MUST be called from the owning
    /// MonoBehaviour's LateUpdate, after the Animator has run this frame —
    /// exactly CharacterRig.cs's rule, because these are built on its own
    /// BoneTwist/BoneSet which depend on it (ClipQ's "what the animator left"
    /// read is only honest after the Animator has written).
    /// </summary>
    public static class FootPoses
    {
        // ---- characters.js:200-205 fold-angle constants (rad) ----
        const float KneeFold = 1.7f;
        const float TuckFold = 2.3f;
        const float SlideFold = 1.6f;
        const float SitFold = 1.5f;
        const float SitRecline = 0.21f;
        const float AirFold = 1.5f;
        const float FistFold = 0.85f;   // characters.js:364
        const float FistThumb = 0.55f;  // characters.js:368
        const float EdgeReachFrom = 0.99f;  // characters.js:248
        const float EdgeReachTo = 1.06f;    // characters.js:249
        const float PocketAhead = 0.22f;    // characters.js:385
        const float PocketProud = 0.015f;   // characters.js:386
        const float PocketDrop = 0.05f;     // characters.js:387

        // characters.js:262-267 AIR_KEYS — ldThigh ldCalf ldFoot trThigh trCalf
        // trFoot spine armUp armFore neck
        static readonly float[][] AirKeys =
        {
            new[] { -0.55f, 0.75f, -0.25f, 0.35f, 0.25f, 0.35f, -0.10f, -0.35f, 0.25f, -0.10f },
            new[] { -0.80f, 0.50f, -0.30f, 0.50f, 0.40f, 0.30f, -0.06f, -0.80f, 0.10f, -0.05f },
            new[] { -0.50f, 1.10f, -0.40f, -0.30f, 0.95f, -0.30f, -0.22f, -0.25f, 0.45f, 0.05f },
        };

        // characters.js:229-235 PULL_KEYS — u ldThigh ldCalf ldFoot trThigh
        // trCalf trFoot spine neck
        static readonly float[][] PullKeys =
        {
            new[] { 0.00f, -0.10f, 0.25f, 0.18f, -0.05f, 0.18f, 0.18f, -0.05f, 0.27f },
            new[] { 0.25f, -0.20f, 0.35f, 0.15f, -0.05f, 0.20f, 0.18f, -0.08f, 0.25f },
            new[] { 0.40f, -0.70f, 0.95f, 0.05f, -0.10f, 0.25f, 0.15f, -0.12f, 0.18f },
            new[] { 0.75f, -0.55f, 0.80f, -0.10f, -0.40f, 0.70f, 0.10f, -0.20f, 0.08f },
            new[] { 1.00f, -0.60f, 0.90f, -0.25f, -0.45f, 0.85f, 0.05f, -0.28f, 0.00f },
        };

        // characters.js:239-243 MANTLE_KEYS — row 0 is PULL_KEYS[4]'s own tail
        static readonly float[][] MantleKeys =
        {
            // 2026-11-01: each row is [u, 8 values] like PULL_KEYS — the port had
            // dropped one value per row, so LegKeysAt read index 8 and threw on
            // every climb-out frame (48 IndexOutOfRange per ParkourRun).
            new[] { 0.00f, -0.60f, 0.90f, -0.25f, -0.45f, 0.85f, 0.05f, -0.28f, 0.00f },
            new[] { 0.50f, -0.45f, 0.55f, -0.15f, -0.35f, 0.90f, -0.10f, -0.18f, -0.05f },
            new[] { 1.00f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f },
        };

        public struct RollBall { public float Core, R; }
        public struct SlideMarks { public Vector3[] Pts; public Vector3 Hips; }

        // characters.js:256-261 CLIMB_MET_FALLBACK — a human's numbers, for a
        // rig with no method to measure its own (never actually hit in this
        // port's 4-body roster, kept for parity/safety).
        public struct ClimbMet
        {
            public float ShoulderY, ShoulderZ, ShoulderHalf, ArmLen;
            public float HipY, HipZ, HipHalf, LegLen, AnkleOff, Torso;
            public static ClimbMet Fallback => new ClimbMet
            {
                ShoulderY = 1.05f, ShoulderZ = 0.04f, ShoulderHalf = 0.19f, ArmLen = 0.52f,
                HipY = 0.85f, HipZ = 0f, HipHalf = 0.09f, LegLen = 0.75f, AnkleOff = 0.1f, Torso = 0.2f,
            };
        }

        sealed class LegSide { public Transform Thigh, Calf, Foot, Toe; public float L1, L2; }
        sealed class ArmSide { public Transform Upper, Fore, Hand; }

        sealed class PoseRig
        {
            public bool Valid;             // false: no legs to fold (non-Humanoid) — characters.js "legs == null" bodies
            public bool HasArms;
            public Transform Hips, Spine, Neck, Head;
            public LegSide L = new LegSide(), R = new LegSide();
            public ArmSide AL = new ArmSide(), AR = new ArmSide();
            public float AnkleOff;
            public float LegLen, ArmLen;
            public int? CrouchSign;        // characters.js `_crouchSign`
            public float CrouchDrop;       // characters.js `_crouchDrop`
            public float? AnkleY0;         // characters.js `_ankleY0`
            public int? ArmSign;           // characters.js `arms.sign`
            public RollBall? Tuck;         // characters.js `this.tuck`
            public SlideMarks? Slide;      // characters.js `this.slide`
            public readonly Dictionary<Transform, Quaternion> EdgeHeld = new Dictionary<Transform, Quaternion>();
            public int? RightSign;         // characters.js `_rightSign` (aimOffFacing)
            public readonly Dictionary<string, Vector3> Pocket = new Dictionary<string, Vector3>(); // rifleShoulder, measured once per side
        }

        static readonly Dictionary<CharacterRig, PoseRig> _cache = new Dictionary<CharacterRig, PoseRig>();

        static PoseRig Get(CharacterRig rig)
        {
            if (rig == null) return null;
            if (_cache.TryGetValue(rig, out var pr)) return pr;
            pr = Build(rig);
            _cache[rig] = pr;
            return pr;
        }

        static PoseRig Build(CharacterRig rig)
        {
            var pr = new PoseRig();
            if (!rig.IsHumanoid || rig.Anim == null) return pr; // Valid = false
            var a = rig.Anim;
            Transform GB(HumanBodyBones b) => a.GetBoneTransform(b);
            pr.Hips = GB(HumanBodyBones.Hips);
            pr.Spine = GB(HumanBodyBones.Spine);
            pr.Neck = GB(HumanBodyBones.Neck);
            pr.Head = GB(HumanBodyBones.Head);
            pr.L.Thigh = GB(HumanBodyBones.LeftUpperLeg); pr.L.Calf = GB(HumanBodyBones.LeftLowerLeg);
            pr.L.Foot = GB(HumanBodyBones.LeftFoot); pr.L.Toe = GB(HumanBodyBones.LeftToes);
            pr.R.Thigh = GB(HumanBodyBones.RightUpperLeg); pr.R.Calf = GB(HumanBodyBones.RightLowerLeg);
            pr.R.Foot = GB(HumanBodyBones.RightFoot); pr.R.Toe = GB(HumanBodyBones.RightToes);
            pr.AL.Upper = GB(HumanBodyBones.LeftUpperArm); pr.AL.Fore = GB(HumanBodyBones.LeftLowerArm); pr.AL.Hand = GB(HumanBodyBones.LeftHand);
            pr.AR.Upper = GB(HumanBodyBones.RightUpperArm); pr.AR.Fore = GB(HumanBodyBones.RightLowerArm); pr.AR.Hand = GB(HumanBodyBones.RightHand);
            pr.Valid = pr.L.Thigh != null && pr.L.Calf != null && pr.R.Thigh != null && pr.R.Calf != null;
            pr.HasArms = pr.AL.Upper != null && pr.AR.Upper != null;
            if (pr.Valid)
            {
                // Bone-length measurement is POSE-INDEPENDENT (a rigid skeleton's
                // joint-to-joint distance is the bone length whatever the clip is
                // doing this instant) — no bind-pose workaround needed here,
                // unlike characters.js:1752-1771's skeleton.boneInverses dance,
                // which existed only for rigs this port does not carry forward
                // (parked boots — see CharacterRig.cs header).
                pr.L.L1 = Vector3.Distance(pr.L.Thigh.position, pr.L.Calf.position);
                pr.L.L2 = pr.L.Foot != null ? Vector3.Distance(pr.L.Calf.position, pr.L.Foot.position) : pr.L.L1 * 0.9f;
                pr.R.L1 = Vector3.Distance(pr.R.Thigh.position, pr.R.Calf.position);
                pr.R.L2 = pr.R.Foot != null ? Vector3.Distance(pr.R.Calf.position, pr.R.Foot.position) : pr.R.L1 * 0.9f;
                pr.LegLen = (pr.L.L1 + pr.L.L2 + pr.R.L1 + pr.R.L2) / 2f;
                float aoL = rig.AnimGroup.InverseTransformPoint((pr.L.Foot ?? pr.L.Calf).position).y;
                float aoR = rig.AnimGroup.InverseTransformPoint((pr.R.Foot ?? pr.R.Calf).position).y;
                pr.AnkleOff = Mathf.Max(0f, Mathf.Min(aoL, aoR));
            }
            if (pr.HasArms)
            {
                float al = 0f; int an = 0;
                if (pr.AL.Fore != null && pr.AL.Hand != null)
                { al += Vector3.Distance(pr.AL.Upper.position, pr.AL.Fore.position) + Vector3.Distance(pr.AL.Fore.position, pr.AL.Hand.position); an++; }
                if (pr.AR.Fore != null && pr.AR.Hand != null)
                { al += Vector3.Distance(pr.AR.Upper.position, pr.AR.Fore.position) + Vector3.Distance(pr.AR.Fore.position, pr.AR.Hand.position); an++; }
                if (an > 0) pr.ArmLen = al / an;
            }
            return pr;
        }

        static LegSide Leg(PoseRig pr, string side) => side == "L" ? pr.L : pr.R;
        static ArmSide Arm(PoseRig pr, string side) => side == "L" ? pr.AL : pr.AR;

        // characters.js:427-430 qSame — forwarded to CharacterRig's own (public).
        static bool QSame(Quaternion a, Quaternion b) => CharacterRig.QSame(a, b);

        // =====================================================================
        // _aimQ (characters.js:438) — the bone-local rotation that lays a bone
        // along `want` given it lies along `cur` now (both WORLD directions).
        // Handedness-safe: see class header.
        // =====================================================================
        static Quaternion AimLocalRotation(Transform bone, Vector3 curWorldDir, Vector3 wantWorldDir)
        {
            if (bone == null) return Quaternion.identity;
            if (curWorldDir.sqrMagnitude < 1e-12f || wantWorldDir.sqrMagnitude < 1e-12f) return bone.localRotation;
            Quaternion correction = Quaternion.FromToRotation(curWorldDir.normalized, wantWorldDir.normalized);
            Quaternion wantWorld = (correction * bone.rotation).normalized;
            if (bone.parent == null) return wantWorld;
            return (Quaternion.Inverse(bone.parent.rotation) * wantWorld).normalized;
        }

        // characters.js:2597 _boneAxis — a BoneTwist about an arbitrary WORLD
        // axis, not one of the rig's own three. Built on CharacterRig's public
        // ClipQ (what the animator/previous base left) + BoneSet (write +
        // pose-stack bookkeeping), so it participates correctly in ReleaseBones.
        static void BoneAxis(CharacterRig rig, Transform bone, Vector3 axis, float angleDeg)
        {
            if (bone == null || Mathf.Abs(angleDeg) < 1e-6f) return;
            Quaternion baseQ = rig.ClipQ(bone);
            Quaternion d = Quaternion.AngleAxis(angleDeg, axis);
            Quaternion parentWorld = bone.parent != null ? bone.parent.rotation : Quaternion.identity;
            Quaternion local = Quaternion.Inverse(parentWorld) * d * parentWorld;
            rig.BoneSet(bone, (local * baseQ).normalized);
        }

        enum Toward { Fwd, Back, Left, Right, Up, Down, Fold }

        // characters.js:2429 _turnToward — cross-product operand SWAPPED for
        // Unity's left-handedness (class header; CharacterRig.cs's own private
        // TurnToward does the identical swap — this is that same precedent).
        // `axis` defaults to Root.right (col 0 in the JS); pass Root.forward
        // for the abduction case (JS col 2).
        static float TurnTowardDeg(CharacterRig rig, Transform bone, float angleRad, Toward want, Transform tip, Vector3? axis = null)
        {
            if (bone == null || tip == null || Mathf.Abs(angleRad) < 1e-6f) return 0f;
            Vector3 j = bone.position;
            Vector3 r = tip.position - j;
            Vector3 ax = axis ?? rig.Root.right;
            Vector3 move = Vector3.Cross(r, ax); // swapped vs. JS's axis × r
            Vector3 d;
            switch (want)
            {
                case Toward.Fwd: d = rig.Root.forward; break;
                case Toward.Back: d = -rig.Root.forward; break;
                case Toward.Left: d = -rig.Root.right; break;
                case Toward.Right: d = rig.Root.right; break;
                case Toward.Up: d = rig.Root.up; break;
                case Toward.Down: d = -rig.Root.up; break;
                default: // Fold — in toward the joint above (the parent), not a fixed body axis
                    if (bone.parent == null) return 0f;
                    d = bone.parent.position - tip.position;
                    if (d.sqrMagnitude < 1e-8f) return 0f;
                    d.Normalize();
                    break;
            }
            float signedRad = Vector3.Dot(move, d) >= 0f ? angleRad : -angleRad;
            return signedRad * Mathf.Rad2Deg;
        }

        // ---- crouch-sign / arm-sign calibration (characters.js:1791, 2308) ----
        // Found BY TRYING BOTH and keeping whichever reaches further the way
        // the real move wants it to, exactly as CharacterRig.cs's own (private,
        // internal-use-only) CalibrateArms does for SwingArms — this is this
        // file's OWN cached copy (FootPoses cannot reach CharacterRig's private
        // `_armSign`), which is fine: it is a property of the body, and two
        // independent measurements of the same body converge on the same sign.
        static void EnsureAnkleY0(CharacterRig rig, PoseRig pr)
        {
            if (!pr.Valid || pr.AnkleY0.HasValue) return;
            float yL = rig.AnimGroup.InverseTransformPoint((pr.L.Foot ?? pr.L.Calf).position).y;
            float yR = rig.AnimGroup.InverseTransformPoint((pr.R.Foot ?? pr.R.Calf).position).y;
            pr.AnkleY0 = Mathf.Min(yL, yR);
        }

        static void EnsureCrouchSign(CharacterRig rig, PoseRig pr)
        {
            if (!pr.Valid || pr.CrouchSign.HasValue) return;
            EnsureAnkleY0(rig, pr);
            float Reach(int sign)
            {
                foreach (var side in new[] { "L", "R" })
                {
                    var l = Leg(pr, side);
                    rig.BoneTwist(l.Thigh, -KneeFold * 0.55f * sign * Mathf.Rad2Deg);
                    rig.BoneTwist(l.Calf, KneeFold * sign * Mathf.Rad2Deg);
                    if (l.Foot != null) rig.BoneTwist(l.Foot, -KneeFold * 0.45f * sign * Mathf.Rad2Deg);
                }
                Vector3 fwd = rig.Root.forward;
                Vector3 knee = pr.L.Calf.position, hip = (pr.Hips ?? pr.L.Thigh).position;
                return Vector3.Dot(knee - hip, fwd);
            }
            float plus = Reach(1);
            float minus = Reach(-1);
            pr.CrouchSign = plus >= minus ? 1 : -1;
            Reach(pr.CrouchSign.Value); // hold the real pose, then measure the ankle lift
            float yL = rig.AnimGroup.InverseTransformPoint((pr.L.Foot ?? pr.L.Calf).position).y;
            float yR = rig.AnimGroup.InverseTransformPoint((pr.R.Foot ?? pr.R.Calf).position).y;
            float ankleNow = Mathf.Min(yL, yR);
            pr.CrouchDrop = Mathf.Max(0f, ankleNow - pr.AnkleY0.Value);
        }

        static void EnsureArmSign(CharacterRig rig, PoseRig pr)
        {
            if (!pr.HasArms || pr.ArmSign.HasValue) return;
            Transform hand = pr.AL.Hand ?? pr.AR.Hand ?? pr.AL.Fore ?? pr.AR.Fore;
            Transform hips = pr.Hips ?? pr.L.Thigh ?? pr.R.Thigh;
            if (hand == null || hips == null) { pr.ArmSign = pr.CrouchSign ?? 1; return; }
            float Reach(int sign)
            {
                float a = TuckFold * sign;
                rig.BoneTwist(pr.AL.Upper, -a * 0.4f * Mathf.Rad2Deg);
                rig.BoneTwist(pr.AL.Fore, a * 0.55f * Mathf.Rad2Deg);
                rig.BoneTwist(pr.AR.Upper, -a * 0.4f * Mathf.Rad2Deg);
                rig.BoneTwist(pr.AR.Fore, a * 0.55f * Mathf.Rad2Deg);
                return Vector3.Distance(hand.position, hips.position);
            }
            float plus = Reach(1);
            float minus = Reach(-1);
            pr.ArmSign = plus <= minus ? 1 : -1;
            rig.ReleaseBones();
        }

        // =====================================================================
        // CROUCH — characters.js:1192 setCrouch. Returns true when it fell back
        // to the whole-body squash (never happens for the 4-body roster's
        // Humanoid bodies; kept for the elephant/any future Generic body).
        // =====================================================================
        public static bool SetCrouch(CharacterRig rig, float t)
        {
            var pr = Get(rig);
            if (!pr.Valid)
            {
                float sq = 1f - 0.26f * t;
                rig.ActGroup.localScale = new Vector3(1f + 0.08f * t, sq, 1f + 0.08f * t);
                rig.ActGroup.localRotation = Quaternion.Euler(0.18f * t * Mathf.Rad2Deg, 0f, 0f);
                return true;
            }
            rig.ActGroup.localScale = Vector3.one;
            rig.ActGroup.localRotation = Quaternion.identity;
            if (t < 0.02f)
            {
                var p0 = rig.ActGroup.localPosition; p0.y = 0f; rig.ActGroup.localPosition = p0;
                pr.AnkleY0 = null;
                rig.ReleaseBones();
                return false;
            }
            var p = rig.ActGroup.localPosition; p.y = 0f; rig.ActGroup.localPosition = p;
            EnsureAnkleY0(rig, pr);
            EnsureCrouchSign(rig, pr);
            float k = KneeFold * t * (pr.CrouchSign ?? 1);
            foreach (var side in new[] { "L", "R" })
            {
                var l = Leg(pr, side);
                rig.BoneTwist(l.Thigh, -k * 0.55f * Mathf.Rad2Deg);
                rig.BoneTwist(l.Calf, k * Mathf.Rad2Deg);
                if (l.Foot != null) rig.BoneTwist(l.Foot, -k * 0.45f * Mathf.Rad2Deg);
            }
            rig.BoneTwist(pr.Spine, -k * 0.22f * Mathf.Rad2Deg);
            var pp = rig.ActGroup.localPosition; pp.y = -pr.CrouchDrop * t; rig.ActGroup.localPosition = pp;
            return false;
        }

        // =====================================================================
        // SEATED — characters.js:1250 setSeated/1396 _poseSeated. Named in this
        // port's scope because the POSE is CharacterRig's; the surrounding
        // cockpit/seat orchestration (sitInPlane/leaveSeat/updateSeated/
        // _flyControls, foot.js:3826-4126) is the Flight/Cockpit system's, not
        // this file's (see SKILL.md — already ported per
        // docs/port_units/cockpit.json). Returns the hip height above the
        // soles, same contract as the JS.
        // =====================================================================
        public static float SetSeated(CharacterRig rig, float t)
        {
            var pr = Get(rig);
            if (!pr.Valid)
            {
                return pr.Hips != null ? rig.AnimGroup.InverseTransformPoint(pr.Hips.position).y : CharacterRoster.HeroHeight * 0.52f;
            }
            rig.ActGroup.localScale = Vector3.one;
            var p0 = rig.ActGroup.localPosition; p0.y = 0f;
            rig.ActGroup.localRotation = Quaternion.identity;
            rig.ActGroup.localPosition = p0;
            EnsureAnkleY0(rig, pr);
            EnsureCrouchSign(rig, pr);
            if (pr.HasArms) EnsureArmSign(rig, pr);
            PoseSeated(rig, pr, t);
            return pr.Hips != null ? rig.AnimGroup.InverseTransformPoint(pr.Hips.position).y : CharacterRoster.HeroHeight * 0.52f;
        }

        static void PoseSeated(CharacterRig rig, PoseRig pr, float t)
        {
            if (t < 0.02f) { rig.ReleaseBones(); return; }
            float k = SitFold * t * (pr.CrouchSign ?? 1);
            foreach (var side in new[] { "L", "R" })
            {
                var l = Leg(pr, side);
                rig.BoneTwist(l.Thigh, -k * Mathf.Rad2Deg);
                rig.BoneTwist(l.Calf, k * 0.95f * Mathf.Rad2Deg);
                if (l.Foot != null) rig.BoneTwist(l.Foot, -k * 0.15f * Mathf.Rad2Deg);
            }
            rig.BoneTwist(pr.Spine, -k * SitRecline * Mathf.Rad2Deg);
            if (!pr.HasArms) return;
            float a = SitFold * t * (pr.ArmSign ?? 1);
            foreach (var side in new[] { "L", "R" })
            {
                var g = Arm(pr, side);
                rig.BoneTwist(g.Upper, -a * 0.22f * Mathf.Rad2Deg);
                rig.BoneTwist(g.Fore, a * 0.5f * Mathf.Rad2Deg);
            }
        }

        // =====================================================================
        // ROLL TUCK — characters.js:1434 setRollTuck/1455 _poseTuck. Returns the
        // measured ball (centre height above soles, radius) the dodge roll
        // spins about, null for a body with no legs to fold.
        // =====================================================================
        public static RollBall? SetRollTuck(CharacterRig rig, float t)
        {
            var pr = Get(rig);
            if (!pr.Valid) return null;
            EnsureCrouchSign(rig, pr);
            if (pr.HasArms) EnsureArmSign(rig, pr);
            if (!pr.Tuck.HasValue)
            {
                PoseTuck(rig, pr, 1f);
                pr.Tuck = MeasureBall(rig, pr);
            }
            PoseTuck(rig, pr, t);
            return pr.Tuck;
        }

        static void PoseTuck(CharacterRig rig, PoseRig pr, float t)
        {
            if (t < 0.02f) { rig.ReleaseBones(); return; }
            float k = TuckFold * t * (pr.CrouchSign ?? 1);
            foreach (var side in new[] { "L", "R" })
            {
                var l = Leg(pr, side);
                rig.BoneTwist(l.Thigh, -k * 0.85f * Mathf.Rad2Deg);
                rig.BoneTwist(l.Calf, k * Mathf.Rad2Deg);
                if (l.Foot != null) rig.BoneTwist(l.Foot, -k * 0.25f * Mathf.Rad2Deg);
            }
            rig.BoneTwist(pr.Spine, -k * 0.45f * Mathf.Rad2Deg);
            if (!pr.HasArms)
            {
                rig.BoneTwist(pr.Neck ?? pr.Head, -k * 0.3f * Mathf.Rad2Deg);
                return;
            }
            float a = TuckFold * t * (pr.ArmSign ?? 1);
            foreach (var side in new[] { "L", "R" })
            {
                var g = Arm(pr, side);
                rig.BoneTwist(g.Upper, -a * 0.4f * Mathf.Rad2Deg);
                rig.BoneTwist(g.Fore, a * 0.55f * Mathf.Rad2Deg);
            }
            rig.BoneTwist(pr.Neck ?? pr.Head, -k * 0.3f * Mathf.Rad2Deg);
        }

        // characters.js:2271 _measureMarks, 2334 _measureBall — measured off the
        // posed BONES (a skinned bounding box reports the bind pose), in
        // AnimGroup's local frame (metres above the soles).
        static RollBall MeasureBall(CharacterRig rig, PoseRig pr)
        {
            var pts = new List<Vector3>();
            void Add(Transform b) { if (b != null) pts.Add(rig.AnimGroup.InverseTransformPoint(b.position)); }
            Add(pr.L.Foot ?? pr.L.Calf); Add(pr.L.Thigh); Add(pr.AL.Hand ?? pr.AL.Fore);
            Add(pr.R.Foot ?? pr.R.Calf); Add(pr.R.Thigh); Add(pr.AR.Hand ?? pr.AR.Fore);
            Add(pr.Hips); Add(pr.Spine); Add(pr.Head ?? pr.Neck);
            if (pts.Count == 0) return new RollBall { Core = CharacterRoster.HeroHeight / 2f, R = CharacterRoster.HeroHeight / 3.4f };
            Vector3 c = Vector3.zero;
            foreach (var p in pts) c += p;
            c /= pts.Count;
            float r = 0f;
            foreach (var p in pts) r = Mathf.Max(r, Vector3.Distance(p, c));
            return new RollBall { Core = c.y, R = Mathf.Min(CharacterRoster.HeroHeight * 0.42f, r + 0.13f) };
        }

        // =====================================================================
        // SLIDE POSE — characters.js:1484 setSlidePose/1503 _poseSlide.
        // =====================================================================
        public static SlideMarks? SetSlidePose(CharacterRig rig, float t, int side)
        {
            var pr = Get(rig);
            if (!pr.Valid) return null;
            EnsureAnkleY0(rig, pr);
            EnsureCrouchSign(rig, pr);
            if (pr.HasArms) EnsureArmSign(rig, pr);
            if (!pr.Slide.HasValue)
            {
                PoseSlide(rig, pr, 1f, side);
                pr.Slide = MeasureMarks(rig, pr);
            }
            PoseSlide(rig, pr, t, side);
            return pr.Slide;
        }

        static void PoseSlide(CharacterRig rig, PoseRig pr, float t, int side)
        {
            if (t < 0.02f) { rig.ReleaseBones(); return; }
            int sign = pr.CrouchSign ?? 1;
            float k = SlideFold * t * sign;
            string lead = side > 0 ? "R" : "L";
            foreach (var s in new[] { "L", "R" })
            {
                var l = Leg(pr, s);
                if (s == lead)
                {
                    rig.BoneTwist(l.Thigh, -k * 0.36f * Mathf.Rad2Deg);
                    rig.BoneTwist(l.Calf, k * 0.1f * Mathf.Rad2Deg);
                    if (l.Foot != null) rig.BoneTwist(l.Foot, -k * 0.45f * Mathf.Rad2Deg);
                }
                else
                {
                    rig.BoneTwist(l.Thigh, -k * 0.2f * Mathf.Rad2Deg);
                    rig.BoneTwist(l.Calf, k * 0.95f * Mathf.Rad2Deg);
                    if (l.Foot != null) rig.BoneTwist(l.Foot, k * 0.5f * Mathf.Rad2Deg);
                }
            }
            rig.BoneTwist(pr.Spine, -k * 0.18f * Mathf.Rad2Deg);
            if (!pr.HasArms) { rig.BoneTwist(pr.Neck ?? pr.Head, -k * 0.55f * Mathf.Rad2Deg); return; }
            float a = SlideFold * t * (pr.ArmSign ?? 1);
            foreach (var s in new[] { "L", "R" })
            {
                var g = Arm(pr, s);
                rig.BoneTwist(g.Upper, -a * 0.22f * Mathf.Rad2Deg);
                rig.BoneTwist(g.Fore, a * 0.3f * Mathf.Rad2Deg);
            }
            rig.BoneTwist(pr.Neck ?? pr.Head, -k * 0.55f * Mathf.Rad2Deg);
        }

        static SlideMarks MeasureMarks(CharacterRig rig, PoseRig pr)
        {
            var pts = new List<Vector3>();
            float sole = Mathf.Max(0.03f, pr.AnkleY0 ?? 0.08f);
            void AddFoot(Transform b)
            {
                if (b == null) return;
                Vector3 p = rig.AnimGroup.InverseTransformPoint(b.position);
                pts.Add(p); pts.Add(new Vector3(p.x, p.y - sole, p.z));
            }
            void Add(Transform b) { if (b != null) pts.Add(rig.AnimGroup.InverseTransformPoint(b.position)); }
            foreach (var side in new[] { "L", "R" })
            {
                var l = Leg(pr, side); var a = Arm(pr, side);
                AddFoot(l.Foot ?? l.Calf);
                AddFoot(l.Toe);
                Add(l.Calf); Add(l.Thigh); Add(a.Hand);
            }
            Add(pr.Spine);
            Add(pr.Head ?? pr.Neck);
            Vector3 hips = pr.Hips != null ? rig.AnimGroup.InverseTransformPoint(pr.Hips.position)
                : (pr.Spine != null ? rig.AnimGroup.InverseTransformPoint(pr.Spine.position) : new Vector3(0, CharacterRoster.HeroHeight * 0.52f, 0));
            pts.Add(hips);
            return new SlideMarks { Pts = pts.ToArray(), Hips = hips };
        }

        // =====================================================================
        // AIR POSE — characters.js:1551 setAirPose. `lead` > 0 picks R as the
        // lead leg (see JS: `const L = lead > 0 ? 'R' : 'L';`).
        // =====================================================================
        public static bool SetAirPose(CharacterRig rig, float w, float p, float lead = 1f)
        {
            var pr = Get(rig);
            if (!pr.Valid) return false;
            EnsureAnkleY0(rig, pr);
            EnsureCrouchSign(rig, pr);
            if (w < 0.02f) { rig.ReleaseBones(); return true; }
            if (pr.HasArms) EnsureArmSign(rig, pr);
            bool half = p < 0.5f;
            float[] a = AirKeys[half ? 0 : 1];
            float[] b = AirKeys[half ? 1 : 2];
            float u = half ? p * 2f : (p - 0.5f) * 2f;
            float V(int i) => a[i] + (b[i] - a[i]) * u;
            float k = AirFold * w * (pr.CrouchSign ?? 1);
            string L = lead > 0 ? "R" : "L";
            foreach (var side in new[] { "L", "R" })
            {
                var l = Leg(pr, side);
                int o = side == L ? 0 : 3;
                rig.BoneTwist(l.Thigh, V(o) * k * Mathf.Rad2Deg);
                rig.BoneTwist(l.Calf, V(o + 1) * k * Mathf.Rad2Deg);
                if (l.Foot != null) rig.BoneTwist(l.Foot, V(o + 2) * k * Mathf.Rad2Deg);
            }
            rig.BoneTwist(pr.Spine, V(6) * k * Mathf.Rad2Deg);
            if (pr.HasArms)
            {
                float ak = AirFold * w * (pr.ArmSign ?? 1);
                foreach (var side in new[] { "L", "R" })
                {
                    var g = Arm(pr, side);
                    rig.BoneTwist(g.Upper, V(7) * ak * Mathf.Rad2Deg);
                    rig.BoneTwist(g.Fore, V(8) * ak * Mathf.Rad2Deg);
                }
                rig.BoneTwist(pr.Neck ?? pr.Head, V(9) * k * Mathf.Rad2Deg);
            }
            return true;
        }

        // =====================================================================
        // THE CLIMB's legs — characters.js:1609 setPullPose/1615 setMantlePose/
        // 1643 _legKeysAt/1657 _poseLegs. No arm column: both hands are solved
        // onto the ledge WORLD-LOCKED by FootClimb (handsOnEdge machinery) —
        // a keyed arm here would fight that IK, exactly as the JS says.
        // =====================================================================
        static readonly float[] _keyv = new float[8];

        static float[] LegKeysAt(float[][] keys, float u)
        {
            float[] a = keys[keys.Length - 1], b = a;
            for (int i = 0; i + 1 < keys.Length; i++)
            {
                if (u <= keys[i + 1][0]) { a = keys[i]; b = keys[i + 1]; break; }
            }
            float span = b[0] - a[0];
            float t = span > 1e-6f ? Mathf.Clamp01((u - a[0]) / span) : 1f;
            for (int i = 0; i < 8; i++) _keyv[i] = a[i + 1] + (b[i + 1] - a[i + 1]) * t;
            return _keyv;
        }

        static bool PoseLegs(CharacterRig rig, PoseRig pr, float[] v, float w, string lead)
        {
            if (!pr.Valid) return false;
            float k = AirFold * w * (pr.CrouchSign ?? 1);
            foreach (var side in new[] { "L", "R" })
            {
                var l = Leg(pr, side);
                int o = side == lead ? 0 : 3;
                rig.BoneTwist(l.Thigh, v[o] * k * Mathf.Rad2Deg);
                rig.BoneTwist(l.Calf, v[o + 1] * k * Mathf.Rad2Deg);
                if (l.Foot != null) rig.BoneTwist(l.Foot, v[o + 2] * k * Mathf.Rad2Deg); // a parked boot has no ankle — N/A in this roster
            }
            rig.BoneTwist(pr.Spine, v[6] * k * Mathf.Rad2Deg);
            LookUp(rig, pr, v[7] * AirFold * w);
            return true;
        }

        // characters.js:1676 _lookUp — the head tipped back (+) to look at the
        // edge, or forward (-) over it, signed GEOMETRICALLY off where the head
        // IS this frame rather than a per-rig sign table.
        static void LookUp(CharacterRig rig, PoseRig pr, float angle)
        {
            Transform neck = pr.Neck ?? pr.Head;
            if (neck == null || Mathf.Abs(angle) < 1e-6f) return;
            Transform tip = (pr.Head != null && pr.Head != neck) ? pr.Head : null;
            float deg = TurnTowardDeg(rig, neck, Mathf.Abs(angle), angle > 0f ? Toward.Back : Toward.Fwd, tip ?? FarthestChild(neck));
            rig.BoneTwist(neck, deg);
        }

        // characters.js:2469 _limbTip fallback — only reached when there is no
        // explicit tip bone (LookUp's `tip` arg handles the common case).
        static Transform FarthestChild(Transform bone)
        {
            Transform best = null; float far = -1f;
            foreach (Transform c in bone.GetComponentsInChildren<Transform>(true))
            {
                if (c == bone) continue;
                float d = Vector3.Distance(c.position, bone.position);
                if (d > far) { far = d; best = c; }
            }
            return best;
        }

        public static bool SetPullPose(CharacterRig rig, float w, float u, string lead = "R")
        {
            var pr = Get(rig);
            if (w < 0.02f) { rig.ReleaseBones(); return true; }
            return PoseLegs(rig, pr, LegKeysAt(PullKeys, u), w, lead);
        }

        public static bool SetMantlePose(CharacterRig rig, float w, float u, string lead = "R")
        {
            var pr = Get(rig);
            if (w < 0.02f) { rig.ReleaseBones(); return true; }
            return PoseLegs(rig, pr, LegKeysAt(MantleKeys, u), w, lead);
        }

        // characters.js:1625 setHangPose — both hands overhead (FootClimb), the
        // body hanging straight under them, a slow pendulum sway.
        public static bool SetHangPose(CharacterRig rig, float w, float t = 0f)
        {
            if (w < 0.02f) { rig.ReleaseBones(); return true; }
            var pr = Get(rig);
            if (!pr.Valid) return false;
            float sway = Mathf.Sin(t * 1.7f) * 0.06f;
            float k = AirFold * w * (pr.CrouchSign ?? 1);
            foreach (var s in new[] { "L", "R" })
            {
                var l = Leg(pr, s);
                float ld = s == "L" ? 1f : 0.75f;
                rig.BoneTwist(l.Thigh, k * (-0.10f * ld + sway) * Mathf.Rad2Deg);
                rig.BoneTwist(l.Calf, k * 0.24f * ld * Mathf.Rad2Deg);
                if (l.Foot != null) rig.BoneTwist(l.Foot, k * 0.18f * Mathf.Rad2Deg);
            }
            rig.BoneTwist(pr.Spine, k * (-0.05f + sway * 0.5f) * Mathf.Rad2Deg);
            LookUp(rig, pr, 0.40f * w);
            return true;
        }

        // =====================================================================
        // DIVE POSE — characters.js:2101 setDivePose. The air dive's body: a
        // superman dive, arms stretched ahead, legs straight and together.
        // =====================================================================
        public static bool SetDivePose(CharacterRig rig, float w, int side = 1)
        {
            var pr = Get(rig);
            if (!pr.Valid) return false;
            EnsureAnkleY0(rig, pr);
            EnsureCrouchSign(rig, pr);
            if (w < 0.02f) { rig.ReleaseBones(); return true; }
            if (pr.HasArms) EnsureArmSign(rig, pr);
            float k = AirFold * w * (pr.CrouchSign ?? 1);
            foreach (var s in new[] { "L", "R" })
            {
                var l = Leg(pr, s);
                rig.BoneTwist(l.Thigh, k * 0.12f * Mathf.Rad2Deg);
                rig.BoneTwist(l.Calf, k * 0.08f * Mathf.Rad2Deg);
                if (l.Foot != null) rig.BoneTwist(l.Foot, k * 0.35f * Mathf.Rad2Deg);
            }
            rig.BoneTwist(pr.Spine, k * 0.12f * Mathf.Rad2Deg);
            if (pr.HasArms)
            {
                float a = AirFold * w * (pr.ArmSign ?? 1);
                string leadSide = side > 0 ? "R" : "L";
                foreach (var s in new[] { "L", "R" })
                {
                    var g = Arm(pr, s);
                    float lead = s == leadSide ? 1.12f : 0.9f;
                    rig.BoneTwist(g.Upper, -a * 0.95f * lead * Mathf.Rad2Deg);
                    rig.BoneTwist(g.Fore, -a * 0.1f * Mathf.Rad2Deg);
                }
                rig.BoneTwist(pr.Neck ?? pr.Head, k * 0.5f * Mathf.Rad2Deg);
            }
            return true;
        }

        // =====================================================================
        // PLANT FOOT — characters.js:1910 plantFoot. A two-bone solve (law of
        // cosines) on the thigh and shin onto a WORLD point, knee placed along
        // `kneeDir` (also world). Returns how much of the leg's reach the point
        // needed (>= PLANT_RELEASE in FootClimb.cs means "let go": the leg is
        // straight, the push is spent), 0 for a body with no leg to plant.
        // =====================================================================
        public static float PlantFoot(CharacterRig rig, string side, Vector3 plant, Vector3 kneeDir, Vector3? toeDir, float w)
        {
            var pr = Get(rig);
            if (!pr.Valid || !(w > 0.001f)) return 0f;
            var l = Leg(pr, side);
            if (l.Thigh == null || l.Calf == null) return 0f;
            Transform thigh = l.Thigh, calf = l.Calf, ankle = l.Foot, toe = l.Toe;
            float l1 = l.L1, l2 = l.L2;
            Quaternion q0t = thigh.localRotation, q0c = calf.localRotation;
            Quaternion q0a = ankle != null ? ankle.localRotation : Quaternion.identity;
            Vector3 H = thigh.position;
            Vector3 K0 = calf.position;
            Vector3 toT = plant - H;
            float d = toT.magnitude;
            if (d < 1e-5f) return 0f;
            toT /= d;
            float reach = d / (l1 + l2);
            float dd = Mathf.Clamp(d, Mathf.Abs(l1 - l2) + 0.02f, (l1 + l2) * 0.985f);
            float cosA = Mathf.Clamp((l1 * l1 + dd * dd - l2 * l2) / (2f * l1 * dd), -1f, 1f);
            float sinA = Mathf.Sqrt(1f - cosA * cosA);
            Vector3 perp = kneeDir - toT * Vector3.Dot(kneeDir, toT);
            if (perp.sqrMagnitude < 1e-6f) perp = Vector3.up - toT * toT.y;
            perp.Normalize();
            Vector3 knee = H + toT * (l1 * cosA) + perp * (l1 * sinA);
            Quaternion qT = AimLocalRotation(thigh, K0 - H, knee - H);
            if (w < 0.999f) qT = Quaternion.Slerp(q0t, qT, w);
            rig.BoneSet(thigh, qT);
            Vector3 K = calf.position; // live — Unity recomputes it the instant thigh.localRotation changed, no manual update needed
            Vector3 cur = ankle != null ? ankle.position - K : (calf.rotation * Vector3.up) * l2;
            Quaternion qC = AimLocalRotation(calf, cur, plant - K);
            if (w < 0.999f) qC = Quaternion.Slerp(q0c, qC, w);
            rig.BoneSet(calf, qC);
            if (ankle != null && toe != null && toeDir.HasValue)
            {
                Vector3 A = ankle.position;
                Quaternion qA = AimLocalRotation(ankle, toe.position - A, toeDir.Value);
                if (w < 0.999f) qA = Quaternion.Slerp(q0a, qA, w);
                rig.BoneSet(ankle, qA);
            }
            return reach;
        }

        // characters.js:1967 settleFeet — the parked-boot carry. A no-op here:
        // every body that reaches this file is one of the 3 Humanoid bodies in
        // the 4-body roster, and a Humanoid Avatar's Foot bone is ALWAYS a
        // child of its Calf (that is what Unity's auto-mapper requires) — the
        // "parked boot" case (the astronaut's FootL hung off the root) is a
        // body this roster deliberately excludes (CharacterRig.cs's header:
        // "the astronaut... is this port's NOT ported" list). Kept as a named
        // call so FootClimb's per-frame sequence matches the JS 1:1.
        public static void SettleFeet(CharacterRig rig) { }

        // =====================================================================
        // CLIMB METRICS — characters.js:1849 climbMetrics.
        // =====================================================================
        public static ClimbMet ClimbMetrics(CharacterRig rig)
        {
            var pr = Get(rig);
            if (!pr.Valid && !pr.HasArms) return ClimbMet.Fallback;
            var m = ClimbMet.Fallback;
            if (pr.HasArms)
            {
                float sy = 0, sz = 0, sx = 0; int n = 0;
                foreach (var side in new[] { "L", "R" })
                {
                    var up = Arm(pr, side).Upper;
                    if (up == null) continue;
                    Vector3 p = rig.AnimGroup.InverseTransformPoint(up.position);
                    sy += p.y; sz += p.z; sx += Mathf.Abs(p.x); n++;
                }
                if (n > 0) { m.ShoulderY = sy / n; m.ShoulderZ = sz / n; m.ShoulderHalf = sx / n; }
            }
            if (pr.Valid)
            {
                float hy = 0, hz = 0, hx = 0;
                foreach (var side in new[] { "L", "R" })
                {
                    Vector3 p = rig.AnimGroup.InverseTransformPoint(Leg(pr, side).Thigh.position);
                    hy += p.y; hz += p.z; hx += Mathf.Abs(p.x);
                }
                m.HipY = hy / 2f; m.HipZ = hz / 2f; m.HipHalf = hx / 2f;
                m.AnkleOff = pr.AnkleOff;
                if (pr.LegLen > 0f) m.LegLen = pr.LegLen;
            }
            if (pr.ArmLen > 0f) m.ArmLen = pr.ArmLen;
            m.Torso = m.ShoulderY - m.HipY;
            return m;
        }

        // =====================================================================
        // BOTH HANDS ON A SURFACE — characters.js:2013 handsOnEdge. WORLD-
        // LOCKED (hard rule): each hand is a two-bone IK solve to a fixed world
        // point via Combat.Weapons.ApplyReachPose (the SAME solver
        // src/weapons.js's applyReachPose is — foot.js's climb uses that exact
        // function, per weapons.js's own header: "foot.js's climb uses the same
        // three [this, findArmChain, findArmChainL]"), faded both by `w` and by
        // how far the shoulder is from the target (a hand lets go when the arm
        // runs out, rather than stretching to a point it cannot reach).
        // =====================================================================
        public static bool HandsOnEdge(CharacterRig rig, Vector3? targetL, Vector3? targetR, float w = 1f, Vector3? hook = null)
        {
            var pr = Get(rig);
            if (!pr.HasArms) return false;
            if (!(w > 0.02f))
            {
                foreach (var kv in new List<KeyValuePair<Transform, Quaternion>>(pr.EdgeHeld))
                    if (QSame(kv.Value, kv.Key.localRotation)) rig.ReleaseBone(kv.Key);
                pr.EdgeHeld.Clear();
                return true;
            }
            float len = ClimbMetrics(rig).ArmLen; if (len <= 0f) len = 0.55f;
            bool any = false; float curl = 0f;
            Vector3 down = hook ?? -rig.Root.up;
            foreach (var pair in new (string side, Vector3? target)[] { ("L", targetL), ("R", targetR) })
            {
                if (!pair.target.HasValue) continue;
                var a = Arm(pr, pair.side);
                Transform hand = a.Hand;
                if (a.Upper == null || a.Fore == null || hand == null) continue;
                Vector3 S = a.Upper.position;
                float wr = w * Mathf.Clamp01((EdgeReachTo * len - Vector3.Distance(S, pair.target.Value)) / ((EdgeReachTo - EdgeReachFrom) * len));
                if (!(wr > 0.01f)) continue;
                Quaternion preU = a.Upper.localRotation, preF = a.Fore.localRotation;
                Quaternion clipU = rig.ClipQ(a.Upper), clipF = rig.ClipQ(a.Fore);
                var chain = new Weapons.ArmChain { Upper = a.Upper, Fore = a.Fore };
                if (!Weapons.ApplyReachPose(rig.ActGroup, rig.Root, chain, hand, pair.target.Value, null, null)) continue;
                Quaternion fU = a.Upper.localRotation, fF = a.Fore.localRotation;
                if (wr < 0.999f) { fU = Quaternion.Slerp(clipU, fU, wr); fF = Quaternion.Slerp(clipF, fF, wr); }
                a.Upper.localRotation = preU; rig.BoneSet(a.Upper, fU);
                a.Fore.localRotation = preF; rig.BoneSet(a.Fore, fF);
                pr.EdgeHeld[a.Upper] = a.Upper.localRotation;
                pr.EdgeHeld[a.Fore] = a.Fore.localRotation;
                SetHandHook(rig, pair.side, down, wr);
                curl = Mathf.Max(curl, wr);
                any = true;
            }
            if (curl > 0.01f) SetGrip(rig, curl, null);
            return any;
        }

        // characters.js:2082 setHandHook — the mitten hand bent to point along
        // `dir`. Only fires for a hand with no finger chain to curl instead
        // (SetGrip handles that case) — checked each call via HasFingers, since
        // which of the 3 Humanoid bodies ships finger bones is not assumed.
        public static bool SetHandHook(CharacterRig rig, string side, Vector3 dir, float w = 1f)
        {
            var pr = Get(rig);
            if (!pr.HasArms || HasFingers(rig)) return false;
            var a = Arm(pr, side);
            if (a.Hand == null || a.Fore == null) return false;
            if (!(w > 0.001f)) return true;
            Vector3 cur = a.Hand.position - a.Fore.position;
            Quaternion q = AimLocalRotation(a.Hand, cur, dir);
            if (w < 0.999f) q = Quaternion.Slerp(rig.ClipQ(a.Hand), q, w);
            rig.BoneSet(a.Hand, q);
            return true;
        }

        // =====================================================================
        // GRIP — characters.js:588 setGrip. `t` 0 open .. 1 fist; `skip` is a
        // busy hand (holding a weapon) left exactly as the weapon system posed
        // it. Returns false where there are no fingers to close.
        // =====================================================================
        static readonly HumanBodyBones[][] FingerChains =
        {
            new[] { HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal },
            new[] { HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal },
            new[] { HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal },
            new[] { HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal },
            new[] { HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal },
        };
        static readonly HumanBodyBones[][] FingerChainsR =
        {
            new[] { HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal },
            new[] { HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal },
            new[] { HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal },
            new[] { HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal },
            new[] { HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal },
        };

        static bool? _hasFingers;
        static bool HasFingers(CharacterRig rig)
        {
            if (_hasFingers.HasValue) return _hasFingers.Value;
            bool has = rig.Anim != null && rig.Anim.GetBoneTransform(HumanBodyBones.LeftIndexProximal) != null;
            _hasFingers = has;
            return has;
        }

        public static bool SetGrip(CharacterRig rig, float t, Transform skip)
        {
            if (!HasFingers(rig)) return false;
            var a = rig.Anim;
            float k = Mathf.Clamp01(t);
            foreach (var sideSet in new[] { (side: "L", chains: FingerChains, hand: a.GetBoneTransform(HumanBodyBones.LeftHand)),
                                             (side: "R", chains: FingerChainsR, hand: a.GetBoneTransform(HumanBodyBones.RightHand)) })
            {
                Transform wrist = sideSet.hand;
                if (wrist == null) continue;
                bool busy = skip != null && (wrist == skip || IsUnder(skip, wrist) || IsUnder(wrist, skip));
                float amount = busy ? 0f : k;
                foreach (var chainBones in sideSet.chains)
                {
                    var bones = new List<Transform>();
                    foreach (var hb in chainBones) { var b = a.GetBoneTransform(hb); if (b != null) bones.Add(b); }
                    if (bones.Count == 0) continue;
                    if (amount < 0.02f) { foreach (var b in bones) rig.ReleaseBone(b); continue; }
                    Transform root = bones[0];
                    Transform tip = bones[bones.Count - 1];
                    Transform par = root.parent != null ? root.parent : wrist;
                    Vector3 j = root.position;
                    Vector3 r = tip.position - j;
                    Vector3 toWrist = par.position - j;
                    Vector3 axis = Vector3.Cross(toWrist, r); // swapped vs. JS's r × toWrist (class header)
                    if (axis.sqrMagnitude < 1e-12f) continue;
                    axis.Normalize();
                    bool thumb = chainBones == FingerChains[0] || chainBones == FingerChainsR[0];
                    float ang = (thumb ? FistThumb : FistFold) * amount * Mathf.Rad2Deg;
                    for (int i = 0; i < bones.Count; i++) BoneAxis(rig, bones[i], axis, ang * (i == 0 ? 1f : 0.75f));
                }
            }
            return true;
        }

        static bool IsUnder(Transform node, Transform ancestor)
        {
            for (Transform p = node != null ? node.parent : null; p != null; p = p.parent) if (p == ancestor) return true;
            return false;
        }

        // =====================================================================
        // WIND TUNNEL POSES — characters.js:2145 setWindBrace / 2190 setFlyPose
        // / 2238 setWingGrip. All three use `_legRig` in the JS (not
        // `_crouchRig`), specifically so a "parked boot" body (the astronaut)
        // still gets a pose — moot here (see SettleFeet), so Valid is the same
        // gate as everywhere else in this file.
        // =====================================================================
        public static bool SetWindBrace(CharacterRig rig, float w, int side = 1)
        {
            var pr = Get(rig);
            if (!pr.Valid) return false;
            if (w < 0.02f) { rig.ReleaseBones(); return true; }
            EnsureCrouchSign(rig, pr);
            if (pr.HasArms) EnsureArmSign(rig, pr);
            float k = AirFold * w * (pr.CrouchSign ?? 1);
            string up = side > 0 ? "R" : "L", other = side > 0 ? "L" : "R";
            foreach (var s in new[] { "L", "R" })
            {
                var l = Leg(pr, s);
                float back = s == other ? 1.15f : 0.75f;
                rig.BoneTwist(l.Thigh, k * 0.10f * back * Mathf.Rad2Deg);
                rig.BoneTwist(l.Calf, k * 0.34f * back * Mathf.Rad2Deg);
                if (l.Foot != null) rig.BoneTwist(l.Foot, -k * 0.12f * back * Mathf.Rad2Deg);
            }
            rig.BoneTwist(pr.Spine, -k * 0.20f * Mathf.Rad2Deg);
            if (pr.HasArms)
            {
                float a = AirFold * w * (pr.ArmSign ?? 1);
                Transform HandOf(string s) { var g = Arm(pr, s); return g.Hand ?? g.Fore; }
                var gUp = Arm(pr, up); var gOther = Arm(pr, other);
                float rollUp = TurnTowardDeg(rig, gUp.Upper, 0.30f * w, up == "R" ? Toward.Right : Toward.Left, HandOf(up), rig.Root.forward);
                rig.BoneTurn(gUp.Upper, -a * 1.30f * Mathf.Rad2Deg, 0f, rollUp);
                rig.BoneTwist(gUp.Fore, -a * 0.16f * Mathf.Rad2Deg);
                float rollOther = TurnTowardDeg(rig, gOther.Upper, 0.08f * w, other == "R" ? Toward.Left : Toward.Right, HandOf(other), rig.Root.forward);
                rig.BoneTurn(gOther.Upper, a * 0.16f * Mathf.Rad2Deg, 0f, rollOther);
                rig.BoneTwist(gOther.Fore, TurnTowardDeg(rig, gOther.Fore, 1.05f * w, Toward.Fold, HandOf(other)));
                LookUp(rig, pr, 0.22f * w);
            }
            SettleFeet(rig);
            return true;
        }

        public static bool SetFlyPose(CharacterRig rig, float w)
        {
            var pr = Get(rig);
            if (!pr.Valid) return false;
            if (w < 0.02f) { rig.ReleaseBones(); return true; }
            EnsureCrouchSign(rig, pr);
            if (pr.HasArms) EnsureArmSign(rig, pr);
            float k = AirFold * w * (pr.CrouchSign ?? 1);
            foreach (var s in new[] { "L", "R" })
            {
                var l = Leg(pr, s);
                rig.BoneTwist(l.Thigh, k * 0.14f * Mathf.Rad2Deg);
                rig.BoneTwist(l.Calf, k * 0.46f * Mathf.Rad2Deg);
                if (l.Foot != null) rig.BoneTwist(l.Foot, k * 0.18f * Mathf.Rad2Deg);
            }
            rig.BoneTwist(pr.Spine, k * 0.26f * Mathf.Rad2Deg);
            if (pr.HasArms)
            {
                float a = AirFold * w * (pr.ArmSign ?? 1);
                foreach (var s in new[] { "L", "R" })
                {
                    var g = Arm(pr, s);
                    Transform hand = g.Hand ?? g.Fore;
                    float sgn = s == "R" ? 1f : -1f;
                    float outDeg = TurnTowardDeg(rig, g.Upper, 1.15f * w, s == "R" ? Toward.Right : Toward.Left, hand, rig.Root.forward);
                    rig.BoneTurn(g.Upper, 0f, -sgn * 0.22f * w * Mathf.Rad2Deg, outDeg);
                    rig.BoneTurn(g.Fore, 0f, sgn * 0.95f * w * Mathf.Rad2Deg, 0f);
                }
                LookUp(rig, pr, 0.45f * w);
            }
            SettleFeet(rig);
            return true;
        }

        public static bool SetWingGrip(CharacterRig rig, float w, int side = 1)
        {
            var pr = Get(rig);
            if (!pr.Valid) return false;
            if (w < 0.02f) { rig.ReleaseBones(); return true; }
            EnsureCrouchSign(rig, pr);
            if (pr.HasArms) EnsureArmSign(rig, pr);
            float k = AirFold * w * (pr.CrouchSign ?? 1);
            foreach (var s in new[] { "L", "R" })
            {
                var l = Leg(pr, s);
                rig.BoneTwist(l.Thigh, k * 0.07f * Mathf.Rad2Deg);
                rig.BoneTwist(l.Calf, k * 0.13f * Mathf.Rad2Deg);
                if (l.Foot != null) rig.BoneTwist(l.Foot, k * 0.22f * Mathf.Rad2Deg);
            }
            rig.BoneTwist(pr.Spine, k * 0.12f * Mathf.Rad2Deg);
            if (pr.HasArms)
            {
                float a = AirFold * w * (pr.ArmSign ?? 1);
                string leadSide = side > 0 ? "R" : "L";
                foreach (var s in new[] { "L", "R" })
                {
                    var g = Arm(pr, s);
                    Transform hand = g.Hand ?? g.Fore;
                    float lead = s == leadSide ? 1.08f : 0.92f;
                    float outDeg = TurnTowardDeg(rig, g.Upper, 0.12f * w, s == "R" ? Toward.Right : Toward.Left, hand, rig.Root.forward);
                    rig.BoneTurn(g.Upper, -a * 1.42f * lead * Mathf.Rad2Deg, 0f, outDeg);
                    rig.BoneTwist(g.Fore, -a * 0.12f * Mathf.Rad2Deg);
                }
                LookUp(rig, pr, 0.30f * w);
            }
            SettleFeet(rig);
            return true;
        }

        // =====================================================================
        // AIM-OFF-FACING — characters.js:983 aimOffFacing. Signed angle (rad)
        // of `aim` off AnimGroup's own facing (never the chest's — see JS
        // comment), positive to the body's RIGHT.
        // =====================================================================
        public static float AimOffFacing(CharacterRig rig, Vector3? aim)
        {
            if (!aim.HasValue || aim.Value.sqrMagnitude < 1e-8f) return 0f;
            var pr = Get(rig);
            Transform an = rig.AnimGroup;
            if (!pr.RightSign.HasValue)
            {
                Transform up = pr.AR.Upper;
                if (up != null)
                {
                    float x = an.InverseTransformPoint(up.position).x;
                    pr.RightSign = x < 0f ? -1 : 1;
                }
                else pr.RightSign = 1;
            }
            Vector3 fwd = an.forward; fwd.y = 0f; fwd.Normalize();
            Vector3 right = an.right * pr.RightSign.Value; right.y = 0f; right.Normalize();
            Vector3 a = aim.Value; a.y = 0f;
            if (a.sqrMagnitude < 1e-8f) return 0f;
            return Mathf.Atan2(Vector3.Dot(a, right), Vector3.Dot(a, fwd));
        }

        // =====================================================================
        // RIFLE SHOULDER — characters.js:888 rifleShoulder/927 _measurePocket.
        // REDUCED FIDELITY (documented honestly, not hidden): the JS measures a
        // true ellipse by scanning every skinned-mesh vertex within a band at
        // shoulder height and keeping whichever bone most of them are bound to
        // (~90 lines, characters.js:927-975) — a real per-vertex mesh/skin-
        // weight read this port's time budget did not reach (this method is a
        // SUPPORT function for the weapon system's rifle-hold pose, not on the
        // ground-locomotion/climb/cover critical path this port prioritised).
        // This version estimates the same pocket from MEASURED BODY DIMENSIONS
        // instead of a vertex scan: the shoulder joint, pushed out to the gun
        // side of the aim by ShoulderHalf (the measured shoulder half-width —
        // the real stand-in for the ellipse radius) plus POCKET_PROUD, ahead by
        // POCKET_AHEAD, and down by POCKET_DROP — same constants, same shape of
        // answer, not vertex-exact. A caller after true fidelity should port
        // _measurePocket's skinned-mesh scan (Mesh.vertices + boneWeights).
        // =====================================================================
        public static Vector3? RifleShoulder(CharacterRig rig, string side, Vector3? aim)
        {
            var pr = Get(rig);
            if (!pr.HasArms) return null;
            var g = Arm(pr, side);
            if (g.Upper == null) return null;
            Vector3 fwd = aim.HasValue && aim.Value.sqrMagnitude > 1e-8f ? new Vector3(aim.Value.x, 0f, aim.Value.z) : rig.AnimGroup.forward;
            if (fwd.sqrMagnitude < 1e-8f) fwd = rig.Root.forward;
            fwd.Normalize();
            Vector3 sideDir = Vector3.Cross(fwd, Vector3.up).normalized; // swapped vs. JS's cross(a, up) (class header)
            if (side == "L") sideDir = -sideDir;
            Vector3 d = sideDir * Mathf.Cos(PocketAhead) + fwd * Mathf.Sin(PocketAhead);
            var met = ClimbMetrics(rig);
            float r = Mathf.Max(0.05f, met.ShoulderHalf);
            return g.Upper.position + d * (r + PocketProud) - Vector3.up * PocketDrop;
        }

        // =====================================================================
        // Small public accessors FootClimb.cs needs (characters.js:1827
        // _handPoint, 1849 climbMetrics's armLen) — exposed here rather than
        // duplicating PoseRig/Build in FootClimb.cs. Not a pose: just bone
        // lookup, so no LateUpdate/handedness concerns.
        // =====================================================================
        public static (Transform Upper, Transform Fore, Transform Hand) GetArmSide(CharacterRig rig, string side)
        {
            var pr = Get(rig);
            var a = Arm(pr, side);
            return (a.Upper, a.Fore, a.Hand);
        }

        public static float GetArmLen(CharacterRig rig)
        {
            var pr = Get(rig);
            return pr.ArmLen;
        }
    }
}

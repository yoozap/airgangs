using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Port of src/characters.js's `CharacterRig` class (characters.js:450-2845),
    /// scoped per .claude/skills/unity/SKILL.md's "the big win" and this port's
    /// explicit hard boundary: src/foot.js (6,458 lines — climbing, cover, gaits)
    /// is owned by a different worker and is NOT this file.
    ///
    /// ============================== WHAT THIS PORTS ==============================
    /// - Loading a body, stripping a welded-in weapon mesh (characters.js:456-464)
    /// - Height normalisation to CharacterRoster.HeroHeight (characters.js:466-486)
    /// - The procedural run for a body with no run clip — ONLY the elephant here,
    ///   where the JS used it for every rigless/auto-rigged body (locomote,
    ///   characters.js:538-562)
    /// - The whole-body lean/bank spring on top of a real run clip (settleBody/
    ///   swingBody, characters.js:683-805) and the per-joint arm spring (swingArms/
    ///   _armJoint, characters.js:1049-1180) — both apply during ORDINARY running,
    ///   not a foot.js-owned gameplay state, so they are this file's to carry
    /// - The bone-pose STACK PRIMITIVES (_boneTwist/_boneSet/_boneTurn/_boneAxis/
    ///   _releaseBone(s)/freezePose/restoreFrozen/qSame, characters.js:403-2685) —
    ///   exposed here as public methods because foot.js's future port needs them
    ///   to build its own poses (crouch/climb/roll/slide/seated/cover) on top of
    ///   this rig, exactly as it calls into CharacterRig today
    /// - Rig introspection foot.js and Footsteps.cs both need: LegPair() (the two
    ///   foot bones, characters.js:843-857, src/footsteps.js:97) and the arm chain
    ///   (RollRig, characters.js:2362-2391) that SwingArms/SwingBody use internally
    ///
    /// ============================ WHAT REPLACES ITSELF ============================
    /// For banana/eggplant/toad (22-bone UE rig, 18/18 humanoid landmarks,
    /// measured) Unity's HUMANOID AVATAR directly replaces:
    ///   - _rollRig/_crouchRig/_legRig/_limbTip/_torsoRef (characters.js:843-875,
    ///     2362-2502, ~200 lines of regex bone-name sniffing with per-body
    ///     calibration-by-trying-both) → one `Animator.GetBoneTransform(HumanBodyBones.X)`
    ///     call each, no sniffing, no calibration, no cache map
    ///   - the 39-body loader / per-model yaw table / auto-rig path entirely
    ///     (characters.js:140-195) → glTFast/FBX import + the Avatar it produces
    /// The elephant (Generic, 0/18 landmarks, measured) gets none of that for
    /// free, so it keeps a small hand-rolled name lookup — see GenericBones below.
    ///
    /// ============================= WHAT IS NOT HERE ==============================
    /// Left for src/foot.js's own port, which is the sole caller of every one of
    /// these in the JS (characters.js:1182-2358, 2360-2503 minus RollRig/LegPair,
    /// 1991-2266, 581-666, 876-1031): setCrouch/setSeated/_poseSeated, setRollTuck/
    /// _poseTuck, setSlidePose/_poseSlide, setAirPose, the whole climb pose family
    /// (setHangPose/setPullPose/setMantlePose/plantFoot/settleFeet/_legRig's climb
    /// variant/climbMetrics/_handPoint), handsOnEdge/setHandHook/setDivePose/
    /// setWindBrace/setFlyPose/setWingGrip, setGrip/_handRig (finger curling),
    /// rifleShoulder/_measurePocket, aimOffFacing, shoulderFront. All of them are
    /// built ONLY out of the primitives this file exposes (BoneTwist/BoneSet/
    /// BoneTurn/ReleaseBones/FreezePose, RollRig/LegPair) — foot.js's port can
    /// call straight into a CharacterRig instance exactly as the JS does.
    /// Several of these (the two-bone foot/hand solves in plantFoot/handsOnEdge,
    /// the finger curl in setGrip) are also reasonable candidates to reimplement
    /// with Unity's own Animation Rigging package (Two Bone IK Constraint, Multi-
    /// Aim Constraint) instead of hand-rolled quaternion solves — that package is
    /// already in this project's manifest (docs/UNITY_PORT.md Phase 0) for
    /// exactly this reason, but wiring it is foot.js's port's call to make.
    ///
    /// ================================ HANDEDNESS =================================
    /// Every rotation here is built with UnityEngine.Quaternion.AngleAxis about one
    /// of THIS RIG'S OWN local axes (Root.right/up/forward) — not a cross product
    /// standing in for one. That is handedness-SAFE as-is: Unity's own AngleAxis
    /// already encodes the left-handed rotation generator, so no term needs
    /// negating (see FlightModel.cs's header for the general rule; this is the
    /// "component-identical" case it describes, not the cross-product case).
    ///
    /// ONE place is genuinely the cross-product case: `_turnToward` (characters.js:
    /// 2429-2464) hand-derives which SIGN of angle moves a limb's far end toward a
    /// wanted direction, using `move = axis × r` — a stand-in for "a positive
    /// AngleAxis rotation moves a point by angle·(axis × r)", which is a
    /// right-hand-rule fact and flips in a left-handed engine. Ported here as
    /// `Vector3.Cross(r, axis)` (operands swapped), exactly the FlightModel.cs
    /// lift-direction pattern. This is the only cross product in the ported scope.
    ///
    /// ================================ LATEUPDATE =================================
    /// Every pose-writing method here (Locomote/SettleBody/SwingBody/SwingArms/
    /// BoneTwist/BoneSet/BoneTurn/ReleaseBones) MUST be called from the owning
    /// MonoBehaviour's LateUpdate — Unity evaluates the Animator between Update
    /// and LateUpdate, so LateUpdate is "after the mixer", exactly the ordering
    /// characters.js depends on (qSame below is the other half of that contract:
    /// never rebase a pose off its own output).
    /// </summary>
    public sealed class CharacterRig
    {
        public readonly string Id;
        public readonly Transform Root;       // the mover's own transform — position/rotation driven externally
        public readonly Transform ActGroup;   // foot.js's future action-pose layer (climb/jump) — empty slot here
        public readonly Transform AnimGroup;  // procedural locomotion layer: hop/lean/bank written here
        public readonly Transform Model;      // the scaled, ground-offset instantiated body
        public readonly Animator Anim;        // null only if the GLB truly has no skeleton
        public readonly bool IsHumanoid;       // Anim.isHuman — true for banana/eggplant/toad
        public readonly bool Procedural;       // true only for the elephant (characters.js:514 "this.procedural")

        public float BodyHeight { get; private set; } // measured, metres — should settle at HeroHeight
        public float Aspect { get; private set; }      // characters.js:493-494 — drives locomote's wobble width

        // ---- procedural run state (characters.js:515-516) ----
        float _phase;
        float _locoPitchDeg, _locoBankDeg; // AnimGroup's own lean/bank while Procedural owns it outright

        // ---- whole-body lean/bank spring state (characters.js:712 `_bd`) ----
        float _bp, _bpv, _br, _brv;
        float _asymMax = 0.25f;
        float _gaitArmMax, _gaitLegMax;

        // ---- the bone-pose stack (characters.js:412-430, 2403-2685) ----
        struct PosedRec { public Quaternion Base; public Quaternion Exit; public bool Has; }
        readonly Dictionary<Transform, PosedRec> _posed = new Dictionary<Transform, PosedRec>();
        Dictionary<Transform, (Quaternion q, Vector3 p)> _frozen;

        // ---- arm spring state (characters.js:1082 `_sw` — `_swW` below is the
        // shared `sw.w`, this is the per-bone entry in `sw.j`) ----
        sealed class ArmSpring { public Quaternion Q; public Quaternion Avg; public Vector3 V; }
        readonly Dictionary<Transform, ArmSpring> _arm = new Dictionary<Transform, ArmSpring>();
        float _swW;
        int? _armSign; // characters.js `arms.sign` — found once by _calibrateArms

        public CharacterRig(GameObject modelInstance, string bodyId)
        {
            Id = bodyId;
            Procedural = CharacterRoster.IsProceduralBody(bodyId);

            var rootGo = new GameObject($"rig_{bodyId}");
            Root = rootGo.transform;
            var actGo = new GameObject("act");
            actGo.transform.SetParent(Root, false);
            ActGroup = actGo.transform;
            var animGo = new GameObject("anim");
            animGo.transform.SetParent(ActGroup, false);
            AnimGroup = animGo.transform;

            StripWeldedWeapons(modelInstance.transform);

            Model = modelInstance.transform;
            Model.SetParent(AnimGroup, false);
            Model.localPosition = Vector3.zero;
            Model.localRotation = Quaternion.identity;
            Model.localScale = Vector3.one;

            Anim = modelInstance.GetComponentInChildren<Animator>();
            IsHumanoid = Anim != null && Anim.avatar != null && Anim.avatar.isHuman;
            if (Anim != null) Anim.applyRootMotion = false; // characters.js never lets the clip move the root — physics does

            NormalizeHeight();
        }

        // characters.js:456-464 — strip a weapon welded into the model; pickups
        // put one in the hand now (src/pickups.js), a baked-in one is just wrong.
        static void StripWeldedWeapons(Transform root)
        {
            var toRemove = new List<Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root) continue;
                var n = t.name.ToLowerInvariant();
                if (n.StartsWith("pistol") || n.StartsWith("gun") || n.StartsWith("weapon")
                    || n.StartsWith("rifle") || n.StartsWith("sword") || n.StartsWith("knife"))
                {
                    toRemove.Add(t);
                }
            }
            foreach (var t in toRemove) if (t != null) Object.Destroy(t.gameObject);
        }

        // characters.js:469-486 measureBox()+normalize: scale the model so its
        // measured height is HeroHeight, then offset it so the soles sit at
        // AnimGroup's own y=0. Renderer.bounds is a WORLD AABB Unity keeps live
        // for skinned meshes (close enough for a one-time standing measurement —
        // the JS's own warning about FBX-sourced skins' RAW bind bounds is about
        // geometry.boundingBox, which this does not use).
        void NormalizeHeight()
        {
            var b = MeasureWorldBounds(Model);
            float h = b.size.y;
            if (h > 0.001f) Model.localScale = Vector3.one * (CharacterRoster.HeroHeight / h);
            var b2 = MeasureWorldBounds(Model);
            // b2 is in world space; convert the vertical offset into AnimGroup's local Y
            float worldMinY = b2.min.y;
            float animWorldY = AnimGroup.position.y;
            Model.position += new Vector3(0f, -(worldMinY - animWorldY), 0f);
            var b3 = MeasureWorldBounds(Model);
            BodyHeight = b3.size.y > 0.01f ? b3.size.y : CharacterRoster.HeroHeight;
            float widest = Mathf.Max(b3.size.x, b3.size.z);
            Aspect = Mathf.Clamp(widest / Mathf.Max(0.01f, BodyHeight), 0.25f, 2.5f);
        }

        static Bounds MeasureWorldBounds(Transform root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Bounds(root.position, Vector3.zero);
            var b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            return b;
        }

        // =====================================================================
        // PROCEDURAL RUN — characters.js:538-562 locomote(). Ported as-is; it is
        // the elephant's ONLY way of moving (Idle is its sole shipped clip).
        // Writes AnimGroup's local transform directly, so a body running this
        // must not also receive SettleBody/SwingBody (both guard on Procedural).
        // =====================================================================
        public void Locomote(float dt, float speed, float runSpeed = 6.5f)
        {
            if (!Procedural) return;
            float s = Mathf.Clamp(Mathf.Abs(speed) / runSpeed, 0f, 1.6f);
            var a = AnimGroup;
            if (s < 0.02f)
            {
                // ease back to a clean standing pose — characters.js:544-550
                float k = Mathf.Min(1f, 8f * dt);
                var pos = a.localPosition; pos.y += (0f - pos.y) * k; a.localPosition = pos;
                _locoPitchDeg += (0f - _locoPitchDeg) * k;
                _locoBankDeg += (0f - _locoBankDeg) * k;
                a.localRotation = Quaternion.Euler(_locoPitchDeg, 0f, _locoBankDeg);
                var sc = a.localScale;
                sc.y += (1f - sc.y) * k;
                sc.x = sc.z = 2f - sc.y;
                a.localScale = sc;
                return;
            }
            _phase += dt * (7.5f + 4.5f * s);
            float hop = Mathf.Abs(Mathf.Sin(_phase));
            float land = Mathf.Max(0f, -Mathf.Cos(_phase * 2f));
            float wide = 1f / (0.8f + Aspect * 0.6f);
            var p2 = a.localPosition;
            p2.y = hop * 0.16f * CharacterRoster.HeroHeight * s * wide;
            a.localPosition = p2;
            // HANDEDNESS (cosmetic only — see class header): JS leans about local X
            // with "(faces -Z)"; Unity's forward is +Z, so the sign below is chosen
            // so a positive lean tips the top of the body toward +Z/forward. Flip
            // if the verifier sees it leaning backward.
            float leanSign = Mathf.Sign(speed == 0f ? 1f : speed);
            _locoPitchDeg = -(leanSign * -0.24f * s) / (1f + Aspect) * Mathf.Rad2Deg;
            _locoBankDeg = Mathf.Sin(_phase * 0.5f) * 0.08f * s * wide * Mathf.Rad2Deg;
            a.localRotation = Quaternion.Euler(_locoPitchDeg, 0f, _locoBankDeg);
            float sq = 1f - 0.11f * s * land;
            a.localScale = new Vector3(2f - sq, sq, 2f - sq);
        }

        // =====================================================================
        // WALK DIRECTION — replaces characters.js:2812-2834 walkClip()/walkRate()
        // (the discrete 8-way compass switch) with Unity's own 2D blend-tree
        // parameters. See CharacterRoster.WalkSet for the measured per-clip
        // speeds used as the blend tree's child-motion positions, and its header
        // for why the sign convention is "ours", not Unity's axis-letter one.
        ///
        /// `travelAngleRad`: 0 = straight ahead, +90° = the body's own LEFT —
        /// identical convention to walkClip()'s `a`. `paceFrac` 0..1+ of
        /// CharacterRoster.WalkMaxRate — the blend tree's overall speed lives on
        /// a separate "Speed" parameter (set by whatever drives the Idle/Walk/Run
        /// layer transitions); this only sets the 2D direction.
        // =====================================================================
        public void SetWalkDirection(Animator anim, float travelAngleRad, float paceFrac)
        {
            if (anim == null) return;
            float lateral = Mathf.Sin(travelAngleRad) * Mathf.Clamp01(paceFrac);
            float forward = Mathf.Cos(travelAngleRad) * Mathf.Clamp01(paceFrac);
            anim.SetFloat("MoveX", lateral); // +left, see CharacterRoster.WalkClip
            anim.SetFloat("MoveZ", forward); // +ahead
        }

        // =====================================================================
        // WHOLE-BODY LEAN/BANK SPRING — characters.js:683-805 settleBody/swingBody.
        // Applies to AnimGroup's local rotation ON TOP of whatever the Animator
        // just wrote underneath it (the Model child) — never to Procedural bodies,
        // which already own that transform via Locomote.
        // =====================================================================
        public struct BodyLeanInput
        {
            public float Speed, RunSpeed, Turn, Accel, Stroll, Blend;

            /// <summary>characters.js:776 `opts.crouch` — gates the gentleman's
            /// chest bow off while ducking (foot.js's `this._crouchT`).</summary>
            public float Crouch;

            /// <summary>characters.js:719-722 `opts.lean`/`opts.bank` — rad,
            /// added directly to the lean/bank spring's WANT alongside the run
            /// physics (foot.js's cover stance, FootController.CoverLeanVector).
            /// Zero for plain running — see foot.js:6161-6204 bodyOpts, the only
            /// caller that ever sets these.</summary>
            public float Lean, Bank;

            /// <summary>characters.js:782-801 `opts.chestPitch`/`headYaw`/
            /// `chestYaw` — rad, cover-stance chest/head offsets added on top of
            /// the stride twist (foot.js:6197-6200, FootController.CoverStanceTerms).</summary>
            public float ChestPitch, HeadYaw, ChestYaw;

            /// <summary>characters.js:782-792 `opts.gunTwist`/`gunDip`/`gunPitch`
            /// — rad, the reach-to-the-hip shoulder term (gunTwist/gunDip,
            /// foot.js:6201-6204 via FootWeapons.GunReachEnv) and a shouldered
            /// rifle's recoil rock (gunPitch, foot.js:6204 `g.pistol.rock`).
            /// swingArms does NOT read these three — see characters.js:1041-1043.</summary>
            public float GunTwist, GunDip, GunPitch;
        }

        const float BodyHz = 2.2f, BodyZeta = 0.9f, BodyMax = 0.36f;
        const float BodyLean = 0.2f, BodyAcc = 0.022f, BodyBank = 0.11f, BodyTwist = 0.17f;
        const float SwingDead = 0.3f;
        const float GentChest = 0.38f; // characters.js:315 GENT_CHEST — the gentleman's chest bow

        /// <summary>characters.js:683-698 settleBody — holds whatever lean/bank the
        /// spring already has with nothing new asked of it (used while a foot.js-
        /// owned move, e.g. a climb, owns the body outright).</summary>
        public void SettleBody(float dt)
        {
            float h = Mathf.Min(dt, 1f / 30f);
            StepSpring(ref _bp, ref _bpv, 0f, h);
            StepSpring(ref _br, ref _brv, 0f, h);
            if (!Procedural) ApplyLeanBank();
        }

        /// <summary>characters.js:706-805 swingBody IN FULL, including the
        /// cover/weapon-reach extension terms (opts.lean/bank/chestPitch/
        /// headYaw/chestYaw/gunTwist/gunDip/gunPitch) — foot.js's own
        /// FootController.CoverLeanVector/CoverStanceTerms build those from the
        /// cover state and the weapon-reach envelope and pass them through
        /// BodyLeanInput; a plain running caller just leaves them at 0, which
        /// reproduces the previous "minus cover" behaviour exactly.</summary>
        public void SwingBody(float dt, BodyLeanInput input)
        {
            float h = Mathf.Min(dt, 1f / 30f);
            float s = Mathf.Clamp(Mathf.Abs(input.Speed) / Mathf.Max(0.01f, input.RunSpeed), 0f, 1.6f);
            float gent = Mathf.Clamp01(input.Stroll);
            float blend = Mathf.Clamp01(input.Blend);
            // characters.js:719-722 — the cover stance's own lean/bank is added
            // OUTSIDE the run-physics blend: it is the pose, not the run's physics.
            float wantP = blend * (1f - 0.9f * gent) * (BodyLean * s + BodyAcc * input.Accel) + input.Lean;
            float wantR = blend * (1f - 0.9f * gent) * BodyBank * input.Turn * Mathf.Min(1f, s) + input.Bank;
            StepSpring(ref _bp, ref _bpv, wantP, h);
            StepSpring(ref _br, ref _brv, wantR, h);
            if (!Procedural) ApplyLeanBank();

            if (!IsHumanoid) return; // the torso twist needs arm bones to read a stride from
            var lUp = Anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var rUp = Anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
            if (lUp == null || rUp == null) return;
            var tipL = Anim.GetBoneTransform(HumanBodyBones.LeftHand) ?? lUp;
            var tipR = Anim.GetBoneTransform(HumanBodyBones.RightHand) ?? rUp;
            Vector3 fwd = Root.forward;
            float d = Vector3.Dot(tipL.position, fwd) - Vector3.Dot(tipR.position, fwd);
            _asymMax = Mathf.Max(_asymMax, Mathf.Abs(d));
            float asym = Mathf.Clamp(d / _asymMax, -1f, 1f);
            if (_gaitLegMax > 0.05f && _gaitArmMax < _gaitLegMax * SwingDead)
            {
                asym = Mathf.Clamp(-_gaitLeg / Mathf.Max(0.0001f, _gaitLegMax), -1f, 1f);
            }
            var chest = Anim.GetBoneTransform(HumanBodyBones.Chest) ?? Anim.GetBoneTransform(HumanBodyBones.Spine);
            var head = Anim.GetBoneTransform(HumanBodyBones.Head) ?? Anim.GetBoneTransform(HumanBodyBones.Neck);
            float tw = BodyTwist * asym * Mathf.Min(1f, s / 0.45f) * blend * (1f - gent);
            if (chest != null)
            {
                // characters.js:774-775 — col=1: the chest YAWS about the body's
                // own UP axis (every other TurnToward call in this file's scope
                // uses the default right axis — see TurnToward's `axis` param).
                float yawDeg = Mathf.Abs(tw) > 1e-4f
                    ? TurnToward(chest, Mathf.Abs(tw), asym > 0f ? Toward.Fwd : Toward.Back, lUp, Root.up) : 0f;

                // characters.js:776-777 — THE GENTLEMAN'S CHEST BOW: chest up,
                // chin level, only while strolling and not crouched.
                float pitchDeg = (gent > 0.01f && input.Crouch < 0.05f && head != null)
                    ? TurnToward(chest, GentChest * gent, Toward.Back, head) : 0f;

                // characters.js:782-786 — THE REACH TO THE HIP, measured on the
                // RIGHT arm regardless of which shoulder is doing the reaching
                // (a holster draw rocks the whole torso, not just the gun arm's
                // own side) — same geometric _turnToward rule as the stride twist.
                float gt = input.GunTwist, gd = input.GunDip;
                float yawGDeg = Mathf.Abs(gt) > 1e-4f
                    ? TurnToward(chest, Mathf.Abs(gt), gt > 0f ? Toward.Back : Toward.Fwd, rUp, Root.up) : 0f;
                float rollGDeg = Mathf.Abs(gd) > 1e-4f
                    ? TurnToward(chest, Mathf.Abs(gd), gd > 0f ? Toward.Down : Toward.Up, rUp, Root.forward) : 0f;

                // characters.js:787-792 — a shouldered rifle's recoil rock.
                float gpv = input.GunPitch;
                Transform pitchTip = head ?? rUp;
                float pitchGDeg = (Mathf.Abs(gpv) > 1e-4f && pitchTip != null)
                    ? TurnToward(chest, Mathf.Abs(gpv), gpv > 0f ? Toward.Back : Toward.Fwd, pitchTip) : 0f;

                // characters.js:795 — ...and the stance's own chest offsets
                // (cover breathing/corner-turn), already in radians: converted
                // to degrees ONCE here, at the same point every other term above
                // already is (TurnToward returns degrees; these two do not).
                float chestPitchDeg = input.ChestPitch * Mathf.Rad2Deg;
                float chestYawDeg = input.ChestYaw * Mathf.Rad2Deg;

                BoneTurn(chest, pitchDeg + pitchGDeg + chestPitchDeg, yawDeg + yawGDeg + chestYawDeg, rollGDeg);
                if (head != null && head != chest)
                {
                    // characters.js:800-801 — both terms are dimensionless scales
                    // (HEAD_LEVEL/HEAD_TWIST) applied to an already-converted
                    // degree value; `yaw` here is deliberately the STRIDE twist
                    // alone (yawDeg), not the combined chest total above — the
                    // JS reads its own `yaw` local, not `yaw + yawG`.
                    float headPitchDeg = 0.55f * _bp * Mathf.Rad2Deg;
                    float headYawDeg = -0.5f * yawDeg + input.HeadYaw * Mathf.Rad2Deg;
                    BoneTurn(head, headPitchDeg, headYawDeg, 0f);
                }
            }
        }

        float _gaitLeg; // characters.js `g.leg` — set by GaitAsym

        /// <summary>characters.js:813-838 _gaitAsym — how far ahead the trailing
        /// hand/foot sit of their pair, in metres, with a slow-decaying running
        /// max. Call once a frame before SwingArms/SwingBody if you want the
        /// "dead arms, drive the swing off the legs" fallback to engage.</summary>
        public void GaitAsym(float dt)
        {
            if (!IsHumanoid) return;
            var lUp = Anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var rUp = Anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
            if (lUp == null || rUp == null) return;
            var tipL = Anim.GetBoneTransform(HumanBodyBones.LeftHand) ?? lUp;
            var tipR = Anim.GetBoneTransform(HumanBodyBones.RightHand) ?? rUp;
            Vector3 fwd = Root.forward;
            float arm = Vector3.Dot(tipL.position, fwd) - Vector3.Dot(tipR.position, fwd);
            var legs = LegPair();
            float leg = legs.HasValue ? Vector3.Dot(legs.Value.L.position, fwd) - Vector3.Dot(legs.Value.R.position, fwd) : 0f;
            float keep = 1f - Mathf.Min(0.5f, 0.15f * Mathf.Min(dt, 1f / 30f));
            _gaitArmMax = Mathf.Max(Mathf.Abs(arm), _gaitArmMax * keep);
            _gaitLegMax = Mathf.Max(Mathf.Abs(leg), _gaitLegMax * keep);
            _gaitLeg = leg;
            if (_armLen <= 0f) _armLen = Mathf.Max(0.05f, Vector3.Distance(tipL.position, lUp.position));
        }

        void ApplyLeanBank()
        {
            // HANDEDNESS (cosmetic, see class header): forward is +Z in Unity;
            // pitch about local right is chosen so positive _bp tips the body
            // forward, matching the JS's "-X is forward: the body faces -Z" intent
            // translated to this engine's own forward. Verify visually.
            AnimGroup.localRotation = Quaternion.Euler(-_bp * Mathf.Rad2Deg, 0f, _br * Mathf.Rad2Deg);
        }

        static void StepSpring(ref float x, ref float v, float want, float h)
        {
            float w0 = 2f * Mathf.PI * BodyHz;
            v += (w0 * w0 * (want - x) - 2f * BodyZeta * w0 * v) * h;
            x = Mathf.Clamp(x + v * h, -BodyMax, BodyMax);
        }

        // =====================================================================
        // ARM SPRING — characters.js:1049-1180 swingArms/_armJoint. Humanoid-only
        // (the elephant has no quality-bar requirement for arm physics and no
        // stride to read one off — see class header).
        // =====================================================================
        public struct ArmSwingInput
        {
            public float Speed, RunSpeed, Turn, Accel, Stroll, Blend;
        }

        const float ArmSway = 0.09f, ArmBrake = 0.018f, SwingGain = 0.6f, GentCalm = 0.92f;
        const float SwingMeanBase = 0.45f, ArmSnap = 1.1f, ArmVMax = 26f;
        const float GentBack = 0.75f, GentElbow = 0.85f, GentIn = 0.12f; // characters.js:307-313
        static readonly Dictionary<string, float> ArmHz = new() { { "upper", 3.6f }, { "fore", 3.0f }, { "hand", 2.5f } };
        static readonly Dictionary<string, float> ArmZeta = new() { { "upper", 0.52f }, { "fore", 0.4f }, { "hand", 0.32f } };
        const float ArmFromLeg = 0.8f;
        float _armLen; // characters.js `gait.armLen` — measured once in GaitAsym

        public bool SwingArms(float dt, ArmSwingInput input)
        {
            if (!IsHumanoid) return false;
            var lu = Anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var ru = Anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
            if (lu == null && ru == null) return false;
            float h = Mathf.Min(dt, 1f / 30f);
            if (_armSign == null) _armSign = CalibrateArms();
            int sg = _armSign.Value;
            float s = Mathf.Clamp(Mathf.Abs(input.Speed) / Mathf.Max(0.01f, input.RunSpeed), 0f, 1.6f);
            float gent = Mathf.Clamp01(input.Stroll);
            float want = Mathf.Clamp01(input.Blend) * Mathf.Max(gent, Mathf.Clamp01(s * 2.5f));
            float synth = _gaitLegMax > 0.05f ? Mathf.Clamp01(1f - _gaitArmMax / (_gaitLegMax * SwingDead)) : 0f;
            if (input.Blend <= 0f) _swW = 0f; else _swW += (want - _swW) * Mathf.Min(1f, 8f * dt);
            bool write = _swW > 0.004f;
            float sway = -input.Turn * ArmSway * (0.3f + s);
            float brake = -input.Accel * ArmBrake;
            float amp = SwingGain * Mathf.Clamp01((s - 0.12f) / 0.85f) - GentCalm * gent;
            float mean = SwingMeanBase * (1f + 2.5f * gent);

            ArmJointSide(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                "L", -1, sg, h, write, amp, mean, sway, brake, gent, synth, s);
            ArmJointSide(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                "R", 1, sg, h, write, amp, mean, sway, brake, gent, synth, s);
            return write;
        }

        // characters.js:1105-1137 — one side of swingArms's per-frame loop.
        // `outSign`: abduction mirrors (+1 R / -1 L); `sg`: the calibrated elbow-
        // fold sign, shared by both arms (characters.js `arms.sign`).
        void ArmJointSide(HumanBodyBones upperBone, HumanBodyBones foreBone, HumanBodyBones handBone,
            string side, int outSign, int sg, float h, bool write, float amp, float mean,
            float sway, float brake, float gent, float synth, float s)
        {
            var upper = Anim.GetBoneTransform(upperBone);
            var fore = Anim.GetBoneTransform(foreBone);
            var hand = Anim.GetBoneTransform(handBone);
            if (upper == null) return;
            // THE SWING THE CLIP OWES THIS BODY, if it owes one — characters.js:1119-1124.
            float wantSwing = 0f;
            if (synth > 0.01f && _gaitLegMax > 0.05f && _armLen > 0.01f)
            {
                float leg = side == "L" ? -_gaitLeg : _gaitLeg;
                wantSwing = leg * ArmFromLeg * synth * (1f - gent) * Mathf.Clamp(s / 0.25f, 0f, 1f);
            }
            float swing = Mathf.Abs(wantSwing) > 1e-5f
                ? TurnToward(upper, Mathf.Abs(wantSwing) / _armLen, wantSwing > 0f ? Toward.Fwd : Toward.Back)
                : 0f;
            float back = gent > 0.01f ? TurnToward(upper, GentBack * gent, Toward.Back) : 0f;
            float bend = gent > 0.01f ? TurnToward(fore, GentElbow * gent, Toward.Fold) : 0f;
            ArmJoint(upper, "upper", h, write, amp, mean, -sg * brake + back + swing, sway + outSign * -GentIn * gent);
            if (fore != null) ArmJoint(fore, "fore", h, write, amp * 0.35f, mean, bend, 0f);
            if (hand != null) ArmJoint(hand, "hand", h, write, amp, mean, 0f, sway * 0.5f);
        }

        void ArmJoint(Transform bone, string kind, float h, bool write, float amp, float mean, float flexDeg, float abdDeg)
        {
            var clip = ClipQ(bone);
            if (!_arm.TryGetValue(bone, out var st))
            {
                st = new ArmSpring { Q = clip, Avg = clip, V = Vector3.zero };
                _arm[bone] = st;
            }
            st.Avg = Quaternion.Slerp(st.Avg, clip, Mathf.Min(1f, h / mean));
            Quaternion drive = clip;
            if (amp != 0f)
            {
                Vector3 logv = RotLog(clip, st.Avg);
                drive = RotExp(logv * amp, clip);
            }
            Vector3 err = RotLog(drive, st.Q);
            if (err.magnitude > ArmSnap)
            {
                st.Q = drive;
                st.V = Vector3.zero;
            }
            else
            {
                float w0 = 2f * Mathf.PI * ArmHz[kind];
                st.V += err * (w0 * w0 * h);
                st.V *= Mathf.Max(0f, 1f - 2f * ArmZeta[kind] * w0 * h);
                if (st.V.magnitude > ArmVMax) st.V = st.V.normalized * ArmVMax;
                st.Q = RotExp(st.V * h, st.Q);
            }
            if (write)
            {
                var q = Quaternion.Slerp(clip, st.Q, _swW);
                if (Mathf.Abs(flexDeg) > 1e-5f || Mathf.Abs(abdDeg) > 1e-5f) q = LocalTurn(bone, flexDeg, abdDeg) * q;
                BoneSet(bone, q);
            }
        }

        int CalibrateArms()
        {
            // characters.js:2308-2329 — try both elbow-fold signs, keep whichever
            // brings the hand closer to the hips (a tucked arm's tell).
            var hips = Anim.GetBoneTransform(HumanBodyBones.Hips);
            var lHand = Anim.GetBoneTransform(HumanBodyBones.LeftHand);
            var lUp = Anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var lFore = Anim.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            var rUp = Anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
            var rFore = Anim.GetBoneTransform(HumanBodyBones.RightLowerArm);
            if (hips == null || lHand == null || lUp == null || lFore == null) return 1;
            float Reach(int sign)
            {
                float a = 2.3f * sign; // TUCK_FOLD
                BoneTwist(lUp, -a * 0.4f * Mathf.Rad2Deg);
                BoneTwist(lFore, a * 0.55f * Mathf.Rad2Deg);
                if (rUp != null) BoneTwist(rUp, -a * 0.4f * Mathf.Rad2Deg);
                if (rFore != null) BoneTwist(rFore, a * 0.55f * Mathf.Rad2Deg);
                return Vector3.Distance(lHand.position, hips.position);
            }
            float plus = Reach(1);
            float minus = Reach(-1);
            ReleaseBones();
            return plus <= minus ? 1 : -1;
        }

        // =====================================================================
        // SHOULDER POCKET ENTRY POINT — characters.js:927 _measurePocket, the real
        // per-vertex skinned-mesh ellipse fit (see Character/ShoulderPocket.cs for
        // the full port and why it lives in its own file rather than replacing
        // FootPoses.cs's documented approximation in place). Cached per side, the
        // same "measured once" contract as the JS's `this._pocket[side] === undefined`.
        // =====================================================================
        Dictionary<string, ShoulderPocket.Pocket> _pocketCache;
        public ShoulderPocket.Pocket MeasureShoulderPocket(string side)
        {
            _pocketCache ??= new Dictionary<string, ShoulderPocket.Pocket>();
            if (_pocketCache.TryGetValue(side, out var cached)) return cached;
            var rec = ShoulderPocket.Measure(this, side);
            _pocketCache[side] = rec;
            return rec;
        }

        // =====================================================================
        // RIG INTROSPECTION — characters.js:843-857 _legPair, 2362-2391 _rollRig.
        // Trivial for the 3 humanoid bodies (direct Avatar bone access replaces
        // the whole name-sniffing system); a small hardcoded lookup for the
        // Generic elephant, matching anims.js's own 'toy' bone scheme
        // (anims.js:129-142) since that is the one other rig this file ever sees.
        // =====================================================================
        public (Transform L, Transform R)? LegPair()
        {
            if (IsHumanoid)
            {
                var l = Anim.GetBoneTransform(HumanBodyBones.LeftToes) ?? Anim.GetBoneTransform(HumanBodyBones.LeftFoot);
                var r = Anim.GetBoneTransform(HumanBodyBones.RightToes) ?? Anim.GetBoneTransform(HumanBodyBones.RightFoot);
                if (l != null && r != null) return (l, r);
                return null;
            }
            var gl = GenericBones.Find(Model, "Foot.L");
            var gr = GenericBones.Find(Model, "Foot.R");
            return (gl != null && gr != null) ? (gl, gr) : ((Transform, Transform)?)null;
        }

        // =====================================================================
        // BONE-POSE STACK PRIMITIVES — characters.js:403-430, 2504-2685. Exposed
        // public: foot.js's future port builds every gameplay pose on top of these.
        // =====================================================================

        /// <summary>characters.js:427-430 qSame — compare COMPONENTS with an
        /// epsilon, never angleTo() against a copy of the same quaternion. See
        /// this port's hard rules: angleTo(q, q.clone()) is 2*acos(|q|^2), so a
        /// 1e-13 norm drift from repeated quaternion products already reads as
        /// 1e-6 of angle — exactly the bug that made a planted foot spin forever.</summary>
        public static bool QSame(Quaternion a, Quaternion b)
            => Mathf.Abs(a.x - b.x) < 1e-6f && Mathf.Abs(a.y - b.y) < 1e-6f
            && Mathf.Abs(a.z - b.z) < 1e-6f && Mathf.Abs(a.w - b.w) < 1e-6f;

        /// <summary>characters.js:2504-2512 _clipQ — what the ANIMATOR put on this
        /// bone, recovered even though we may have overwritten it last frame.</summary>
        public Quaternion ClipQ(Transform bone)
        {
            if (_posed.TryGetValue(bone, out var rec) && rec.Has && QSame(rec.Exit, bone.localRotation))
                return rec.Base;
            return bone.localRotation;
        }

        PosedRec GetRec(Transform bone)
        {
            if (!_posed.TryGetValue(bone, out var rec))
            {
                rec = new PosedRec { Base = bone.localRotation, Exit = bone.localRotation, Has = false };
            }
            if (!(rec.Has && QSame(rec.Exit, bone.localRotation))) rec.Base = bone.localRotation;
            return rec;
        }

        /// <summary>characters.js:2403-2418 _boneTwist — rotate about this rig's own
        /// LOCAL RIGHT axis by `angleDeg`, written in the bone's PARENT frame so it
        /// composes correctly with whatever base pose the Animator just wrote.</summary>
        public void BoneTwist(Transform bone, float angleDeg)
        {
            if (bone == null || Mathf.Abs(angleDeg) < 1e-6f) return;
            var rec = GetRec(bone);
            Quaternion d = Quaternion.AngleAxis(angleDeg, Root.right);
            Quaternion parentWorld = bone.parent != null ? bone.parent.rotation : Quaternion.identity;
            Quaternion local = Quaternion.Inverse(parentWorld) * d * parentWorld;
            bone.localRotation = (local * rec.Base).normalized;
            rec.Exit = bone.localRotation;
            rec.Has = true;
            _posed[bone] = rec;
        }

        /// <summary>characters.js:2554-2566 _localTurn — flex about local right,
        /// abduct about local forward, combined into ONE parent-frame delta (two
        /// separate BoneTwist calls on one bone would throw the first away).</summary>
        public Quaternion LocalTurn(Transform bone, float flexDeg, float abdDeg)
        {
            Quaternion d = Quaternion.identity;
            if (Mathf.Abs(abdDeg) > 1e-6f) d = Quaternion.AngleAxis(abdDeg, Root.forward) * d;
            if (Mathf.Abs(flexDeg) > 1e-6f) d = Quaternion.AngleAxis(flexDeg, Root.right) * d;
            Quaternion parentWorld = bone.parent != null ? bone.parent.rotation : Quaternion.identity;
            return Quaternion.Inverse(parentWorld) * d * parentWorld;
        }

        /// <summary>characters.js:2572-2593 _boneTurn — pitch/yaw/roll about this
        /// rig's own three axes in ONE write.</summary>
        public void BoneTurn(Transform bone, float pitchDeg, float yawDeg, float rollDeg)
        {
            if (bone == null || (Mathf.Abs(pitchDeg) < 1e-6f && Mathf.Abs(yawDeg) < 1e-6f && Mathf.Abs(rollDeg) < 1e-6f)) return;
            var rec = GetRec(bone);
            Quaternion d = Quaternion.identity;
            if (Mathf.Abs(yawDeg) > 1e-6f) d = Quaternion.AngleAxis(yawDeg, Root.up) * d;
            if (Mathf.Abs(pitchDeg) > 1e-6f) d = Quaternion.AngleAxis(pitchDeg, Root.right) * d;
            if (Mathf.Abs(rollDeg) > 1e-6f) d = Quaternion.AngleAxis(rollDeg, Root.forward) * d;
            Quaternion parentWorld = bone.parent != null ? bone.parent.rotation : Quaternion.identity;
            Quaternion local = Quaternion.Inverse(parentWorld) * d * parentWorld;
            bone.localRotation = (local * rec.Base).normalized;
            rec.Exit = bone.localRotation;
            rec.Has = true;
            _posed[bone] = rec;
        }

        /// <summary>characters.js:2514-2527 _boneSet — put a bone exactly here,
        /// keeping the pose-stack bookkeeping so ReleaseBones can still hand it
        /// back to the Animator later.</summary>
        public void BoneSet(Transform bone, Quaternion q)
        {
            var rec = GetRec(bone);
            bone.localRotation = q.normalized;
            rec.Exit = bone.localRotation;
            rec.Has = true;
            _posed[bone] = rec;
        }

        /// <summary>characters.js:2614-2619 _releaseBone — hand back ONE bone.</summary>
        public void ReleaseBone(Transform bone)
        {
            if (!_posed.TryGetValue(bone, out var rec) || !rec.Has) return;
            if (QSame(rec.Exit, bone.localRotation)) bone.localRotation = rec.Base;
            rec.Has = false;
            _posed[bone] = rec;
        }

        /// <summary>characters.js:2621-2637 _releaseBones — hand every bone we
        /// posed back to the Animator, but only the ones still holding OUR value.</summary>
        public void ReleaseBones()
        {
            var keys = new List<Transform>(_posed.Keys);
            foreach (var bone in keys)
            {
                var rec = _posed[bone];
                if (rec.Has && QSame(rec.Exit, bone.localRotation)) bone.localRotation = rec.Base;
                rec.Has = false;
                _posed[bone] = rec;
            }
        }

        /// <summary>
        /// characters.js:2664-2687 freezePose/restoreFrozen/thawPose. Needed
        /// whenever the Animator will be held at a fixed frame (a seated pilot's
        /// breathing must not leak into hand-on-stick IK) — see this port's hard
        /// rule: "never rebase a pose off its own output". Every bone under Model
        /// is captured, not just the ones this rig has posed, because the thing
        /// being frozen is the ANIMATOR'S output, which this class does not own.
        /// </summary>
        public int FreezePose()
        {
            _frozen = new Dictionary<Transform, (Quaternion, Vector3)>();
            foreach (var t in Model.GetComponentsInChildren<Transform>(true))
                _frozen[t] = (t.localRotation, t.localPosition);
            return _frozen.Count;
        }

        public bool RestoreFrozen()
        {
            if (_frozen == null || _frozen.Count == 0) return false;
            foreach (var kv in _frozen)
            {
                if (kv.Key == null) continue;
                kv.Key.localRotation = kv.Value.Item1;
                kv.Key.localPosition = kv.Value.Item2;
            }
            return true;
        }

        public void ThawPose() => _frozen?.Clear();

        // ---- the one cross-product in this file's scope: see class header ----
        enum Toward { Fwd, Back, Left, Right, Up, Down, Fold }

        /// <summary>characters.js:2429-2464 _turnToward, cross-product operands
        /// SWAPPED for Unity's left-handedness (class header). Returns a SIGNED
        /// angle in DEGREES to hand to BoneTwist/the arm spring. `axis` is the
        /// JS's `col`-selected world column — right by default (flexion: an arm
        /// swinging fore/aft), Root.up for a yaw (characters.js's col=1 call).</summary>
        float TurnToward(Transform bone, float angleRad, Toward want, Transform tipOverride = null, Vector3? axis = null)
        {
            if (bone == null || Mathf.Abs(angleRad) < 1e-6f) return 0f;
            Transform tip = tipOverride ?? LimbTip(bone);
            if (tip == null) return 0f;
            Vector3 j = bone.position;
            Vector3 r = tip.position - j;
            Vector3 ax = axis ?? Root.right;
            // HANDEDNESS: cross(r, axis), not cross(axis, r) — see class header.
            Vector3 move = Vector3.Cross(r, ax);
            Vector3 d;
            switch (want)
            {
                case Toward.Fwd: d = Root.forward; break;
                case Toward.Back: d = -Root.forward; break;
                case Toward.Left: d = -Root.right; break;
                case Toward.Right: d = Root.right; break;
                case Toward.Up: d = Root.up; break;
                case Toward.Fold:
                    // characters.js:2455-2461 default branch — "in toward the
                    // joint above": the parent joint minus the tip, not a fixed
                    // body axis (an elbow closing is toward the SHOULDER, which
                    // moves with the clip, not toward a constant direction).
                    if (bone.parent == null) return 0f;
                    d = bone.parent.position - tip.position;
                    if (d.sqrMagnitude < 1e-8f) return 0f;
                    d.Normalize();
                    break;
                default: d = -Root.up; break;
            }
            float signedRad = Vector3.Dot(move, d) >= 0f ? angleRad : -angleRad;
            return signedRad * Mathf.Rad2Deg;
        }

        Dictionary<Transform, Transform> _tips;
        Transform LimbTip(Transform bone)
        {
            _tips ??= new Dictionary<Transform, Transform>();
            if (_tips.TryGetValue(bone, out var cached)) return cached;
            Transform tip = null;
            float far = -1f;
            Vector3 basePos = bone.position;
            foreach (Transform o in bone.GetComponentsInChildren<Transform>(true))
            {
                if (o == bone) continue;
                float d = Vector3.Distance(o.position, basePos);
                if (d > far) { far = d; tip = o; }
            }
            _tips[bone] = tip;
            return tip;
        }

        // ---- quaternion log/exp for the arm springs (characters.js:2529-2547) ----
        static Vector3 RotLog(Quaternion a, Quaternion b)
        {
            Quaternion q = a * Quaternion.Inverse(b);
            q.Normalize();
            if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            float sn = Mathf.Sqrt(Mathf.Max(1e-16f, q.x * q.x + q.y * q.y + q.z * q.z));
            float ang = 2f * Mathf.Atan2(sn, Mathf.Min(1f, q.w));
            return new Vector3(q.x, q.y, q.z) * (ang / sn);
        }

        static Quaternion RotExp(Vector3 v, Quaternion q)
        {
            float ang = v.magnitude;
            if (ang < 1e-7f) return q;
            Quaternion d = Quaternion.AngleAxis(ang * Mathf.Rad2Deg, v / ang);
            return (d * q).normalized;
        }

        // =====================================================================
        // GENERIC-RIG BONE LOOKUP — the elephant only. Mirrors anims.js's 'toy'
        // scheme names (anims.js:129-142) since that is the one Generic rig in
        // this project's roster; NOT a general-purpose rig sniffer like
        // characters.js's _rollRig/_crouchRig; see class header on why that
        // whole system is replaced rather than ported for the 3 Humanoid bodies.
        // =====================================================================
        static class GenericBones
        {
            public static Transform Find(Transform root, string exactName)
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (t.name == exactName) return t;
                // exporters sometimes suffix an index ("FootL_9") — fall back to a
                // normalized, non-suffixed match, same trick anims.js:167-174 uses.
                string norm = Normalize(exactName);
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (Normalize(t.name) == norm) return t;
                return null;
            }

            static string Normalize(string n)
            {
                var sb = new System.Text.StringBuilder();
                foreach (char c in n) if (char.IsLetter(c)) sb.Append(char.ToLowerInvariant(c));
                return sb.ToString();
            }
        }
    }
}

using UnityEngine;
using TbilisiFly.Combat;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Port of src/foot.js's Assassin's-Creed climb: reach, hang, pull, mantle,
    /// free-run and vault. Source citations are `// src/foot.js:NNN` throughout;
    /// the bone POSES it drives (setHangPose/setPullPose/setMantlePose/
    /// plantFoot/settleFeet/climbMetrics/handsOnEdge's machinery) live in
    /// FootPoses.cs — this file is the STATE MACHINE and the WORLD GEOMETRY
    /// (where the ledge is, where the hands go, how long each beat takes).
    ///
    /// ============================= THE HOLD RULE =============================
    /// The climb is driven entirely by Tick() being called every frame while
    /// Active is true, reading ordinary HELD input (MoveForward, Jump, LetGo —
    /// see ClimbInput) each frame. Nothing here uses an edge-triggered "key
    /// just went down" test except the initial TryStart call (Space/free-run),
    /// exactly as src/foot.js does (the 'W still down flows straight over it'
    /// comment at foot.js:5264-5266) — the gentleman's-stroll class of bug
    /// (Cmd cannot be held) does not apply here because nothing in this file
    /// needs a held CMD-LIKE key; W/S/Space are ordinary keys a browser does
    /// not steal.
    ///
    /// =========================== WORLD-LOCKED HANDS ===========================
    /// HandsOnLedge solves each arm with Combat.Weapons.ApplyReachPose onto a
    /// FIXED WORLD POINT (c.Grip/c.Push, lerped by `mix`) every frame — never
    /// parented to the body. The body moves under the hands (Tick sets `pos`
    /// directly from the planned path), which is what makes this read as
    /// HOLDING something instead of a mime. See HandsOnLedge below.
    ///
    /// ======================= THE LEAN-SPRING / AK BUG =======================
    /// EndClimb('done') does NOT touch the lean spring (CharacterRig.SwingBody's
    /// _bp/_br) — the caller's LateUpdate must keep calling
    /// CharacterRig.SettleBody(dt) every frame the climb owns the body (Tick
    /// does this internally, mirroring foot.js:5233 `rig.settleBody?.(dt)`),
    /// so the spring is already at (or near) zero by the time the climb ends,
    /// rather than snapping from a railed sprint-lean to zero in one frame
    /// (foot.js:5052-5062's "THE LEAN SPRING IS HANDED BACK SETTLED" note — the
    /// bug that made a slung rifle appear to fall off the hero's back at the
    /// top of a climb). EndClimb ALSO resets the caller's own turn/accel
    /// estimators (ResetMomentumEstimators) for the same reason: `speed` is
    /// zeroed on a successful climb, and an accel estimate taken across that
    /// frame would read a instantaneous deceleration from a sprint to 0 and
    /// throw the torso-lean spring from a railed value to its opposite in one
    /// step — FootController.cs must call this at the same point foot.js does
    /// (immediately after EndClimb, before anything reads _accV/_turnV).
    ///
    /// ================================ HANDEDNESS =================================
    /// Every direction here (`dir`, `right = (-dir.z, 0, dir.x)`) is built once
    /// from the character's own yaw via (-sin, 0, -cos)/( -dz, 0, dx) — the SAME
    /// formula foot.js uses, not a cross product standing in for one, so it is
    /// handedness-safe as-is (both engines' sin/cos/atan2 agree once yaw is
    /// defined the same way — see GeoFrame.YawToUnity for hand-tuned constants;
    /// foot.js's own yaw is runtime-derived, not hand-tuned, so no table entry
    /// is needed here). The one place this file hands off to a cross product
    /// is inside FootPoses/CharacterRig (already audited there).
    /// </summary>
    public sealed class FootClimb
    {
        // ---- src/foot.js:44-108 climb constants (SI units, radians) ----
        const float StepUp = 0.65f;         // also FootController.StepUp — src/foot.js:43
        const float ClimbMax = 2.6f;
        const float ClimbReach = 1.0f;
        const float ClimbOut = 0.22f;
        const float ClimbClear = 1.7f;
        const float LedgeFlat = 0.6f;
        const float HangDropMax = 2.0f;
        const float HangArm = 0.95f;
        const float HangOff = 0.26f;
        const float HangMin = 0.7f;
        const float CatchTime = 0.14f;
        const float CatchHop = 0.30f;
        const float HangReach = 0.22f;
        const float PullRate = 0.75f;
        const float PullMin = 0.50f;
        const float PullMinHang = 0.60f;
        const float PullMomentum = 0.75f;
        const float HangDead = 0.25f;
        const float PullHip = -0.08f;
        const float ChestOver = 0.12f;
        const float LeanTop = 0.30f;
        const float MantleTime = 0.55f;
        const float PushIn = 0.12f;
        const float StandIn = 0.30f;
        const float PlantAt = 0.40f;
        const float PlantReach0 = 0.70f;
        const float PlantInDist = 0.15f;
        const float PlantRelease = 0.95f;
        const float PlantFade = 0.10f;
        const float HandReachFrom = 0.99f;
        const float HandReachTo = 1.06f;
        const float HandOver = 0.07f;
        const float HangSwayX = 0.02f;
        const float HangSwayZ = 0.025f;

        // src/foot.js:109-111 HINT_* — (up, right, fwd) in the body's own axes
        static readonly Vector3 HintHang = new Vector3(0.05f, 0.95f, 0.3f);
        static readonly Vector3 HintPull = new Vector3(-0.35f, 0.75f, -0.55f);
        static readonly Vector3 HintPress = new Vector3(-0.8f, 0.35f, -0.45f);

        static float Ease01(float k) { k = Mathf.Clamp01(k); return k * k * (3f - 2f * k); }
        static float PullLift(float u) => u - 0.12f * Mathf.Sin(2f * Mathf.PI * u);
        static float WrapPi(float a) { return Mathf.Atan2(Mathf.Sin(a), Mathf.Cos(a)); }

        /// <summary>Colliders the climb may grab. Mirrors Footsteps.cs's own
        /// `GroundMask` field convention. Defaults to everything.</summary>
        public LayerMask ClimbMask = ~0;
        /// <summary>2026-11-01: the purchased shooter puts a capsule on rig.Root
        /// and cover markers on their own layers; both must be invisible to the
        /// ledge/room/top probes (ParkourMoves.WorldMask + IgnoreRoot).</summary>
        int Mask => ClimbMask.value == ~0 ? ParkourMoves.WorldMask : ClimbMask.value;
        static bool Own(Transform t) => ParkourMoves.IgnoreRoot != null && t != null && t.IsChildOf(ParkourMoves.IgnoreRoot);
        bool CastDown(Vector3 origin, float dist, out RaycastHit hit)
        {
            var hits = Physics.RaycastAll(origin, Vector3.down, dist, Mask, QueryTriggerInteraction.Ignore);
            hit = default; bool got = false;
            foreach (var h in hits) { if (Own(h.transform)) continue; if (!got || h.distance < hit.distance) { hit = h; got = true; } }
            return got;
        }

        // NoClimbMarker moved OUT of this class to Character/NoClimbMarker.cs,
        // for the same reason PlaneCockpitRig left FootBoarding: a MonoBehaviour
        // nested inside another class has no script asset of its own, so it
        // cannot be serialised into a scene a PLAYER can load.

        public sealed class ClimbState
        {
            public float T;
            public string Phase; // "reach" | "hang" | "pull" | "mantle"
            public Vector3 From, Catch, To, Dir, Right, Grip, Push, Face;
            public bool Hold;            // a true hang vs. a straight-through vault
            public bool StartGrounded;   // src/foot.js:5255 `this.grounded !== false` at the moment of the catch
            public float Span;
            public float Yaw;
            public Vector3 PlantWall, PlantTop, KneeWall, ToeWall, KneeTop, ToeTop;
            public string Lead; public int Side;
            public float High;           // 0..1: how tall a ledge this is (chest-over blend)
            public float ChestY, Lift0;
            public FootPoses.ClimbMet Met;
            public float ReachDur, Hung, PullDur, MantleDur, Plant;
            public bool PlantIn, PlantOff, TopOn;

            // ---- THE PURCHASED CLIP'S OWN MOTION (phase "clip") --------------
            // Set by UseTrack() when the move has a baked root-motion track and
            // this is a straight-through vault/mantle (never a held hang).
            public ParkourRootTrack Track;
            public Vector3 TrkFwd, TrkRight, TrkResid;
            public float TrkKz, TrkKy, TrkDur;
        }

        ClimbState _c;
        public bool Active => _c != null;
        /// <summary>The live climb — Tests/PlayMode/ParkourRun.cs reads the
        /// track and its warp off this to prove the move followed the
        /// purchased clip's own curve and not our old lerp.</summary>
        public ClimbState State => _c;
        public string Phase => _c?.Phase;

        /// <summary>false = a straight-through VAULT (ClimbState.Hold's own
        /// doc), true = a real held hang. FootController reads this (2026-11-01,
        /// parkour pack wiring) to pick a vault-low clip vs. a mantle clip at
        /// the same "pull"/"mantle" phase transition — see FootController's
        /// own climb-clip block next to its `Climb.Tick` call.</summary>
        public bool Hold => _c?.Hold ?? false;

        /// <summary>0..1 "how tall a ledge this is" (ClimbState.High's own
        /// doc) — FootController's mantle-low-vs-mantle-high clip pick.</summary>
        public float High => _c?.High ?? 0f;

        /// <summary>Set by FootController once a real parkour clip is driving
        /// the pull/mantle beat this frame — <see cref="Tick"/> then skips its
        /// own FootPoses.SetPullPose/SetMantlePose call so the clip's bones
        /// are not fought (ActGroup's own BodyPivot lean and the world-locked
        /// HandsOnLedge/PlantFoot/SettleFeet calls are UNCHANGED either way —
        /// see ParkourClipPlayer's class header for why those compose on top
        /// of a clip for free).</summary>
        public bool MantleClipActive;

        /// <summary>Same idea for the "hang" phase's SetHangPose.</summary>
        public bool HangClipActive;
        /// <summary>2026-11-01: a purchased approach clip (up-jump / climb-up
        /// into the grab) owns the reach beat — SetHangPose stands down.</summary>
        public bool ReachClipActive;

        /// <summary>src/foot.js `this._climbLead` — the leg that found the
        /// wall last time (callers alternate it across climbs). Null outside
        /// an active climb; FootController.cs keeps its own copy of the last
        /// value to pass back into the next TryStart.</summary>
        public string Lead => _c?.Lead;

        /// <summary>src/foot.js:4718-4900 the exit-fade state (CLIMB_OUT) —
        /// public so FootController can keep posing it for a fifth of a second
        /// after EndClimb('done') returns, exactly as foot.js's own
        /// `this._climbOut` does outside the `_climb` object's lifetime.</summary>
        public sealed class ClimbOutState { public float T; public string Lead; }
        public ClimbOutState Out;

        float _gripT; // src/foot.js `this._gripT` — eases 0..1 as the hand closes

        /// <summary>Everything Tick needs off the mover that is not this file's
        /// own state, in place of reaching into foot.js's `this`/`game`.</summary>
        public struct ClimbInput
        {
            public float MoveZ;      // signed: <0 held "forward" (W), >0 held "back" (S)
            public bool JumpPressed; // Space, edge-triggered by the caller
            public bool LetGoPressed;// 'C', held is fine (foot.js reads keys.has('KeyC') every frame)
            public bool BoardAfter;  // true forces a pull from a hang regardless of input — plane-boarding handoff; always false outside that system
        }

        // =====================================================================
        // TRY START — src/foot.js:4816-4900 _tryClimb, plus the ledge/face/grip
        // probes (4902-4935, 5362-5433). `dirs` is the one or two world-space
        // directions to look along (facing, and the strafe direction if the
        // hero is moving sideways with a weapon out — foot.js:4818-4822).
        // </summary>
        public bool TryStart(CharacterRig rig, Vector3 pos, float yaw, bool grounded, float gY,
            string lastLead, Vector3[] dirs)
        {
            if (rig == null || Active) return false;
            foreach (var d in dirs)
            {
                if (!FindLedge(pos, grounded ? StepUp : 0.05f, d, out Vector3 ledge)) continue;
                var met = FootPoses.ClimbMetrics(rig);
                float rise = ledge.y - pos.y;
                float h = HangOff + HandOver + met.ShoulderZ;
                float armLenEff = met.ArmLen > 0f ? met.ArmLen : 0.52f;
                float vert = Mathf.Sqrt(Mathf.Max(0.05f, (HangArm * armLenEff) * (HangArm * armLenEff) - h * h));
                float hangDrop = Mathf.Min(HangDropMax, met.ShoulderY + vert);
                float catchY = Mathf.Max(pos.y, ledge.y - hangDrop);
                FacePoint(pos, d, catchY, ledge.y, out Vector3 face, out Vector3 catchP);
                float gYv = gY;
                bool hold = catchY > gYv + HangMin || ledge.y - gYv > ClimbMax * 0.85f;
                Vector3 grip = GripPoint(pos, d, ledge);
                Vector3 corner = grip - d * HandOver;
                Vector3 right = new Vector3(-d.z, 0f, d.x);
                float span = Mathf.Max(0.16f, met.ShoulderHalf + 0.02f);
                Vector3 push = grip + d * Mathf.Min(PushIn, 0.3f * armLenEff);
                push.y = ledge.y + 0.04f;
                Vector3 to = ledge; to.x = corner.x + d.x * StandIn; to.z = corner.z + d.z * StandIn;
                float chestY = Mathf.Max(ledge.y - met.HipY + PullHip, ledge.y + ChestOver - met.ShoulderY);
                float lift0 = Mathf.Max(0f, chestY - catchP.y);
                float high = Mathf.Clamp01((rise - StepUp) / (ClimbMax - StepUp));
                string lead = lastLead == "L" ? "R" : "L";
                int side = lead == "R" ? 1 : -1;
                float hipLand = catchP.y + lift0 * PullLift(PlantAt) + met.HipY;
                float dz = HangOff + met.HipZ - (met.AnkleOff + 0.03f);
                float legLenEff = met.LegLen > 0f ? met.LegLen : 0.75f;
                float dy = Mathf.Sqrt(Mathf.Max((PlantReach0 * legLenEff) * (PlantReach0 * legLenEff) - dz * dz, (0.3f * legLenEff) * (0.3f * legLenEff)));
                Vector3 plantWall = face - d * (met.AnkleOff + 0.03f) + right * (side * (met.HipHalf + 0.03f));
                plantWall.y = hipLand - dy;
                float dyTop = met.AnkleOff + 0.01f - PullHip;
                float fwdMax = Mathf.Sqrt(Mathf.Max(0f, (0.75f * legLenEff) * (0.75f * legLenEff) - dyTop * dyTop)) - (HangOff + met.HipZ);
                Vector3 plantTop = corner + d * Mathf.Clamp(fwdMax, 0.05f, PlantInDist) + right * (side * (met.HipHalf + 0.02f));
                plantTop.y = ledge.y + met.AnkleOff + 0.01f;
                Vector3 kneeWall = (Vector3.up * 0.5f + d * 0.25f + right * (side * 0.8f)).normalized;
                Vector3 toeWall = (Vector3.up * 0.85f + d * 0.5f).normalized;
                Vector3 kneeTop = (Vector3.up + d * 0.4f).normalized;
                Vector3 toeTop = d;

                float hop = catchY - pos.y;
                _c = new ClimbState
                {
                    T = 0f, Phase = "reach", From = pos, Catch = catchP, To = to, Dir = d, Right = right,
                    Yaw = Mathf.Atan2(-d.x, -d.z), Grip = grip, Push = push, Span = span, Face = face,
                    PlantWall = plantWall, PlantTop = plantTop, KneeWall = kneeWall, ToeWall = toeWall,
                    KneeTop = kneeTop, ToeTop = toeTop, Lead = lead, Side = side, Hold = hold, High = high,
                    StartGrounded = grounded,
                    ChestY = chestY, Lift0 = lift0, Met = met,
                    ReachDur = !grounded ? HangReach : CatchTime + CatchHop * hop,
                    Hung = 0f, PullDur = 0f, MantleDur = 0f, Plant = 0f, PlantIn = false, PlantOff = false, TopOn = false,
                };
                return true;
            }
            return false;
        }

        // =====================================================================
        // DRIVE THE MOVE WITH THE PACK'S OWN MOTION (2026-11-06, user: "we are
        // using our parkour asset, u need check demo, and make same for all our
        // personages, i mean physic").
        //
        // Threepeat's demo scenes move the character by ROOT MOTION and nothing
        // else. This port instead lerped the body From→Catch→To over three
        // hand-tuned durations (CatchTime/PullRate/MantleTime) with hand-written
        // easing curves, and played the clip on top as decoration — so the body
        // never travelled the arc the animator authored and the feet never
        // matched the ground.
        //
        // Here the clip is the authority for SHAPE and TIMING, and the measured
        // obstacle is the authority for WHERE IT ENDS: the baked track is warped
        // so its net displacement becomes the real From→To of this particular
        // ledge (the pack ships no motion warping — its own manual, section
        // 1.4.1, says to add exactly this), and the residual term guarantees the
        // hero finishes standing precisely on the measured top rather than
        // wherever a scaled curve happened to land. HandsOnLedge keeps running
        // on top, which is the manual's section 1.4.2 hand IK.
        //
        // WHY THIS IS RIGHT FOR EVERY PERSONAGE: nothing here is a body
        // constant. The track is the animator's motion, From/To are measured
        // from the hero's own metrics and the wall in front of them, so a
        // banana, a potato and a pear all run the same authored arc onto the
        // same real ledge.
        //
        // Held hangs are deliberately NOT track-driven: a hang is interactive
        // (the player decides when to pull, switch sides or let go), so no
        // single clip's timeline owns it.
        public bool UseTrack(ParkourRootTrack t)
        {
            var c = _c;
            if (c == null || c.Hold || t == null || t.Len < 0.05f || t.P == null || t.P.Length < 2) return false;

            Vector3 d = c.To - c.From;
            Vector3 fwd = new Vector3(d.x, 0f, d.z);
            float plan = fwd.magnitude;
            fwd = plan > 1e-3f ? fwd / plan : c.Dir;
            Vector3 right = new Vector3(-fwd.z, 0f, fwd.x);

            // Only the ENGAGEMENT WINDOW is warped: the pack's moves are whole
            // run-ups (mantle-low-frontroll-sprint-to-sprint covers 6.91 m, of
            // which ~1 m is the climb), and squashing all of that onto our
            // From→To crushed the clip to a third of its authored speed. See
            // ParkourRootTrack.U0/U1.
            Vector3 net = t.SpanNet;
            float kz = Mathf.Abs(net.z) > 0.05f ? Mathf.Clamp(plan / net.z, 0.5f, 2.0f) : 1f;
            float ky = Mathf.Abs(net.y) > 0.05f ? Mathf.Clamp(d.y / net.y, 0.5f, 2.0f) : 1f;
            Vector3 raw = fwd * (net.z * kz) + right * (net.x * kz) + Vector3.up * (net.y * ky);

            c.Track = t;
            c.TrkFwd = fwd; c.TrkRight = right;
            c.TrkKz = kz; c.TrkKy = ky;
            c.TrkResid = d - raw;          // zero unless a clamp bit or the span was flat on an axis
            // the clip plays 1:1 with the body over the window — no foot slide;
            // a warped span takes proportionally longer or shorter, so the
            // authored ground speed survives the warp too
            c.TrkDur = t.SpanLen * Mathf.Clamp(Mathf.Sqrt(Mathf.Max(0.05f, kz)), 0.7f, 1.5f);
            c.Phase = "clip"; c.T = 0f;
            c.TopOn = false; c.PlantIn = false; c.PlantOff = false; c.Plant = 0f;
            Debug.Log($"CLIMB_TRACK stem={t.Stem} dur={c.TrkDur:F2} clipLen={t.Len:F2} span=[{t.U0:F2},{t.U1:F2}] kz={kz:F2} ky={ky:F2} rise={d.y:F2} fwd={plan:F2} spanNet=({net.x:F2},{net.y:F2},{net.z:F2}) resid={c.TrkResid.magnitude:F3} exit={t.ExitSpeed:F2}");
            return true;
        }

        /// <summary>Seconds into the clip the track-driven move starts — the
        /// authored run-up is skipped (FootController passes this to
        /// ParkourClipPlayer.Play so the animation and the body agree).</summary>
        public float TrackStartTime => _c != null && _c.Track != null ? _c.Track.U0 * _c.Track.Len : 0f;

        /// <summary>The ground speed the clip is doing as it leaves the
        /// obstacle, warped the same way the motion was — the hero runs out of
        /// a mantle at the pace the animator gave it instead of stopping dead.</summary>
        public float TrackExitSpeed => _c != null && _c.Track != null ? _c.Track.ExitSpeed * _c.TrkKz : 0f;

        /// <summary>Where the track-driven move puts the body at normalised time
        /// u — the warped clip curve, landing exactly on the measured top.</summary>
        Vector3 TrackPos(ClimbState c, float u)
        {
            Vector3 p = c.Track.SampleSpan(u);
            return c.From
                 + c.TrkFwd * (p.z * c.TrkKz)
                 + c.TrkRight * (p.x * c.TrkKz)
                 + Vector3.up * (p.y * c.TrkKy)
                 + c.TrkResid * Mathf.Clamp01(u);
        }

        // src/foot.js:4902-4918 _facePoint
        bool FacePoint(Vector3 from, Vector3 d, float y, float lipY, out Vector3 face, out Vector3 catchP)
        {
            Vector3 origin = new Vector3(from.x, Mathf.Min(y + 1.0f, lipY - 0.1f), from.z);
            if (Physics.Raycast(origin, d, out var hit, ClimbReach + 0.4f, ClimbMask))
                face = hit.point;
            else
                face = from + d * HangOff;
            face.y = y;
            catchP = face - d * HangOff;
            catchP.y = y;
            return true;
        }

        // src/foot.js:4920-4935 _gripPoint — the LIP: a ray just under the top.
        Vector3 GripPoint(Vector3 pos, Vector3 d, Vector3 ledge)
        {
            Vector3 origin = new Vector3(pos.x, ledge.y - 0.25f, pos.z);
            Vector3 p;
            if (Physics.Raycast(origin, d, out var hit, ClimbReach + 0.5f, ClimbMask))
                p = hit.point + d * HandOver;
            else
                p = ledge + d * (-0.35f + HandOver);
            p.y = ledge.y + 0.015f;
            return p;
        }

        // src/foot.js:5362-5409 _findLedge
        bool FindLedge(Vector3 pos, float feetYReach, Vector3 dir, out Vector3 standOn)
        {
            standOn = default;
            float feet = pos.y;
            foreach (float reach in new[] { 0.55f, ClimbReach })
            {
                float px = pos.x + dir.x * reach, pz = pos.z + dir.z * reach;
                Vector3 origin = new Vector3(px, feet + ClimbMax + 0.4f, pz);
                if (!CastDown(origin, ClimbMax + 0.6f, out var down)) continue;
                if (down.collider.GetComponent<NoClimbMarker>() != null) return false; // the first thing under the probe says no — there is no ledge here at all
                float top = down.point.y;
                float rise = top - feet;
                if (rise <= StepUp || rise > ClimbMax) continue;
                if (Mathf.Abs(down.normal.y) < LedgeFlat) continue; // a floor, not a flank
                if (!RoomOver(px, pz, top)) continue;
                float sx = px + dir.x * 0.35f, sz = pz + dir.z * 0.35f;
                if (TopNear(sx, sz, top, out float stand) && RoomOver(sx, sz, stand))
                {
                    standOn = new Vector3(sx, stand + 0.02f, sz);
                    return true;
                }
                standOn = new Vector3(px, top + 0.02f, pz);
                return true;
            }
            return false;
        }

        // src/foot.js:5411-5419 _roomOver
        bool RoomOver(float x, float z, float y)
        {
            foreach (var h in Physics.RaycastAll(new Vector3(x, y + 0.12f, z), Vector3.up, ClimbClear, Mask, QueryTriggerInteraction.Ignore))
                if (!Own(h.transform)) return false;
            return true;
        }

        // src/foot.js:5421-5433 _topNear
        bool TopNear(float x, float z, float y, out float stand)
        {
            stand = 0f;
            var hits = Physics.RaycastAll(new Vector3(x, y + 0.9f, z), Vector3.down, 0.9f + StepUp, Mask, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                if (Own(h.transform)) continue;
                if (Mathf.Abs(h.point.y - y) <= StepUp) { stand = h.point.y; return true; }
            }
            return false;
        }

        // =====================================================================
        // THE PULL BEGINS — src/foot.js:4993-5012 _startPull.
        // =====================================================================
        void StartPull(ClimbState c, Vector3 pos)
        {
            bool dead = c.Hung > HangDead;
            c.PullDur = c.Lift0 > 0.02f
                ? Mathf.Max(dead ? PullMinHang : PullMin, PullRate * c.Lift0) * (dead ? 1f : PullMomentum)
                : 0f;
            c.MantleDur = MantleTime * (0.7f + 0.3f * c.High) * (dead ? 1f : 0.8f);
            c.T = 0f;
            c.From = pos; // src/foot.js:5003 `c.from.copy(this.pos)` — wherever the reach/hang phase left it
            if (c.PullDur > 0f) c.Phase = "pull";
            else { c.Phase = "mantle"; c.TopOn = true; c.PlantIn = true; c.PlantOff = false; c.Plant = 0f; }
        }

        // src/foot.js:4983-4991 _bodyPivot — the ActGroup turned about a point
        // `pivotY` up the body (the hips for the pull's lean, not the soles).
        static void BodyPivot(CharacterRig rig, float tiltX, float tiltZ, float pivotY)
        {
            rig.ActGroup.localRotation = Quaternion.Euler(tiltX * Mathf.Rad2Deg, 0f, tiltZ * Mathf.Rad2Deg);
            Vector3 rotated = rig.ActGroup.localRotation * new Vector3(0f, pivotY, 0f);
            rig.ActGroup.localPosition = new Vector3(0f, pivotY, 0f) - rotated;
        }

        // =====================================================================
        // HANDS ON THE LEDGE — src/foot.js:4937-4981 _handsOnLedge (foot.js's
        // OWN version, not characters.js's generic handsOnEdge — this one mixes
        // an elbow HINT per climb phase, which the generic one does not take).
        // =====================================================================
        void HandsOnLedge(CharacterRig rig, ClimbState c, float w, Vector3 hint, float mix)
        {
            if (!(w > 0.01f)) return;
            float len = c.Met.ArmLen > 0f ? c.Met.ArmLen : 0.55f;
            foreach (var side in new[] { ("R", 1f), ("L", -1f) })
            {
                var (upper, fore, hand) = FootPoses.GetArmSide(rig, side.Item1);
                if (upper == null || fore == null || hand == null) continue;
                Vector3 target = Vector3.Lerp(c.Grip, c.Push, mix) + c.Right * (side.Item2 * c.Span);
                float dist = Vector3.Distance(upper.position, target);
                float wr = w * Mathf.Clamp01((HandReachTo * len - dist) / ((HandReachTo - HandReachFrom) * len));
                if (!(wr > 0.01f)) continue;
                Quaternion clipU = rig.ClipQ(upper), clipF = rig.ClipQ(fore);
                Quaternion preU = upper.localRotation, preF = fore.localRotation;
                var chain = new Weapons.ArmChain { Upper = upper, Fore = fore };
                if (!Weapons.ApplyReachPose(rig.ActGroup, rig.Root, chain, hand, target, null, hint)) continue;
                Quaternion fU = upper.localRotation, fF = fore.localRotation;
                if (wr < 0.999f) { fU = Quaternion.Slerp(clipU, fU, wr); fF = Quaternion.Slerp(clipF, fF, wr); }
                upper.localRotation = preU; rig.BoneSet(upper, fU);
                fore.localRotation = preF; rig.BoneSet(fore, fF);
                // a mitten hand bends over the edge; a hand with fingers curls (SetGrip, below)
                Vector3 hook = (c.Dir * (mix < 0.5f ? 0.55f : 0.95f) + Vector3.up * (mix < 0.5f ? -0.85f : -0.3f)).normalized;
                FootPoses.SetHandHook(rig, side.Item1, hook, wr);
            }
        }

        // =====================================================================
        // ONE FRAME — src/foot.js:5220-5345 _climbFrame. Returns the exit this
        // frame produced, if any ("done"/"letgo"), else null while still active.
        // `pos`/`yaw` are written directly — this is the climb owning the body.
        // =====================================================================
        public string Tick(float dt, CharacterRig rig, ref Vector3 pos, ref float yaw, ClimbInput input)
        {
            var c = _c;
            if (c == null) return null;
            // src/foot.js:5226-5227 `rig.play('Idle'); rig.update(dt);` — THREE's
            // AnimationMixer needs an explicit clip switch + a manual update(dt)
            // tick; Unity's Animator does both on its own every frame (it runs
            // automatically between Update and LateUpdate, which is WHY the
            // "bone writes go in LateUpdate" rule works at all — see class
            // header). Whichever state (Idle/Run/whatever) the Animator
            // Controller happens to be in during a climb does not matter: every
            // bone this move cares about (legs, spine, head, both arms) is
            // overwritten below, after the Animator has run. Selecting a
            // specific Animator state here would need a public API CharacterRig
            // does not expose (its own header: SetWalkDirection replaces the
            // JS's play()/walkClip() with a blend tree driven by parameters,
            // not a named-clip switch) — nothing to call.
            rig.SettleBody(dt);   // keep the lean spring running at nothing — see class header, the AK bug
            yaw += WrapPi(c.Yaw - yaw) * Mathf.Min(1f, 10f * dt);
            rig.Root.localRotation = Quaternion.Euler(0f, yaw * Mathf.Rad2Deg, 0f);
            rig.ActGroup.localPosition = Vector3.zero;
            rig.ActGroup.localRotation = Quaternion.identity;

            System.Action keys = null;
            float handW = 0f;
            Vector3 hint = HintHang;
            float mix = 0f;
            (Vector3 at, Vector3 knee, Vector3 toe)? plant = null;
            string exit = null;

            if (c.Phase == "clip")
            {
                // THE WHOLE MOVE IS ONE CLIP — see UseTrack above. Its own
                // length is the duration, its own curve is the path, and the
                // hands/feet helpers below run exactly as they do for a
                // hand-driven pull+mantle so nothing downstream changes.
                c.T += dt;
                float u = Mathf.Clamp01(c.T / Mathf.Max(0.05f, c.TrkDur));
                pos = TrackPos(c, u);
                BodyPivot(rig, -LeanTop * (1f - Ease01(u / 0.7f)) * (1f - Ease01((u - 0.55f) / 0.45f)), 0f, c.Met.HipY);
                string leadClip = c.Lead;
                keys = MantleClipActive ? (System.Action)null
                     : () => FootPoses.SetMantlePose(rig, 1f, u, leadClip);
                handW = Ease01(u / 0.18f) * (1f - Ease01((u - 0.55f) / 0.3f));
                hint = u < 0.5f ? FootWeapons.HintMix(HintHang, HintPull, Ease01(u / 0.5f)) : HintPress;
                mix = Ease01((u - 0.45f) / 0.35f);
                // the wall foot plants early, the top foot once the hips clear
                if (!c.TopOn)
                {
                    if (u >= PlantAt && !c.PlantOff) c.PlantIn = true;
                    if (u > 0.62f) c.PlantOff = true;
                    if (c.PlantOff && c.Plant < 0.02f && u >= 0.7f) { c.TopOn = true; c.PlantOff = false; c.PlantIn = true; c.Plant = 0f; }
                }
                if (c.TopOn) plant = (c.PlantTop, c.KneeTop, c.ToeTop);
                else if (c.PlantIn) plant = (c.PlantWall, c.KneeWall, c.ToeWall);
                _gripT = 1f;
                if (u >= 1f) { pos = c.To; exit = "done"; }
            }
            else if (c.Phase == "reach")
            {
                c.T += dt / Mathf.Max(0.02f, c.ReachDur);
                float k = Mathf.Min(1f, c.T);
                float e = Ease01(k);
                pos = Vector3.Lerp(c.From, c.Catch, e);
                float hop = c.Catch.y - c.From.y;
                bool jumpedFor = hop > 0.2f && c.StartGrounded; // src/foot.js:5255 `hop>0.2 && this.grounded!==false`
                keys = () =>
                {
                    if (jumpedFor) FootPoses.SetAirPose(rig, 1f, 0.3f + 0.4f * e, 1f);
                    else if (c.Hold && !ReachClipActive) FootPoses.SetHangPose(rig, e, 0f);
                    else FootPoses.SetPullPose(rig, e * 0.6f, 0f, c.Lead);
                };
                handW = e;
                _gripT += (1f - _gripT) * Mathf.Min(1f, 18f * dt);
                if (k >= 1f)
                {
                    if (c.Hold) { c.Phase = "hang"; c.T = 0f; }
                    else StartPull(c, pos);
                }
            }
            else if (c.Phase == "hang")
            {
                c.T += dt;
                c.Hung = c.T;
                pos = c.Catch;
                BodyPivot(rig, HangSwayX * Mathf.Sin(0.9f * c.T + 1f), HangSwayZ * Mathf.Sin(1.3f * c.T), c.Grip.y - c.Catch.y);
                float tCapture = c.T;
                // 2026-11-01 parkour pack: a real ledge-grab-braced-idle clip
                // owns the torso/legs while HangClipActive — HandsOnLedge
                // below still solves the grip onto the fixed world point
                // regardless (ParkourClipPlayer's header: it composes on top
                // of whatever the Animator wrote, clip or not).
                keys = HangClipActive ? (System.Action)null : () => FootPoses.SetHangPose(rig, 1f, tCapture);
                handW = 1f;
                _gripT = 1f;
                if (input.MoveZ < 0f || input.JumpPressed || input.BoardAfter) StartPull(c, pos);
                else if (input.MoveZ > 0f || input.LetGoPressed) exit = "letgo";
            }
            else if (c.Phase == "pull")
            {
                c.T += dt / Mathf.Max(0.0001f, c.PullDur);
                float u = Mathf.Min(1f, c.T);
                pos = new Vector3(c.Catch.x, c.Catch.y + c.Lift0 * PullLift(u), c.Catch.z);
                BodyPivot(rig, -LeanTop * Ease01((u - 0.55f) / 0.45f), 0f, c.Met.HipY);
                string lead = c.Lead;
                // 2026-11-01 parkour pack: MantleClipActive covers BOTH "pull"
                // and "mantle" — a vault-low/mantle-low/mantle-high clip spans
                // the pull-up-and-over as one continuous move, same as the
                // "hang" gate above.
                keys = MantleClipActive ? (System.Action)null : () => FootPoses.SetPullPose(rig, 1f, u, lead);
                handW = 1f;
                hint = FootWeapons.HintMix(HintHang, HintPull, Ease01(u / 0.6f)); // src/foot.js:478-483 hintMix, :5292
                mix = Ease01((u - 0.85f) / 0.15f);
                if (!c.TopOn)
                {
                    if (u >= PlantAt && !c.PlantOff) c.PlantIn = true;
                    if (u > 0.8f) c.PlantOff = true;
                    if (c.PlantOff && c.Plant < 0.02f && u >= 0.9f) { c.TopOn = true; c.PlantOff = false; c.PlantIn = true; c.Plant = 0f; }
                }
                if (c.TopOn) plant = (c.PlantTop, c.KneeTop, c.ToeTop);
                else if (c.PlantIn) plant = (c.PlantWall, c.KneeWall, c.ToeWall);
                _gripT = 1f;
                if (u >= 1f)
                {
                    c.Phase = "mantle"; c.T = 0f; c.From = pos;
                    if (!c.TopOn) { c.TopOn = true; c.PlantOff = false; c.PlantIn = true; c.Plant = 0f; }
                }
            }
            else // mantle
            {
                c.T += dt / Mathf.Max(0.0001f, c.MantleDur);
                float u = Mathf.Min(1f, c.T);
                float ey = 1f - (1f - u) * (1f - u);
                float exz = Ease01((u - 0.12f) / 0.88f);
                pos = new Vector3(
                    c.From.x + (c.To.x - c.From.x) * exz,
                    c.From.y + (c.To.y - c.From.y) * ey,
                    c.From.z + (c.To.z - c.From.z) * exz);
                BodyPivot(rig, -LeanTop * (1f - Ease01(u / 0.7f)), 0f, c.Met.HipY);
                string lead = c.Lead;
                keys = MantleClipActive ? (System.Action)null : () => FootPoses.SetMantlePose(rig, 1f, u, lead);
                handW = 1f - Ease01((u - 0.5f) / 0.3f);
                hint = HintPress;
                mix = 1f;
                if (u > 0.7f) c.PlantOff = true;
                plant = (c.PlantTop, c.KneeTop, c.ToeTop);
                if (u >= 1f) { pos = c.To; exit = "done"; }
            }

            if (c.PlantOff) c.Plant = Mathf.Max(0f, c.Plant - dt / PlantFade);
            else if (c.PlantIn) c.Plant = Mathf.Min(1f, c.Plant + dt / PlantFade);

            keys?.Invoke();
            if (plant.HasValue && c.Plant > 0.001f)
            {
                float reach = FootPoses.PlantFoot(rig, c.Lead, plant.Value.at, plant.Value.knee, plant.Value.toe, c.Plant);
                if (reach >= PlantRelease) c.PlantOff = true;
            }
            HandsOnLedge(rig, c, handW, hint, mix);
            FootPoses.SettleFeet(rig);
            FootPoses.SetGrip(rig, _gripT, null);

            if (exit != null) EndClimb(rig, exit, ref pos);
            return exit;
        }

        // =====================================================================
        // THE ONE EXIT — src/foot.js:5014-5066 _endClimb. See class header for
        // the lean-spring note; FootController.cs must call
        // ResetMomentumEstimators() immediately after this, the same frame.
        // =====================================================================
        public (bool grounded, bool letGoPush) EndClimb(CharacterRig rig, string how, ref Vector3 pos)
        {
            // one line per climb: which way it ended and how far through the
            // move it got — the track-driven phase is supposed to end at u=1,
            // and anything else is somebody cutting the move short
            if (_c != null)
                Debug.Log($"CLIMB_END how={how} phase={_c.Phase} T={_c.T:F3} dur={_c.TrkDur:F3} u={(_c.TrkDur > 0f ? _c.T / _c.TrkDur : 0f):F2} track={(_c.Track != null ? _c.Track.Stem : "none")}");
            var c = _c;
            if (how == "done") Out = new ClimbOutState { T = 0f, Lead = c?.Lead ?? "R" };
            FootPoses.SetHangPose(rig, 0f); // _releaseBones at weight 0 — hands the whole climb back to the clip
            rig.ActGroup.localRotation = Quaternion.identity;
            rig.ActGroup.localPosition = Vector3.zero;
            bool grounded = false, pushOff = false;
            if (how == "done") grounded = true;
            else if (how == "letgo") { if (c != null) pos -= c.Dir * 0.12f; _gripT = 0f; pushOff = true; }
            _c = null;
            MantleClipActive = false; HangClipActive = false; // 2026-11-01 parkour pack: never leak into the next climb
            return (grounded, pushOff);
        }

        /// <summary>Call once a frame while <see cref="Out"/> is non-null and no
        /// climb is Active — foot.js:6091-6096's fade of the mantle's own pose
        /// over CLIMB_OUT once the climb has handed the body back.</summary>
        public void TickClimbOut(float dt, CharacterRig rig)
        {
            if (Out == null) return;
            Out.T += dt;
            float w = 1f - Mathf.Min(1f, Out.T / ClimbOut);
            if (w <= 0.02f) { Out = null; return; }
            FootPoses.SetMantlePose(rig, w, 1f, Out.Lead);
        }

        /// <summary>src/foot.js:3734-3791 _stand's climb-abort path, and
        /// foot.js:4816 _tryClimb's own guard list — call before TryStart to
        /// decide whether a climb is even allowed to begin.</summary>
        public static bool CanStart(bool active, bool crouched, bool inCover, bool flipping)
            => active && !crouched && !inCover && !flipping;
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.Character
{
    /// <summary>
    /// THE MOVE TABLE (2026-11-01, user: "u need take all real movements from
    /// my parkour assets. all!") — which purchased clip plays for which
    /// situation, and the two geometry probes FootController asks before a
    /// free-run move: what is in front of the hero (obstacle height, depth,
    /// the ground beyond) and whether it is a wall.
    ///
    /// Every move is an IN-PLACE clip; the displacement a move needs comes
    /// from <see cref="Move.Arc"/>: a launch (forward speed, up speed, and
    /// for the wall leaps a yaw change) that FootController hands to its own
    /// air integration — closed-loop on the world, the same physics every
    /// jump already uses — instead of trusting clip root motion.
    ///
    /// Speed bands (locomotion loops, unarmed, grounded):
    ///   |v| &lt; 0.15 m/s             parkour-idle                       (parkour pack)
    ///   0.15 … WalkBand (2.6 m/s)   walk_fwd / jog_fwd by speed        (Cover+Shoot: the parkour pack has no walk)
    ///   WalkBand … RunSpeed·1.05    IP-sprint-loop-smooth at a reduced rate (plain W run)
    ///   sprint (Shift)              IP-sprint-loop-smooth at full rate
    ///   strafing (Q/E)              strafe (Cover+Shoot, reversed for the other side)
    ///   crouched                    crouch_idle / crouch_sneak (Cover+Shoot)
    ///   airborne, rising            jump_start (Cover+Shoot) then the parkour midair clips
    /// </summary>
    public static class ParkourMoves
    {
        public sealed class Move
        {
            public string Name;      // short id printed by the tests (PARKOUR_FLOW move=...)
            public string Clip;      // ParkourClipLibrary path
            public bool Loop;
            public float Blend = 0.1f;
            /// <summary>Launch given to FootController when the move starts:
            /// x = forward speed (m/s, along the facing), y = up speed, z = yaw
            /// turn in radians applied at launch (the 180 leap / backflip).</summary>
            public Vector3 Arc;
            public float Lock;       // seconds the body stays planted (landings, wall plant, kipup)
            public string Cat;
            public Move(string name, string clip, string cat, bool loop = false, float blend = 0.1f, Vector3 arc = default, float lockS = 0f)
            { Name = name; Clip = clip; Cat = cat; Loop = loop; Blend = blend; Arc = arc; Lock = lockS; }
        }

        // ------------------------------------------------------------ loops
        public static readonly Move Idle = new Move("parkour-idle", ParkourClipLibrary.ParkourIdle, "loco", loop: true, blend: 0.2f);
        public static readonly Move Walk = new Move("walk", ParkourClipLibrary.LWalkFwd, "loco", loop: true, blend: 0.15f);
        public static readonly Move Jog = new Move("jog", ParkourClipLibrary.LJogFwd, "loco", loop: true, blend: 0.15f);
        public static readonly Move Run = new Move("run", ParkourClipLibrary.SprintLoopSmooth, "loco", loop: true, blend: 0.15f);
        public static readonly Move Sprint = new Move("sprint-loop", ParkourClipLibrary.SprintLoopSmooth, "loco", loop: true, blend: 0.15f);
        public static readonly Move Strafe = new Move("strafe", ParkourClipLibrary.LStrafe, "loco", loop: true, blend: 0.15f);
        public static readonly Move CrouchIdle = new Move("crouch-idle", ParkourClipLibrary.LCrouchIdle, "loco", loop: true, blend: 0.15f);
        public static readonly Move CrouchSneak = new Move("crouch-sneak", ParkourClipLibrary.LCrouchSneak, "loco", loop: true, blend: 0.15f);
        public static readonly Move JumpStart = new Move("jump-start", ParkourClipLibrary.LJumpStart, "air", loop: false, blend: 0.06f);
        public static readonly Move FallLoop = new Move("fall", ParkourClipLibrary.LFall, "air", loop: true, blend: 0.12f);

        // ------------------------------------------------------------ air
        public static readonly Move MidairLow = new Move("midair-lowvel", ParkourClipLibrary.MidairLowvel, "air", loop: true, blend: 0.12f);
        public static readonly Move MidairFall = new Move("midair-fallingdown", ParkourClipLibrary.MidairFallingdown, "air", loop: true, blend: 0.12f);
        public static readonly Move MidairFast = new Move("midair-fallingdown-highfwd", ParkourClipLibrary.MidairFallingdownhighforwardvel, "air", loop: true, blend: 0.12f);
        public static readonly Move DropHigh = new Move("drop-high-reversegainer", ParkourClipLibrary.DropHighReversegainerSprintToFallfwd, "drop", blend: 0.08f);
        public static readonly Move DropHighToSprint = new Move("drop-high-reversegainer-to-sprint", ParkourClipLibrary.DropHighReversegainerSprintToSprint, "drop", blend: 0.08f);
        public static readonly Move DropDive = new Move("dropdive-frontflip", ParkourClipLibrary.DropdiveFrontflipSprintToFallfwd, "drop", blend: 0.08f);

        // ------------------------------------------------------------ landings
        public static readonly Move LandSoft = new Move("landing-hard1-slower", ParkourClipLibrary.LandingHard1SlowerFallfwdToIdle, "landing", blend: 0.05f, lockS: 0.55f);
        public static readonly Move LandHard = new Move("landing-hard1-fast", ParkourClipLibrary.LandingHard1FastFallfwdToIdle, "landing", blend: 0.05f, lockS: 0.75f);
        public static readonly Move LandHands = new Move("landing-hardhands", ParkourClipLibrary.LandingHardhandsFallfwdToSprint, "landing", blend: 0.05f, lockS: 0.35f);
        public static readonly Move LandRoll = new Move("landing-hardfrontroll", ParkourClipLibrary.LandingHardfrontrollFallfwdToSprint, "landing", blend: 0.05f);
        public static readonly Move LyingBack = new Move("lying-back", ParkourClipLibrary.LyingBackHeadup, "knockdown", loop: true, blend: 0.1f);
        public static readonly Move Kipup = new Move("kipup", ParkourClipLibrary.KipupLyingBackHeadupToIdle, "knockdown", blend: 0.08f);
        public static readonly Move KipupBack = new Move("kipup-backwards", ParkourClipLibrary.KipupBackwardsLyingbackToIdle, "knockdown", blend: 0.08f);
        public static readonly Move Stop = new Move("stop-lowplatform", ParkourClipLibrary.StopLowplatformSprintToIdle, "loco", blend: 0.08f, lockS: 0.3f);

        // ------------------------------------------------------------ roll / vault / mantle
        public static readonly Move RollGround = new Move("roll", ParkourClipLibrary.MantleLowFrontrollSprintToSprint, "roll", blend: 0.08f);
        public static readonly Move RollAir = new Move("roll-landing", ParkourClipLibrary.LandingHardfrontrollFallfwdToSprint, "roll", blend: 0.08f);
        public static readonly Move VaultDive = new Move("vault-dive", ParkourClipLibrary.VaultLowDiveRunToRun, "vault");
        public static readonly Move VaultKongShort = new Move("vault-kong-short", ParkourClipLibrary.VaultLowKongshortRunToRun, "vault");
        public static readonly Move VaultKongLong = new Move("vault-kong-long", ParkourClipLibrary.VaultLowKonglongRunToRun, "vault");
        public static readonly Move VaultKongLongSprint = new Move("vault-kong-long-sprint", ParkourClipLibrary.VaultLowKonglongSprintToSprint, "vault");
        public static readonly Move VaultShoulderRoll = new Move("vault-shoulderroll", ParkourClipLibrary.VaultLowShoulderrollSprintToSprint, "vault");
        public static readonly Move VaultShoulderRollIdle = new Move("vault-shoulderroll-idle", ParkourClipLibrary.VaultLowShoulderrollIdleToIdle, "vault");
        public static readonly Move VaultTwoHandLow = new Move("vault-twohand-leftarm", ParkourClipLibrary.VaultLowTwohandLeftarmRunToRun, "vault");
        public static readonly Move VaultTwoHandLowSprint = new Move("vault-twohand-leftarm-sprint", ParkourClipLibrary.VaultLowTwohandLeftarmSprintToSprint, "vault");
        public static readonly Move VaultTwoHandLowIdle = new Move("vault-twohand-leftarm-idle", ParkourClipLibrary.VaultLowTwohandLeftarmIdleToIdle, "vault");
        public static readonly Move VaultHigh = new Move("vault-high-twohand", ParkourClipLibrary.VaultHighTwohandRunToRun, "vault");
        public static readonly Move VaultHighSprint = new Move("vault-high-twohand-sprint", ParkourClipLibrary.VaultHighTwohandSprintToSprint, "vault");
        public static readonly Move VaultHighToFall = new Move("vault-high-twohand-to-fall", ParkourClipLibrary.VaultHighTwohandRunToFallfwd, "vault");
        public static readonly Move VaultHigh3m = new Move("vault-high-3m-to-fall", ParkourClipLibrary.VaultHigh3mTwohandRunToFallfwd, "vault");
        public static readonly Move VaultTrip = new Move("vault-kong-trip-wipeout", ParkourClipLibrary.VaultLowKongTripWipeoutSprintToLyingBackHeadup, "vault", blend: 0.08f);
        public static readonly Move MantleLowRoll = new Move("mantle-low-frontroll", ParkourClipLibrary.MantleLowFrontrollSprintToSprint, "mantle");
        public static readonly Move MantleLowKongShort = new Move("mantle-low-kong-short", ParkourClipLibrary.MantleLowOntoKongshortRunToRun, "mantle");
        public static readonly Move MantleLowKongLong = new Move("mantle-low-kong-long", ParkourClipLibrary.MantleLowKonglongRunToRun, "mantle");
        public static readonly Move MantleHighToIdle = new Move("mantle-high-climbup-to-idle", ParkourClipLibrary.MantleHighClimbupRunToIdle, "mantle");
        public static readonly Move MantleHighToRun = new Move("mantle-high-climbup-to-run", ParkourClipLibrary.MantleHighClimbupRunToRun, "mantle");
        public static readonly Move MantleHighSprint = new Move("mantle-high-climbup-sprint", ParkourClipLibrary.MantleHighClimbupSprintToSprint, "mantle");
        public static readonly Move MantleHigh3m = new Move("mantle-high-3m", ParkourClipLibrary.MantleHigh3mClimbupRunToRun, "mantle");

        // ------------------------------------------------------------ ledge
        public static readonly Move LedgeStart = new Move("ledge-grab-start", ParkourClipLibrary.LedgeGrabBracedStart, "ledge", blend: 0.08f);
        public static readonly Move LedgeIdle = new Move("ledge-grab-idle", ParkourClipLibrary.LedgeGrabBracedIdle, "ledge", loop: true, blend: 0.15f);
        public static readonly Move LedgeClimbUp = new Move("climbup-to-ledgegrab", ParkourClipLibrary.ClimbupToLedgegrabSprintToLedgeGrabBraced, "ledge", blend: 0.08f);
        public static readonly Move LedgeUpJump = new Move("up-jump-to-ledge-grab", ParkourClipLibrary.UpJumpToLedgeGrabBraced, "ledge", blend: 0.08f);
        public static readonly Move LedgeJump = new Move("ledge-grab-jump", ParkourClipLibrary.LedgeGrabBracedJump, "ledge", blend: 0.08f);
        public static readonly Move LedgeSwitch = new Move("ledge-grab-switchsides", ParkourClipLibrary.LedgeGrabBracedSwitchsides, "ledge", blend: 0.08f);
        public static readonly Move LedgeDrop = new Move("ledge-grab-drop", ParkourClipLibrary.LedgeGrabBracedDrop, "ledge", blend: 0.06f);
        public static readonly Move LedgeDrop180 = new Move("ledge-grab-drop180", ParkourClipLibrary.LedgeGrabBracedDrop180, "ledge", blend: 0.06f, arc: new Vector3(0f, 0f, Mathf.PI));
        public static readonly Move LedgeVault180Idle = new Move("vault-low-over-180-to-ledge", ParkourClipLibrary.VaultLowOver180LedgeGrabBracedIdleToLedgeGrabBraced, "ledge", blend: 0.08f);
        public static readonly Move LedgeVault180Run = new Move("vault-low-over-180-to-ledge-run", ParkourClipLibrary.VaultLowOver180LedgeGrabBracedRunToLedgeGrabBraced, "ledge", blend: 0.08f);

        // ------------------------------------------------------------ walls
        public static readonly Move WallPlantFront = new Move("wall-plant-front", ParkourClipLibrary.SprintToWallPlantFront, "wall", blend: 0.08f, lockS: 0.45f);
        public static readonly Move WallPlantRight = new Move("wall-plant-right", ParkourClipLibrary.SprintToWallPlantRight, "wall", blend: 0.08f, lockS: 0.45f);
        public static readonly Move WallPlantAirFront = new Move("midair-to-wall-plant-front", ParkourClipLibrary.MidairLowvelToWallPlantFront, "wall", blend: 0.08f, lockS: 0.45f);
        public static readonly Move WallPlantAirLeanBack = new Move("midair-to-wall-plant-leaningback", ParkourClipLibrary.MidairLowvelToWallPlantFrontLeaningback, "wall", blend: 0.08f, lockS: 0.45f);
        public static readonly Move WallPlantAirRight = new Move("midair-to-wall-plant-right", ParkourClipLibrary.MidairLowvelToWallPlantRight, "wall", blend: 0.08f, lockS: 0.45f);
        public static readonly Move WallPlantAirLeft = new Move("midair-to-wall-plant-left", ParkourClipLibrary.MidairLowvelMirrorToWallPlantLeft, "wall", blend: 0.08f, lockS: 0.45f);
        public static readonly Move WallUpJump = new Move("wall-up-jump", ParkourClipLibrary.WallPlantFrontToUpJump, "wall", blend: 0.06f, arc: new Vector3(0.6f, 7.2f, 0f));
        public static readonly Move WallStepUp = new Move("wall-step-up", ParkourClipLibrary.WallPlantFrontStepUp, "wall", blend: 0.06f, arc: new Vector3(1.4f, 6.2f, 0f));
        public static readonly Move Wall180Leap = new Move("wall-180-leap", ParkourClipLibrary.WallPlantFrontTo180leap, "wall", blend: 0.06f, arc: new Vector3(4.2f, 5.0f, Mathf.PI));
        public static readonly Move WallBackflip = new Move("wall-backflip", ParkourClipLibrary.WallPlantFrontLeaningbackBackflip, "wall", blend: 0.06f, arc: new Vector3(2.4f, 5.8f, Mathf.PI));
        public static readonly Move WallDrop = new Move("wall-drop", ParkourClipLibrary.WallPlantFrontDrop, "wall", blend: 0.06f, arc: new Vector3(-0.8f, 1.2f, 0f));
        public static readonly Move WallOutwardLeapR = new Move("wall-outward-leap-right", ParkourClipLibrary.WallPlantRightToOutwardLeap, "wall", blend: 0.06f, arc: new Vector3(3.5f, 4.6f, Mathf.PI * 0.5f));
        public static readonly Move WallOutwardLeapL = new Move("wall-outward-leap-left", ParkourClipLibrary.WallPlantLeftToOutwardLeapMirror, "wall", blend: 0.06f, arc: new Vector3(3.5f, 4.6f, -Mathf.PI * 0.5f));
        public static readonly Move WallForwardLeap = new Move("wall-forward-leap", ParkourClipLibrary.WallPlantRightToForwardLeap, "wall", blend: 0.06f, arc: new Vector3(4.5f, 4.2f, -Mathf.PI * 0.5f));
        public static readonly Move WallForwardLeapHigh = new Move("wall-forward-leap-high", ParkourClipLibrary.WallPlantRightToForwardLeapHigh, "wall", blend: 0.06f, arc: new Vector3(3.6f, 6.4f, -Mathf.PI * 0.5f));
        public static readonly Move WallGrabHighLedge = new Move("wall-jump-reach-grab-high-ledge", ParkourClipLibrary.WallPlantRightToForwardJumpReachAndGrabHighLedge, "wall", blend: 0.06f, arc: new Vector3(1.2f, 7.4f, 0f));
        public static readonly Move TicTac = new Move("tictac-reverse-vault", ParkourClipLibrary.TictacToReverseVaultSprintToFallfwd, "wall", blend: 0.06f, arc: new Vector3(4.0f, 4.8f, 0f));

        // ------------------------------------------------------------ bands / thresholds
        public const float WalkBand = 2.6f;        // m/s — below: Cover+Shoot walk/jog, above: the parkour sprint loop

        /// <summary>m/s where the jog loop hands over to the sprint loop — the
        /// GEOMETRIC MEAN of the two clips' own measured speeds, so whichever
        /// is playing is always near rate 1. See PickLoco for why that matters
        /// more than it sounds.</summary>
        public static float JogBand => Mathf.Sqrt(JogNominal * SprintNominal);

        // ---- THE LOOPS' REAL GROUND SPEEDS -----------------------------------
        // A loop plays at `speed / nominal` so the feet match the ground. These
        // were EYEBALLED and all three were wrong — the bake of the clips' own
        // root motion (2026-11-06, Editor/BakeParkourRootMotion.cs) measured:
        //
        //     walk_fwd            1.541 m / 0.990 s = 1.56 m/s   (was 1.25)
        //     jog_fwd             2.467 m / 0.667 s = 3.70 m/s   (was 2.30)
        //     sprint-loop-smooth  3.837 m / 0.633 s = 6.06 m/s   (was 8.40)
        //     strafe              1.658 m / 0.733 s = 2.26 m/s   (was 2.00)
        //     crouch_sneak        1.312 m / 1.467 s = 0.89 m/s   (was 1.60)
        //
        // so every personage skated: the sprint loop was told it covers 8.4 m/s
        // and played at 0.77x while the body really moved 6.5. `Nominal` reads
        // the number back off the baked track so it can never drift from the
        // asset again; the literals below are only the fallback for a missing
        // bake (and are now the measured values anyway).
        public static float WalkNominal => Nominal(ParkourClipLibrary.LWalkFwd, 1.56f);
        public static float JogNominal => Nominal(ParkourClipLibrary.LJogFwd, 3.70f);
        public static float SprintNominal => Nominal(ParkourClipLibrary.SprintLoopSmooth, 6.06f);
        public static float StrafeNominal => Nominal(ParkourClipLibrary.LStrafe, 2.26f);
        public static float SneakNominal => Nominal(ParkourClipLibrary.LCrouchSneak, 0.89f);

        static readonly Dictionary<string, float> _nominal = new Dictionary<string, float>();

        /// <summary>Ground speed the loop was authored at (m/s): the clip's own
        /// planar root travel over its length, or `fallback` with no bake.</summary>
        public static float Nominal(string clipPath, float fallback)
        {
            if (_nominal.TryGetValue(clipPath, out float hit)) return hit;
            var t = ParkourRootTracks.Get(clipPath);
            float v = (t != null && t.Len > 1e-3f && t.Path > 0.05f) ? t.Path / t.Len : fallback;
            _nominal[clipPath] = v;
            return v;
        }

        /// <summary>Forget the cache — a probe that re-bakes mid-run needs this.</summary>
        public static void ResetNominalCache() { _nominal.Clear(); }
        public const float VaultMin = 0.35f, VaultMax = 1.15f;   // obstacle height band for a vault
        public const float MantleMax = 2.7f;        // above this it is a wall
        public const float WallMinHeight = 2.2f;
        public const float FallSoft = 1.6f, FallHard = 3.2f, FallKnockdown = 6.0f;
        public const float DropHighMin = 3.5f;

        // ------------------------------------------------------------ probes
        public struct Obstacle
        {
            public bool Hit;          // something in reach ahead
            public float Dist;        // horizontal distance to its face
            public float Height;      // its top above the hero's feet (= MantleMax+ when no top found: a wall)
            public float Depth;       // how far the top runs before it drops (≥ 4 = long)
            public float Beyond;      // ground height beyond relative to the feet (negative = a drop on the far side)
            public Vector3 Point, Normal;
            public string HitName; public int HitLayer;
            public bool Wall => Height >= MantleMax;
            public float Angle;       // degrees between the approach and the wall normal (0 = head-on)
        }

        /// <summary>The hero's own colliders (the purchased shooter's capsule on
        /// rig.Root) are skipped by this root; the pack's cover-marker layers
        /// ("Cover", "Cover Invisible") are left out of the mask so a marker in
        /// front of a wall is not mistaken for a waist-high obstacle.</summary>
        public static Transform IgnoreRoot;
        static int _worldMask = -1;
        public static int WorldMask
        {
            get
            {
                if (_worldMask != -1) return _worldMask;
                int m = ~0;
                foreach (var n in new[] { "Cover", "Cover Invisible", "Ignore Raycast", "UI" })
                {
                    int l = LayerMask.NameToLayer(n);
                    if (l >= 0) m &= ~(1 << l);
                }
                _worldMask = m;
                return m;
            }
        }
        static bool Own(Transform t) => IgnoreRoot != null && t != null && t.IsChildOf(IgnoreRoot);

        static bool Cast(Vector3 origin, Vector3 dir, float maxDist, int mask, out RaycastHit hit)
        {
            var hits = Physics.RaycastAll(origin, dir, maxDist, mask, QueryTriggerInteraction.Ignore);
            hit = default; bool got = false;
            foreach (var h in hits)
            {
                if (Own(h.transform)) continue;
                if (!got || h.distance < hit.distance) { hit = h; got = true; }
            }
            return got;
        }

        /// <summary>What stands in front of the feet: a ladder of forward rays
        /// (0.25 … 2.4 m up) finds the face, a downward ray behind the face
        /// finds the top, and a march across the top finds its depth and the
        /// ground beyond.</summary>
        public static Obstacle Probe(Vector3 feet, Vector3 dir, float reach = 2.4f, int mask = 0)
        {
            if (mask == 0) mask = WorldMask;
            var o = new Obstacle();
            dir.y = 0f; if (dir.sqrMagnitude < 1e-6f) return o; dir.Normalize();
            float[] hs = { 0.25f, 0.55f, 0.9f, 1.3f, 1.7f, 2.1f, 2.4f };
            RaycastHit best = default; bool got = false;
            foreach (float h in hs)
            {
                if (Cast(feet + Vector3.up * h, dir, reach, mask, out var hit))
                {
                    if (!got || hit.distance < best.distance) { best = hit; got = true; }
                }
            }
            if (!got) return o;
            o.Hit = true; o.Dist = best.distance; o.Point = best.point; o.Normal = best.normal;
            o.HitName = best.collider != null ? best.collider.name : "?"; o.HitLayer = best.collider != null ? best.collider.gameObject.layer : -1;
            o.Angle = Vector3.Angle(-new Vector3(best.normal.x, 0f, best.normal.z), dir);
            // the top: cast down from well above, just behind the face
            Vector3 over = new Vector3(best.point.x, feet.y + MantleMax + 1.5f, best.point.z) + dir * 0.25f;
            if (Cast(over, Vector3.down, MantleMax + 1.5f - 0.02f, mask, out var top))
                o.Height = top.point.y - feet.y;
            else
                o.Height = MantleMax + 1f; // nothing within reach above: a wall
            // depth: march along the top until the surface drops away
            o.Depth = 4f; o.Beyond = 0f;
            if (o.Height < MantleMax)
            {
                float topY = feet.y + o.Height;
                for (float d = 0.5f; d <= 4.0f; d += 0.35f)
                {
                    Vector3 p = new Vector3(best.point.x, topY + 0.4f, best.point.z) + dir * d;
                    if (Cast(p, Vector3.down, 8f, mask, out var g))
                    {
                        if (g.point.y < topY - 0.45f) { o.Depth = d - 0.35f; o.Beyond = g.point.y - feet.y; break; }
                    }
                    else { o.Depth = d - 0.35f; o.Beyond = -8f; break; }
                }
            }
            return o;
        }

        /// <summary>Is there a wall at `reach` ahead with no top within MantleMax?</summary>
        public static bool WallAhead(Vector3 feet, Vector3 dir, float reach, out Obstacle o, int mask = 0)
        {
            o = Probe(feet, dir, reach, mask);
            return o.Hit && o.Wall;
        }

        // ------------------------------------------------------------ the table
        /// <summary>A straight-through vault over a low obstacle.</summary>
        public static Move PickVault(Obstacle o, float speed, bool sprint)
        {
            bool fast = sprint || speed > 6.5f;
            bool slow = speed < 2.0f;
            Move hand;
            if (o.Beyond < -1.5f) hand = o.Height > 0.9f ? VaultHigh3m : VaultHighToFall;   // nothing to land on behind it
            else if (o.Height > 0.9f) hand = fast ? VaultHighSprint : VaultHigh;
            else if (o.Depth > 1.6f) hand = fast ? VaultKongLongSprint : VaultKongLong;
            else if (slow) hand = o.Height < 0.6f ? VaultTwoHandLowIdle : VaultShoulderRollIdle;
            else if (o.Height < 0.55f) hand = fast ? VaultDive : VaultKongShort;
            else if (o.Height < 0.8f) hand = fast ? VaultTwoHandLowSprint : VaultTwoHandLow;
            else hand = fast ? VaultShoulderRoll : VaultKongShort;
            // nothing to land on behind it: that choice is about the WORLD, not
            // the clip's shape — leave it to the hand rule
            if (o.Beyond < -1.5f) return hand;
            return PickByGeometry(VaultCands, o.Height, o.Depth + 1.5f, fast, hand);
        }

        // ---- PICKING THE MOVE BY THE PACK'S OWN GEOMETRY ---------------------
        // (2026-11-06, user: "make same for all our personages, i mean physic")
        //
        // The thresholds above are hand-written guesses at which clip suits
        // which wall. The bake knows the answer exactly: every clip's authored
        // rise and forward carry (ParkourRootTrack.SpanNet). So score the
        // candidates against the obstacle actually in front of the hero and
        // take the closest — a 1.8 m wall gets the clip that lifts 1.6 m, not
        // whichever one a threshold happened to name.
        //
        // The forward term only PENALISES overshoot: FootClimb lands the hero
        // just past the lip, so a clip that carries 4.8 m of front-roll
        // (mantle-low-frontroll-sprint-to-sprint) has to be squashed to a
        // quarter of its authored speed to fit — which is exactly the foot
        // sliding this whole change exists to remove. Under-carrying is free:
        // the hero runs the rest out normally at the clip's own exit speed.
        //
        // With no bake (the asset missing in a stripped build) every candidate
        // scores nothing and the hand-written choice stands — never worse.
        static readonly Move[] VaultCands = {
            VaultHigh, VaultHighSprint, VaultHigh3m, VaultHighToFall,
            VaultKongLong, VaultKongLongSprint, VaultKongShort, VaultDive,
            VaultShoulderRoll, VaultShoulderRollIdle,
            VaultTwoHandLow, VaultTwoHandLowSprint, VaultTwoHandLowIdle,
        };
        static readonly Move[] MantleCands = {
            MantleHigh3m, MantleHighSprint, MantleHighToRun, MantleHighToIdle,
            MantleLowRoll, MantleLowKongLong, MantleLowKongShort,
        };

        /// <summary>The candidate whose authored motion best fits this
        /// obstacle. `rise` is what the body must gain (a vault's hop height, a
        /// mantle's ledge); `room` is how far forward the move may carry before
        /// it is overshooting.</summary>
        static Move PickByGeometry(Move[] cands, float rise, float room, bool fast, Move fallback)
        {
            Move best = null; float bestScore = float.PositiveInfinity;
            foreach (var m in cands)
            {
                var t = ParkourRootTracks.Get(m.Clip);
                if (t == null) continue;
                float clipRise = Mathf.Max(Mathf.Abs(t.SpanNet.y), t.Peak);
                float carry = Mathf.Abs(t.SpanNet.z);
                float score = 2.0f * Mathf.Abs(clipRise - rise)
                            + 0.30f * Mathf.Max(0f, carry - Mathf.Max(1.2f, room));
                // flavour: a sprint-ending clip under a walk reads wrong, and
                // an idle-ending one kills a run
                bool sprinty = m.Clip.Contains("sprint-to-sprint") || m.Clip.Contains("run-to-run") || m.Clip.Contains("to-sprint");
                bool idly = m.Clip.Contains("to-idle") || m.Clip.Contains("idle-to-idle");
                if (fast && idly) score += 0.35f;
                if (!fast && sprinty) score += 0.20f;
                if (score < bestScore) { bestScore = score; best = m; }
            }
            return best ?? fallback;
        }

        /// <summary>Up onto a platform you keep standing on.</summary>
        public static Move PickMantle(float height, float speed, bool sprint, bool keepRunning)
        {
            Move hand;
            if (height > 2.3f) hand = MantleHigh3m;
            else if (height > 1.25f) hand = sprint ? MantleHighSprint : (keepRunning ? MantleHighToRun : MantleHighToIdle);
            else if (sprint && speed > 5f) hand = MantleLowRoll;
            else hand = height > 0.8f ? MantleLowKongLong : MantleLowKongShort;
            // a mantle ends standing on the ledge, so there is no room to carry
            // — see PickByGeometry's header
            return PickByGeometry(MantleCands, height, 1.2f, sprint || speed > 5f, hand);
        }

        /// <summary>The plant into a wall: from a sprint or from the air, head-on or at an angle.</summary>
        public static Move PickWallPlant(Obstacle o, bool airborne, float lateralSign)
        {
            bool angled = o.Angle > 28f;
            if (airborne)
            {
                if (angled) return lateralSign >= 0f ? WallPlantAirRight : WallPlantAirLeft;
                return WallPlantAirFront;
            }
            return angled ? WallPlantRight : WallPlantFront;
        }

        /// <summary>The follow-up chosen by the key held while planted. `key`:
        /// "space" | "w" | "s" | "x" | "a" | "d" | null (nothing: drop off).</summary>
        public static Move PickWallFollowUp(string key, bool angledPlant, bool ledgeAbove)
        {
            switch (key)
            {
                case "space": return ledgeAbove ? WallGrabHighLedge : (angledPlant ? WallForwardLeapHigh : WallUpJump);
                case "w": return angledPlant ? WallForwardLeap : WallStepUp;
                case "s": return Wall180Leap;
                case "x": return WallBackflip;
                case "a": return WallOutwardLeapL;
                case "d": return WallOutwardLeapR;
                case "z": return TicTac;
                default: return WallDrop;
            }
        }

        /// <summary>The loop for the air: rising = the Cover+Shoot jump start
        /// (handled by the caller), then the parkour midair clips by speed.</summary>
        public static Move PickMidair(float vy, float speed)
        {
            if (vy < -7f) return speed > 5f ? MidairFast : MidairFall;
            if (vy < -3f) return MidairFall;
            return MidairLow;
        }

        /// <summary>The landing by fall height; null below FallSoft (the
        /// ordinary landing dip handles it).</summary>
        public static Move PickLanding(float drop, bool sprint, bool keepRunning)
        {
            if (drop >= FallKnockdown) return LyingBack;
            if (drop < FallSoft) return null;
            if (drop < FallHard) return LandSoft;
            return keepRunning ? LandHands : LandHard;
        }

        /// <summary>The grounded locomotion loop for a speed band. `strafe`
        /// is the signed lateral intent (0 = none).</summary>
        public static Move PickLoco(float speed, bool sprint, bool crouched, float strafe, out float rate)
        {
            float v = Mathf.Abs(speed);
            rate = 1f;
            if (crouched)
            {
                if (v < 0.15f) return CrouchIdle;
                rate = Mathf.Clamp(v / SneakNominal, 0.5f, 1.6f); return CrouchSneak;
            }
            if (v < 0.15f) return Idle;
            if (Mathf.Abs(strafe) > 0.01f) { rate = Mathf.Clamp(v / StrafeNominal, 0.5f, 1.8f) * Mathf.Sign(strafe); return Strafe; }
            // THE BANDS FOLLOW THE CLIPS, NOT THE OTHER WAY ROUND (user,
            // 2026-11-06: "when i stop and start running i see jumping
            // millisecond blink").
            //
            // A jog and a sprint both have a FLIGHT PHASE — a moment with both
            // feet off the ground. Play such a clip at half rate and that
            // moment stretches into a visible hang: the hero reads as jumping.
            // The old bands handed 2.6–6.5 m/s to the sprint loop, which is
            // authored for 6.06 m/s, so the whole acceleration out of a stop
            // (and the whole deceleration into one) played the sprint at
            // 0.43–0.78 — exactly the window the user sees it in.
            //
            // Switching at the geometric mean of two clips' measured speeds
            // keeps whichever is playing between ~0.7 and ~1.3 of its authored
            // rate, where no flight phase is stretched. A walk has no flight
            // phase, so it is the only loop allowed far from rate 1.
            if (v < WalkBand) { rate = Mathf.Clamp(v / WalkNominal, 0.5f, 1.6f); return Walk; }
            if (v < JogBand) { rate = Mathf.Clamp(v / JogNominal, 0.7f, 1.35f); return Jog; }
            rate = Mathf.Clamp(v / SprintNominal, 0.7f, 1.7f);
            return sprint ? Sprint : Run;
        }
    }
}

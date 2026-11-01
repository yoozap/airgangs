using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.Character
{
    /// <summary>
    /// The 17-clip subset of Threepeat's "Parkour Animation Set" this port
    /// imported (2026-11-01, user: "use from parkour set" — see FootController/
    /// FootClimb for the wiring). Every path below is an IN-PLACE ("IP-") twin:
    /// the clip's own root motion is zero, so <see cref="ParkourClipPlayer"/>
    /// can drive it on <see cref="CharacterRig"/>'s Animator while
    /// FootController/FootClimb keep moving the body through the world exactly
    /// as they already do (roll's own spin+travel, the climb's reach/pull/
    /// mantle path curve) — "the JS timing is the spec for WHEN, the clip is
    /// HOW the bones move" (this task's own brief).
    ///
    /// Each FBX carries its own .meta (copied verbatim from the pack) with
    /// animationType=3/avatarSetup=1, so it imports Humanoid regardless of
    /// which veggie Avatar ends up playing it — Unity retargets a Humanoid
    /// clip onto ANY Humanoid avatar through the shared muscle space; the
    /// clip's own avatar (built from ITS OWN skeleton) is never used at
    /// runtime here.
    ///
    /// NOT imported from the pack: the demo scene, any controller, the
    /// pack's own character/script assets, and every NON-"IP-" (root-motion)
    /// twin — this project's acrobatics already own the root, closed-loop on
    /// the world (foot.js's tuned physics), so a root-motion clip would fight
    /// FootController's own Pos/Yaw integration.
    /// </summary>
    public static class ParkourClipLibrary
    {
        const string Root = "Assets/Resources/Art/Anims/Parkour";

        public const string KipupLyingBack = Root + "/base/IP-kipup-lying-back-headup-to-idle.fbx";
        public const string MidairFalling = Root + "/base/IP-midair-fallingdown.fbx";

        public const string DropHigh = Root + "/drop/IP-drop-high-reversegainer-sprint-to-fallfwd.fbx";

        public const string LandingFrontRoll = Root + "/landing/IP-landing-hardfrontroll-fallfwd-to-sprint.fbx";
        public const string LandingHard1Fast = Root + "/landing/IP-landing-hard1-fast-fallfwd-to-idle.fbx";
        public const string LandingHard1Slower = Root + "/landing/IP-landing-hard1-slower-fallfwd-to-idle.fbx";

        public const string LedgeGrabBracedStart = Root + "/ledge/IP-ledge-grab-braced-start.fbx";
        public const string LedgeGrabBracedIdle = Root + "/ledge/IP-ledge-grab-braced-idle.fbx";
        public const string LedgeClimbUp = Root + "/ledge/IP-climbup-to-ledgegrab-sprint-to-ledge-grab-braced.fbx";
        public const string LedgeGrabBracedDrop = Root + "/ledge/IP-ledge-grab-braced-drop.fbx";

        public const string MantleLowFrontRoll = Root + "/mantle/low/IP-mantle-low-frontroll-sprint-to-sprint.fbx";
        public const string MantleLowClimbup = Root + "/mantle/low/IP-mantle-low-onto-kongshort-run-to-run.fbx";
        public const string MantleHighClimbup = Root + "/mantle/high/IP-mantle-high-climbup-run-to-run.fbx";

        public const string VaultLowDive = Root + "/vault/low/IP-vault-low-dive-run-to-run.fbx";
        public const string VaultLowKongShort = Root + "/vault/low/IP-vault-low-kongshort-run-to-run.fbx";
        public const string VaultLowKongLong = Root + "/vault/low/IP-vault-low-konglong-run-to-run.fbx";

        public const string VaultHighTwoHand = Root + "/vault/high/IP-vault-high-twohand-run-to-run.fbx";

        /// <summary>Every path above, for a probe/importer sweep.</summary>
        public static readonly string[] All =
        {
            KipupLyingBack, MidairFalling, DropHigh,
            LandingFrontRoll, LandingHard1Fast, LandingHard1Slower,
            LedgeGrabBracedStart, LedgeGrabBracedIdle, LedgeClimbUp, LedgeGrabBracedDrop,
            MantleLowFrontRoll, MantleLowClimbup, MantleHighClimbup,
            VaultLowDive, VaultLowKongShort, VaultLowKongLong,
            VaultHighTwoHand,
        };

        static readonly Dictionary<string, AnimationClip> _cache = new Dictionary<string, AnimationClip>();

        /// <summary>The one real AnimationClip baked into an imported parkour
        /// FBX (each file carries exactly one take, named identically to the
        /// file — confirmed off the pack's own .meta `takeName`). Null if the
        /// asset is missing; callers are already null-safe throughout this
        /// port's pose code.</summary>
        public static AnimationClip Get(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            if (_cache.TryGetValue(assetPath, out var hit) && hit != null) return hit;
            var clips = ArtAssets.LoadAllAt<AnimationClip>(assetPath);
            AnimationClip clip = null;
            foreach (var c in clips) { clip = c; break; } // exactly one real clip per file
            _cache[assetPath] = clip;
            return clip;
        }
    }
}

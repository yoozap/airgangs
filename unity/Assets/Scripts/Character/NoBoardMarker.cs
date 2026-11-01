using UnityEngine;

namespace TbilisiFly.Character
{
    // ONE MonoBehaviour PER FILE, NAMED AFTER IT (.claude/skills/unity/SKILL.md):
    // moved out of Character/FootBoarding.cs, which is named after a different class.
    // Unity cannot bind a script ASSET to a MonoBehaviour whose file is not
    // named after it, so an AddComponent'd component serialises with NO guid the
    // moment it is saved into a scene a player loads (that is what corrupted
    // level0). Behaviour unchanged — it was NESTED inside FootBoarding, so it is un-nested here and
    // referenced as plain `NoBoardMarker`.
    /// <summary>A collider under this plane's hull means "no boarding
    /// here" (foot.js:730 `w.userData.noBoard` — src/crane.js's held
    /// airframe, with no wing root to step onto).</summary>
    public sealed class NoBoardMarker : MonoBehaviour { }
}

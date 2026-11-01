using UnityEngine;

namespace TbilisiFly.Character
{
    // ONE MonoBehaviour PER FILE, NAMED AFTER IT (.claude/skills/unity/SKILL.md):
    // moved out of Character/FootClimb.cs, which is named after a different class.
    // Unity cannot bind a script ASSET to a MonoBehaviour whose file is not
    // named after it, so an AddComponent'd component serialises with NO guid the
    // moment it is saved into a scene a player loads (that is what corrupted
    // level0). Behaviour unchanged — it was NESTED inside FootClimb, so it is un-nested here and
    // referenced as plain `NoClimbMarker`.
    /// <summary>A collider with this component refuses the climb outright
    /// (src/foot.js:4383 `down[0].object.userData.noClimb` — a shooting
    /// range's firing counter, foot.js's own example).</summary>
    public sealed class NoClimbMarker : MonoBehaviour { }
}

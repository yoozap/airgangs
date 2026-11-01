using UnityEngine;

namespace TbilisiFly.Combat
{
    // ONE MonoBehaviour PER FILE, NAMED AFTER IT (.claude/skills/unity/SKILL.md):
    // moved out of Combat/PlaneDamage.cs, which is named after a different class.
    // Unity cannot bind a script ASSET to a MonoBehaviour whose file is not
    // named after it, so an AddComponent'd component serialises with NO guid the
    // moment it is saved into a scene a player loads (that is what corrupted
    // level0). Behaviour unchanged.
    /// <summary>Marker a propeller hub's pivot Transform carries (src/propeller.js tags the
    /// pivot with userData.isPropeller). A hit on anything under this pivot gets sparks and
    /// damage but NO mark — src/planedamage.js:147 `spins()`: a hole stamped on a spinning
    /// blade hangs in the air in front of the nose once the blade has turned past it.</summary>
    public sealed class SpinningPartTag : MonoBehaviour { }
}

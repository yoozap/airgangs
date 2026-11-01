using UnityEngine;

namespace TbilisiFly.Combat
{
    // ONE MonoBehaviour PER FILE, NAMED AFTER IT (.claude/skills/unity/SKILL.md):
    // moved out of Combat/Guns.cs, which is named after a different class.
    // Unity cannot bind a script ASSET to a MonoBehaviour whose file is not
    // named after it, so an AddComponent'd component serialises with NO guid the
    // moment it is saved into a scene a player loads (that is what corrupted
    // level0). Behaviour unchanged.
    /// <summary>Marker for a plane's REAL skin collider (as opposed to its climb-shell
    /// box) — the Unity equivalent of the JS hit.planeRec tag src/guns.js reads to pick
    /// PLANE_DAMAGE over DAMAGE. Attached by whatever Flight port builds the airframe
    /// colliders; Combat only ever checks for its presence.</summary>
    public sealed class PlaneSurfaceTag : MonoBehaviour { }
}

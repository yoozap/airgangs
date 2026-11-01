using UnityEngine;

namespace TbilisiFly.Combat
{
    // ONE MonoBehaviour PER FILE, NAMED AFTER IT (.claude/skills/unity/SKILL.md):
    // moved out of Combat/RangeBootstrap.cs, which is named after a different class.
    // Unity cannot bind a script ASSET to a MonoBehaviour whose file is not
    // named after it, so an AddComponent'd component serialises with NO guid the
    // moment it is saved into a scene a player loads (that is what corrupted
    // level0). Behaviour unchanged.
    /// <summary>Marks a paper sheet's own collider so a hit-router can find it
    /// without reaching into RangeLanes/PaperTargets' private dictionaries —
    /// same convention as Combat/PlaneSurfaceTag.cs. Added at runtime by
    /// Combat/RangeBootstrap.cs, which is where it used to be declared.</summary>
    public sealed class PaperCardTag : MonoBehaviour { public Transform Pivot; }
}

using UnityEngine;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// Marker for a plane's climbable box-shell collider (planedamage.js
    /// territory — Combat only needs to know NOT to treat it as "real"
    /// geometry during proxy refine).
    ///
    /// ITS OWN FILE, NAMED AFTER IT, BECAUSE A PLAYER BUILD DEPENDS ON IT.
    /// This lived inside Combat/Ballistics.cs, and Unity cannot bind a script
    /// ASSET to a MonoBehaviour whose file is not named after it: the component
    /// on `plane_shell` in Assets/Scenes/Game.unity serialised as
    /// `m_Script: {fileID: 1307156209}` with NO GUID and fell back to
    /// `m_EditorClassIdentifier: Assembly-CSharp::TbilisiFly.Combat.PlaneShellTag`.
    /// m_EditorClassIdentifier is EDITOR-ONLY metadata. A built player cannot
    /// resolve it, reads the component's managed fields against nothing, and
    /// dies in scene load with
    ///   "Data/level0 is corrupted! / [Position out of bounds!]"
    /// out of CachedReader::OutOfBoundsError — no error names the component, and
    /// the whole scene (the entire game) fails to load. That was one of the two
    /// serialised offenders; Character/PlaneCockpitRig.cs is the other.
    /// </summary>
    public sealed class PlaneShellTag : MonoBehaviour { }
}

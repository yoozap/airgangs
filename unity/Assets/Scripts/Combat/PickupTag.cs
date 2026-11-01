using UnityEngine;
using TbilisiFly.Data;

namespace TbilisiFly.Combat
{
    // ONE MonoBehaviour PER FILE, NAMED AFTER IT (.claude/skills/unity/SKILL.md):
    // moved out of Combat/Pickups.cs, which is named after a different class.
    // Unity cannot bind a script ASSET to a MonoBehaviour whose file is not
    // named after it, so an AddComponent'd component serialises with NO guid the
    // moment it is saved into a scene a player loads (that is what corrupted
    // level0). Behaviour unchanged.
    /// <summary>src/pickups.js's `userData.pickup` tag, as a component instead
    /// of a duck-typed field — attach to any loot GameObject. Exactly one of
    /// `Weapon`/`Gear` is set. City-side scenery builders (out of this port's
    /// scope) are the intended owner of placing these in the world; this file
    /// only reads them.</summary>
    public sealed class PickupTag : MonoBehaviour
    {
        public WeaponDef Weapon;
        public GearDef Gear;
        public bool IsGear => Gear != null;
    }
}

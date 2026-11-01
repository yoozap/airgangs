using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Marks a GameObject as a named landmark for the combat material
    /// classifier and the damage system — src/citymap.js:1781-1826
    /// (registerLandmark). Pure classification data; it carries no runtime
    /// HP of its own (that is Combat's job, the same City/Core split
    /// CombatBuildingChunk already uses for ordinary buildings — this file
    /// only answers "is this collider a landmark, and is it immune").
    ///
    /// BEFORE THIS FILE the concept did not exist anywhere in the port:
    /// Core/CombatWorldAdapter.cs's IsLandmark(Collider) was a hardcoded
    /// `=> false` with its own comment ("nothing in City/ attaches such tags
    /// to a collider today"), and City/BuildingDamage.cs's
    /// LandmarkStageParams/LandmarkCollapseDuration are pure maths helpers
    /// with no caller that threads an immunity flag through them. Separately,
    /// City/BuildingBuilder.cs generated a generic destructible grey box on
    /// every one of the 31 LandmarkPlacer.Exclusions footprints (see that
    /// file's own fix, same pass as this one) — so a landmark was not merely
    /// un-tagged, it had a stand-in box actually absorbing damage in its
    /// place. Both halves needed fixing for "shoot at Sameba Cathedral" to
    /// stop working.
    ///
    /// ATTACH THIS wherever a landmark/statue/shed GameObject is instantiated
    /// — the still-open "which component goes in which scene" question this
    /// project's README already flags for CityLoader.cs applies here too; the
    /// natural call site is whatever loader turns LandmarkPlacer.BuiltLandmarks/
    /// ProceduralLandmarks/HeroStatues (or Props/WarehouseCrane.cs's shed,
    /// src/shed.js:268 `registerLandmark(g, label, {pose:false, immune:true})`)
    /// into real GameObjects.
    /// </summary>
    public sealed class LandmarkTag : MonoBehaviour
    {
        /// <summary>citymap.js:1783 `name` — Georgian label used for the
        /// destruction toast / target card ("შენობა #n" style addressing).</summary>
        public string LandmarkName;

        /// <summary>citymap.js:1781's `immune` option. citymap.js:1828
        /// (_damageLandmark) early-returns when this is true: "nothing marks
        /// it, nothing brings it down". src/shed.js:268 sets this true for
        /// the player's own warehouse — "may [not] be shot down or crashed
        /// into rubble" — everything else defaults false (still the TOUGHEST
        /// thing in the city per citymap.js:1793's maxHp clamp, but not
        /// unkillable).</summary>
        public bool Immune;

        /// <summary>citymap.js:1781's `pose` option, stored inverted
        /// (`rec.noPose = pose === false`, citymap.js:1807): true disables the
        /// lean-and-sag collapse pose built for towers — on a long low
        /// building (the 152 m warehouse) even a 3° lean drops one end
        /// several metres into the terrain.</summary>
        public bool NoPose;

        /// <summary>citymap.js:1793 — maxHp = clamp(900 + h*30 + footprint*0.2,
        /// 1200, 6000). 0 means "not yet computed"; the loader that attaches
        /// this component is expected to fill it from the real mesh bounds the
        /// same way registerLandmark does, since a sealed component field
        /// cannot run that maths itself without a live Renderer to measure.</summary>
        public float MaxHp;
    }
}

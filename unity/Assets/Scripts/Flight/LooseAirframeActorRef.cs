using UnityEngine;

namespace TbilisiFly.Flight
{
    /// <summary>
    /// The read side of `col.userData.planeRec` (src/planephys.js:587, read at
    /// :886), so PlanePhysicsSystem's Sweep() can get from a RaycastHit's
    /// Collider to the aeroplane record that owns it.
    ///
    /// IT CARRIES THE RECORD, NOT THE ACTOR, and that distinction is the
    /// behaviour. The JS reads `hit.object.userData?.planeRec?.body` — a
    /// record, then the body HANGING OFF it — and `rec.body` is deliberately
    /// null for a machine that is not loose (src/planephys.js:719-720 `_park`
    /// nulls it the moment a wreck comes to rest). An earlier version of this
    /// file stored the LooseAirframeActor directly, which would have resolved
    /// a non-null actor for a PARKED aeroplane and made Sweep() dent and
    /// damage it as a rigid body where the JS treats it as world — a
    /// divergence in the opposite direction from the one it was meant to fix.
    /// Storing the record and deriving <see cref="Actor"/> from it reproduces
    /// the JS's own null-ness exactly, with no lifecycle of its own to keep in
    /// step.
    ///
    /// src/planephys.js has no direct counterpart to this file: it exists only
    /// because Unity needs a Component to carry a back-reference that a plain
    /// object property carries in JS. Split into its OWN file, named after it,
    /// per the unity skill's "ONE MonoBehaviour PER FILE" rule: a MonoBehaviour
    /// declared inside PlanePhysics.cs (this one's original location) binds to
    /// no script guid, so AddComponent&lt;LooseAirframeActorRef&gt;() would
    /// silently half-work.
    ///
    /// WRITTEN BY: PlanePhysicsSystem.BakeCollider (src/planephys.js:574-591
    /// `_bakeCollider`), and cleared by PlanePhysicsSystem.DropCollider
    /// (:562-569 `_dropCollider`). The JS has one more writer this layer
    /// cannot reach — src/target.js:71 `collider.userData.planeRec = rec` on
    /// registration, which is what makes a never-yet-loose hall machine
    /// resolvable; its Unity counterpart is Core's
    /// TargetRegistry.Entry.PlaneRec and attaching one here is that layer's
    /// call, not this file's.
    /// </summary>
    public sealed class LooseAirframeActorRef : MonoBehaviour
    {
        /// <summary>src/planephys.js:587 `col.userData.planeRec = rec`.</summary>
        public PlaneRecord Record;

        /// <summary>src/planephys.js:886 `...planeRec?.body` — null unless this
        /// machine is loose RIGHT NOW, which is the whole point. See the class
        /// comment.</summary>
        public LooseAirframeActor Actor => Record != null ? Record.Body : null;
    }
}

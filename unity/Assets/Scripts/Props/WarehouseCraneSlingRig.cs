using UnityEngine;

namespace TbilisiFly.Props
{
    /// <summary>
    /// Port of src/crane.js:442-461 — the four strops' per-frame re-aim. Built
    /// by <see cref="WarehouseCraneFill"/> (its own header explains why this
    /// needs to be a live component rather than baked geometry: the pivot
    /// swings, the aeroplane is counter-rotated inside it, and it is hauled
    /// up when lift beats weight — "a cable that is drawn once can only be
    /// right while nothing moves").
    ///
    /// ONE MonoBehaviour, named after this file, per the project's hard rule
    /// (unity SKILL.md: "ONE MonoBehaviour PER FILE, NAMED AFTER IT — or
    /// AddComponent silently half-works").
    ///
    /// All of the actual maths is PlaneSling.AimRopes (already ported, pure,
    /// unedited) — this file only does the Transform-walking boilerplate
    /// PlaneSling.cs's own header says is "a caller's job": converting the
    /// block (the swing pivot) and the four feet (fixed in the PLANE's own
    /// local space) into the ropes' shared parent's local space, every
    /// frame, in world space (crane.js:430 "NOT the pivot: these are driven
    /// in world space").
    /// </summary>
    public class WarehouseCraneSlingRig : MonoBehaviour
    {
        public Transform RopesParent;   // "crane_slings" — this GameObject itself, set by the bake
        public Transform Block;         // the swing pivot (crane.js's `swing`) — world position IS the hook block
        public Transform Plane;
        public Vector3[] FeetPlaneLocal; // PlaneSling.FindFeet's own output
        public Transform[] Ropes;        // 4 real rope GameObjects, sharing BuildUnitRopeMesh

        void LateUpdate() => Aim();

        /// <summary>crane.js:442-460, exactly. Public so a probe (or the bake
        /// itself, crane.js:461 "so the first frame drawn already has them
        /// right") can call it synchronously without waiting a frame.</summary>
        public void Aim()
        {
            if (Block == null || Plane == null || Ropes == null || RopesParent == null) return;
            Matrix4x4 toLocal = RopesParent.worldToLocalMatrix;
            Vector3 blockLocal = toLocal.MultiplyPoint3x4(Block.position);
            var feetLocal = new Vector3[FeetPlaneLocal.Length];
            for (int i = 0; i < feetLocal.Length; i++)
                feetLocal[i] = toLocal.MultiplyPoint3x4(Plane.TransformPoint(FeetPlaneLocal[i]));

            var mats = PlaneSling.AimRopes(blockLocal, feetLocal);
            for (int i = 0; i < Ropes.Length && i < mats.Length; i++)
            {
                if (Ropes[i] == null) continue;
                var m = mats[i];
                Ropes[i].localPosition = m.GetColumn(3);
                Ropes[i].localRotation = m.rotation;
                // PlaneSling.AimRopes' own TRS carries scale (1, length, 1) —
                // Matrix4x4.rotation/lossyScale decomposition on a pure TRS
                // (no shear) recovers that exactly.
                Ropes[i].localScale = m.lossyScale;
            }
        }
    }
}

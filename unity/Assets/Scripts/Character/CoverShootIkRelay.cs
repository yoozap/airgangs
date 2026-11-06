using UnityEngine;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Delivers the Animator's IK pass to the purchased pack's ShootBehaviour.
    /// Unity only calls <c>OnAnimatorIK</c> on MonoBehaviours that share the
    /// Animator's own GameObject; the pack's behaviours live on rig.Root (the
    /// physics body) while the Animator sits on the model three nodes below
    /// (Root/act/anim/Model — CharacterRig.cs), so the pack's torso-lean /
    /// aim-pitch / blocked-aim IK (ShootBehaviour.OnAnimatorIK) never ran: the
    /// gun stayed level while the camera looked up or down. The controller's
    /// Base Layer and Shooting layer both have the IK pass on, so this relay is
    /// all that was missing. Attached by CoverShootHero.Setup.
    /// </summary>
    public sealed class CoverShootIkRelay : MonoBehaviour
    {
        public ShootBehaviour Target;
        /// <summary>IK passes delivered — proof that the pass is reached (GunHandRun).</summary>
        public int Calls;

        /// <summary>
        /// THE PACK'S ANIMATION EVENTS LAND HERE TOO (2026-11-06). Unity
        /// delivers an AnimationEvent only to components on the GameObject
        /// that owns the Animator — the same rule as OnAnimatorIK above — and
        /// ShootBehaviour lives on the hero ROOT, one level up. So the reload
        /// clip's own `EndReloadWeapon` event found nothing and every reload
        /// logged "AnimationEvent 'EndReloadWeapon' on animation 'pistol_reload'
        /// has no receiver!" (the one error line FirstPlayModeRun kept
        /// reporting). ShootBehaviour's timer fallback finished the reload, so
        /// it worked while complaining; this makes the pack's own event drive
        /// it, which is what the animator timed it to.
        /// </summary>
        public void EndReloadWeapon()
        {
            if (Target == null || !Target.isActiveAndEnabled) return;
            Target.EndReloadWeapon();
        }

        void OnAnimatorIK(int layerIndex)
        {
            if (Target == null || !Target.isActiveAndEnabled) return;
            Calls++;
            Target.OnAnimatorIK(layerIndex);
        }
    }
}

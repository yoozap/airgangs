using UnityEngine;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// The one choke point the purchased "Cover + Shooting System" asset uses to
    /// reach OUR building-damage world. `ShootBehaviour.ShootWeapon` (the pack's
    /// own script, imported verbatim under Assets/ThirdParty/CoverShoot) already
    /// has a real `RaycastHit` from its own `Physics.Raycast` call — this bridge
    /// hands that straight to the SAME `IDamageWorld.DamageHit(RaycastHit, float)`
    /// call every other weapon in this project uses (Combat/Guns.cs:254,
    /// Combat/Rockets.cs:224, Combat/ImpactFx.cs:393, Core/PlaneWallContact.cs:143,
    /// Core/PlaneRaycastWorld.cs:165) so a purchased-shooter round damages a
    /// building exactly the way a belly-gun or rocket round does: HP drops, a
    /// crumble stage advances, glass breaks, a hole is carved — see
    /// DamageReachProbe.cs for the production call chain this now joins.
    ///
    /// Set once, in Core/CombatWorldAdapter.cs's Install() right where every
    /// other weapon system (`impactFx.World`, `machineGun.World`, ...) is wired
    /// to the same live `adapter` instance. Left null and no-op'd out when the
    /// purchased shooter is never attached (GameLoop.UsePurchasedShooter=false)
    /// or CombatWorldAdapter.Install has not run yet (e.g. a play-mode test that
    /// spawns the hero before the combat world boots).
    /// </summary>
    public static class CoverShootBridge
    {
        public static IDamageWorld DamageWorld;
        /// <summary>The pack's recoil camera bounce (ThirdPersonOrbitCam.BounceVertical,
        /// degrees per shot from InteractiveWeapon.recoilAngle) handed to the
        /// project's own on-foot camera — Core/GameLoop.cs consumes it with the
        /// orbit cam's own 10/s rise and 5 deg/s settle.</summary>
        public static System.Action<float> CameraKick;
    }
}

using TbilisiFly.Flight;
using UnityEngine;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Concrete, minimal implementation of <see cref="FootBoarding.IPlaneHost"/>
    /// attached to a plane wrapper by the cockpit-measurement bake. Wires to an
    /// OPTIONAL PlaneController for Airborne/Speed (a parked hall machine has
    /// none — Airborne/Speed read as false/0, matching a stationary parked
    /// plane in the JS).
    ///
    /// ITS OWN FILE, AT NAMESPACE LEVEL, BECAUSE A PLAYER BUILD DEPENDS ON IT.
    /// This was `FootBoarding.PlaneCockpitRig`, a MonoBehaviour NESTED inside
    /// another class, which Unity cannot give a script asset at all: the
    /// component on the flyable `Plane` in Assets/Scenes/Game.unity serialised
    /// as `m_Script: {fileID: 2108306704}` with NO GUID and only
    /// `m_EditorClassIdentifier: Assembly-CSharp::TbilisiFly.Character.FootBoarding/PlaneCockpitRig`
    /// to identify it. That identifier is EDITOR-ONLY metadata, so a built
    /// player cannot resolve the type, reads HalfSpanValue/ShellValue/
    /// CockpitKitValue/FlightController against nothing, and dies in scene load
    /// with "Data/level0 is corrupted! / [Position out of bounds!]" from
    /// CachedReader::OutOfBoundsError — the entire game failing to load, with
    /// nothing in the log naming the component. Un-nesting is the fix the
    /// skill's "ONE MonoBehaviour PER FILE, NAMED AFTER IT" rule demands;
    /// Combat/PlaneShellTag.cs was the other serialised offender.
    ///
    /// The nested helper TYPES it uses (IPlaneHost, CockpitInfo, ControlParts)
    /// are not MonoBehaviours and stay where they are.
    /// </summary>
    public sealed class PlaneCockpitRig : MonoBehaviour, FootBoarding.IPlaneHost
    {
        public float HalfSpanValue = 6f;
        public FootBoarding.CockpitInfo? CockpitValue;
        public FootBoarding.ControlParts ControlsValue;
        public Transform ShellValue;
        public Transform CockpitKitValue;
        public PlaneController FlightController;

        public Transform Wrapper => transform;
        public float HalfSpan => HalfSpanValue;
        public bool Airborne => FlightController != null && FlightController.AltitudeAGL > 0.5f;
        public float Speed => FlightController != null ? FlightController.Speed : 0f;
        public FootBoarding.CockpitInfo? Cockpit => CockpitValue;
        public FootBoarding.ControlParts Controls => ControlsValue;
        public Transform Shell => ShellValue;
        public Transform CockpitKit => CockpitKitValue;
    }
}

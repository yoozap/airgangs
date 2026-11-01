using TbilisiFly.Character;
using TbilisiFly.Flight;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Prints the SAME KEY=value lines as tools/_probe_planechar.mjs so the
    /// two can be diffed 1:1 via tools/unity_port_check.py (FootBoardingProbe.cs's
    /// own convention). Covers ONLY the constants this pass could name and
    /// cite against the real src/planes.js / src/characters.js literals —
    /// Flight/PlaneCockpitMeasurement.cs, PlaneCockpitKit.cs, RigSeatExtras.cs.
    ///
    /// NOT covered here (needs a loaded plane/body in a live scene — this
    /// task's brief held the batch-mode lock elsewhere, so none of the
    /// geometry-measuring code in those three files has been run):
    /// PlaneCockpitMeasurement.Measure, PlaneCockpitKit.FitCockpitKit/
    /// BuildPlaneShell/HingeHood, RigSeatExtras.ShoulderFront/HipHeight.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity ^
    ///         -executeMethod TbilisiFly.Probes.PlaneCharProbe.Run -logFile /dev/stdout
    /// </summary>
    public static class PlaneCharProbe
    {
        public static void Run()
        {
            Debug.Log($"PLANES_CONST id=_const pilotSit={PlaneCockpitMeasurement.PilotSit:F3} " +
                $"pilotShoulder={PlaneCockpitMeasurement.PilotShoulder:F3} pilotWide={PlaneCockpitMeasurement.PilotWide:F3} " +
                $"tubHalfW={PlaneCockpitKit.TubHalfW:F3} tubSill={PlaneCockpitKit.TubSill:F3} " +
                $"pilotGrowMax={PlaneCockpitMeasurement.PilotGrowMax:F3} pilotRoomMin={PlaneCockpitMeasurement.PilotRoomMin:F3} " +
                $"flightdeckRatio={PlaneCockpitMeasurement.FlightdeckRatio:F3} floorRatio={PlaneCockpitMeasurement.FloorRatio:F3} " +
                $"shellSlabs={PlaneCockpitKit.ShellSlabs} shellMinT={PlaneCockpitKit.ShellMinT:F3} hoodSwing={PlaneCockpitKit.HoodSwing:F3}");

            VLine("stick", "stick", PlaneCockpitKit.StickRaw);
            VLine("throttle", "throttle", PlaneCockpitKit.ThrottleRaw);
            VLine("pedalR", "pedalR", PlaneCockpitKit.PedalRRaw);
            VLine("pedalL", "pedalL", PlaneCockpitKit.PedalLRaw);

            Debug.Log($"PLANES_CONTROLS_THROW id=_throw stickPitch={FootBoarding.StickPitch:F3} " +
                $"stickRoll={FootBoarding.StickRoll:F3} pedalThrow={FootBoarding.PedalThrow:F3} " +
                $"throttleThrow={FootBoarding.ThrottleThrow:F3}");

            Debug.Log($"CHARACTERS_CONST id=_const shoulderBandX={RigSeatExtras.ShoulderBandX:F3} " +
                $"shoulderBandY={RigSeatExtras.ShoulderBandY:F3} shoulderMin={RigSeatExtras.ShoulderMin:F3} " +
                $"shoulderDefault={RigSeatExtras.ShoulderDefault:F3} hipRatio={RigSeatExtras.HipRatio:F3} " +
                $"strafeRate={RigSeatExtras.StrafeRate:F3}");
        }

        static void VLine(string id, string label, Vector3 v) =>
            Debug.Log($"PLANES_CONTROLS id={id} name={label} x={v.x:F3} y={v.y:F3} z={v.z:F3}");
    }
}

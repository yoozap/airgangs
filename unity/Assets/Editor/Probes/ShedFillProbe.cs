using TbilisiFly.Data;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves ShedFill.cs (the scene-graph half of src/shed.js, loading
    /// warehouse.fbx + the door cut + the pad/ramp meshes + colliders) against
    /// ShedLayout.cs's already-verified pure math (ShedLayoutProbe.cs) and
    /// against src/shed.js's own real constants. Builds BOTH real sheds (the
    /// x3 warehouse and the x16/7.5/9.3 plane hall) with the actual
    /// warehouse.fbx asset, using the same synthetic deterministic height
    /// field as ShedLayoutProbe.cs (a gentle slope + a sine ripple) so the
    /// numbers are reproducible without depending on StreamingAssets/city.json.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.ShedFillProbe.Run
    /// </summary>
    public static class ShedFillProbe
    {
        static float? HeightAt(float x, float z) => 100f + x * 0.01f + Mathf.Sin(z * 0.003f) * 3f;

        static void RunCase(string id, ShedFill.ShedBuildResult r, float apron, Vector3 scale, float px, float pz, float yaw)
        {
            Debug.Log($"SHEDFILL id={id} built=1 name={r.Root.name}");
            Debug.Log($"SHELL id={id} w={r.Shell.Width:F4} l={r.Shell.Length:F4} h={r.Shell.Height:F4}");

            // src/shed.js's real DOOR0/BASE constants, scaled the same way
            // ShedLayout.ScaledDoor does — same skeleton as
            // tools/_probe_shedfill.mjs's DOOR line, so these pair directly
            // (proves ShedFill used the shed.js numbers, not a re-typed copy).
            Debug.Log($"DOOR id={id} x={r.Shell.DoorHalfW:F6} y={r.Shell.DoorH:F6} z={r.Shell.DoorZ:F6}");

            // PAD/RAMPS below are printed in the SAME skeleton as
            // tools/_probe_shedlayout.mjs / tools/_probe_shedfill.mjs's own
            // PAD/RAMP lines, computed from the SAME real site (px,pz,yaw)
            // and the SAME synthetic height field — these two numbers (what
            // ShedFill actually built) and the JS replication's numbers (what
            // the math says it should be) are meant to pair and diff
            // numerically via tools/unity_port_check.py.
            Debug.Log($"PAD id={id} padTop={r.PadTop:F5} padDepth={r.PadDepth:F5}");
            Debug.Log($"RAMPS id={id} built={r.RampsBuilt}");

            // Everything below has NO JS counterpart on purpose — it needs a
            // real loaded mesh, which plain node cannot do (same limitation
            // ShedLayoutProbe.cs's own docstring documents for buildShed()).
            Debug.Log($"DOORCUT id={id} trisBefore={r.SolidTrianglesBefore} trisAfter={r.SolidTrianglesAfter} " +
                $"removed={r.SolidTrianglesBefore - r.SolidTrianglesAfter}");

            var box = r.Root.GetComponentsInChildren<BoxCollider>(true);
            var mesh = r.Root.GetComponentsInChildren<MeshCollider>(true);
            Debug.Log($"COLLIDERS id={id} box={box.Length} mesh={mesh.Length}");

            var inner = r.Root.transform.Find("inner");
            Debug.Log($"HIERARCHY id={id} hasInner={(inner != null ? 1 : 0)} " +
                $"hasPad={(r.Root.transform.Find("pad") != null ? 1 : 0)} " +
                $"childCount={r.Root.transform.childCount}");

            // Independent in-C# sanity check (not meant to pair with the JS
            // side): re-run ShedLayout.LevelPad directly with the SAME
            // halfW/halfL ShedFill derived from the real measured model, and
            // confirm ShedFill reported exactly what ShedLayout computes —
            // proving ShedFill actually calls ShedLayout rather than
            // duplicating its own copy of the formula.
            float halfW = r.Shell.Width / 2f + apron, halfL = r.Shell.Length / 2f + apron;
            var padIndep = ShedLayout.LevelPad(px, pz, halfW, halfL, yaw, HeightAt);
            bool padOk = Mathf.Abs(padIndep.PadTop - r.PadTop) < 1e-4f && Mathf.Abs(padIndep.PadDepth - r.PadDepth) < 1e-4f;
            Debug.Log($"SELFCHECK id={id} padMatchesLayout={(padOk ? 1 : 0)}");
        }

        public static void Run()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var meta = new PlaneHallLayout.GeoMeta
            {
                OriginLat = GeoFrame.Lat0, OriginLon = GeoFrame.Lon0,
                MLat = GeoFrame.MetresPerLat, MLon = GeoFrame.MetresPerLon,
            };
            Debug.Log($"CONST id=_const lat={ShedLayout.ShedLat:F6} lon={ShedLayout.ShedLon:F6} yaw={ShedLayout.ShedYaw:F6}");

            try
            {
                var (wx, wz) = ShedLayout.SiteFromLatLon(ShedLayout.ShedLat, ShedLayout.ShedLon, meta);
                var warehouse = ShedFill.BuildWarehouse(null, meta, HeightAt);
                RunCase("warehouse", warehouse, 7f, new Vector3(3f, 3f, 3f), wx, wz, ShedLayout.ShedYaw);

                var (hx, hz, _, _, _, _) = PlaneHallLayout.Site(meta, ShedLayout.ShedYaw);
                var hall = ShedFill.BuildPlaneHall(null, hx, hz, ShedLayout.ShedYaw, HeightAt);
                RunCase("hall", hall, PlaneHallLayout.Apron, PlaneHallLayout.Scale, hx, hz, ShedLayout.ShedYaw);

                ProbeExit.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"SHEDFILL FAILED: {e}");
                ProbeExit.Exit(1);
            }
        }
    }
}

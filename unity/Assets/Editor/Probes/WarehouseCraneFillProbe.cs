using TbilisiFly.Data;
using TbilisiFly.Flight;
using TbilisiFly.Props;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves WarehouseCraneFill.cs/WarehouseFanSwitch.cs/
    /// WarehouseCraneSlingRig.cs against a REAL built warehouse (same
    /// ShedFill.BuildWarehouse call and synthetic height field
    /// ShedFillProbe.cs already uses) — not a synthetic stand-in. This is
    /// the "is it REACHED" proof unity SKILL.md's own history section asks
    /// for: a count computed off a fixture is not evidence; this builds the
    /// real crane_machine.fbx/p51.fbx/fan.fbx assets, in a real Transform
    /// hierarchy, under a real GeoFrame-placed shed root, and reads the
    /// result back off real Unity components.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.WarehouseCraneFillProbe.Run
    /// </summary>
    public static class WarehouseCraneFillProbe
    {
        static float? HeightAt(float x, float z) => 100f + x * 0.01f + Mathf.Sin(z * 0.003f) * 3f;

        public static void Run()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                // ---- independent algebra check (no scene needed) --------------
                // tools/_probe_crane.mjs's own ground truth: hook/aim/hubY/
                // halfDepth, fed through WarehouseCrane.Compute with the SAME
                // handedness convention this pass's bake uses (Unity-space
                // in, Unity-space out — see WarehouseCraneFill.cs's header).
                // JS: fan0=(9.9,7.9,24.35) fan1=(9.9,7.9,31.35) fan2=(9.9,7.9,38.35)
                // Mirror(JS) = negate X -> (-9.9,...) for all three.
                Vector3 hookJs = new Vector3(-0.1f, 7.9f, 31.35f);
                Vector3 hookUnity = new Vector3(-hookJs.x, hookJs.y, hookJs.z);
                Vector3 aimUnity = hookUnity + new Vector3(0f, -0.3f, 0f);
                var placements = WarehouseCrane.Compute(hookUnity, aimUnity, 1.3f, 0.9f);
                for (int i = 0; i < placements.Length; i++)
                    Debug.Log($"CRANEFILL_FANALG i={i} x={placements[i].Position.x:F4} y={placements[i].Position.y:F4} z={placements[i].Position.z:F4}");

                // ---- the real bake --------------------------------------------
                var meta = new PlaneHallLayout.GeoMeta
                {
                    OriginLat = GeoFrame.Lat0, OriginLon = GeoFrame.Lon0,
                    MLat = GeoFrame.MetresPerLat, MLon = GeoFrame.MetresPerLon,
                };
                var warehouse = ShedFill.BuildWarehouse(null, meta, HeightAt);
                Debug.Log($"CRANEFILL_SHED built={(warehouse.Root != null ? 1 : 0)} name={warehouse.Root?.name}");

                var result = WarehouseCraneFill.Build(warehouse);
                Debug.Log($"CRANEFILL_BUILT built={(result.Built ? 1 : 0)} scale={result.ScaleApplied:F4} " +
                    $"hookX={result.HookLocalInCraneUnity.x:F4} hookY={result.HookLocalInCraneUnity.y:F4} hookZ={result.HookLocalInCraneUnity.z:F4} " +
                    $"fans={result.FanHolders?.Length ?? 0} cablePts={result.CablePolylinePoints} wallAt={result.WallAtUnity:F4}");

                if (!result.Built)
                {
                    Debug.LogWarning("CRANEFILL: Build() returned Built=false (missing FBX assets?) — stopping here, not a hard failure of the wiring itself.");
                    ProbeExit.Exit(0);
                    return;
                }

                // ---- wiring: is the runtime physics actually REACHED? ---------
                int meshColliders = result.CraneRoot.GetComponentsInChildren<MeshCollider>(true).Length;
                int boxColliders = warehouse.Root.GetComponentsInChildren<BoxCollider>(true).Length;
                var devTags = result.CraneRoot.GetComponents<TbilisiFly.Tools.DevPlacementTag>();
                Debug.Log($"CRANEFILL_COLLIDERS craneMesh={meshColliders} boxTotal={boxColliders} devTag={devTags.Length}");

                bool slingWired = result.Slings != null && result.Slings.Ropes != null && result.Slings.Ropes.Length == 4
                    && result.Slings.Block != null && result.Slings.Plane != null;
                Debug.Log($"CRANEFILL_SLING wired={(slingWired ? 1 : 0)} ropes={result.Slings?.Ropes?.Length ?? 0}");

                bool switchWired = result.Switch != null && result.Switch.SwingPivot != null && result.Switch.Plane != null
                    && result.Switch.FanRotors != null;
                Debug.Log($"CRANEFILL_SWITCH wired={(switchWired ? 1 : 0)} rotors={result.Switch?.FanRotors?.Length ?? 0} " +
                    $"skinCols={result.Switch?.SkinColliders?.Length ?? 0}");

                // Actually tick it, like a real frame would (GameLoop.WorldTick
                // has no live instance in this headless probe scene — this
                // proves the OTHER half, that WarehouseFanSwitch.Update runs
                // end-to-end with no GameLoop present, same as a scene with the
                // crane built but the player elsewhere, rather than throwing).
                InvokeAwake(result.Switch); // see InvokeAwake's own doc comment — a real, separate finding
                result.Switch.Press(WarehouseCrane.FanSwitch.Setting.Lo);
                for (int i = 0; i < 5; i++) InvokeUpdate(result.Switch);
                Debug.Log($"CRANEFILL_TICK mode={result.Switch.Mode} airspeed={GetAirspeed(result.Switch):F2} " +
                    $"swingY={(result.SwingPivot != null ? result.SwingPivot.transform.localRotation.eulerAngles.y : -1f):F3}");

                if (result.Slings != null) result.Slings.Aim(); // crane.js:461 "first frame drawn already has them right"
                Debug.Log($"CRANEFILL_ROPES aimed=1 rope0pos={(result.Slings?.Ropes?[0]?.localPosition.ToString("F3") ?? "n/a")}");

                // ---- wind_tunnel_flow + propeller windmilling: real wiring ----
                // WarehouseFanSwitch.Update() reads Time.deltaTime, which is 0
                // outside Play mode in a headless -batchmode run (the TICK
                // line above already shows airspeed=0.00 for exactly this
                // reason) — so this pushes the switch's own Airspeed up
                // directly via FanSwitch.Tick(dt) with a SYNTHETIC dt (that
                // method takes dt as a real parameter, it does not read
                // Time.deltaTime itself), bypassing Unity's clock rather than
                // the switch's ramp logic. flowOpacity/propRpm in
                // WindTunnel.Step are functions of airspeed alone (not of
                // dt), so they come out real and nonzero even on a dt=0
                // Update() call right after.
                var switchField = typeof(WarehouseFanSwitch).GetField("_switch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var fanSwitch = switchField?.GetValue(result.Switch) as WarehouseCrane.FanSwitch;
                fanSwitch?.Tick(30f); // SpeedUp=700 km/h/s * 30s >> AirLo=500 -> pinned at 500
                var fmField = typeof(WarehouseFanSwitch).GetField("_fm", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var initField = typeof(WarehouseFanSwitch).GetField("_initialized", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Debug.Log($"CRANEFILL_DIAG fmNull={(fmField?.GetValue(result.Switch) == null ? 1 : 0)} initialized={initField?.GetValue(result.Switch)}");
                InvokeUpdate(result.Switch);
                Debug.Log($"CRANEFILL_FLOW renderer={(result.Switch.FlowRenderer != null ? 1 : 0)} " +
                    $"enabled={(result.Switch.FlowRenderer != null ? (result.Switch.FlowRenderer.enabled ? 1 : 0) : -1)} " +
                    $"opacity={(result.Switch.FlowRenderer != null ? result.Switch.FlowRenderer.sharedMaterial.GetFloat("_Opacity") : -1f):F4} " +
                    $"texOffset={(result.Switch.FlowRenderer != null ? result.Switch.FlowRenderer.sharedMaterial.mainTextureOffset.ToString("F4") : "n/a")} " +
                    $"verts={(result.Switch.FlowRenderer != null ? result.Switch.FlowRenderer.GetComponent<MeshFilter>().sharedMesh.vertexCount : -1)} " +
                    $"airspeedAtTest={GetAirspeed(result.Switch):F2}");

                var props = result.Switch.Propellers;
                if (props != null && props.Count > 0)
                {
                    var p0 = props[0];
                    // IS THE HUB ON THE NOSE? pivotLocalPos alone cannot say
                    // — it needs the airframe's own extent to be read against,
                    // because which END of the plane the hub's z sits at is the
                    // whole question (it used to sit 4.95 m the wrong way, just
                    // behind the cockpit; see Props/WarehouseCraneFill.
                    // BuildPropellers' header). The nose is the plane holder's
                    // local -Z: the FBX bridge mirrors X, not Z, so these files
                    // arrive nose-aft exactly as the GLB had them — measured on
                    // all 35 by PlanePropsProbe.Axis. So a correct hub has
                    // z NEGATIVE and within a few cm of the model's own minZ
                    // plus the spinner's length, and the spin axis points the
                    // same way.
                    var planeT = p0.Pivot.parent;
                    Bounds pb = default; bool first = true;
                    foreach (var r in planeT.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r.transform.IsChildOf(p0.Pivot)) continue; // not the propeller we just built
                        var lb = r.bounds;
                        var c = planeT.InverseTransformPoint(lb.center);
                        var e = planeT.InverseTransformVector(lb.extents);
                        var b = new Bounds(c, new Vector3(Mathf.Abs(e.x), Mathf.Abs(e.y), Mathf.Abs(e.z)) * 2f);
                        if (first) { pb = b; first = false; } else pb.Encapsulate(b);
                    }
                    Debug.Log($"CRANEFILL_PROP count={props.Count} hasDisc={(p0.HasDisc ? 1 : 0)} " +
                        $"radius={p0.Radius:F3} blades={(p0.Blades?.Length ?? 0)} " +
                        $"discEnabled={(p0.Disc != null ? (p0.Disc.GetComponent<Renderer>().enabled ? 1 : 0) : -1)} " +
                        $"pivotLocalPos={p0.Pivot.localPosition.ToString("F3")}");
                    Debug.Log($"CRANEFILL_PROPNOSE planeLocalZ=[{pb.min.z:F3},{pb.max.z:F3}] " +
                        $"hubZ={p0.Pivot.localPosition.z:F3} " +
                        $"onNose={(p0.Pivot.localPosition.z < 0f ? 1 : 0)} " +
                        $"spinAxisLocal={p0.SpinAxisLocal.ToString("F3")} " +
                        $"authoredHubF={TbilisiFly.Flight.Planes.PlaneById("p51")?.PropMake?.Hub.z ?? -1f:F3}");
                }
                else Debug.Log("CRANEFILL_PROP count=0");

                ProbeExit.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"CRANEFILL FAILED: {e}");
                ProbeExit.Exit(1);
            }
        }

        // MonoBehaviour.Update() is private/engine-called; invoke it directly
        // via reflection so this probe can prove a handful of real frames run
        // with no PlayMode loop (same technique other Editor probes in this
        // project use to exercise a MonoBehaviour outside Play mode).
        static void InvokeUpdate(WarehouseFanSwitch sw)
        {
            var m = typeof(WarehouseFanSwitch).GetMethod("Update",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            m?.Invoke(sw, null);
        }

        /// <summary>REAL FINDING (this pass): AddComponent&lt;WarehouseFanSwitch&gt;()
        /// from an -executeMethod editor script does NOT run Awake()
        /// synchronously — there is no scene tick in this headless harness
        /// to deliver it. Every earlier CRANEFILL_TICK call sequence (5x
        /// InvokeUpdate with no InvokeAwake first) was therefore hitting
        /// Update()'s own `if (!_initialized) return;` guard and returning
        /// immediately every time — a real instance of unity SKILL.md's "a
        /// probe proves a calculation, not that it is REACHED" class of bug,
        /// just inside the PROBE rather than the game. Forced explicitly
        /// here so this probe's own CRANEFILL_FLOW/CRANEFILL_PROP numbers
        /// are proven against the REAL per-frame branch, not a no-op.</summary>
        static void InvokeAwake(WarehouseFanSwitch sw)
        {
            var m = typeof(WarehouseFanSwitch).GetMethod("Awake",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            m?.Invoke(sw, null);
        }

        static float GetAirspeed(WarehouseFanSwitch sw)
        {
            var f = typeof(WarehouseFanSwitch).GetField("_switch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var fs = f?.GetValue(sw) as WarehouseCrane.FanSwitch;
            return fs?.Airspeed ?? -1f;
        }

        /// <summary>
        /// Derives WarehouseCraneFill.HookOverrideLocal from the REAL
        /// crane_machine.fbx mesh rather than guessing it. Chain of custody:
        /// tools/build_crane_machine.py computes BLOCK (the main block's
        /// centre-X/Y, bottom-Z, in Blender Z-up space) and bakes it into
        /// public/models/crane_machine.glb's root-node `extras.hook` as
        /// glTF Y-up [x,y,z] = [-12.499158, 4.331408, -0.145040] (read
        /// directly out of that GLB's JSON chunk — not re-derived by eye).
        /// Two things had to be checked, not assumed, before that number
        /// could be reused here:
        ///   1. crane_machine.fbx (what Unity imports) is byte-for-byte the
        ///      SAME mesh as crane_machine.glb round-tripped through
        ///      tools/glb_batch_fbx.py's Blender import/export — verified
        ///      by loading both headless in Blender and comparing bounding
        ///      boxes: (-12.9260,-4.2938,-3.5686)-(5.5587,4.2948,8.7926) for
        ///      the GLB vs (-12.9260,-4.2938,-3.5686)-(5.5587,4.2948,8.7926)
        ///      for the FBX, same 131897 verts both sides. Not just "close";
        ///      the GLTF->FBX axis round trip (axis_forward=-Z, axis_up=Y)
        ///      is the identity on these axes.
        ///   2. Unity's OWN FBX import mirrors X at import time (ShedFill.cs's
        ///      header, "the FBX importer applies the same mirror" as
        ///      glTFast) — so the Unity-space hook is (-gltf.x, gltf.y,
        ///      gltf.z) = (12.499158, 4.331408, -0.145040), NOT the raw
        ///      glTF triple. This method proves that by measuring the loaded
        ///      FBX's own vertices (rule (a): already Unity-correct, no
        ///      further mirror) and finding the nearest one to that mirrored
        ///      point — if the chain above is right, a real surface vertex
        ///      sits within centimetres of it; if it is wrong (e.g. the
        ///      import does NOT mirror, or mirrors a different axis), the
        ///      nearest vertex will be metres away instead, same test the
        ///      earlier Blender-only check already passed in Blender's own
        ///      (non-Unity) axes before this method was written.
        ///
        ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.WarehouseCraneFillProbe.HookDerivation
        /// </summary>
        public static void HookDerivation()
        {
            try
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(WarehouseCraneFill.CraneModelPath);
                if (prefab == null)
                {
                    Debug.LogError($"HOOKDERIVE missing={WarehouseCraneFill.CraneModelPath}");
                    ProbeExit.Exit(1);
                    return;
                }
                var inner = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                Debug.Log($"HOOKDERIVE_NATURAL pos={inner.transform.position} rot={inner.transform.rotation.eulerAngles} scale={inner.transform.localScale} lossy={inner.transform.lossyScale}");
                foreach (Transform child in inner.transform)
                    Debug.Log($"HOOKDERIVE_CHILD name={child.name} localScale={child.localScale} lossy={child.lossyScale} localPos={child.localPosition}");
                var mf0 = inner.GetComponentInChildren<MeshFilter>(true);
                if (mf0 != null && mf0.sharedMesh != null)
                    Debug.Log($"HOOKDERIVE_MESHBOUNDS local={mf0.sharedMesh.bounds} verts={mf0.sharedMesh.vertexCount}");
                var rend0 = inner.GetComponentsInChildren<Renderer>(true);
                if (rend0.Length > 0)
                {
                    Bounds natBox = rend0[0].bounds;
                    for (int i = 1; i < rend0.Length; i++) natBox.Encapsulate(rend0[i].bounds);
                    Debug.Log($"HOOKDERIVE_NATURALBOX min={natBox.min} max={natBox.max} size={natBox.size}");
                }

                // Do NOT reset rotation/scale — WarehouseCraneFill.cs's own
                // fix (this pass) keeps the natural (0,180,180)/(100,100,100)
                // import transform, so the measurement here must match what
                // the real bake now does.
                inner.transform.position = Vector3.zero;

                var renderers = inner.GetComponentsInChildren<Renderer>(true);
                Bounds box = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) box.Encapsulate(renderers[i].bounds);
                Debug.Log($"HOOKDERIVE_BOX min=({box.min.x:F4},{box.min.y:F4},{box.min.z:F4}) max=({box.max.x:F4},{box.max.y:F4},{box.max.z:F4})");

                // gltf extras.hook = (-12.499158, 4.331408, -0.145040); Unity
                // import mirrors X (ShedFill.cs's header rule (a)) -> candidate:
                Vector3 candidate = new Vector3(12.499158f, 4.331408f, -0.145040f);

                float best = float.PositiveInfinity;
                Vector3 bestV = Vector3.zero;
                int n = 0;
                foreach (var mf in inner.GetComponentsInChildren<MeshFilter>(true))
                {
                    var mesh = mf.sharedMesh;
                    if (mesh == null) continue;
                    var local = mesh.vertices;
                    var m = mf.transform.localToWorldMatrix;
                    n += local.Length;
                    foreach (var lv in local)
                    {
                        Vector3 w = m.MultiplyPoint3x4(lv);
                        float d = (w - candidate).sqrMagnitude;
                        if (d < best) { best = d; bestV = w; }
                    }
                }
                Debug.Log($"HOOKDERIVE_NEAREST dist={Mathf.Sqrt(best):F4} at=({bestV.x:F4},{bestV.y:F4},{bestV.z:F4}) ofN={n}");

                // Negative control: if the import did NOT mirror X, THIS
                // point would be the close one instead, ~25 m off (twice the
                // X magnitude) rather than a few cm off.
                Vector3 unmirrored = new Vector3(-12.499158f, 4.331408f, -0.145040f);
                float best2 = float.PositiveInfinity;
                foreach (var mf in inner.GetComponentsInChildren<MeshFilter>(true))
                {
                    var mesh = mf.sharedMesh;
                    if (mesh == null) continue;
                    var local = mesh.vertices;
                    var m = mf.transform.localToWorldMatrix;
                    foreach (var lv in local)
                    {
                        Vector3 w = m.MultiplyPoint3x4(lv);
                        float d = (w - unmirrored).sqrMagnitude;
                        if (d < best2) best2 = d;
                    }
                }
                Debug.Log($"HOOKDERIVE_NEAREST_UNMIRRORED dist={Mathf.Sqrt(best2):F4}");

                UnityEngine.Object.DestroyImmediate(inner);

                // Same question for the other two FBX this bake instantiates
                // the identical way (SetPositionAndRotation+localScale=one) —
                // is the "natural" scale=100/rot=(0,180,180) quirk unique to
                // crane_machine.fbx, or does it affect p51.fbx/fan.fbx too
                // (which would mean PlaneHallFill.cs's parked fleet has the
                // same exposure)?
                foreach (var path in new[] { WarehouseCraneFill.PlaneModelPath, WarehouseCraneFill.FanModelPath })
                {
                    var pf = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (pf == null) { Debug.LogWarning($"HOOKDERIVE_OTHER missing={path}"); continue; }
                    var go2 = (GameObject)PrefabUtility.InstantiatePrefab(pf);
                    Debug.Log($"HOOKDERIVE_OTHER path={path} scale={go2.transform.localScale} rot={go2.transform.rotation.eulerAngles}");
                    UnityEngine.Object.DestroyImmediate(go2);
                }

                ProbeExit.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"HOOKDERIVE FAILED: {e}");
                ProbeExit.Exit(1);
            }
        }
    }
}

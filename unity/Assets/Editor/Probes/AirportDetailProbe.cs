using TbilisiFly.City;
using TbilisiFly.Combat;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves AirportDetail.cs's layout math against the REAL prop assets,
    /// with NO scene dependency at all — deterministic regardless of
    /// whatever ShedFill.cs/the sibling unit has or hasn't built yet (the
    /// task brief's own instruction: "Your probe should exercise THIS
    /// self-contained path so it has a deterministic, reviewable answer
    /// independent of scene state or the sibling agent's timing").
    ///
    /// It uses AirportDetail.EstimateWarehouseSizeFallback() for the shed
    /// size (48 x 15 x 138.9 m) and measures the REAL wooden_table.fbx /
    /// akm_anim.fbx assets already on disk (Assets/Resources/Art/Props/) by
    /// instantiating them in a throwaway scratch scene, measuring, and
    /// destroying — so the printed numbers are not guessed constants, they
    /// come from the actual mesh data this project ships.
    ///
    /// Counterpart: tools/_probe_airportdetail.mjs (transcribes the same
    /// airport.js literals and re-derives the same layout numerically, for
    /// side-by-side eyeballing — it cannot load the FBX/measure real bounds,
    /// so its table/rifle numbers use the same fallback shed size but a
    /// TRANSCRIBED raw table bounds rather than a measured one; see its own
    /// header for exactly which numbers are "real" vs "transcribed" on each
    /// side).
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.EditorTools.AirportDetailProbe.Run
    ///        -logFile /dev/stdout
    ///
    /// DetailProbe.cs (the four-unit aggregator) calls <see cref="Print"/>
    /// directly once this file lands — see that file's own header; its call
    /// is commented out today because this file did not exist yet when it
    /// was written.
    /// </summary>
    public static class AirportDetailProbe
    {
        const string TableFbx = "Assets/Resources/Art/Props/wooden_table.fbx";
        const string AkmFbx = "Assets/Resources/Art/Props/akm_anim.fbx";

        public static void Run()
        {
            Print();
            ProbeExit.Exit(0);
        }

        public static void Print()
        {
            var size = AirportDetail.EstimateWarehouseSizeFallback();
            Debug.Log($"AIRPORTDETAILPROBE_SHED width={size.Width:F3} height={size.Height:F3} length={size.Length:F3}");

            // ---- tables -----------------------------------------------------
            var tableFbx = AssetDatabase.LoadAssetAtPath<GameObject>(TableFbx);
            GameObject tableRoot = null, flatTable = null;
            Bounds rawBounds;
            if (tableFbx == null)
            {
                Debug.LogWarning($"AIRPORTDETAILPROBE_TABLE_MISSING path={TableFbx}");
                rawBounds = new Bounds(Vector3.zero, Vector3.zero);
            }
            else
            {
                var importer = AssetImporter.GetAtPath(TableFbx) as ModelImporter;
                bool wasReadable = importer == null || importer.isReadable;
                if (importer != null && !importer.isReadable)
                {
                    importer.isReadable = true;
                    importer.SaveAndReimport();
                }
                // NATURAL IMPORT TRANSFORM, kept (2026-10-30). wooden_table.fbx
                // imports with rot (0,180,180) and scale (254,254,254), and
                // AirportDetail.FlattenProp bakes relative to the root's OWN
                // local frame — so flattening the FBX ROOT cancels exactly that
                // correction and measures a 1/254-scale, Y/Z-negated table.
                // Props/WarehouseInteriorFill.cs hit this for real (the whole
                // interior built ~100x small); this probe must measure the same
                // way the builder does or its TABLE_RAW/TABLE_LAYOUT numbers go
                // stale against the thing they are supposed to prove. Fix: give
                // FlattenProp a WRAPPER at world identity, so the natural
                // transform ends up baked into the merged mesh.
                var tableInst = (GameObject)PrefabUtility.InstantiatePrefab(tableFbx);
                tableRoot = new GameObject("wooden_table_wrap");
                tableRoot.transform.position = Vector3.zero;
                tableRoot.transform.rotation = Quaternion.identity;
                tableRoot.transform.localScale = Vector3.one;
                tableInst.transform.SetParent(tableRoot.transform, false);
                Debug.Log($"AIRPORTDETAILPROBE_TABLE_NATURAL rot={tableInst.transform.localRotation.eulerAngles.ToString("F1")} " +
                          $"scale={tableInst.transform.localScale.ToString("F4")} " +
                          $"pos={tableInst.transform.localPosition.ToString("F4")}");
                flatTable = AirportDetail.FlattenProp(tableRoot.transform);
                rawBounds = MeasureLocalBounds(flatTable.transform);
                Debug.Log($"AIRPORTDETAILPROBE_TABLE_RAW sizeX={rawBounds.size.x:F4} sizeY={rawBounds.size.y:F4} " +
                          $"sizeZ={rawBounds.size.z:F4} centerX={rawBounds.center.x:F4} centerY={rawBounds.center.y:F4} " +
                          $"centerZ={rawBounds.center.z:F4} importerWasReadable={(wasReadable ? 1 : 0)}");
            }

            var tableLayout = AirportDetail.ComputeTableLayout(size, rawBounds);
            Debug.Log($"AIRPORTDETAILPROBE_TABLE_LAYOUT scale={tableLayout.Scale:F5} len={tableLayout.TableLength:F3} " +
                      $"offsetX={tableLayout.ModelLocalOffset.x:F4} offsetY={tableLayout.ModelLocalOffset.y:F4} " +
                      $"offsetZ={tableLayout.ModelLocalOffset.z:F4}");
            for (int i = 0; i < tableLayout.Spots.Length; i++)
            {
                var s = tableLayout.Spots[i];
                Debug.Log($"AIRPORTDETAILPROBE_TABLE_SPOT index={i} x={s.x:F4} y={s.y:F4} z={s.z:F4}");
            }
            // Echoed so the JS side's own honesty line has a partner and the pairing
            // is clean; the JS probe cannot read the real wooden-table bounds.
            Debug.Log($"AIRPORTDETAILPROBE_TABLE_SPOT_UNMEASURED indices=1,2 reason=needs_real_wooden_table_bounds");
            Debug.Log($"AIRPORTDETAILPROBE_TABLE_COUNT n={tableLayout.Spots.Length}");

            if (flatTable != null && flatTable != tableRoot) Object.DestroyImmediate(flatTable);
            if (tableRoot != null) Object.DestroyImmediate(tableRoot);

            // ---- ammo crates --------------------------------------------------
            var crates = AirportDetail.ComputeCrateSpots();
            foreach (var c in crates)
                Debug.Log($"AIRPORTDETAILPROBE_CRATE id={c.Id} x={c.Pos.x:F4} y={c.Pos.y:F4} z={c.Pos.z:F4} yaw={c.YawRad:F5}");
            Debug.Log($"AIRPORTDETAILPROBE_CRATE_COUNT n={crates.Length}");

            // ---- guns (pistols + rifle) ---------------------------------------
            var guns = AirportDetail.ComputeGunLayout(tableLayout.Spots[0], tableLayout.Spots[1]);
            foreach (var g in guns)
            {
                if (!g.IsRifle)
                {
                    var p = AirportDetail.PistolLocalPos(g);
                    var r = AirportDetail.PistolLocalRot(g);
                    // eulerY is NOT printed on the GUN line: _probe_airportdetail.mjs
                    // does not print it either, and the extra token changed the line's
                    // skeleton so unity_port_check.py paired 0 of the GUN values. It is
                    // still worth seeing, so it goes on a line of its own.
                    Debug.Log($"AIRPORTDETAILPROBE_GUN id={g.Id} x={p.x:F4} y={p.y:F4} z={p.z:F4} " +
                              $"ry={g.RyRad:F5} rifle=0");
                    Debug.Log($"AIRPORTDETAILPROBE_GUN_EULER id={g.Id} eulerY={r.eulerAngles.y:F3}");
                }
                else
                {
                    float depthZ = MeasureRifleDepthZ(g.Id);
                    var p = AirportDetail.RifleLocalPos(g, depthZ);
                    var r = AirportDetail.RifleLocalRot(g.RyRad);
                    // The rifle line is split in two. _probe_airportdetail.mjs cannot
                    // measure the AKM's real bounds (no FBX in Node), so it prints only
                    // `GUN id=akm x= ry= rifle=1` and declares y/z/depthZ UNMEASURED.
                    // Printing the measured extras on the SAME line changed its skeleton
                    // and nothing paired. So: one line matching the JS shape exactly,
                    // and the richer measured values under their own key.
                    Debug.Log($"AIRPORTDETAILPROBE_GUN id={g.Id} x={p.x:F4} ry={g.RyRad:F5} rifle=1");
                    Debug.Log($"AIRPORTDETAILPROBE_GUN_MEASURED id={g.Id} y={p.y:F4} z={p.z:F4} " +
                              $"depthZ={depthZ:F4} liftY={AirportDetail.RifleLiftY(depthZ):F4} " +
                              $"quatX={r.x:F4} quatY={r.y:F4} quatZ={r.z:F4} quatW={r.w:F4}");
                    Debug.Log($"AIRPORTDETAILPROBE_GUN_UNMEASURED id={g.Id} fields=y,z,depthZ reason=needs_real_akm_bounds_and_table_spot");
                }
            }
            Debug.Log($"AIRPORTDETAILPROBE_GUN_COUNT n={guns.Length}");
        }

        static float MeasureRifleDepthZ(string gunId)
        {
            var def = WeaponDefs.ById.TryGetValue(gunId, out var d) ? d : null;
            float scale = def?.Scale ?? 1f;
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(AkmFbx);
            if (fbx == null)
            {
                Debug.LogWarning($"AIRPORTDETAILPROBE_RIFLE_MISSING path={AkmFbx}");
                return 0f;
            }
            var rifle = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            rifle.transform.position = Vector3.zero;
            // akm_anim.fbx is the one prop here whose ROOT natural transform
            // really IS identity/one (its 100 sits on the inner `akm_anim`
            // node), so setting them is a no-op — asserted rather than assumed.
            Debug.Log($"AIRPORTDETAILPROBE_RIFLE_NATURAL rot={rifle.transform.localRotation.eulerAngles.ToString("F1")} " +
                      $"scale={rifle.transform.localScale.ToString("F4")}");
            rifle.transform.rotation = Quaternion.identity;
            rifle.transform.localScale = Vector3.one * scale;

            // FPS-DOWNLOAD JUNK: the FBX carries a 2 m `Icosphere` beside the
            // rifle (tools/import_pistols.py:8 names it; the GLB the JS loads,
            // public/models/akm_anim.glb, has none). Measured, it returned
            // depthZ=2.0 for a 0.068 m-thick rifle and lifted the AKM a metre
            // above the bench. Props/WarehouseInteriorFill.StripFpsJunk does
            // the same at build time.
            int junk = 0;
            foreach (var t in rifle.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t == rifle.transform) continue;
                if (!t.name.StartsWith("Icosphere", System.StringComparison.OrdinalIgnoreCase)) continue;
                Object.DestroyImmediate(t.gameObject);
                junk++;
            }
            Debug.Log($"AIRPORTDETAILPROBE_RIFLE_JUNK icospheres={junk}");

            Bounds? b = null;
            foreach (var r in rifle.GetComponentsInChildren<Renderer>(true))
                b = b.HasValue ? Encapsulate(b.Value, r.bounds) : r.bounds;
            float depth = (b ?? new Bounds()).size.z;
            Object.DestroyImmediate(rifle);
            return depth;
        }

        static Bounds MeasureLocalBounds(Transform root)
        {
            Bounds? b = null;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var local = mf.sharedMesh.bounds;
                Matrix4x4 m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                var c = local.center; var e = local.extents;
                Vector3[] corners =
                {
                    c + new Vector3(-e.x, -e.y, -e.z), c + new Vector3(e.x, -e.y, -e.z),
                    c + new Vector3(-e.x, e.y, -e.z),  c + new Vector3(e.x, e.y, -e.z),
                    c + new Vector3(-e.x, -e.y, e.z),  c + new Vector3(e.x, -e.y, e.z),
                    c + new Vector3(-e.x, e.y, e.z),   c + new Vector3(e.x, e.y, e.z),
                };
                for (int i = 0; i < corners.Length; i++) corners[i] = m.MultiplyPoint3x4(corners[i]);
                var wb = new Bounds(corners[0], Vector3.zero);
                for (int i = 1; i < corners.Length; i++) wb.Encapsulate(corners[i]);
                b = b.HasValue ? Encapsulate(b.Value, wb) : wb;
            }
            return b ?? new Bounds(Vector3.zero, Vector3.zero);
        }

        static Bounds Encapsulate(Bounds a, Bounds bb) { a.Encapsulate(bb); return a; }
    }
}

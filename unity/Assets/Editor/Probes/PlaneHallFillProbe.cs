using System.Collections.Generic;
using System.Linq;
using TbilisiFly.Data;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves PlaneHallFill.cs actually parks the 35-plane fleet where
    /// PlaneHallLayout.Pack/RankDepths/PlaceRanks (already-verified pure
    /// math) say it should. This is the single most important probe for the
    /// whole task: it builds a REAL plane hall with ShedFill.BuildPlaneHall,
    /// calls the REAL PlaneHallFill.Fill, then independently re-runs
    /// Pack/RankDepths/PlaceRanks on the SAME measured spans/lens for each
    /// group and diffs every parked plane's local position against that
    /// hand-computation.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.PlaneHallFillProbe.Run
    /// </summary>
    public static class PlaneHallFillProbe
    {
        public static void Run()
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                var city = CityData.Load(null, buildings: false);
                var meta = new PlaneHallLayout.GeoMeta
                {
                    OriginLat = city.OriginLat, OriginLon = city.OriginLon,
                    MLat = city.MLat, MLon = city.MLon,
                };
                System.Func<float, float, float?> heightAt = (x, z) => city.Terrain.HeightAtGame(x, z);

                var (hx, hz, _, _, _, _) = PlaneHallLayout.Site(meta, ShedLayout.ShedYaw);
                var hall = ShedFill.BuildPlaneHall(null, hx, hz, ShedLayout.ShedYaw, heightAt);
                Debug.Log($"HALL id=_hall w={hall.Shell.Width:F3} l={hall.Shell.Length:F3} " +
                    $"pos_x={hall.Root.transform.position.x:F3} pos_z={hall.Root.transform.position.z:F3} " +
                    $"yawDeg={hall.Root.transform.localEulerAngles.y:F4}");

                var fill = PlaneHallFill.Fill(hall);
                Debug.Log($"SUMMARY id=_summary parked={fill.Parked.Count} failed={fill.Failed.Count} " +
                    $"tanks={fill.TankCount} dps={fill.DpsCount} tankdepth={fill.TankDepth:F3} dpsdepth={fill.DpsDepth:F3} " +
                    $"expected=35 ok={(fill.Parked.Count == 35 && fill.Failed.Count == 0 ? 1 : 0)}");
                foreach (var id in fill.Failed) Debug.Log($"FAILED id={id}");

                float halfW = hall.Shell.Width / 2f - PlaneHallLayout.Wall;
                float halfL = hall.Shell.Length / 2f - PlaneHallLayout.End;
                float strip = halfW - PlaneHallLayout.Lane / 2f;

                var tanks = fill.Parked.Where(p => Planes.IsTankPlane(p.Def)).ToList();
                var rest = fill.Parked.Where(p => !Planes.IsTankPlane(p.Def)).ToList();

                int tankMismatches = CheckGroup("tank", tanks, strip, -halfL, +1, Mathf.PI);
                int dpsMismatches = CheckGroup("dps", rest, strip, +halfL, -1, 0f);
                Debug.Log($"POSITIONCHECK id=_positioncheck mismatches={tankMismatches + dpsMismatches} " +
                    $"checked={tanks.Count + rest.Count}");

                foreach (var p in fill.Parked)
                {
                    var bc = p.GameObject.GetComponent<BoxCollider>();
                    var rig = p.GameObject.GetComponent<TbilisiFly.Character.PlaneCockpitRig>();
                    Debug.Log($"PLANE id={p.Id} tank={(Planes.IsTankPlane(p.Def) ? 1 : 0)} " +
                        $"span={p.Span:F3} len={p.Len:F3} " +
                        $"localX={p.GameObject.transform.localPosition.x:F4} localY={p.GameObject.transform.localPosition.y:F4} " +
                        $"localZ={p.GameObject.transform.localPosition.z:F4} yawDeg={p.GameObject.transform.localEulerAngles.y:F3} " +
                        $"worldX={p.GameObject.transform.position.x:F3} worldZ={p.GameObject.transform.position.z:F3} " +
                        $"hasCollider={(bc != null ? 1 : 0)} shadowOff={ShadowOffCheck(p.GameObject)} " +
                        $"hasCockpitRig={(rig != null ? 1 : 0)} hasCockpit={(rig != null && rig.CockpitValue.HasValue ? 1 : 0)} " +
                        $"hasKit={(p.CockpitKit != null ? 1 : 0)} shellSlabs={(rig != null && rig.ShellValue != null ? rig.ShellValue.GetComponents<BoxCollider>().Length : 0)} " +
                        $"controlsNull={(rig == null || rig.ControlsValue == null ? 1 : 0)}"); // flyable:false -> controls must be null, scenery only
                }

                // =========================================================
                // THE PACKING, CHECKED AS GEOMETRY AND NOT AS ARITHMETIC.
                //
                // POSITIONCHECK above re-runs Pack/RankDepths/PlaceRanks and
                // proves the port agrees with its own maths. That is not the
                // same claim as "the 35 machines do not overlap and do not
                // stick out through the shed" — src/planehall.js:278's
                // `sgn * (LANE/2 + at + p.span/2)` only puts a plane in its
                // slot if the plane's ORIGIN is the midpoint of its span, and
                // whether that is true is a property of
                // Flight/PlaneModelFrame (planes.js:1533-1535's X/Z recentre),
                // not of the layout maths. Added with that recentre
                // (docs/open_items.json `xz-recentre-deviation`, 2026-10-31)
                // so a regression in either shows up here as real metres.
                //
                // HALLCENTRE is the direct statement of the assumption: the
                // airframe's own vertex-precise AABB centre, in its own local
                // frame, must be 0 in X and Z.
                int offCentre = 0;
                foreach (var p in fill.Parked)
                {
                    bool ok = Mathf.Abs(p.LocalBounds.center.x) < 0.01f && Mathf.Abs(p.LocalBounds.center.z) < 0.01f;
                    if (!ok) offCentre++;
                    Debug.Log($"HALLCENTRE id={p.Id} ctrX={p.LocalBounds.center.x:F4} ctrZ={p.LocalBounds.center.z:F4} " +
                        $"minY={p.LocalBounds.min.y:F4} centred={ok.ToString().ToLowerInvariant()}");
                }
                Debug.Log($"HALLCENTRESUM planes={fill.Parked.Count} offCentre={offCentre} " +
                    $"pass={(offCentre == 0).ToString().ToLowerInvariant()}");

                // The footprints themselves, in hall-local X/Z. A parked yaw
                // is 0 or 180 deg, and 180 about Y maps (x,z) -> (-x,-z), so
                // the rotated AABB is the same size with a mirrored centre.
                var rects = new List<(string Id, float X0, float X1, float Z0, float Z1)>();
                foreach (var p in fill.Parked)
                {
                    var t = p.GameObject.transform;
                    bool flipped = Mathf.Abs(Mathf.DeltaAngle(t.localEulerAngles.y, 180f)) < 1f;
                    float cx = (flipped ? -p.LocalBounds.center.x : p.LocalBounds.center.x) + t.localPosition.x;
                    float cz = (flipped ? -p.LocalBounds.center.z : p.LocalBounds.center.z) + t.localPosition.z;
                    rects.Add((p.Id, cx - p.Span / 2f, cx + p.Span / 2f, cz - p.Len / 2f, cz + p.Len / 2f));
                }

                int overlaps = 0; float worst = 0f; string worstPair = "-";
                for (int a = 0; a < rects.Count; a++)
                    for (int b2 = a + 1; b2 < rects.Count; b2++)
                    {
                        float ox = Mathf.Min(rects[a].X1, rects[b2].X1) - Mathf.Max(rects[a].X0, rects[b2].X0);
                        float oz = Mathf.Min(rects[a].Z1, rects[b2].Z1) - Mathf.Max(rects[a].Z0, rects[b2].Z0);
                        if (ox <= 0.01f || oz <= 0.01f) continue;   // 1 cm slop
                        float pen = Mathf.Min(ox, oz);
                        overlaps++;
                        if (pen > worst) { worst = pen; worstPair = rects[a].Id + "/" + rects[b2].Id; }
                        Debug.Log($"HALLOVERLAP a={rects[a].Id} b={rects[b2].Id} ox={ox:F3} oz={oz:F3} pen={pen:F3}");
                    }
                Debug.Log($"HALLOVERLAPSUM pairs={rects.Count * (rects.Count - 1) / 2} overlaps={overlaps} " +
                    $"worstPen={worst:F3} worstPair={worstPair} pass={(overlaps == 0).ToString().ToLowerInvariant()}");

                // ...and nothing sticks out through a wall. The shed's own
                // half extents, with PlaneHallFill's own Wall/End margins —
                // the same `halfW`/`halfL` the fill warns on.
                int outside = 0; float worstOut = 0f; string worstOutId = "-";
                foreach (var r in rects)
                {
                    float outX = Mathf.Max(Mathf.Abs(r.X0), Mathf.Abs(r.X1)) - halfW;
                    float outZ = Mathf.Max(Mathf.Abs(r.Z0), Mathf.Abs(r.Z1)) - halfL;
                    float o = Mathf.Max(outX, outZ);
                    bool inside = o <= 0.01f;
                    if (!inside)
                    {
                        outside++;
                        if (o > worstOut) { worstOut = o; worstOutId = r.Id; }
                        Debug.Log($"HALLOUTSIDE id={r.Id} outX={outX:F3} outZ={outZ:F3}");
                    }
                }
                Debug.Log($"HALLINSIDE planes={rects.Count} halfW={halfW:F3} halfL={halfL:F3} outside={outside} " +
                    $"worstOut={worstOut:F3} worstOutId={worstOutId} pass={(outside == 0).ToString().ToLowerInvariant()}");

                // PlaneHallVisibility._cockpits (src/planehall.js:196-219) — prove
                // the distance toggle this pass wired actually flips kits by
                // camera distance, using the SAME Cockpits[] PlaneHallFill.Fill
                // just built.
                var vis = hall.Root.GetComponent<PlaneHallVisibility>();
                int kitsWithGeo = vis.Cockpits.Count(c => c.Kit != null);
                Debug.Log($"COCKPITTOGGLE id=_toggle cockpitEntries={vis.Cockpits.Length} kitsWithGeo={kitsWithGeo}");
                if (vis.Cockpits.Length > 0)
                {
                    var c0 = vis.Cockpits[0];
                    Vector3 near = c0.PlanePos + new Vector3(10f, 0f, 0f);  // well inside KitDist=90
                    Vector3 far = c0.PlanePos + new Vector3(500f, 0f, 0f); // well outside
                    vis.UpdateCockpits(near);
                    bool onNear = c0.Kit.activeSelf;
                    vis.UpdateCockpits(far);
                    bool onFar = c0.Kit.activeSelf;
                    Debug.Log($"COCKPITTOGGLE id={fill.Parked[0].Id} onNear={onNear.ToString().ToLowerInvariant()} " +
                        $"onFar={onFar.ToString().ToLowerInvariant()} correct={(onNear && !onFar ? 1 : 0)}");
                }

                // The packing verdict is now part of this probe's exit code, so
                // an off-centre origin or a wall-crossing wingtip cannot read
                // as a pass just because POSITIONCHECK agreed with itself.
                ProbeExit.Exit(offCentre == 0 && overlaps == 0 && outside == 0 ? 0 : 1);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"PLANEHALLFILL FAILED: {e}");
                ProbeExit.Exit(1);
            }
        }

        static int ShadowOffCheck(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) return 0;
            return 1;
        }

        /// <summary>Independently re-runs Pack/RankDepths/PlaceRanks on the
        /// SAME measured (span, len) pairs PlaneHallFill itself measured, and
        /// diffs the result against each plane's actual local position.
        /// Valid regardless of using a different Tag type (string id here vs
        /// the GameObject PlaneHallFill used): Pack's sort is stable and the
        /// input (fill.Parked, filtered by role) is already in the
        /// Len-descending order the original Pack call produced, so
        /// re-sorting it is a no-op and reproduces identical rank/side/at
        /// assignments.</summary>
        static int CheckGroup(string id, List<PlaneHallFill.ParkedPlane> group, float strip, float fromZ, int dir, float yawGame)
        {
            if (group.Count == 0) { Debug.Log($"GROUPCHECK id={id} items=0 mismatches=0"); return 0; }

            var items = group.Select(p => new PlaneHallLayout.PackItem { Tag = p.Id, Span = p.Span, Len = p.Len }).ToList();
            var placed = PlaneHallLayout.Pack(items, strip);
            var depths = PlaneHallLayout.RankDepths(items, placed);
            var (positions, totalDepth) = PlaneHallLayout.PlaceRanks(placed, depths, fromZ, dir);

            var placedByTag = placed.ToDictionary(p => (string)p.Tag, p => p);
            var posByTag = positions.ToDictionary(p => (string)p.tag, p => p);
            float yawUnityDeg = GeoFrame.YawToUnity(yawGame) * Mathf.Rad2Deg;

            int mismatches = 0;
            foreach (var p in group)
            {
                var pl = placedByTag[p.Id];
                var pos = posByTag[p.Id];
                float sgn = pl.Side == 0 ? -1f : 1f;
                // src/planehall.js:278 sgn*(LANE/2+at+span/2); PlaceRanks's x
                // already carries (LANE/2+at), caller adds the +/- span/2.
                float xGameExpected = pos.x + sgn * p.Span / 2f;
                float xUnityExpected = -xGameExpected; // mirror — see PlaneHallFill.cs's header derivation
                float zExpected = pos.z;
                float yawExpected = Mathf.DeltaAngle(0f, yawUnityDeg); // normalise -180 vs 180

                // Read off the REAL Transform, not the struct field it was
                // derived from — this is the actual proof that the GameObject
                // Unity has in the scene sits where the hand-computation says.
                Vector3 actual = p.GameObject.transform.localPosition;
                float dx = Mathf.Abs(actual.x - xUnityExpected);
                float dz = Mathf.Abs(actual.z - zExpected);
                float dyaw = Mathf.Abs(Mathf.DeltaAngle(p.GameObject.transform.localEulerAngles.y, yawExpected));
                bool bad = dx > 1e-3f || dz > 1e-3f || dyaw > 1e-2f;
                if (bad) mismatches++;
                Debug.Log($"HANDCHECK id={p.Id} group={id} expected_x={xUnityExpected:F5} actual_x={actual.x:F5} " +
                    $"expected_z={zExpected:F5} actual_z={actual.z:F5} dx={dx:F6} dz={dz:F6} dyaw={dyaw:F4} " +
                    $"bad={(bad ? 1 : 0)}");
            }
            Debug.Log($"GROUPCHECK id={id} items={group.Count} mismatches={mismatches} totalDepth={totalDepth:F3}");
            return mismatches;
        }
    }
}

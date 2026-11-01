using System;
using System.Collections.Generic;
using System.Linq;
using TbilisiFly.Combat;
using TbilisiFly.Core;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// THE TWO FLIGHT ITEMS OF 2026-10-31, each with the thing it was held
    /// back for measured rather than argued:
    ///
    /// 1. `xz-recentre-deviation` (docs/open_items.json) — src/planes.js:1533-1535
    ///    recentres EVERY airframe in X/Z; the port did it only inside
    ///    Flight/PlaneModelFrame's `fix != identity` branch. Closed in
    ///    PlaneModelFrame.RecentreXZ. The three hand-measured consumers the
    ///    deviation note named are re-measured here:
    ///      RECENTRE  — the airframe's own AABB centre in the FLIGHT frame is
    ///                  now 0 in X and Z (and the belly still -1.7).
    ///      MUZZLEFIT — Combat/GunPod.MuzzleLocal (src/guns.js:417) against
    ///                  that airframe: on the centreline, inside the length,
    ///                  and how far it actually sits from the skin.
    ///      SHELLFIT  — Flight/PlaneCockpitKit.BuildPlaneShell (planes.js:1418),
    ///                  whose fuselage/wing split is `Mathf.Abs(p.x) <= fw`
    ///                  and therefore ASSUMES the centreline is x = 0.
    ///    The third, the hall packing, is in Editor/Probes/PlaneHallFillProbe.cs
    ///    (HALLCENTRE / HALLOVERLAPSUM / HALLINSIDE), chained below so one
    ///    editor launch answers all of it.
    ///
    /// 2. `crane-plane-no-record` — src/crane.js:361
    ///    `citymap.targets?.register(plane, def, shell)` was unported, so the
    ///    crane-held P-51 in the warehouse was not clickable, had no card and
    ///    no hull points. Closed in Props/WarehouseCraneFill.HangPlane.
    ///    CRANETGT* below selects it through the game's OWN paths (Tab ->
    ///    TargetRegistry.Cycle, click -> PickAt), reads the REAL card's
    ///    Georgian labels back, lands a round on its skin through the REAL
    ///    Combat/PlaneDamage.Refine, and asserts Enter still refuses it.
    ///
    ///   U=/Applications/Unity/Hub/Editor/6000.6.7f1/Unity.app/Contents/MacOS/Unity
    ///   "$U" -batchmode -quit -nographics -projectPath unity \
    ///        -executeMethod TbilisiFly.Probes.FlightXzCraneProbe.All \
    ///        -logFile /dev/stdout
    /// </summary>
    public static class FlightXzCraneProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";

        public static void All()
        {
            ProbeExit.Suppress = true;
            bool ok = Recentre();
            // The packing third of item 1, plus the pure layout maths that
            // must not have moved.
            PlaneHallLayoutProbe.Run();
            PlaneHallFillProbe.Run();
            // Item 2 needs the real booting scene, so it goes last.
            ok &= CraneTarget();
            ProbeExit.Suppress = false;
            Debug.Log($"XZCRANESUITE ok={ok.ToString().ToLowerInvariant()} " +
                      $"chainedNonZero={ProbeExit.LastNonZero}");
            EditorApplication.Exit(ok && ProbeExit.LastNonZero == 0 ? 0 : 1);
        }

        public static void Run() { All(); }

        // =====================================================================
        // ITEM 1 — the recentre, and the two consumers measured in the plane's
        // own frame (the third is the hall, in PlaneHallFillProbe).
        // =====================================================================
        public static bool Recentre()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            int planes = 0, centred = 0, seated = 0, muzzleOnCentre = 0, muzzleInLen = 0, shellCentred = 0;
            var offCentre = new List<string>();

            foreach (var def in Planes.All)
            {
                string path = def.Id == "spitfire"
                    ? PlaneHallFill.SpitfireObjPath
                    : PlaneHallFill.PlanesFbxDir + def.Id + ".fbx";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) { Debug.Log($"RECENTRE id={def.Id} asset=missing"); continue; }

                var inner = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                var go = new GameObject(def.Id);
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var frames = PlaneModelFrame.Attach(go.transform, inner, def);
                if (!frames.Valid) { Debug.Log($"RECENTRE id={def.Id} chain=invalid"); UnityEngine.Object.DestroyImmediate(go); continue; }

                // THE FLIGHT FRAME, which is the frame GunPod.MuzzleLocal,
                // PlaneHallFill's packing and FootBoarding all work in.
                var pts = Verts(go.transform, go.transform);
                if (pts.Count == 0) { Debug.Log($"RECENTRE id={def.Id} verts=0"); UnityEngine.Object.DestroyImmediate(go); continue; }
                Bounds box = Box(pts);
                planes++;

                // ---- RECENTRE: planes.js:1533-1535's whole observable effect.
                // A tolerance and not an equality: the box is walked twice in
                // two different frames (RecentreXZ measures in `model_scale`,
                // this measures in `planeRoot`) through two float32 matrices,
                // and the fleet spans 8.5 m to 84 m.
                bool c = Mathf.Abs(box.center.x) < 0.01f && Mathf.Abs(box.center.z) < 0.01f;
                bool s = Mathf.Abs(box.min.y - PlaneModelFrame.BellyLocalY) < 0.01f;
                if (c) centred++; else offCentre.Add(def.Id);
                if (s) seated++;
                Debug.Log($"RECENTRE id={def.Id} ctrX={box.center.x:F4} ctrZ={box.center.z:F4} " +
                          $"minY={box.min.y:F4} wantMinY={PlaneModelFrame.BellyLocalY:F4} " +
                          $"span={box.size.x:F2} len={box.size.z:F2} " +
                          $"centred={c.ToString().ToLowerInvariant()} seated={s.ToString().ToLowerInvariant()}");

                // ---- MUZZLEFIT: src/guns.js:409/417, hand-measured on the JS
                // wrapper. The muzzle is supposed to be slung UNDER the
                // fuselage on the centreline, pointing out of the nose — all
                // three of which are statements about an origin that is the
                // airframe's centre, which is what this change makes true.
                Vector3 mz = GunPod.MuzzleLocal;
                float offC = Mathf.Abs(mz.x - box.center.x);
                bool onCentre = offC < 0.01f;
                bool inLen = mz.z > box.min.z && mz.z < box.max.z;
                if (onCentre) muzzleOnCentre++;
                if (inLen) muzzleInLen++;
                float near = float.PositiveInfinity;
                for (int i = 0; i < pts.Count; i += Mathf.Max(1, pts.Count / 20000))
                    near = Mathf.Min(near, Vector3.Distance(pts[i], mz));
                Debug.Log($"MUZZLEFIT id={def.Id} mx={mz.x:F2} my={mz.y:F2} mz={mz.z:F2} " +
                          $"offCentreX={offC:F4} onCentre={onCentre.ToString().ToLowerInvariant()} " +
                          $"inLenZ={inLen.ToString().ToLowerInvariant()} " +
                          $"nearestSkinM={near:F3} bellyY={box.min.y:F2} noseZ={box.max.z:F2}");

                // ---- SHELLFIT: the box shell the hero stands on. Built on
                // `model_frame` exactly as PlaneCockpitBaker builds it, and
                // measured there. Its union must straddle the centreline,
                // because its fuselage slabs are authored as (-cx .. +cx).
                var shell = TbilisiFly.Flight.PlaneCockpitKit.BuildPlaneShell(frames.Frame);
                int slabs = 0; float ucx = float.NaN, usx = float.NaN;
                if (shell != null)
                {
                    var cols = shell.GetComponents<BoxCollider>();
                    slabs = cols.Length;
                    if (slabs > 0)
                    {
                        var ub = new Bounds(cols[0].center, cols[0].size);
                        for (int i = 1; i < slabs; i++) ub.Encapsulate(new Bounds(cols[i].center, cols[i].size));
                        ucx = ub.center.x; usx = ub.size.x;
                    }
                }
                // model_frame is the flight frame yawed 180 deg about Y, so the
                // span is the same number and the centre's sign flips — either
                // way "straddles the centreline" is |ctr| small next to span.
                bool shellOk = slabs > 0 && Mathf.Abs(ucx) < Mathf.Max(0.25f, usx * 0.04f);
                if (shellOk) shellCentred++;
                Debug.Log($"SHELLFIT id={def.Id} slabs={slabs} unionCtrX={ucx:F4} unionSpanX={usx:F3} " +
                          $"airSpanX={box.size.x:F3} straddles={shellOk.ToString().ToLowerInvariant()}");

                UnityEngine.Object.DestroyImmediate(go);
            }

            bool pass = planes > 0 && centred == planes && seated == planes
                        && muzzleOnCentre == planes && shellCentred == planes;
            Debug.Log($"RECENTRESUM planes={planes} centred={centred} seated={seated} " +
                      $"muzzleOnCentre={muzzleOnCentre} muzzleInLen={muzzleInLen} " +
                      $"shellStraddles={shellCentred} " +
                      $"offCentre={offCentre.Count}{(offCentre.Count > 0 ? " ids=" + string.Join(",", offCentre) : "")} " +
                      $"pass={pass.ToString().ToLowerInvariant()}");
            return pass;
        }

        // =====================================================================
        // ITEM 2 — the slung P-51 is a target with hull points, takes real
        // gunfire, and still refuses Enter.
        // =====================================================================
        public static bool CraneTarget()
        {
            bool pass = true;
            void Check(string key, bool cond, string note = null)
            {
                Debug.Log($"{(cond ? "PASS" : "FAIL")} {key}{(!cond && note != null ? $" ({note})" : "")}");
                if (!cond) pass = false;
            }

            UnityEngine.SceneManagement.Scene scene = default;
            bool openOk = true;
            try { scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single); }
            catch (Exception e) { openOk = false; Debug.LogException(e); }
            Check("craneSceneLoads", openOk && scene.IsValid());
            if (!openOk || !scene.IsValid()) return false;

            // ShedSceneFill builds the warehouse/crane/hall in OnEnable (it is
            // [ExecuteAlways] and nothing about this is baked), so opening the
            // scene is what runs WarehouseCraneFill.Build for real.
            var shed = UnityEngine.Object.FindFirstObjectByType<ShedSceneFill>();
            Check("shedSceneFillPresent", shed != null, "Game.unity has no ShedSceneFill — no crane at all");
            if (shed == null) return false;
            shed.EnsureBuilt();
            var planeGo = shed.Crane.PlaneGo;
            Debug.Log($"CRANETGT id=_build craneBuilt={shed.Crane.Built.ToString().ToLowerInvariant()} " +
                      $"planeGo={(planeGo != null ? planeGo.name : "null")} " +
                      $"hallParked={(shed.Fleet.Parked != null ? shed.Fleet.Parked.Count : 0)}");
            Check("cranePlaneExists", planeGo != null);
            if (planeGo == null) return false;

            var rec = planeGo.GetComponent<PlaneDamageRecord>();
            var noBoard = planeGo.GetComponent<TbilisiFly.Character.NoBoardMarker>();
            Debug.Log($"CRANETGT id={planeGo.name} record={(rec != null ? 1 : 0)} " +
                      $"noBoard={(noBoard != null ? 1 : 0)} " +
                      $"hp={(rec != null ? rec.MaxHullPoints : 0f):F0} " +
                      $"halfSpan={(rec != null ? rec.HalfSpan : 0f):F3} " +
                      $"size={(rec != null ? rec.Size : 0f):F3} noseLen={(rec != null ? rec.NoseLen : 0f):F3} " +
                      $"name=\"{(rec != null ? rec.DisplayName : "-")}\"");
            Check("cranePlaneHasRecord", rec != null, "src/crane.js:361 register() still unported");
            Check("cranePlaneHasHullPoints", rec != null && rec.MaxHullPoints > 0f);
            Check("cranePlaneStillNoBoard", noBoard != null, "Enter could climb a wing root 4 m overhead");
            if (rec == null) return false;

            // ---- it is in the LIVE registry the game's own sweep fills ------
            var game = UnityEngine.Object.FindFirstObjectByType<GameLoop>();
            Check("gameLoopExists", game != null);
            if (game == null) return false;
            var boot = UnityEngine.Object.FindFirstObjectByType<PlayableSceneBoot>();
            try { boot?.Run(); } catch (Exception e) { Debug.LogException(e); }
            for (int i = 0; i < 10; i++) { try { game.Tick(1f / 60f); } catch (Exception e) { Debug.LogException(e); break; } }

            var reg = game.Targets;
            int records = UnityEngine.Object.FindObjectsByType<PlaneDamageRecord>(FindObjectsSortMode.None).Length;
            var entry = reg.List.FirstOrDefault(e => e.Wrapper == planeGo.transform);
            Debug.Log($"CRANETGT id=_registry planeDamageRecords={records} registryEntries={reg.List.Count} " +
                      $"craneEntry={(entry != null ? 1 : 0)} " +
                      $"hallExpected={(shed.Fleet.Parked != null ? shed.Fleet.Parked.Count : 0)}+crane1");
            Check("craneInRegistry", entry != null,
                "CombatWorldAdapter's FindObjectsByType<PlaneDamageRecord> sweep missed it");
            if (entry == null) return false;
            Check("craneRegistryHp", entry.MaxHp > 0 && entry.HpGetter() > 0);
            Check("craneRegistryHalfSpan", entry.HalfSpan.HasValue && entry.HalfSpan.Value > 0f);

            // ---- Tab selects it (GameLoop.cs:2094 _targets.Cycle) ----------
            reg.Clear();
            bool tabbed = false;
            for (int i = 0; i < reg.List.Count + 2 && !tabbed; i++)
            {
                reg.Cycle(game.MainCamera);
                tabbed = ReferenceEquals(reg.Current, entry);
            }
            Check("tabSelectsCranePlane", tabbed);

            // ---- the REAL card, read back off its own labels ---------------
            if (game.TargetCard != null)
            {
                reg.Tick(1f / 60f, game.TargetCard);
                string nameTxt = game.TargetCard.Q<Label>("tf-name")?.text ?? "";
                string hpTxt = game.TargetCard.Q<Label>("tf-hp")?.text ?? "";
                string roleTxt = game.TargetCard.Q<Label>("tf-role")?.text ?? "";
                Debug.Log($"CRANETGT id=_card name=\"{nameTxt}\" hp=\"{hpTxt}\" role=\"{roleTxt}\"");
                Check("cardShowsCranePlaneName", nameTxt == entry.Name && !string.IsNullOrEmpty(nameTxt));
                Check("cardShowsCranePlaneHp", hpTxt == $"{entry.HpGetter()} / {entry.MaxHp}", $"got \"{hpTxt}\"");
            }
            else Debug.Log("CRANETGT id=_card view=absent");

            // ---- a click picks it (GameLoop.cs:2098 _targets.PickAt) -------
            var cam = game.MainCamera;
            bool clicked = false;
            if (cam != null)
            {
                Vector3 at = planeGo.transform.position;
                foreach (float back in new[] { 14f, 25f, 40f })
                {
                    cam.transform.position = at + new Vector3(0f, 1f, -back);
                    cam.transform.rotation = Quaternion.LookRotation((at - cam.transform.position).normalized, Vector3.up);
                    reg.Clear();
                    bool okPick = reg.PickAt(cam, new Vector2(cam.pixelWidth * 0.5f, cam.pixelHeight * 0.5f));
                    Debug.Log($"CRANETGT id=_clickTry backM={back:F0} picked={okPick.ToString().ToLowerInvariant()} " +
                              $"current={(reg.Current != null ? reg.Current.Id : "none")}");
                    if (okPick && ReferenceEquals(reg.Current, entry)) { clicked = true; break; }
                }
            }
            Check("clickSelectsCranePlane", clicked);

            // ---- A ROUND LANDS ON ITS SKIN ---------------------------------
            // The same question Editor/Probes/PlaneLiveryProbe.Damage asks of
            // the 35 parked machines: the crude hit is on the box collider,
            // and PlaneDamage.Refine must come back with a DIFFERENT collider
            // — a real mesh of the real airframe. "refine must never return
            // the collider-box hit" (docs: plane damage, 2026-10).
            var pd = new PlaneDamage();
            pd.Watch(rec);
            var boxCol = planeGo.GetComponent<BoxCollider>();
            Check("cranePlaneHasBoxCollider", boxCol != null);
            int tried = 0, boxHits = 0, refined = 0;
            string firstPart = "null";
            if (boxCol != null)
            {
                Bounds wb = new Bounds(planeGo.transform.TransformPoint(boxCol.center),
                    Vector3.Scale(boxCol.size, Abs(planeGo.transform.lossyScale)));
                for (int ix = -2; ix <= 2; ix++)
                    for (int iz = -2; iz <= 2; iz++)
                    {
                        tried++;
                        var p = new Vector3(wb.center.x + wb.extents.x * 0.6f * ix / 2f,
                                            wb.max.y + 40f,
                                            wb.center.z + wb.extents.z * 0.6f * iz / 2f);
                        var ray = new Ray(p, Vector3.down);
                        if (!boxCol.Raycast(ray, out RaycastHit crude, 300f)) continue;
                        boxHits++;
                        var r = pd.Refine(crude, Vector3.down);
                        if (r == null || r.Value.collider == boxCol) continue;
                        refined++;
                        if (firstPart == "null") firstPart = r.Value.collider.name;
                    }
            }
            Debug.Log($"CRANETGT id=_hit stations={tried} boxHits={boxHits} refined={refined} part={firstPart}");
            Check("roundLandsOnCraneSkin", refined > 0,
                "PlaneDamage.Refine never left the box collider — the slung machine still is not shootable");
            pd.Dispose();

            // ---- and Enter still refuses it --------------------------------
            // FootBoarding.NearestPlane()'s own rule, applied to the live
            // registry entry (Character/FootBoarding.cs:420
            // `if (w.GetComponent<NoBoardMarker>() != null) continue;`).
            bool refused = entry.Wrapper.GetComponent<TbilisiFly.Character.NoBoardMarker>() != null;
            Debug.Log($"CRANETGT id=_board registryEntry=1 noBoardOnWrapper={refused.ToString().ToLowerInvariant()} " +
                      $"=> NearestPlane skips it");
            Check("enterStillRefusesCranePlane", refused);

            Debug.Log(pass ? "CRANETGT RESULT=PASS" : "CRANETGT RESULT=FAIL");
            return pass;
        }

        // =====================================================================
        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        /// <summary>Vertex-precise, posed, in `frame`'s local space — the same
        /// walk Flight/PlaneCockpitMeasurement.BoxOf does, kept local so this
        /// probe's numbers do not come from the code under test.</summary>
        static List<Vector3> Verts(Transform root, Transform frame)
        {
            var outp = new List<Vector3>();
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var m = mf.transform.localToWorldMatrix;
                foreach (var v in mf.sharedMesh.vertices)
                    outp.Add(frame.InverseTransformPoint(m.MultiplyPoint3x4(v)));
            }
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                var baked = new Mesh();
                smr.BakeMesh(baked, true);
                var m = smr.transform.localToWorldMatrix;
                foreach (var v in baked.vertices)
                    outp.Add(frame.InverseTransformPoint(m.MultiplyPoint3x4(v)));
                UnityEngine.Object.DestroyImmediate(baked);
            }
            return outp;
        }

        static Bounds Box(List<Vector3> pts)
        {
            var b = new Bounds(pts[0], Vector3.zero);
            for (int i = 1; i < pts.Count; i++) b.Encapsulate(pts[i]);
            return b;
        }
    }
}

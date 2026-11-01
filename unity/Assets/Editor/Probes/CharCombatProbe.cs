using TbilisiFly.Character;
using TbilisiFly.Combat;
using TbilisiFly.UI.Target;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves this pass's four closed gaps:
    ///   A. Character/ShoulderPocket.cs — the real per-vertex rifleShoulder
    ///      ellipse fit (characters.js:927 _measurePocket), and a verification
    ///      that Character/FootPoses.cs's SetSeated (characters.js:1250/1396
    ///      setSeated/_poseSeated) ALREADY bends real thigh/calf bones — this
    ///      pass found that done (by another agent) rather than doing it
    ///      itself; see docs/port_units/charcombat.json for the honest split.
    ///   B. Combat/DamageDecals.cs — the procedural canvas decal art
    ///      (src/planedamage.js:1668-1998, src/glass.js:34-85) actually draws
    ///      non-transparent pixels, is not a blank texture.
    ///   C. Combat/TargetRegistry.cs + UI/Target/TargetRingView.cs — the
    ///      registry/pick/cycle/ring half of src/target.js the UI pass left
    ///      as Combat territory.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CharCombatProbe.Run
    ///
    /// tools/_probe_charcombat.mjs prints the matching JS-side facts (module-
    /// private constants transcribed with their source line cited, same
    /// precedent as tools/_probe_damage.mjs's tearRadius/bridgeOf) — diff the
    /// shared KEY lines with tools/unity_port_check.py. The per-vertex
    /// ellipse fit and the real leg-bend both need a LOADED SKINNED BODY,
    /// which the JS side has no Node-only way to do either (see
    /// tools/_probe_character.mjs's own "NOT comparable 1:1" note) — those
    /// facts are Unity-only, same honesty standard.
    /// </summary>
    public static class CharCombatProbe
    {
        static readonly string[] HumanoidBodies = { "banana", "eggplant", "toad" };

        public static void Run()
        {
            Debug.Log($"POCKETCONST id=_const pocketAheadRad={ShoulderPocket.PocketAhead:F3} " +
                      $"pocketProudM={ShoulderPocket.PocketProud:F4} pocketDropM={ShoulderPocket.PocketDrop:F3}");
            Debug.Log($"RINGCONST id=_const wheelHeightM={TargetRingMath.WheelHeight:F1}");
            RingMathFacts();
            ShoulderPocketAndSeatedFacts();
            DamageDecalFacts();
            TargetRegistryFacts();
            ProbeExit.Exit(0);
        }

        // =====================================================================
        // RING MATH — target.js:256-277/319-320's pure formulas (band width,
        // radius clamp, pulse/colour), diffable against tools/_probe_charcombat
        // .mjs's own transcription since neither side needs a scene for this.
        // =====================================================================
        static void RingMathFacts()
        {
            foreach (var (id, halfSpan) in new (string, float?)[] { ("spitfire", 5.5f), ("an225", 44f), ("default", null) })
            {
                float r = TargetRingMath.RingRadius(halfSpan);
                Debug.Log($"RING id={id} radiusM={r:F4} bandM={TargetRingMath.RingBandWidth(r):F4}");
            }
            foreach (float t in new[] { 0f, 0.5f, 1.2f })
            {
                Debug.Log($"PULSE id=t{t:F1}_alive alpha={TargetRingMath.PulseOpacity(false, t):F4}");
                Debug.Log($"PULSE id=t{t:F1}_dead alpha={TargetRingMath.PulseOpacity(true, t):F4}");
            }
        }

        // =====================================================================
        // A. SHOULDER POCKET (new) + SEATED LEG-BEND (verifying already-done work)
        // =====================================================================
        static void ShoulderPocketAndSeatedFacts()
        {
            foreach (var id in HumanoidBodies)
            {
                string path = $"Assets/Resources/Art/Characters/{id}.fbx";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) { Debug.Log($"POCKET id={id} missing=1"); continue; }

                var instance = Object.Instantiate(prefab);
                CharacterRig rig = null;
                try
                {
                    rig = new CharacterRig(instance, id);
                    if (!rig.IsHumanoid) { Debug.Log($"POCKET id={id} humanoid=0"); continue; }

                    foreach (var side in new[] { "R", "L" })
                    {
                        var pocket = rig.MeasureShoulderPocket(side);
                        if (!pocket.Valid)
                        {
                            Debug.Log($"POCKET id={id}_{side} valid=0");
                            continue;
                        }
                        Debug.Log($"POCKET id={id}_{side} valid=1 bone={pocket.Bone.name} " +
                                  $"rxM={pocket.Rx:F4} rzM={pocket.Rz:F4} " +
                                  $"cx={pocket.C.x:F4} cy={pocket.C.y:F4} cz={pocket.C.z:F4}");

                        // the resolved world pocket for two aims, as a DISTANCE from the
                        // shoulder bone so the number is frame-independent (reproducible
                        // regardless of where this instance sits in the scene).
                        var upBone = rig.Anim.GetBoneTransform(side == "L" ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
                        Vector3 fwdAim = rig.AnimGroup.forward;
                        Vector3 rightAim = rig.AnimGroup.right;
                        var pFwd = ShoulderPocket.Resolve(rig, pocket, side, fwdAim);
                        var pRight = ShoulderPocket.Resolve(rig, pocket, side, rightAim);
                        float dFwd = pFwd.HasValue ? Vector3.Distance(pFwd.Value, upBone.position) : -1f;
                        float dRight = pRight.HasValue ? Vector3.Distance(pRight.Value, upBone.position) : -1f;
                        Debug.Log($"POCKETRESOLVE id={id}_{side} aimFwdDistM={dFwd:F4} aimRightDistM={dRight:F4}");
                    }

                    // ---- SEATED LEG-BEND — verifying Character/FootPoses.cs's SetSeated,
                    // which this pass did NOT write (already present, complete, on the
                    // do-not-edit list) but reports on honestly: proves it is a REAL bend,
                    // not the static FreezePose/RestoreFrozen substitute Core/GameLoop.cs
                    // currently wires up instead (see charcombat.json).
                    var hip = rig.Anim.GetBoneTransform(HumanBodyBones.RightUpperLeg);
                    var knee = rig.Anim.GetBoneTransform(HumanBodyBones.RightLowerLeg);
                    if (hip != null && knee != null)
                    {
                        Quaternion hip0 = hip.localRotation, knee0 = knee.localRotation;
                        float hipHeight = FootPoses.SetSeated(rig, 1f);
                        float hipDeg = Quaternion.Angle(hip0, hip.localRotation);
                        float kneeDeg = Quaternion.Angle(knee0, knee.localRotation);
                        Debug.Log($"SEATED id={id} hipHeightM={hipHeight:F4} hipBendDeg={hipDeg:F3} kneeBendDeg={kneeDeg:F3}");
                        rig.ReleaseBones();
                    }
                }
                finally
                {
                    if (rig != null) Object.DestroyImmediate(rig.Root.gameObject);
                    else Object.DestroyImmediate(instance);
                }
            }
        }

        // =====================================================================
        // B. DAMAGE DECAL ART
        // =====================================================================
        static void DamageDecalFacts()
        {
            Report("hole_entry_pistol", DamageDecals.BuildHoleAtlas());
            Report("tear_merged_rifle", DamageDecals.BuildTearAtlas());
            Report("exit_bellygun", DamageDecals.BuildExitAtlas());
            Report("craze_perspex", DamageDecals.BuildCrazeAtlas());
            Report("scrape_glancing", DamageDecals.BuildScrapeTexture());
            Report("broken_pane_glass", DamageDecals.BuildBrokenPaneTexture());

            var shard = DamageDecals.BuildShardMesh();
            Debug.Log($"SHARDMESH id=glass_shard verts={shard.vertexCount} tris={shard.triangles.Length / 3}");
            Object.DestroyImmediate(shard);
        }

        static void Report(string id, Texture2D tex)
        {
            float frac = DamageDecals.NonTransparentFraction(tex);
            Debug.Log($"DECAL id={id} w={tex.width} h={tex.height} nonTransparentFrac={frac:F4}");
            Object.DestroyImmediate(tex);
        }

        // =====================================================================
        // C. TARGET REGISTRY (pick/cycle/damage) — pure-data facts, no Physics
        // scene needed (see Combat/TargetRegistry.cs's class header).
        // =====================================================================
        static void TargetRegistryFacts()
        {
            var reg = new TargetRegistry();

            // ---- RecOf: a hit transform NESTED under the registered collider's
            // GameObject must still resolve (the ray walks into the model's child
            // meshes, the record hangs off a PARENT) ----
            var planeRoot = new GameObject("plane_root");
            var col = planeRoot.AddComponent<BoxCollider>();
            var childMesh = new GameObject("child_mesh");
            childMesh.transform.SetParent(planeRoot.transform);
            var rec = reg.Register(planeRoot.transform, col, "p1", "Test Plane", "TST · ფიქო", null, false, 1000, halfSpan: 8f);

            var hitOnSelf = reg.RecOf(planeRoot.transform, null, null);
            var hitOnChild = reg.RecOf(childMesh.transform, null, null);
            var unrelated = new GameObject("unrelated");
            var hitUnrelated = reg.RecOf(unrelated.transform, null, null);
            Debug.Log($"RECOF id=self match={(ReferenceEquals(hitOnSelf, rec) ? 1 : 0)}");
            Debug.Log($"RECOF id=child_parentwalk match={(ReferenceEquals(hitOnChild, rec) ? 1 : 0)}");
            Debug.Log($"RECOF id=unrelated match={(hitUnrelated == null ? 1 : 0)}");

            // own-plane root test
            var ownRoot = new GameObject("own_root");
            var ownRec = reg.OwnRecord(ownRoot.transform, 800, 1000, "own");
            var hitOwn = reg.RecOf(ownRoot.transform, ownRoot.transform, ownRec);
            Debug.Log($"RECOF id=own match={(ReferenceEquals(hitOwn, ownRec) ? 1 : 0)}");

            // ---- Cycle: 4 synthetic targets around an eye looking +Z. Parented
            // under a container so `Wrapper.parent != null` reads as "still in the
            // world" (target.js:133) — a true scene root would otherwise read as
            // "not placed yet" and Cycle would skip every one of them. ----
            var container = new GameObject("cycle_container");
            var eye = Vector3.zero;
            var fwd = Vector3.forward;
            var near = Make("near", new Vector3(0, 0, 50), container.transform);     // in range, in cone -> picked FIRST (nearest)
            var far = Make("far", new Vector3(0, 0, 400), container.transform);      // in range, in cone, farther
            var behind = Make("behind", new Vector3(0, 0, -50), container.transform); // in range, OUT of cone (behind)
            var tooFar = Make("toofar", new Vector3(0, 0, 900), container.transform); // OUT of range
            var nearRec = reg.Register(near.transform, null, "near", "Near", "", null, false, 500);
            var farRec = reg.Register(far.transform, null, "far", "Far", "", null, false, 500);
            reg.Register(behind.transform, null, "behind", "Behind", "", null, false, 500);
            reg.Register(tooFar.transform, null, "toofar", "TooFar", "", null, false, 500);

            var c1 = reg.Cycle(eye, fwd);
            var c2 = reg.Cycle(eye, fwd);
            var c3 = reg.Cycle(eye, fwd); // wraps back to nearest
            Debug.Log($"CYCLE id=1 picked={c1?.Id ?? "null"} expected=near");
            Debug.Log($"CYCLE id=2 picked={c2?.Id ?? "null"} expected=far");
            Debug.Log($"CYCLE id=3 picked={c3?.Id ?? "null"} expected=near");

            // ---- DamagePlane: hp drains, dies at 0, fires OnKillFx ----
            bool killed = false; Vector3 killPos = default; float killR = -1f;
            reg.OnKillFx += (pos, r) => { killed = true; killPos = pos; killR = r; };
            reg.DamagePlane(nearRec, 200f);
            Debug.Log($"DAMAGE id=hp_after_200 hp={nearRec.Hp} maxHp={nearRec.MaxHp} dead={(nearRec.Dead ? 1 : 0)}");
            reg.DamagePlane(nearRec, 10000f);
            Debug.Log($"DAMAGE id=hp_after_lethal hp={nearRec.Hp} dead={(nearRec.Dead ? 1 : 0)} " +
                      $"killFxFired={(killed ? 1 : 0)} killRadius={killR:F3}");

            // own-machine damage floors at 1, never 0 (standing beside your own plane
            // and shooting it must not drop the death screen while on foot).
            int ownHp = 5;
            reg.DamagePlane(ownRec, 50f, () => ownHp, v => ownHp = v);
            Debug.Log($"DAMAGE id=own_floor ownHp={ownHp} expectedFloor=1");

            // ---- ComputePickRay: pointer-locked centres on screen; unlocked follows the pointer ----
            var camGO = new GameObject("probe_cam");
            var cam = camGO.AddComponent<Camera>();
            cam.pixelRect = new Rect(0, 0, 800, 600);
            var rLocked = TargetRegistry.ComputePickRay(cam, new Vector2(10, 10), true);
            var rFree = TargetRegistry.ComputePickRay(cam, new Vector2(10, 10), false);
            var rCenter = cam.ScreenPointToRay(new Vector2(400, 300));
            Debug.Log($"PICKRAY id=locked matchesCenter={(Vector3.Distance(rLocked.direction, rCenter.direction) < 1e-5f ? 1 : 0)}");
            Debug.Log($"PICKRAY id=free matchesCenter={(Vector3.Distance(rFree.direction, rCenter.direction) < 1e-5f ? 1 : 0)}");

            Debug.Log($"CONST id=_const pickRangeM={TargetRegistry.PickRange:F1} cycleRangeM={TargetRegistry.CycleRange:F1} cycleDot={TargetRegistry.CycleDot:F3}");

            foreach (var go in new[] { planeRoot, childMesh, unrelated, ownRoot, container, camGO })
                Object.DestroyImmediate(go);
        }

        static GameObject Make(string name, Vector3 pos, Transform parent = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.position = pos;
            return go;
        }
    }
}

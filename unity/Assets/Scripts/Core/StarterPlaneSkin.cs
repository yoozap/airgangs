using TbilisiFly.Combat;
using TbilisiFly.Flight;
using UnityEngine;

namespace TbilisiFly.Core
{
    // =========================================================================
    // StarterPlaneSkin.cs — EVERY HERO GETS THEIR OWN STARTER MACHINE.
    //
    // THE BUG. `Boot.StarterPlaneFor(raceId)` has been ported since the shell
    // landed: a race owns a role (veggie = DPS, toys = TANK — there is no plane
    // picking on the personage screen, project memory 2026-10-19) and a role
    // owns a starter aeroplane. `Boot.CreateHero` saves that id on the hero and
    // `StartFlightAsync` reads it back. And then:
    //
    //     PlaneControllers.FirstOrDefault(p => p.PlaneId == planeId)
    //         ?? PlaneControllers.FirstOrDefault()
    //
    // Assets/Editor/BuildPlayableScene.cs bakes exactly ONE PlaneController
    // (p51), so the `??` arm fired for every hero whose starter was anything
    // else and the whole starter-plane chain — the role split, the saved id,
    // the catalog row — decided nothing at all. docs/open_items.json
    // `starter-plane`.
    //
    // THE SHAPE OF THE FIX, and why it is not 35 controllers in the scene: one
    // aeroplane is flyable at a time, and a PlaneController carries a whole
    // flight rig (FlightModel, PlaneRaycastWorld, ground probe, ground roll,
    // hull damage, wall contact, plane physics). Thirty-five of those for one
    // flown machine is the opposite of this port's perf history. The path that
    // already exists is the Enter-takes-a-parked-plane path
    // (Core/GameLoop.SwapToPlane -> PlaneController.Init ->
    // Flight/PlaneModelFrame.Ensure), which re-initialises a controller against
    // a catalog row. This file does the same thing to the one controller we
    // have: swap its SKIN (model, cockpit furniture, box shell, damage record)
    // and re-initialise it against the hero's own def.
    //
    // Asset resolution is AssetDatabase and therefore Editor-only, which is the
    // established convention for every model in this port
    // (Flight/PlaneHallFill.cs loads all 35 the same way, City/CityRenderer.cs
    // its kit meshes, Boot.BodyPrefabFor the bodies) — there is no
    // Resources/Addressables pipeline here yet. In a player build, and whenever
    // the asset is missing, the hero keeps the baked machine rather than flying
    // an invisible one, and the reason is logged.
    // =========================================================================

    public static class StarterPlaneSkin
    {
        /// <summary>
        /// Re-skin <paramref name="pc"/> to <paramref name="planeId"/>. Returns
        /// true only if the controller is now that plane. A no-op (true) when it
        /// already is, false when the id is unknown or the asset cannot be
        /// resolved — in which case the caller must keep flying what it has.
        ///
        /// Call BEFORE GameLoop.BeginSession: BeginSession's WirePlaneCombat
        /// re-wires the hooks on the rig this rebuilds, and ResetPlane puts the
        /// new airframe on the spawn with its OWN belly offset.
        /// </summary>
        public static bool Apply(PlaneController pc, string planeId)
        {
            if (pc == null || string.IsNullOrEmpty(planeId)) return false;
            if (pc.PlaneId == planeId) return true;                 // already the right machine

            // Planes.PlaneById falls back to PLANES[0] on a miss (the JS's own
            // `planeById(id) ?? PLANES[0]`), so the answer's Id is what says
            // whether the row really exists — same test Boot.BodyPrefabFor uses
            // against CharacterRoster.
            var def = Planes.PlaneById(planeId);
            if (def == null || def.Id != planeId)
            {
                Debug.LogWarning($"StarterPlaneSkin: no plane row for '{planeId}' — keeping {pc.PlaneId}");
                return false;
            }

            GameObject prefab = LoadAirframe(def.Id);
            if (prefab == null)
            {
                Debug.LogWarning($"StarterPlaneSkin: no asset for '{def.Id}' — keeping {pc.PlaneId}");
                return false;
            }

            var root = pc.transform;

            // ---- 1. the old skin comes off ----------------------------------
            // DETACHED FIRST, then destroyed. Object.Destroy is deferred to the
            // end of the frame, and a Destroy()'d GameObject is STILL found by
            // transform.Find until then — so PlaneModelFrame.Of would hand
            // Attach the dying frame and the new model would be parented into a
            // corpse. Re-parenting to null takes it out of the lookup
            // immediately, whichever destroy flavour runs.
            //
            // The cockpit kit, the hinged hood and the measured box shell are
            // all children of `model_frame` (Flight/PlaneCockpitBaker.Bake
            // builds them under the measure frame), so this one detach takes the
            // whole P-51-shaped set of furniture with it. The gun pod lives on
            // the FLIGHT root instead and is frame-independent
            // (Combat/GunPod's muzzleLocal is a constant), so it stays.
            var old = PlaneModelFrame.Of(root);
            if (old.Frame != null)
            {
                old.Frame.SetParent(null, false);
                Kill(old.Frame.gameObject);
            }
            // PlanePropellers caches blade transforms inside the model that just
            // went, and its `_built` flag is private with no reset — so the
            // component itself goes and PlaneController.Init adds a fresh one if
            // the new def has a propeller at all (a jet must not inherit the
            // Mustang's blur disc). DestroyImmediate: Init's own
            // `GetComponent<PlanePropellers>() == null` test runs in this same
            // frame and a deferred destroy would still answer non-null.
            var props = pc.GetComponent<PlanePropellers>();
            if (props != null) Object.DestroyImmediate(props);

            // ---- 2. the new skin goes on ------------------------------------
            pc.PlaneId = def.Id;
            var model = Object.Instantiate(prefab);
            model.name = def.Id;
            // Attach, not Ensure: the chain was just removed on purpose, and
            // Attach is what applies def.Scale and src/planes.js:834's own
            // orientation fix for THIS airframe (Flight/PlaneModelFrame.cs).
            PlaneModelFrame.Attach(root, model, def);

            // ---- 3. cockpit, shell, hood for the new airframe ---------------
            // Same call Editor/BuildPlayableScene.cs makes for the baked P-51,
            // flyable:true because this is the machine the pilot sits in.
            GameObject kit = LoadCockpitKit();
            TbilisiFly.Character.PlaneCockpitRig cockpitRig = null;
            try { cockpitRig = PlaneCockpitBaker.Bake(pc.gameObject, def, kit, flyable: true, out _); }
            catch (System.Exception e) { Debug.LogException(e); } // a missing cockpit must not cost the plane

            // ---- 4. the damage record is about the airframe, so it moves too -
            // Flight/PlaneHallFill.cs:341-356's own derivation, read off this
            // airframe's identity-measured local box. Left alone if there is no
            // record (nothing else in the project assumes one).
            var rec = pc.GetComponent<PlaneDamageRecord>();
            if (rec != null)
            {
                var air = PlaneCockpitMeasurement.BoxOf(root, root); // skin only — see that method
                float span = Mathf.Max(0.1f, air.size.x);
                float len = Mathf.Max(0.1f, air.size.z);
                rec.HalfSpan = span * 0.5f;                       // planes.js:1574
                rec.Size = Mathf.Max(span, len);                  // planes.js:1575
                rec.NoseLen = Mathf.Max(1f, air.max.z);           // planes.js:1587, +Z nose here
                rec.MaxHullPoints = Planes.PlaneStamina(def);     // target.js:69
                rec.HullPoints = rec.MaxHullPoints;
                rec.Dead = false;
                rec.DisplayName = def.Name;                       // planedamage.js:1341
                rec.IsPlayerOwn = true;
                // FOUND BY Assets/Tests/PlayMode/UserFlowsRun.cs's FLOW_AIRPORT
                // scenario (2026-11-01): Core/CombatWorldAdapter.AirframeHeight's
                // own doc comment says it "excludes the cockpit kit explicitly"
                // via rec.CockpitKit — but nothing anywhere (not here, not
                // Editor/BuildPlayableScene.cs's bake of the original P-51)
                // ever ASSIGNED that field for the hero's own plane, so the
                // exclusion never actually ran for the one machine that
                // matters to TryParkInShed's door-height fit check.
                rec.CockpitKit = cockpitRig != null ? cockpitRig.CockpitKitValue : null;
            }

            // ---- 5. the flight rig is rebuilt from the new catalog row ------
            pc.ReInit();

            Debug.Log($"STARTERPLANESKIN plane={pc.PlaneId} name={def.Name} "
                      + $"span={(def.Span ?? 0f):F2} belly={pc.BellyOffset:F3} "
                      + $"props={(pc.GetComponent<PlanePropellers>() != null).ToString().ToLowerInvariant()}");
            return true;
        }

        static GameObject LoadAirframe(string id)
        {
            // Flight/PlaneHallFill.cs's own two paths, reused rather than
            // re-spelled (spitfire is an .obj, everything else an .fbx).
            string path = id == "spitfire" ? PlaneHallFill.SpitfireObjPath : PlaneHallFill.PlanesFbxDir + id + ".fbx";
            return ArtAssets.Load<GameObject>(path);
        }

        static GameObject LoadCockpitKit()
        {
            return ArtAssets.Load<GameObject>(PlaneHallFill.CockpitKitPath);
        }

        static void Kill(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) Object.Destroy(go);
            else Object.DestroyImmediate(go); // Destroy is illegal outside play mode — UI/Shell/ShellUi.Kill's note
        }
    }
}

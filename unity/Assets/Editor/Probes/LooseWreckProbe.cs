using System;
using TbilisiFly.Combat;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the DEAD FEATURE this port pass was asked to wire is actually
    /// alive, not just present as unreachable code: Flight/PlanePhysics.cs's
    /// PlanePhysicsSystem.Update(dt) was fully ported (src/planephys.js:
    /// 678-699) and called from NOWHERE in the project before Flight/
    /// PlaneController.cs's Step() gained the call this pass added — a wreck
    /// knocked loose by a collision never fell, slid, tumbled or burned,
    /// because the simulation that does all of that ran zero times per
    /// frame. Its sibling PlanePhysicsSystem.HitPlayer (src/planephys.js:
    /// 756/923-970 `_hitPlayer` — the machine you flung coming back and
    /// hitting YOUR OWN aeroplane) was ALSO unreachable until this pass
    /// wired it into the same Update() call via Step()'s new PilotState/
    /// shell-collider/onHitPlayer arguments.
    ///
    /// Same two scenarios, IDENTICAL literal masses/positions/speeds, as
    /// tools/_probe_loosewreck.mjs (which runs the REAL src/planephys.js
    /// `PlanePhysics` class directly) — diff with tools/unity_port_check.py
    /// or by eye. Per the project's own trivia list: booleans are printed
    /// lower-case (`LBool` below) because C#'s `bool.ToString()` gives
    /// "True" where JavaScript prints "true", which otherwise silently
    /// breaks the line-pairing.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.LooseWreckProbe.Run
    /// </summary>
    public static class LooseWreckProbe
    {
        static string LBool(bool b) => b ? "true" : "false";

        /// <summary>Every IPlanePhysicsWorld method may do nothing (the
        /// interface's own documented convention) — GroundAt is the one
        /// exception that must return a real, FIXED, far-below floor so
        /// neither scenario's body ever actually lands during the probe's
        /// short window, matching tools/_probe_loosewreck.mjs's own
        /// `_groundAt: () => -1e5` stub exactly.</summary>
        sealed class StubWorld : IPlanePhysicsWorld
        {
            public float? GroundAt(float x, float z, float refY, float maxDist) => -100000f;
            public float CameraDistanceTo(Vector3 point) => 50f;
            public float SoundVolume() => 0.7f;
            public void SpawnEmber(Vector3 at) { }
            public void SpawnSmoke(Vector3 at, float size, float dark, float dur, float vy, float spread) { }
            public void Explosion(Vector3 at, float radius) { }
            public void Plume(Vector3 at, float radius, float strength) { }
            public void Wave(Vector3 at, float radius) { }
            public void StampScorch(Vector3 at, float radius) { }
            public void OnShake(float power, float x, float z) { }
            public void DustBurst(Vector3 at, int count, float radius) { }
            public bool DamageBuilding(Vector3 point, Vector3 normal, float roundsEquivalent, out bool destroyed)
            { destroyed = false; return false; }
            public void SetCollidersEnabled(Transform wrapper, bool enabled_) { }
            public bool RaycastWorld(Vector3 origin, Vector3 dir, float maxDist, out RaycastHit hit)
            { hit = default; return false; }
        }

        const float Dt = 1f / 60f;

        /// <summary>Like StubWorld, but SetCollidersEnabled REALLY toggles the
        /// colliders and there is a floor to come to rest on — so the
        /// drop/bake pair can be observed on real Unity components instead of
        /// on a no-op. Used only by <see cref="RunWreckRef"/>.</summary>
        sealed class FloorWorld : IPlanePhysicsWorld
        {
            public float Floor;
            public float? GroundAt(float x, float z, float refY, float maxDist) => Floor;
            public float CameraDistanceTo(Vector3 point) => 50f;
            public float SoundVolume() => 0.7f;
            public void SpawnEmber(Vector3 at) { }
            public void SpawnSmoke(Vector3 at, float size, float dark, float dur, float vy, float spread) { }
            public void Explosion(Vector3 at, float radius) { }
            public void Plume(Vector3 at, float radius, float strength) { }
            public void Wave(Vector3 at, float radius) { }
            public void StampScorch(Vector3 at, float radius) { }
            public void OnShake(float power, float x, float z) { }
            public void DustBurst(Vector3 at, int count, float radius) { }
            public bool DamageBuilding(Vector3 point, Vector3 normal, float roundsEquivalent, out bool destroyed)
            { destroyed = false; return false; }
            public void SetCollidersEnabled(Transform wrapper, bool enabled_)
            {
                if (wrapper == null) return;
                foreach (var c in wrapper.GetComponentsInChildren<Collider>(true)) c.enabled = enabled_;
            }
            public bool RaycastWorld(Vector3 origin, Vector3 dir, float maxDist, out RaycastHit hit)
            { hit = default; return false; }
        }

        public static void Run()
        {
            RunFall();
            RunHit();
            RunWreckRef();
            Debug.Log("JSONLY note=cs_update_and_hitplayer_wired_from_planecontroller_step_this_pass_shell_found_via_getcomponentinchildren_combat_planeshelltag_on_the_plane_root_not_character_planecockpitrig");
            ProbeExit.Exit(0);
        }

        /// <summary>FALL — src/planephys.js:678-699 `update`'s own gravity/
        /// drag integration (`_step`, :730-753), through PlanePhysicsSystem.
        /// Update(dt) exactly as PlaneController.Step() now calls it every
        /// frame, run for 60 real frames with no shell/pilot supplied (the
        /// same no-op convention as a parked plane's own now-also-ticking
        /// system) and no ground ever found, so the body free-falls for the
        /// whole window.</summary>
        static void RunFall()
        {
            var world = new StubWorld();
            var sys = new PlanePhysicsSystem(world);

            var wrapperGo = new GameObject("fallWreck");
            wrapperGo.transform.position = new Vector3(0f, 500f, 0f);

            var rec = new PlaneRecord
            {
                Wrapper = wrapperGo.transform,
                Def = new PlaneDef { Mass = 3400f },
                Dead = false,
                Span = 11f, Len = 9f, Height = 3f, NoseLen = 2.6f,
            };
            var body = sys.MakeLoose(rec);

            const int frames = 60;
            float posY0 = body.Obj.position.y;
            float velY0 = body.Vel.y;
            for (int i = 0; i < frames; i++) sys.Update(Dt);
            float posY1 = body.Obj.position.y;
            float velY1 = body.Vel.y;

            bool moved = Mathf.Abs(posY1 - posY0) > 0.01f;
            bool fellDown = posY1 < posY0;
            bool velFell = velY1 < velY0;

            Debug.Log($"FALL frames={frames} dt={Dt:F6} mass=3400.000000 posY0={posY0:F6} posY1={posY1:F6} " +
                      $"velY0={velY0:F6} velY1={velY1:F6} moved={LBool(moved)} fellDown={LBool(fellDown)} velFell={LBool(velFell)}");

            UnityEngine.Object.DestroyImmediate(wrapperGo);
        }

        /// <summary>HIT — src/planephys.js:756/923-970 `_hitPlayer`, through
        /// PlanePhysicsSystem.HitPlayer, reached the SAME way
        /// PlaneController.Step() now reaches it: via Update(dt)'s optional
        /// pilotState/playerShellColliders/onHitPlayer parameters, never
        /// called directly. A stationary "player" plane at (0,50,0) carries
        /// a real "plane_shell" child GameObject with a BoxCollider matching
        /// Editor/BuildPlayableScene.cs's REAL baked numbers (size
        /// 3.4x1.7x6.6, local offset (0,0.65,0.4)) and the SAME
        /// Combat.PlaneShellTag Flight/PlaneController.cs's own
        /// ShellColliders() looks for via GetComponentInChildren — this
        /// probe resolves the collider list the identical way, not by
        /// reaching into the shell GameObject directly, so a change to that
        /// lookup would show up here too. A 3400 kg wreck closes on it at
        /// 25 m/s from 8 m away, starting at the shell's own vertical
        /// CENTRE (50.65) so gravity cannot carry it out of the box's
        /// vertical span before the sweep ray reaches — see tools/
        /// _probe_loosewreck.mjs's own header for why that matters (an
        /// earlier revision at y=50 never once hit in 30 frames).</summary>
        static void RunHit()
        {
            var world = new StubWorld();
            var sys = new PlanePhysicsSystem(world);

            var playerGo = new GameObject("player");
            playerGo.transform.SetPositionAndRotation(new Vector3(0f, 50f, 0f), Quaternion.identity);

            var shellGo = new GameObject("plane_shell");
            shellGo.transform.SetParent(playerGo.transform, false);
            shellGo.transform.localPosition = new Vector3(0f, 0.65f, 0.4f);
            var shellBox = shellGo.AddComponent<BoxCollider>();
            shellBox.size = new Vector3(3.4f, 1.7f, 6.6f);
            shellGo.AddComponent<PlaneShellTag>();

            // SAME lookup PlaneController.ShellColliders() does: find the
            // PlaneShellTag under our own transform, take every Collider on
            // that same GameObject.
            var shellTag = playerGo.GetComponentInChildren<PlaneShellTag>(true);
            var shellCols = shellTag.GetComponents<Collider>();

            var playerDef = new PlaneDef { Mass = 4500f };
            float playerColMult = Planes.PlaneColMult(playerDef);

            var wreckWrapperGo = new GameObject("wreck");
            wreckWrapperGo.transform.position = new Vector3(0f, 50.65f, -8f);
            var wreckRec = new PlaneRecord
            {
                Wrapper = wreckWrapperGo.transform,
                Def = new PlaneDef { Mass = 3400f },
                Dead = false,
                Span = 11.28f, Len = 9.1f, Height = 3f, NoseLen = 2.6f,
            };
            var wreckBody = sys.MakeLoose(wreckRec);
            wreckBody.Vel = new Vector3(0f, 0f, 25f);

            Vector3 playerVel = Vector3.zero;
            int hitFrame = -1;
            bool handled = false;
            float dmgUs = 0f, nz = 0f, pilotVelZ = 0f, pilotPosDeltaZ = 0f, wreckVelZ = 0f, wreckPosZ = 0f;
            const int maxFrames = 20;

            for (int frame = 1; frame <= maxFrames && !handled; frame++)
            {
                var pilotState = new PilotState
                {
                    Position = playerGo.transform.position,
                    Rotation = playerGo.transform.rotation,
                    Mass = 4500f,
                    ColMult = playerColMult,
                    HalfSpan = 5.64f,
                    Size = 9.1f,
                    Height = 3f,
                    Airborne = true,
                    Speed = 0f,
                    Vel = playerVel,
                    Rates = Vector3.zero,
                };

                PlanePhysicsSystem.HitPlayerResult captured = default;
                bool got = false;
                sys.Update(Dt, pilotState, shellCols, false, r => { captured = r; got = true; });

                if (got && captured.Handled)
                {
                    handled = true;
                    hitFrame = frame;
                    dmgUs = captured.DamageToUs;
                    nz = captured.ImpactNormal.z;
                    pilotVelZ = captured.PilotVelocity.z;
                    pilotPosDeltaZ = captured.PilotPositionDelta.z;
                    playerVel = captured.PilotVelocity;
                    playerGo.transform.position += captured.PilotPositionDelta;
                }
                wreckVelZ = wreckBody.Vel.z;
                wreckPosZ = wreckBody.Obj.position.z;
            }

            Debug.Log($"HIT hitFrame={hitFrame} handled={LBool(handled)} dmgUs={dmgUs:F6} nz={nz:F6} " +
                      $"pilotVelZ={pilotVelZ:F6} pilotPosDeltaZ={pilotPosDeltaZ:F6} wreckVelZ={wreckVelZ:F6} wreckPosZ={wreckPosZ:F6}");

            UnityEngine.Object.DestroyImmediate(playerGo);
            UnityEngine.Object.DestroyImmediate(wreckWrapperGo);
        }

        /// <summary>WRECKREF — src/planephys.js:560-591 `_dropCollider` /
        /// `_bakeCollider`, the pair whose BACK-REFERENCE half had no port, so
        /// `_sweep`'s wreck-vs-wreck resolution (:886 `hit.object.userData?.
        /// planeRec?.body`) had no writer at all and fell through to the world
        /// actor unconditionally.
        ///
        /// Driven through the REAL production path — MakeLoose for the drop,
        /// and Update(dt) settling the wreck onto a floor for the bake, never
        /// by calling DropCollider/BakeCollider directly — on a plane carrying
        /// a real BoxCollider, exactly like the 35 PlaneHallFill parks
        /// (PlaneHallFill.cs:208-214). The last two assertions are the ones
        /// that matter and they are a PARITY statement, not a bug fix: the JS
        /// writes `planeRec` only on a collider that exists, and a collider
        /// only exists while the machine is NOT loose (`_body` drops it at
        /// :553, `_park` re-bakes it at :718 and nulls `rec.body` at :720), so
        /// `planeRec?.body` resolves non-null only for a record whose Body is
        /// live. refActorAfterPark=null is therefore CORRECT and matches the
        /// JS; refResolvesLiveBody=true is the proof that the read side works
        /// the moment the data says loose, which is what was unprovable while
        /// nothing ever wrote the reference.</summary>
        static void RunWreckRef()
        {
            var world = new FloorWorld { Floor = 0f };
            var sys = new PlanePhysicsSystem(world);

            var wrapperGo = new GameObject("parkedWreck");
            wrapperGo.transform.position = new Vector3(0f, 0.2f, 0f);
            var box = wrapperGo.AddComponent<BoxCollider>();
            box.size = new Vector3(11f, 3f, 9f);

            var rec = new PlaneRecord
            {
                Wrapper = wrapperGo.transform,
                Def = new PlaneDef { Mass = 3400f },
                Dead = false,
                Span = 11f, Len = 9f, Height = 3f, NoseLen = 2.6f,
            };

            bool colOnBefore = box.enabled;
            bool refBefore = wrapperGo.GetComponent<LooseAirframeActorRef>() != null;

            var body = sys.MakeLoose(rec);
            bool looseFlag = rec.Loose;
            bool colOffWhileLoose = !box.enabled;

            // Settle it: 300 frames is 5 s, comfortably past the 0.35 s rest
            // timer once it is on the floor.
            int frames = 0;
            for (; frames < 300 && sys.Bodies.Count > 0; frames++) sys.Update(Dt);
            bool parked = sys.Bodies.Count == 0;

            var reference = wrapperGo.GetComponent<LooseAirframeActorRef>();
            bool refAfter = reference != null;
            bool recWired = refAfter && ReferenceEquals(reference.Record, rec);
            bool colOnAfter = box.enabled;
            bool notLooseAfter = !rec.Loose;
            bool refActorNullAfterPark = refAfter && reference.Actor == null;

            // ...and the read side, with the data saying loose: this is what
            // Sweep() evaluates at PlanePhysics.cs's `other` line.
            rec.Body = body;
            bool refResolvesLiveBody = refAfter && ReferenceEquals(reference.Actor, body);
            rec.Body = null;

            Debug.Log($"WRECKREF colOnBefore={LBool(colOnBefore)} refBefore={LBool(refBefore)} " +
                      $"looseFlag={LBool(looseFlag)} colOffWhileLoose={LBool(colOffWhileLoose)} " +
                      $"frames={frames} parked={LBool(parked)} refAfter={LBool(refAfter)} " +
                      $"recWired={LBool(recWired)} colOnAfter={LBool(colOnAfter)} " +
                      $"notLooseAfter={LBool(notLooseAfter)} " +
                      $"refActorNullAfterPark={LBool(refActorNullAfterPark)} " +
                      $"refResolvesLiveBody={LBool(refResolvesLiveBody)}");

            UnityEngine.Object.DestroyImmediate(wrapperGo);
        }
    }
}

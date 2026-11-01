using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using TbilisiFly.Character;
using TbilisiFly.Combat;
using TbilisiFly.Props;

namespace TbilisiFly.Probes
{
    /// <summary>Minimal no-op IFootBelt — this probe never draws from a
    /// holster or measures the body for belt-fitting, it only needs
    /// FootWeapons' already-public Held/HeldSide/Armed/IsRifleHeld surface
    /// (what WindRider.ResolveGunArmBones actually reads). Every method here
    /// returning null/empty is the SAME documented reduced-fidelity choice
    /// IFootBelt.ResyncRide's own doc comment calls out as valid.</summary>
    sealed class StubFootBelt : IFootBelt
    {
        public WornBelt HolsterSocket => null;
        public Vector3? RingCenterWorld(WornBelt w) => null;
        public IEnumerable<Vector3> GearVertsWorld(WornBelt w) => System.Array.Empty<Vector3>();
        public BodySection? BodySectionAt(CharacterRig rig, float worldY) => null;
        public IEnumerable<Vector3> BodyVertsWorld(CharacterRig rig) => System.Array.Empty<Vector3>();
        public void ResyncRide(WornBelt w, Transform sock) { }
    }

    // Counterpart to tools/_probe_windgunarm.mjs — proves crane.js:1733-1807's
    // gun-arm preservation (Props/WindRider.cs's ResolveGunArmBones + the
    // save/restore straddling SetPose/HandsOnEdge in Tick()'s fly branch).
    //
    // METHOD: drive a REAL CharacterRig (banana.fbx, same body CharCombatProbe
    // already loads) through off->ride->blown->fly on the SAME synthetic wing
    // skin Crane3Probe/CraneRestProbe use. Every frame BEFORE calling Tick(),
    // force the candidate gun arm's upper/fore/hand bones to one FIXED,
    // arbitrary rotation (far from the bind pose) — standing in for whatever
    // Character/FootWeaponFrame.cs's _weaponFrame wrote that frame, exactly
    // the way crane.js's own comment describes it ("foot.js now poses and
    // seats a drawn weapon... it runs BEFORE this updater"). After Tick()
    // returns, read the SAME bone back:
    //   * armed, gun on the side HandsOnEdge would otherwise grab -> the
    //     bone must read back EXACTLY the forced value (drift 0.000000) on
    //     EVERY frame once in fly mode, proving the save/restore holds
    //     through the whole ride, not just the entering frame.
    //   * unarmed -> the SAME bone, same geometry, same frames, is NOT
    //     protected, and HandsOnEdge's reach solver visibly moves it — the
    //     contrast that proves the mechanism is actually doing something,
    //     not just reporting zero because nothing would have moved anyway.
    //
    // ENVIRONMENT GAP FOUND WHILE WRITING THIS (not this port unit's to fix):
    // every character FBX in this checkout (banana/eggplant/toad/elephant)
    // has an EMPTY humanoid bone mapping (`human: []` in the .meta,
    // animationType: 2/Generic, never 3/Human) — no `grep -rl "animationType:
    // 3" Assets` hit ANYWHERE. FootPoses.cs's own `Build(rig)` hard-gates on
    // `rig.IsHumanoid` (line ~156) before it will fill in ANY arm/leg bone —
    // so in THIS checkout, SetWingGrip/HandsOnEdge/SetWindBrace/ClimbMetrics
    // are ALL no-ops for every body, project-wide, not something this pass
    // introduced. The maxDriftDeg=0 readings below are therefore NOT proof
    // that HandsOnEdge tried and failed to move the unprotected hand — they
    // mean HandsOnEdge never got far enough to try. What IS proven
    // independent of that gap: `resolvedCount`/`sideDifferent` below call the
    // REAL WindRider.ResolveGunArmBones, which is NAME-based (Combat.Weapons.
    // FindArmChain/FindHandBone) and does NOT depend on IsHumanoid — so the
    // bone-SELECTION half of this port is verified for real; the "does the
    // save/restore actually fight a live write" half needs a Humanoid-
    // configured body this checkout does not currently have.
    //
    //   Unity -batchmode -quit -nographics -projectPath unity
    //        -executeMethod TbilisiFly.Probes.WindGunArmProbe.Run -logFile ...
    public static class WindGunArmProbe
    {
        static readonly Quaternion Forced = Quaternion.Euler(41f, -17f, 9f);
        const int FlyHoldFrames = 30;
        const int MaxFrames = 600;
        const float Dt = 1f / 60f;

        public static void Run()
        {
            RunCase("R", "akm", "R");
            RunCase("L", "akm", "L");
            RunCase("NONE", null, "R");
            Debug.Log("WINDGUNARM_PROBE_DONE=1");
        }

        static void RunCase(string id, string weaponId, string heldSide)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Art/Characters/banana.fbx");
            if (prefab == null) { Debug.LogError($"WINDGUNARM id={id} MISSING BODY"); return; }
            var instance = Object.Instantiate(prefab);
            CharacterRig rig = null;
            GameObject planeGo = null, gunGo = null;
            try
            {
                rig = new CharacterRig(instance, "banana");
                // NOT gated on rig.IsHumanoid: that flag needs a configured
                // Humanoid Avatar (Anim.avatar.isHuman) and this mechanism
                // does not depend on it. ResolveGunArmBones and the
                // HandsOnEdge/SetPose calls it is tested against all key off
                // Combat.Weapons.FindArmChain/FindHandBone's plain NAME-based
                // bone search (FootPoses.cs's own `pr.HasArms`, independent
                // of `pr.Valid`'s IsHumanoid gate) — confirmed by reading
                // FootPoses.cs before relying on it, not assumed.

                planeGo = new GameObject("ProbePlane_" + id);
                Transform plane = planeGo.transform;

                var box = new WindRider.PlaneBox { MinX = -5.5f, MaxX = 5.5f, MinY = -0.3f, MaxY = 0.5f, MinZ = -4.5f, MaxZ = 4.5f };
                var rider = new WindRider(box);
                // same synthetic skin as CraneProbe.cs/Crane3Probe.cs's own
                // CornerAt/AdvanceOnWing tests: a long wing run z in
                // [-3.5,1.5] (top 0.4), a short tailplane run z in [2.5,3.3].
                rider.SkinAt = (x, z) =>
                {
                    if (z >= -3.5f && z <= 1.5f) return new Vector3(x, 0.4f, z);
                    if (z >= 2.5f && z <= 3.3f) return new Vector3(x, 0.6f, z);
                    return null;
                };

                var foot = new FootController();
                foot.Weapons = new FootWeapons(new StubFootBelt(), instance.transform);
                if (weaponId != null)
                {
                    gunGo = new GameObject("ProbeGun_" + id);
                    foot.Weapons.Held = new CarriedWeapon { Def = WeaponDefs.ById[weaponId], Obj = gunGo.transform };
                    foot.Weapons.HeldSide = heldSide;
                }
                foot.Pos = new Vector3(3f, 0.5f, 0f);
                foot.Vy = 0f;

                // crane.js:981-982 `gunSide(foot)`, the SAME boolean
                // ResolveGunArmBones computes — replicated here (not called
                // into) so this probe can print it as a PURE, comparable fact
                // (see tools/_probe_windgunarm.mjs) independent of the
                // rig-dependent drift measurement below, which has no
                // Node-only counterpart (no loaded skeleton there either —
                // same "Unity-only" honesty CharCombatProbe's own header
                // documents for its per-vertex pocket fit).
                bool heldBool = weaponId != null;
                bool rifleBool = heldBool && WeaponDefs.IsRifle(WeaponDefs.ById[weaponId]);
                bool left = heldBool && rifleBool && heldSide == "L";
                // same numeric encoding as tools/_probe_windgunarm.mjs's GUNSIDE
                // line: heldN/rifleN/sideInN are the inputs, sideOutN is the
                // result, all 0=false/R, 1=true/L.
                Debug.Log($"GUNSIDE id={id} heldN={(heldBool ? 1 : 0)} rifleN={(rifleBool ? 1 : 0)} " +
                    $"sideInN={(heldSide == "L" ? 1 : 0)} sideOutN={(left ? 1 : 0)}");

                Transform hand = TbilisiFly.Combat.Weapons.FindHandBone(rig.Root);
                Transform handL = TbilisiFly.Combat.Weapons.FindHandBoneL(rig.Root);
                Debug.Log($"CSONLY WINDGUNARM id={id} sideDifferent={(hand != handL).ToString().ToLowerInvariant()}");

                // Call the REAL production method directly (not a replica)
                // — the strongest fact this probe prints: does
                // ResolveGunArmBones actually select the right side's
                // bones, with the right count (upper+fore+hand = 3 when the
                // arm-chain regexes match banana.fbx's own bone names, fewer
                // if they don't, 0/null when nothing is held)? This does
                // NOT depend on rig.IsHumanoid — FindArmChain/FindHandBone
                // are plain name searches — unlike the HandsOnEdge/SetPose
                // calls below.
                Transform[] resolved = WindRider.ResolveGunArmBones(foot, rig);
                Debug.Log($"CSONLY WINDGUNARM id={id} resolvedCount={(resolved?.Length ?? 0)}");
                // Paired against tools/_probe_windgunarm.mjs's own RESOLVECOUNT
                // prediction (3 when armed and all 3 bone names match this
                // body, 0 when unarmed) — n=3 here is itself empirical (banana
                // .fbx's bone names happen to match every regex), not pure
                // logic, but worth asserting since it is what makes the
                // unarmed n=0 contrast meaningful instead of a coincidence.
                Debug.Log($"RESOLVECOUNT id={id} n={(resolved?.Length ?? 0)}");
                Transform tracked = left ? handL : hand; // the bone ResolveGunArmBones protects this case (if it resolved one)

                var st = new WindRider.State();
                Vector3 windWorld = Vector3.forward;
                string mode = "off";
                int flyFrames = 0;
                float maxDrift = 0f;
                int framesChecked = 0;

                for (int i = 0; i < MaxFrames && flyFrames < FlyHoldFrames; i++)
                {
                    // Stand in for Character/FootWeaponFrame.cs's per-frame
                    // weapon-arm write, which runs BEFORE WindRider.Tick in
                    // the real game (crane.js:1734-1745's own comment).
                    if (tracked != null) tracked.localRotation = Forced;

                    mode = rider.Tick(st, Dt, 1.6f, foot, rig, plane, windWorld, true);

                    if (mode == "fly")
                    {
                        flyFrames++;
                        if (tracked != null)
                        {
                            float drift = Quaternion.Angle(Forced, tracked.localRotation);
                            if (drift > maxDrift) maxDrift = drift;
                            framesChecked++;
                        }
                    }
                }

                Debug.Log($"CSONLY WINDGUNARM id={id} finalMode={mode}");
                Debug.Log($"CSONLY WINDGUNARM id={id} gotEdge={st.GotEdge.ToString().ToLowerInvariant()}");
                Debug.Log($"CSONLY WINDGUNARM id={id} flyFramesChecked={framesChecked}");
                Debug.Log($"CSONLY WINDGUNARM id={id} maxDriftDeg={maxDrift:F6}");
            }
            finally
            {
                if (instance != null) Object.DestroyImmediate(instance);
                if (planeGo != null) Object.DestroyImmediate(planeGo);
                if (gunGo != null) Object.DestroyImmediate(gunGo);
            }
        }
    }
}

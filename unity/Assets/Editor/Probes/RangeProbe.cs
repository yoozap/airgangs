using TbilisiFly.Character;
using TbilisiFly.Combat;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the Range port unit (RifleHolds.cs, PaperTargets.cs, RangeLane.cs,
    /// Pickups.cs) against src/weapons.js/papertargets.js/ballistics.js. Good
    /// targets per the port brief: ISSF ring radii and the score for a set of
    /// fixed shot coordinates (including deliberate line-break cases), hole
    /// diameter per calibre, the rifle hold's hand positions for a fixed body
    /// pose, and the pickup take-radius.
    ///
    /// tools/_probe_range.mjs prints the matching JS-side facts — the SCORE/
    /// RING/HOLE/B27/PICKUP lines are run against the exact same fixed inputs
    /// here, so they diff directly (tools/unity_port_check.py).
    ///
    /// ---- RIFLEHOLD: THE PAIR IS BACK, AND IT WAS HIDING A BUG -------------
    /// This header used to say the rifle-hold numbers "can never line-diff"
    /// because the JS side runs a synthetic two-bone rig and this side loads
    /// the real banana.fbx, and the JS side renamed its lines RIFLEHOLD_SYNTH
    /// to stop them colliding. The pair then reported "worst 0.000" by
    /// comparing NOTHING, and a real defect lived in that gap for a whole
    /// pass: THIS PROBE WAS FEEDING `applyRifleHold` A THREE.JS AIM VECTOR.
    /// `new Vector3(0, 0, -1)` is forward in Three and BACKWARD in Unity, so
    /// the two modes that build off the aim (`aim`, `carry`) came out mirrored
    /// while the two that build their forward off the BODY AXES instead
    /// (`cover`, `reload` — weapons.js:884's `crossVectors(_up, _right)`,
    /// ported as RifleHolds.ChestLine's swapped `Cross(right, up)`) came out
    /// correct. Hand-pairing the four modes read as "cover and reload flip
    /// sign on one axis" — i.e. as a bug in the two branches that were right.
    ///
    /// So there are now TWO rifle-hold blocks here:
    ///   RIFLEHOLD / RIFLESUPPORT / RIFLECHAINS — the SAME synthetic rig
    ///     tools/_probe_range.mjs builds (shoulder 1.40 m, 0.27 m upper arm,
    ///     0.26 m forearm, z-offsets mirrored for Unity's +Z forward), so the
    ///     numbers PAIR. They are printed in the BODY's own frame
    ///     (R = toward the gun shoulder, U = up, F = forward) rather than as
    ///     raw world x/y/z, because F is -Z in Three and +Z in Unity and a raw
    ///     Z can therefore never agree however correct the port is. Every
    ///     other quantity these functions compute is already frame-invariant
    ///     (hints are expressed in up/right/fwd; the IK has no cross product),
    ///     so this is the whole of the conversion.
    ///   CSONLY RIFLEHOLD_REAL — the standalone sanity assertion on the REAL
    ///     imported banana.fbx (hand within the real arm's reach, no NaN),
    ///     which has no JS counterpart by construction and gates the exit code
    ///     alongside the synthetic block.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.RangeProbe.Run
    /// </summary>
    public static class RangeProbe
    {
        public static void Run()
        {
            // ---- ISSF ring radii — src/papertargets.js:33-48 -------------------
            // id=r1..r10: digit GLUED to a letter, not standing on its own, so
            // tools/unity_port_check.py's number-blanking cannot collapse two
            // different rings to one skeleton (its own header's documented
            // failure mode) — mirrors tools/_probe_range.mjs's own fix.
            for (int ring = 1; ring <= IssfTarget.Rings; ring++)
                Debug.Log($"RING id=r{ring} radiusMm={IssfTarget.RingRadiusMm(ring):F3}");

            // ---- SCORE — same fixed coordinates/ids as tools/_probe_range.mjs -
            var makarov = Cartridges.ById["9x18"];
            var beretta = Cartridges.ById["9x19"];
            void PrintScore(string id, float x, float y, Cartridge cart)
            {
                var s = IssfTarget.ScoreOpenRing(x, y, cart.BulletMm);
                Debug.Log($"SCORE id={id} x={x:F2} y={y:F2} bulletMm={cart.BulletMm:F3} " +
                          $"r={s.RMm:F4} edge={s.EdgeMm:F4} ring={s.Ring} inner={(s.Inner ? 1 : 0)}");
            }
            PrintScore("centre9x18", 0f, 0f, makarov);
            PrintScore("breakTenNine9x18", 25f, 0f, makarov);
            PrintScore("breakNineTen9x19", 0f, 29f, beretta);
            PrintScore("breakNineEight9x18", 50.5f, 0f, makarov);
            PrintScore("farHit9x18", 180f, 40f, makarov);
            PrintScore("edgeTen9x19", 20f, 15f, beretta);

            // ---- HOLE diameter per calibre — real Ballistics.Cartridges ---------
            float pxPerMm = IssfTarget.Px / IssfTarget.CardMm;
            foreach (var id in Cartridges.ById.Keys)
            {
                var c = Cartridges.ById[id];
                Debug.Log($"HOLE id={id} bulletMm={c.BulletMm:F3} diamPxAt1024={(c.BulletMm * pxPerMm):F4}");
            }

            // ---- B-27 — src/papertargets.js:61-66 B27 --------------------------
            var centre = B27Target.Score(B27Target.Cu, B27Target.Cv, beretta.BulletMm, 0.6f, false);
            Debug.Log($"B27 id=centre ring={centre.Ring} inner={(centre.Inner ? 1 : 0)} edgeMm={centre.EdgeMm:F4}");
            var offsheet = B27Target.Score(0.05f, 0.05f, beretta.BulletMm, 0.6f, false);
            Debug.Log($"B27 id=offsheet ring={offsheet.Ring} inner={(offsheet.Inner ? 1 : 0)} edgeMm={offsheet.EdgeMm:F4}");
            var onbody = B27Target.Score(0.5f, 0.75f, beretta.BulletMm, 0.6f, true);
            Debug.Log($"B27 id=onbody ring={onbody.Ring} inner={(onbody.Inner ? 1 : 0)} edgeMm={onbody.EdgeMm:F4}");

            // ---- pickup take-radius — src/pickups.js:14 `const RANGE = 2.6;` ---
            Debug.Log("PICKUP rangeM=2.600");

            // ---- rifle hold, paired: the same synthetic rig the JS probe uses
            bool synthOk = RunRifleHoldSynth();
            // ---- ...and the standalone check on the real banana.fbx rig
            bool holdOk = RunRifleHold();

            ProbeExit.Exit(synthOk && holdOk ? 0 : 1);
        }

        /// <summary>RIFLEHOLD_REAL is a STANDALONE (CSONLY) assertion on the
        /// REAL imported banana.fbx through a real CharacterRig: different bone
        /// lengths and a different rest pose from the synthetic rig, so these
        /// coordinates can never pair — the paired numbers come from
        /// RunRifleHoldSynth() below instead. What IS checked here, and gates
        /// this probe's own exit code: for every hold mode, the hand lands
        /// within the real arm's own reach of the real shoulder, and no
        /// component is NaN.</summary>
        static bool RunRifleHold()
        {
            const string path = "Assets/Resources/Art/Characters/banana.fbx";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) { Debug.Log("CSONLY RIFLEHOLD_REAL body=banana missing=1"); return false; }

            var instance = Object.Instantiate(prefab);
            CharacterRig rig = null;
            bool allOk = true;
            try
            {
                rig = new CharacterRig(instance, "banana");
                var def = WeaponDefs.ById["akm"];
                // HANDEDNESS: straight ahead, gameplay forward. The JS probe's
                // own `aimFwd` is `(0,0,-1)` because Three's forward is -Z;
                // Unity's is +Z. Retyped verbatim (as it was) this aimed the
                // hero BACKWARD and mirrored the `aim`/`carry` holds — see this
                // file's header.
                Vector3 aimFwd = new Vector3(0f, 0f, 1f);

                void PrintHold(string mode)
                {
                    var chains = RifleHolds.RifleChains(rig, rig.Root, "R");
                    if (!chains.Gun.Valid || chains.GunHand == null)
                    {
                        Debug.Log($"CSONLY RIFLEHOLD_REAL mode={mode} ok=0 reason=no-arm-chain");
                        allOk = false;
                        return;
                    }
                    bool ok = RifleHolds.ApplyRifleHold(rig, rig.Root, chains.GunHand, aimFwd, def, mode, "R");
                    Vector3 shoulder = chains.Gun.Upper.position;
                    Vector3 ep = chains.Gun.Fore.position;
                    Vector3 hp = chains.GunHand.position;

                    float upperLen = Vector3.Distance(shoulder, ep);
                    float foreLen = Vector3.Distance(ep, hp);
                    float reach = Vector3.Distance(shoulder, hp);
                    float maxReach = upperLen + foreLen + 0.02f; // 2cm slack for float rounding
                    bool reachOk = reach <= maxReach;
                    bool nanOk = !(float.IsNaN(hp.x) || float.IsNaN(hp.y) || float.IsNaN(hp.z)
                        || float.IsNaN(ep.x) || float.IsNaN(ep.y) || float.IsNaN(ep.z));
                    if (!ok || !reachOk || !nanOk) allOk = false;

                    Debug.Log($"CSONLY RIFLEHOLD_REAL mode={mode} ok={(ok ? 1 : 0)} " +
                              $"handX={hp.x:F5} handY={hp.y:F5} handZ={hp.z:F5} " +
                              $"elbowX={ep.x:F5} elbowY={ep.y:F5} elbowZ={ep.z:F5} " +
                              $"reachOk={(reachOk ? 1 : 0)} nanOk={(nanOk ? 1 : 0)} " +
                              $"reachM={reach:F4} maxReachM={maxReach:F4}");
                }
                PrintHold("aim");
                PrintHold("carry");
                PrintHold("reload");
                PrintHold("cover");

                var chainsAll = RifleHolds.RifleChains(rig, rig.Root, "R");
                Debug.Log($"CSONLY RIFLECHAINS_REAL gunValid={(chainsAll.Gun.Valid ? 1 : 0)} supValid={(chainsAll.Sup.Valid ? 1 : 0)} " +
                          $"gunHand={(chainsAll.GunHand != null ? chainsAll.GunHand.name : "null")} " +
                          $"supHand={(chainsAll.SupHand != null ? chainsAll.SupHand.name : "null")}");
            }
            finally
            {
                if (rig != null) Object.DestroyImmediate(rig.Root.gameObject);
                else Object.DestroyImmediate(instance);
            }
            return allOk;
        }

        /// <summary>The PAIRED rifle-hold block: the same synthetic two-armed
        /// rig tools/_probe_range.mjs:117-137 builds, run through the same real
        /// functions, printed in the body's own frame so the two engines'
        /// opposite forward axes cannot masquerade as a port bug.
        ///
        /// The rig: a right-handed humanoid arm, shoulder 1.40 m up, 0.27 m
        /// upper arm, 0.26 m forearm — "a banana-sized reach, not a full Mixamo
        /// skeleton, since the only thing these functions read off a bone is
        /// its WORLD POSITION/matrix, never its rest rotation" (the JS probe's
        /// own words). The local Z offsets are MIRRORED (+0.05/+0.04 in Three ->
        /// -0.05/-0.04 here) so the rig is the same physical body; X is NOT
        /// mirrored, because the gun-side arm is at +X in both engines (a
        /// character facing -Z in Three and +Z in Unity has its right hand at
        /// +X either way — mirror ONCE, and the forward axis is where it lands).
        ///
        /// Why a CharacterRig over a bare hierarchy: `ApplyRifleHold` needs one
        /// for its arm-chain cache and its `ActGroup` frame. The ctor is safe
        /// on a rendererless rig — NormalizeHeight() measures 0 and skips both
        /// the scale and the sole-offset, so the bones keep the exact local
        /// coordinates set here. And FootPoses.RifleShoulder returns null for
        /// it (no Animator -> PoseRig.HasArms false), which is precisely what
        /// the JS's `{}` rigStub does with its missing `rifleShoulder` — so the
        /// aim/carry pocket FALLBACK is exercised on both sides, not one.</summary>
        static bool RunRifleHoldSynth()
        {
            var rootGo = new GameObject("SynthRoot");
            CharacterRig rig = null;
            bool allOk = true;
            try
            {
                // tools/_probe_range.mjs:117-131 makeArm
                (Transform upper, Transform fore, Transform hand) MakeArm(Transform parent, string sideLetter)
                {
                    float s = sideLetter == "L" ? -1f : 1f;
                    string suffix = sideLetter == "L" ? "l" : "r";
                    Transform Bone(string name, Transform p, Vector3 local)
                    {
                        var go = new GameObject(name);
                        go.transform.SetParent(p, false);
                        go.transform.localPosition = local;
                        return go.transform;
                    }
                    var up = Bone($"upperarm_{suffix}", parent, new Vector3(0.20f * s, 1.40f, 0f));
                    var fo = Bone($"lowerarm_{suffix}", up, new Vector3(0.02f * s, -0.27f, -0.05f));
                    var ha = Bone($"hand_{suffix}", fo, new Vector3(0.01f * s, -0.26f, -0.04f));
                    return (up, fo, ha);
                }
                MakeArm(rootGo.transform, "R");
                MakeArm(rootGo.transform, "L");

                rig = new CharacterRig(rootGo, "synth");
                var def = WeaponDefs.ById["akm"];
                // straight ahead, gameplay forward — Unity's +Z (the JS probe's
                // own (0,0,-1), mirrored; see this method's header).
                Vector3 aimFwd = new Vector3(0f, 0f, 1f);

                // The synthetic rig sits at identity, so the body's own frame IS
                // the world axes: R = +X (the gun shoulder's side, which
                // Weapons.BodyAxes resolves to for this rig on both sides),
                // U = +Y, F = the engine's forward (+Z here, -Z in Three), so
                // handR/handU/handF below are just x/y/z read in that order.
                var chains = RifleHolds.RifleChains(rig, rig.Root, "R");
                if (!chains.Gun.Valid || chains.GunHand == null || !chains.Sup.Valid || chains.SupHand == null)
                {
                    Debug.Log("CSONLY RIFLEHOLD_SYNTH_SETUP ok=0 reason=no-arm-chain");
                    return false;
                }

                void PrintHold(string mode)
                {
                    bool ok = RifleHolds.ApplyRifleHold(rig, rig.Root, chains.GunHand, aimFwd, def, mode, "R");
                    Vector3 hp = chains.GunHand.position;
                    Vector3 ep = chains.Gun.Fore.position;
                    bool nanOk = !(float.IsNaN(hp.x) || float.IsNaN(hp.y) || float.IsNaN(hp.z)
                        || float.IsNaN(ep.x) || float.IsNaN(ep.y) || float.IsNaN(ep.z));
                    if (!ok || !nanOk) allOk = false;
                    Debug.Log($"RIFLEHOLD mode={mode} ok={(ok ? 1 : 0)} " +
                              $"handR={hp.x:F5} handU={hp.y:F5} handF={hp.z:F5} " +
                              $"elbowR={ep.x:F5} elbowU={ep.y:F5} elbowF={ep.z:F5}");
                }
                PrintHold("aim");
                PrintHold("carry");
                PrintHold("reload");
                PrintHold("cover");

                // applyRifleSupport onto a synthetic weapon object — the JS puts
                // it at (0.20, 1.10, 0.35) in Three, i.e. R=0.20 U=1.10 F=-0.35,
                // which is z=-0.35 here. Only its matrixWorld and local +X (the
                // bore) are read, so an empty Transform is the whole stand-in.
                var weapon = new GameObject("synth_weapon").transform;
                weapon.position = new Vector3(0.20f, 1.10f, -0.35f);
                bool sOk = RifleHolds.ApplyRifleSupport(rig, rig.Root, weapon, def, 1f, aimFwd, "R");
                Vector3 sp = chains.SupHand.position;
                if (!sOk) allOk = false;
                Debug.Log($"RIFLESUPPORT ok={(sOk ? 1 : 0)} handR={sp.x:F5} handU={sp.y:F5} handF={sp.z:F5}");

                var chainsAll = RifleHolds.RifleChains(rig, rig.Root, "R");
                Debug.Log($"RIFLECHAINS gunValid={(chainsAll.Gun.Valid ? 1 : 0)} supValid={(chainsAll.Sup.Valid ? 1 : 0)} " +
                          $"gunHand={(chainsAll.GunHand != null ? chainsAll.GunHand.name : "null")} " +
                          $"supHand={(chainsAll.SupHand != null ? chainsAll.SupHand.name : "null")}");
                Object.DestroyImmediate(weapon.gameObject);
            }
            finally
            {
                if (rig != null) Object.DestroyImmediate(rig.Root.gameObject);
                else Object.DestroyImmediate(rootGo);
            }
            return allOk;
        }
    }
}

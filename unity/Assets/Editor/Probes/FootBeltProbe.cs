using TbilisiFly.Character;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves FootBelt.cs's named constants and WornGear's field defaults match
    /// the real numbers in src/foot.js and src/weapons.js's GEAR_DEFS.belt.
    ///
    /// Needs no GLB/body asset: it reads FootBelt's `internal const` fields
    /// directly (visible across the Assembly-CSharp / Assembly-CSharp-Editor
    /// split via AssemblyInfo.cs's InternalsVisibleTo) and a fresh
    /// `new WornGear()`'s field initialisers, which is exactly what those
    /// constants/defaults are for.
    ///
    /// tools/_probe_footbelt.mjs prints the matching KEY=value lines straight
    /// out of the real src/foot.js + src/weapons.js text (regex, not retyped).
    /// Diff the two outputs by KEY.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///         -executeMethod TbilisiFly.Probes.FootBeltProbe.Run -logFile /dev/stdout
    /// </summary>
    public static class FootBeltProbe
    {
        public static void Run()
        {
            var w = new WornGear();
            Debug.Log($"BELT_WIDTH={w.Width}");
            Debug.Log($"BELT_FIT={w.Fit}");
            Debug.Log($"BELT_DROP={w.Drop}");
            Debug.Log($"BELT_BACK={w.Back}");
            Debug.Log($"BELT_HOLSTER_SCALE={w.HolsterScale}");
            Debug.Log($"BELT_HAS_CONFORM={(w.Conform ? "true" : "false")}");
            // BELT_SOCKETS is scene/prefab wiring (socket Transform names inside
            // the instantiated belt.glb), not code — nothing to read without an
            // instantiated gear object. Not probed here; see FootBelt.cs's own
            // citation of src/weapons.js:87-88 `socket`/`sockets`.

            Debug.Log($"CLEAR_HANGERS_MARGIN={FootBelt.ClearHangersMargin}");
            Debug.Log($"CLEAR_HANGERS_SHORTFALL_LIMIT={FootBelt.ClearHangersShortfallLimit}");
            Debug.Log($"CLEAR_HANGERS_PUSH_CLAMP={FootBelt.ClearHangersPushClamp}");

            Debug.Log($"FIT_BELT_CONFORM_N={FootBelt.FitBeltConformN}");
            Debug.Log($"FIT_BELT_HALF_BAND={FootBelt.FitBeltHalfBand}");
            Debug.Log($"FIT_BELT_HITS_FLOOR_FRAC={FootBelt.FitBeltHitsFloorFrac}");
            Debug.Log($"FIT_BELT_FILL_HOLE_FRAC={FootBelt.FitBeltFillHoleFrac}");

            Debug.Log($"RING_RADIUS_N={FootBelt.RingRadiusN}");
            Debug.Log($"TORSO_HULL_SLAB={FootBelt.TorsoHullSlab}");
            Debug.Log($"TORSO_HULL_MIN_TRIS={FootBelt.TorsoHullMinTris}");
            Debug.Log($"RING_OF_BAND_HALF={FootBelt.RingOfBandHalf}");

            Debug.Log($"WAIST_Y0_FRAC={FootBelt.WaistY0Frac}");
            Debug.Log($"WAIST_Y1_FRAC={FootBelt.WaistY1Frac}");
            Debug.Log($"WAIST_Y_FRAC={FootBelt.WaistYFrac}");
            Debug.Log($"WAIST_GENERIC_FALLBACK_FRAC={FootBelt.WaistGenericFallbackFrac}");
            Debug.Log($"WAIST_CLAMP_LO={FootBelt.WaistClampLo}");
            Debug.Log($"WAIST_CLAMP_HI={FootBelt.WaistClampHi}");
            Debug.Log($"WAIST_DEFAULT={FootBelt.WaistDefault}");
            Debug.Log($"WAIST_SPREAD_LO_PCTL={FootBelt.WaistSpreadLoPctl}");
            Debug.Log($"WAIST_SPREAD_HI_PCTL={FootBelt.WaistSpreadHiPctl}");
            Debug.Log($"WAIST_SPREAD_FUDGE={FootBelt.WaistSpreadFudge}");

            Debug.Log($"SCALE_BELT_GROW_CLAMP_LO={FootBelt.ScaleBeltGrowClampLo}");
            Debug.Log($"SCALE_BELT_GROW_CLAMP_HI={FootBelt.ScaleBeltGrowClampHi}");

            // Sanity: the brute-force ray/triangle solve, checked against a known
            // analytic case rather than against JS (there is no JS-side unit for
            // this — three.js's own BVH raycaster was never ours to re-derive).
            // A unit XY triangle at z=5, ray straight down +Z from z=0 through
            // its centroid must hit at t=5.
            Vector3 v0 = new Vector3(-1, -1, 5), v1 = new Vector3(1, -1, 5), v2 = new Vector3(0, 1, 5);
            var hull = new FootBelt.Hull();
            hull.Tris.Add(v0); hull.Tris.Add(v1); hull.Tris.Add(v2);
            bool hit = hull.Raycast(new Vector3(0, -1f / 3f, 0), Vector3.forward, 10f, out Vector3 hp);
            Debug.Log($"RAYTRI_SANITY_HIT={(hit ? "true" : "false")}");
            Debug.Log($"RAYTRI_SANITY_Z={(hit ? hp.z.ToString("F4") : "NaN")}");

            RunReseat();
        }

        /// <summary>
        /// Proves FootBelt.ReseatAll (new — src/foot.js:624, the one line of
        /// `_loadBody` this file can honestly own; see its own doc comment)
        /// actually carries worn gear across a BODY RELOAD rather than being a
        /// no-op: `_rig` is `readonly`, so a reload builds a brand new
        /// FootBelt over a brand new CharacterRig, and this checks the gear's
        /// Transform really changes parent from rig1's hips bone to rig2's —
        /// two DIFFERENT GameObjects, same bone NAME — and that the new
        /// instance's own `Worn` map knows about it afterward. Unity-only;
        /// no JS counterpart line prints this (same asymmetry as this file's
        /// BODY lines above) because the JS never recreates the belt object
        /// itself, only the rig it hangs off.
        /// </summary>
        static void RunReseat()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Art/Characters/banana.fbx");
            if (prefab == null) { Debug.Log("RESEAT missing=1"); return; }

            var inst1 = Object.Instantiate(prefab);
            var inst2 = Object.Instantiate(prefab);
            GameObject gearObj = null;
            CharacterRig rig1 = null, rig2 = null;
            try
            {
                rig1 = new CharacterRig(inst1, "banana");
                rig2 = new CharacterRig(inst2, "banana");

                var belt1 = new FootBelt(rig1);
                gearObj = new GameObject("TestGear");
                var w = new WornGear { Id = "test", Obj = gearObj.transform };
                belt1.SeatGear(w);

                Transform parentAfterBelt1 = w.Obj.parent;
                bool onRig1 = parentAfterBelt1 != null && parentAfterBelt1.root == rig1.Root;
                bool wornOnBelt1 = belt1.Worn.ContainsKey("test");

                var belt2 = new FootBelt(rig2);
                belt2.ReseatAll(belt1.Worn.Values);

                Transform parentAfterBelt2 = w.Obj.parent;
                bool onRig2 = parentAfterBelt2 != null && parentAfterBelt2.root == rig2.Root;
                bool reparented = parentAfterBelt2 != parentAfterBelt1;
                bool wornOnBelt2 = belt2.Worn.ContainsKey("test");

                Debug.Log($"RESEAT id=test wornOnBelt1={(wornOnBelt1 ? 1 : 0)} onRig1={(onRig1 ? 1 : 0)} " +
                    $"reparented={(reparented ? 1 : 0)} onRig2={(onRig2 ? 1 : 0)} wornOnBelt2={(wornOnBelt2 ? 1 : 0)}");
            }
            finally
            {
                if (rig1 != null) Object.DestroyImmediate(rig1.Root.gameObject); else Object.DestroyImmediate(inst1);
                if (rig2 != null) Object.DestroyImmediate(rig2.Root.gameObject); else Object.DestroyImmediate(inst2);
                if (gearObj != null) Object.DestroyImmediate(gearObj);
            }
        }
    }
}

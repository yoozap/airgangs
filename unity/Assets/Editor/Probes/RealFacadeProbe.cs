using TbilisiFly.City;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves City/RealFacadeTextures.cs against src/citymap.js's
    /// `_loadRealFacades` (6241-6280), paired with
    /// tools/_probe_realfacades.mjs.
    ///
    /// The .mjs reads the REAL public/textures/facades/facades.json and regexes
    /// the apply() call list out of the REAL function body, so the bindings,
    /// the per-kind anisotropy and the 1/bays x 1/floors tiling are all checked
    /// against shipped data rather than against a retyped copy.
    ///
    /// CREDITS_MISSING is a licence check, not a cosmetic one: these are
    /// CC BY / CC BY-SA photographs and the credit is an obligation. A non-zero
    /// count is a real finding.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.RealFacadeProbe.Run
    /// </summary>
    public static class RealFacadeProbe
    {
        // The JS target expressions, so the printed shape matches the .mjs's
        // regex capture of the real source.
        static string SlotExpr(string slot) => slot switch
        {
            "wall0" => "wallMats[0]",
            "wall1" => "wallMats[1]",
            "wall2" => "wallMats[2]",
            "wall3" => "wallMats[3]",
            "loggia0" => "loggiaVariants[0]",
            "loggia1" => "loggiaVariants[1]",
            "loggia2" => "loggiaVariants[2]",
            "oldTbilisiB" => "oldTbilisiMatB",
            "roofFlat" => "roofFlatMat",
            "roofMetal" => "roofMetalMat",
            _ => slot,
        };

        public static void Run()
        {
            var man = RealFacadeTextures.LoadManifest();
            var all = RealFacadeTextures.AllBindings();

            Debug.Log($"BINDING_COUNT={all.Length}");
            foreach (var (slot, kind) in all)
                Debug.Log($"BINDING_{kind}={SlotExpr(slot)},{RealFacadeTextures.AnisoFor(kind)}");

            int credits = 0, missing = 0;
            foreach (var (_, kind) in all)
            {
                if (!man.TryGetValue(kind, out var e) || e == null)
                {
                    Debug.Log($"TILING_{kind}=MISSING");
                    missing++;
                    continue;
                }
                var t = RealFacadeTextures.TilingFor(e);
                Debug.Log($"TILING_{kind}={t.x:F6},{t.y:F6}");
                Debug.Log($"BAYSFLOORS_{kind}={e.Bays},{e.Floors}");
                if (string.IsNullOrEmpty(e.Credit)) missing++; else credits++;
            }
            Debug.Log($"CREDITS_PRESENT={credits}");
            Debug.Log($"CREDITS_MISSING={missing}");

            // The guard itself: the roof pair must NOT be applied from
            // facades.json once roofs.json exists, or the two loaders race.
            Debug.Log($"ROOFPAIR_GUARDED={(!RealFacadeTextures.ShouldApplyRoofPair(true)).ToString().ToLowerInvariant()}");
            Debug.Log($"MANIFEST_KINDS={man.Count}");

            ProbeExit.Exit(0);
        }
    }
}

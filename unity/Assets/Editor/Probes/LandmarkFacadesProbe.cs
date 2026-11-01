using System.Linq;
using TbilisiFly.City;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves City/LandmarkFacades.cs's table actually lines up with
    /// LandmarkPlacer.BuildProcedural's real output (not a table nobody ever
    /// queries), that every mesh it builds now carries one UV per vertex
    /// (LandmarkPlacer.cs's new UV generation — before this pass every one of
    /// these meshes had zero texture coordinates), and that every facades.json
    /// kind this table references still has its licence credit (this project's
    /// CREDITS_MISSING=0 rule, same obligation RealFacadeProbe already checks
    /// for RealFacadeTextures.cs's own 8 bindings).
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.LandmarkFacadesProbe.Run
    /// </summary>
    public static class LandmarkFacadesProbe
    {
        // landmarkKey -> expected number of tint buckets that SHOULD resolve
        // to a real facade (the rest of this task's own evidence — see
        // LandmarkFacades.cs's Table). sheratonMetechi/radissonRed expect 0.
        static readonly (string key, int expectFacaded)[] Expect =
        {
            ("narikala", 2), ("funicularStation", 1), ("metekhi", 2),
            ("svetitskhoveli", 2), ("jvari", 1), ("baths", 2),
            ("marriott", 1), ("biltmore", 1), ("sheratonMetechi", 0), ("radissonRed", 0),
        };

        public static void Run()
        {
            RunNoExit();
            ProbeExit.Exit(0);
        }

        /// <summary>Same checks as <see cref="Run"/>, without exiting the editor
        /// — so CityFinalProbe.cs can dispatch to this and ShopSignTextureProbe's
        /// RunNoExit from a single -executeMethod pass.</summary>
        public static void RunNoExit()
        {
            var manifest = RealFacadeTextures.LoadManifest();
            int creditsMissing = 0;
            var kindsUsed = new System.Collections.Generic.HashSet<string>();

            foreach (var (key, expectFacaded) in Expect)
            {
                var buckets = LandmarkPlacer.BuildProcedural(key);
                int facaded = 0;
                bool uvOk = true;
                foreach (var (mesh, tint) in buckets)
                {
                    if (mesh.uv == null || mesh.uv.Length != mesh.vertexCount) uvOk = false;
                    if (LandmarkFacades.TryGetFacade(key, tint, out var use))
                    {
                        facaded++;
                        kindsUsed.Add(use.Kind);
                    }
                }
                Debug.Log($"LANDMARKFACADE id={key} buckets={buckets.Count} facaded={facaded} expectFacaded={expectFacaded} " +
                          $"match={(facaded == expectFacaded).ToString().ToLowerInvariant()} uvOk={uvOk.ToString().ToLowerInvariant()}");
            }

            foreach (var kind in kindsUsed.OrderBy(k => k))
            {
                bool present = manifest.TryGetValue(kind, out var e) && e != null;
                bool hasCredit = present && !string.IsNullOrEmpty(manifest[kind].Credit);
                if (!hasCredit) creditsMissing++;
                Debug.Log($"LANDMARKFACADE_KIND kind={kind} manifestPresent={present.ToString().ToLowerInvariant()} hasCredit={hasCredit.ToString().ToLowerInvariant()}");
            }
            Debug.Log($"LANDMARKFACADE_SUMMARY kindsUsed={kindsUsed.Count} creditsMissing={creditsMissing}");
        }
    }
}

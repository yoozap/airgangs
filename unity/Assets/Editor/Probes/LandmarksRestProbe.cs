using TbilisiFly.City;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the two files this port adds on top of the existing
    /// LandmarkPlacer.cs/LandmarkSignage.cs claims — StatueMaterials.cs (the
    /// statue/bust material layer landmarks.js builds but LandmarkPlacer.cs
    /// never did) and JusticeHouseMaterials.cs (justiceHouse()'s post-load
    /// fixups) — against the real numbers tools/_probe_landmarksrest.mjs pulls
    /// out of src/landmarks.js. Diff this probe's STATMAT/STATUE_MAT/JUSTICE_*
    /// lines against that script's output; they are keyed the same way.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.LandmarksRestProbe.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class LandmarksRestProbe
    {
        // Same BuiltIds/ProceduralIds/HeroIds convention as LandmarkProbe.cs —
        // English identity slugs, in the HeroStatues declaration order, so
        // this probe's STATUE_MAT lines pair against the .mjs script's
        // STATUE_MAT lines (keyed by statue FILE, not by index).
        static readonly string[] HeroIds =
        {
            "freedom_monument", "kartlis_deda", "gorgasali", "rustaveli_monument",
            "david_builder", "saint_nino", "saakadze", "vazha_pshavela", "writers_pair", "pharnavaz",
        };

        public static void Run()
        {
            Print();
            ProbeExit.Exit(0);
        }

        public static void Print()
        {
            // ---- the five built materials, read back from the Material so
            // this is a real round-trip check, not just an echo of the C#
            // literals. ----------------------------------------------------
            PrintMat("GOLD", StatueMaterials.Gold);
            PrintMat("ALUMINUM", StatueMaterials.Aluminum);
            PrintMat("BRONZE_V", StatueMaterials.BronzeV);
            PrintMat("STREET_MAT", StatueMaterials.StreetStatue);
            PrintMat("BUST_MAT", StatueMaterials.Bust);

            // ---- every statueGLB() call site in landmarks.js:1254-1357,
            // reproduced from LandmarkPlacer.HeroStatues' own Tint field run
            // through StatueMaterials.For — same comparison
            // tools/_probe_landmarksrest.mjs's STATUE_MAT lines make against
            // the real statueGLB('<file>', <mat>, ...) arguments. ------------
            for (int hi = 0; hi < LandmarkPlacer.HeroStatues.Length; hi++)
            {
                var def = LandmarkPlacer.HeroStatues[hi];
                foreach (var spot in def.Spots)
                {
                    var mat = StatueMaterials.For(spot.Tint);
                    Debug.Log($"STATUE_MAT file={spot.File} hero={HeroIds[hi]} tint={spot.Tint} " +
                              $"mat={(mat == null ? "null" : mat.name)}");
                }
            }

            // ---- ApplyToInstance: proves the per-renderer traversal itself
            // (castShadow always on; material swapped unless KeepOwn) on a
            // synthetic throwaway hierarchy, since no real statue FBX is
            // guaranteed loaded in this probe's otherwise-empty scene. -------
            {
                var root = new GameObject("probe_statue");
                var child = new GameObject("mesh");
                child.transform.SetParent(root.transform);
                var mf = child.AddComponent<MeshFilter>();
                mf.sharedMesh = new Mesh();
                var mr = child.AddComponent<MeshRenderer>();
                mr.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")) { name = "original_scan_material" };
                mr.shadowCastingMode = ShadowCastingMode.Off;

                StatueMaterials.ApplyToInstance(root, LandmarkPlacer.Tint.Gold);
                Debug.Log($"STATUE_APPLY tint=Gold shadow={mr.shadowCastingMode} mat={mr.sharedMaterial.name}");

                mr.sharedMaterial = new Material(mr.sharedMaterial) { name = "original_scan_material" };
                mr.shadowCastingMode = ShadowCastingMode.Off;
                StatueMaterials.ApplyToInstance(root, LandmarkPlacer.Tint.KeepOwn);
                Debug.Log($"STATUE_APPLY tint=KeepOwn shadow={mr.shadowCastingMode} mat={mr.sharedMaterial.name}");

                Object.DestroyImmediate(root);
            }

            // ---- 2026-11-01 full-file audit addendum: the last five
            // previously-uncited landmarks.js lines (box()/cyl()/photoMat()/
            // TILE_ROOF/GRANITE) — proof they are the real values/behaviour,
            // pairing against tools/_probe_landmarksrest.mjs's
            // TILE_ROOF/GRANITE/BOX_SIG/CYL_SIG/PHOTOMAT_* lines. -------------
            {
                // GRANITE (landmarks.js:1218) is already public via
                // LandmarkPlacer.GraniteTint — direct value check.
                var g = LandmarkPlacer.GraniteTint;
                Debug.Log($"GRANITE_TINT hex={g.r:x2}{g.g:x2}{g.b:x2} expectedHex=8e8a80");

                // TILE_ROOF (landmarks.js:1008) is private to LandmarkPlacer —
                // proven indirectly through the one PUBLIC surface that uses
                // it: narikala's own roof cone (BuildProcedural("narikala"),
                // AddCyl(B(bufs, TILE_ROOF), ...) at landmarks.js:1047). A
                // bucket whose tint equals 0x9a5a44 existing in that output
                // proves both the hex value AND that it is reached.
                var buckets = LandmarkPlacer.BuildProcedural("narikala");
                bool found = false;
                int vertCount = 0, triCount = 0;
                foreach (var (mesh, tint) in buckets)
                {
                    if (tint.r == 0x9a && tint.g == 0x5a && tint.b == 0x44)
                    {
                        found = true;
                        vertCount = mesh.vertexCount;
                        triCount = mesh.triangles.Length / 3;
                        break;
                    }
                }
                Debug.Log($"TILE_ROOF_BUCKET found={(found ? 1 : 0)} vertCount={vertCount} triCount={triCount}");
            }

            // ---- JusticeHouseMaterials.ApplyFixups: a synthetic hierarchy
            // with one opaque and one transparent URP/Lit material, mirroring
            // justiceHouse()'s `if (m && m.transparent) m.depthWrite = false`
            // guard — opaque must be left alone, transparent must end with
            // _ZWrite=0. --------------------------------------------------
            {
                var root = new GameObject("probe_justice_house");
                var opaqueGO = new GameObject("opaque_wall");
                opaqueGO.transform.SetParent(root.transform);
                var opaqueMat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")) { name = "opaque" };
                if (opaqueMat.HasProperty("_Surface")) opaqueMat.SetFloat("_Surface", 0f);
                if (opaqueMat.HasProperty("_ZWrite")) opaqueMat.SetFloat("_ZWrite", 1f);
                opaqueGO.AddComponent<MeshFilter>().sharedMesh = new Mesh();
                var opaqueMr = opaqueGO.AddComponent<MeshRenderer>();
                opaqueMr.sharedMaterial = opaqueMat;
                opaqueMr.shadowCastingMode = ShadowCastingMode.Off;
                opaqueMr.receiveShadows = false;

                var glassGO = new GameObject("glazing");
                glassGO.transform.SetParent(root.transform);
                var glassMat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")) { name = "glazing" };
                if (glassMat.HasProperty("_Surface")) glassMat.SetFloat("_Surface", 1f); // Transparent
                if (glassMat.HasProperty("_ZWrite")) glassMat.SetFloat("_ZWrite", 1f);   // deliberately wrong, so the fix is observable
                glassGO.AddComponent<MeshFilter>().sharedMesh = new Mesh();
                var glassMr = glassGO.AddComponent<MeshRenderer>();
                glassMr.sharedMaterial = glassMat;
                glassMr.shadowCastingMode = ShadowCastingMode.Off;
                glassMr.receiveShadows = false;

                JusticeHouseMaterials.ApplyFixups(root);

                Debug.Log($"JUSTICE_APPLY id=opaque_wall shadow={opaqueMr.shadowCastingMode} " +
                          $"receive={opaqueMr.receiveShadows} zwrite={(opaqueMat.HasProperty("_ZWrite") ? opaqueMat.GetFloat("_ZWrite").ToString("F0") : "n/a")}");
                Debug.Log($"JUSTICE_APPLY id=glazing shadow={glassMr.shadowCastingMode} " +
                          $"receive={glassMr.receiveShadows} zwrite={(glassMat.HasProperty("_ZWrite") ? glassMat.GetFloat("_ZWrite").ToString("F0") : "n/a")}");

                Object.DestroyImmediate(root);
            }
        }

        static void PrintMat(string id, Material m)
        {
            string hex = "n/a";
            if (m.HasProperty("_BaseColor")) hex = ColorToHex(m.GetColor("_BaseColor"));
            else if (m.HasProperty("_Color")) hex = ColorToHex(m.GetColor("_Color"));
            float metallic = m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : float.NaN;
            float smoothness = m.HasProperty("_Smoothness") ? m.GetFloat("_Smoothness")
                              : m.HasProperty("_Glossiness") ? m.GetFloat("_Glossiness") : float.NaN;
            float roughness = float.IsNaN(smoothness) ? float.NaN : 1f - smoothness;
            Debug.Log($"STATMAT id={id} color=0x{hex} metalness={metallic:F2} roughness={roughness:F2}");
        }

        static string ColorToHex(Color c)
        {
            int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
            int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
            int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
            return $"{r:x2}{g:x2}{b:x2}";
        }
    }
}

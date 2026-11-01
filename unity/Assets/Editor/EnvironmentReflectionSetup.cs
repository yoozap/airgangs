using UnityEditor;
using UnityEngine;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// One-off asset-import fix for Assets/Resources/Art/Textures/env.hdr so it can be
    /// read as a Cubemap at runtime — see Core/SceneEnvironmentSetup.cs's
    /// SetupEnvironmentReflections() for why this exists (src/game.js:299-313,
    /// the PMREM sky bake + Poly Haven HDR upgrade).
    ///
    /// Before this runs, env.hdr.meta has textureShape=Texture2D (the plain
    /// equirectangular import), so AssetDatabase.LoadAssetAtPath&lt;Cubemap&gt;
    /// returns null — nothing anywhere in this project reads it as a cubemap
    /// yet (grep confirms zero references outside this file and
    /// SceneEnvironmentSetup.cs). Flips the SAME asset to a Cube-shaped import
    /// with Cylindrical (lat-long) unwrap, which is the correct layout for an
    /// equirectangular .hdr — done through the typed TextureImporter API, not
    /// hand-edited YAML enum integers, so there is no guessing which enum value
    /// means what.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///     -executeMethod TbilisiFly.EditorTools.EnvironmentReflectionSetup.Run
    /// </summary>
    public static class EnvironmentReflectionSetup
    {
        public const string EnvHdrPath = "Assets/Resources/Art/Textures/env.hdr";

        public static void Run()
        {
            var importer = AssetImporter.GetAtPath(EnvHdrPath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogError($"ENVREFLECT missing importer at {EnvHdrPath}");
                EditorApplication.Exit(1);
                return;
            }

            importer.textureShape = TextureImporterShape.TextureCube;
            importer.generateCubemap = TextureImporterGenerateCubemap.Cylindrical;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();

            var cube = AssetDatabase.LoadAssetAtPath<Cubemap>(EnvHdrPath);
            Debug.Log($"ENVREFLECT path={EnvHdrPath} shape={importer.textureShape} " +
                      $"cubemapMode={importer.generateCubemap} " +
                      $"loaded={(cube != null ? cube.GetType().Name : "null")} " +
                      $"dimension={(cube != null ? cube.dimension.ToString() : "n/a")} " +
                      $"size={(cube != null ? cube.width.ToString() : "n/a")}");
        }
    }
}

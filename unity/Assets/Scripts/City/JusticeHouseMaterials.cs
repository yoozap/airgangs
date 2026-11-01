using UnityEngine;
using UnityEngine.Rendering;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/landmarks.js:1367-1385 justiceHouse()'s post-load material
    /// fixups — the one piece of that function the existing LandmarkPlacer.cs
    /// claim doesn't cover. Its BuiltLandmarks entry for "იუსტიციის სახლი"
    /// (AssetPath = Assets/Resources/Art/Props/justice_house.fbx) is placement data only
    /// (lat/lon/yaw → position + Unity yaw degrees); it never traverses the
    /// imported prefab's renderers the way the JS traverses gltf.scene. This
    /// file is that traversal. Depends on LandmarkPlacer.cs; does not edit it.
    ///
    /// JS:
    ///   gltf.scene.traverse((o) => {
    ///     if (!o.isMesh) return;
    ///     o.castShadow = true;
    ///     o.receiveShadow = true;
    ///     for (const m of ms) {
    ///       if (m && m.transparent) m.depthWrite = false;
    ///       if (m) m.envMapIntensity = 0.6;
    ///     }
    ///   });
    /// Both literal constants (depthWrite guard, envMapIntensity = 0.6) were
    /// re-extracted from the live source by tools/_probe_landmarksrest.mjs —
    /// see docs/port_units/landmarksrest.json.
    ///
    /// Call once after the FBX is instantiated at the transform
    /// LandmarkPlacer.PlaceWorld(def) computes for that BuiltLandmarks entry.
    /// </summary>
    public static class JusticeHouseMaterials
    {
        /// <summary>
        /// ENVMAPINTENSITY HAS NO URP/LIT EQUIVALENT. Three.js's
        /// MeshStandardMaterial exposes `envMapIntensity` as a continuous 0..1+
        /// scalar; URP's stock Lit shader exposes only an Environment
        /// Reflections ON/OFF toggle (`_EnvironmentReflections`, 0 or 1), not an
        /// intensity it can be set to 0.6. There is no property to write that
        /// number into — left unset rather than guessed at with an
        /// unverifiable approximation (no `-batchmode` render to check it
        /// against).
        ///
        /// DEPTHWRITE ON A URP/LIT TRANSPARENT SURFACE IS LIKELY ALREADY A
        /// NO-OP HERE. URP's Lit shader sets ZWrite Off for Surface Type =
        /// Transparent as part of its own standard transparent setup, so this
        /// guard probably does nothing new — it is reproduced anyway because
        /// it is a precise 1:1 mirror of the JS condition (only ever touches a
        /// material that is ALREADY transparent) and costs nothing if it is
        /// redundant.
        /// </summary>
        public static void ApplyFixups(GameObject instance)
        {
            foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = ShadowCastingMode.On; // JS: o.castShadow = true
                r.receiveShadows = true;                     // JS: o.receiveShadow = true

                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    bool isTransparent =
                        (m.HasProperty("_Surface") && m.GetFloat("_Surface") >= 0.5f) ||
                        m.renderQueue >= (int)RenderQueue.Transparent;
                    if (isTransparent && m.HasProperty("_ZWrite"))
                        m.SetFloat("_ZWrite", 0f); // JS: if (m && m.transparent) m.depthWrite = false
                }
            }
        }
    }
}

using UnityEngine;
using UnityEngine.Rendering;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/landmarks.js's statue MATERIAL layer — the one piece the
    /// existing LandmarkPlacer.cs claim (505 lines, HeroStatueDef/Tint/
    /// PlaceWorld/BuildPedestal) never builds: it computes WHERE each statue
    /// spot goes and WHAT TINT it should carry, but no Unity Material for
    /// Gold/Aluminum/BronzeV/street/bust ever gets constructed anywhere in
    /// this project. Without this file <see cref="LandmarkPlacer.Tint"/> is
    /// inert data. Depends on LandmarkPlacer.cs; does not edit it.
    ///
    /// Source:
    ///   landmarks.js:63   GOLD     = MeshStandardMaterial(0xd9b13b, metalness .85, roughness .25)
    ///   landmarks.js:1219 ALUMINUM = MeshStandardMaterial(0xc9ccd1, metalness .75, roughness .35)
    ///   landmarks.js:1222 BRONZE_V = MeshStandardMaterial(0xfff6ea, metalness .45, roughness .55, vertexColors:true)
    ///   landmarks.js:1498 streetMat = MeshLambertMaterial(0xcbb89a, vertexColors:true)
    ///   landmarks.js:1504 bustMat   = MeshLambertMaterial(0xbfae92, vertexColors:true)
    ///   landmarks.js:1227-1250 statueGLB() — per mesh: o.castShadow = true;
    ///     if (mat) o.material = mat (mat=null keeps the scan's own baked
    ///     photo-texture materials — gorgasali/saakadze, Tint.KeepOwn here).
    /// All five numeric triples and every statueGLB() material argument were
    /// re-extracted from the live source by tools/_probe_landmarksrest.mjs,
    /// not retyped from memory — see docs/port_units/landmarksrest.json.
    ///
    /// NOT PORTED, ON PURPOSE: landmarks.js:1216-1217's module-level BRONZE
    /// (0x5a4a38) and PATINA (0x4f7a68) consts. Neither is referenced by any
    /// of the ten statue builders (freedomMonument..pharnavaz) — verified by
    /// tools/_probe_landmarksrest.mjs's CONST_USED_IN_STATUES check (both
    /// come back 0). They are dead code in the live game; reproducing them
    /// here would be porting something nothing ever uses. (philharmonic()'s
    /// own local `const PATINA = lambert(0x5f8f7a)` is a different value that
    /// shadows the module one and belongs to that landmark's OBJ asset, not
    /// to this file.)
    ///
    /// VERTEX-COLOUR LIMITATION. Three of these JS materials set
    /// `vertexColors: true`: BRONZE_V reads a real per-vertex patina mask
    /// baked into the FBX by tools/build_statues2.py (COLOR_0 — bronze high
    /// points, verdigris crevices), and streetMat/bustMat vary each instanced
    /// figure similarly. Unity's stock URP/Lit shader does not sample mesh
    /// vertex colour at all — there is no project-wide solution for this yet
    /// (WorshipPlacer.cs's own header says the identical thing: "a future
    /// pass can drop real photo materials on... with nothing but a
    /// vertex-colour material assigned"). The materials below carry every
    /// OTHER JS parameter correctly (base colour, metallic, smoothness), so
    /// the statues read right at a glance; the per-vertex patina VARIATION
    /// needs a vertex-colour-aware shader (Shader Graph custom node, or a
    /// hand-written .shader) that is bigger than this file and is reported
    /// here rather than invented speculatively without being able to run the
    /// editor to check it renders.
    /// </summary>
    public static class StatueMaterials
    {
        static Material _gold, _aluminum, _bronzeV, _streetMat, _bustMat;

        static Material Make(Color32 c, float metallic, float smoothness, string name)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var m = new Material(shader) { name = name };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            else if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            else if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
            return m;
        }

        /// <summary>landmarks.js:63,1264 — the 5.6 m gilded St George atop the
        /// Freedom Monument (freedomMonument() → statueGLB('george', GOLD, ...)).</summary>
        public static Material Gold =>
            _gold ??= Make(new Color32(0xd9, 0xb1, 0x3b, 255), 0.85f, 1f - 0.25f, "statue_gold");

        /// <summary>landmarks.js:1219,1274 — the 18.6 m Kartlis Deda figure
        /// (kartlisDeda() → statueGLB('kartlis_deda', ALUMINUM, ...)).</summary>
        public static Material Aluminum =>
            _aluminum ??= Make(new Color32(0xc9, 0xcc, 0xd1, 255), 0.75f, 1f - 0.35f, "statue_aluminum");

        /// <summary>landmarks.js:1222 — rustaveli/david/nino/vazha/writer_ilia/
        /// writer_akaki/pharnavaz (seven statueGLB() call sites). See the class
        /// header for the vertex-colour patina limitation.</summary>
        public static Material BronzeV =>
            _bronzeV ??= Make(new Color32(0xff, 0xf6, 0xea, 255), 0.45f, 1f - 0.55f, "statue_bronze_v");

        /// <summary>landmarks.js:1498 — the 207-point instanced street-statue
        /// layer (placeInstanced(STREET_STATUES,...)). Colour matches
        /// <see cref="LandmarkPlacer.StreetStatueTint"/> exactly (0xcbb89a) —
        /// same hex, this file just turns it into a real Material.</summary>
        public static Material StreetStatue =>
            _streetMat ??= Make(LandmarkPlacer.StreetStatueTint, 0f, 0.1f, "statue_street");

        /// <summary>landmarks.js:1504 — the 91-point instanced bust layer.
        /// Colour matches <see cref="LandmarkPlacer.BustTint"/> exactly
        /// (0xbfae92).</summary>
        public static Material Bust =>
            _bustMat ??= Make(LandmarkPlacer.BustTint, 0f, 0.1f, "statue_bust");

        /// <summary>Tint → the material statueGLB() would have assigned, or
        /// null for <see cref="LandmarkPlacer.Tint.KeepOwn"/> (JS passes
        /// mat=null to keep the photogrammetry scan's own baked photo-texture
        /// materials — gorgasali, saakadze).</summary>
        public static Material For(LandmarkPlacer.Tint tint) => tint switch
        {
            LandmarkPlacer.Tint.Gold => Gold,
            LandmarkPlacer.Tint.Aluminum => Aluminum,
            LandmarkPlacer.Tint.BronzeV => BronzeV,
            _ => null,
        };

        /// <summary>Port of statueGLB()'s per-mesh traverse (landmarks.js:
        /// 1233-1240: `if (o.isMesh) { if (mat) o.material = mat; o.castShadow
        /// = true; }`). Call once per instantiated statue spot, after the FBX
        /// prefab (Assets/Resources/Art/Statues/&lt;file&gt;.fbx) is instanced at the
        /// transform <c>LandmarkPlacer.PlaceWorld(HeroStatueDef, TerrainSampler)</c>
        /// computed for that spot.</summary>
        public static void ApplyToInstance(GameObject instance, LandmarkPlacer.Tint tint)
        {
            var mat = For(tint);
            foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = ShadowCastingMode.On; // JS: o.castShadow = true
                if (mat != null) r.sharedMaterial = mat;    // JS: if (mat) o.material = mat
            }
        }
    }
}

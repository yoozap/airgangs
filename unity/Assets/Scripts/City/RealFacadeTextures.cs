using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/citymap.js `_loadRealFacades` (6241-6280): puts the REAL
    /// photographed Tbilisi facade tiles onto the shared wall materials.
    ///
    /// This was the gap. `roofs.json`, `walls.json` and `loggia_tiles.json` all
    /// already have readers (City/WallMaterials.cs, City/CityMaterials.cs) and
    /// `textures/pb/manifest.json` has City/PhotoFacades.cs — but a grep for
    /// `facades.json` across Assets/Scripts and Assets/Editor found NO reader,
    /// so the eight photo tiles this function binds were simply never applied
    /// and every one of those walls fell back to a procedural tile. The asset
    /// side was already in place: Assets/Resources/Art/Textures/facades/ holds the .jpg
    /// files AND facades.json, imported.
    ///
    /// ================================ THE TILING IS THE POINT =============
    /// Each manifest entry carries `bays` and `floors` — how many window bays
    /// and storeys the PHOTOGRAPH itself covers. The material's tiling is
    /// therefore 1/bays by 1/floors, so one source photo maps onto exactly that
    /// many bays and storeys of wall. Getting this wrong does not fail, it just
    /// renders a building with the wrong number of windows.
    ///
    /// ================================ 16x ANISOTROPY, NOT 8 ===============
    /// panel, glass, panel2, tower16 and khrush5 get 16x; the rest get 8x. The
    /// JS says why at citymap.js:6261-6263, and it is a measured decision worth
    /// preserving: these walls are seen at grazing angles down long avenues (and
    /// towers from kilometres away), and at 8x the window grid smears into a
    /// flat slab well before the distance cull would have hidden it.
    ///
    /// ================================ THE ROOF RACE =======================
    /// roof_flat and roof_metal are in facades.json too, but they are applied
    /// here ONLY when roofs.json is absent. citymap.js:6270-6271: "roofs.json
    /// (build_roof_materials.py) owns every roof bucket now, photo tiles
    /// included — applying them here too would race that loader." Honour that;
    /// the roofs.json reader already exists in WallMaterials.cs.
    ///
    /// ================================ ATTRIBUTION =========================
    /// Every entry carries a `credit` and a `source`. These are CC BY / CC BY-SA
    /// images, so the credit is a LICENCE OBLIGATION, not a nicety. The
    /// attribution string citymap.js:6275 appends is already ported in
    /// UI/Credits/CreditsData.cs — this class exposes the per-entry credit so
    /// nothing silently drops it, but it does not duplicate that view.
    /// </summary>
    public static class RealFacadeTextures
    {
        public const string FacadeTexDir = "Assets/Resources/Art/Textures/facades";

        /// <summary>One facades.json entry. `Bays`/`Floors` default to 1 to
        /// match the JS, where a missing field divides by one.</summary>
        public sealed class Entry
        {
            public string File;
            public int Bays = 1;
            public int Floors = 1;
            public string Credit;
            public string Source;
        }

        /// <summary>The eight bindings citymap.js:6257-6266 makes, in order.
        /// `Slot` names the material the JS assigns to, so a caller holding
        /// those materials can apply them without re-deriving the mapping.</summary>
        public static readonly (string Slot, string Kind)[] Bindings =
        {
            ("wall0", "plaster"),       // citymap.js:6257
            ("wall1", "panel"),         // :6261
            ("wall2", "glass"),         // :6262
            ("wall3", "oldtbilisi"),    // :6263
            ("loggia0", "panel2"),      // :6264
            ("loggia1", "tower16"),     // :6265
            ("loggia2", "khrush5"),     // :6266
            ("oldTbilisiB", "oldtbilisi2"), // :6267
        };

        /// <summary>The roof pair, kept SEPARATE from Bindings because it is
        /// conditional — see ShouldApplyRoofPair. citymap.js:6272-6273.</summary>
        public static readonly (string Slot, string Kind)[] RoofBindings =
        {
            ("roofFlat", "roof_flat"),
            ("roofMetal", "roof_metal"),
        };

        /// <summary>All ten apply() targets the JS names, wall pair first then
        /// the conditional roof pair — the order tools/_probe_realfacades.mjs
        /// extracts from the real function body.</summary>
        public static (string Slot, string Kind)[] AllBindings()
        {
            var all = new (string, string)[Bindings.Length + RoofBindings.Length];
            Bindings.CopyTo(all, 0);
            RoofBindings.CopyTo(all, Bindings.Length);
            return all;
        }

        /// <summary>Kinds the JS gives 16x anisotropy — citymap.js:6261-6266.</summary>
        static readonly HashSet<string> Aniso16 = new HashSet<string>
        { "panel", "glass", "panel2", "tower16", "khrush5" };

        public const int AnisoHigh = 16;
        public const int AnisoDefault = 8;

        public static int AnisoFor(string kind) => Aniso16.Contains(kind) ? AnisoHigh : AnisoDefault;

        /// <summary>Tiling for a kind: 1/bays by 1/floors (citymap.js:6253).</summary>
        public static Vector2 TilingFor(Entry e) =>
            new Vector2(1f / Mathf.Max(1, e.Bays), 1f / Mathf.Max(1, e.Floors));

        /// <summary>
        /// Reads Assets/Resources/Art/Textures/facades/facades.json. Returns an empty map
        /// rather than throwing when the file is absent — the JS wraps the whole
        /// function in try/catch and comments "offline — procedural facades
        /// remain", so a missing manifest must degrade to the procedural tiles
        /// instead of failing the city load.
        /// </summary>
        public static Dictionary<string, Entry> LoadManifest(string dir = FacadeTexDir)
        {
            var map = new Dictionary<string, Entry>();
            try
            {
                // Through the loader seam, not the current working directory
                // (which is the project folder only in the editor): a player
                // run logged "facades.json missing at <cwd>/Assets/Resources/
                // Art/Textures/facades/facades.json" and every landmark lost
                // its photo facade (facadeTextures=0 against the editor's 10).
                string full = $"{dir}/facades.json";
                string json = Core.ArtAssets.LoadText(full);
                if (json == null)
                {
                    Debug.LogWarning($"RealFacadeTextures: facades.json missing at {full} — procedural facades remain");
                    return map;
                }
                var obj = JObject.Parse(json);
                foreach (var prop in obj.Properties())
                {
                    var v = prop.Value as JObject;
                    if (v == null) continue;
                    map[prop.Name] = new Entry
                    {
                        File = (string)v["file"],
                        Bays = v["bays"] != null ? (int)v["bays"] : 1,
                        Floors = v["floors"] != null ? (int)v["floors"] : 1,
                        Credit = (string)v["credit"],
                        Source = (string)v["source"],
                    };
                }
            }
            catch (System.Exception e)
            {
                // Same posture as the JS's bare catch: the city still builds.
                Debug.LogWarning($"RealFacadeTextures: facades.json not read ({e.Message}) — procedural facades remain");
            }
            return map;
        }

        /// <summary>
        /// Applies one photo tile to a material: albedo, Repeat wrapping, the
        /// 1/bays x 1/floors tiling and the kind's anisotropy. Returns false if
        /// either the material or the entry is missing, exactly as the JS's
        /// `if (!m || !mat) return;` guard does.
        /// </summary>
        public static bool Apply(Material mat, string kind, Entry e, Texture2D tex)
        {
            if (mat == null || e == null || tex == null) return false;
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.anisoLevel = AnisoFor(kind);
            mat.mainTexture = tex;
            mat.mainTextureScale = TilingFor(e);
            return true;
        }

        /// <summary>
        /// Whether the roof_flat / roof_metal pair should be applied from
        /// facades.json. False whenever roofs.json exists, because
        /// build_roof_materials.py owns every roof bucket then and applying both
        /// would race — citymap.js:6270-6271.
        /// </summary>
        public static bool ShouldApplyRoofPair(bool roofManifestPresent) => !roofManifestPresent;
    }
}

using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves PhotoFacades (the exclusive real-Commons-photo building facade
    /// draw — docs/QUALITY_BAR.md's unticked "real photo facades" box) against
    /// tools/_probe_photofacades.py, which computes the same exclusive
    /// cheapest-first assignment straight from manifest.json/city.json.gz with
    /// no Unity involved.
    ///
    /// Unlike the window/balcony placement probes this one needs NO terrain
    /// sampling and NO randomness — the assignment is pure data (lat/lon,
    /// footprint centroids, a 30 m grid, a sort). Every number here should
    /// match the Python oracle exactly, not just within tolerance.
    ///
    /// THE DECISIVE NUMBER: photofacades_maxperphoto MUST be exactly 1. That is
    /// the entire point of the exclusive draw (citymap.js:4095-4143) — the old,
    /// non-exclusive "every building within 28 m" match put the same facade on
    /// up to a dozen neighbours.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.PhotoFacadeProbe.Run
    /// </summary>
    public static class PhotoFacadeProbe
    {
        public static void Run()
        {
            var manifest = PhotoFacades.LoadManifest(PhotoFacades.DefaultManifestPath);
            // The `PF ` prefix these lines used to carry is GONE (2026-10-30,
            // pass-4 sweep). tools/_probe_photofacades.py prints the identical
            // keys bare, and a prefix changes the line SKELETON, so all 8 of
            // them paired with nothing and this probe compared ZERO values
            // while reading as verified — the exact failure mode SKILL.md
            // documents for GameRestProbe's `GR ` prefix.
            Debug.Log($"photofacades_manifest={manifest.Count}");

            var attribution = PhotoFacades.BuildAttribution(manifest, out int photographers);
            Debug.Log($"photofacades_photographers={photographers}");
            Debug.Log($"photofacades_attribution_text={attribution}");

            var city = CityData.Load();
            Debug.Log($"photofacades_cityfootprints={city.Buildings.Count}");

            var footprints = new List<float[]>(city.Buildings.Count);
            foreach (var f in city.Buildings) footprints.Add(f.Flat);

            var result = PhotoFacades.Assign(manifest, footprints);

            // max buildings sharing any single photo — must be exactly 1
            var perPhotoCount = new Dictionary<string, int>();
            foreach (var kv in result.AssignedByBuilding)
            {
                var key = kv.Value.Key;
                perPhotoCount.TryGetValue(key, out int c);
                perPhotoCount[key] = c + 1;
            }
            int maxPerPhoto = 0;
            foreach (var c in perPhotoCount.Values) if (c > maxPerPhoto) maxPerPhoto = c;

            Debug.Log($"photofacades_buildingsmatched={result.AssignedByBuilding.Count}");
            Debug.Log($"photofacades_photosused={result.UsedPhotoKeys.Count}");
            Debug.Log($"photofacades_maxperphoto={maxPerPhoto}");

            // docs/QUALITY_BAR.md's own "exclusively assigned" wording, as one
            // grep-able line: duplicates = buildings beyond the FIRST that
            // share a photo key (0 iff maxPerPhoto == 1 — the whole point of
            // the cheapest-first exclusive draw above).
            int duplicates = 0;
            foreach (var c in perPhotoCount.Values) if (c > 1) duplicates += c - 1;
            Debug.Log($"PHOTO assigned={result.AssignedByBuilding.Count} duplicates={duplicates}");

            // WHERE THIS IS REACHED AT RUNTIME. This class and
            // BuildingBuilder.BuildRich are pure data/mesh-building — neither
            // touches a GameObject. Two callers exist and they do DIFFERENT
            // things with the same Result:
            //   - City/CityRenderer.cs:EnsureBuilt() (OnEnable, so it runs
            //     every time Assets/Scenes/Game.unity is loaded, in the editor
            //     AND in the shipped player) also calls BuildRich, but only to
            //     read rich.Panels/Houses/Outs/Apts/Sheds/Relief/Props for the
            //     KIT-claimed buildings; it immediately frees rich.Chunks
            //     (ReleaseRichChunkMeshes) because the wall/roof/pb geometry a
            //     photo-winning building needs is NOT redrawn by this path.
            //   - Editor/BuildPlayableScene.cs:Run is the ACTUAL producer: it
            //     calls BuildRich once at bake time, feeds every
            //     Chunk.SubmeshNames entry (including "pb:<key>") through
            //     CityMaterials.Get to bind a real URP Material, and
            //     AssetDatabase.CreateAsset's the resulting Mesh+Material pair
            //     straight into the 604 "chunk_<n>" GameObjects it writes into
            //     Assets/Scenes/Game.unity — the ONE scene that boots the game
            //     (.claude/skills/unity/SKILL.md "Not in the scene"). Those
            //     GameObjects are what the player actually sees: the photo
            //     assignment is baked in, not recomputed per play session, the
            //     same way CityMaterials.cs's own header describes the wall/
            //     roof buckets. Editor/BuildCityRender.cs:85 is the legacy,
            //     superseded bake this class's own header already distinguishes.
            Debug.Log("PHOTO_RUNTIME caller=BuildPlayableScene.Run->BuildingBuilder.BuildRich " +
                      "bakesInto=Assets/Scenes/Game.unity materialBinder=CityMaterials.Get " +
                      "note=CityRenderer.EnsureBuilt_also_calls_BuildRich_but_frees_rich.Chunks_(kit/relief/props only)");
        }
    }
}

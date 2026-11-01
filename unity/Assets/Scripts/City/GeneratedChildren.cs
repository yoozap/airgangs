using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// The clear half of the city's "nothing is baked, OnEnable -&gt;
    /// EnsureBuilt regenerates on every load" contract (City/CityRenderer.cs's
    /// class doc; .claude/skills/unity/SKILL.md "Keep runtime city generation").
    ///
    /// WHY THIS EXISTS — a measured defect, 2026-10-29. Every scenery renderer
    /// is <c>[ExecuteAlways]</c> with <c>OnEnable -&gt; EnsureBuilt</c>, but
    /// only ShopfrontRenderer's clear walked real Transform children.
    /// ParkRenderer's and LandmarkRenderer's cleared a private
    /// <c>List&lt;GameObject&gt; _children</c> — which a SCENE LOAD leaves
    /// EMPTY, because a private field is not serialised and the deserialised
    /// GameObjects were never put in it — and MetroRenderer, MallRenderer and
    /// StreetFurnitureRenderer had no clear at all. So anything a bake had
    /// written under a renderer survived the load and the fresh build was added
    /// alongside it: the "Buildings" root measured 2,735 descendants after
    /// loading the baked scene against 1,771 after loading a stripped one —
    /// ~964 objects (including three city-spanning flower meshes) drawn TWICE,
    /// co-located, with peak load memory at 2.50 GB.
    ///
    /// Editor/BuildPlayableScene.cs now strips generated children before
    /// SaveScene, which hides it today. It is fixed HERE so it stays fixed: a
    /// renderer's clear must be able to remove children it did not personally
    /// remember creating, because after a domain reload or a scene load it
    /// never does.
    ///
    /// The <c>_children</c>/<c>_spawned</c> lists are kept where they exist —
    /// they back public counts (ParkRenderer.StaticObjectCount,
    /// MetroRenderer.SpawnedCount) and are cheap. They are simply no longer
    /// the AUTHORITY on what to destroy.
    ///
    /// DISPOSAL: a renderer that builds Meshes/Materials/Texture2Ds at runtime
    /// leaks one full set per rebuild unless they are destroyed with their
    /// children (74 runtime Texture2Ds were 71 MB of the baked scene alone).
    /// <paramref name="disposeRuntimeAssets"/> destroys exactly the
    /// non-persistent ones the destroyed children referenced — anything the
    /// AssetDatabase owns (an imported FBX's own mesh/material, every
    /// PrefabUtility.InstantiatePrefab child) is skipped, so this can never
    /// eat an asset off disk.
    ///
    /// Callers pass <c>disposeRuntimeAssets: false</c> when a STATIC cache
    /// shares the objects across builds. MetroRenderer is the one such case:
    /// its <c>_matCache</c> and <c>_nameBoardMesh</c> are deliberately built
    /// once per domain and reused, so there is nothing per-build to reclaim
    /// and destroying them would hand the next build a dead reference.
    /// </summary>
    public static class GeneratedChildren
    {
        /// <summary>Destroys every real Transform child of
        /// <paramref name="root"/> and returns how many Transforms went with
        /// them (children plus their descendants — the same figure
        /// Editor/BuildPlayableScene.cs's strip step and
        /// Editor/SceneSizeReport.cs's census count).</summary>
        public static int Clear(Transform root, bool disposeRuntimeAssets = true)
        {
            if (root == null) return 0;

            int destroyed = 0;
            var owned = disposeRuntimeAssets ? new List<Object>() : null;
            var seen = disposeRuntimeAssets ? new HashSet<Object>() : null;

            for (int i = root.childCount - 1; i >= 0; i--)
            {
                var child = root.GetChild(i).gameObject;
                destroyed += child.GetComponentsInChildren<Transform>(true).Length;
                if (owned != null) Collect(child, owned, seen);
                DestroyNow(child);
            }

            // After the GameObjects, so nothing is still referencing them when
            // they go (Unity does not care, but a half-destroyed Renderer
            // pointing at a destroyed Material logs noise in the Editor).
            if (owned != null)
                for (int i = 0; i < owned.Count; i++) DestroyNow(owned[i]);

            return destroyed;
        }

        static void Collect(GameObject go, List<Object> owned, HashSet<Object> seen)
        {
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                Own(mf.sharedMesh, owned, seen);
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                Own(smr.sharedMesh, owned, seen);

            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                if (mats == null) continue;
                foreach (var m in mats)
                {
                    // Only take a texture when the MATERIAL is ours too: a
                    // runtime material wrapping an imported texture is common
                    // (MetroRenderer.PhotoMat), and that texture is persistent
                    // so Own() drops it anyway.
                    if (!Own(m, owned, seen)) continue;
                    Own(TextureOf(m, "_BaseMap"), owned, seen);
                    Own(TextureOf(m, "_MainTex"), owned, seen);
                }
            }
        }

        static Texture TextureOf(Material m, string prop) =>
            m != null && m.HasProperty(prop) ? m.GetTexture(prop) : null;

        /// <summary>Takes ownership of <paramref name="o"/> for disposal and
        /// says whether it did. Refuses anything null, already taken, or owned
        /// by the AssetDatabase.</summary>
        static bool Own(Object o, List<Object> owned, HashSet<Object> seen)
        {
            if (o == null) return false;
            if (IsPersistent(o)) return false;
            if (!seen.Add(o)) return false;
            owned.Add(o);
            return true;
        }

        static bool IsPersistent(Object o)
        {
            return ArtAssets.IsPersistent(o);
        }

        /// <summary>ShopfrontRenderer.ClearChildren's own form — plain
        /// DestroyImmediate in edit mode, NOT Undo.DestroyObjectImmediate:
        /// an undo record per object is pointless for regenerated geometry and
        /// costs memory at these counts.</summary>
        static void DestroyNow(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }
}

using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of citymap.js:6078-6118 — the facade_kit.glb part-merging glue
    /// at the top of `_buildFacadeProps`. "Multi-material Blender objects
    /// arrive as one mesh PER material — merge them back per base name or
    /// instances render as a single white slab" (the JS's own comment,
    /// citymap.js:6085-6086).
    ///
    /// GLB LOADING IS DIFFERENT IN THIS PORT: no runtime fetch (see
    /// VegetationLoader.cs's class doc for why — GLBs import as ordinary
    /// Unity assets, not a network load). <see cref="GroupByBaseName"/> takes
    /// an already-instantiated kit root Transform where the JS takes a
    /// freshly-loaded `gltf.scene`, and is exercised in this session with a
    /// synthetic hierarchy, not the real facade_kit.glb.
    ///
    /// Uses Unity's own Mesh.CombineMeshes(mergeSubMeshes: false) for the
    /// merge, which is the direct equivalent of three.js's
    /// `mergeGeometries(geos, true)` — the boolean there means "useGroups",
    /// i.e. keep one group (submesh) per source mesh so each wears its own
    /// material, exactly what `mergeSubMeshes: false` does here.
    /// </summary>
    public static class FacadeKitLoader
    {
        // citymap.js:6091 `o.name.replace(/[._]\d+$/, '')`
        static readonly Regex VariantSuffix = new Regex(@"[._]\d+$");

        public struct KitPart
        {
            public Mesh Mesh;
            public Material[] Materials;
            /// <summary>
            /// THE PART'S OWN IMPORT TRANSFORM, in the kit root's frame — the
            /// facade half of docs/open_items.json `kit-scale-1-100`, and the
            /// fifth time .claude/skills/unity/SKILL.md's "Never reset an FBX's
            /// NATURAL import transform to identity" has been hit here.
            ///
            /// Every one of facade_kit.fbx's 24 model nodes carries
            /// `Lcl Scaling (100,100,100)` and `Lcl Rotation (-90,0,0)` (read
            /// straight out of the FBX binary), because Blender writes the
            /// metre→centimetre factor and the Z-up→Y-up turn onto the OBJECT
            /// and leaves the mesh data in the object's own frame. The FBX
            /// declares `UnitScaleFactor 1.0` and the importer runs with
            /// `useFileUnits: 1, bakeAxisConversion: 0`, so Unity keeps that
            /// split exactly as it does for the building kit: the MESH arrives
            /// at 1/100 scale and still Z-up, and the child transform carries
            /// the 100 and the -90.
            ///
            /// The JS has no such split — citymap.js:6080 loads
            /// facade_kit.GLB, whose single node is identity and whose
            /// geometry is already metres and Y-up — so returning
            /// `mf.sharedMesh` alone (what this did) handed FacadeKitBuilder a
            /// 7.8 mm air-conditioner lying on its back, 960,921 times.
            ///
            /// Composed on the RIGHT of each instance's placement TRS by
            /// FacadeKitBuilder.Put, same shape as
            /// CityRenderer.ResolveVariant's `model` matrix. Identity on the
            /// merged path below, because CombineMeshes has already baked the
            /// same matrix into the vertices there.
            /// </summary>
            public Matrix4x4 Model;
        }

        /// <summary>Compose each kit part's own import transform into the
        /// instance matrices (see <see cref="KitPart.Model"/>). SETTABLE only
        /// so Editor/Probes/FacadeRiverProbe can render the same camera with
        /// and without it in ONE run — the same reason, and the same default,
        /// as City/CityRenderer.ApplyModelTransform. Nothing in the game
        /// writes it.</summary>
        internal static bool ApplyModelTransform = true;

        public static Dictionary<string, KitPart> GroupByBaseName(Transform kitRoot)
        {
            var groups = new Dictionary<string, List<MeshFilter>>();
            foreach (var mf in kitRoot.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                string baseName = VariantSuffix.Replace(mf.gameObject.name, "");
                if (!groups.TryGetValue(baseName, out var list)) groups[baseName] = list = new List<MeshFilter>();
                list.Add(mf);
            }

            var kit = new Dictionary<string, KitPart>();
            Matrix4x4 rootWorldToLocal = kitRoot.worldToLocalMatrix;
            foreach (var kv in groups)
            {
                var list = kv.Value;
                if (list.Count == 1)
                {
                    var mf = list[0];
                    var mr = mf.GetComponent<MeshRenderer>();
                    kit[kv.Key] = new KitPart
                    {
                        Mesh = mf.sharedMesh,
                        Materials = mr != null ? mr.sharedMaterials : null,
                        // citymap.js:6093 `kit[base] = meshes[0]` keeps the raw
                        // geometry because the GLB's node is identity; the FBX's
                        // is not, so the same geometry needs the node's matrix.
                        Model = ApplyModelTransform
                            ? rootWorldToLocal * mf.transform.localToWorldMatrix
                            : Matrix4x4.identity,
                    };
                    continue;
                }
                var combine = new CombineInstance[list.Count];
                var mats = new Material[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    var mf = list[i];
                    combine[i] = new CombineInstance
                    {
                        mesh = mf.sharedMesh,
                        transform = rootWorldToLocal * mf.transform.localToWorldMatrix,
                    };
                    var mr = mf.GetComponent<MeshRenderer>();
                    mats[i] = mr != null ? mr.sharedMaterial : null;
                }
                var merged = new Mesh { name = kv.Key + "_merged" };
                merged.CombineMeshes(combine, mergeSubMeshes: false, useMatrices: true);
                // IDENTITY, SAID OUT LOUD: `default(Matrix4x4)` is all ZEROS,
                // not identity, so a KitPart that forgot this field would
                // collapse every instance of that part to a point.
                // CombineMeshes above already baked `rootWorldToLocal *
                // localToWorld` into the vertices, so there is nothing left to
                // compose — exactly as citymap.js:6096's
                // `geometry.clone().applyMatrix4(o.matrixWorld)` does.
                kit[kv.Key] = new KitPart { Mesh = merged, Materials = mats, Model = Matrix4x4.identity };
            }
            return kit;
        }
    }
}

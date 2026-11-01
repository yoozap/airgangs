using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// Shared primitive-mesh builders for Combat's own small hand-modelled
    /// props: the rocket body (Fx/RocketBodyFx.cs, src/rockets.js:147-187) and
    /// the belly-gun pod (Combat/Guns.cs's GunPod.Build, src/guns.js:393-417).
    ///
    /// BOTH of those JS blocks were previously filed as "parked for a missing
    /// asset" — wrong. Neither src/rockets.js nor src/guns.js has a
    /// GLTFLoader/loadAsync call anywhere (checked directly): the rocket body
    /// and the gun pod are 100% procedural THREE.CylinderGeometry/
    /// ConeGeometry/BoxGeometry primitives built inline, the exact same kind
    /// of fairground/park prop City/ParkBuilder.cs already ports with its own
    /// local Cylinder()/BoxAt() helpers. So this is a direct, from-scratch
    /// Unity-native port, not a stand-in for an asset that doesn't exist.
    ///
    /// These are self-contained vehicle-LOCAL props, not game-frame world
    /// geometry, so none of GeoFrame's world-mirror/winding-flip rules apply —
    /// see the two callers' own headers for the one convention that DOES
    /// matter here (Three's -Z-forward vs Unity's +Z-forward), which is why
    /// every length here is built directly in Unity's own +Z-forward sense
    /// rather than copying the JS's signs verbatim.
    ///
    /// Triangle winding for the axis-aligned box reuses City/ParkBuilder.cs's
    /// own `BoxAt` index table verbatim (already verified correct in this
    /// project) rather than re-deriving it.
    /// </summary>
    public static class ProceduralPropMesh
    {
        /// <summary>A capped cylinder (cone when rNear or rFar is 0), running
        /// along local Z, centred at `centerZ`, spanning `length`.</summary>
        public static (Vector3[] verts, int[] tris) Cylinder(float rNear, float rFar, float length, int sides, float centerZ = 0f)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            float z0 = centerZ - length / 2f, z1 = centerZ + length / 2f;
            int ring0 = verts.Count;
            for (int i = 0; i < sides; i++)
            {
                float a = (float)i / sides * Mathf.PI * 2f;
                verts.Add(new Vector3(Mathf.Cos(a) * rNear, Mathf.Sin(a) * rNear, z0));
            }
            int ring1 = verts.Count;
            for (int i = 0; i < sides; i++)
            {
                float a = (float)i / sides * Mathf.PI * 2f;
                verts.Add(new Vector3(Mathf.Cos(a) * rFar, Mathf.Sin(a) * rFar, z1));
            }
            for (int i = 0; i < sides; i++)
            {
                int j = (i + 1) % sides;
                // src/citymap.js-style side-wall winding, same table City/
                // ParkBuilder.cs's own Cylinder() already uses.
                tris.Add(ring0 + i); tris.Add(ring1 + i); tris.Add(ring1 + j);
                tris.Add(ring0 + i); tris.Add(ring1 + j); tris.Add(ring0 + j);
            }
            if (rNear > 1e-5f)
            {
                int c0 = verts.Count; verts.Add(new Vector3(0, 0, z0));
                for (int i = 0; i < sides; i++)
                {
                    int j = (i + 1) % sides;
                    tris.Add(c0); tris.Add(ring0 + j); tris.Add(ring0 + i);
                }
            }
            if (rFar > 1e-5f)
            {
                int c1 = verts.Count; verts.Add(new Vector3(0, 0, z1));
                for (int i = 0; i < sides; i++)
                {
                    int j = (i + 1) % sides;
                    tris.Add(c1); tris.Add(ring1 + i); tris.Add(ring1 + j);
                }
            }
            return (verts.ToArray(), tris.ToArray());
        }

        static readonly int[] BoxTris = { 4, 5, 6, 4, 6, 7, 1, 0, 3, 1, 3, 2, 5, 1, 2, 5, 2, 6, 0, 4, 7, 0, 7, 3, 7, 6, 2, 7, 2, 3, 0, 1, 5, 0, 5, 4 };

        /// <summary>Axis-aligned box of size w*h*d centred at `center`. Winding
        /// table identical to City/ParkBuilder.cs's own BoxAt.</summary>
        public static (Vector3[] verts, int[] tris) Box(Vector3 center, float w, float h, float d)
        {
            float x = w / 2f, y = h / 2f, z = d / 2f;
            Vector3[] v =
            {
                center + new Vector3(-x, -y, -z), center + new Vector3(x, -y, -z), center + new Vector3(x, y, -z), center + new Vector3(-x, y, -z),
                center + new Vector3(-x, -y, z), center + new Vector3(x, -y, z), center + new Vector3(x, y, z), center + new Vector3(-x, y, z),
            };
            return (v, (int[])BoxTris.Clone());
        }

        /// <summary>A box oriented by an arbitrary (ex,ey,ez) half-extent basis
        /// instead of the world axes — for the rocket's 4 radial fins, where
        /// `ex`/`ey` are the box's own rotated width/height directions, not
        /// X/Y. `ex x ey` must point the same way as `ez` (same handedness as
        /// the axis-aligned Box above) or the winding comes out inverted.</summary>
        public static (Vector3[] verts, int[] tris) OrientedBox(Vector3 center, Vector3 ex, Vector3 ey, Vector3 ez)
        {
            Vector3[] v =
            {
                center - ex - ey - ez, center + ex - ey - ez, center + ex + ey - ez, center - ex + ey - ez,
                center - ex - ey + ez, center + ex - ey + ez, center + ex + ey + ez, center - ex + ey + ez,
            };
            return (v, (int[])BoxTris.Clone());
        }

        /// <summary>Merges several (verts,tris) pieces into one Mesh, offsetting
        /// each piece's triangle indices by its own base vertex — same assembly
        /// shape as City/ParkBuilder.cs's own MergeGameFrame, minus the
        /// game-frame mirror/winding-flip step (not applicable to a local prop
        /// authored directly in Unity's own frame).</summary>
        public static Mesh Combine(IReadOnlyList<(Vector3[] verts, int[] tris)> pieces, string name)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            foreach (var (pv, pt) in pieces)
            {
                int baseI = verts.Count;
                verts.AddRange(pv);
                foreach (var t in pt) tris.Add(baseI + t);
            }
            var m = new Mesh { name = name };
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}

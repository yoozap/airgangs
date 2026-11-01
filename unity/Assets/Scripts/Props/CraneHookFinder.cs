using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.Props
{
    // =========================================================================
    // Port of src/crane.js:169-217 — finding the crane's FEET (not its
    // bounding box: the jib overhangs the portal by half the model's length,
    // so centring on the bbox would stand the crane four metres off the spot
    // it was asked for) and its HOOK (what the load hangs from), straight
    // off a loaded model's own vertices. WarehouseCrane.cs's header explicitly
    // left this unported ("needs a real imported mesh"); that need is
    // narrowed here to exactly the vertex POSITIONS and the box, both plain
    // data a caller already has once a GLB is imported — nothing below reads
    // a Mesh, a SkinnedMeshRenderer, or any live scene object, so this is
    // exercised with synthetic vertex arrays, no loaded model required.
    //
    // PORTED, in full:
    //   * the "feet" search (crane.js:173, 185-199's first `if`) — every
    //     vertex within a sill's height of the model's own floor, averaged
    //     in X/Z only (crane.js:200's `feet.getCenter()` is read for x/z
    //     alone downstream — crane.js:224 never touches its Y).
    //   * the authored-hook path (crane.js:184-187, 208) — a build that
    //     knows (tools/build_crane_machine.py writes node extras) says so,
    //     and this machine's own headache ball 2.2 m under the main block
    //     means the geometric fallback would find the WRONG thing on it.
    //   * the geometric fallback (crane.js:188-217, the old harbour crane's
    //     path) — the lowest hoist block over the outer third of the jib,
    //     averaged within a 2% band of the lowest point found.
    //
    // NOT PORTED: loading the GLB and walking its scene graph for meshes/
    // vertices (crane.js:162-199's `inner.traverse`) — City/Flight's GLB
    // pipeline, not this port's to build; a caller flattens every mesh's
    // world-space vertices into one array first (and would get `box` from
    // the same renderer bounds Unity already computes on import).
    //
    // ============================== HANDEDNESS ==============================
    // No cross product anywhere in this file — every output is an average
    // or a direct component copy. A caller feeding already-correctly-placed
    // (GeoFrame-converted, if this crane were ever re-sited outside the
    // shed) vertices gets an already-correct result; nothing here needs an
    // operand swap.
    // =========================================================================
    public static class CraneHookFinder
    {
        public struct Result
        {
            public Vector3 FeetCenter;   // x/z only meaningful — see class header
            public Vector3 Hook;
        }

        /// <summary>crane.js:169-217. `boxMin`/`boxMax` are the model's own
        /// measured Box3 (Unity: Renderer/Mesh bounds, min/max — NOT Unity's
        /// Bounds struct, which stores center/extents and would need
        /// converting first); `vertices` is every mesh's vertices already
        /// transformed to the model's own world space (crane.js:192's
        /// `v.applyMatrix4(o.matrixWorld)`); `toldHook` is the authored hook
        /// from the GLB's own node extras (crane.js:184-187) when present —
        /// pass null to force the geometric fallback (the old harbour
        /// crane's own path, crane.js:202-217).</summary>
        public static Result Compute(IReadOnlyList<Vector3> vertices, Vector3 boxMin, Vector3 boxMax, Vector3? toldHook)
        {
            float h = boxMax.y - boxMin.y;
            // crane.js:177 `outer = box.min.z + (box.max.z - box.min.z) * 0.78`
            // — the outer third of the jib, where the hoist blocks hang.
            float outerZ = boxMin.z + (boxMax.z - boxMin.z) * 0.78f;
            // crane.js:193 `v.y - box.min.y < H * 0.035` — within a sill's
            // height of the model's own floor.
            float footBand = h * 0.035f;

            bool feetAny = false;
            float feetMinX = 0f, feetMaxX = 0f, feetMinZ = 0f, feetMaxZ = 0f;
            var hookPts = new List<Vector3>();
            float hookY = float.PositiveInfinity;

            foreach (var v in vertices)
            {
                if (v.y - boxMin.y < footBand)
                {
                    if (!feetAny) { feetMinX = feetMaxX = v.x; feetMinZ = feetMaxZ = v.z; feetAny = true; }
                    else
                    {
                        if (v.x < feetMinX) feetMinX = v.x; else if (v.x > feetMaxX) feetMaxX = v.x;
                        if (v.z < feetMinZ) feetMinZ = v.z; else if (v.z > feetMaxZ) feetMaxZ = v.z;
                    }
                }
                // crane.js:194-198 — only searched when nothing was told
                // (an authored hook means the geometric search would find
                // the WRONG thing on this machine, see class header).
                if (!toldHook.HasValue && v.z > outerZ)
                {
                    hookPts.Add(v);
                    if (v.y < hookY) hookY = v.y;
                }
            }

            var result = new Result
            {
                FeetCenter = feetAny
                    ? new Vector3((feetMinX + feetMaxX) * 0.5f, 0f, (feetMinZ + feetMaxZ) * 0.5f)
                    : Vector3.zero,
            };

            if (toldHook.HasValue)
            {
                result.Hook = toldHook.Value;
                return result;
            }

            // crane.js:207 `band = hookY + H * 0.02` — the 2% band around the
            // lowest point found, which on the old harbour crane's two hoist
            // blocks (main + auxiliary, the auxiliary further out and lower)
            // picks the main block alone.
            float band = hookY + h * 0.02f;
            float sx = 0f, sz = 0f;
            int n = 0;
            foreach (var p in hookPts)
            {
                if (p.y > band) continue;
                sx += p.x; sz += p.z; n++;
            }
            if (n > 0) { sx /= n; sz /= n; }
            result.Hook = new Vector3(sx, hookY, sz);
            return result;
        }
    }
}

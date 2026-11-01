using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.UI.Map
{
    /// <summary>
    /// Port of src/game.js:2672-2699 `_syncPinBeams` — a gold beacon pillar in
    /// the WORLD for every saved map pin, so a pin dropped on the map is
    /// somewhere you can actually fly to, not just a dot on a 2D overlay.
    ///
    /// DISTINCT from MiniMapView's flat minimap dots (mapui.js's own canvas
    /// markers, read straight off <c>this.pins</c> every frame with no dirty
    /// flag at all — see mapui.js:183-186) — those are already ported via
    /// <see cref="MapUiRoot"/>/<see cref="MiniMapView"/>. This class is the
    /// OTHER consumer of <see cref="MapPinStore.Dirty"/>: the JS clears the
    /// flag inside `_syncPinBeams` itself (game.js:2675) and nothing else
    /// ever reads it. Call <see cref="Sync"/> from the SAME place GameLoop
    /// reads <c>MapUi.Pins.Dirty</c> for that reason — and read the flag
    /// BEFORE calling <see cref="MapUiRoot.Update"/>, which also consumes
    /// (and clears) it for its own minimap-cache refresh, or whichever one
    /// runs second sees it already false.
    ///
    /// SHADER: a handwritten "TbilisiFly/PinBeam" rather than URP Unlit with
    /// runtime `_Surface`/`_Blend` property pokes — URP's Unlit.shader reads
    /// the actual blend state from `_SrcBlend`/`_DstBlend`/`_ZWrite`
    /// (hidden, defaulting to One/Zero/1 = effectively opaque), which only
    /// the material INSPECTOR's GUI script ever derives from `_Surface`/
    /// `_Blend` — setting those two alone from code leaves the beam opaque.
    /// A tiny fixed-function additive shader sidesteps that trap entirely,
    /// matching the pattern Shaders/MuzzleFlash.shader and friends already
    /// use for every other additive effect in this project.
    /// </summary>
    public class PinBeams : MonoBehaviour
    {
        // src/game.js:2682 `new THREE.CylinderGeometry(5, 8, 2600, 10, 1, true)`.
        const float RadiusTop = 5f, RadiusBottom = 8f, Height = 2600f;
        const int RadialSegments = 10;

        static Mesh _sharedMesh;
        static Material _sharedMaterial;

        readonly List<GameObject> _beams = new List<GameObject>();

        void EnsureShared()
        {
            if (_sharedMesh == null) _sharedMesh = BuildOpenCylinder(RadiusTop, RadiusBottom, Height, RadialSegments);
            if (_sharedMaterial == null)
            {
                var shader = Shader.Find("TbilisiFly/PinBeam");
                if (shader == null) { Debug.LogWarning("PinBeams: missing shader TbilisiFly/PinBeam"); return; }
                // game.js:2684-2690 — 0xffd257 @ opacity 0.16, additive, no
                // depth write, double-sided, unfogged (all baked into the
                // shader's own fixed render state — see class header).
                _sharedMaterial = new Material(shader) { name = "pin_beam_mat" };
                _sharedMaterial.SetColor("_Color", new Color(1f, 0.8235f, 0.3412f, 0.16f));
            }
        }

        /// <summary>game.js:2674-2698. `pinsWorldXZ` is
        /// <see cref="MapPinStore.PinsWorldXZ"/>'s output (already
        /// GeoFrame-converted); `heightAt` is the ground-height lookup the JS
        /// calls `this.world.heightAt` (game.js:2693) — pass
        /// <c>Terrain.HeightAt</c>. Rebuilds every beam from scratch exactly
        /// like the JS does (cheap: pins are capped at
        /// <see cref="MapPinStore.MaxPins"/> = 30 and this only runs when the
        /// caller's own dirty check says to).</summary>
        public void Sync(IReadOnlyList<Vector3> pinsWorldXZ, Func<float, float, float> heightAt)
        {
            EnsureShared();
            for (int i = 0; i < _beams.Count; i++) if (_beams[i] != null) Destroy(_beams[i]);
            _beams.Clear();
            if (pinsWorldXZ == null || _sharedMaterial == null) return;

            for (int i = 0; i < pinsWorldXZ.Count; i++)
            {
                var p = pinsWorldXZ[i];
                float groundY = heightAt != null ? heightAt(p.x, p.z) : 0f;
                var go = new GameObject($"pin_beam_{i}");
                go.transform.SetParent(transform, false);
                go.transform.position = new Vector3(p.x, groundY + 1300f, p.z); // game.js:2694
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mf.sharedMesh = _sharedMesh;
                mr.sharedMaterial = _sharedMaterial;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                _beams.Add(go);
            }
        }

        public int BeamCount => _beams.Count; // probe/debug read

        // THREE.CylinderGeometry(rt, rb, h, radialSegments, 1, openEnded=true):
        // an open tube (no top/bottom caps), centred on the local origin so y
        // spans -h/2..+h/2 — matches the JS's own geometry exactly.
        static Mesh BuildOpenCylinder(float radiusTop, float radiusBottom, float height, int segments)
        {
            var verts = new List<Vector3>((segments + 1) * 2);
            var tris = new List<int>(segments * 6);
            float halfH = height * 0.5f;
            for (int i = 0; i <= segments; i++)
            {
                float ang = (float)i / segments * Mathf.PI * 2f;
                float cx = Mathf.Cos(ang), sz = Mathf.Sin(ang);
                verts.Add(new Vector3(cx * radiusTop, halfH, sz * radiusTop));
                verts.Add(new Vector3(cx * radiusBottom, -halfH, sz * radiusBottom));
            }
            for (int i = 0; i < segments; i++)
            {
                int a = i * 2, b = i * 2 + 1, c = i * 2 + 2, d = i * 2 + 3;
                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
            }
            var m = new Mesh { name = "pin_beam_cylinder" };
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}

using UnityEngine;
using TbilisiFly.Flight;

namespace TbilisiFly.Core
{
    /// <summary>
    /// Port of src/game.js:408-414 (`_setupScene`'s `this.warnGrid =
    /// new THREE.GridHelper(240, 24, 0xff1a10, 0xff1a10)`, transparent/
    /// depthWrite-off/hidden-by-default) and game.js:2460-2492 (`_tick`'s own
    /// crash-warning-grid branch): a faint red 240x240 m grid, 10 m cells, that
    /// fades in under the aeroplane only while it is airborne and under 10 m
    /// AGL, pulsing, snapped to a 10 m world raster so the lines read as a real
    /// instrument rather than scenery.
    ///
    /// Previously Core/GameLoop.cs's own `StageCombatAndFx` ran this stage as a
    /// named, empty no-op ("Unity/URP has no equivalent fullscreen-quad warning
    /// grid wired yet") — see GameLoop.WireWarnGrid below for the one-line
    /// hookup that replaces that stub.
    ///
    /// NOT a fullscreen quad (the JS's own GridHelper is real world geometry,
    /// not a screen-space overlay either) — a line-topology Mesh in the XZ
    /// plane, lifted to the aeroplane's own ground reference + 0.6 m exactly
    /// like the JS.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WarnGrid : MonoBehaviour
    {
        public PlaneController Plane;

        // src/game.js:409 GridHelper(240, 24, ...): 240 m across, 24 divisions
        // -> 10 m cells, (24+1) lines each way.
        const float Size = 240f;
        const int Divisions = 24;
        static readonly Color GridColor = new Color32(0xff, 0x1a, 0x10, 0xff); // 0xff1a10

        MeshRenderer _mr;
        Material _mat;

        void Awake()
        {
            var mf = gameObject.AddComponent<MeshFilter>();
            mf.sharedMesh = BuildGridMesh(Size, Divisions);
            _mr = gameObject.AddComponent<MeshRenderer>();
            var shader = Shader.Find("TbilisiFly/WarnGrid");
            if (shader == null)
            {
                Debug.LogWarning("WarnGrid: missing shader TbilisiFly/WarnGrid");
                enabled = false;
                return;
            }
            _mat = new Material(shader) { name = "warn_grid_mat" };
            _mat.SetColor("_Color", new Color(GridColor.r, GridColor.g, GridColor.b, 0f)); // game.js:411 opacity = 0
            _mr.sharedMaterial = _mat;
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.receiveShadows = false;
            _mr.enabled = false; // game.js:412/413 visible = false
        }

        static Mesh BuildGridMesh(float size, int divisions)
        {
            int n = divisions + 1;
            var verts = new Vector3[n * 4];
            var idx = new int[n * 4];
            float half = size * 0.5f;
            int v = 0;
            for (int i = 0; i < n; i++)
            {
                float k = -half + size * i / divisions;
                verts[v] = new Vector3(k, 0f, -half); idx[v] = v; v++;
                verts[v] = new Vector3(k, 0f, half); idx[v] = v; v++;
                verts[v] = new Vector3(-half, 0f, k); idx[v] = v; v++;
                verts[v] = new Vector3(half, 0f, k); idx[v] = v; v++;
            }
            var mesh = new Mesh { name = "WarnGridMesh" };
            mesh.vertices = verts;
            mesh.SetIndices(idx, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>src/game.js:2460-2492. `started` is the JS's own
        /// `if (this.started)` outer gate; `elapsedTime` is `this.clock.elapsedTime`.
        /// The JS's `!this.cinema?.active` guard is skipped on purpose — no
        /// cinema system exists anywhere in this Unity port (Core/GameLoop.cs's
        /// own KeepCameraOutside header makes the same call for the same reason).</summary>
        public void Tick(bool started, float elapsedTime)
        {
            if (_mat == null || _mr == null) return;
            if (!started || Plane == null || !Plane.Airborne || Plane.AltitudeAGL >= 10f)
            {
                _mr.enabled = false;
                return;
            }
            float strength = Mathf.Clamp01((10f - Plane.AltitudeAGL) / 10f); // game.js:2477
            float pulse = 0.5f + 0.5f * Mathf.Sin(elapsedTime * 9f);          // game.js:2478
            float opacity = strength * 0.35f * pulse;                        // game.js:2479
            var c = _mat.GetColor("_Color"); c.a = opacity;
            _mat.SetColor("_Color", c);
            _mr.enabled = opacity > 0.03f; // game.js:2480

            Vector3 pp = Plane.transform.position;
            float groundRefY = pp.y - Plane.AltitudeAGL; // game.js:2463-2465, airborne branch (the only reachable one here)
            // game.js:2484-2488 — snap to a 10 m world raster, JsMath.Round (not
            // Mathf.RoundToInt: JS ties round toward +infinity, Unity to even).
            float sx = Data.JsMath.Round(pp.x / 10f) * 10f;
            float sz = Data.JsMath.Round(pp.z / 10f) * 10f;
            transform.position = new Vector3(sx, groundRefY + 0.6f, sz);
        }
    }
}

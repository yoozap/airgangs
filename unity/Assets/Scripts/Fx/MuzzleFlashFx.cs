using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Combat;

namespace TbilisiFly.Fx
{
    /// <summary>
    /// Renders the two guns.js VFX sites that Combat/Guns.cs's MachineGun
    /// (read-only here, never edited) deliberately left as VFX hooks:
    ///
    ///   muzzle flash — src/guns.js:420-439,525-530 — a single reused quad
    ///     (the JS itself only ever has ONE THREE.Sprite instance, re-triggered
    ///     every shot, not one-per-bullet) via Shaders/MuzzleFlash.shader's
    ///     default pass.
    ///   tracers      — src/guns.js:441-456,533-550 — one instanced "glowing
    ///     rod" per live Tracer via Shaders/MuzzleFlash.shader's _TRACER pass,
    ///     up to MachineGun.MaxTracers (48) concurrent.
    ///
    /// NOT A MATERIAL PER SHOT (quality bar / this port's house rule): exactly
    /// ONE flash Material and ONE tracer Material exist for this object's whole
    /// lifetime, created in the constructor; firing 12 rounds/second (Machine-
    /// Gun.Rate) only mutates the single flash quad's transform/visibility and
    /// appends to the tracer instance buffer — the JS's own "a new PointLight
    /// per shot recompiles every material" bug has no code path here at all.
    ///
    /// MachineGun exposes no "just fired" event, so a shot is detected the same
    /// way RocketTrailFx detects a new rocket: diffing MachineGun.Live against
    /// what was seen last frame (a tracer present now that wasn't present a
    /// frame ago is, definitionally, brand new — tracers are only ever added
    /// at the muzzle with Dist=0, src/guns.js:501-523).
    /// </summary>
    public sealed class MuzzleFlashFx
    {
        const float FlashDuration = 0.045f; // src/guns.js:526

        public Material FlashMat;
        public Material TracerMat;
        Mesh _tracerMesh;

        float _flashT;
        Vector3 _flashPos;
        Quaternion _flashRot;
        float _flashScale;

        readonly HashSet<MachineGun.Tracer> _seen = new();
        readonly List<MachineGun.Tracer> _scratchRemove = new();

        public System.Func<float> Rng = () => UnityEngine.Random.value;

        public MuzzleFlashFx()
        {
            var sh = Shader.Find("TbilisiFly/MuzzleFlash");
            if (sh != null)
            {
                FlashMat = new Material(sh);
                TracerMat = new Material(sh) { enableInstancing = true };
                TracerMat.EnableKeyword("_TRACER");
                TracerMat.SetColor("_Color", new Color(1f, 0xce / 255f, 0x6a / 255f)); // src/guns.js:445
                TracerMat.SetFloat("_Intensity", 0.95f);
            }
            _tracerMesh = BuildTracerRod(0.18f, 4.2f); // src/guns.js:443 BoxGeometry(0.09,0.09,4.2) half-extents -> full width
        }

        static Mesh BuildTracerRod(float width, float length)
        {
            // A cross of two quads along local +Z (the muzzle direction), UV.y
            // spanning the rod's own length for Shaders/MuzzleFlash.shader's
            // _TRACER tip/tail fade. Centred at the origin like THREE.BoxGeometry.
            float hw = width * 0.5f, hl = length * 0.5f;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                int i0 = verts.Count;
                verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
                uvs.Add(new Vector2(0, 0)); uvs.Add(new Vector2(1, 0)); uvs.Add(new Vector2(1, 1)); uvs.Add(new Vector2(0, 1));
                tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 1);
                tris.Add(i0); tris.Add(i0 + 3); tris.Add(i0 + 2);
            }
            Quad(new Vector3(-hw, 0, -hl), new Vector3(hw, 0, -hl), new Vector3(hw, 0, hl), new Vector3(-hw, 0, hl));
            Quad(new Vector3(0, -hw, -hl), new Vector3(0, hw, -hl), new Vector3(0, hw, hl), new Vector3(0, -hw, hl));
            var m = new Mesh { name = "Fx_TracerRod" };
            m.SetVertices(verts); m.SetUVs(0, uvs); m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            return m;
        }

        /// <summary>Call once per frame with the gun's live tracer list.</summary>
        public void Update(float dt, IReadOnlyList<MachineGun.Tracer> live)
        {
            // New-tracer diff -> the muzzle flash trigger (src/guns.js:501-530).
            foreach (var tr in live)
            {
                if (_seen.Add(tr))
                {
                    _flashT = FlashDuration;
                    _flashPos = tr.Pos;
                    _flashRot = Quaternion.FromToRotation(Vector3.forward, tr.Dir) *
                                Quaternion.Euler(0, 0, Rng() * 360f); // guns.js:527 random roll
                    _flashScale = 1.1f + Rng() * 0.9f; // guns.js:528
                }
            }
            if (_seen.Count > live.Count)
            {
                _scratchRemove.Clear();
                foreach (var tr in _seen)
                {
                    bool found = false;
                    foreach (var l in live) if (l == tr) { found = true; break; }
                    if (!found) _scratchRemove.Add(tr);
                }
                foreach (var tr in _scratchRemove) _seen.Remove(tr);
            }

            if (_flashT > 0f) _flashT -= dt;

            RenderFlash();
            RenderTracers(live);
        }

        void RenderFlash()
        {
            if (_flashT <= 0f || FlashMat == null) return;
            var m = Matrix4x4.TRS(_flashPos, _flashRot, Vector3.one * _flashScale);
            Graphics.DrawMesh(InstancedSpriteBatch.UnitQuad, m, FlashMat, 0, null, 0, null, false, false);
        }

        readonly Matrix4x4[] _mScratch = new Matrix4x4[MachineGun.MaxTracers];

        void RenderTracers(IReadOnlyList<MachineGun.Tracer> live)
        {
            if (TracerMat == null || live.Count == 0) return;
            int n = Mathf.Min(live.Count, MachineGun.MaxTracers);
            for (int i = 0; i < n; i++)
            {
                var tr = live[i];
                Vector3 p = tr.Pos + tr.Dir * tr.Dist; // src/guns.js:544
                Quaternion rot = Quaternion.FromToRotation(Vector3.forward, tr.Dir); // src/guns.js:545
                _mScratch[i] = Matrix4x4.TRS(p, rot, Vector3.one);
            }
            Graphics.DrawMeshInstanced(_tracerMesh, 0, TracerMat, _mScratch, n, null,
                UnityEngine.Rendering.ShadowCastingMode.Off, false);
        }

        public void Dispose()
        {
            if (FlashMat != null) UnityEngine.Object.Destroy(FlashMat);
            if (TracerMat != null) UnityEngine.Object.Destroy(TracerMat);
            if (_tracerMesh != null) UnityEngine.Object.Destroy(_tracerMesh);
        }
    }
}

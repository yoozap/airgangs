using UnityEngine;
using TbilisiFly.Combat;

namespace TbilisiFly.Fx
{
    /// <summary>
    /// Renders Combat/Blast.cs's BlastFx — read-only consumer, never edited
    /// (Combat/*.cs is out of this port's scope; that file already carries the
    /// FULL simulation — fireball radius/rise curve, flash-light decay, ring
    /// expansion, crater pose — see its own header). This class's only job is
    /// turning BlastFx.Balls/Lights/Rings/Craters into real draws:
    ///
    ///   Balls   -> Shaders/Blast.shader, the boiling icosphere (src/blast.js:41-92)
    ///   Lights  -> 2 real, POOLED UnityEngine.Lights (src/blast.js:173-183's own
    ///              comment: "Created ONCE and never added/removed: changing the
    ///              light count forces three to recompile every material" — the
    ///              Unity-side discipline is the same shape even though URP's
    ///              fixed light loop has no such recompile hazard: never create
    ///              a Light per explosion, reuse these 2 forever)
    ///   Rings   -> one shared annulus Mesh + one shared Unlit Material, drawn
    ///              per-ring with a MaterialPropertyBlock override for opacity
    ///              (no custom shader needed — src/blast.js:187-195 is itself a
    ///              bare MeshBasicMaterial, not a ShaderMaterial)
    ///   Craters -> instanced quads, one shared Unlit Material (the canvas-
    ///              painted crater texture, src/blast.js:95-147, is deferred
    ///              decal ART the same way this port elsewhere defers canvas-
    ///              drawn textures — shape/pose is exact, the paint is not)
    ///
    /// Exactly FbMax(7) Blast.shader Materials and 2 Lights are created ONCE in
    /// the constructor and round-robined in lockstep with BlastFx's own Balls
    /// pool index — never one Material/Light per explosion.
    /// </summary>
    public sealed class BlastRenderer
    {
        public readonly BlastFx Sim;
        readonly Material[] _ballMats;
        readonly Mesh _ballMesh;
        readonly Light[] _lights;
        readonly Material _ringMat;
        readonly Mesh _ringMesh;
        readonly Material _craterMat;

        readonly Matrix4x4[] _craterScratch = new Matrix4x4[BlastFx.CraterMax];

        public BlastRenderer(BlastFx sim, Transform lightParent = null)
        {
            Sim = sim;
            _ballMesh = IcoSphereBuilder.Build(3);
            var blastShader = Shader.Find("TbilisiFly/Blast");
            _ballMats = new Material[BlastFx.FbMax];
            if (blastShader != null)
                for (int i = 0; i < BlastFx.FbMax; i++) _ballMats[i] = new Material(blastShader);

            _lights = new Light[2];
            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject($"BlastFlashLight{i}");
                if (lightParent != null) go.transform.SetParent(lightParent, false);
                var l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.color = new Color(1f, 0.478f, 0.188f); // 0xff7a30 — src/blast.js:179
                l.intensity = 0f;
                l.range = 420f;
                _lights[i] = l;
            }

            // Rings/craters (src/blast.js:187-220) are bare MeshBasicMaterial in
            // the JS — no GLSL — rendered here with Shaders/ImpactSprite.shader's
            // `_FLAT` mode (a plain, always-transparent, non-billboarded quad with
            // a known-correct blend state) rather than the stock URP Lit/Unlit's
            // surface-type/blend keyword machinery — see FireSystem.cs's own ring/
            // scorch materials for the same choice and the pitfall it avoids
            // (Core/CrashSequence.cs's MakeMat() comment documents it first-hand).
            var spriteShader = Shader.Find("TbilisiFly/ImpactSprite");
            _ringMesh = BuildAnnulus(0.78f, 1f, 56);
            if (spriteShader != null)
            {
                _ringMat = new Material(spriteShader);
                _ringMat.EnableKeyword("_FLAT");
                _ringMat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One); // src/blast.js:193 AdditiveBlending

                _craterMat = new Material(spriteShader) { enableInstancing = true }; // drawn via InstancedSpriteBatch/DrawMeshInstanced
                _craterMat.EnableKeyword("_FLAT");
                _craterMat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _craterMat.SetColor("_Tint", new Color(0.08f, 0.07f, 0.06f, 0.95f)); // src/blast.js:207-216 — one shared crater tint
            }
        }

        static Mesh BuildAnnulus(float innerR, float outerR, int segments)
        {
            var verts = new Vector3[(segments + 1) * 2];
            var tris = new int[segments * 6];
            for (int i = 0; i <= segments; i++)
            {
                float a = (float)i / segments * Mathf.PI * 2f;
                float ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                verts[i * 2] = new Vector3(ca * innerR, sa * innerR, 0);
                verts[i * 2 + 1] = new Vector3(ca * outerR, sa * outerR, 0);
            }
            int ti = 0;
            for (int i = 0; i < segments; i++)
            {
                int a = i * 2, b = i * 2 + 1, c = (i + 1) * 2, d = (i + 1) * 2 + 1;
                tris[ti++] = a; tris[ti++] = c; tris[ti++] = b;
                tris[ti++] = b; tris[ti++] = c; tris[ti++] = d;
            }
            var m = new Mesh { name = "Fx_Annulus" };
            m.vertices = verts; m.triangles = tris;
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Call once per frame AFTER BlastFx.Update(dt) has run.</summary>
        public void Render()
        {
            for (int i = 0; i < BlastFx.FbMax; i++)
            {
                var b = Sim.Balls[i];
                if (!b.Live) continue;
                var mat = _ballMats[i];
                if (mat == null) continue; // Shaders/Blast.shader not imported in this build yet
                mat.SetFloat("_T", Mathf.Clamp01(b.T / b.Dur));
                // Seed assigned once per spawn would need a BlastFx edit; instead
                // derived deterministically from the pool slot + spawn time so it
                // still varies ball-to-ball and burst-to-burst without touching
                // Combat/Blast.cs (src/blast.js:238 `uSeed = Math.random()*30`).
                mat.SetFloat("_Seed", (i * 7.919f + b.T * 3.1f) % 30f);
                var m = Matrix4x4.TRS(b.Pos, Quaternion.identity, Vector3.one * b.Scale);
                Graphics.DrawMesh(_ballMesh, m, mat, 0, null, 0, null, false, false);
            }

            for (int i = 0; i < Sim.Lights.Length; i++)
            {
                var e = Sim.Lights[i];
                var l = _lights[i];
                l.intensity = e.Intensity * 0.001f; // JS's raw PointLight scale -> a rough URP photometric rebase (see Core/CrashSequence.cs's TakeLight note — same unresolved unit gap, by design, until this is seen rendered)
                l.range = e.Distance;
                l.transform.position = e.Pos;
            }

            if (_ringMat != null)
            {
                foreach (var r in Sim.Rings)
                {
                    if (!r.Live) continue;
                    var mpb = new MaterialPropertyBlock();
                    mpb.SetColor("_Tint", new Color(1f, 0.91f, 0.78f, r.Opacity)); // 0xffe8c8 — src/blast.js:189
                    var m = Matrix4x4.TRS(r.Pos, r.Rot, Vector3.one * r.Scale);
                    Graphics.DrawMesh(_ringMesh, m, _ringMat, 0, null, 0, mpb);
                }
            }

            if (_craterMat != null && Sim.CraterCount > 0)
            {
                int n = Sim.CraterCount;
                for (int i = 0; i < n; i++)
                {
                    var c = Sim.Craters[i];
                    _craterScratch[i] = c.Live ? Matrix4x4.TRS(c.Position, c.Rotation, c.Scale) : Matrix4x4.zero;
                }
                InstancedSpriteBatch.Draw(InstancedSpriteBatch.UnitQuad, _craterMat, _craterScratch, null, null, n);
            }
        }

        public void Dispose()
        {
            foreach (var m in _ballMats) if (m != null) UnityEngine.Object.Destroy(m);
            if (_ballMesh != null) UnityEngine.Object.Destroy(_ballMesh);
            foreach (var l in _lights) if (l != null) UnityEngine.Object.Destroy(l.gameObject);
            if (_ringMat != null) UnityEngine.Object.Destroy(_ringMat);
            if (_ringMesh != null) UnityEngine.Object.Destroy(_ringMesh);
            if (_craterMat != null) UnityEngine.Object.Destroy(_craterMat);
        }
    }
}

using UnityEngine;

namespace TbilisiFly.Fx
{
    /// <summary>
    /// Port of src/smoke.js (80 lines) — the lightweight one-draw-call smoke
    /// trail used for plane wing damage. Its one ShaderMaterial (smoke.js:19-39)
    /// is the shared default mode of Shaders/ImpactSprite.shader (no keyword):
    /// a soft circular point sprite lerping dark-grey (0.13) to lighter grey
    /// (0.42) over particle life — exactly smoke.js:37's
    /// `mix(vec3(0.13), vec3(0.42), vLife)`.
    ///
    /// Ring buffer of MAX=140 particles, LIFETIME=1.9s — identical constants to
    /// the JS. Size, like fire.js's embers/glows, has no metres equivalent in
    /// the source (gl_PointSize in raw pixels); 0.6 m base is a judgement call
    /// for "a wisp of wing smoke", scaled by the JS's own `2.0 + life*26.0`
    /// growth SHAPE (normalised against its own life=0 value so the CURVE is
    /// exact even though the absolute size is a conversion, not a measurement).
    /// </summary>
    public sealed class SmokeTrailFx
    {
        const int Max = 140;      // src/smoke.js:5
        const float Lifetime = 1.9f; // src/smoke.js:6

        readonly Vector3[] _pos = new Vector3[Max];
        readonly Vector3[] _vel = new Vector3[Max];
        readonly float[] _life = new float[Max];
        int _cursor;

        public Material Mat;
        readonly Matrix4x4[] _mScratch = new Matrix4x4[Max];
        readonly Vector4[] _tintScratch = new Vector4[Max];

        public System.Func<float> Rng = () => UnityEngine.Random.value;

        public SmokeTrailFx()
        {
            for (int i = 0; i < Max; i++) _life[i] = 1f;
            var sh = Shader.Find("TbilisiFly/ImpactSprite");
            if (sh == null) return; // shader asset not imported yet in this build — Mat stays null, Render() no-ops
            Mat = new Material(sh) { enableInstancing = true };
            Mat.SetColor("_ColorA", new Color(0.13f, 0.13f, 0.13f));
            Mat.SetColor("_ColorB", new Color(0.42f, 0.42f, 0.42f));
            Mat.EnableKeyword("_TINTMIX");
            Mat.SetFloat("_EdgeLo", 0.5f);
            Mat.SetFloat("_EdgeHi", 0.12f);
            Mat.SetFloat("_AlphaMul", 0.55f); // src/smoke.js:35
            Mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        }

        // src/smoke.js:46-56
        public void Spawn(Vector3 pos)
        {
            int i = _cursor; _cursor = (_cursor + 1) % Max;
            _pos[i] = pos;
            _life[i] = 0.001f;
            _vel[i] = new Vector3((Rng() - 0.5f) * 2f, 2f + Rng() * 2f, (Rng() - 0.5f) * 2f);
        }

        // src/smoke.js:58-68
        public void Update(float dt)
        {
            for (int i = 0; i < Max; i++)
            {
                if (_life[i] >= 1f) continue;
                _life[i] = Mathf.Min(1f, _life[i] + dt / Lifetime);
                _pos[i] += _vel[i] * dt;
            }
            Render();
        }

        void Render()
        {
            int n = 0;
            for (int i = 0; i < Max; i++)
            {
                if (_life[i] >= 1f) continue;
                // src/smoke.js:28's `2.0 + life*26.0` growth shape, rebased onto a
                // 0.6 m judgement-call size (see class header).
                float size = 0.6f * (2.0f + _life[i] * 26.0f) / 2.0f;
                _mScratch[n] = Matrix4x4.TRS(_pos[i], Quaternion.identity, new Vector3(size, size, 1));
                _tintScratch[n] = new Vector4(0, 0, 0, _life[i]);
                n++;
            }
            InstancedSpriteBatch.Draw(InstancedSpriteBatch.UnitQuad, Mat, _mScratch, _tintScratch, null, n);
        }

        // src/smoke.js:70-73
        public void Clear()
        {
            for (int i = 0; i < Max; i++) _life[i] = 1f;
        }

        public void Dispose()
        {
            if (Mat != null) UnityEngine.Object.Destroy(Mat);
        }
    }
}

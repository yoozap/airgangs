using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.Fx
{
    /// <summary>
    /// Port of src/fire.js (717 lines) — fire + smoke for burning buildings and
    /// explosions. Plain C# class driven externally by Update(dt, camPos), same
    /// convention as Core/CrashSequence.cs and Combat/Blast.cs; constructed once
    /// by whatever owns the scene (mirrors `new FireFX(scene)`).
    ///
    /// fire.js owns 5 distinct ShaderMaterials. This port keeps that exact 5-way
    /// split but consolidates the SHADER CODE into 2 assets rather than 5,
    /// because 4 of the 5 are the same "soft point sprite" shape with different
    /// colour ramps (see Shaders/ImpactSprite.shader's header for the full
    /// rationale and per-mode source citations):
    ///
    ///   flames  (fire.js:50-114)  -> Shaders/Fireball.shader       (unique: domain-warped fbm flame body)
    ///   embers  (fire.js:130-160) -> Shaders/ImpactSprite.shader _FX_EMBER
    ///   smoke   (fire.js:185-243) -> Shaders/ImpactSprite.shader _FX_SMOKE
    ///   flashes (fire.js:260-293) -> Shaders/ImpactSprite.shader _FX_FLASH
    ///   glows   (fire.js:344-374) -> Shaders/ImpactSprite.shader _FX_GLOW
    ///
    /// Shockwave rings (fire.js:298-307) and scorch decals (fire.js:310-330) are
    /// plain untextured/canvas-textured MeshBasicMaterial in the JS too (no
    /// GLSL) — ported here with a stock URP Unlit material, same as this port's
    /// precedent elsewhere for canvas-drawn decal art (PlaneDamage.cs/
    /// BreakingGlass.cs's class headers): the SHAPE/TIMING is exact, the
    /// scorch/crater's painted radial-gradient-plus-cracks texture is deferred
    /// VFX-art work, not shader work.
    ///
    /// Materials are created ONCE in the constructor and reused for the whole
    /// system's lifetime — never per-particle, never per-frame.
    /// ======================= HANDEDNESS =======================
    /// Every position this class touches arrives (from CityMap/Combat callers)
    /// already in Unity world space; nothing here builds a tangent frame or a
    /// cross product of its own, so there is nothing to mirror or swap.
    /// ============================================================
    /// </summary>
    public sealed class FireSystem
    {
        // src/fire.js:12-16
        public const int FqMax = 900;
        public const int EMax = 700;
        public const float ELife = 1.3f;
        public const int SMax = 4200;
        public const int FlMax = 24;
        public const int GMax = 1024;
        const int ScorchMax = 220;
        const int WaveMax = 6; // src/fire.js:508 `if (this._waves.length > 6) return;`
        const int BurningCap = 16;  // src/fire.js:422
        const int SmolderCap = 18;  // src/fire.js:438
        const float Near2 = 1400f * 1400f; // src/fire.js:588

        public Func<float> Rng = () => UnityEngine.Random.value;

        sealed class Emitter { public Vector3[] Anchors; public float Intensity; public float Scale = 1f; public float Acc; }
        readonly Dictionary<int, Emitter> _burning = new();
        readonly List<int> _burningOrder = new();
        readonly Dictionary<int, Emitter> _smolder = new();
        readonly List<int> _smolderOrder = new();

        float _time;

        // ---- embers — src/fire.js:120-125 ----
        readonly Vector3[] _ePos = new Vector3[EMax];
        readonly Vector3[] _eVel = new Vector3[EMax];
        readonly float[] _eLife = new float[EMax];
        readonly float[] _eSeed = new float[EMax];
        int _eCursor;

        // ---- smoke — src/fire.js:165-173 ----
        readonly Vector3[] _sPos = new Vector3[SMax];
        readonly Vector3[] _sVel = new Vector3[SMax];
        readonly float[] _sLife = new float[SMax];
        readonly float[] _sSeed = new float[SMax];
        readonly float[] _sSize = new float[SMax];
        readonly float[] _sDark = new float[SMax];
        readonly float[] _sDur = new float[SMax];
        int _sCursor;
        internal int SCursor => _sCursor; // probe-only read (Editor/Probes/PlumeCountProbe.cs) — counts SpawnSmoke calls via the ring cursor

        // ---- explosion flashes — src/fire.js:248-256 ----
        readonly Vector3[] _flPos = new Vector3[FlMax];
        readonly float[] _flLife = new float[FlMax];
        readonly float[] _flSize = new float[FlMax];
        int _flCursor;

        // ---- persistent per-window glows — src/fire.js:332-343 ----
        readonly Vector3[] _gPos = new Vector3[GMax];
        readonly float[] _gSeed = new float[GMax];
        int _gCount;

        // ---- persistent flame quads — src/fire.js:46-49 ----
        readonly Vector3[] _fPos = new Vector3[FqMax];
        readonly float[] _fScale = new float[FqMax];
        readonly float[] _fSeed = new float[FqMax];
        readonly float[] _fInt = new float[FqMax];
        int _fCount;

        // renamed from `Wave`: the public method Wave(...) below owns that name (CS0102)
        sealed class DustWave { public Vector3 Pos; public float R, T, Dur = 1.1f; }
        readonly List<DustWave> _waves = new();

        sealed class ScorchDecal { public Vector3 Pos; public float Yaw, Size; }
        readonly ScorchDecal[] _scorch = new ScorchDecal[ScorchMax];
        int _scorchCursor, _scorchCount;

        // ---- materials, created once ----
        public Material FlameMat, EmberMat, SmokeMat, FlashMat, GlowMat, RingMat, ScorchMat;

        // scratch buffers for the batched instanced draws, sized to the worst case once
        readonly Matrix4x4[] _mScratchFq = new Matrix4x4[FqMax];
        readonly Vector4[] _propsScratchFq = new Vector4[FqMax];
        readonly Matrix4x4[] _mScratchS = new Matrix4x4[SMax];
        readonly Vector4[] _tintScratchS = new Vector4[SMax];
        readonly Vector4[] _propsScratchS = new Vector4[SMax];
        readonly Matrix4x4[] _mScratchE = new Matrix4x4[EMax];
        readonly Vector4[] _tintScratchE = new Vector4[EMax];
        readonly Matrix4x4[] _mScratchFl = new Matrix4x4[FlMax];
        readonly Vector4[] _tintScratchFl = new Vector4[FlMax];
        readonly Matrix4x4[] _mScratchG = new Matrix4x4[GMax];
        readonly Vector4[] _tintScratchG = new Vector4[GMax];

        public FireSystem()
        {
            var flameShader = Shader.Find("TbilisiFly/Fireball");
            FlameMat = flameShader != null ? new Material(flameShader) { enableInstancing = true } : null;

            EmberMat = MakeSpriteMat("TbilisiFly/ImpactSprite", "_FX_EMBER", additive: true);
            SmokeMat = MakeSpriteMat("TbilisiFly/ImpactSprite", "_FX_SMOKE", additive: false);
            FlashMat = MakeSpriteMat("TbilisiFly/ImpactSprite", "_FX_FLASH", additive: true);
            GlowMat = MakeSpriteMat("TbilisiFly/ImpactSprite", "_FX_GLOW", additive: true);

            // Rings/scorch (src/fire.js:298-330) are bare MeshBasicMaterial in the
            // JS — no GLSL — but rather than the stock URP Lit/Unlit's surface-
            // type/blend keyword machinery (which needs its keywords, render queue
            // AND blend state switched together to actually go transparent — see
            // Core/CrashSequence.cs's MakeMat() comment for this exact pitfall
            // already hit once in this project), these use Shaders/ImpactSprite.
            // shader's `_FLAT` mode: a plain, always-transparent, non-billboarded
            // quad with a known-correct blend state, one shared Material each.
            RingMat = MakeSpriteMat("TbilisiFly/ImpactSprite", "_FLAT", additive: false);
            ScorchMat = MakeSpriteMat("TbilisiFly/ImpactSprite", "_FLAT", additive: false);
            // src/fire.js:322 0x2a2624 @ opacity 0.85 — one shared tint, same as the JS's one shared scorch material.
            ScorchMat?.SetColor("_Tint", new Color(0x2a / 255f, 0x26 / 255f, 0x24 / 255f, 0.85f));
        }

        static Material MakeSpriteMat(string shaderName, string keyword, bool additive)
        {
            var sh = Shader.Find(shaderName);
            if (sh == null) return null; // shader asset not imported yet in this build — callers null-check
            var m = new Material(sh) { enableInstancing = true };
            m.EnableKeyword(keyword);
            // THREE.AdditiveBlending's real factors (SrcAlpha, One) vs NormalBlending
            // (SrcAlpha, OneMinusSrcAlpha) — see Shaders/ImpactSprite.shader's _SrcBlend/_DstBlend.
            m.SetFloat("_DstBlend", additive ? (float)UnityEngine.Rendering.BlendMode.One
                                              : (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            return m;
        }

        // ---- probe-only reads (Editor/Probes/ScorchSmolderWireProbe.cs) -------
        // internal, not public: Assets/Scripts/AssemblyInfo.cs's InternalsVisibleTo
        // is what lets the Editor assembly see them, so nothing widens for a test.
        /// <summary>How many of the 220 scorch slots have ever been stamped
        /// (src/fire.js:530's `scorch.count = max(count, i + 1)`).</summary>
        internal int ScorchCount => _scorchCount;
        internal bool TryGetScorch(int i, out Vector3 pos, out float size)
        {
            pos = Vector3.zero; size = 0f;
            if (i < 0 || i >= _scorch.Length || _scorch[i] == null) return false;
            pos = _scorch[i].Pos; size = _scorch[i].Size; return true;
        }
        /// <summary>Live emitters in each registry — the JS's `this.smolder.size`
        /// / `this.burning.size`.</summary>
        internal int SmolderCount => _smolderOrder.Count;
        internal int BurningCount => _burningOrder.Count;
        internal bool TryGetSmolder(int key, out Vector3[] anchors)
        {
            anchors = null;
            if (!_smolder.TryGetValue(key, out var em)) return false;
            anchors = em.Anchors; return true;
        }
        internal bool TryGetBurning(int key, out Vector3[] anchors, out float intensity, out float scale)
        {
            anchors = null; intensity = 0f; scale = 0f;
            if (!_burning.TryGetValue(key, out var em)) return false;
            anchors = em.Anchors; intensity = em.Intensity; scale = em.Scale; return true;
        }
        /// <summary>Smoke puffs still alive (life &lt; 1). Goes back to 0 once the
        /// last one has aged out — which is how a probe shows the columns FADE
        /// rather than accumulating (src/fire.js:623 `dur: 4.5`).</summary>
        internal int LiveSmokeCount
        {
            get { int n = 0; for (int i = 0; i < SMax; i++) if (_sLife[i] < 1f) n++; return n; }
        }
        /// <summary>Persistent flame quads currently built from the burning set
        /// (src/fire.js:379-409 _rebuildGlows).</summary>
        internal int FlameQuadCount => _fCount;
        /// <summary>One smoke slot's world position and life, so a probe can
        /// match live puffs back to the mound anchors that spawned them (which is
        /// what makes "N smouldering columns" a measurement and not an
        /// assumption).</summary>
        internal bool TryGetSmoke(int i, out Vector3 pos, out float life)
        {
            pos = Vector3.zero; life = 1f;
            if (i < 0 || i >= SMax) return false;
            pos = _sPos[i]; life = _sLife[i]; return true;
        }

        // ---- emitter management — src/fire.js:411-442 ----

        public void SetBurning(int bi, Vector3[] anchors, float intensity, float scale = 1f)
        {
            if (anchors == null || anchors.Length == 0) { _burning.Remove(bi); _burningOrder.Remove(bi); }
            else
            {
                _burning.Remove(bi); _burningOrder.Remove(bi);
                _burning[bi] = new Emitter { Anchors = anchors, Intensity = intensity, Scale = scale };
                _burningOrder.Add(bi);
                while (_burningOrder.Count > BurningCap)
                {
                    _burning.Remove(_burningOrder[0]);
                    _burningOrder.RemoveAt(0);
                }
            }
            RebuildFlames();
        }

        public void ClearBurning(int bi)
        {
            _burning.Remove(bi); _burningOrder.Remove(bi);
            RebuildFlames();
        }

        public void SetSmolder(int bi, Vector3[] anchors)
        {
            if (anchors != null && anchors.Length > 0)
            {
                _smolder.Remove(bi); _smolderOrder.Remove(bi);
                _smolder[bi] = new Emitter { Anchors = anchors };
                _smolderOrder.Add(bi);
                while (_smolderOrder.Count > SmolderCap)
                {
                    _smolder.Remove(_smolderOrder[0]);
                    _smolderOrder.RemoveAt(0);
                }
            }
            else { _smolder.Remove(bi); _smolderOrder.Remove(bi); }
        }

        // src/fire.js:379-409 _rebuildGlows — one persistent flame quad + one
        // distance glow per burning window, rebuilt only when the burning set changes.
        void RebuildFlames()
        {
            int n = 0;
            foreach (int bi in _burningOrder)
            {
                var em = _burning[bi];
                var ax = em.Anchors;
                int count = ax.Length;
                for (int i = 0; i < count && n < Mathf.Min(GMax, FqMax); i++, n++)
                {
                    _gPos[n] = ax[i];
                    _gSeed[n] = (n * 7.13f) % 10f;
                    float s = (1.15f + em.Intensity * 0.75f + ((n * 2.7f) % 1f) * 0.35f) * em.Scale;
                    _fPos[n] = new Vector3(ax[i].x, ax[i].y - 0.5f, ax[i].z);
                    _fScale[n] = s;
                    _fSeed[n] = (n * 3.71f) % 10f;
                    _fInt[n] = em.Intensity;
                }
            }
            _gCount = n;
            _fCount = n;
        }

        // ---- direct spawns — src/fire.js:446-478 ----

        public void SpawnEmber(float x, float y, float z)
        {
            int i = _eCursor; _eCursor = (_eCursor + 1) % EMax;
            _ePos[i] = new Vector3(x + (Rng() - 0.5f) * 0.6f, y + Rng() * 0.8f, z + (Rng() - 0.5f) * 0.6f);
            _eLife[i] = 0.001f;
            _eSeed[i] = Rng() * 10f;
            _eVel[i] = new Vector3((Rng() - 0.5f) * 1.4f, 2.5f + Rng() * 2.5f, (Rng() - 0.5f) * 1.4f);
        }

        public void SpawnSmoke(float x, float y, float z, float size = 5f, float dark = 0.75f, float dur = 3.2f,
            float vx = 0f, float vy = 2.2f, float vz = 0f, float spread = 0.8f)
        {
            int i = _sCursor; _sCursor = (_sCursor + 1) % SMax;
            _sPos[i] = new Vector3(x + (Rng() - 0.5f) * spread, y + (Rng() - 0.5f) * spread * 0.5f, z + (Rng() - 0.5f) * spread);
            _sLife[i] = 0.001f;
            _sSeed[i] = Rng() * 10f;
            _sSize[i] = size * (0.75f + Rng() * 0.5f);
            _sDark[i] = dark;
            _sDur[i] = dur * (0.8f + Rng() * 0.4f);
            _sVel[i] = new Vector3(vx + (Rng() - 0.5f) * 1.2f, vy * (0.75f + Rng() * 0.5f), vz + (Rng() - 0.5f) * 1.2f);
        }

        // src/fire.js:480-504 — kill-shot fireball: layered flashes + embers + black smoke.
        public void Explosion(float x, float y, float z, float r)
        {
            for (int k = 0; k < 5; k++)
            {
                int i = _flCursor; _flCursor = (_flCursor + 1) % FlMax;
                _flPos[i] = new Vector3(x + (Rng() - 0.5f) * r * 0.8f, y + (Rng() - 0.3f) * r * 0.7f, z + (Rng() - 0.5f) * r * 0.8f);
                _flLife[i] = 0.001f + k * 0.04f;
                _flSize[i] = r * (1.6f + Rng() * 1.2f);
            }
            for (int k = 0; k < 14; k++) SpawnEmber(x, y + r * 0.3f, z);
            for (int k = 0; k < 10; k++)
            {
                SpawnSmoke(x, y + Rng() * r * 0.5f, z, size: r * 0.9f + Rng() * r * 0.5f, dark: 0.85f, dur: 3f,
                    vy: 5f + Rng() * 4f, spread: r * 0.5f);
            }
        }

        // src/fire.js:507-515 — expanding dust ring hugging the ground.
        public void Wave(float x, float groundY, float z, float r)
        {
            if (_waves.Count > WaveMax) return;
            _waves.Add(new DustWave { Pos = new Vector3(x, groundY + 0.4f, z), R = r, T = 0f });
        }

        // src/fire.js:518-532 — permanent dark burn mark on the ground.
        public void StampScorch(float x, float groundY, float z, float size)
        {
            int i = _scorchCursor; _scorchCursor = (_scorchCursor + 1) % ScorchMax;
            _scorch[i] = new ScorchDecal { Pos = new Vector3(x, groundY + 0.22f, z), Yaw = Rng() * 360f, Size = size };
            _scorchCount = Mathf.Max(_scorchCount, i + 1);
        }

        // src/fire.js:537-578 — collapse dust avalanche: ground surge + boiling
        // column + a lingering slow canopy.
        public void Plume(float cx, float baseY, float cz, float radius, float height)
        {
            // src/fire.js:537 `80 + Math.round(radius * 10)` — a loop bound
            // (particle count), so the tie-to-even-vs-tie-to-+inf disagreement
            // between Mathf.RoundToInt and JS Math.round is load-bearing here:
            // a building radius landing on an exact .05 m after ×10 spawns one
            // fewer ground-surge puff than the JS does. Use JsMath.Round.
            int n = Mathf.Min(240, 80 + Data.JsMath.Round(radius * 10f));
            for (int k = 0; k < n; k++)
            {
                float a = Rng() * Mathf.PI * 2f;
                float r = radius * (0.25f + Rng() * 0.85f);
                SpawnSmoke(cx + Mathf.Cos(a) * r, baseY + 1f + Rng() * 4f, cz + Mathf.Sin(a) * r,
                    size: 18f + Rng() * 16f, dark: 0.25f + Rng() * 0.2f, dur: 6.5f,
                    vx: Mathf.Cos(a) * (3.5f + Rng() * 5f), vz: Mathf.Sin(a) * (3.5f + Rng() * 5f), vy: 1.6f, spread: 3.5f);
            }
            for (int k = 0; k < 80; k++)
            {
                SpawnSmoke(cx, baseY + Rng() * (height + 6f), cz, size: 18f + Rng() * 16f, dark: 0.35f, dur: 7f,
                    vy: 3f + Rng() * 4f, spread: radius * 0.8f);
            }
            for (int k = 0; k < 55; k++)
            {
                float a = Rng() * Mathf.PI * 2f;
                float r = radius * Rng() * 1.1f;
                SpawnSmoke(cx + Mathf.Cos(a) * r, baseY + 2f + Rng() * (height * 0.6f + 4f), cz + Mathf.Sin(a) * r,
                    size: 26f + Rng() * 20f, dark: 0.24f, dur: 10.5f, vy: 0.55f,
                    vx: (Rng() - 0.5f) * 0.8f, vz: (Rng() - 0.5f) * 0.8f, spread: 4f);
            }
        }

        // ---- per-frame — src/fire.js:580-693 ----

        public void Update(float dt, Vector3 camPos)
        {
            _time += dt;

            foreach (var kv in _burning)
            {
                var em = kv.Value;
                var ax = em.Anchors;
                float d2 = (ax[0].x - camPos.x) * (ax[0].x - camPos.x) + (ax[0].z - camPos.z) * (ax[0].z - camPos.z);
                if (d2 > Near2) continue;
                int nA = ax.Length;
                float sc = em.Scale;
                em.Acc += dt * nA * (1.6f + 2.2f * em.Intensity) * sc;
                while (em.Acc >= 1f)
                {
                    em.Acc -= 1f;
                    int a = Mathf.FloorToInt(Rng() * nA) % Mathf.Max(1, nA);
                    SpawnEmber(ax[a].x, ax[a].y + 1.4f * sc, ax[a].z);
                    if (Rng() < 0.4f)
                        SpawnSmoke(ax[a].x, ax[a].y + 1.8f * sc, ax[a].z, size: (5.5f + em.Intensity * 4f) * sc,
                            dark: 0.82f, dur: 3.8f + sc, vy: 2.8f + sc);
                }
            }
            foreach (var kv in _smolder)
            {
                var em = kv.Value;
                var ax = em.Anchors;
                float d2 = (ax[0].x - camPos.x) * (ax[0].x - camPos.x) + (ax[0].z - camPos.z) * (ax[0].z - camPos.z);
                if (d2 > Near2) continue;
                em.Acc += dt * ax.Length * 1.1f;
                while (em.Acc >= 1f)
                {
                    em.Acc -= 1f;
                    int a = Mathf.FloorToInt(Rng() * ax.Length) % ax.Length;
                    SpawnSmoke(ax[a].x, ax[a].y, ax[a].z, size: 6.5f, dark: 0.55f, dur: 4.5f, vy: 1.6f, spread: 2.5f);
                }
            }

            for (int i = 0; i < FlMax; i++)
            {
                if (_flLife[i] >= 1f) continue;
                _flLife[i] = Mathf.Min(1f, _flLife[i] + dt / 0.55f);
                _flPos[i].y += 3.5f * dt;
            }

            for (int i = _waves.Count - 1; i >= 0; i--)
            {
                var w = _waves[i];
                w.T += dt;
                if (w.T / w.Dur >= 1f) _waves.RemoveAt(i);
            }

            for (int i = 0; i < EMax; i++)
            {
                if (_eLife[i] >= 1f) continue;
                _eLife[i] = Mathf.Min(1f, _eLife[i] + dt / ELife);
                _eVel[i].x += Mathf.Sin(_time * 7f + _eSeed[i] * 9f) * 3f * dt;
                _eVel[i].z += Mathf.Cos(_time * 6f + _eSeed[i] * 7f) * 3f * dt;
                _ePos[i] += _eVel[i] * dt;
            }

            for (int i = 0; i < SMax; i++)
            {
                if (_sLife[i] >= 1f) continue;
                _sLife[i] = Mathf.Min(1f, _sLife[i] + dt / _sDur[i]);
                _sVel[i].x *= 1f - 0.55f * dt;
                _sVel[i].z *= 1f - 0.55f * dt;
                _sPos[i] += _sVel[i] * dt;
            }

            Render(camPos);
        }

        // ---- rendering: instanced draws through Fireball.shader / ImpactSprite.shader ----

        void Render(Vector3 camPos)
        {
            // flames — persistent quads, pivot biased -0.5 in Y (fire.js:395),
            // uniform scale (anisotropy/breathing happens in-shader).
            for (int i = 0; i < _fCount; i++)
            {
                _mScratchFq[i] = Matrix4x4.TRS(_fPos[i], Quaternion.identity, Vector3.one * _fScale[i]);
                _propsScratchFq[i] = new Vector4(_fSeed[i], _fInt[i], 0, 0);
            }
            InstancedSpriteBatch.Draw(InstancedSpriteBatch.UnitQuad, FlameMat, _mScratchFq, null, _propsScratchFq, _fCount);

            // embers — size is an approximation: fire.js drives gl_PointSize in
            // raw pixels ((0.5-0.3*life)*(330/dist)), which has no metres
            // equivalent; 0.35 m is a judgement call for "a hot spark", scaled
            // by the same (1-0.6*life) shrink curve the JS uses.
            int ne = 0;
            for (int i = 0; i < EMax; i++)
            {
                if (_eLife[i] >= 1f) continue;
                float size = 0.35f * (1f - 0.6f * _eLife[i]);
                _mScratchE[ne] = Matrix4x4.TRS(_ePos[i], Quaternion.identity, new Vector3(size, size, 1));
                _tintScratchE[ne] = new Vector4(0, 0, 0, _eLife[i]);
                ne++;
            }
            InstancedSpriteBatch.Draw(InstancedSpriteBatch.UnitQuad, EmberMat, _mScratchE, _tintScratchE, null, ne);

            // smoke — size IS in real metres already (spawnSmoke's own `size`
            // param), grown by fire.js:214's `0.35 + 0.65*life`.
            int ns = 0;
            for (int i = 0; i < SMax; i++)
            {
                if (_sLife[i] >= 1f) continue;
                float grow = 0.35f + 0.65f * _sLife[i];
                float size = _sSize[i] * grow;
                _mScratchS[ns] = Matrix4x4.TRS(_sPos[i], Quaternion.identity, new Vector3(size, size, 1));
                _tintScratchS[ns] = new Vector4(0, 0, 0, _sLife[i]);
                _propsScratchS[ns] = new Vector4(_sSeed[i], _sDark[i], 0, 0);
                ns++;
            }
            InstancedSpriteBatch.Draw(InstancedSpriteBatch.UnitQuad, SmokeMat, _mScratchS, _tintScratchS, _propsScratchS, ns);

            // flashes — size IS in real metres (explode()'s own `r`), swollen
            // by fire.js:277's `0.6 + 1.1*life`.
            int nf = 0;
            for (int i = 0; i < FlMax; i++)
            {
                if (_flLife[i] >= 1f) continue;
                float swell = 0.6f + 1.1f * _flLife[i];
                float size = _flSize[i] * swell;
                _mScratchFl[nf] = Matrix4x4.TRS(_flPos[i], Quaternion.identity, new Vector3(size, size, 1));
                _tintScratchFl[nf] = new Vector4(0, 0, 0, _flLife[i]);
                nf++;
            }
            InstancedSpriteBatch.Draw(InstancedSpriteBatch.UnitQuad, FlashMat, _mScratchFl, _tintScratchFl, null, nf);

            // glows — persistent, size is a judgement call (a window-sized
            // dot, ~0.22 m) — fire.js's own flicker (gSeed-driven) is folded
            // into the instance scale here instead of a per-frame uniform.
            for (int i = 0; i < _gCount; i++)
            {
                float flick = 0.75f + 0.25f * Mathf.Sin(_time * 13f + _gSeed[i] * 51f) * Mathf.Sin(_time * 7.3f + _gSeed[i] * 13f);
                float size = 0.22f * flick;
                _mScratchG[i] = Matrix4x4.TRS(_gPos[i], Quaternion.identity, new Vector3(size, size, 1));
                _tintScratchG[i] = new Vector4(0, 0, 0, 0);
            }
            InstancedSpriteBatch.Draw(InstancedSpriteBatch.UnitQuad, GlowMat, _mScratchG, _tintScratchG, null, _gCount);

            // shockwave rings + scorch decals — plain quads, canvas-art deferred
            // (see class header); shape/timing below is exact, art is not.
            if (RingMat != null)
            {
                foreach (var w in _waves)
                {
                    float p = Mathf.Min(1f, w.T / w.Dur);
                    float scale = w.R * (0.4f + 2.6f * p);
                    var mpb = new MaterialPropertyBlock();
                    float o = 0.55f * (1f - p) * (1f - p); // src/fire.js:648
                    mpb.SetColor("_Tint", new Color(0.847f, 0.804f, 0.729f, o)); // 0xd8cdba — src/fire.js:303
                    Graphics.DrawMesh(InstancedSpriteBatch.UnitQuad, Matrix4x4.TRS(w.Pos, Quaternion.Euler(90, 0, 0), Vector3.one * scale),
                        RingMat, 0, null, 0, mpb);
                }
            }
            if (ScorchMat != null)
            {
                for (int i = 0; i < _scorchCount; i++)
                {
                    var s = _scorch[i];
                    if (s == null) continue;
                    Graphics.DrawMesh(InstancedSpriteBatch.UnitQuad,
                        Matrix4x4.TRS(s.Pos, Quaternion.Euler(90, s.Yaw, 0), new Vector3(s.Size, s.Size, 1)),
                        ScorchMat, 0);
                }
            }
        }

        public void Dispose()
        {
            foreach (var m in new[] { FlameMat, EmberMat, SmokeMat, FlashMat, GlowMat, RingMat, ScorchMat })
                if (m != null) UnityEngine.Object.Destroy(m);
        }
    }
}

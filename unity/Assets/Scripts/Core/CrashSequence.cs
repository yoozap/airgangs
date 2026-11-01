using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.Core
{
    /// <summary>
    /// The Georgian death screen's own strings — index.html:260-265, byte for
    /// byte. THERE IS NO ENGLISH "WASTED" HERE AND THERE MUST NEVER BE ONE —
    /// the user has explicitly rejected English UI strings in this game. The
    /// grid-tag line is the one English-looking string, and it is NOT a
    /// translation choice made by this port: it is the shipped game's own
    /// cyberpunk HUD convention (bracket/slash glitch-tags appear throughout
    /// the existing UI, e.g. "◄ SETTINGS //" in index.html's settings panel) —
    /// carried across unchanged, not introduced here.
    /// </summary>
    public static class DeathScreenText
    {
        public const string GridTag = "■ SIGNAL LOST // GRID-41.72N"; // index.html:262
        public const string Title = "ჩამოვარდი";                      // index.html:263 — "I crashed / went down"
        public const string Subtitle = "კორპუსი განადგურებულია";       // index.html:264 — "the hull is destroyed"
        public const string RestartButton = "კიდევ ერთხელ ↻";          // index.html:265 — "once more"
    }

    /// <summary>fire.js's spawnSmoke option bag (src/fire.js:460) — fire.js itself is out of this port's scope (VFX Graph), this is just the DATA shape CrashSequence hands to it.</summary>
    public struct SmokeOpts
    {
        public float Size, Dark, Dur, Vx, Vy, Vz, Spread;
        public static SmokeOpts Default => new SmokeOpts { Size = 5f, Dark = 0.75f, Dur = 3.2f, Vy = 2.2f, Spread = 0.8f };
    }

    /// <summary>
    /// Everything CrashSequence needs from systems that are explicitly OUT of
    /// this port's scope (fire.js/smoke.js -&gt; VFX Graph; the pooled fx-light
    /// system and ground sampling live in game.js/City). Every hook is
    /// optional — CrashSequence runs (and the numbers it drives are still
    /// exactly right) with none of them wired, so this class does not have to
    /// wait on Combat/City/VFX work to exist. Un-set hooks for lights fall
    /// back to a plain (unpooled) UnityEngine.Light.
    /// </summary>
    public class CrashHooks
    {
        /// <summary>game._groundAt(x,z) — src/game.js. Null if off the sampled grid.</summary>
        public Func<float, float, float?> GroundAt;
        /// <summary>game.sound.explosion(intensity) — already ported: EngineSound.Explosion.</summary>
        public Action<float> Explosion;
        public Action<Vector3> SpawnEmber;
        public Action<Vector3, SmokeOpts> SpawnSmoke;
        /// <summary>fire.js's plume(cx, baseY, cz, radius, height).</summary>
        public Action<float, float, float, float, float> Plume;
        /// <summary>fire.js's setBurning(key, anchors, intensity) — anchors are WORLD positions.</summary>
        public Action<string, Vector3[], float> SetBurning;
        public Action<string> ClearBurning;
        /// <summary>game.takeFxLight(color, intensity, distance, decay) — pooled; falls back to a fresh Light if unset.</summary>
        public Func<Color, float, float, float, Light> TakeFxLight;
        public Action<Light> FreeFxLight;
        /// <summary>game._showWasted() — owned by the (not yet ported) UI layer; expected to be idempotent itself.</summary>
        public Action ShowWasted;
    }

    /// <summary>
    /// Port of src/crash.js (400 lines) — the cinematic crash sequence:
    /// impact -&gt; tumble (trailing fire/smoke, maybe a bounce) -&gt; settle ->
    /// burning wreck -&gt; the death screen.
    ///
    /// Plain C# class, driven externally by Update(dt) every frame — same
    /// convention as FlightModel.cs. Constructed once per crash; Dispose()
    /// tears it down (mirrors the JS's `dispose()`, called from resetPlane()).
    ///
    /// NOT PORTED: the fireball's own GLSL shader (makeFireballMaterial,
    /// src/crash.js:17-81 — boiling 3D-noise-displaced geometry with a
    /// blackbody colour ramp) is shader work, out of this port's scope the
    /// same way every onBeforeCompile shader is (see docs/UNITY_PORT.md).
    /// What IS ported exactly: every timing number (t/dur/R/scale), the
    /// light's intensity curve, and the debris/wreck physics. The fireball
    /// here is a plain sphere whose material colour/alpha is driven by the
    /// SAME formula the fragment shader used, minus the per-pixel noise —
    /// correct silhouette and timing, flat instead of boiling. Swap in a real
    /// Shader Graph "fireball" material later and the driving code needs no
    /// changes (ApplyFireballLook below is the one seam).
    ///
    /// ======================= HANDEDNESS =======================
    /// Every `plane.rotateX/Y/Z(angVel.*, dt)` in the JS is a LOCAL-axis
    /// rotation. Per FlightModel.cs's header: these are component-identical
    /// between the engines, but because forward's LABEL flips (Three -Z,
    /// Unity +Z), the physical result on the nose inverts for pitch (local X)
    /// and yaw (local Y), and does not for roll (local Z) — so every spin
    /// here (both the wreck's and the debris chunks') negates X and Y and
    /// keeps Z, via ApplyLocalSpin. This is applied uniformly even though the
    /// angular velocities here are random tumble values (not aerodynamic
    /// ones): the plane's STARTING orientation going into the crash is real
    /// flight attitude from FlightModel, so the same pitch/yaw/roll axes mean
    /// the same things here as they do there, and must follow the same rule.
    ///
    /// The one true cross product in this file — the side-step off the
    /// camera's "back" vector that places the death-cam to one side
    /// (src/crash.js:130 `new THREE.Vector3(-back.z, 0, back.x)`, which is
    /// algebraically `back.cross(up)`) — gets its operands SWAPPED per
    /// FlightModel's rule: `Vector3.Cross(Vector3.up, back)`. Verified by hand
    /// (see the report that shipped with this port): cross(back,up) in the
    /// mirrored frame equals -cross(up,back), so the swap is what recovers
    /// the correctly-mirrored side vector instead of its negative.
    ///
    /// `Vector3.Reflect` (the wall-smash wreck bounce) is dot-product based
    /// (v - 2(v·n)n) and is mirror-INVARIANT — no swap, no sign flip, direct
    /// port, checked and confirmed NOT a cross product despite looking like
    /// one at a glance.
    /// ============================================================
    /// </summary>
    public class CrashSequence
    {
        const float Gravity = 14f; // src/crash.js:14 — the tumbling hull's own stand-in; real debris falls at true 9.81
        const int DebrisN = 16;

        readonly CrashHooks _hooks;
        readonly Transform _plane;
        readonly Transform _camera;
        readonly float _size;
        readonly float _camScale;

        float _t;
        float _timeScale = 0.45f;
        string _phase = "tumble";
        bool _bounced;
        float? _wastedIn;
        float _trailAcc;

        Vector3 _vel;
        Vector3 _angVel; // kept in the JS's OWN sign convention — negated only at the point of use, see ApplyLocalSpin
        float _groundEst;
        Vector3 _camAnchor;

        readonly List<(Material mat, Color color, float smoothness, bool hadSmoothness)> _matBackup = new();
        readonly List<DebrisChunk> _debris = new();
        readonly List<FireballState> _fireballs = new();
        readonly List<Light> _ownedLights = new();
        Material[] _debrisMats;
        Mesh _tetraMesh;
        GameObject _group;

        struct DebrisChunk
        {
            public Transform T;
            public Vector3 Vel, AngVel;
            public bool Alive, Hot;
        }

        class FireballState
        {
            public GameObject Go;
            public Material Mat;
            public Light Light;
            public float T, Dur, R, Scale;
        }

        /// <summary>src/crash.js:83-143.</summary>
        public CrashSequence(Transform plane, Transform camera, float size, float camScale,
            Vector3 currentVel, bool airborne, float speed, float lastGroundDistance,
            CrashHooks hooks, Vector3? hitNormal = null)
        {
            _hooks = hooks ?? new CrashHooks();
            _plane = plane;
            _camera = camera;
            _size = size <= 0f ? 12f : size;
            _camScale = camScale;

            _groundEst = _hooks.GroundAt?.Invoke(plane.position.x, plane.position.z)
                         ?? (plane.position.y - lastGroundDistance);

            // Wreck momentum: keep part of the TRUE velocity vector (not the
            // nose), or kick back along the hit surface's normal.
            _vel = (airborne && currentVel.sqrMagnitude > 4f)
                ? currentVel * 0.45f
                : plane.forward * Mathf.Max(8f, Mathf.Abs(speed) * 0.4f); // HANDEDNESS: plane.forward IS the Unity equivalent of the JS's (0,0,-1)*quat
            if (hitNormal.HasValue)
            {
                Vector3 n = hitNormal.Value.normalized;
                _vel = Vector3.Reflect(_vel, n) * 0.35f; // dot-product based — mirror-invariant, no operand swap
            }
            _vel.y = Mathf.Min(_vel.y, 4f);
            _angVel = new Vector3(
                (UnityEngine.Random.value - 0.5f) * 5f,
                (UnityEngine.Random.value - 0.5f) * 3f,
                (UnityEngine.Random.value - 0.5f) * 6f);

            _group = new GameObject("CrashSequence");

            // Fixed cinematic vantage point: back and off to the side of the impact.
            Vector3 back = camera.position - plane.position;
            back.y = 0f;
            if (back.sqrMagnitude < 1f) back = Vector3.forward; // JS's (0,0,1) fallback — Unity forward IS (0,0,1), no change needed
            back.Normalize();
            // HANDEDNESS: see the class header — swapped from the JS's back×up.
            Vector3 side = Vector3.Cross(Vector3.up, back)
                * ((UnityEngine.Random.value < 0.5f ? -1f : 1f) * 13f * camScale);
            _camAnchor = plane.position + back * (32f * camScale) + side + new Vector3(0f, 9f * camScale, 0f);
            _camAnchor.y = Mathf.Max(_camAnchor.y, _groundEst + 4f);

            CharPlane();
            SpawnDebris();
            Boom(plane.position, 1f);
        }

        // ---- staging ----

        /// <summary>src/crash.js:147-162 — the hull chars instantly; original look restored in Dispose().</summary>
        void CharPlane()
        {
            _matBackup.Clear();
            var seen = new HashSet<Material>();
            foreach (var r in _plane.GetComponentsInChildren<Renderer>())
            {
                foreach (var m in r.sharedMaterials) // sharedMaterials: mutating one affects every mesh using it, same aliasing the JS relies on
                {
                    if (m == null || !seen.Add(m)) continue;
                    bool hasSmooth = m.HasProperty("_Smoothness");
                    _matBackup.Add((m, GetColorSafe(m), hasSmooth ? m.GetFloat("_Smoothness") : 0f, hasSmooth));
                    SetColorSafe(m, new Color(0x21 / 255f, 0x1e / 255f, 0x1a / 255f));
                    if (hasSmooth) m.SetFloat("_Smoothness", 0f); // roughness=1 -> smoothness=0
                }
            }
        }

        /// <summary>src/crash.js:164-195.</summary>
        void SpawnDebris()
        {
            float s = _size / 11f; // relative to a Spitfire
            _tetraMesh = BuildTetrahedron();
            _debrisMats = new[]
            {
                MakeMat(new Color(0x2a/255f, 0x26/255f, 0x22/255f), 0f),
                MakeMat(new Color(0x4a/255f, 0x44/255f, 0x3c/255f), 0.1f),
            };
            for (int i = 0; i < DebrisN; i++)
            {
                float scale = (0.25f + UnityEngine.Random.value * 0.6f) * s;
                var go = new GameObject($"debris{i}");
                go.transform.SetParent(_group.transform, false);
                go.transform.position = _plane.position;
                go.transform.localScale = Vector3.one * scale;
                var mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = _tetraMesh;
                var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = _debrisMats[i % _debrisMats.Length];
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;

                float a = UnityEngine.Random.value * Mathf.PI * 2f;
                float up = UnityEngine.Random.value;
                float sp = 9f + UnityEngine.Random.value * 22f;
                // A fresh random scatter direction parametrised by (cos a, up, sin a) —
                // not derived from any mirrored JS data, so there is nothing to mirror here.
                Vector3 dir = new Vector3(Mathf.Cos(a) * (1f - up), up, Mathf.Sin(a) * (1f - up)).normalized;
                Vector3 vel = dir * sp + _vel * 0.4f;
                _debris.Add(new DebrisChunk
                {
                    T = go.transform,
                    Vel = vel,
                    AngVel = UnityEngine.Random.onUnitSphere * (4f + UnityEngine.Random.value * 8f),
                    Alive = true,
                    Hot = i < 8,
                });
            }
        }

        static Material MakeMat(Color c, float smoothness)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var m = new Material(sh);
            SetColorSafe(m, c);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            return m;
        }

        /// <summary>URP/Lit names its main colour `_BaseColor`; the built-in Standard shader names it `_Color`. `Material.color` is not reliably redirected across both, so this checks explicitly instead of trusting it.</summary>
        static void SetColorSafe(Material m, Color c)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            else if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        static Color GetColorSafe(Material m)
        {
            if (m.HasProperty("_BaseColor")) return m.GetColor("_BaseColor");
            if (m.HasProperty("_Color")) return m.GetColor("_Color");
            return Color.white;
        }

        static Mesh BuildTetrahedron()
        {
            // A regular tetrahedron, authored fresh and directly in Unity's own
            // winding convention (CCW front faces as seen from outside) — this
            // is new geometry, not mirrored JS data, so GeoFrame.FlipWinding does
            // not apply (that is only for triangles carried over from game-frame
            // vertex data — see GeoFrame.cs's header).
            Vector3 a = new Vector3(1, 1, 1), b = new Vector3(-1, -1, 1), c = new Vector3(-1, 1, -1), d = new Vector3(1, -1, -1);
            var verts = new[] { a, b, c, d };
            var tris = new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 };
            var mesh = new Mesh { vertices = verts, triangles = tris };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>src/crash.js:197-228. See the class header for what the fireball visual does NOT include (the real shader).</summary>
        void Boom(Vector3 pos, float scale)
        {
            float R = _size * 0.85f * scale;
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "fireball";
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(_group.transform, false);
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * (R * 0.3f);
            // Opaque placeholder material: a URP Lit material needs its surface
            // type, render queue AND blend keywords all switched together for
            // real alpha blending (the one-liner this used to be here did not
            // actually do that) — getting alpha blending right is part of the
            // real fireball shader this stands in for, not something to half-do
            // here. ApplyFireballLook still drives `.color`'s alpha channel so
            // the number is correct and ready the moment a transparent material
            // replaces this one.
            var mat = MakeMat(new Color(0.22f, 0.012f, 0.001f), 0f);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;

            Light light = TakeLight(new Color(1f, 0.5f, 0.2f), 3500f * scale * scale, R * 18f, 1.8f);
            light.transform.SetParent(_group.transform, false);
            light.transform.position = pos + new Vector3(0f, R * 0.4f, 0f);

            _fireballs.Add(new FireballState { Go = go, Mat = mat, Light = light, T = 0f, Dur = 0.95f, R = R, Scale = scale });

            int nEmber = Mathf.RoundToInt(45 * scale);
            for (int i = 0; i < nEmber; i++) _hooks.SpawnEmber?.Invoke(pos);
            int nSmoke = Mathf.RoundToInt(40 * scale);
            for (int i = 0; i < nSmoke; i++)
            {
                float a = UnityEngine.Random.value * Mathf.PI * 2f;
                Vector3 p = new Vector3(pos.x, pos.y + UnityEngine.Random.value * R * 0.8f, pos.z);
                _hooks.SpawnSmoke?.Invoke(p, new SmokeOpts
                {
                    Size = (9f + UnityEngine.Random.value * 9f) * scale, Dark = 0.88f, Dur = 5.5f,
                    Vx = Mathf.Cos(a) * 2.5f, Vz = Mathf.Sin(a) * 2.5f, Vy = 4.5f, Spread = R * 0.5f,
                });
            }
        }

        Light TakeLight(Color c, float intensity, float range, float decay)
        {
            if (_hooks.TakeFxLight != null) return _hooks.TakeFxLight(c, intensity, range, decay);
            var go = new GameObject("crash_fx_light");
            var l = go.AddComponent<Light>();
            l.type = LightType.Point; l.color = c; l.intensity = intensity; l.range = range;
            // NOTE: intensity here is the JS's raw Three.js PointLight scale
            // (hundreds-to-thousands); Unity/URP's photometric units are a
            // different scale entirely and this will want re-tuning by eye once
            // it is actually rendered.
            _ownedLights.Add(l);
            return l;
        }

        void FreeLight(Light l)
        {
            if (l == null) return;
            if (_hooks.FreeFxLight != null) { _hooks.FreeFxLight(l); return; }
            if (_ownedLights.Remove(l)) UnityEngine.Object.Destroy(l.gameObject);
        }

        /// <summary>src/crash.js:230-261 — the wreck comes to rest and starts burning.</summary>
        void Settle(float gY)
        {
            _phase = "burning";
            _plane.position = new Vector3(_plane.position.x, gY + 1.0f, _plane.position.z);

            // Mostly flat, but visibly wrong. APPROXIMATION: the JS decomposes
            // the quaternion in explicit 'YXZ' Euler order and clamps pitch/roll;
            // Unity's eulerAngles uses a different internal composition order,
            // so this reproduces the SAME shape of clamp (not a byte-exact
            // YXZ decomposition match) — fine for a small cosmetic rest-pose tilt.
            Vector3 e = _plane.eulerAngles;
            float ex = WrapDeg(e.x) * Mathf.Deg2Rad;
            float ez = WrapDeg(e.z) * Mathf.Deg2Rad;
            ex = Mathf.Clamp(ex * 0.3f, -0.2f, 0.2f) + 0.06f;
            ez = Mathf.Clamp(ez * 0.3f, -0.3f, 0.3f) + (UnityEngine.Random.value < 0.5f ? -0.14f : 0.14f);
            _plane.rotation = Quaternion.Euler(ex * Mathf.Rad2Deg, e.y, ez * Mathf.Rad2Deg);

            var anchors = new Vector3[4];
            int i = 0;
            foreach (float zf in new[] { -0.32f, -0.12f, 0.08f, 0.26f })
            {
                Vector3 local = new Vector3((UnityEngine.Random.value - 0.5f) * _size * 0.12f, 0.8f, zf * _size);
                anchors[i++] = _plane.TransformPoint(local);
            }
            _hooks.SetBurning?.Invoke("crash:plane", anchors, 1.0f);
            _hooks.Plume?.Invoke(_plane.position.x, gY, _plane.position.z, _size * 0.6f, 4f);

            Vector3 toCam = _camera.position - _plane.position;
            toCam.y = 0f;
            if (toCam.sqrMagnitude < 1f) toCam = Vector3.forward;
            toCam.Normalize();
            _camAnchor = _plane.position + toCam * (26f * _camScale) + new Vector3(0f, 8f * _camScale, 0f);
            _wastedIn = 1.1f;
        }

        static float WrapDeg(float deg)
        {
            deg %= 360f;
            if (deg > 180f) deg -= 360f;
            if (deg < -180f) deg += 360f;
            return deg;
        }

        // ---- per-frame — src/crash.js:265-326 ----

        public void Update(float dt)
        {
            _t += dt;
            _timeScale = _t < 1.0f ? 0.45f : Mathf.Min(1f, 0.45f + (_t - 1.0f) * 0.9f);
            float sdt = dt * _timeScale;

            if (_phase == "tumble")
            {
                _vel.y -= Gravity * sdt;
                _vel *= (1f - 0.12f * sdt);
                _plane.position += _vel * sdt;
                ApplyLocalSpin(_plane, _angVel, sdt);

                _trailAcc += sdt;
                if (_trailAcc > 0.03f)
                {
                    _trailAcc = 0f;
                    Vector3 p = _plane.position;
                    _hooks.SpawnEmber?.Invoke(p);
                    _hooks.SpawnSmoke?.Invoke(p + Vector3.up, new SmokeOpts
                    { Size = 6f + UnityEngine.Random.value * 4f, Dark = 0.85f, Dur = 3.5f, Vy = 1.6f, Spread = _size * 0.18f });
                }

                float gY = _hooks.GroundAt?.Invoke(_plane.position.x, _plane.position.z) ?? _groundEst;
                if (_plane.position.y <= gY + 1.4f)
                {
                    if (!_bounced && _vel.magnitude > 14f)
                    {
                        _bounced = true;
                        _plane.position = new Vector3(_plane.position.x, gY + 1.4f, _plane.position.z);
                        _vel.y = Mathf.Abs(_vel.y) * 0.3f;
                        _vel.x *= 0.45f; _vel.z *= 0.45f;
                        _angVel *= 0.55f;
                        Boom(_plane.position, 0.55f);
                        _hooks.Explosion?.Invoke(0.55f);
                        _hooks.Plume?.Invoke(_plane.position.x, gY, _plane.position.z, _size * 0.7f, 5f);
                    }
                    else
                    {
                        Settle(gY);
                    }
                }
            }
            else if (_wastedIn.HasValue)
            {
                _wastedIn -= dt;
                if (_wastedIn <= 0f) { _wastedIn = null; _hooks.ShowWasted?.Invoke(); }
            }
            if (_t > 6f) _hooks.ShowWasted?.Invoke(); // safety valve — never leave the player with no restart

            UpdateDebris(sdt);
            UpdateFireballs(sdt);
            UpdateCamera(dt);
        }

        /// <summary>
        /// HANDEDNESS (see class header): forward flips label between engines,
        /// so a local rotation's physical effect inverts for pitch (local X)
        /// and yaw (local Y), not for roll (local Z). `v` stays in the JS's own
        /// sign convention everywhere else; this is the one place it gets applied.
        /// </summary>
        static void ApplyLocalSpin(Transform t, Vector3 v, float dt)
        {
            // crash.js:278-280 / 333-335 rotateX/Y/Z(av * sdt) are RADIANS;
            // Transform.Rotate takes DEGREES — the same slip FlightModel had
            // (found 2026-10-31 through the stick): without Rad2Deg the wreck
            // tumbled 57x too slowly.
            t.Rotate(-v.x * Mathf.Rad2Deg * dt, 0f, 0f, Space.Self);
            t.Rotate(0f, -v.y * Mathf.Rad2Deg * dt, 0f, Space.Self);
            t.Rotate(0f, 0f, v.z * Mathf.Rad2Deg * dt, Space.Self);
        }

        void UpdateDebris(float sdt)
        {
            for (int i = 0; i < _debris.Count; i++)
            {
                var d = _debris[i];
                if (!d.Alive) continue;
                d.Vel.y -= Gravity * sdt;
                d.T.position += d.Vel * sdt;
                ApplyLocalSpin(d.T, d.AngVel, sdt);
                if (d.Hot && UnityEngine.Random.value < 0.35f)
                {
                    Vector3 p = d.T.position;
                    _hooks.SpawnEmber?.Invoke(p);
                    if (UnityEngine.Random.value < 0.3f)
                        _hooks.SpawnSmoke?.Invoke(p, new SmokeOpts { Size = 2.5f, Dark = 0.8f, Dur = 1.6f, Vy = 0.8f, Spread = 0.3f });
                }
                if (d.T.position.y <= _groundEst + 0.3f)
                {
                    var pos = d.T.position; pos.y = _groundEst + 0.3f; d.T.position = pos;
                    d.Vel.y = Mathf.Abs(d.Vel.y) * 0.35f;
                    d.Vel.x *= 0.55f; d.Vel.z *= 0.55f;
                    d.AngVel *= 0.5f;
                    if (d.Vel.magnitude < 2.5f) d.Alive = false;
                }
                _debris[i] = d;
            }
        }

        void UpdateFireballs(float sdt)
        {
            foreach (var f in _fireballs)
            {
                if (f.T >= 1f) continue;
                f.T = Mathf.Min(1f, f.T + sdt / f.Dur);
                ApplyFireballLook(f);
                f.Go.transform.localScale = Vector3.one * (f.R * (0.3f + 0.7f * Mathf.Pow(f.T, 0.42f)));
                f.Go.transform.position += new Vector3(0f, 3.5f * sdt, 0f);
                f.Light.intensity = 3500f * f.Scale * f.Scale * (1f - f.T) * (1f - f.T);
                if (f.T >= 1f)
                {
                    f.Go.SetActive(false);
                    f.Light.intensity = 0f;
                }
            }
        }

        /// <summary>
        /// The one seam between the ported timing (exact, untouched above) and
        /// the fireball's LOOK. WIRED (2026): Boom() still creates the flat-
        /// sphere placeholder via MakeMat (that call site is outside this
        /// method and this port may not touch it — see the class header) —
        /// this method now lazily swaps that placeholder Material for a real
        /// Shaders/Blast.shader one, exactly ONCE per FireballState (never
        /// per-frame, never per-shot: Boom() is itself only called once per
        /// impact/bounce, matching src/crash.js's own 1-2 calls per crash), and
        /// drives it with `_T` every frame after that. Shaders/Blast.shader's
        /// `_CRASH` keyword selects src/crash.js:17-81's own constants (3
        /// fbm octaves, no soot mixing, its own heat/body/alpha formula) rather
        /// than src/blast.js's — see that shader's header for the full
        /// src-line citations of both tunings it carries. If the shader asset
        /// is missing from this build (no Shaders/Blast.shader imported yet),
        /// this falls back to the EXACT flat-colour formula this method always
        /// used, so CrashSequence keeps working and keeps its probed numbers
        /// unchanged either way.
        /// </summary>
        void ApplyFireballLook(FireballState f)
        {
            if (f.Mat == null || f.Mat.shader == null || f.Mat.shader.name != "TbilisiFly/Blast")
            {
                var blastShader = Shader.Find("TbilisiFly/Blast");
                if (blastShader != null)
                {
                    var mesh = TbilisiFly.Fx.IcoSphereBuilder.Build(3);
                    var mat = new Material(blastShader);
                    mat.EnableKeyword("_CRASH");
                    mat.SetFloat("_Seed", UnityEngine.Random.value * 20f); // src/crash.js:47 uSeed = Math.random()*20
                    var mf = f.Go.GetComponent<MeshFilter>();
                    if (mf != null) mf.sharedMesh = mesh; // swap the primitive Sphere for the real icosphere Blast.shader expects
                    var mr = f.Go.GetComponent<MeshRenderer>();
                    if (mr != null)
                    {
                        var old = mr.sharedMaterial;
                        mr.sharedMaterial = mat;
                        if (old != null) UnityEngine.Object.Destroy(old);
                    }
                    f.Mat = mat;
                }
            }

            if (f.Mat != null && f.Mat.shader != null && f.Mat.shader.name == "TbilisiFly/Blast")
            {
                f.Mat.SetFloat("_T", Mathf.Clamp01(f.T)); // Shaders/Blast.shader's _CRASH branch owns the rest of the curve
                return;
            }

            // Fallback: the ORIGINAL flat-sphere formula, unchanged, so a build
            // without the shader asset still renders something with the exact
            // right colour/alpha curve.
            const float vD = 0.4f;
            float heat = Mathf.Clamp01(vD * 1.3f - f.T * 1.15f + 0.25f);
            Color c = Color.Lerp(new Color(0.22f, 0.012f, 0.001f), new Color(0.95f, 0.16f, 0.006f), Smoothstep(0.1f, 0.62f, heat));
            c = Color.Lerp(c, new Color(1.0f, 0.5f, 0.1f), Smoothstep(0.8f, 1.0f, heat) * (1f - f.T));
            float body = Smoothstep(0.14f, 0.45f, vD + 0.3f * (1f - f.T));
            float a = Mathf.Min(Mathf.Pow(1f - f.T, 2.2f) * body * 2.2f, 0.97f);
            a *= 1f - Smoothstep(0.5f, 0.75f, f.T);
            c.a = a;
            SetColorSafe(f.Mat, c);
            if (f.Mat.HasProperty("_EmissionColor"))
            {
                f.Mat.EnableKeyword("_EMISSION");
                f.Mat.SetColor("_EmissionColor", c * 2f);
            }
        }

        static float Smoothstep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        void UpdateCamera(float dt)
        {
            _camera.position = Vector3.Lerp(_camera.position, _camAnchor, 1f - Mathf.Exp(-2.0f * dt));
            float shake = Mathf.Exp(-2.4f * _t) * 2.2f;
            Vector3 p = _plane.position;
            Vector3 lookAt = new Vector3(
                p.x + (UnityEngine.Random.value - 0.5f) * shake,
                p.y + 2f + (UnityEngine.Random.value - 0.5f) * shake,
                p.z + (UnityEngine.Random.value - 0.5f) * shake);
            _camera.LookAt(lookAt);
        }

        /// <summary>src/crash.js:385-399.</summary>
        public void Dispose()
        {
            foreach (var (mat, color, smoothness, hadSmoothness) in _matBackup)
            {
                SetColorSafe(mat, color);
                if (hadSmoothness) mat.SetFloat("_Smoothness", smoothness);
            }
            _hooks.ClearBurning?.Invoke("crash:plane");
            foreach (var f in _fireballs)
            {
                UnityEngine.Object.Destroy(f.Mat);
                FreeLight(f.Light);
            }
            foreach (var m in _debrisMats ?? Array.Empty<Material>()) UnityEngine.Object.Destroy(m);
            if (_tetraMesh != null) UnityEngine.Object.Destroy(_tetraMesh);
            if (_group != null) UnityEngine.Object.Destroy(_group);
        }
    }
}

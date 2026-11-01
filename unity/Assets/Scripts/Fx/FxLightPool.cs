using UnityEngine;

namespace TbilisiFly.Fx
{
    // =========================================================================
    // FxLightPool.cs — port of src/game.js's effect-light pool (constructed in
    // `_setupScene`, game.js:341-356; borrowed/returned by `takeFxLight`/
    // `freeFxLight`, game.js:2654-2670).
    //
    // WHY THIS EXISTS — the exact bug named in this port's own brief: "A new
    // PointLight recompiles EVERY material" (measured in the JS build: 36
    // shader programs recompiled in one frame, an 0.8 s freeze, every time a
    // crash fireball lit up). The JS's own fix was never to `new PointLight`
    // in a gameplay path at all — create a small fixed pool ONCE, parked at
    // intensity 0 far below the map, and round-robin borrow from it. This is
    // the Unity-side version of that SAME rule: three Light components are
    // created once, here, and never instantiated again — fire.js/Fx/
    // FireSystem.cs's own header already avoids per-flame lights by using
    // billboard sprites instead; this pool exists for whatever DOES need a
    // real dynamic light (an explosion's flash, a crash fireball) without
    // ever paying the "new Light this frame" cost.
    //
    // UNITY-NATIVE NOTE: three.js's PointLight takes a physical decay
    // exponent (the JS pool uses 1.8); Unity's built-in/URP Light has no such
    // parameter (realtime lights are always inverse-square) — Range is the
    // nearest analogue to three's `distance` and is set per-borrow exactly as
    // the JS sets `l.distance`.
    // =========================================================================
    public sealed class FxLightPool
    {
        readonly Light[] _lights;
        int _cursor;

        public const int PoolSize = 3; // game.js:350 `for (let i = 0; i < 3; i++)`
        static readonly Color DefaultColor = new Color(1f, 0.5019608f, 0.2f); // 0xff8033

        /// <summary>game.js:349-356 — built ONCE, parked far below the map at
        /// intensity 0, added under `parent` (pass the scene root or a dummy
        /// "fx" transform; three.js added these straight to `this.scene`).</summary>
        public FxLightPool(Transform parent)
        {
            _lights = new Light[PoolSize];
            for (int i = 0; i < PoolSize; i++)
            {
                var go = new GameObject($"FxLight_{i}");
                if (parent != null) go.transform.SetParent(parent, false);
                go.transform.position = new Vector3(0f, -10000f, 0f); // game.js:352
                var l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.color = DefaultColor;
                l.intensity = 0f;
                l.range = 1f;
                l.shadows = LightShadows.None; // the JS pool never casts shadows either
                _lights[i] = l;
            }
        }

        /// <summary>game.js:2656-2663 `takeFxLight`. `color` is linear 0..1
        /// (not a hex int — callers already have a Color on this side of the
        /// port); `range` is three's `distance`; `decay` is accepted for
        /// call-site parity with the JS but has no Unity equivalent (see class
        /// header) and is otherwise unused.</summary>
        public Light Take(Color color, float intensity, float range, float decay = 1.8f)
        {
            var l = _lights[_cursor++ % _lights.Length];
            l.color = color;
            l.intensity = intensity;
            l.range = range;
            l.transform.position = new Vector3(0f, -10000f, 0f); // caller repositions before use, same as the JS leaving it wherever it last was until moved
            return l;
        }

        /// <summary>game.js:2665-2670 `freeFxLight` — parked and dark, so the
        /// next crash starts clean.</summary>
        public void Free(Light l)
        {
            if (l == null) return;
            l.intensity = 0f;
            l.transform.position = new Vector3(0f, -10000f, 0f);
        }
    }
}

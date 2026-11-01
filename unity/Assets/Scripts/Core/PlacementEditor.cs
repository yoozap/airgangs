using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.InputSystem;
using TbilisiFly.Data;

namespace TbilisiFly.Core
{
    /// <summary>One row of custom_models.json — src/placement.js's `entry`.</summary>
    [Serializable]
    public class CustomModelEntry
    {
        public string file;
        public double lat;
        public double lon;
        public float yaw;       // GAME-FRAME yaw, radians — a hand-tuned editor value, NOT footprint-derived
        public float scale = 1f;
        public float yOffset = 0f;
    }

    /// <summary>A placed instance: the live Transform plus the JSON row driving it.</summary>
    public class CustomModelInstance
    {
        public Transform Obj;
        public CustomModelEntry Entry;
    }

    /// <summary>
    /// Port of src/placement.js (131 lines) — the in-game placement editor for
    /// user-scanned photogrammetry models. This is NOT src/devmode.js's general
    /// gizmo editor (Unity's own editor replaces that one, per the unity skill's
    /// porting rules) — placement.js is a separate, narrower tool: nudging
    /// custom_models.json entries in LAT/LON space, which still makes sense at
    /// runtime, with no Unity editor attached, on any build target.
    ///
    /// Positions are produced from lat/lon through GeoFrame.FromLatLon, which
    /// already performs the mirror (`x = -(lon-lon0)*mlon` in the JS is
    /// GeoFrame's X-mirror, hand-inlined) — so unlike the JS, this class never
    /// computes the game-frame x/z itself.
    ///
    /// The HTML side panel (src/placement.js:86-120, raw DOM) is UI Toolkit's
    /// job in Phase 6; StatusText()/Flash expose the same information as plain
    /// strings instead of building a <div>.
    /// </summary>
    public class PlacementEditor
    {
        public bool Active { get; private set; }
        public int Index { get; private set; }
        public string Flash { get; private set; } = "";
        float _flashT;

        readonly Func<List<CustomModelInstance>> _models;
        // UNITY-frame heightAt(x, z) -> y, i.e. TerrainSampler.HeightAt (NOT HeightAtGame:
        // the positions this class works with are already converted to Unity world space).
        readonly Func<float, float, float> _heightAt;

        public PlacementEditor(Func<List<CustomModelInstance>> models, Func<float, float, float> heightAtUnity)
        {
            _models = models;
            _heightAt = heightAtUnity;
        }

        List<CustomModelInstance> Models => _models() ?? new List<CustomModelInstance>();

        CustomModelInstance Current
        {
            get
            {
                var list = Models;
                return list.Count == 0 ? null : list[Mathf.Clamp(Index, 0, list.Count - 1)];
            }
        }

        /// <summary>src/placement.js:18-29. Returns false (and sets Flash) if there is nothing to place.</summary>
        public bool Toggle(Transform plane)
        {
            if (Models.Count == 0)
            {
                _flash("No custom models. Add one with tools/add_model.py first.");
                return false;
            }
            Active = !Active;
            if (Active) FlyTo(plane);
            return true;
        }

        /// <summary>Park the plane above the selected model — src/placement.js:32-41.</summary>
        public void FlyTo(Transform plane)
        {
            var m = Current;
            if (m == null || plane == null) return;
            Vector3 pos = m.Obj.position;
            plane.position = new Vector3(pos.x + 40f, pos.y + 60f, pos.z + 40f);
            // Three's Object3D.lookAt aims local -Z at the target; Unity's
            // Transform.LookAt aims local +Z. Each engine's own "forward" is
            // self-consistent with its own LookAt, so this is a direct port —
            // no extra sign flip (unlike a hand-tuned yaw CONSTANT, this one
            // is derived fresh every call from the current positions).
            plane.LookAt(pos);
        }

        /// <summary>
        /// src/placement.js:44-74. Returns true if the key was consumed.
        /// `key` is the Input System's Key enum, matched 1:1 against the JS's
        /// KeyboardEvent.code strings.
        /// </summary>
        public bool HandleKey(Key key, bool shift, Transform plane)
        {
            var list = Models;
            if (!Active || list.Count == 0) return false;
            var m = list[Mathf.Clamp(Index, 0, list.Count - 1)];
            var e = m.Entry;
            float step = shift ? 20f : 3f; // metres
            double dLat = step / GeoFrame.MetresPerLat;
            double dLon = step / GeoFrame.MetresPerLon;
            bool handled = true;
            switch (key)
            {
                case Key.RightBracket: Index = (Index + 1) % list.Count; FlyTo(plane); break;
                case Key.LeftBracket: Index = (Index - 1 + list.Count) % list.Count; FlyTo(plane); break;
                case Key.UpArrow: e.lat += dLat; break;              // north
                case Key.DownArrow: e.lat -= dLat; break;
                case Key.LeftArrow: e.lon -= dLon; break;            // west
                case Key.RightArrow: e.lon += dLon; break;
                case Key.R: e.yaw += shift ? -0.1f : 0.1f; break;
                case Key.Equals: case Key.NumpadPlus: e.scale *= 1.06f; break;
                case Key.Minus: case Key.NumpadMinus: e.scale /= 1.06f; break;
                case Key.PageUp: e.yOffset += step; break;
                case Key.PageDown: e.yOffset -= step; break;
                case Key.G: e.yOffset = 0f; break; // snap to ground
                default: handled = false; break;
            }
            if (handled) Apply(m);
            return handled;
        }

        /// <summary>src/placement.js:76-84 — lat/lon -> world, through GeoFrame.</summary>
        void Apply(CustomModelInstance m)
        {
            var e = m.Entry;
            Vector3 p = GeoFrame.FromLatLon(e.lat, e.lon);
            float y = _heightAt(p.x, p.z) + e.yOffset;
            m.Obj.position = new Vector3(p.x, y, p.z);
            // HANDEDNESS: e.yaw is a hand-tuned value nudged by the R key, not
            // derived from mirrored geometry -- it goes through YawToUnity.
            m.Obj.rotation = Quaternion.Euler(0f, GeoFrame.YawToUnity(e.yaw) * Mathf.Rad2Deg, 0f);
            m.Obj.localScale = Vector3.one * e.scale;
        }

        /// <summary>src/placement.js:112-120 — save the roster back to custom_models.json's own shape.</summary>
        public void Save(string path)
        {
            var data = new List<CustomModelEntry>();
            foreach (var m in Models) data.Add(m.Entry);
            File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));
            _flash($"Saved to {path}");
        }

        void _flash(string msg)
        {
            Flash = msg;
            _flashT = 3.2f; // src/placement.js:129 setTimeout(..., 3200)
        }

        /// <summary>Call once a frame; clears Flash after ~3.2s like the JS's setTimeout.</summary>
        public void Tick(float dt)
        {
            if (_flashT <= 0f) return;
            _flashT -= dt;
            if (_flashT <= 0f) Flash = "";
        }

        /// <summary>Mirrors the panel's innerHTML (src/placement.js:99-103) as plain text for whatever UI binds it.</summary>
        public string StatusText()
        {
            var m = Current;
            if (m == null) return "";
            var e = m.Entry;
            return $"მოდელის განთავსება ({Index + 1}/{Models.Count})\n{e.file}\n" +
                   $"lat {e.lat:F5} · lon {e.lon:F5}\n" +
                   $"yaw {e.yaw:F2} · scale {e.scale:F2} · y {e.yOffset:F1}m";
        }
    }
}

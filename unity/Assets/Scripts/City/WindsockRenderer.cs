using System.Collections.Generic;
using TbilisiFly.Core;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// The windsocks at TBS — src/airport.js:1015-1058. The RENDERED half of
    /// <see cref="AirportBuilder.PlaceWindsocks"/> plus the per-frame chase that
    /// <see cref="WindsockDriver"/> has held, correct and unreferenced, since it
    /// was ported: "a windsock is an INSTRUMENT: it points where the wind is
    /// going, and it never sits still."
    ///
    /// Why this file had to exist for Core/Wind.cs's WindsockDriver to stop being
    /// DEAD_UNREFERENCED: the driver needs a Transform, and there was none.
    /// AirportBuilder computes the sock POSITIONS and the one real OSM sock that
    /// has to step 20 m off the shed's taxiway, but the only caller of
    /// PlaceWindsocks is CityLoader.BuildAirport — and CityLoader.RunScenery has
    /// no runtime caller at all (Editor/Probes/CityLoadProbe.cs is the only one),
    /// so those positions were computed during a bake and dropped. Nothing in the
    /// project ever Instantiate()'d Assets/Resources/Art/Airport/windsock.fbx; `grep
    /// windsock Assets/Scenes/Game.unity` returned 0.
    ///
    /// ONE SHARED BREEZE. The sock reads <see cref="Wind.Heading"/>/
    /// <see cref="Wind.Gust"/>/<see cref="Wind.Time"/> — the same static state
    /// Core/GameLoop.StageWindAndWorld already advances once a frame
    /// (GameLoop.cs:1377 `Wind.Update(dt)`) and the same state the tree-sway
    /// vertex shader samples through the `_WindTime`/`_WindDir`/`_WindAmp`
    /// globals Wind.Update writes. There is deliberately no second wind clock
    /// here and no Wind.Update call of its own: "the trees on Rustaveli lean the
    /// way the windsock at the airport is pointing" only holds if both read one
    /// source.
    ///
    /// HANDEDNESS: the positions come back from AirportBuilder already in Unity
    /// space (GeoFrame.FromLatLon + its GameOffsetToUnity shed-clearance shift).
    /// The yaw is Wind.Heading, which Core/Wind.cs's header establishes as
    /// already-mirrored and SELF-CORRECTING — it was produced by atan2 over the
    /// already-mirrored direction vector, so negating it again would un-mirror
    /// it. The FBX is a static asset converted by the project's Blender bridge
    /// (same as malls.fbx — see MallPlacer's note), so it needs no FlipWinding
    /// and its natural import transform is COMPOSED WITH, never reset: the model
    /// is instantiated as a child and the yaw/tilt go on the parent, so whatever
    /// rotation/scale the importer gave it survives untouched (the crane/fan
    /// "~100x too small and facing the wrong way" lesson).
    ///
    /// EULER ORDER: airport.js writes `g.rotation.y` and `g.rotation.z` on a
    /// THREE.Object3D with the default 'XYZ' order, i.e. R = Ry·Rz once x is 0.
    /// Unity's Quaternion.Euler(x, y, z) composes Ry·Rx·Rz, which with x = 0 is
    /// the SAME product — so Quaternion.Euler(0, yaw, tilt) is a direct port, not
    /// an approximation.
    /// </summary>
    [ExecuteAlways]
    public class WindsockRenderer : MonoBehaviour
    {
        /// <summary>airport.js:1029 `place(windsock, la, lo, 200, 1, 0, …)` — the
        /// bearing the prop is dropped at. It only decides frame one: the chase
        /// below pulls the sock onto the wind at 1.6 rad/s.</summary>
        public const float InitialBearingDeg = 200f;

        sealed class Sock
        {
            public Transform T;
            public float Yaw;   // radians, src/airport.js:1049 `g.rotation.y`
            public float Tilt;  // radians, src/airport.js:1055 `g.rotation.z`
        }

        readonly List<Sock> _socks = new List<Sock>();
        bool _built;

        public int SockCount => _socks.Count;
        /// <summary>Last yaw/tilt actually written, for the probe.</summary>
        public float LastYaw => _socks.Count > 0 ? _socks[0].Yaw : 0f;
        public float LastTilt => _socks.Count > 0 ? _socks[0].Tilt : 0f;

        void OnEnable() => EnsureBuilt();

        /// <summary>Regenerate from the live data pipeline if nothing is built yet.
        /// Same contract as CityRenderer/MetroRenderer.EnsureBuilt.</summary>
        public void EnsureBuilt()
        {
            if (_built) return;
            _built = true;
            // A scene load leaves _built false but may hand us baked children —
            // the double-draw defect every other City renderer was just fixed
            // for (docs/port_units/cityfix.json). Walk real children, not a
            // private list.
            GeneratedChildren.Clear(transform, disposeRuntimeAssets: false);
            _socks.Clear();
            var ap = AirportBuilder.Load();
            var city = CityData.Load();
            if (ap == null || city?.Terrain == null)
            {
                Debug.Log($"WINDSOCK socks=0 reason=noAirportOrTerrain {ArtAssets.Tag}");
                return;
            }
            var prefab = ArtAssets.Load<GameObject>("Assets/Resources/Art/Airport/windsock.fbx");
            if (prefab == null)
            {
                Debug.LogWarning("WINDSOCK socks=0 reason=missingAsset path=Assets/Resources/Art/Airport/windsock.fbx");
                return;
            }
            var spots = AirportBuilder.PlaceWindsocks(ap, city.Terrain);
            foreach (var p in spots) AddSock(prefab, p);
            Debug.Log($"WINDSOCK socks={_socks.Count} placed={spots.Count} driver=WindsockDriver.Step {ArtAssets.Tag}");
        }

        void AddSock(GameObject prefab, Vector3 pos)
        {
            // Outer pivot carries the chase; the FBX child keeps its NATURAL
            // import transform (never reset to identity — see the class header).
            var pivot = new GameObject("windsock");
            pivot.transform.SetParent(transform, false);
            pivot.transform.localPosition = pos;
            var model = Object.Instantiate(prefab, pivot.transform, false);
            model.name = "windsock_model";
            var sock = new Sock { T = pivot.transform, Yaw = InitialBearingDeg * Mathf.Deg2Rad, Tilt = 0f };
            _socks.Add(sock);
            Apply(sock);
        }

        void Update() => Tick(Time.deltaTime);

        /// <summary>src/airport.js:1042-1057 propUpdater. Separate from Update()
        /// so a probe can step it deterministically — a component AddComponent'd
        /// from an -executeMethod script never gets a scene tick, which is how a
        /// previous probe "verified" five Update() calls that all early-returned.</summary>
        internal void Tick(float dt)
        {
            if (_socks.Count == 0 || dt <= 0f) return;
            for (int i = 0; i < _socks.Count; i++)
            {
                var s = _socks[i];
                if (s.T == null) continue;
                (s.Yaw, s.Tilt) = WindsockDriver.Step(s.Yaw, s.Tilt, dt, Wind.Heading, Wind.Gust, Wind.Time);
                Apply(s);
            }
        }

        static void Apply(Sock s)
        {
            if (s.T == null) return;
            s.T.localRotation = Quaternion.Euler(0f, s.Yaw * Mathf.Rad2Deg, s.Tilt * Mathf.Rad2Deg);
        }
    }
}

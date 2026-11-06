using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Adapts <see cref="FootBelt"/> (the belt-fitting/skinning port) to the
    /// <see cref="IFootBelt"/> contract <see cref="FootWeapons"/> was written
    /// against. The two files were written concurrently against different
    /// shapes of "a worn belt" (FootWeapons.cs's own SCOPE CUT note says so):
    /// FootBelt hands out <see cref="WornGear"/> with a List of sockets, while
    /// FootWeapons wants <see cref="WornBelt"/> with an array and a
    /// HolsterScale. One cached WornBelt per WornGear keeps socket identity
    /// stable, which FootWeapons.SeatInPocket relies on (its per-socket
    /// PocketMetrics live in WornBelt.DataFor).
    /// </summary>
    public sealed class FootBeltBridge : IFootBelt
    {
        readonly FootBelt _belt;
        readonly CharacterRig _rig;
        readonly float _holsterScale;
        readonly Dictionary<WornGear, WornBelt> _map = new Dictionary<WornGear, WornBelt>();

        public FootBeltBridge(FootBelt belt, CharacterRig rig, float holsterScale)
        {
            _belt = belt; _rig = rig; _holsterScale = holsterScale;
        }

        public FootBelt Belt => _belt;

        WornBelt Wrap(WornGear g)
        {
            if (g == null) return null;
            if (!_map.TryGetValue(g, out var wb))
            {
                wb = new WornBelt { Obj = g.Obj, HolsterScale = _holsterScale };
                _map[g] = wb;
            }
            wb.Obj = g.Obj;
            wb.Sockets = g.Sockets != null ? g.Sockets.ToArray() : new Transform[0];
            wb.RideBody = g.RideBody != null ? g.RideBody.transform : null; // WornGear keeps the SkinnedMeshRenderer, WornBelt its Transform
            return wb;
        }

        WornGear Unwrap(WornBelt w)
        {
            foreach (var kv in _map) if (kv.Value == w) return kv.Key;
            return null;
        }

        public WornBelt HolsterSocket => Wrap(_belt.HolsterSocket);

        public Vector3? RingCenterWorld(WornBelt w)
        {
            var g = Unwrap(w); if (g == null) return null;
            var r = _belt.RingOf(g);
            return r.HasValue ? r.Value.C : (Vector3?)null;
        }

        public IEnumerable<Vector3> GearVertsWorld(WornBelt w)
        {
            var g = Unwrap(w); if (g == null) yield break;
            foreach (var mf in _belt.GearMeshFilters(g))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var m = mf.transform.localToWorldMatrix;
                var verts = mf.sharedMesh.vertices;
                for (int i = 0; i < verts.Length; i++) yield return m.MultiplyPoint3x4(verts[i]);
            }
        }

        public BodySection? BodySectionAt(CharacterRig rig, float worldY)
        {
            var s = _belt.BodySection(worldY);
            return s.HasValue ? new BodySection { C = s.Value.C, R = s.Value.R } : (BodySection?)null;
        }

        public IEnumerable<Vector3> BodyVertsWorld(CharacterRig rig)
        {
            foreach (var r in _belt.BodyMeshes())
            {
                Vector3[] verts; Mesh owned = null;
                if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null) { owned = new Mesh(); smr.BakeMesh(owned, true) /* MEASURED 2026-11-02 (GunHandRun BODY_bake on the x88 rig): useScale=true + localToWorldMatrix = 1.12x1.62x1.09 m, the real posed body; useScale=false here is NOT "local" — it came out 99 m. Keep true. */; verts = owned.vertices; }
                else if (r.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null) verts = mf.sharedMesh.vertices;
                else continue;
                var m = r.transform.localToWorldMatrix;
                for (int i = 0; i < verts.Length; i++) yield return m.MultiplyPoint3x4(verts[i]);
                if (owned != null) Object.Destroy(owned);
            }
        }

        public void ResyncRide(WornBelt w, Transform sock)
        {
            var g = Unwrap(w); if (g != null) _belt.RideOne(g);
        }
    }
}
